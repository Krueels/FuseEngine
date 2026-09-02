using System.Numerics;
using Fuse.Renderer;
using Fuse.Scene.Model;
using Silk.NET.OpenGL;
using Shader = Fuse.Renderer.Shader;

namespace Blowtorch;

/// <summary>
/// Lighting and shadow resources used by a Blowtorch viewport. The editor keeps
/// its own UBO because it runs without MasterRenderer, but consumes the same
/// LightingBlock and shadow samplers as the game shaders.
/// </summary>
public sealed class EditorLightingSystem : IDisposable
{
    private const int CascadeCount = 3;
    private const int MaxShadowedPointLights = 4;
    private const uint ShadowResolution = 512;
    private const float ShadowNearPlane = 0.1f;
    private const float ShadowFarPlane = 150.0f;
    private const float ShadowBiasBase = 0.00001f;
    private const float ShadowBiasFactor = 0.0005f;
    private const float ShadowSpread = 1.0f;
    private const float CascadeSplitLambda = 0.70f;
    private const float CascadeBlendFraction = 0.10f;
    private const float ShadowFadeFraction = 0.12f;
    private const float ShadowDepthPadding = 3.0f;

    private readonly GL _gl;
    private readonly LightingBuffer _lightingBuffer;
    private readonly bool _supportsShadows;
    private ImageBasedLighting? _imageBasedLighting;
    private readonly ShadowMap? _directionalShadowMap;
    private readonly ShadowMap? _spotShadowMap;
    private readonly PointShadowMap[] _pointShadowMaps = [];

    private readonly Light[] _pointLights = new Light[LightingBuffer.MaxPointLights];
    private readonly Light[] _spotLights = new Light[LightingBuffer.MaxSpotLights];
    private readonly Light[] _shadowPointLights = new Light[MaxShadowedPointLights];
    private readonly Matrix4x4[] _lightSpaceMatrices = new Matrix4x4[CascadeCount];
    private readonly Matrix4x4[] _spotSpaceMatrices = new Matrix4x4[LightingBuffer.MaxSpotLights];
    private readonly float[] _cascadeLevels = new float[CascadeCount];
    private readonly float[] _cascadeTexelSizes = new float[CascadeCount];

    private readonly LayerShadowCache[] _directionalCache = new LayerShadowCache[CascadeCount];
    private readonly LayerShadowCache[] _spotCache = new LayerShadowCache[LightingBuffer.MaxSpotLights];
    private readonly PointShadowCache[] _pointCache = new PointShadowCache[MaxShadowedPointLights];
    private Scene? _cachedScene;

    private static readonly Vector3[] PointShadowTargets =
    [
        new(1, 0, 0), new(-1, 0, 0), new(0, 1, 0),
        new(0, -1, 0), new(0, 0, 1), new(0, 0, -1)
    ];

    private static readonly Vector3[] PointShadowUps =
    [
        new(0, -1, 0), new(0, -1, 0), new(0, 0, 1),
        new(0, 0, -1), new(0, -1, 0), new(0, -1, 0)
    ];

    private struct LayerShadowCache
    {
        public Light? Light;
        public ulong SceneRevision;
        public ulong DynamicSceneRevision;
        public Matrix4x4 Matrix;
        public bool Valid;
    }

    private struct PointShadowCache
    {
        public Light? Light;
        public ulong SceneRevision;
        public ulong DynamicSceneRevision;
        public Vector3 Position;
        public float Radius;
        public bool Valid;
    }

    public EditorLightingSystem(GL gl, bool supportsShadows, ImageBasedLighting? imageBasedLighting = null)
    {
        _gl = gl;
        _supportsShadows = supportsShadows;
        _imageBasedLighting = imageBasedLighting;
        _lightingBuffer = new LightingBuffer(gl);

        if (!supportsShadows)
            return;

        _directionalShadowMap = new ShadowMap(gl, ShadowResolution * 2, ShadowResolution * 2, CascadeCount);
        _spotShadowMap = new ShadowMap(gl, ShadowResolution, ShadowResolution, LightingBuffer.MaxSpotLights);
        _pointShadowMaps = new PointShadowMap[MaxShadowedPointLights];
        for (int i = 0; i < _pointShadowMaps.Length; i++)
            _pointShadowMaps[i] = new PointShadowMap(gl, ShadowResolution);
    }

