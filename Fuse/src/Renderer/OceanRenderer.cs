using System.Numerics;
using Fuse.Core;
using Fuse.Renderer.PostProcess;
using Fuse.Scene.Model;
using Silk.NET.OpenGL;

namespace Fuse.Renderer;

/// <summary>
/// Render-only ocean pass shared by the game and Blowtorch. The ocean does not
/// create a physics body: it copies the already rendered scene, draws a
/// camera-following spectral surface and can apply the screen-space underwater
/// treatment.
/// </summary>
public sealed unsafe class OceanRenderer : IDisposable
{
    private const int CascadeCount = 3;
    private const int WaveSimulationResolution = 128;
    private const int WaveSurfaceTextureUnit0 = 10;
    private const int WaveSlopeTextureUnit0 = 13;
    private const int OceanNormalTextureUnit = 16;
    private const float TwoPi = 6.28318530718f;
    private const float Gravity = 9.81f;

    private static readonly float[] CascadeWeights = [1.0f, 0.45f, 0.20f];
    private static readonly Vector2[] CascadeOffsetFactors =
    [
        new(0.173f, 0.371f),
        new(0.617f, 0.233f),
        new(0.291f, 0.719f)
    ];

    private readonly GL _gl;
    private readonly Shader _surfaceShader;
    private readonly Shader _underwaterShader;
    private readonly ComputeShader _spectrumCompute;
    private readonly ComputeShader _fftCompute;
    private readonly ComputeShader _resolveCompute;
    private readonly Texture _oceanNormalTexture;
    private readonly FullscreenQuad _quad;
    private readonly WaveCascade?[] _cascades = new WaveCascade?[CascadeCount];
    private Mesh? _mesh;

    private uint _sceneCopyFbo;
    private uint _sceneCopyColor;
    private uint _sceneCopyDepth;
    private uint _surfaceDataFbo;
    private uint _surfaceDataColor;
    private uint _underwaterFbo;
    private uint _underwaterColor;

    private int _meshResolution;
    private int _width;
    private int _height;
    private readonly long _timeOriginMilliseconds = Environment.TickCount64;
    private bool _waveResourcesInitialized;
    private bool _simulationStateValid;
    private bool _disposed;

    private float _spectrumWaveLength = float.NaN;
    private float _spectrumWindSpeed = float.NaN;
    private float _spectrumSmallWaveLength = float.NaN;
    private Vector2 _spectrumDirection;
    private int _spectrumSeed;
    private float _lastSimulationTime = float.NaN;
    private float _lastSimulationAmplitude = float.NaN;
    private float _lastSimulationSpeed = float.NaN;
    private float _lastSimulationChoppiness = float.NaN;
    private float _underwaterCameraSubmersion;
    private float _underwaterAnimationTime;
    private bool _underwaterPassPending;

    public bool IsValid => !_disposed &&
        _surfaceShader.IsValid && _underwaterShader.IsValid;

    private bool WaveSimulationValid
    {
        get
        {
            if (_disposed || !_waveResourcesInitialized ||
                !_spectrumCompute.IsValid || !_fftCompute.IsValid ||
                !_resolveCompute.IsValid)
                return false;

            for (int band = 0; band < CascadeCount; band++)
            {
                WaveCascade? cascade = _cascades[band];
                if (cascade == null ||
                    cascade.H0 == 0 ||
                    cascade.SpectrumA == 0 ||
                    cascade.SpectrumB == 0 ||
                    cascade.SpectrumC == 0 ||
                    cascade.PingA == 0 ||
                    cascade.PingB == 0 ||
                    cascade.PingC == 0 ||
                    cascade.Surface == 0 ||
                    cascade.Slope == 0)
                    return false;
            }

            return true;
        }
    }

    public bool LastFrameUnderwater { get; private set; }

    /// <summary>
    /// Indicates that the surface pass found a camera close enough to the
    /// waterline that the fullscreen underwater pass must still be applied.
    /// The pass is intentionally deferred until after clouds and atmospheric
    /// fog have been composited.
    /// </summary>
    public bool UnderwaterPassPending => _underwaterPassPending;

    public OceanRenderer(GL gl)
    {
        _gl = gl;
        _quad = new FullscreenQuad(gl);
        _surfaceShader = Shader.FromFile(
            gl,
            Bible.Shader(Bible.OceanVert),
            Bible.Shader(Bible.OceanFrag));
        _underwaterShader = Shader.FromFile(
            gl,
            Bible.Shader(Bible.PostProcessVert),
            Bible.Shader(Bible.UnderwaterFrag));
        _spectrumCompute = ComputeShader.FromFile(
            gl,
            Bible.Shader(Bible.OceanSpectrumCompute));
        _fftCompute = ComputeShader.FromFile(
            gl,
            Bible.Shader(Bible.OceanFftCompute));
        _resolveCompute = ComputeShader.FromFile(
            gl,
            Bible.Shader(Bible.OceanResolveCompute));
        _oceanNormalTexture = new Texture(
            gl,
            Bible.Tex(Bible.OceanNormal),
            TextureColorSpace.Data);

        EnsureMesh(128);
    }

