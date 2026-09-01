using System.Numerics;
using Fuse.Core;
using Fuse.Renderer.PostProcess;
using Fuse.Scene.Model;
using Silk.NET.OpenGL;

namespace Fuse.Renderer;

public readonly record struct CloudCompositeResult(uint Framebuffer, uint ColorTexture);

/// <summary>
/// Shared low-resolution volumetric cloud renderer used by both the game and
/// Blowtorch. It owns all transient buffers, procedural 3D noise and the cloud
/// shadow texture, so maps only need to persist lightweight settings.
/// </summary>
public sealed unsafe class VolumetricCloudRenderer : IDisposable
{
    public const int CloudShadowTextureUnit = 18;

    private readonly GL _gl;
    private readonly FullscreenQuad _quad;
    private readonly Shader _cloudShader;
    private readonly Shader _compositeShader;
    private readonly Shader _shadowShader;
    private readonly ComputeShader _cloudNoiseCompute;
    private readonly ComputeShader _cloudWeatherCompute;
    private readonly long _timeOriginMilliseconds = Environment.TickCount64;

    private uint _baseNoiseTexture;
    private uint _detailNoiseTexture;
    private uint _weatherTexture;
    private readonly uint[] _cloudFbos = new uint[2];
    private readonly uint[] _cloudTextures = new uint[2];
    private uint _compositeFbo;
    private uint _compositeTexture;
    private uint _shadowFbo;
    private uint _shadowTexture;

    private int _fullWidth;
    private int _fullHeight;
    private int _lowWidth;
    private int _lowHeight;
    private int _shadowResolution;
    private int _historyIndex;
    private int _frameIndex;
    private bool _historyValid;
    private bool _shadowValid;
    private Matrix4x4 _previousViewProjection = Matrix4x4.Identity;
    private Vector3 _previousCameraPosition;
    private float _previousCloudTime;
    private float _lastShadowTime = float.NegativeInfinity;
    private Vector2 _shadowCenter;
    private float _shadowExtent;
    private Vector3 _shadowSunDirection = ProceduralSky.FallbackSunDirection;
    private bool _disposed;
    private ulong _settingsSignature;

    public bool IsValid =>
        _cloudShader.IsValid && _compositeShader.IsValid && _shadowShader.IsValid;

    public VolumetricCloudRenderer(GL gl)
    {
        _gl = gl;
        _quad = new FullscreenQuad(gl);
        _cloudShader = Shader.FromFile(
            gl,
            Bible.Shader(Bible.PostProcessVert),
            Bible.Shader(Bible.VolumetricCloudFrag));
        _compositeShader = Shader.FromFile(
            gl,
            Bible.Shader(Bible.PostProcessVert),
            Bible.Shader(Bible.VolumetricCloudCompositeFrag));
        _shadowShader = Shader.FromFile(
            gl,
            Bible.Shader(Bible.PostProcessVert),
            Bible.Shader(Bible.VolumetricCloudShadowFrag));

        _cloudNoiseCompute = ComputeShader.FromFile(
            gl,
            Bible.Shader(Bible.VolumetricCloudNoiseCompute));
        _cloudWeatherCompute = ComputeShader.FromFile(
            gl,
            Bible.Shader(Bible.VolumetricCloudWeatherCompute));

        // The reference technique separates broad Perlin-Worley shapes from
        // small Worley erosion and large-scale weather coverage. The volumes
        // are generated once on the GPU so increasing their resolution does
        // not add work to every ray-march sample or stall the render loop.
        if (_cloudNoiseCompute.IsValid && _cloudWeatherCompute.IsValid)
        {
            _baseNoiseTexture = CreateGeneratedNoiseTexture(
                128, detail: false, seed: 0x51F15E);
            _detailNoiseTexture = CreateGeneratedNoiseTexture(
                32, detail: true, seed: 0xC10D5);
            _weatherTexture = CreateGeneratedWeatherTexture(1024, 0x7EA7E2);
        }
        else
        {
            Logger.Warn("Volumetric cloud compute shaders are unavailable; using the CPU fallback noise generator.");
            _baseNoiseTexture = CreateNoiseTexture(128, detail: false, seed: 0x51F15E);
            _detailNoiseTexture = CreateNoiseTexture(32, detail: true, seed: 0xC10D5);
            _weatherTexture = CreateWeatherTexture(1024, seed: 0x7EA7E2);
        }
    }