    public void SetImageBasedLighting(ImageBasedLighting? imageBasedLighting)
    {
        _imageBasedLighting = imageBasedLighting;
    }

    public ShadowMap? DirectionalShadowMap => _directionalShadowMap;

    public void Prepare(
        Scene scene,
        ViewportCamera camera,
        float aspect,
        bool shadowsEnabled,
        bool unlit,
        Shader shadowShader,
        Shader pointShadowShader,
        SkyboxSettings? skyboxSettings = null)
    {
        Array.Clear(_lightSpaceMatrices);
        Array.Clear(_spotSpaceMatrices);
        Array.Clear(_cascadeTexelSizes);

        if (unlit)
        {
            UploadUnlit(camera.Position);
            return;
        }

        Light? directionalLight = null;
        for (int i = 0; i < scene.Lights.Count; i++)
        {
            Light light = scene.Lights[i];
            if (light.Enabled && light.Type == LightType.Directional)
            {
                directionalLight = light;
                break;
            }
        }

        Vector3 lightDirection = directionalLight != null && directionalLight.Direction.LengthSquared() > 1e-8f
            ? -Vector3.Normalize(directionalLight.Direction)
            : ProceduralSky.FallbackSunDirection;

        int pointCount = SelectLights(scene.Lights, LightType.Point, _pointLights,
            LightingBuffer.MaxPointLights, camera.Position);
        int spotCount = SelectLights(scene.Lights, LightType.Spot, _spotLights,
            LightingBuffer.MaxSpotLights, camera.Position);
        int shadowPointCount = SelectShadowedPointLights(
            _pointLights.AsSpan(0, pointCount), _shadowPointLights, camera.Position);

        CalculateCascadeSplits(ShadowNearPlane, ShadowFarPlane, _cascadeLevels);

        bool canRenderShadows = _supportsShadows && shadowsEnabled &&
                                shadowShader.ID != 0 && pointShadowShader.ID != 0;
        bool directionalShadows = canRenderShadows &&
                                  directionalLight is { CastShadows: true };

        if (_supportsShadows)
        {
            scene.PrepareShadowCasters();
            if (!ReferenceEquals(_cachedScene, scene))
            {
                InvalidateCaches();
                _cachedScene = scene;
            }
        }

        if (directionalShadows)
            RenderDirectionalShadows(scene, camera, aspect, lightDirection, shadowShader);

        if (canRenderShadows)
        {
            RenderSpotShadows(scene, spotCount, shadowShader);
            RenderPointShadows(scene, shadowPointCount, pointShadowShader);
        }
        else
        {
            CalculateSpotMatrices(spotCount);
        }

        Vector3 directionalColor = directionalLight != null
            ? directionalLight.Color * MathF.Max(directionalLight.Intensity, 0.0f)
            : Vector3.One;
        float ambient = 0.20f;
        if (skyboxSettings?.Mode == SkyboxMode.Procedural)
        {
            Vector3 skyColor = ProceduralSky.EstimateAmbientColor(
                skyboxSettings,
                lightDirection);
            float skyLuminance = skyColor.X * 0.2126f +
                                 skyColor.Y * 0.7152f +
                                 skyColor.Z * 0.0722f;
            ambient = 0.02f + 0.28f * skyLuminance;
        }
        float fadeStart = ShadowFarPlane * (1.0f - ShadowFadeFraction);

        _lightingBuffer.Upload(
            camera.Position,
            lightDirection,
            directionalColor,
            ambient,
            directionalShadows,
            canRenderShadows,
            true,
            ShadowBiasBase,
            ShadowBiasFactor,
            ShadowSpread,
            ShadowFarPlane,
            CascadeBlendFraction,
            fadeStart,
            _cascadeLevels,
            _cascadeTexelSizes,
            _lightSpaceMatrices,
            _pointLights.AsSpan(0, pointCount),
            _shadowPointLights.AsSpan(0, shadowPointCount),
            _spotLights.AsSpan(0, spotCount),
            _spotSpaceMatrices.AsSpan(0, spotCount));
    }

