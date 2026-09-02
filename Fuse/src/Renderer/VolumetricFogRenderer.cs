using System.Numerics;
using Fuse.Core;
using Fuse.Renderer.PostProcess;
using Fuse.Scene.Model;
using Silk.NET.OpenGL;

namespace Fuse.Renderer;

public readonly record struct FogCompositeResult(uint Framebuffer, uint ColorTexture);

/// <summary>
/// Height-aware fullscreen volumetric fog. The pass runs at a configurable
/// fraction of the scene resolution, stores its result in a temporal history,
/// and composites the scattering over the already rendered scene.
/// </summary>
public sealed unsafe class VolumetricFogRenderer : IDisposable
{
    private const int SpotShadowTextureUnit = 4;
    private const int PointShadowTextureUnit = 5;
    private const int FogDepthHistoryTextureUnit = 9;
    private const int MaxPointShadowMaps = 4;

    private readonly GL _gl;
    private readonly FullscreenQuad _quad;
    private readonly Shader _fogShader;
    private readonly Shader _compositeShader;
    private readonly uint _sharedNoiseTexture;
    private ShadowMap? _directionalShadowMap;
    private ShadowMap? _spotShadowMap;
    private PointShadowMap[]? _pointShadowMaps;
    private readonly long _timeOriginMilliseconds = Environment.TickCount64;

    private readonly uint[] _fogFbos = new uint[2];
    private readonly uint[] _fogTextures = new uint[2];
    private readonly uint[] _fogDepthTextures = new uint[2];
    private uint _compositeFbo;
    private uint _compositeTexture;

    private int _fullWidth;
    private int _fullHeight;
    private int _lowWidth;
    private int _lowHeight;
    private int _historyIndex;
    private int _frameIndex;
    private bool _historyValid;
    private bool _disposed;
    private ulong _settingsSignature;
    private Matrix4x4 _previousViewProjection = Matrix4x4.Identity;
    private Vector3 _previousCameraPosition;
    private Vector3 _previousSunDirection = ProceduralSky.FallbackSunDirection;
    private float _previousFogTime;

    public bool IsValid => _fogShader.IsValid && _compositeShader.IsValid;

    public VolumetricFogRenderer(
        GL gl,
        uint sharedNoiseTexture = 0,
        ShadowMap? directionalShadowMap = null)
    {
        _gl = gl;
        _sharedNoiseTexture = sharedNoiseTexture;
        _directionalShadowMap = directionalShadowMap;
        _quad = new FullscreenQuad(gl);
        _fogShader = Shader.FromFile(
            gl,
            Bible.Shader(Bible.PostProcessVert),
            Bible.Shader(Bible.VolumetricFogFrag));
        _compositeShader = Shader.FromFile(
            gl,
            Bible.Shader(Bible.PostProcessVert),
            Bible.Shader(Bible.VolumetricFogCompositeFrag));
        _fogShader.BindUniformBlock("LightingBlock", LightingBuffer.BindingPoint);
    }

    public void SetDirectionalShadowMap(ShadowMap? directionalShadowMap) =>
        _directionalShadowMap = directionalShadowMap;

    public void SetLocalShadowMaps(
        ShadowMap? spotShadowMap,
        PointShadowMap[]? pointShadowMaps)
    {
        _spotShadowMap = spotShadowMap;
        _pointShadowMaps = pointShadowMaps;
    }

    public void InvalidateHistory()
    {
        _historyValid = false;
        _previousViewProjection = Matrix4x4.Identity;
    }

    public FogCompositeResult Render(
        uint sceneColorTexture,
        uint sceneDepthTexture,
        int width,
        int height,
        Matrix4x4 view,
        Matrix4x4 projection,
        Vector3 cameraPosition,
        Vector3 sunDirection,
        Vector3 directionalLightColor,
        VolumetricFogSettings settings,
        SkyboxSettings skyboxSettings,
        Vector3 fallbackAmbientColor,
        bool sceneIsSrgb = false,
        bool outputSrgb = false)
    {
        EnsureSettingsCurrent(settings);
        if (!settings.Enabled || sceneColorTexture == 0 || sceneDepthTexture == 0 ||
            width <= 0 || height <= 0 || !IsValid)
        {
            return default;
        }

        EnsureFrameTargets(width, height, settings.ResolutionScale);
        float time = CurrentTimeSeconds();
        Matrix4x4 viewProjection = view * projection;
        if (!Matrix4x4.Invert(viewProjection, out Matrix4x4 inverseViewProjection))
            return default;

        Vector3 normalizedSun = sunDirection.LengthSquared() > 1e-8f
            ? Vector3.Normalize(sunDirection)
            : ProceduralSky.FallbackSunDirection;
        if (_historyValid &&
            (Vector3.DistanceSquared(cameraPosition, _previousCameraPosition) > 40000.0f ||
             time - _previousFogTime > 0.5f ||
             Vector3.Dot(normalizedSun, _previousSunDirection) < 0.996f))
        {
            _historyValid = false;
        }

        int writeIndex = 1 - _historyIndex;
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _fogFbos[writeIndex]);
        _gl.Viewport(0, 0, (uint)_lowWidth, (uint)_lowHeight);
        _gl.Disable(EnableCap.DepthTest);
        _gl.Disable(EnableCap.CullFace);
        _gl.Disable(EnableCap.Blend);
        _gl.DepthMask(false);
        _gl.ClearColor(0.0f, 0.0f, 0.0f, 1.0f);
        _gl.Clear(ClearBufferMask.ColorBufferBit);