    public void InvalidateHistory()
    {
        _historyValid = false;
        _shadowValid = false;
        _lastShadowTime = float.NegativeInfinity;
    }

    public void UpdateShadow(
        Vector3 cameraPosition,
        Vector3 sunDirection,
        VolumetricCloudSettings settings)
    {
        EnsureSettingsCurrent(settings);
        if (!settings.Enabled || !settings.ShadowsEnabled || !IsValid)
        {
            _shadowValid = false;
            return;
        }

        EnsureShadowTarget(settings.ShadowResolution);
        float time = CurrentTimeSeconds();
        float updateInterval = MathF.Max(0.0f, settings.ShadowUpdateInterval);
        if (_shadowValid && time - _lastShadowTime < updateInterval)
            return;

        Vector3 normalizedSun = sunDirection.LengthSquared() > 1e-8f
            ? Vector3.Normalize(sunDirection)
            : ProceduralSky.FallbackSunDirection;
        float extent = MathF.Max(50.0f, settings.ShadowExtent);
        float worldUnitsPerTexel = extent * 2.0f / System.Math.Max(1, _shadowResolution);
        Vector2 center = new(
            MathF.Floor(cameraPosition.X / worldUnitsPerTexel) * worldUnitsPerTexel,
            MathF.Floor(cameraPosition.Z / worldUnitsPerTexel) * worldUnitsPerTexel);

        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _shadowFbo);
        _gl.Viewport(0, 0, (uint)_shadowResolution, (uint)_shadowResolution);
        _gl.Disable(EnableCap.DepthTest);
        _gl.Disable(EnableCap.CullFace);
        _gl.Disable(EnableCap.Blend);
        _gl.ClearColor(1.0f, 1.0f, 1.0f, 1.0f);
        _gl.Clear(ClearBufferMask.ColorBufferBit);

        _shadowShader.Use();
        ApplyCommonParameters(_shadowShader, settings, time, cameraPosition);
        _shadowShader.SetVec2("uCloudShadowCenter", center);
        _shadowShader.SetFloat("uCloudShadowExtent", extent);
        _shadowShader.SetVec3("uSunDirection", normalizedSun);
        _shadowShader.SetFloat("uCloudAbsorption", settings.Absorption);
        BindNoiseTextures(_shadowShader);
        _quad.Draw();