    public void BindShadowMaps(Shader shader)
    {
        shader.SetInt("uShadowMap", 1);
        shader.SetInt("uSpotShadowMap", 2);
        shader.SetInt("uPointShadowMap0", 3);
        shader.SetInt("uPointShadowMap1", 4);
        shader.SetInt("uPointShadowMap2", 5);
        shader.SetInt("uPointShadowMap3", 6);

        if (!_supportsShadows || _directionalShadowMap == null || _spotShadowMap == null)
            return;

        _directionalShadowMap.BindForReading(TextureUnit.Texture1);
        _spotShadowMap.BindForReading(TextureUnit.Texture2);
        for (int i = 0; i < _pointShadowMaps.Length; i++)
            _pointShadowMaps[i].BindForReading(TextureUnit.Texture3 + i);
    }

    public void BindImageBasedLighting(Shader shader)
    {
        if (_imageBasedLighting != null)
            _imageBasedLighting.Bind(shader);
        else
        {
            shader.SetBool("uUseIbl", false);
            shader.SetFloat("uIblIntensity", 1.0f);
        }
    }

    private void UploadUnlit(Vector3 cameraPosition)
    {
        _lightingBuffer.Upload(
            cameraPosition,
            Vector3.UnitY,
            Vector3.One,
            1.0f,
            false,
            false,
            false,
            0.0f,
            0.0f,
            1.0f,
            ShadowFarPlane,
            0.0f,
            ShadowFarPlane,
            _cascadeLevels,
            _cascadeTexelSizes,
            _lightSpaceMatrices,
            ReadOnlySpan<Light>.Empty,
            ReadOnlySpan<Light>.Empty,
            ReadOnlySpan<Light>.Empty,
            ReadOnlySpan<Matrix4x4>.Empty);
    }

    private void RenderDirectionalShadows(
        Scene scene,
        ViewportCamera camera,
        float aspect,
        Vector3 lightDirection,
        Shader shadowShader)
    {
        PrepareShadowRenderState();

        for (int cascade = 0; cascade < CascadeCount; cascade++)
        {
            float logicalNear = cascade == 0 ? ShadowNearPlane : _cascadeLevels[cascade - 1];
            float far = _cascadeLevels[cascade];
            float near = logicalNear;
            if (cascade > 0)
            {
                float previousNear = cascade == 1 ? ShadowNearPlane : _cascadeLevels[cascade - 2];
                float overlap = (logicalNear - previousNear) * CascadeBlendFraction;
                near = MathF.Max(ShadowNearPlane, logicalNear - overlap);
            }

            Matrix4x4 matrix = GetLightSpaceMatrix(
                scene, camera.ViewMatrix, aspect, near, far, lightDirection, out float texelWorldSize);
            _lightSpaceMatrices[cascade] = matrix;
            _cascadeTexelSizes[cascade] = texelWorldSize;

            ref LayerShadowCache cache = ref _directionalCache[cascade];
            bool cacheDirty = !cache.Valid ||
                              cache.SceneRevision != scene.StaticShadowRevision ||
                              cache.DynamicSceneRevision != scene.DynamicShadowRevision ||
                              !MatrixApproximatelyEqual(cache.Matrix, matrix);
            if (!cacheDirty)
                continue;

            shadowShader.Use();
            shadowShader.SetMat4("uLightSpaceMatrix", matrix);
            _directionalShadowMap!.BindForWriting(cascade);
            scene.RenderShadowCasters(shadowShader, matrix, ShadowCasterFilter.Static);
            scene.RenderShadowCasters(shadowShader, matrix, ShadowCasterFilter.Dynamic);

            cache.SceneRevision = scene.StaticShadowRevision;
            cache.DynamicSceneRevision = scene.DynamicShadowRevision;
            cache.Matrix = matrix;
            cache.Valid = true;
        }
    }