        _fogShader.Use();
        ProceduralSky.ApplyShaderParameters(
            _fogShader,
            skyboxSettings,
            normalizedSun,
            directionalLightColor);
        _fogShader.SetInt("uSceneDepth", 0);
        _fogShader.SetInt("uFogHistory", 1);
        _fogShader.SetInt("uFogNoise", 2);
        _fogShader.SetInt("uFogDepthHistory", FogDepthHistoryTextureUnit);
        _fogShader.SetMat4("uInvViewProj", inverseViewProjection);
        _fogShader.SetMat4("uPreviousViewProj", _previousViewProjection);
        _fogShader.SetVec3("uFogCameraPosition", cameraPosition);
        _fogShader.SetVec3("uPreviousCameraPosition", _previousCameraPosition);
        _fogShader.SetVec3("uSunDirection", normalizedSun);
        _fogShader.SetInt("uDirectionalShadowMap", 3);
        _fogShader.SetBool("uDirectionalShadowEnabled", _directionalShadowMap != null);
        _fogShader.SetVec3("uFogAmbientColor", Vector3.Max(fallbackAmbientColor, Vector3.Zero));
        _fogShader.SetFloat("uFogDensity", MathF.Max(settings.Density, 0.0f));
        _fogShader.SetFloat("uFogBaseHeight", settings.BaseHeight);
        _fogShader.SetFloat("uFogHeightFalloff", MathF.Max(settings.HeightFalloff, 0.1f));
        _fogShader.SetFloat("uFogSkyDensity", MathF.Max(settings.SkyDensity, 0.0f));
        _fogShader.SetFloat("uFogSkyHeightFalloff", MathF.Max(settings.SkyHeightFalloff, 0.1f));
        _fogShader.SetFloat("uFogMaxDistance", MathF.Max(settings.MaxDistance, 10.0f));
        _fogShader.SetFloat("uFogNoiseScale", MathF.Max(settings.NoiseScale, 0.00001f));
        _fogShader.SetFloat("uFogNoiseStrength", System.Math.Clamp(settings.NoiseStrength, 0.0f, 1.0f));
        Vector2 windDirection = settings.WindDirection.LengthSquared() > 1e-8f
            ? Vector2.Normalize(settings.WindDirection)
            : Vector2.UnitX;
        _fogShader.SetVec2("uFogWindDirection", windDirection);
        _fogShader.SetFloat("uFogWindSpeed", settings.WindSpeed);
        _fogShader.SetFloat("uFogAnisotropy", System.Math.Clamp(settings.Anisotropy, -0.8f, 0.9f));
        _fogShader.SetFloat("uFogAbsorption", System.Math.Clamp(settings.Absorption, 0.01f, 20.0f));
        _fogShader.SetFloat("uFogAmbientStrength", System.Math.Max(settings.AmbientStrength, 0.0f));
        _fogShader.SetFloat("uFogSunScattering", System.Math.Max(settings.SunScattering, 0.0f));
        _fogShader.SetBool("uFogLightShaftsEnabled", settings.LightShaftsEnabled);
        _fogShader.SetFloat("uFogLightShaftStrength", System.Math.Clamp(settings.LightShaftStrength, 0.0f, 4.0f));
        _fogShader.SetInt("uFogRaySteps", System.Math.Clamp(settings.RaySteps, 8, 128));
        _fogShader.SetFloat("uFogTemporalBlend", System.Math.Clamp(settings.TemporalBlend, 0.0f, 0.98f));
        _fogShader.SetFloat("uFogTime", time);
        _fogShader.SetFloat("uPreviousFogTime", _previousFogTime);
        _fogShader.SetVec2("uFogHistoryTexelSize", new Vector2(
            1.0f / _lowWidth,
            1.0f / _lowHeight));
        _fogShader.SetBool("uFogNoiseEnabled", _sharedNoiseTexture != 0);
        _fogShader.SetBool("uHistoryValid", _historyValid);
        _fogShader.SetInt("uFogFrameIndex", _frameIndex);
        _fogShader.SetInt("uSpotShadowMap", SpotShadowTextureUnit);
        for (int i = 0; i < MaxPointShadowMaps; i++)
            _fogShader.SetInt($"uPointShadowMap{i}", PointShadowTextureUnit + i);

        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, sceneDepthTexture);
        _gl.ActiveTexture(TextureUnit.Texture1);
        _gl.BindTexture(TextureTarget.Texture2D, _fogTextures[_historyIndex]);
        _gl.ActiveTexture(TextureUnit.Texture2);
        _gl.BindTexture(TextureTarget.Texture3D, _sharedNoiseTexture);
        if (_directionalShadowMap != null)
            _directionalShadowMap.BindForReading(TextureUnit.Texture3);
        if (_spotShadowMap != null)
            _spotShadowMap.BindForReading((TextureUnit)((int)TextureUnit.Texture0 + SpotShadowTextureUnit));
        if (_pointShadowMaps != null)
        {
            for (int i = 0; i < System.Math.Min(MaxPointShadowMaps, _pointShadowMaps.Length); i++)
                _pointShadowMaps[i].BindForReading(
                    (TextureUnit)((int)TextureUnit.Texture0 + PointShadowTextureUnit + i));
        }
        _gl.ActiveTexture((TextureUnit)((int)TextureUnit.Texture0 + FogDepthHistoryTextureUnit));
        _gl.BindTexture(TextureTarget.Texture2D, _fogDepthTextures[_historyIndex]);
        _quad.Draw();

        _historyIndex = writeIndex;
        _historyValid = true;
        _frameIndex = (_frameIndex + 1) & 1023;

        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _compositeFbo);
        _gl.Viewport(0, 0, (uint)_fullWidth, (uint)_fullHeight);
        _gl.ClearColor(0.0f, 0.0f, 0.0f, 1.0f);
        _gl.Clear(ClearBufferMask.ColorBufferBit);
        _compositeShader.Use();
        _compositeShader.SetInt("uSceneColor", 0);
        _compositeShader.SetInt("uFogColor", 1);
        _compositeShader.SetVec2("uFogTexelSize", new Vector2(
            1.0f / _lowWidth,
            1.0f / _lowHeight));
        _compositeShader.SetBool("uUpsampleFog", _lowWidth != _fullWidth || _lowHeight != _fullHeight);
        _compositeShader.SetBool("uSceneIsSrgb", sceneIsSrgb);
        _compositeShader.SetBool("uOutputSrgb", outputSrgb);
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, sceneColorTexture);
        _gl.ActiveTexture(TextureUnit.Texture1);
        _gl.BindTexture(TextureTarget.Texture2D, _fogTextures[_historyIndex]);
        _quad.Draw();

        _previousViewProjection = viewProjection;
        _previousCameraPosition = cameraPosition;
        _previousSunDirection = normalizedSun;
        _previousFogTime = time;
        _gl.DepthMask(true);
        _gl.ActiveTexture(TextureUnit.Texture0);
        return new FogCompositeResult(_compositeFbo, _compositeTexture);
    }

    private void EnsureSettingsCurrent(VolumetricFogSettings settings)
    {
        ulong signature = ComputeSettingsSignature(settings);
        if (_settingsSignature == signature)
            return;
        _settingsSignature = signature;
        InvalidateHistory();
    }

    private static ulong ComputeSettingsSignature(VolumetricFogSettings settings)
    {
        ulong hash = 1469598103934665603UL;
        static void Mix(ref ulong target, uint value)
        {
            target ^= value;
            target *= 1099511628211UL;
        }
        static void MixFloat(ref ulong target, float value) =>
            Mix(ref target, unchecked((uint)BitConverter.SingleToInt32Bits(value)));

        Mix(ref hash, settings.Enabled ? 1u : 0u);
        MixFloat(ref hash, settings.Density);
        MixFloat(ref hash, settings.BaseHeight);
        MixFloat(ref hash, settings.HeightFalloff);
        MixFloat(ref hash, settings.SkyDensity);
        MixFloat(ref hash, settings.SkyHeightFalloff);
        MixFloat(ref hash, settings.MaxDistance);
        MixFloat(ref hash, settings.NoiseScale);
        MixFloat(ref hash, settings.NoiseStrength);
        MixFloat(ref hash, settings.WindDirection.X);
        MixFloat(ref hash, settings.WindDirection.Y);
        MixFloat(ref hash, settings.WindSpeed);
        MixFloat(ref hash, settings.Anisotropy);
        MixFloat(ref hash, settings.Absorption);
        MixFloat(ref hash, settings.AmbientStrength);
        MixFloat(ref hash, settings.SunScattering);
        Mix(ref hash, settings.LightShaftsEnabled ? 1u : 0u);
        MixFloat(ref hash, settings.LightShaftStrength);
        Mix(ref hash, unchecked((uint)settings.RaySteps));
        MixFloat(ref hash, settings.ResolutionScale);
        MixFloat(ref hash, settings.TemporalBlend);
        return hash;
    }

    private void EnsureFrameTargets(int width, int height, float resolutionScale)
    {
        float scale = System.Math.Clamp(resolutionScale, 0.25f, 1.0f);
        int lowWidth = System.Math.Max(1, (int)MathF.Ceiling(width * scale));
        int lowHeight = System.Math.Max(1, (int)MathF.Ceiling(height * scale));
        if (_fullWidth == width && _fullHeight == height &&
            _lowWidth == lowWidth && _lowHeight == lowHeight &&
            _compositeFbo != 0)
        {
            return;
        }

        DeleteFrameTargets();
        _fullWidth = width;
        _fullHeight = height;
        _lowWidth = lowWidth;
        _lowHeight = lowHeight;
        for (int i = 0; i < 2; i++)
        {
            CreateHistoryTarget(
                ref _fogFbos[i],
                ref _fogTextures[i],
                ref _fogDepthTextures[i],
                lowWidth,
                lowHeight);
        }
        CreateColorTarget(
            ref _compositeFbo,
            ref _compositeTexture,
            width,
            height,
            InternalFormat.Rgba16f,
            PixelFormat.Rgba);
        _historyIndex = 0;
        _historyValid = false;
    }

    private void CreateHistoryTarget(
        ref uint framebuffer,
        ref uint colorTexture,
        ref uint depthTexture,
        int width,
        int height)
    {
        CreateColorTarget(
            ref framebuffer,
            ref colorTexture,
            width,
            height,
            InternalFormat.Rgba16f,
            PixelFormat.Rgba);

        depthTexture = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, depthTexture);
        _gl.TexImage2D(
            TextureTarget.Texture2D,
            0,
            (int)InternalFormat.R16f,
            (uint)width,
            (uint)height,
            0,
            PixelFormat.Red,
            PixelType.Float,
            null);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);

        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
        _gl.FramebufferTexture2D(
            FramebufferTarget.Framebuffer,
            FramebufferAttachment.ColorAttachment1,
            TextureTarget.Texture2D,
            depthTexture,
            0);
        _gl.DrawBuffers(new[]
        {
            DrawBufferMode.ColorAttachment0,
            DrawBufferMode.ColorAttachment1
        });
        GLEnum status = _gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        if (status != GLEnum.FramebufferComplete)
            throw new InvalidOperationException($"Volumetric fog history framebuffer is incomplete: {status}");
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    private void CreateColorTarget(
        ref uint framebuffer,
        ref uint texture,
        int width,
        int height,
        InternalFormat internalFormat,
        PixelFormat pixelFormat)
    {
        framebuffer = _gl.GenFramebuffer();
        texture = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, texture);
        _gl.TexImage2D(
            TextureTarget.Texture2D,
            0,
            (int)internalFormat,
            (uint)width,
            (uint)height,
            0,
            pixelFormat,
            PixelType.Float,
            null);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);

        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
        _gl.FramebufferTexture2D(
            FramebufferTarget.Framebuffer,
            FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D,
            texture,
            0);
        _gl.DrawBuffer(DrawBufferMode.ColorAttachment0);
        GLEnum status = _gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        if (status != GLEnum.FramebufferComplete)
            throw new InvalidOperationException($"Volumetric fog framebuffer is incomplete: {status}");
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    private float CurrentTimeSeconds() =>
        (Environment.TickCount64 - _timeOriginMilliseconds) / 1000.0f;

    private void DeleteFrameTargets()
    {
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        for (int i = 0; i < 2; i++)
        {
            if (_fogTextures[i] != 0) _gl.DeleteTexture(_fogTextures[i]);
            if (_fogDepthTextures[i] != 0) _gl.DeleteTexture(_fogDepthTextures[i]);
            if (_fogFbos[i] != 0) _gl.DeleteFramebuffer(_fogFbos[i]);
            _fogTextures[i] = 0;
            _fogDepthTextures[i] = 0;
            _fogFbos[i] = 0;
        }
        if (_compositeTexture != 0) _gl.DeleteTexture(_compositeTexture);
        if (_compositeFbo != 0) _gl.DeleteFramebuffer(_compositeFbo);
        _compositeTexture = 0;
        _compositeFbo = 0;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        DeleteFrameTargets();
        _fogShader.Dispose();
        _compositeShader.Dispose();
        _quad.Dispose();
    }
}
