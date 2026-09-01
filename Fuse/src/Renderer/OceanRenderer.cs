using System.Numerics;
using Fuse.Core;
using Fuse.Renderer.PostProcess;
using Fuse.Scene.Model;
using Silk.NET.OpenGL;

namespace Fuse.Renderer;

/// <summary>
/// Render-only ocean pass shared by the game and Blowtorch. The ocean does not
/// create a physics body: it copies the already rendered scene, draws a
/// camera-following animated surface, and can apply a screen-space underwater
/// treatment with a waterline that follows the animated surface.
/// </summary>
public sealed unsafe class OceanRenderer : IDisposable
{
    private readonly GL _gl;
    private readonly Shader _surfaceShader;
    private readonly Shader _underwaterShader;
    private readonly ComputeShader _waveSimulationCompute;
    private readonly FullscreenQuad _quad;
    private Mesh? _mesh;

    private const int WaveSimulationResolution = 128;
    private const int WaveBand0TextureUnit = 10;
    private const int WaveBand1TextureUnit = 11;
    private const int WaveBand2TextureUnit = 12;
    private const float TwoPi = 6.28318530718f;
    private uint _waveBand0Texture;
    private uint _waveBand1Texture;
    private uint _waveBand2Texture;

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
    private bool _disposed;

    public bool IsValid => !_disposed &&
        _surfaceShader.IsValid && _underwaterShader.IsValid;

    private bool WaveSimulationValid =>
        !_disposed && _waveSimulationCompute.IsValid &&
        _waveBand0Texture != 0 && _waveBand1Texture != 0 && _waveBand2Texture != 0;

    public bool LastFrameUnderwater { get; private set; }

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
        _waveSimulationCompute = ComputeShader.FromFile(
            gl,
            Bible.Shader(Bible.OceanSimulationCompute));

        EnsureMesh(128);
        EnsureWaveSimulationTextures();
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
        if (_disposed || targetFbo == 0 || width <= 0 || height <= 0 ||
            !settings.Enabled || !IsValid)
            return false;

        if (!Matrix4x4.Invert(view * projection, out Matrix4x4 inverseViewProjection))
            return false;

        EnsureMesh(settings.GridResolution);
        EnsureWaveSimulationTextures();
        EnsureTargets(width, height);
        // The game supplies Engine.Time, which stops while the game is
        // paused. Blowtorch omits it and keeps its editor preview animated.
        float animationTime = simulationTimeSeconds ?? CurrentTimeSeconds();
        UpdateWaveSimulation(animationTime, settings);
        CopyScene(targetFbo, width, height);

        // Query the displaced surface at the camera instead of comparing the
        // camera with the mean water level. This keeps the underwater state in
        // sync with the same three wave bands that deform the visible mesh.
        float cameraWaterHeight = EvaluateCameraWaterHeight(
            cameraPosition,
            animationTime,
            settings);
        float cameraSubmersion = cameraWaterHeight - cameraPosition.Y;

        float oceanSize = MathF.Max(settings.OceanSize, 64.0f);
        float spacing = oceanSize / MathF.Max(settings.GridResolution, 1);
        Vector3 oceanOrigin = ComputeOceanOrigin(cameraPosition, spacing);

        // The fullscreen pass is useful while the camera is close to the
        // displaced surface as well as when it is fully submerged. The shader
        // computes the actual per-pixel waterline; this band only avoids an
        // unnecessary fullscreen pass for cameras that are clearly far above
        // the ocean.
        float maximumWaveHeight = MathF.Abs(settings.WaveAmplitude) * 1.96f;
        float waterlineBand = maximumWaveHeight + 0.25f;
        bool renderUnderwater = settings.UnderwaterEnabled &&
                                cameraSubmersion >= -waterlineBand;
        bool cameraBelowWater = settings.UnderwaterEnabled &&
                                 cameraSubmersion > 0.0f;