    private void RenderSpotShadows(Scene scene, int spotCount, Shader shadowShader)
    {
        PrepareShadowRenderState();

        for (int slot = 0; slot < spotCount; slot++)
        {
            Light light = _spotLights[slot];
            Matrix4x4 matrix = CreateSpotShadowMatrix(light);
            _spotSpaceMatrices[slot] = matrix;

            if (!light.CastShadows)
                continue;

            ref LayerShadowCache cache = ref _spotCache[slot];
            bool cacheDirty = !cache.Valid ||
                              !ReferenceEquals(cache.Light, light) ||
                              cache.SceneRevision != scene.StaticShadowRevision ||
                              cache.DynamicSceneRevision != scene.DynamicShadowRevision ||
                              !MatrixApproximatelyEqual(cache.Matrix, matrix);
            if (!cacheDirty)
                continue;

            shadowShader.Use();
            shadowShader.SetMat4("uLightSpaceMatrix", matrix);
            _spotShadowMap!.BindForWriting(slot);
            scene.RenderShadowCasters(shadowShader, matrix, ShadowCasterFilter.Static);
            scene.RenderShadowCasters(shadowShader, matrix, ShadowCasterFilter.Dynamic);

            cache.Light = light;
            cache.SceneRevision = scene.StaticShadowRevision;
            cache.DynamicSceneRevision = scene.DynamicShadowRevision;
            cache.Matrix = matrix;
            cache.Valid = true;
        }
    }

    private void RenderPointShadows(Scene scene, int shadowPointCount, Shader pointShadowShader)
    {
        PrepareShadowRenderState();

        for (int slot = 0; slot < shadowPointCount; slot++)
        {
            Light light = _shadowPointLights[slot];
            float farPlane = MathF.Max(light.Radius, 0.11f);
            float nearPlane = MathF.Min(MathF.Max(farPlane * 0.01f, 0.05f), farPlane * 0.25f);
            Matrix4x4 projection = Matrix4x4.CreatePerspectiveFieldOfView(
                MathF.PI * 0.5f, 1.0f, nearPlane, farPlane);

            ref PointShadowCache cache = ref _pointCache[slot];
            bool cacheDirty = !cache.Valid ||
                              !ReferenceEquals(cache.Light, light) ||
                              cache.SceneRevision != scene.StaticShadowRevision ||
                              cache.DynamicSceneRevision != scene.DynamicShadowRevision ||
                              Vector3.DistanceSquared(cache.Position, light.Position) > 1e-8f ||
                              MathF.Abs(cache.Radius - light.Radius) > 1e-5f;
            if (!cacheDirty)
                continue;

            pointShadowShader.Use();
            pointShadowShader.SetVec3("uLightPos", light.Position);
            pointShadowShader.SetFloat("uRadius", farPlane);

            for (int face = 0; face < 6; face++)
            {
                Matrix4x4 matrix = CreatePointShadowMatrix(light.Position, projection, face);
                pointShadowShader.SetMat4("uLightSpaceMatrix", matrix);
                _pointShadowMaps[slot].BindForWriting(face);
                scene.RenderShadowCasters(pointShadowShader, matrix, ShadowCasterFilter.Static);
                scene.RenderShadowCasters(pointShadowShader, matrix, ShadowCasterFilter.Dynamic);
            }

            cache.Light = light;
            cache.SceneRevision = scene.StaticShadowRevision;
            cache.DynamicSceneRevision = scene.DynamicShadowRevision;
            cache.Position = light.Position;
            cache.Radius = light.Radius;
            cache.Valid = true;
        }
    }

    private void CalculateSpotMatrices(int spotCount)
    {
        for (int i = 0; i < spotCount; i++)
            _spotSpaceMatrices[i] = CreateSpotShadowMatrix(_spotLights[i]);
    }