    /// <summary>
    /// Renders the ocean into attachment 0 of <paramref name="targetFbo"/>.
    /// The target already contains the opaque scene and its depth buffer.
    /// </summary>
    public bool Render(
        uint targetFbo,
        int width,
        int height,
        Matrix4x4 view,
        Matrix4x4 projection,
        Vector3 cameraPosition,
        Vector3 sunDirection,
        Vector3 sunColor,
        OceanSettings settings,
        SkyboxSettings skyboxSettings,
        Vector3 fallbackSkyColor,
        ImageBasedLighting? imageBasedLighting,
        bool sceneIsSrgb = false,
        bool outputSrgb = false,
        bool targetHasMrt = true,
        float? simulationTimeSeconds = null)
    {
        LastFrameUnderwater = false;
        _underwaterPassPending = false;
        if (_disposed || targetFbo == 0 || width <= 0 || height <= 0 ||
            !settings.Enabled || !IsValid)
            return false;

        if (!Matrix4x4.Invert(view * projection, out Matrix4x4 inverseViewProjection))
            return false;

        EnsureMesh(settings.GridResolution);
        EnsureWaveSimulationResources(settings);
        EnsureTargets(width, height);

        // The game supplies Engine.Time, which stops while the game is
        // paused. Blowtorch omits it and keeps its editor preview animated.
        float animationTime = simulationTimeSeconds ?? CurrentTimeSeconds();
        UpdateWaveSimulation(animationTime, settings);
        CopyScene(targetFbo, width, height);

        // The CPU query uses the same initial spectrum and dispersion relation
        // as the GPU. It is only used to decide whether the fullscreen
        // underwater pass is needed; the actual per-pixel boundary comes from
        // the surface sidecar written by the visible ocean.
        float cameraWaterHeight = EvaluateCameraWaterHeight(
            cameraPosition,
            animationTime,
            settings);
        float cameraSubmersion = cameraWaterHeight - cameraPosition.Y;

        float oceanSize = MathF.Max(settings.OceanSize, 64.0f);
        float spacing = oceanSize / MathF.Max(settings.GridResolution, 1);
        Vector3 oceanOrigin = ComputeOceanOrigin(cameraPosition, spacing);

        float maximumWaveHeight = MathF.Abs(settings.WaveAmplitude) * 2.8f;
        float waterlineBand = maximumWaveHeight + 0.25f;
        bool renderUnderwater = settings.UnderwaterEnabled &&
                                cameraSubmersion >= -waterlineBand;
        bool cameraBelowWater = settings.UnderwaterEnabled &&
                                 cameraSubmersion > 0.0f;

        _underwaterCameraSubmersion = cameraSubmersion;
        _underwaterAnimationTime = animationTime;
        _underwaterPassPending = renderUnderwater;

        ClearSurfaceData(width, height);

        RenderSurface(
            targetFbo,
            width,
            height,
            view,
            projection,
            inverseViewProjection,
            cameraPosition,
            animationTime,
            sunDirection,
            sunColor,
            settings,
            skyboxSettings,
            fallbackSkyColor,
            imageBasedLighting,
            sceneIsSrgb,
            outputSrgb,
            oceanOrigin,
            oceanSize,
            doubleSided: renderUnderwater);

        LastFrameUnderwater = cameraBelowWater;
        RestoreTargetState(targetFbo, width, height, targetHasMrt);
        return true;
    }

    /// <summary>
    /// Applies the deferred fullscreen underwater treatment to the current
    /// scene color. Call this after all atmospheric passes, especially
    /// volumetric fog, so the underwater view does not get overwritten by a
    /// later compositor.
    /// </summary>
    public bool ApplyUnderwater(
        uint targetFbo,
        int width,
        int height,
        Matrix4x4 view,
        Matrix4x4 projection,
        Vector3 cameraPosition,
        Vector3 sunDirection,
        Vector3 sunColor,
        OceanSettings settings,
        bool sceneIsSrgb = false,
        bool outputSrgb = false,
        bool targetHasMrt = true)
    {
        if (!_underwaterPassPending)
            return false;

        _underwaterPassPending = false;
        if (_disposed || targetFbo == 0 || width <= 0 || height <= 0 ||
            !settings.Enabled || !settings.UnderwaterEnabled || !IsValid ||
            !Matrix4x4.Invert(view * projection, out Matrix4x4 inverseViewProjection))
        {
            return false;
        }

        // Capture the color after fog/cloud composition, while retaining the
        // exact depth written by the opaque scene and ocean surface. The
        // surface sidecar remains the authoritative per-pixel waterline.
        CopyScene(targetFbo, width, height);
        RenderUnderwater(
            targetFbo,
            width,
            height,
            inverseViewProjection,
            cameraPosition,
            _underwaterAnimationTime,
            sunDirection,
            sunColor,
            settings,
            _underwaterCameraSubmersion,
            sceneIsSrgb,
            outputSrgb,
            targetHasMrt);
        RestoreTargetState(targetFbo, width, height, targetHasMrt);
        return true;
    }

    private void RenderSurface(
        uint targetFbo,
        int width,
        int height,
        Matrix4x4 view,
        Matrix4x4 projection,
        Matrix4x4 inverseViewProjection,
        Vector3 cameraPosition,
        float animationTime,
        Vector3 sunDirection,
        Vector3 sunColor,
        OceanSettings settings,
        SkyboxSettings skyboxSettings,
        Vector3 fallbackSkyColor,
        ImageBasedLighting? imageBasedLighting,
        bool sceneIsSrgb,
        bool outputSrgb,
        Vector3 oceanOrigin,
        float oceanSize,
        bool doubleSided)
    {
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, targetFbo);
        _gl.DrawBuffer(DrawBufferMode.ColorAttachment0);
        _gl.Viewport(0, 0, (uint)width, (uint)height);
        _gl.Enable(EnableCap.DepthTest);
        if (doubleSided)
            _gl.Disable(EnableCap.CullFace);
        else
        {
            _gl.Enable(EnableCap.CullFace);
            _gl.CullFace(GLEnum.Back);
        }
        _gl.DepthFunc(DepthFunction.Less);
        _gl.DepthMask(true);
        _gl.Disable(EnableCap.Blend);
        _gl.PolygonMode(GLEnum.FrontAndBack, GLEnum.Fill);

        _surfaceShader.Use();
        _surfaceShader.SetMat4("uInvViewProj", inverseViewProjection);
        SetSurfaceDataUniforms(
            _surfaceShader,
            view,
            projection,
            oceanOrigin,
            oceanSize,
            animationTime,
            settings);

        _surfaceShader.SetVec3("uCameraPosition", cameraPosition);
        Vector3 normalizedSun = sunDirection.LengthSquared() > 1e-8f
            ? Vector3.Normalize(sunDirection)
            : ProceduralSky.FallbackSunDirection;
        _surfaceShader.SetVec3("uSunDirection", normalizedSun);
        _surfaceShader.SetVec3("uSunColor", sunColor);

        Vector3 zenith = skyboxSettings.Mode == SkyboxMode.Procedural
            ? skyboxSettings.ZenithColor
            : fallbackSkyColor;
        Vector3 horizon = skyboxSettings.Mode == SkyboxMode.Procedural
            ? skyboxSettings.HorizonColor
            : fallbackSkyColor;
        Vector3 ground = skyboxSettings.Mode == SkyboxMode.Procedural
            ? skyboxSettings.GroundColor
            : fallbackSkyColor * 0.35f;
        _surfaceShader.SetVec3("uSkyZenithColor", zenith);
        _surfaceShader.SetVec3("uSkyHorizonColor", horizon);
        _surfaceShader.SetVec3("uSkyGroundColor", ground);