        // The visible surface writes its own depth/coverage/normal metadata
        // into the auxiliary texture during the same rasterization.
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
            // A camera close to the wave can see both sides of the displaced
            // surface in the same frame. Keep both faces available there.
            doubleSided: renderUnderwater);

        if (renderUnderwater)
        {
            // The underwater pass must include the water surface seen from
            // below and its updated depth. The fragment shader then decides
            // which pixels are actually in the water volume.
            CopyScene(targetFbo, width, height);
            RenderUnderwater(
                targetFbo,
                width,
                height,
                inverseViewProjection,
                cameraPosition,
                animationTime,
                sunDirection,
                sunColor,
                settings,
                cameraSubmersion,
                sceneIsSrgb,
                outputSrgb,
                targetHasMrt);
        }

        // Keep this state in sync with the displaced surface, not the mean
        // water level. Systems such as the cloud pass use it as a render hint.
        LastFrameUnderwater = cameraBelowWater;
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
        _surfaceShader.SetBool("uSceneIsSrgb", sceneIsSrgb);
        _surfaceShader.SetBool("uOutputSrgb", outputSrgb);

        _surfaceShader.SetInt("uSceneColor", 0);
        _surfaceShader.SetInt("uSceneDepth", 1);
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, _sceneCopyColor);
        _gl.ActiveTexture(TextureUnit.Texture1);
        _gl.BindTexture(TextureTarget.Texture2D, _sceneCopyDepth);

        BindWaveSimulationTextures();

        // The ocean fragment shader writes depth, coverage and normal to this
        // sidecar image. Because the shader uses early fragment tests, only
        // fragments that pass the same target depth test as the visible water
        // can publish metadata.
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
        shader.SetBool("uUseWaveTextures", WaveSimulationValid);
        shader.SetInt("uWaveBand0", WaveBand0TextureUnit);
        shader.SetInt("uWaveBand1", WaveBand1TextureUnit);
        shader.SetInt("uWaveBand2", WaveBand2TextureUnit);
        shader.SetFloat("uWaveBandWorldSize0", ComputeWaveBandWorldSize(settings, 0));
        shader.SetFloat("uWaveBandWorldSize1", ComputeWaveBandWorldSize(settings, 1));
        shader.SetFloat("uWaveBandWorldSize2", ComputeWaveBandWorldSize(settings, 2));
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
        _underwaterShader.SetFloat(
            "uCameraSubmersion",
            cameraSubmersion);
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
                    // Concentrate vertices around the camera while keeping a
                    // continuous grid, so no ring stitching or cracks are
                    // needed at the LOD boundaries.
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

    private void EnsureWaveSimulationTextures()
    {
        if (_waveBand0Texture != 0 && _waveBand1Texture != 0 && _waveBand2Texture != 0)
            return;

        _waveBand0Texture = CreateWaveTexture(WaveSimulationResolution);
        _waveBand1Texture = CreateWaveTexture(WaveSimulationResolution);
        _waveBand2Texture = CreateWaveTexture(WaveSimulationResolution);
    }

    private uint CreateWaveTexture(int size)
    {
        uint texture = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, texture);
        _gl.TexImage2D(
            TextureTarget.Texture2D,
            0,
            (int)InternalFormat.Rgba16f,
            (uint)size,
            (uint)size,
            0,
            PixelFormat.Rgba,
            PixelType.Float,
            null);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        // Every cascade is tileable. Repeat is important here because the
        // shader samples the texture using absolute world coordinates.
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.Repeat);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.Repeat);
        _gl.BindTexture(TextureTarget.Texture2D, 0);
        return texture;
    }

    private void UpdateWaveSimulation(float animationTime, OceanSettings settings)
    {
        if (!WaveSimulationValid)
            return;

        _waveSimulationCompute.Use();
        _waveSimulationCompute.SetInt("uSize", WaveSimulationResolution);
        _waveSimulationCompute.SetFloat("uTime", animationTime);
        _waveSimulationCompute.SetFloat("uAmplitude", settings.WaveAmplitude);
        _waveSimulationCompute.SetFloat("uWaveSpeed", settings.WaveSpeed);
        _waveSimulationCompute.SetFloat("uWaveChoppiness", settings.WaveChoppiness);
        _waveSimulationCompute.SetVec2("uWaveDirection", settings.WaveDirection);

        uint groups = (uint)((WaveSimulationResolution + 7) / 8);
        uint[] textures = [_waveBand0Texture, _waveBand1Texture, _waveBand2Texture];
        for (int band = 0; band < textures.Length; band++)
        {
            _waveSimulationCompute.SetInt("uBand", band);
            _gl.BindImageTexture(
                0,
                textures[band],
                0,
                false,
                0,
                GLEnum.WriteOnly,
                GLEnum.Rgba16f);
            _gl.DispatchCompute(groups, groups, 1);
            _gl.MemoryBarrier(MemoryBarrierMask.ShaderImageAccessBarrierBit | MemoryBarrierMask.TextureFetchBarrierBit);
        }

        _gl.BindImageTexture(0, 0, 0, false, 0, GLEnum.WriteOnly, GLEnum.Rgba16f);
        _gl.UseProgram(0);
    }

    private void BindWaveSimulationTextures()
    {
        _gl.ActiveTexture(TextureUnit.Texture0 + WaveBand0TextureUnit);
        _gl.BindTexture(TextureTarget.Texture2D, WaveSimulationValid ? _waveBand0Texture : 0);
        _gl.ActiveTexture(TextureUnit.Texture0 + WaveBand1TextureUnit);
        _gl.BindTexture(TextureTarget.Texture2D, WaveSimulationValid ? _waveBand1Texture : 0);
        _gl.ActiveTexture(TextureUnit.Texture0 + WaveBand2TextureUnit);
        _gl.BindTexture(TextureTarget.Texture2D, WaveSimulationValid ? _waveBand2Texture : 0);
        _gl.ActiveTexture(TextureUnit.Texture0);
    }

    private static float ComputeWaveBandWorldSize(OceanSettings settings, int band)
    {
        float baseLength = MathF.Max(settings.WaveLength, 1.0f);
        return band switch
        {
            0 => MathF.Max(baseLength * 24.0f, 256.0f),
            1 => MathF.Max(baseLength * 8.0f, 96.0f),
            _ => MathF.Max(baseLength * 2.5f, 32.0f)
        };
    }

    // This is the CPU-side counterpart of ocean_simulation.comp/ocean.vert.
    // It is intentionally kept here, beside the renderer, so the underwater
    // state cannot drift to a second, unrelated wave equation.
    private float EvaluateCameraWaterHeight(
        Vector3 cameraPosition,
        float animationTime,
        OceanSettings settings)
    {
        Vector2 targetWorldPosition = new(cameraPosition.X, cameraPosition.Z);
        Vector3 displacement = Vector3.Zero;

        // The vertex shader displaces the horizontal position as well as the
        // height. Solve that same mapping backwards a few times to find the
        // wave sample directly below the camera.
        for (int i = 0; i < 4; i++)
        {
            displacement = WaveSimulationValid
                ? EvaluateSpectralWaveDisplacement(targetWorldPosition, animationTime, settings)
                : EvaluateFallbackWaveDisplacement(targetWorldPosition, animationTime, settings);
            targetWorldPosition = new Vector2(
                cameraPosition.X - displacement.X,
                cameraPosition.Z - displacement.Z);
        }

        return settings.WaterLevel + displacement.Y;
    }

    private static Vector3 EvaluateSpectralWaveDisplacement(
        Vector2 worldPosition,
        float animationTime,
        OceanSettings settings)
    {
        Vector2 forward = settings.WaveDirection.LengthSquared() > 0.001f
            ? Vector2.Normalize(settings.WaveDirection)
            : Vector2.UnitX;
        Vector2 side = new(-forward.Y, forward.X);
        Vector3 displacement = Vector3.Zero;

        for (int band = 0; band < 3; band++)
        {
            float bandWorldSize = ComputeWaveBandWorldSize(settings, band);
            Vector2 coordinate = new(
                PositiveModulo(worldPosition.X, bandWorldSize) / bandWorldSize,
                PositiveModulo(worldPosition.Y, bandWorldSize) / bandWorldSize);
            float bandAmplitude = MathF.Max(settings.WaveAmplitude, 0.0f) *
                                  GetBandAmplitude(band);

            for (int i = 0; i < 4; i++)
            {
                Vector2 directionBase = GetBandDirection(i);
                Vector2 direction = Vector2.Normalize(
                    forward * directionBase.X + side * directionBase.Y);
                Vector2 frequency = GetBandFrequency(band, i);
                float phase = TwoPi * Vector2.Dot(coordinate, frequency) -
                    animationTime * settings.WaveSpeed * GetBandSpeed(band) +
                    GetPhaseOffset(band, i);
                float sine = MathF.Sin(phase);
                float cosine = MathF.Cos(phase);
                float componentAmplitude = bandAmplitude * GetComponentWeight(i);

                displacement.Y += componentAmplitude *
                    (sine + 0.075f * MathF.Sin(phase * 2.0f + 0.31f));
                float horizontalAmount = componentAmplitude *
                    settings.WaveChoppiness * (0.82f + 0.12f * i) * cosine;
                displacement.X += direction.X * horizontalAmount;
                displacement.Z += direction.Y * horizontalAmount;
            }
        }

        return displacement;
    }

    private static Vector3 EvaluateFallbackWaveDisplacement(
        Vector2 worldPosition,
        float animationTime,
        OceanSettings settings)
    {
        Vector2 forward = settings.WaveDirection.LengthSquared() > 0.001f
            ? Vector2.Normalize(settings.WaveDirection)
            : Vector2.UnitX;
        Vector2 side = new(-forward.Y, forward.X);
        Vector3 displacement = Vector3.Zero;

        Vector2[] directions =
        [
            new(1.00f, 0.06f), new(-0.74f, 0.67f), new(0.38f, -0.93f), new(0.86f, -0.51f),
            new(0.97f, -0.23f), new(-0.48f, 0.88f), new(0.15f, -1.00f), new(0.72f, 0.69f),
            new(1.00f, 0.31f), new(-0.83f, 0.55f), new(0.52f, -0.86f), new(-0.18f, -0.99f)
        ];
        float[] amplitudes =
        [
            0.42f, 0.18f, 0.09f, 0.035f,
            0.14f, 0.08f, 0.045f, 0.025f,
            0.055f, 0.032f, 0.018f, 0.010f
        ];
        float[] lengths =
        [
            24.0f, 14.0f, 8.0f, 4.5f,
            12.0f, 7.0f, 4.0f, 2.6f,
            3.8f, 2.4f, 1.5f, 0.9f
        ];
        float[] speeds =
        [
            0.42f, 0.48f, 0.56f, 0.64f,
            0.78f, 0.86f, 0.95f, 1.04f,
            1.26f, 1.34f, 1.47f, 1.61f
        ];
        float[] phases =
        [
            0.17f, 2.41f, 4.88f, 1.33f,
            1.79f, 4.20f, 0.62f, 3.51f,
            3.14f, 0.91f, 5.37f, 2.28f
        ];

        for (int i = 0; i < directions.Length; i++)
        {
            Vector2 direction = Vector2.Normalize(
                forward * directions[i].X + side * directions[i].Y);
            float waveNumber = TwoPi / MathF.Max(
                settings.WaveLength * lengths[i] / 24.0f,
                0.45f);
            float phase = waveNumber * Vector2.Dot(direction, worldPosition) -
                animationTime * settings.WaveSpeed * speeds[i] + phases[i];
            float componentAmplitude = settings.WaveAmplitude * amplitudes[i];
            float cosine = MathF.Cos(phase);

            displacement.X += direction.X * componentAmplitude *
                settings.WaveChoppiness * cosine;
            displacement.Y += componentAmplitude *
                (MathF.Sin(phase) + 0.075f * MathF.Sin(phase * 2.0f + 0.31f));
            displacement.Z += direction.Y * componentAmplitude *
                settings.WaveChoppiness * cosine;
        }

        return displacement;
    }

    private static float PositiveModulo(float value, float modulus)
    {
        float remainder = value % modulus;
        return remainder < 0.0f ? remainder + modulus : remainder;
    }

    private static Vector2 GetBandDirection(int index) => index switch
    {
        0 => new(1.00f, 0.06f),
        1 => new(-0.74f, 0.67f),
        2 => new(0.38f, -0.93f),
        _ => new(0.86f, -0.51f)
    };

    private static Vector2 GetBandFrequency(int band, int index) => band switch
    {
        0 => index switch
        {
            0 => new(1.0f, 0.0f),
            1 => new(1.0f, 1.0f),
            2 => new(2.0f, -1.0f),
            _ => new(1.0f, 2.0f)
        },
        1 => index switch
        {
            0 => new(1.0f, 0.0f),
            1 => new(2.0f, 1.0f),
            2 => new(3.0f, -2.0f),
            _ => new(4.0f, 1.0f)
        },
        _ => index switch
        {
            0 => new(1.0f, 1.0f),
            1 => new(2.0f, -1.0f),
            2 => new(3.0f, 2.0f),
            _ => new(5.0f, -3.0f)
        }
    };

    private static float GetComponentWeight(int index) => index switch
    {
        0 => 0.58f,
        1 => 0.25f,
        2 => 0.12f,
        _ => 0.05f
    };

    private static float GetBandAmplitude(int band) => band switch
    {
        0 => 0.72f,
        1 => 0.24f,
        _ => 0.10f
    };

    private static float GetBandSpeed(int band) => band switch
    {
        0 => 0.42f,
        1 => 0.78f,
        _ => 1.26f
    };

    private static float GetPhaseOffset(int band, int index) => band switch
    {
        0 => index switch
        {
            0 => 0.17f,
            1 => 2.41f,
            2 => 4.88f,
            _ => 1.33f
        },
        1 => index switch
        {
            0 => 1.79f,
            1 => 4.20f,
            2 => 0.62f,
            _ => 3.51f
        },
        _ => index switch
        {
            0 => 3.14f,
            1 => 0.91f,
            2 => 5.37f,
            _ => 2.28f
        }
    };

    private static float AdaptiveCoordinate(float normalized)
    {
        float centered = normalized * 2.0f - 1.0f;
        float sign = centered < 0.0f ? -1.0f : 1.0f;
        // A power curve puts most samples near the camera and progressively
        // widens the cells toward the horizon.
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
            format == InternalFormat.DepthComponent32f
                ? PixelType.Float
                : PixelType.Float,
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

    private void DeleteWaveSimulationTextures()
    {
        if (_waveBand0Texture != 0) _gl.DeleteTexture(_waveBand0Texture);
        if (_waveBand1Texture != 0) _gl.DeleteTexture(_waveBand1Texture);
        if (_waveBand2Texture != 0) _gl.DeleteTexture(_waveBand2Texture);
        _waveBand0Texture = 0;
        _waveBand1Texture = 0;
        _waveBand2Texture = 0;
    }

    private void CheckFramebuffer(string name)
    {
        GLEnum status = _gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        if (status != GLEnum.FramebufferComplete)
            throw new InvalidOperationException($"Ocean framebuffer incomplete during {name}: {status}");
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
        DeleteWaveSimulationTextures();
        _mesh?.Dispose();
        _surfaceShader.Dispose();
        _underwaterShader.Dispose();
        _waveSimulationCompute.Dispose();
        _quad.Dispose();
        _mesh = null;
    }
}