    private static Matrix4x4 CreateSpotShadowMatrix(Light light)
    {
        Vector3 direction = light.Direction.LengthSquared() > 1e-8f
            ? Vector3.Normalize(light.Direction)
            : -Vector3.UnitY;
        Vector3 up = MathF.Abs(Vector3.Dot(direction, Vector3.UnitY)) > 0.999f
            ? Vector3.UnitZ
            : Vector3.UnitY;
        Matrix4x4 view = Matrix4x4.CreateLookAt(light.Position, light.Position + direction, up);
        float farPlane = MathF.Max(light.Radius, 0.11f);
        float nearPlane = MathF.Min(MathF.Max(farPlane * 0.01f, 0.05f), farPlane * 0.25f);
        float fieldOfView = float.Clamp(light.OuterConeAngle * 2.0f, 0.02f, MathF.PI - 0.02f);
        Matrix4x4 projection = Matrix4x4.CreatePerspectiveFieldOfView(fieldOfView, 1.0f, nearPlane, farPlane);
        return view * projection;
    }

    private static Matrix4x4 CreatePointShadowMatrix(Vector3 position, Matrix4x4 projection, int face)
    {
        Matrix4x4 view = Matrix4x4.CreateLookAt(
            position,
            position + PointShadowTargets[face],
            PointShadowUps[face]);
        return view * projection;
    }

    private void PrepareShadowRenderState()
    {
        _gl.Enable(EnableCap.DepthTest);
        _gl.Enable(EnableCap.CullFace);
        _gl.CullFace(GLEnum.Back);
        _gl.DepthMask(true);
        _gl.DepthFunc(DepthFunction.Less);
    }

    private static int SelectLights(
        IReadOnlyList<Light> lights,
        LightType type,
        Light[] destination,
        int max,
        Vector3 cameraPosition)
    {
        int count = 0;
        for (int i = 0; i < lights.Count; i++)
        {
            Light light = lights[i];
            if (!light.Enabled || light.Type != type)
                continue;

            float influence = CalculateVisualInfluence(light, cameraPosition);
            int insertion = Math.Min(count, max);
            while (insertion > 0 &&
                   influence > CalculateVisualInfluence(destination[insertion - 1], cameraPosition))
            {
                if (insertion < max)
                    destination[insertion] = destination[insertion - 1];
                insertion--;
            }
            if (insertion < max)
                destination[insertion] = light;
            if (count < max)
                count++;
        }

        for (int i = count; i < destination.Length; i++)
            destination[i] = null!;
        return count;
    }

    private static int SelectShadowedPointLights(
        ReadOnlySpan<Light> selectedPointLights,
        Light[] destination,
        Vector3 cameraPosition)
    {
        int count = 0;
        for (int i = 0; i < selectedPointLights.Length; i++)
        {
            Light light = selectedPointLights[i];
            if (!light.CastShadows)
                continue;

            float influence = CalculateVisualInfluence(light, cameraPosition);
            int insertion = Math.Min(count, destination.Length);
            while (insertion > 0 &&
                   influence > CalculateVisualInfluence(destination[insertion - 1], cameraPosition))
            {
                if (insertion < destination.Length)
                    destination[insertion] = destination[insertion - 1];
                insertion--;
            }
            if (insertion < destination.Length)
                destination[insertion] = light;
            if (count < destination.Length)
                count++;
        }

        for (int i = count; i < destination.Length; i++)
            destination[i] = null!;
        return count;
    }

    private static float CalculateVisualInfluence(Light light, Vector3 cameraPosition)
    {
        float distanceSquared = MathF.Max(Vector3.DistanceSquared(light.Position, cameraPosition), 0.25f);
        float radiusSquared = MathF.Max(light.Radius * light.Radius, 0.01f);
        float spatialInfluence = radiusSquared /
                                 MathF.Max(distanceSquared, radiusSquared * 0.0625f);
        float luminance = light.Color.X * 0.2126f +
                          light.Color.Y * 0.7152f +
                          light.Color.Z * 0.0722f;
        return MathF.Max(light.Intensity * MathF.Max(luminance, 0.001f), 0.001f) * spatialInfluence;
    }