        _surfaceShader.SetFloat("uReflectionStrength", settings.ReflectionStrength);
        _surfaceShader.SetFloat("uRefractionStrength", settings.RefractionStrength);
        _surfaceShader.SetFloat("uAbsorptionDistance", settings.AbsorptionDistance);
        _surfaceShader.SetFloat("uSurfaceRoughness", settings.SurfaceRoughness);
        _surfaceShader.SetVec3("uShallowColor", settings.ShallowColor);
        _surfaceShader.SetVec3("uDeepColor", settings.DeepColor);
        _surfaceShader.SetVec3("uFoamColor", settings.FoamColor);
        _surfaceShader.SetFloat("uFoamStrength", settings.FoamStrength);
        _surfaceShader.SetFloat("uFoamDepth", settings.FoamDepth);
        _surfaceShader.SetInt("uOceanNormalMap", OceanNormalTextureUnit);
        _surfaceShader.SetBool(
            "uUseOceanNormalMap",
            settings.NormalMapEnabled && _oceanNormalTexture.ID != 0);
        _surfaceShader.SetFloat(
            "uOceanNormalMapStrength",
            System.Math.Clamp(settings.NormalMapStrength, 0.0f, 1.0f));
        _surfaceShader.SetFloat(
            "uOceanNormalMapScale",
            System.Math.Clamp(settings.NormalMapScale, 0.001f, 0.25f));
        _surfaceShader.SetFloat(
            "uOceanNormalMapDistortion",
            System.Math.Clamp(settings.NormalMapDistortion, 0.0f, 2.0f));
        _surfaceShader.SetBool("uSceneIsSrgb", sceneIsSrgb);
        _surfaceShader.SetBool("uOutputSrgb", outputSrgb);