        _shadowCenter = center;
        _shadowExtent = extent;
        _shadowSunDirection = normalizedSun;
        _shadowValid = true;
        _lastShadowTime = time;
        RestoreTextureUnitZero();
    }

    public void BindWorldShadow(Shader shader, VolumetricCloudSettings settings)
    {
        bool enabled = settings.Enabled && settings.ShadowsEnabled &&
            settings.ShadowStrength > 0.0f && _shadowValid && _shadowTexture != 0;

        shader.SetBool("uCloudShadowsEnabled", enabled);
        shader.SetInt("uCloudShadowMap", CloudShadowTextureUnit);
        shader.SetVec2("uCloudShadowCenter", _shadowCenter);
        shader.SetFloat("uCloudShadowExtent", MathF.Max(_shadowExtent, 1.0f));
        shader.SetFloat("uCloudShadowStrength", settings.ShadowStrength);
        shader.SetVec3("uCloudShadowSunDirection", _shadowSunDirection);
        shader.SetFloat("uCloudLayerBaseHeight", settings.BaseHeight);
        shader.SetFloat("uCloudLayerThickness", settings.Thickness);

        if (enabled)
        {
            _gl.ActiveTexture((TextureUnit)((int)TextureUnit.Texture0 + CloudShadowTextureUnit));
            _gl.BindTexture(TextureTarget.Texture2D, _shadowTexture);
            RestoreTextureUnitZero();
        }
    }

    public CloudCompositeResult Render(
        uint sceneColorTexture,
        uint sceneDepthTexture,
        int width,
        int height,
        Matrix4x4 view,
        Matrix4x4 projection,
        Vector3 cameraPosition,
        Vector3 sunDirection,
        Vector3 sunColor,
        VolumetricCloudSettings settings,
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

        if (_historyValid &&
            (Vector3.DistanceSquared(cameraPosition, _previousCameraPosition) > 40000.0f ||
             time - _previousCloudTime > 0.5f))
        {
            _historyValid = false;
        }

        int writeIndex = 1 - _historyIndex;
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _cloudFbos[writeIndex]);
        _gl.Viewport(0, 0, (uint)_lowWidth, (uint)_lowHeight);
        _gl.Disable(EnableCap.DepthTest);
        _gl.Disable(EnableCap.CullFace);
        _gl.Disable(EnableCap.Blend);
        _gl.DepthMask(false);
        _gl.ClearColor(0.0f, 0.0f, 0.0f, 1.0f);
        _gl.Clear(ClearBufferMask.ColorBufferBit);

        Vector3 normalizedSun = sunDirection.LengthSquared() > 1e-8f
            ? Vector3.Normalize(sunDirection)
            : ProceduralSky.FallbackSunDirection;
        _cloudShader.Use();
        ApplyCommonParameters(_cloudShader, settings, time, cameraPosition);
        _cloudShader.SetInt("uSceneDepth", 0);
        _cloudShader.SetInt("uCloudHistory", 1);
        _cloudShader.SetMat4("uInvViewProj", inverseViewProjection);
        _cloudShader.SetMat4("uPreviousViewProj", _previousViewProjection);
        _cloudShader.SetVec3("uCameraPosition", cameraPosition);
        _cloudShader.SetVec3("uSunDirection", normalizedSun);
        _cloudShader.SetVec3("uSunColor", Vector3.Max(sunColor, Vector3.Zero));
        _cloudShader.SetFloat("uCloudMaxDistance", settings.MaxDistance);
        _cloudShader.SetInt("uCloudPrimarySteps", System.Math.Max(64, settings.PrimarySteps));
        _cloudShader.SetInt("uCloudLightSteps", System.Math.Max(6, settings.LightSteps));
        _cloudShader.SetFloat("uCloudTemporalBlend", settings.TemporalBlend);
        _cloudShader.SetFloat("uCloudAnisotropy", settings.Anisotropy);
        _cloudShader.SetFloat("uCloudAbsorption", settings.Absorption);
        _cloudShader.SetFloat("uCloudAmbientStrength", settings.AmbientStrength);
        _cloudShader.SetFloat("uPreviousCloudTime", _previousCloudTime);
        _cloudShader.SetBool("uHistoryValid", _historyValid);
        _cloudShader.SetInt("uCloudFrameIndex", _frameIndex);

        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, sceneDepthTexture);
        _gl.ActiveTexture(TextureUnit.Texture1);
        _gl.BindTexture(TextureTarget.Texture2D, _cloudTextures[_historyIndex]);
        BindNoiseTextures(_cloudShader);
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
        _compositeShader.SetInt("uCloudColor", 1);
        _compositeShader.SetInt("uSceneDepth", 4);
        _compositeShader.SetVec2("uCloudTexelSize", new Vector2(1.0f / _lowWidth, 1.0f / _lowHeight));
        _compositeShader.SetBool("uDepthAwareUpsample", _lowWidth != _fullWidth || _lowHeight != _fullHeight);
        _compositeShader.SetBool("uSceneIsSrgb", sceneIsSrgb);
        _compositeShader.SetBool("uOutputSrgb", outputSrgb);
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, sceneColorTexture);
        _gl.ActiveTexture(TextureUnit.Texture1);
        _gl.BindTexture(TextureTarget.Texture2D, _cloudTextures[_historyIndex]);
        _gl.ActiveTexture(TextureUnit.Texture4);
        _gl.BindTexture(TextureTarget.Texture2D, sceneDepthTexture);
        _quad.Draw();

        _previousViewProjection = viewProjection;
        _previousCameraPosition = cameraPosition;
        _previousCloudTime = time;
        _gl.DepthMask(true);
        RestoreTextureUnitZero();
        return new CloudCompositeResult(_compositeFbo, _compositeTexture);
    }

    private void ApplyCommonParameters(
        Shader shader,
        VolumetricCloudSettings settings,
        float time,
        Vector3 cameraPosition)
    {
        Vector2 windDirection = settings.WindDirection.LengthSquared() > 1e-8f
            ? Vector2.Normalize(settings.WindDirection)
            : Vector2.UnitX;
        shader.SetInt("uCloudBaseNoise", 2);
        shader.SetInt("uCloudDetailNoise", 3);
        shader.SetInt("uCloudWeatherMap", 5);
        shader.SetFloat("uCloudBaseHeight", settings.BaseHeight);
        shader.SetFloat("uCloudThickness", MathF.Max(1.0f, settings.Thickness));
        shader.SetFloat("uCloudCoverage", System.Math.Clamp(settings.Coverage, 0.0f, 1.0f));
        shader.SetFloat("uCloudDensity", MathF.Max(0.0f, settings.Density));
        shader.SetFloat("uCloudScale", MathF.Max(0.00001f, settings.Scale));
        shader.SetFloat("uCloudDetailScale", MathF.Max(1.0f, settings.DetailScale));
        shader.SetFloat("uCloudDetailStrength", System.Math.Clamp(settings.DetailStrength, 0.0f, 1.0f));
        shader.SetInt("uCloudPreset", (int)settings.Preset);
        shader.SetVec2("uCloudWindDirection", windDirection);
        shader.SetFloat("uCloudWindSpeed", settings.WindSpeed);
        shader.SetFloat("uCloudTime", time);

        // The sphere is centered beneath the current camera to keep the local
        // shell numerically stable in Fuse-sized worlds. Its radius grows with
        // the view distance, so the visible horizon curves without turning a
        // small test map into a visibly tiny planet.
        float curvatureRadius = MathF.Max(
            20000.0f,
            MathF.Max(settings.MaxDistance * 8.0f, settings.Thickness * 64.0f));
        shader.SetVec3(
            "uCloudSphereCenter",
            new Vector3(cameraPosition.X, settings.BaseHeight - curvatureRadius, cameraPosition.Z));
        shader.SetFloat("uCloudInnerRadius", curvatureRadius);
        shader.SetFloat(
            "uCloudOuterRadius",
            curvatureRadius + MathF.Max(1.0f, settings.Thickness));

        // Keep enough world space for several distinct cloud formations. The
        // weather projection uses an irrational multiple of this period so
        // the two tileable textures do not repeat as a visible grid.
        shader.SetFloat("uCloudWorldTileSize", MathF.Max(16384.0f, settings.MaxDistance * 8.0f));
    }

    private void BindNoiseTextures(Shader shader)
    {
        shader.SetInt("uCloudBaseNoise", 2);
        shader.SetInt("uCloudDetailNoise", 3);
        shader.SetInt("uCloudWeatherMap", 5);
        _gl.ActiveTexture(TextureUnit.Texture2);
        _gl.BindTexture(TextureTarget.Texture3D, _baseNoiseTexture);
        _gl.ActiveTexture(TextureUnit.Texture3);
        _gl.BindTexture(TextureTarget.Texture3D, _detailNoiseTexture);
        _gl.ActiveTexture(TextureUnit.Texture5);
        _gl.BindTexture(TextureTarget.Texture2D, _weatherTexture);
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
            CreateColorTarget(ref _cloudFbos[i], ref _cloudTextures[i], lowWidth, lowHeight, InternalFormat.Rgba16f, PixelFormat.Rgba);
        CreateColorTarget(ref _compositeFbo, ref _compositeTexture, width, height, InternalFormat.Rgba16f, PixelFormat.Rgba);

        _historyIndex = 0;
        _historyValid = false;
    }

    private void EnsureShadowTarget(int requestedResolution)
    {
        int resolution = System.Math.Clamp(requestedResolution, 64, 1024);
        if (_shadowFbo != 0 && _shadowResolution == resolution)
            return;

        if (_shadowTexture != 0) _gl.DeleteTexture(_shadowTexture);
        if (_shadowFbo != 0) _gl.DeleteFramebuffer(_shadowFbo);
        _shadowTexture = 0;
        _shadowFbo = 0;
        _shadowResolution = resolution;
        CreateColorTarget(
            ref _shadowFbo,
            ref _shadowTexture,
            resolution,
            resolution,
            InternalFormat.R16f,
            PixelFormat.Red);
        _shadowValid = false;
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
            throw new InvalidOperationException($"Volumetric cloud framebuffer is incomplete: {status}");
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    private uint CreateGeneratedNoiseTexture(int size, bool detail, int seed)
    {
        uint texture = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture3D, texture);
        _gl.TexStorage3D(
            TextureTarget.Texture3D,
            (uint)CalculateMipLevels(size),
            SizedInternalFormat.Rgba8,
            (uint)size,
            (uint)size,
            (uint)size);
        ConfigureNoiseTexture(TextureTarget.Texture3D);

        _cloudNoiseCompute.Use();
        _cloudNoiseCompute.SetInt("uSize", size);
        _cloudNoiseCompute.SetInt("uDetail", detail ? 1 : 0);
        _cloudNoiseCompute.SetInt("uSeed", seed);
        _gl.BindImageTexture(
            0,
            texture,
            0,
            true,
            0,
            GLEnum.WriteOnly,
            GLEnum.Rgba8);
        _gl.DispatchCompute(
            (uint)((size + 3) / 4),
            (uint)((size + 3) / 4),
            (uint)((size + 3) / 4));
        _gl.MemoryBarrier(
            MemoryBarrierMask.ShaderImageAccessBarrierBit |
            MemoryBarrierMask.TextureFetchBarrierBit);
        _gl.BindImageTexture(0, 0, 0, false, 0, GLEnum.WriteOnly, GLEnum.Rgba8);
        _gl.GenerateMipmap(TextureTarget.Texture3D);
        _gl.BindTexture(TextureTarget.Texture3D, 0);
        return texture;
    }

    private uint CreateGeneratedWeatherTexture(int size, int seed)
    {
        uint texture = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, texture);
        _gl.TexStorage2D(
            TextureTarget.Texture2D,
            (uint)CalculateMipLevels(size),
            SizedInternalFormat.Rgba8,
            (uint)size,
            (uint)size);
        ConfigureNoiseTexture(TextureTarget.Texture2D);

        _cloudWeatherCompute.Use();
        _cloudWeatherCompute.SetInt("uSize", size);
        _cloudWeatherCompute.SetInt("uSeed", seed);
        _gl.BindImageTexture(
            0,
            texture,
            0,
            false,
            0,
            GLEnum.WriteOnly,
            GLEnum.Rgba8);
        _gl.DispatchCompute(
            (uint)((size + 7) / 8),
            (uint)((size + 7) / 8),
            1);
        _gl.MemoryBarrier(
            MemoryBarrierMask.ShaderImageAccessBarrierBit |
            MemoryBarrierMask.TextureFetchBarrierBit);
        _gl.BindImageTexture(0, 0, 0, false, 0, GLEnum.WriteOnly, GLEnum.Rgba8);
        _gl.GenerateMipmap(TextureTarget.Texture2D);
        _gl.BindTexture(TextureTarget.Texture2D, 0);
        return texture;
    }

    private void ConfigureNoiseTexture(TextureTarget target)
    {
        _gl.TexParameter(target, TextureParameterName.TextureMinFilter, (int)GLEnum.LinearMipmapLinear);
        _gl.TexParameter(target, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
        _gl.TexParameter(target, TextureParameterName.TextureWrapS, (int)GLEnum.Repeat);
        _gl.TexParameter(target, TextureParameterName.TextureWrapT, (int)GLEnum.Repeat);
        if (target == TextureTarget.Texture3D)
            _gl.TexParameter(target, TextureParameterName.TextureWrapR, (int)GLEnum.Repeat);
    }

    private static int CalculateMipLevels(int size)
    {
        int levels = 1;
        while (size > 1)
        {
            size >>= 1;
            levels++;
        }
        return levels;
    }

    private uint CreateNoiseTexture(int size, bool detail, int seed)
    {
        byte[] pixels = GenerateNoiseVolume(size, detail, seed);
        uint texture = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture3D, texture);
        fixed (byte* pixelPointer = pixels)
        {
            _gl.TexImage3D(
                TextureTarget.Texture3D,
                0,
                InternalFormat.Rgba8,
                (uint)size,
                (uint)size,
                (uint)size,
                0,
                PixelFormat.Rgba,
                PixelType.UnsignedByte,
                pixelPointer);
        }
        _gl.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMinFilter, (int)GLEnum.LinearMipmapLinear);
        _gl.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
        _gl.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapS, (int)GLEnum.Repeat);
        _gl.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapT, (int)GLEnum.Repeat);
        _gl.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapR, (int)GLEnum.Repeat);
        _gl.GenerateMipmap(TextureTarget.Texture3D);
        _gl.BindTexture(TextureTarget.Texture3D, 0);
        return texture;
    }

    private static byte[] GenerateNoiseVolume(int size, bool detail, int seed)
    {
        byte[] result = new byte[size * size * size * 4];
        int index = 0;
        for (int z = 0; z < size; z++)
        {
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float nx = (float)x / size;
                    float ny = (float)y / size;
                    float nz = (float)z / size;

                    if (detail)
                    {
                        float worley0 = PeriodicWorley(nx, ny, nz, 2, seed);
                        float worley1 = PeriodicWorley(nx, ny, nz, 4, seed + 101);
                        float worley2 = PeriodicWorley(nx, ny, nz, 8, seed + 211);
                        float worley3 = PeriodicWorley(nx, ny, nz, 16, seed + 307);

                        float worleyFbm0 = worley0 * 0.625f +
                            worley1 * 0.250f + worley2 * 0.125f;
                        float worleyFbm1 = worley1 * 0.625f +
                            worley2 * 0.250f + worley3 * 0.125f;
                        float worleyFbm2 = worley2 * 0.750f + worley3 * 0.250f;

                        result[index++] = ToByte(worleyFbm0);
                        result[index++] = ToByte(worleyFbm1);
                        result[index++] = ToByte(worleyFbm2);
                        result[index++] = byte.MaxValue;
                    }
                    else
                    {
                        float worley0 = PeriodicWorley(nx, ny, nz, 4, seed);
                        float worley1 = PeriodicWorley(nx, ny, nz, 8, seed + 101);
                        float worley2 = PeriodicWorley(nx, ny, nz, 16, seed + 211);
                        float worley3 = PeriodicWorley(nx, ny, nz, 24, seed + 307);

                        float worleyFbm0 = worley0 * 0.625f +
                            worley1 * 0.250f + worley2 * 0.125f;
                        float worleyFbm1 = worley1 * 0.625f +
                            worley2 * 0.250f + worley3 * 0.125f;
                        float worleyFbm2 = worley2 * 0.750f + worley3 * 0.250f;

                        float perlin0 = PeriodicValueNoise(nx, ny, nz, 6, seed + 401);
                        float perlin1 = PeriodicValueNoise(nx, ny, nz, 12, seed + 503);
                        float perlin2 = PeriodicValueNoise(nx, ny, nz, 24, seed + 607);
                        float perlinFbm = perlin0 * 0.625f +
                            perlin1 * 0.250f + perlin2 * 0.125f;
                        float perlinWorley = Lerp(worleyFbm1, 1.0f, perlinFbm);
                        perlinWorley *= perlinWorley;

                        result[index++] = ToByte(perlinWorley);
                        result[index++] = ToByte(worleyFbm0);
                        result[index++] = ToByte(worleyFbm1);
                        result[index++] = ToByte(worleyFbm2);
                    }
                }
            }
        }
        return result;
    }

    private uint CreateWeatherTexture(int size, int seed)
    {
        byte[] pixels = GenerateWeatherMap(size, seed);
        uint texture = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, texture);
        fixed (byte* pixelPointer = pixels)
        {
            _gl.TexImage2D(
                TextureTarget.Texture2D,
                0,
                (int)InternalFormat.Rgba8,
                (uint)size,
                (uint)size,
                0,
                PixelFormat.Rgba,
                PixelType.UnsignedByte,
                pixelPointer);
        }
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.LinearMipmapLinear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.Repeat);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.Repeat);
        _gl.GenerateMipmap(TextureTarget.Texture2D);
        _gl.BindTexture(TextureTarget.Texture2D, 0);
        return texture;
    }

    private static byte[] GenerateWeatherMap(int size, int seed)
    {
        byte[] result = new byte[size * size * 4];
        int index = 0;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float nx = (float)x / size;
                float ny = (float)y / size;

                float coverage =
                    PeriodicValueNoise(nx, 0.19f, ny, 2, seed) * 0.58f +
                    PeriodicValueNoise(nx, 0.19f, ny, 4, seed + 79) * 0.29f +
                    PeriodicValueNoise(nx, 0.19f, ny, 8, seed + 151) * 0.13f;
                float cloudType =
                    PeriodicValueNoise(nx, 0.47f, ny, 2, seed + 251) * 0.72f +
                    PeriodicValueNoise(nx, 0.47f, ny, 5, seed + 337) * 0.28f;
                float variation =
                    PeriodicValueNoise(nx, 0.73f, ny, 5, seed + 431) * 0.68f +
                    PeriodicValueNoise(nx, 0.73f, ny, 11, seed + 557) * 0.32f;

                result[index++] = ToByte(Smooth(coverage));
                result[index++] = ToByte(Smooth(cloudType));
                result[index++] = ToByte(variation);
                result[index++] = byte.MaxValue;
            }
        }
        return result;
    }

    private static byte ToByte(float value) =>
        (byte)System.Math.Clamp((int)(value * 255.0f + 0.5f), 0, 255);

    private static float PeriodicValueNoise(float x, float y, float z, int period, int seed)
    {
        float px = x * period;
        float py = y * period;
        float pz = z * period;
        int x0 = (int)MathF.Floor(px);
        int y0 = (int)MathF.Floor(py);
        int z0 = (int)MathF.Floor(pz);
        float tx = Smooth(px - x0);
        float ty = Smooth(py - y0);
        float tz = Smooth(pz - z0);

        float c000 = Hash01(Mod(x0, period), Mod(y0, period), Mod(z0, period), seed);
        float c100 = Hash01(Mod(x0 + 1, period), Mod(y0, period), Mod(z0, period), seed);
        float c010 = Hash01(Mod(x0, period), Mod(y0 + 1, period), Mod(z0, period), seed);
        float c110 = Hash01(Mod(x0 + 1, period), Mod(y0 + 1, period), Mod(z0, period), seed);
        float c001 = Hash01(Mod(x0, period), Mod(y0, period), Mod(z0 + 1, period), seed);
        float c101 = Hash01(Mod(x0 + 1, period), Mod(y0, period), Mod(z0 + 1, period), seed);
        float c011 = Hash01(Mod(x0, period), Mod(y0 + 1, period), Mod(z0 + 1, period), seed);
        float c111 = Hash01(Mod(x0 + 1, period), Mod(y0 + 1, period), Mod(z0 + 1, period), seed);

        float x00 = Lerp(c000, c100, tx);
        float x10 = Lerp(c010, c110, tx);
        float x01 = Lerp(c001, c101, tx);
        float x11 = Lerp(c011, c111, tx);
        return Lerp(Lerp(x00, x10, ty), Lerp(x01, x11, ty), tz);
    }

    private static float PeriodicWorley(float x, float y, float z, int period, int seed)
    {
        float px = x * period;
        float py = y * period;
        float pz = z * period;
        int cellX = (int)MathF.Floor(px);
        int cellY = (int)MathF.Floor(py);
        int cellZ = (int)MathF.Floor(pz);
        float closestSquared = float.MaxValue;

        for (int dz = -1; dz <= 1; dz++)
        for (int dy = -1; dy <= 1; dy++)
        for (int dx = -1; dx <= 1; dx++)
        {
            int wrappedX = Mod(cellX + dx, period);
            int wrappedY = Mod(cellY + dy, period);
            int wrappedZ = Mod(cellZ + dz, period);
            float featureX = cellX + dx + Hash01(wrappedX, wrappedY, wrappedZ, seed);
            float featureY = cellY + dy + Hash01(wrappedX, wrappedY, wrappedZ, seed + 101);
            float featureZ = cellZ + dz + Hash01(wrappedX, wrappedY, wrappedZ, seed + 211);
            float deltaX = featureX - px;
            float deltaY = featureY - py;
            float deltaZ = featureZ - pz;
            closestSquared = MathF.Min(closestSquared,
                deltaX * deltaX + deltaY * deltaY + deltaZ * deltaZ);
        }

        return 1.0f - System.Math.Clamp(MathF.Sqrt(closestSquared) / 1.25f, 0.0f, 1.0f);
    }

    private static float Hash01(int x, int y, int z, int seed)
    {
        uint hash = unchecked((uint)seed);
        hash ^= unchecked((uint)x) * 0x9E3779B9u;
        hash ^= unchecked((uint)y) * 0x85EBCA6Bu;
        hash ^= unchecked((uint)z) * 0xC2B2AE35u;
        hash ^= hash >> 16;
        hash *= 0x7FEB352Du;
        hash ^= hash >> 15;
        hash *= 0x846CA68Bu;
        hash ^= hash >> 16;
        return (hash & 0x00FFFFFFu) / 16777215.0f;
    }

    private static int Mod(int value, int modulus) =>
        (value % modulus + modulus) % modulus;

    private static float Smooth(float value) =>
        value * value * (3.0f - 2.0f * value);

    private static float Lerp(float from, float to, float amount) =>
        from + (to - from) * amount;

    private float CurrentTimeSeconds() =>
        (Environment.TickCount64 - _timeOriginMilliseconds) / 1000.0f;

    private void RestoreTextureUnitZero()
    {
        _gl.ActiveTexture(TextureUnit.Texture0);
    }

    private void EnsureSettingsCurrent(VolumetricCloudSettings settings)
    {
        ulong signature = ComputeSettingsSignature(settings);
        if (_settingsSignature == signature)
            return;
        _settingsSignature = signature;
        InvalidateHistory();
    }

    private static ulong ComputeSettingsSignature(VolumetricCloudSettings settings)
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
        Mix(ref hash, (uint)settings.Preset);
        MixFloat(ref hash, settings.BaseHeight);
        MixFloat(ref hash, settings.Thickness);
        MixFloat(ref hash, settings.Coverage);
        MixFloat(ref hash, settings.Density);
        MixFloat(ref hash, settings.Scale);
        MixFloat(ref hash, settings.DetailScale);
        MixFloat(ref hash, settings.DetailStrength);
        MixFloat(ref hash, settings.WindDirection.X);
        MixFloat(ref hash, settings.WindDirection.Y);
        MixFloat(ref hash, settings.WindSpeed);
        MixFloat(ref hash, settings.MaxDistance);
        Mix(ref hash, unchecked((uint)settings.PrimarySteps));
        Mix(ref hash, unchecked((uint)settings.LightSteps));
        MixFloat(ref hash, settings.ResolutionScale);
        MixFloat(ref hash, settings.TemporalBlend);
        MixFloat(ref hash, settings.Anisotropy);
        MixFloat(ref hash, settings.Absorption);
        MixFloat(ref hash, settings.AmbientStrength);
        Mix(ref hash, settings.ShadowsEnabled ? 1u : 0u);
        MixFloat(ref hash, settings.ShadowStrength);
        MixFloat(ref hash, settings.ShadowExtent);
        Mix(ref hash, unchecked((uint)settings.ShadowResolution));
        MixFloat(ref hash, settings.ShadowUpdateInterval);
        return hash;
    }

    private void DeleteFrameTargets()
    {
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        for (int i = 0; i < 2; i++)
        {
            if (_cloudTextures[i] != 0) _gl.DeleteTexture(_cloudTextures[i]);
            if (_cloudFbos[i] != 0) _gl.DeleteFramebuffer(_cloudFbos[i]);
            _cloudTextures[i] = 0;
            _cloudFbos[i] = 0;
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
        if (_shadowTexture != 0) _gl.DeleteTexture(_shadowTexture);
        if (_shadowFbo != 0) _gl.DeleteFramebuffer(_shadowFbo);
        if (_baseNoiseTexture != 0) _gl.DeleteTexture(_baseNoiseTexture);
        if (_detailNoiseTexture != 0) _gl.DeleteTexture(_detailNoiseTexture);
        if (_weatherTexture != 0) _gl.DeleteTexture(_weatherTexture);
        _cloudShader.Dispose();
        _compositeShader.Dispose();
        _shadowShader.Dispose();
        _cloudNoiseCompute.Dispose();
        _cloudWeatherCompute.Dispose();
        _quad.Dispose();
    }
}