    private static void CalculateCascadeSplits(float nearPlane, float farPlane, float[] destination)
    {
        nearPlane = MathF.Max(nearPlane, 0.001f);
        farPlane = MathF.Max(farPlane, nearPlane + 0.001f);
        for (int cascade = 1; cascade <= CascadeCount; cascade++)
        {
            float ratio = (float)cascade / CascadeCount;
            float logarithmic = nearPlane * MathF.Pow(farPlane / nearPlane, ratio);
            float uniform = nearPlane + (farPlane - nearPlane) * ratio;
            destination[cascade - 1] = uniform + (logarithmic - uniform) * CascadeSplitLambda;
        }
        destination[CascadeCount - 1] = farPlane;
    }

    private static void GetFrustumCornersWorldSpace(
        Matrix4x4 projection,
        Matrix4x4 view,
        Span<Vector3> destination)
    {
        Matrix4x4.Invert(view * projection, out Matrix4x4 inverseViewProjection);
        int index = 0;
        for (int x = 0; x < 2; x++)
        {
            for (int y = 0; y < 2; y++)
            {
                for (int z = 0; z < 2; z++)
                {
                    Vector4 point = Vector4.Transform(new Vector4(
                        2.0f * x - 1.0f,
                        2.0f * y - 1.0f,
                        z,
                        1.0f), inverseViewProjection);
                    point /= point.W;
                    destination[index++] = new Vector3(point.X, point.Y, point.Z);
                }
            }
        }
    }

    private Matrix4x4 GetLightSpaceMatrix(
        Scene scene,
        Matrix4x4 cameraView,
        float aspect,
        float near,
        float far,
        Vector3 lightDirection,
        out float texelWorldSize)
    {
        Matrix4x4 projection = Matrix4x4.CreatePerspectiveFieldOfView(
            float.DegreesToRadians(45.0f), aspect, near, far);
        Span<Vector3> frustumCorners = stackalloc Vector3[8];
        GetFrustumCornersWorldSpace(projection, cameraView, frustumCorners);

        Vector3 center = Vector3.Zero;
        for (int i = 0; i < frustumCorners.Length; i++)
            center += frustumCorners[i];
        center /= frustumCorners.Length;

        float radius = 0.0f;
        for (int i = 0; i < frustumCorners.Length; i++)
            radius = MathF.Max(radius, Vector3.Distance(center, frustumCorners[i]));
        radius = MathF.Ceiling(radius * 16.0f) / 16.0f;

        Vector3 up = MathF.Abs(Vector3.Dot(lightDirection, Vector3.UnitY)) > 0.999f
            ? Vector3.UnitZ
            : Vector3.UnitY;
        Matrix4x4 baseView = Matrix4x4.CreateLookAt(Vector3.Zero, -lightDirection, up);
        Vector3 centerLightSpace = Vector3.Transform(center, baseView);

        texelWorldSize = radius * 2.0f / _directionalShadowMap!.Width;
        centerLightSpace.X = MathF.Floor(centerLightSpace.X / texelWorldSize) * texelWorldSize;
        centerLightSpace.Y = MathF.Floor(centerLightSpace.Y / texelWorldSize) * texelWorldSize;

        float minZ = float.PositiveInfinity;
        float maxZ = float.NegativeInfinity;
        for (int i = 0; i < frustumCorners.Length; i++)
        {
            float z = Vector3.Transform(frustumCorners[i], baseView).Z;
            minZ = MathF.Min(minZ, z);
            maxZ = MathF.Max(maxZ, z);
        }

        AccumulateCasterDepthRange(scene.StaticShadowCasters, baseView, centerLightSpace, radius, ref minZ, ref maxZ);
        AccumulateCasterDepthRange(scene.DynamicShadowCasters, baseView, centerLightSpace, radius, ref minZ, ref maxZ);

        float depthPadding = MathF.Max(ShadowDepthPadding, radius * 0.08f);
        minZ = MathF.Floor((minZ - depthPadding) * 4.0f) * 0.25f;
        maxZ = MathF.Ceiling((maxZ + depthPadding) * 4.0f) * 0.25f;

        Matrix4x4.Invert(baseView, out Matrix4x4 inverseBaseView);
        Vector3 eye = Vector3.Transform(
            new Vector3(centerLightSpace.X, centerLightSpace.Y, maxZ), inverseBaseView);
        Vector3 target = Vector3.Transform(
            new Vector3(centerLightSpace.X, centerLightSpace.Y, maxZ - 1.0f), inverseBaseView);

        Matrix4x4 lightView = Matrix4x4.CreateLookAt(eye, target, up);
        float farDistance = MathF.Max(ShadowNearPlane + 0.01f, maxZ - minZ);
        Matrix4x4 lightProjection = Matrix4x4.CreateOrthographicOffCenter(
            -radius, radius, -radius, radius, ShadowNearPlane, farDistance);
        return lightView * lightProjection;
    }