        _surfaceShader.SetInt("uSceneColor", 0);
        _surfaceShader.SetInt("uSceneDepth", 1);
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, _sceneCopyColor);
        _gl.ActiveTexture(TextureUnit.Texture1);
        _gl.BindTexture(TextureTarget.Texture2D, _sceneCopyDepth);

        BindWaveSimulationTextures();
        _gl.ActiveTexture(TextureUnit.Texture0 + OceanNormalTextureUnit);
        _gl.BindTexture(TextureTarget.Texture2D, _oceanNormalTexture.ID);
        _gl.ActiveTexture(TextureUnit.Texture0);

        // The fragment shader writes depth, coverage and the same normal that
        // it uses for shading into this sidecar image.
        _gl.BindImageTexture(
            0,
            _surfaceDataColor,
            0,
            false,
            0,
            GLEnum.WriteOnly,
            GLEnum.Rgba16f);

        if (imageBasedLighting != null)
            imageBasedLighting.Bind(_surfaceShader);
        else
        {
            _surfaceShader.SetBool("uUseIbl", false);
            _surfaceShader.SetFloat("uIblIntensity", 1.0f);
        }

        _mesh?.Draw();
        _gl.MemoryBarrier(
            MemoryBarrierMask.ShaderImageAccessBarrierBit |
            MemoryBarrierMask.TextureFetchBarrierBit);
        _gl.BindImageTexture(0, 0, 0, false, 0, GLEnum.WriteOnly, GLEnum.Rgba16f);
        _gl.ActiveTexture(TextureUnit.Texture0 + OceanNormalTextureUnit);
        _gl.BindTexture(TextureTarget.Texture2D, 0);
        _gl.ActiveTexture(TextureUnit.Texture0);
    }

    private void ClearSurfaceData(int width, int height)
    {
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _surfaceDataFbo);
        _gl.DrawBuffer(DrawBufferMode.ColorAttachment0);
        _gl.Viewport(0, 0, (uint)width, (uint)height);
        _gl.ClearColor(1.0f, 0.0f, 0.0f, 0.0f);
        _gl.Clear(ClearBufferMask.ColorBufferBit);
    }

    private void SetSurfaceDataUniforms(
        Shader shader,
        Matrix4x4 view,
        Matrix4x4 projection,
        Vector3 oceanOrigin,
        float oceanSize,
        float animationTime,
        OceanSettings settings)
    {
        shader.SetMat4("uView", view);
        shader.SetMat4("uProj", projection);
        shader.SetVec3("uOceanOrigin", oceanOrigin);
        shader.SetFloat("uWaterLevel", settings.WaterLevel);
        shader.SetFloat("uOceanSize", oceanSize);
        shader.SetFloat("uWaveTime", animationTime);
        shader.SetFloat("uWaveAmplitude", settings.WaveAmplitude);
        shader.SetFloat("uWaveLength", settings.WaveLength);
        shader.SetFloat("uWaveSpeed", settings.WaveSpeed);
        shader.SetFloat("uWaveChoppiness", settings.WaveChoppiness);
        shader.SetVec2("uWaveDirection", settings.WaveDirection);
        shader.SetInt("uDebugView", System.Math.Clamp(settings.DebugView, 0, 3));
        shader.SetBool("uUseWaveTextures", WaveSimulationValid);

        for (int band = 0; band < CascadeCount; band++)
        {
            WaveCascade? cascade = _cascades[band];
            float patchSize = cascade?.PatchSize ?? ComputeWavePatchWorldSize(settings, band);
            Vector2 offset = GetCascadeOffset(band, patchSize);
            shader.SetInt($"uWaveSurface{band}", WaveSurfaceTextureUnit0 + band);
            shader.SetInt($"uWaveSlope{band}", WaveSlopeTextureUnit0 + band);
            shader.SetFloat($"uWavePatchSize{band}", patchSize);
            shader.SetVec2($"uWaveOffset{band}", offset);
        }
    }

    private void RenderUnderwater(
        uint targetFbo,
        int width,
        int height,
        Matrix4x4 inverseViewProjection,
        Vector3 cameraPosition,
        float animationTime,
        Vector3 sunDirection,
        Vector3 sunColor,
        OceanSettings settings,
        float cameraSubmersion,
        bool sceneIsSrgb,
        bool outputSrgb,
        bool targetHasMrt)
    {
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _underwaterFbo);
        _gl.DrawBuffer(DrawBufferMode.ColorAttachment0);
        _gl.Viewport(0, 0, (uint)width, (uint)height);
        _gl.Disable(EnableCap.DepthTest);
        _gl.Disable(EnableCap.CullFace);
        _gl.Disable(EnableCap.Blend);

        _underwaterShader.Use();
        _underwaterShader.SetInt("uSceneColor", 0);
        _underwaterShader.SetInt("uSceneDepth", 1);
        _underwaterShader.SetInt("uWaterSurfaceData", 2);
        _underwaterShader.SetMat4("uInvViewProj", inverseViewProjection);
        _underwaterShader.SetVec3("uCameraPosition", cameraPosition);
        _underwaterShader.SetFloat("uCameraSubmersion", cameraSubmersion);
        _underwaterShader.SetVec3("uUnderwaterColor", settings.UnderwaterColor);
        _underwaterShader.SetVec3(
            "uSunDirection",
            sunDirection.LengthSquared() > 1e-8f
                ? Vector3.Normalize(sunDirection)
                : ProceduralSky.FallbackSunDirection);
        _underwaterShader.SetVec3("uSunColor", sunColor);
        _underwaterShader.SetFloat("uUnderwaterFogDensity", settings.UnderwaterFogDensity);
        _underwaterShader.SetFloat("uUnderwaterDistortion", settings.UnderwaterDistortion);
        _underwaterShader.SetFloat("uUnderwaterDarkening", settings.UnderwaterDarkening);
        _underwaterShader.SetFloat("uTime", animationTime);
        _underwaterShader.SetFloat(
            "uWaterlineSoftness",
            MathF.Max(0.025f, MathF.Abs(settings.WaveAmplitude) * 0.04f));
        _underwaterShader.SetBool("uSceneIsSrgb", sceneIsSrgb);
        _underwaterShader.SetBool("uOutputSrgb", outputSrgb);

        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, _sceneCopyColor);
        _gl.ActiveTexture(TextureUnit.Texture1);
        _gl.BindTexture(TextureTarget.Texture2D, _sceneCopyDepth);
        _gl.ActiveTexture(TextureUnit.Texture2);
        _gl.BindTexture(TextureTarget.Texture2D, _surfaceDataColor);
        _quad.Draw();

        _gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, _underwaterFbo);
        _gl.ReadBuffer(ReadBufferMode.ColorAttachment0);
        _gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, targetFbo);
        _gl.DrawBuffer(DrawBufferMode.ColorAttachment0);
        _gl.BlitFramebuffer(
            0, 0, width, height,
            0, 0, width, height,
            ClearBufferMask.ColorBufferBit,
            BlitFramebufferFilter.Nearest);
    }

    private void CopyScene(uint targetFbo, int width, int height)
    {
        _gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, targetFbo);
        _gl.ReadBuffer(ReadBufferMode.ColorAttachment0);
        _gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, _sceneCopyFbo);
        _gl.DrawBuffer(DrawBufferMode.ColorAttachment0);
        _gl.BlitFramebuffer(
            0, 0, width, height,
            0, 0, width, height,
            ClearBufferMask.ColorBufferBit,
            BlitFramebufferFilter.Nearest);

        _gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, targetFbo);
        _gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, _sceneCopyFbo);
        _gl.BlitFramebuffer(
            0, 0, width, height,
            0, 0, width, height,
            ClearBufferMask.DepthBufferBit,
            BlitFramebufferFilter.Nearest);
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, targetFbo);
    }

    private void EnsureMesh(int requestedResolution)
    {
        int resolution = System.Math.Clamp(requestedResolution, 32, 256);
        if (_mesh != null && _meshResolution == resolution)
            return;

        int side = resolution + 1;
        Vertex[] vertices = new Vertex[side * side];
        for (int z = 0; z < side; z++)
        {
            float v = (float)z / resolution;
            for (int x = 0; x < side; x++)
            {
                float u = (float)x / resolution;
                float adaptiveU = AdaptiveCoordinate(u);
                float adaptiveV = AdaptiveCoordinate(v);
                vertices[z * side + x] = new Vertex
                {
                    // Concentrate vertices near the camera while retaining a
                    // continuous grid, so no ring stitching is required.
                    Position = new Vector3(adaptiveU * 0.5f, 0.0f, adaptiveV * 0.5f),
                    TexCoord = new Vector2(u, v),
                    Normal = Vector3.UnitY
                };
            }
        }

        uint[] indices = new uint[resolution * resolution * 6];
        int index = 0;
        for (int z = 0; z < resolution; z++)
        {
            for (int x = 0; x < resolution; x++)
            {
                uint p00 = (uint)(z * side + x);
                uint p10 = p00 + 1;
                uint p01 = (uint)((z + 1) * side + x);
                uint p11 = p01 + 1;

                indices[index++] = p10;
                indices[index++] = p00;
                indices[index++] = p11;
                indices[index++] = p01;
                indices[index++] = p11;
                indices[index++] = p00;
            }
        }

        Mesh replacement = new(_gl, vertices, indices);
        _mesh?.Dispose();
        _mesh = replacement;
        _meshResolution = resolution;
    }

    private void EnsureWaveSimulationResources(OceanSettings settings)
    {
        if (SpectrumSettingsMatch(settings))
            return;

        DeleteWaveSimulationResources();
        for (int band = 0; band < CascadeCount; band++)
        {
            float patchSize = ComputeWavePatchWorldSize(settings, band);
            Vector2[] h0Cpu = BuildInitialSpectrum(settings, band, patchSize);
            WaveCascade cascade = new()
            {
                PatchSize = patchSize,
                CpuH0 = h0Cpu,
                H0 = CreateH0Texture(h0Cpu),
                SpectrumA = CreateWaveTexture(InternalFormat.Rgba32f, TextureMinFilter.Nearest, TextureWrapMode.ClampToEdge),
                SpectrumB = CreateWaveTexture(InternalFormat.Rgba32f, TextureMinFilter.Nearest, TextureWrapMode.ClampToEdge),
                SpectrumC = CreateWaveTexture(InternalFormat.Rgba32f, TextureMinFilter.Nearest, TextureWrapMode.ClampToEdge),
                PingA = CreateWaveTexture(InternalFormat.Rgba32f, TextureMinFilter.Nearest, TextureWrapMode.ClampToEdge),
                PingB = CreateWaveTexture(InternalFormat.Rgba32f, TextureMinFilter.Nearest, TextureWrapMode.ClampToEdge),
                PingC = CreateWaveTexture(InternalFormat.Rgba32f, TextureMinFilter.Nearest, TextureWrapMode.ClampToEdge),
                Surface = CreateWaveTexture(InternalFormat.Rgba16f, TextureMinFilter.Linear, TextureWrapMode.Repeat),
                Slope = CreateWaveTexture(InternalFormat.Rgba16f, TextureMinFilter.Linear, TextureWrapMode.Repeat)
            };
            _cascades[band] = cascade;
        }

        _spectrumWaveLength = settings.WaveLength;
        _spectrumWindSpeed = settings.WindSpeed;
        _spectrumSmallWaveLength = settings.SmallWaveLength;
        _spectrumDirection = NormalizeWaveDirection(settings.WaveDirection);
        _spectrumSeed = settings.SpectrumSeed;
        _waveResourcesInitialized = true;
        _simulationStateValid = false;
        _lastSimulationTime = float.NaN;
    }

    private bool SpectrumSettingsMatch(OceanSettings settings)
    {
        if (!_waveResourcesInitialized)
            return false;

        Vector2 direction = NormalizeWaveDirection(settings.WaveDirection);
        return NearlyEqual(_spectrumWaveLength, settings.WaveLength) &&
               NearlyEqual(_spectrumWindSpeed, settings.WindSpeed) &&
               NearlyEqual(_spectrumSmallWaveLength, settings.SmallWaveLength) &&
               Vector2.DistanceSquared(_spectrumDirection, direction) < 1e-8f &&
               _spectrumSeed == settings.SpectrumSeed;
    }

    private bool SimulationParametersMatch(float time, OceanSettings settings)
    {
        return _simulationStateValid &&
               NearlyEqual(_lastSimulationTime, time) &&
               NearlyEqual(_lastSimulationAmplitude, settings.WaveAmplitude) &&
               NearlyEqual(_lastSimulationSpeed, settings.WaveSpeed) &&
               NearlyEqual(_lastSimulationChoppiness, settings.WaveChoppiness);
    }

    private void UpdateWaveSimulation(float animationTime, OceanSettings settings)
    {
        if (!WaveSimulationValid || SimulationParametersMatch(animationTime, settings))
            return;

        uint groups2D = (uint)((WaveSimulationResolution + 7) / 8);
        for (int band = 0; band < CascadeCount; band++)
        {
            WaveCascade cascade = _cascades[band]!;
            float phaseTime = animationTime * settings.WaveSpeed;

            _spectrumCompute.Use();
            _spectrumCompute.SetInt("uSize", WaveSimulationResolution);
            _spectrumCompute.SetFloat("uPatchSize", cascade.PatchSize);
            _spectrumCompute.SetFloat("uTime", phaseTime);
            _spectrumCompute.SetFloat("uAmplitude", settings.WaveAmplitude);
            _spectrumCompute.SetFloat("uChoppiness", settings.WaveChoppiness);
            _spectrumCompute.SetFloat("uCascadeWeight", CascadeWeights[band]);
            BindImage(0, cascade.H0, GLEnum.ReadOnly, (GLEnum)0x8230);
            BindImage(1, cascade.SpectrumA, GLEnum.WriteOnly, GLEnum.Rgba32f);
            BindImage(2, cascade.SpectrumB, GLEnum.WriteOnly, GLEnum.Rgba32f);
            BindImage(3, cascade.SpectrumC, GLEnum.WriteOnly, GLEnum.Rgba32f);
            _gl.DispatchCompute(groups2D, groups2D, 1);
            WaveMemoryBarrier();

            _fftCompute.Use();
            _fftCompute.SetInt("uSize", WaveSimulationResolution);
            _fftCompute.SetInt("uAxis", 0);
            BindImage(0, cascade.SpectrumA, GLEnum.ReadOnly, GLEnum.Rgba32f);
            BindImage(1, cascade.SpectrumB, GLEnum.ReadOnly, GLEnum.Rgba32f);
            BindImage(2, cascade.SpectrumC, GLEnum.ReadOnly, GLEnum.Rgba32f);
            BindImage(3, cascade.PingA, GLEnum.WriteOnly, GLEnum.Rgba32f);
            BindImage(4, cascade.PingB, GLEnum.WriteOnly, GLEnum.Rgba32f);
            BindImage(5, cascade.PingC, GLEnum.WriteOnly, GLEnum.Rgba32f);
            _gl.DispatchCompute((uint)WaveSimulationResolution, 1, 1);
            WaveMemoryBarrier();

            _fftCompute.SetInt("uAxis", 1);
            BindImage(0, cascade.PingA, GLEnum.ReadOnly, GLEnum.Rgba32f);
            BindImage(1, cascade.PingB, GLEnum.ReadOnly, GLEnum.Rgba32f);
            BindImage(2, cascade.PingC, GLEnum.ReadOnly, GLEnum.Rgba32f);
            BindImage(3, cascade.SpectrumA, GLEnum.WriteOnly, GLEnum.Rgba32f);
            BindImage(4, cascade.SpectrumB, GLEnum.WriteOnly, GLEnum.Rgba32f);
            BindImage(5, cascade.SpectrumC, GLEnum.WriteOnly, GLEnum.Rgba32f);
            _gl.DispatchCompute((uint)WaveSimulationResolution, 1, 1);
            WaveMemoryBarrier();

            _resolveCompute.Use();
            _resolveCompute.SetInt("uSize", WaveSimulationResolution);
            _resolveCompute.SetFloat(
                "uFoamScale",
                1.0f + MathF.Max(settings.WaveChoppiness, 0.0f) * 0.35f);
            BindImage(0, cascade.SpectrumA, GLEnum.ReadOnly, GLEnum.Rgba32f);
            BindImage(1, cascade.SpectrumB, GLEnum.ReadOnly, GLEnum.Rgba32f);
            BindImage(2, cascade.SpectrumC, GLEnum.ReadOnly, GLEnum.Rgba32f);
            BindImage(3, cascade.Surface, GLEnum.WriteOnly, GLEnum.Rgba16f);
            BindImage(4, cascade.Slope, GLEnum.WriteOnly, GLEnum.Rgba16f);
            _gl.DispatchCompute(groups2D, groups2D, 1);
            WaveMemoryBarrier();
        }

        UnbindWaveImages();
        _gl.UseProgram(0);
        _simulationStateValid = true;
        _lastSimulationTime = animationTime;
        _lastSimulationAmplitude = settings.WaveAmplitude;
        _lastSimulationSpeed = settings.WaveSpeed;
        _lastSimulationChoppiness = settings.WaveChoppiness;
    }

    private void BindImage(int unit, uint texture, GLEnum access, GLEnum format)
    {
        _gl.BindImageTexture(
            (uint)unit,
            texture,
            0,
            false,
            0,
            access,
            format);
    }

    private void UnbindWaveImages()
    {
        for (int unit = 0; unit < 6; unit++)
            _gl.BindImageTexture((uint)unit, 0, 0, false, 0, GLEnum.WriteOnly, GLEnum.Rgba32f);
    }

    private void WaveMemoryBarrier()
    {
        _gl.MemoryBarrier(
            MemoryBarrierMask.ShaderImageAccessBarrierBit |
            MemoryBarrierMask.TextureFetchBarrierBit);
    }

    private void BindWaveSimulationTextures()
    {
        for (int band = 0; band < CascadeCount; band++)
        {
            uint surface = 0;
            uint slope = 0;
            if (WaveSimulationValid && _cascades[band] is WaveCascade cascade)
            {
                surface = cascade.Surface;
                slope = cascade.Slope;
            }

            _gl.ActiveTexture(TextureUnit.Texture0 + WaveSurfaceTextureUnit0 + band);
            _gl.BindTexture(TextureTarget.Texture2D, surface);
            _gl.ActiveTexture(TextureUnit.Texture0 + WaveSlopeTextureUnit0 + band);
            _gl.BindTexture(TextureTarget.Texture2D, slope);
        }
        _gl.ActiveTexture(TextureUnit.Texture0);
    }

    private uint CreateH0Texture(Vector2[] values)
    {
        float[] packed = new float[values.Length * 2];
        for (int i = 0; i < values.Length; i++)
        {
            packed[i * 2] = values[i].X;
            packed[i * 2 + 1] = values[i].Y;
        }

        uint texture = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, texture);
        fixed (float* pointer = packed)
        {
            _gl.TexImage2D(
                TextureTarget.Texture2D,
                0,
                (int)(InternalFormat)0x8230,
                (uint)WaveSimulationResolution,
                (uint)WaveSimulationResolution,
                0,
                (PixelFormat)0x8227,
                PixelType.Float,
                pointer);
        }
        ConfigureWaveTexture(TextureMinFilter.Nearest, TextureWrapMode.ClampToEdge);
        _gl.BindTexture(TextureTarget.Texture2D, 0);
        return texture;
    }

    private uint CreateWaveTexture(
        InternalFormat format,
        TextureMinFilter minFilter,
        TextureWrapMode wrapMode)
    {
        uint texture = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, texture);
        _gl.TexImage2D(
            TextureTarget.Texture2D,
            0,
            (int)format,
            WaveSimulationResolution,
            WaveSimulationResolution,
            0,
            PixelFormat.Rgba,
            PixelType.Float,
            null);
        ConfigureWaveTexture(minFilter, wrapMode);
        _gl.BindTexture(TextureTarget.Texture2D, 0);
        return texture;
    }

    private void ConfigureWaveTexture(
        TextureMinFilter minFilter,
        TextureWrapMode wrapMode)
    {
        _gl.TexParameter(
            TextureTarget.Texture2D,
            TextureParameterName.TextureMinFilter,
            (int)minFilter);
        _gl.TexParameter(
            TextureTarget.Texture2D,
            TextureParameterName.TextureMagFilter,
            (int)(minFilter == TextureMinFilter.Nearest
                ? TextureMagFilter.Nearest
                : TextureMagFilter.Linear));
        _gl.TexParameter(
            TextureTarget.Texture2D,
            TextureParameterName.TextureWrapS,
            (int)wrapMode);
        _gl.TexParameter(
            TextureTarget.Texture2D,
            TextureParameterName.TextureWrapT,
            (int)wrapMode);
    }

    private Vector2[] BuildInitialSpectrum(
        OceanSettings settings,
        int band,
        float patchSize)
    {
        int size = WaveSimulationResolution;
        Vector2[] values = new Vector2[size * size];
        Random random = new(unchecked(settings.SpectrumSeed + (band + 1) * 104729));
        Vector2 windDirection = NormalizeWaveDirection(settings.WaveDirection);
        float windSpeed = MathF.Max(settings.WindSpeed, 0.1f);
        float largestWave = MathF.Max(windSpeed * windSpeed / Gravity, 1.0f);
        float largestWaveSquared = largestWave * largestWave;
        float smallWaveLength = MathF.Max(settings.SmallWaveLength, 0.05f);
        float smallWaveLengthSquared = smallWaveLength * smallWaveLength;
        double totalPower = 0.0;

        for (int y = 0; y < size; y++)
        {
            int signedY = y <= size / 2 ? y : y - size;
            for (int x = 0; x < size; x++)
            {
                int signedX = x <= size / 2 ? x : x - size;
                int index = y * size + x;
                Vector2 waveNumber = TwoPi *
                    new Vector2(signedX, signedY) / MathF.Max(patchSize, 0.001f);
                float length = waveNumber.Length();
                if (length < 0.00001f)
                    continue;

                Vector2 direction = waveNumber / length;
                float alignment = Vector2.Dot(direction, windDirection);
                float directionalEnergy = alignment * alignment;
                if (alignment < 0.0f)
                    directionalEnergy *= 0.25f;

                float lengthSquared = length * length;
                float longWaveEnvelope = MathF.Exp(
                    -1.0f / MathF.Max(lengthSquared * largestWaveSquared, 0.000001f));
                float smallWaveEnvelope = MathF.Exp(
                    -lengthSquared * smallWaveLengthSquared);
                float phillips = longWaveEnvelope *
                                 directionalEnergy *
                                 smallWaveEnvelope /
                                 MathF.Max(lengthSquared * lengthSquared, 0.000001f);
                if (phillips <= 0.0f || !float.IsFinite(phillips))
                    continue;

                Vector2 gaussian = new(
                    NextGaussian(random),
                    NextGaussian(random));
                values[index] = gaussian * MathF.Sqrt(phillips * 0.5f);
                totalPower += phillips;
            }
        }

        // The spectrum is normalized so WaveAmplitude remains a useful,
        // scene-scale control instead of depending on the arbitrary FFT grid.
        // The two-dimensional inverse FFT is normalized by N on each axis,
        // so its output is divided by N². Scale the frequency coefficients by
        // N² here; using only N would make every wave approximately N times
        // too small and leave the rendered ocean visually flat.
        float normalization = totalPower > 1e-12
            ? (size * size) / MathF.Sqrt((float)totalPower)
            : 1.0f;
        for (int i = 0; i < values.Length; i++)
            values[i] *= normalization;

        return values;
    }

    private static float NextGaussian(Random random)
    {
        float first = MathF.Max((float)random.NextDouble(), 1e-6f);
        float second = (float)random.NextDouble();
        return MathF.Sqrt(-2.0f * MathF.Log(first)) *
               MathF.Cos(TwoPi * second);
    }

    private static float ComputeWavePatchWorldSize(
        OceanSettings settings,
        int band)
    {
        float baseLength = MathF.Max(settings.WaveLength, 4.0f);
        return band switch
        {
            0 => MathF.Max(baseLength * 64.0f, 1024.0f),
            1 => MathF.Max(baseLength * 16.0f, 256.0f),
            _ => MathF.Max(baseLength * 4.0f, 64.0f)
        };
    }

    private static Vector2 GetCascadeOffset(int band, float patchSize) =>
        CascadeOffsetFactors[System.Math.Clamp(band, 0, CascadeCount - 1)] * patchSize;

    private static Vector2 NormalizeWaveDirection(Vector2 direction) =>
        direction.LengthSquared() > 1e-8f
            ? Vector2.Normalize(direction)
            : Vector2.UnitX;

    private static bool NearlyEqual(float left, float right) =>
        float.IsFinite(left) && float.IsFinite(right) &&
        MathF.Abs(left - right) <= 0.00001f;

    private float EvaluateCameraWaterHeight(
        Vector3 cameraPosition,
        float animationTime,
        OceanSettings settings)
    {
        Vector2 targetWorldPosition = new(cameraPosition.X, cameraPosition.Z);
        Vector3 displacement = WaveSimulationValid
            ? EvaluateSpectralWaveDisplacement(targetWorldPosition, animationTime, settings)
            : EvaluateFallbackWaveDisplacement(targetWorldPosition, animationTime, settings);

        // One fixed-point correction accounts for horizontal choppiness
        // without making the CPU-side gate scale with the FFT resolution.
        if (WaveSimulationValid)
        {
            targetWorldPosition = new Vector2(
                cameraPosition.X - displacement.X,
                cameraPosition.Z - displacement.Z);
            displacement = EvaluateSpectralWaveDisplacement(
                targetWorldPosition,
                animationTime,
                settings);
        }

        return settings.WaterLevel + displacement.Y;
    }

    private Vector3 EvaluateSpectralWaveDisplacement(
        Vector2 worldPosition,
        float animationTime,
        OceanSettings settings)
    {
        Vector3 displacement = Vector3.Zero;
        float inverseSizeSquared =
            1.0f / (WaveSimulationResolution * WaveSimulationResolution);
        float phaseTime = animationTime * settings.WaveSpeed;

        for (int band = 0; band < CascadeCount; band++)
        {
            WaveCascade cascade = _cascades[band]!;
            Vector2 samplePosition =
                worldPosition + GetCascadeOffset(band, cascade.PatchSize);
            Vector2[] h0 = cascade.CpuH0;
            int size = WaveSimulationResolution;

            for (int y = 0; y < size; y++)
            {
                int signedY = y <= size / 2 ? y : y - size;
                int negativeY = (size - y) % size;
                for (int x = 0; x < size; x++)
                {
                    int signedX = x <= size / 2 ? x : x - size;
                    int negativeX = (size - x) % size;
                    int index = y * size + x;
                    int negativeIndex = negativeY * size + negativeX;
                    Vector2 waveNumber = TwoPi *
                        new Vector2(signedX, signedY) /
                        MathF.Max(cascade.PatchSize, 0.001f);
                    float length = waveNumber.Length();
                    if (length < 0.00001f)
                        continue;

                    float angularFrequency = MathF.Sqrt(Gravity * length);
                    Vector2 forward = ComplexMultiply(
                        h0[index],
                        ComplexExp(angularFrequency * phaseTime));
                    Vector2 backward = ComplexMultiply(
                        ComplexConjugate(h0[negativeIndex]),
                        ComplexExp(-angularFrequency * phaseTime));
                    Vector2 height = (forward + backward) *
                        (settings.WaveAmplitude * CascadeWeights[band]);
                    Vector2 spatial = ComplexExp(Vector2.Dot(
                        waveNumber,
                        samplePosition));

                    displacement.Y += ComplexMultiply(height, spatial).X *
                                       inverseSizeSquared;

                    float inverseLength = 1.0f / length;
                    Vector2 displacementX = ComplexMultiply(
                        new Vector2(
                            0.0f,
                            -waveNumber.X * inverseLength *
                            settings.WaveChoppiness),
                        height);
                    Vector2 displacementZ = ComplexMultiply(
                        new Vector2(
                            0.0f,
                            -waveNumber.Y * inverseLength *
                            settings.WaveChoppiness),
                        height);
                    displacement.X += ComplexMultiply(
                        displacementX,
                        spatial).X * inverseSizeSquared;
                    displacement.Z += ComplexMultiply(
                        displacementZ,
                        spatial).X * inverseSizeSquared;
                }
            }
        }

        return displacement;
    }

    private static Vector2 ComplexMultiply(Vector2 a, Vector2 b) =>
        new(
            a.X * b.X - a.Y * b.Y,
            a.X * b.Y + a.Y * b.X);

    private static Vector2 ComplexConjugate(Vector2 value) =>
        new(value.X, -value.Y);

    private static Vector2 ComplexExp(float angle) =>
        new(MathF.Cos(angle), MathF.Sin(angle));

    private static Vector3 EvaluateFallbackWaveDisplacement(
        Vector2 worldPosition,
        float animationTime,
        OceanSettings settings)
    {
        Vector2 direction = NormalizeWaveDirection(settings.WaveDirection);
        Vector2 side = new(-direction.Y, direction.X);
        Vector3 result = Vector3.Zero;
        Vector2[] localDirections =
        [
            new(1.0f, 0.05f),
            new(-0.62f, 0.78f),
            new(0.37f, -0.93f),
            new(0.91f, -0.42f)
        ];
        float[] lengths = [1.0f, 0.52f, 0.23f, 0.10f];
        float[] amplitudes = [0.62f, 0.24f, 0.10f, 0.04f];

        for (int i = 0; i < localDirections.Length; i++)
        {
            Vector2 waveDirection = Vector2.Normalize(
                direction * localDirections[i].X +
                side * localDirections[i].Y);
            float wavelength = MathF.Max(settings.WaveLength * lengths[i], 0.5f);
            float waveNumber = TwoPi / wavelength;
            float phase = Vector2.Dot(worldPosition, waveDirection) * waveNumber -
                          animationTime * settings.WaveSpeed * (0.7f + i * 0.2f);
            float amplitude = settings.WaveAmplitude * amplitudes[i];
            result.Y += amplitude * MathF.Sin(phase);
            float horizontal = amplitude * settings.WaveChoppiness * MathF.Cos(phase);
            result.X += waveDirection.X * horizontal;
            result.Z += waveDirection.Y * horizontal;
        }

        return result;
    }

    private static float AdaptiveCoordinate(float normalized)
    {
        float centered = normalized * 2.0f - 1.0f;
        float sign = centered < 0.0f ? -1.0f : 1.0f;
        return sign * MathF.Pow(MathF.Abs(centered), 1.65f);
    }

    private void EnsureTargets(int width, int height)
    {
        if (_sceneCopyFbo != 0 && _width == width && _height == height)
            return;

        DeleteTargets();
        _width = width;
        _height = height;

        _sceneCopyFbo = _gl.GenFramebuffer();
        _sceneCopyColor = CreateTexture(
            TextureTarget.Texture2D,
            InternalFormat.Rgba16f,
            PixelFormat.Rgba,
            width,
            height,
            TextureMinFilter.Linear,
            TextureMagFilter.Linear);
        _sceneCopyDepth = CreateTexture(
            TextureTarget.Texture2D,
            InternalFormat.DepthComponent32f,
            PixelFormat.DepthComponent,
            width,
            height,
            TextureMinFilter.Nearest,
            TextureMagFilter.Nearest);
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _sceneCopyFbo);
        _gl.FramebufferTexture2D(
            FramebufferTarget.Framebuffer,
            FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D,
            _sceneCopyColor,
            0);
        _gl.FramebufferTexture2D(
            FramebufferTarget.Framebuffer,
            FramebufferAttachment.DepthAttachment,
            TextureTarget.Texture2D,
            _sceneCopyDepth,
            0);
        _gl.DrawBuffer(DrawBufferMode.ColorAttachment0);
        CheckFramebuffer("ocean scene copy");

        _underwaterFbo = _gl.GenFramebuffer();
        _underwaterColor = CreateTexture(
            TextureTarget.Texture2D,
            InternalFormat.Rgba16f,
            PixelFormat.Rgba,
            width,
            height,
            TextureMinFilter.Linear,
            TextureMagFilter.Linear);
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _underwaterFbo);
        _gl.FramebufferTexture2D(
            FramebufferTarget.Framebuffer,
            FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D,
            _underwaterColor,
            0);
        _gl.DrawBuffer(DrawBufferMode.ColorAttachment0);
        CheckFramebuffer("ocean underwater");

        _surfaceDataFbo = _gl.GenFramebuffer();
        _surfaceDataColor = CreateTexture(
            TextureTarget.Texture2D,
            InternalFormat.Rgba16f,
            PixelFormat.Rgba,
            width,
            height,
            TextureMinFilter.Linear,
            TextureMagFilter.Linear);
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _surfaceDataFbo);
        _gl.FramebufferTexture2D(
            FramebufferTarget.Framebuffer,
            FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D,
            _surfaceDataColor,
            0);
        _gl.DrawBuffer(DrawBufferMode.ColorAttachment0);
        CheckFramebuffer("ocean surface data");
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    private uint CreateTexture(
        TextureTarget target,
        InternalFormat format,
        PixelFormat pixelFormat,
        int width,
        int height,
        TextureMinFilter minFilter,
        TextureMagFilter magFilter)
    {
        uint texture = _gl.GenTexture();
        _gl.BindTexture(target, texture);
        _gl.TexImage2D(
            target,
            0,
            (int)format,
            (uint)width,
            (uint)height,
            0,
            pixelFormat,
            PixelType.Float,
            null);
        _gl.TexParameter(target, TextureParameterName.TextureMinFilter, (int)minFilter);
        _gl.TexParameter(target, TextureParameterName.TextureMagFilter, (int)magFilter);
        _gl.TexParameter(target, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        _gl.TexParameter(target, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
        if (format == InternalFormat.DepthComponent32f)
            _gl.TexParameter(target, TextureParameterName.TextureCompareMode, (int)TextureCompareMode.None);
        _gl.BindTexture(target, 0);
        return texture;
    }

    private void RestoreTargetState(uint targetFbo, int width, int height, bool targetHasMrt)
    {
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, targetFbo);
        if (targetHasMrt)
        {
            _gl.DrawBuffers(new[]
            {
                DrawBufferMode.ColorAttachment0,
                DrawBufferMode.ColorAttachment1,
                DrawBufferMode.ColorAttachment2,
                DrawBufferMode.ColorAttachment3
            });
        }
        else
        {
            _gl.DrawBuffer(DrawBufferMode.ColorAttachment0);
        }
        _gl.Viewport(0, 0, (uint)width, (uint)height);
        _gl.Enable(EnableCap.DepthTest);
        _gl.Enable(EnableCap.CullFace);
        _gl.CullFace(GLEnum.Back);
        _gl.DepthFunc(DepthFunction.Less);
        _gl.DepthMask(true);
        _gl.Disable(EnableCap.Blend);
        _gl.ActiveTexture(TextureUnit.Texture0);
    }

    private void DeleteTargets()
    {
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        if (_sceneCopyColor != 0) _gl.DeleteTexture(_sceneCopyColor);
        if (_sceneCopyDepth != 0) _gl.DeleteTexture(_sceneCopyDepth);
        if (_sceneCopyFbo != 0) _gl.DeleteFramebuffer(_sceneCopyFbo);
        if (_underwaterColor != 0) _gl.DeleteTexture(_underwaterColor);
        if (_underwaterFbo != 0) _gl.DeleteFramebuffer(_underwaterFbo);
        if (_surfaceDataColor != 0) _gl.DeleteTexture(_surfaceDataColor);
        if (_surfaceDataFbo != 0) _gl.DeleteFramebuffer(_surfaceDataFbo);
        _sceneCopyColor = 0;
        _sceneCopyDepth = 0;
        _sceneCopyFbo = 0;
        _underwaterColor = 0;
        _underwaterFbo = 0;
        _surfaceDataColor = 0;
        _surfaceDataFbo = 0;
    }

    private void DeleteWaveSimulationResources()
    {
        for (int band = 0; band < CascadeCount; band++)
        {
            if (_cascades[band] is WaveCascade cascade)
            {
                cascade.Dispose(_gl);
                _cascades[band] = null;
            }
        }

        _waveResourcesInitialized = false;
        _simulationStateValid = false;
        _lastSimulationTime = float.NaN;
    }

    private void CheckFramebuffer(string name)
    {
        GLEnum status = _gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        if (status != GLEnum.FramebufferComplete)
            throw new InvalidOperationException(
                $"Ocean framebuffer incomplete during {name}: {status}");
    }

    private float CurrentTimeSeconds() =>
        (Environment.TickCount64 - _timeOriginMilliseconds) * 0.001f;

    private static Vector3 ComputeOceanOrigin(Vector3 cameraPosition, float spacing)
    {
        float safeSpacing = MathF.Max(spacing, 0.001f);
        return new Vector3(
            MathF.Floor(cameraPosition.X / safeSpacing) * safeSpacing,
            0.0f,
            MathF.Floor(cameraPosition.Z / safeSpacing) * safeSpacing);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        DeleteTargets();
        DeleteWaveSimulationResources();
        _mesh?.Dispose();
        _surfaceShader.Dispose();
        _underwaterShader.Dispose();
        _spectrumCompute.Dispose();
        _fftCompute.Dispose();
        _resolveCompute.Dispose();
        _oceanNormalTexture.Dispose();
        _quad.Dispose();
        _mesh = null;
    }

    private sealed class WaveCascade
    {
        public float PatchSize;
        public Vector2[] CpuH0 = Array.Empty<Vector2>();
        public uint H0;
        public uint SpectrumA;
        public uint SpectrumB;
        public uint SpectrumC;
        public uint PingA;
        public uint PingB;
        public uint PingC;
        public uint Surface;
        public uint Slope;

        public void Dispose(GL gl)
        {
            Delete(gl, ref H0);
            Delete(gl, ref SpectrumA);
            Delete(gl, ref SpectrumB);
            Delete(gl, ref SpectrumC);
            Delete(gl, ref PingA);
            Delete(gl, ref PingB);
            Delete(gl, ref PingC);
            Delete(gl, ref Surface);
            Delete(gl, ref Slope);
            CpuH0 = Array.Empty<Vector2>();
        }

        private static void Delete(GL gl, ref uint texture)
        {
            if (texture != 0)
                gl.DeleteTexture(texture);
            texture = 0;
        }
    }
}