    private static void AccumulateCasterDepthRange(
        IReadOnlyList<Entity> casters,
        Matrix4x4 baseView,
        Vector3 centerLightSpace,
        float radius,
        ref float minZ,
        ref float maxZ)
    {
        Span<Vector3> corners = stackalloc Vector3[8];
        for (int casterIndex = 0; casterIndex < casters.Count; casterIndex++)
        {
            Fuse.Math.AABB bounds = casters[casterIndex].GetWorldRenderBounds();
            if (!bounds.IsValid)
                continue;
            bounds.GetCorners(corners);

            float casterMinX = float.PositiveInfinity;
            float casterMaxX = float.NegativeInfinity;
            float casterMinY = float.PositiveInfinity;
            float casterMaxY = float.NegativeInfinity;
            float casterMinZ = float.PositiveInfinity;
            float casterMaxZ = float.NegativeInfinity;

            for (int cornerIndex = 0; cornerIndex < corners.Length; cornerIndex++)
            {
                Vector3 lightSpace = Vector3.Transform(corners[cornerIndex], baseView);
                casterMinX = MathF.Min(casterMinX, lightSpace.X);
                casterMaxX = MathF.Max(casterMaxX, lightSpace.X);
                casterMinY = MathF.Min(casterMinY, lightSpace.Y);
                casterMaxY = MathF.Max(casterMaxY, lightSpace.Y);
                casterMinZ = MathF.Min(casterMinZ, lightSpace.Z);
                casterMaxZ = MathF.Max(casterMaxZ, lightSpace.Z);
            }

            if (casterMaxX < centerLightSpace.X - radius || casterMinX > centerLightSpace.X + radius ||
                casterMaxY < centerLightSpace.Y - radius || casterMinY > centerLightSpace.Y + radius)
                continue;

            minZ = MathF.Min(minZ, casterMinZ);
            maxZ = MathF.Max(maxZ, casterMaxZ);
        }
    }

    private void InvalidateCaches()
    {
        Array.Clear(_directionalCache);
        Array.Clear(_spotCache);
        Array.Clear(_pointCache);
    }

    private static bool MatrixApproximatelyEqual(Matrix4x4 left, Matrix4x4 right)
    {
        const float epsilon = 1e-5f;
        return MathF.Abs(left.M11 - right.M11) < epsilon && MathF.Abs(left.M12 - right.M12) < epsilon &&
               MathF.Abs(left.M13 - right.M13) < epsilon && MathF.Abs(left.M14 - right.M14) < epsilon &&
               MathF.Abs(left.M21 - right.M21) < epsilon && MathF.Abs(left.M22 - right.M22) < epsilon &&
               MathF.Abs(left.M23 - right.M23) < epsilon && MathF.Abs(left.M24 - right.M24) < epsilon &&
               MathF.Abs(left.M31 - right.M31) < epsilon && MathF.Abs(left.M32 - right.M32) < epsilon &&
               MathF.Abs(left.M33 - right.M33) < epsilon && MathF.Abs(left.M34 - right.M34) < epsilon &&
               MathF.Abs(left.M41 - right.M41) < epsilon && MathF.Abs(left.M42 - right.M42) < epsilon &&
               MathF.Abs(left.M43 - right.M43) < epsilon && MathF.Abs(left.M44 - right.M44) < epsilon;
    }

    public void Dispose()
    {
        _lightingBuffer.Dispose();
        _directionalShadowMap?.Dispose();
        _spotShadowMap?.Dispose();
        for (int i = 0; i < _pointShadowMaps.Length; i++)
            _pointShadowMaps[i].Dispose();
    }
}
