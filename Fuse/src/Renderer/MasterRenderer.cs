using System.Collections.Generic;
using System.Numerics;
using Silk.NET.OpenGL;
using Fuse.Core;
using Fuse.Renderer.PostProcess;
using Fuse.Math;
using Fuse.Scene.Model;

namespace Fuse.Renderer;

public struct BillboardDraw
{
    public Matrix4x4 View;
    public Matrix4x4 Proj;
    public uint Texture;
    public Vector3 WorldPos;
    public Vector2 Size;
    public Vector4 Color;
}

public struct DecalDraw
{
    public Vector3 Position;
    public Vector3 Normal;
    public Vector3 Right;
    public Vector3 Up;
    public Matrix4x4 ModelMatrix;
    public Matrix4x4 InvModelMatrix;
    public uint AlbedoTexture;
    public uint NormalTexture;
    public float Size;
    public float Depth;
    public float LifeTime;
    public float FadeStart;
    public float Age;

    // Physics attachment
    public Physics.RigidBody? ParentBody;
    public Vector3 LocalPosition;
    public Quaternion LocalRotation;
}

public unsafe class MasterRenderer
{
    private readonly GL _gl;
    private int _scrWidth, _scrHeight;

    public int Width => _scrWidth;
    public int Height => _scrHeight;

    // Shaders
    private Shader _shader = null!;
    private Shader _skyboxShader = null!;
    private Shader _shadowShader = null!;
    private Shader _skinnedShader = null!;
    private Shader _skinnedShadowShader = null!;
    private Shader _skinnedPointShadowShader = null!;
    private ShadowMap _shadowMap = null!;
    private ShadowMap _staticShadowMap = null!;
    private ShadowMap _spotShadowMap = null!;
    private ShadowMap _staticSpotShadowMap = null!;
    private Shader _pointShadowShader = null!;
    private PointShadowMap[] _pointShadowMaps = null!;
    private PointShadowMap[] _staticPointShadowMaps = null!;
    private LightingBuffer _lightingBuffer = null!;
    private ImageBasedLighting? _imageBasedLighting;

    private const int CascadeCount = 3;
    private const int MaxShadowedPointLightSlots = 4;
    private const int MaxLightCandidates = 256;

    private struct LayerShadowCache
    {
        public Light? Light;
        public ulong SceneRevision;
        public Matrix4x4 Matrix;
        public bool Valid;
    }

    private struct PointShadowCache
    {
        public Light? Light;
        public ulong SceneRevision;
        public Vector3 Position;
        public float Radius;
        public bool Valid;
    }

    private struct LightCandidate
    {
        public Light Light;
        public float Score;
    }

    private readonly LayerShadowCache[] _directionalShadowCache = new LayerShadowCache[CascadeCount];
    private readonly LayerShadowCache[] _spotShadowCache = new LayerShadowCache[LightingBuffer.MaxSpotLights];
    private readonly PointShadowCache[] _pointShadowCache = new PointShadowCache[MaxShadowedPointLightSlots];
    private readonly LightCandidate[] _lightCandidates = new LightCandidate[MaxLightCandidates];
    private readonly Light[] _pointLights = new Light[ForwardPlusLighting.MaxPointLights];
    private readonly Light[] _spotLights = new Light[ForwardPlusLighting.MaxSpotLights];
    private readonly Light[] _shadowPointLights = new Light[MaxShadowedPointLightSlots];
    private readonly Light[] _shadowPointSelectionScratch = new Light[MaxShadowedPointLightSlots];
    private readonly Matrix4x4[] _lightSpaceMatrices = new Matrix4x4[CascadeCount];
    private readonly Matrix4x4[] _spotSpaceMatrices = new Matrix4x4[LightingBuffer.MaxSpotLights];
    private readonly float[] _cascadeLevels = new float[CascadeCount];
    private readonly float[] _cascadeTexelSizes = new float[CascadeCount];
    private ForwardPlusLighting? _forwardPlusLighting;

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

    // billboard
    private Shader _bbShader = null!;
    private int _bbUView, _bbUProj, _bbUWorldPos, _bbUSize, _bbUColor, _bbUTexture;
    private uint _bbVao, _bbVbo;

    // Decal Shader (Box Projected Decals)
    private Shader _decalShader = null!;
    private uint _decalDepthFboHdr;     // DepthComponent32f — para HdrFbo (post-process ON)
    private uint _decalDepthTexHdr;
    private uint _decalDepthFboDefault; // DepthComponent24  — para FBO 0 (post-process OFF)
    private uint _decalDepthTexDefault;
    private uint _decalSurfaceFboHdr;
    private uint _decalNormalTexHdr;
    private uint _decalMaterialTexHdr;

    // Shared bone SSBO (all skinned shaders read from binding point 0)
    private uint _sharedBonesSSBO;
    private Matrix4x4[] _sharedTransposedMatrices = [];

    // Post-Process
    private PostProcessPipeline _postPipeline = null!;
    private VolumetricCloudRenderer? _cloudRenderer;
    private VolumetricFogRenderer? _fogRenderer;
    private OceanRenderer? _oceanRenderer;
    private VolumetricCloudSettings _cloudSettings = new();
    private VolumetricFogSettings _fogSettings = new();
    private OceanSettings _oceanSettings = new();
    private PostProcessSettings _postSettings = new();
    public PostProcessPipeline PostPipeline => _postPipeline;
    public VolumetricCloudSettings VolumetricClouds => _cloudSettings;
    public VolumetricFogSettings VolumetricFog => _fogSettings;
    public OceanSettings Ocean => _oceanSettings;

    public void SetVolumetricClouds(VolumetricCloudSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _cloudSettings = settings.Clone();
        _cloudRenderer?.InvalidateHistory();
    }

    public void SetVolumetricFog(VolumetricFogSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _fogSettings = settings.Clone();
        _fogRenderer?.InvalidateHistory();
    }

    public void SetOcean(OceanSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _oceanSettings = settings.Clone();
    }
    private readonly EngineProfiler _profiler = new();
    public EngineProfiler Profiler => _profiler;
    private Matrix4x4 _prevViewProj = Matrix4x4.Identity;

    // SSAO
    private Vector3[] _ssaoKernel = [];
    private uint _ssaoNoiseTex;
    public uint SsaoNoiseTex => _ssaoNoiseTex;

    // Billboard queue (rendered in HDR FBO before post-process)
    private readonly List<BillboardDraw> _billboardQueue = new();

    // Decal queue
    private readonly List<DecalDraw> _decalQueue = new();
    public IReadOnlyList<DecalDraw> DecalQueue => _decalQueue;

    public void QueueBillboard(Matrix4x4 view, Matrix4x4 proj, uint texture, Vector3 worldPos, Vector2 size, Vector4 color)
    {
        _billboardQueue.Add(new BillboardDraw
        {
            View = view,
            Proj = proj,
            Texture = texture,
            WorldPos = worldPos,
            Size = size,
            Color = color
        });
    }

    public void ClearBillboardQueue()
    {
        _billboardQueue.Clear();
    }

    public void QueueDecal(DecalDraw decal)
    {
        _decalQueue.Add(decal);
    }

    public void ReloadPostProcessShader()
    {
        _postPipeline.ReloadShader();
        GameNotify.Info("PP Shaders Reload");
    }

    public void ReloadAllShaders(AssetManagement.AssetManager assets, DeathScreen? deathScreen = null)
    {
        int reloaded = assets.ReloadAllShaders();

        // AssetManager reloads the shared post-process Shader in place. Its
        // wrapper stores raw uniform locations, so refresh them after the swap.
        _postPipeline.RefreshShaderBindings();

        if (_forwardPlusLighting?.ReloadShader() == true)
            reloaded++;

        CacheBillboardUniforms();

        if (deathScreen?.Reload() == true)
            reloaded++;

        GameNotify.Info($"Shaders Reloaded ({reloaded})");
        Logger.InfoGold($"[ShaderHotReload] {reloaded} shader programs recarregados");
    }

    /// <summary>
    /// Spawns a deferred box projected decal at the given world position and orientation.
    /// If parentBody is specified, the decal automatically follows the physical object when it moves or rotates.
    /// </summary>
    public void SpawnDecal(
        Vector3 position,
        Vector3 normal,
        uint textureId,
        float size = 0.30f,
        float lifeTime = 30f,
        float fadeStart = 0.7f,
        Physics.RigidBody? parentBody = null,
        Physics.PhysicsWorld? physics = null)
    {
        float depth = size * 0.9f;

        // Build stable orientation basis
        Vector3 forward = Vector3.Normalize(normal);
        Vector3 upHint = MathF.Abs(forward.Y) > 0.99f ? Vector3.UnitZ : Vector3.UnitY;
        Vector3 right = Vector3.Normalize(Vector3.Cross(upHint, forward));
        Vector3 up = Vector3.Normalize(Vector3.Cross(forward, right));

        var rotMat = new Matrix4x4(
            right.X,   right.Y,   right.Z,   0,
            up.X,      up.Y,      up.Z,      0,
            forward.X, forward.Y, forward.Z, 0,
            0,         0,         0,         1
        );
        var rot = Quaternion.CreateFromRotationMatrix(rotMat);

        Matrix4x4 model = Matrix4x4.CreateScale(size, size, depth) *
                          Matrix4x4.CreateFromQuaternion(rot) *
                          Matrix4x4.CreateTranslation(position);

        Matrix4x4.Invert(model, out Matrix4x4 invModel);

        Vector3 localPos = Vector3.Zero;
        Quaternion localRot = Quaternion.Identity;

        if (parentBody != null && parentBody.IsBuilt)
        {
            Vector3 bodyPos = physics != null ? parentBody.Position(physics) : parentBody.GetPosition();
            Quaternion bodyRot = physics != null ? parentBody.Rotation(physics) : parentBody.GetRotation();
            var invBodyRot = Quaternion.Inverse(bodyRot);

            localPos = Vector3.Transform(position - bodyPos, invBodyRot);
            localRot = invBodyRot * rot;
        }

        _decalQueue.Add(new DecalDraw
        {
            Position       = position,
            Normal         = forward,
            Right          = right,
            Up             = up,
            ModelMatrix    = model,
            InvModelMatrix = invModel,
            AlbedoTexture  = textureId,
            Size           = size,
            Depth          = depth,
            LifeTime       = lifeTime,
            FadeStart      = fadeStart,
            Age            = 0f,
            ParentBody     = parentBody,
            LocalPosition  = localPos,
            LocalRotation  = localRot
        });
    }


    public void ClearDecalQueue()
    {
        _decalQueue.Clear();
    }

    public void UpdateDecals(float dt, Physics.PhysicsWorld? physics = null)
    {
        for (int i = _decalQueue.Count - 1; i >= 0; i--)
        {
            var decal = _decalQueue[i];
            decal.Age += dt;

            // Se o corpo de física pai foi destruído, remove o decalque
            if (decal.ParentBody != null)
            {
                if (!decal.ParentBody.IsBuilt)
                {
                    _decalQueue.RemoveAt(i);
                    continue;
                }

                if (physics != null)
                {
                    Vector3 currentBodyPos = decal.ParentBody.Position(physics);
                    Quaternion currentBodyRot = decal.ParentBody.Rotation(physics);

                    Vector3 worldPos = currentBodyPos + Vector3.Transform(decal.LocalPosition, currentBodyRot);
                    Quaternion worldRot = currentBodyRot * decal.LocalRotation;

                    Matrix4x4 model = Matrix4x4.CreateScale(decal.Size, decal.Size, decal.Depth) *
                                      Matrix4x4.CreateFromQuaternion(worldRot) *
                                      Matrix4x4.CreateTranslation(worldPos);

                    Matrix4x4.Invert(model, out Matrix4x4 invModel);

                    decal.Position = worldPos;
                    decal.ModelMatrix = model;
                    decal.InvModelMatrix = invModel;
                }
            }

            _decalQueue[i] = decal;

            if (decal.Age >= decal.LifeTime)
                _decalQueue.RemoveAt(i);
        }
    }


    // Textures
    private Texture _crateTexture = null!;
    private Texture _skyboxTexture = null!;
    private Vector3 _skyboxDominantColor = Vector3.One;
    private SkyboxSettings _skyboxSettings = new();
    private ulong _skyboxSettingsSignature;
    private ulong _proceduralIblSignature;
    private long _proceduralIblLastAttemptMilliseconds;
    private const long ProceduralIblRefreshIntervalMilliseconds = 500;
    public Texture SkyboxTexture => _skyboxTexture;
    public SkyboxSettings SkyboxSettings => _skyboxSettings;
    public bool IsProceduralSkybox => _skyboxSettings.Mode == SkyboxMode.Procedural;

    public void SetSkyboxTexture(Texture tex)
    {
        _skyboxTexture = tex;
        _skyboxSettings = new SkyboxSettings();
        _skyboxSettingsSignature = ProceduralSky.ComputeSettingsSignature(_skyboxSettings);
        _proceduralIblSignature = 0;
        _proceduralIblLastAttemptMilliseconds = 0;
        _imageBasedLighting?.Dispose();
        _imageBasedLighting = null;
        _fogRenderer?.InvalidateHistory();
        if (_skyboxTexture.ID != 0)
        {
            _skyboxDominantColor = _skyboxTexture.GetDominantColor();
            _gl.BindTexture(TextureTarget.Texture2D, _skyboxTexture.ID);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
            try
            {
                _imageBasedLighting = new ImageBasedLighting(_gl, _skyboxTexture);
            }
            catch (Exception ex)
            {
                Logger.Warn($"IBL disabled after skybox change: {ex.Message}");
            }
        }
    }

    public void SetProceduralSkybox(SkyboxSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        SkyboxSettings next = settings.Clone();
        next.Mode = SkyboxMode.Procedural;
        ulong signature = ProceduralSky.ComputeSettingsSignature(next);
        bool alreadyProcedural = _skyboxSettings.Mode == SkyboxMode.Procedural;
        if (alreadyProcedural && signature == _skyboxSettingsSignature)
            return;

        if (!alreadyProcedural)
        {
            _imageBasedLighting?.Dispose();
            _imageBasedLighting = null;
            _proceduralIblSignature = 0;
            _proceduralIblLastAttemptMilliseconds = 0;
        }

        _skyboxSettings = next;
        _skyboxSettingsSignature = signature;
        _skyboxDominantColor = ProceduralSky.EstimateAmbientColor(_skyboxSettings);
        _fogRenderer?.InvalidateHistory();
    }

    private void EnsureProceduralSkyboxIbl(
        Vector3 sunDirection,
        Vector3 directionalLightColor)
    {
        if (!IsProceduralSkybox)
            return;

        ulong signature = ProceduralSky.ComputeIblSignature(
            _skyboxSettings,
            sunDirection,
            directionalLightColor);
        long now = Environment.TickCount64;
        long elapsed = now - _proceduralIblLastAttemptMilliseconds;

        if (_imageBasedLighting != null && _proceduralIblSignature == signature)
            return;
        if (elapsed < ProceduralIblRefreshIntervalMilliseconds)
            return;

        ImageBasedLighting? replacement = null;
        try
        {
            replacement = ImageBasedLighting.CreateProcedural(
                _gl,
                _skyboxSettings,
                sunDirection,
                directionalLightColor);
        }
        catch (Exception ex)
        {
            Logger.Warn($"Procedural sky IBL disabled: {ex.Message}");
        }

        _proceduralIblLastAttemptMilliseconds = now;
        _proceduralIblSignature = signature;
        if (replacement == null)
            return;

        _imageBasedLighting?.Dispose();
        _imageBasedLighting = replacement;
    }

    // Meshes
    private Mesh _skyBoxCubeMesh = null!;

    // Shadow Settings
    public uint ShadowResolution = 512;
    public float ShadowBiasFactor = 0.0005f;
    public float ShadowBiasBase = 0.00001f;
    public float ShadowNearPlane = 0.1f;
    public float ShadowFarPlane = 150.0f;
    public float ShadowSpread = 1.0f;
    public float CascadeSplitLambda = 0.70f;
    public float CascadeBlendFraction = 0.10f;
    public float ShadowFadeFraction = 0.12f;
    public float ShadowDepthPadding = 3.0f;
    public int MaxShadowedPointLights = 4;
    public float LightSelectionHysteresis = 0.25f;
    public bool ShadowsEnabled = false;
    public bool EnableShadowFilter = true;

    public MasterRenderer(GL gl)
    {
        _gl = gl;
    }

    public unsafe void Init(AssetManagement.AssetManager assets, int width, int height)
    {
        _scrWidth = width; 
        _scrHeight = height;
        _gl.Enable(EnableCap.TextureCubeMapSeamless);

        _shader = assets.GetShader(Bible.Shader(Bible.ShaderDefaultVert), Bible.Shader(Bible.ShaderDefaultFrag))!;
        _skyboxShader = assets.GetShader(Bible.Shader(Bible.ShaderSkyboxVert), Bible.Shader(Bible.ShaderSkyboxFrag))!;
        _shadowShader = assets.GetShader(Bible.Shader(Bible.ShaderShadowVert), Bible.Shader(Bible.ShaderShadowFrag))!;
        _pointShadowShader = assets.GetShader(Bible.Shader(Bible.ShaderPointShadowVert), Bible.Shader(Bible.ShaderPointShadowFrag))!;
        _skinnedShader = assets.GetShader(Bible.Shader(Bible.ShaderSkinnedVert), Bible.Shader(Bible.ShaderDefaultFrag))!;
        _skinnedShadowShader = assets.GetShader(Bible.Shader(Bible.ShaderSkinnedShadowVert), Bible.Shader(Bible.ShaderShadowFrag))!;
        _skinnedPointShadowShader = assets.GetShader(Bible.Shader(Bible.ShaderPointShadowSkinnedVert), Bible.Shader(Bible.ShaderPointShadowFrag))!;
        
        _shadowMap = new ShadowMap(_gl, ShadowResolution * 2, ShadowResolution * 2);
        _staticShadowMap = new ShadowMap(_gl, ShadowResolution * 2, ShadowResolution * 2);
        _spotShadowMap = new ShadowMap(_gl, ShadowResolution, ShadowResolution, 4);
        _staticSpotShadowMap = new ShadowMap(_gl, ShadowResolution, ShadowResolution, 4);
        _pointShadowMaps = new PointShadowMap[MaxShadowedPointLightSlots];
        _staticPointShadowMaps = new PointShadowMap[MaxShadowedPointLightSlots];
        for (int i = 0; i < MaxShadowedPointLightSlots; i++)
        {
            _pointShadowMaps[i] = new PointShadowMap(_gl, ShadowResolution);
            _staticPointShadowMaps[i] = new PointShadowMap(_gl, ShadowResolution);
        }
        _lightingBuffer = new LightingBuffer(_gl);
        try
        {
            _forwardPlusLighting = new ForwardPlusLighting(
                _gl, Bible.Shader(Bible.ShaderForwardPlusCull));
            Logger.Important("Forward+ light culling enabled");
        }
        catch (Exception ex)
        {
            _forwardPlusLighting = null;
            Logger.Warn($"Forward+ disabled: {ex.Message}");
        }
        
        _skyBoxCubeMesh = assets.GetMesh("cube")!;
        
        _crateTexture = assets.GetTexture(Bible.Tex(Bible.Crate), TextureColorSpace.Srgb);
        _skyboxTexture = assets.GetTexture(Bible.Tex(Bible.Skybox), TextureColorSpace.Srgb);
        
        if (_skyboxTexture.ID != 0)
        {
            _skyboxDominantColor = _skyboxTexture.GetDominantColor();
            _gl.BindTexture(TextureTarget.Texture2D, _skyboxTexture.ID);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
        }

        try
        {
            _imageBasedLighting = new ImageBasedLighting(_gl, _skyboxTexture);
        }
        catch (Exception ex)
        {
            _imageBasedLighting = null;
            Logger.Warn($"IBL disabled: {ex.Message}");
        }

        // Billboard shader (muzzle flash) - carregar de arquivos
        _bbShader = assets.GetShader(Bible.Shader(Bible.ShaderBillboardVert), Bible.Shader(Bible.ShaderBillboardFrag))!;
        CacheBillboardUniforms();

        // Decal Shader (Box Projected Decals)
        _decalShader = assets.GetShader(Bible.Shader(Bible.ShaderDecalVert), Bible.Shader(Bible.ShaderDecalFrag))!;
        _shader.BindUniformBlock("LightingBlock", LightingBuffer.BindingPoint);
        _skinnedShader.BindUniformBlock("LightingBlock", LightingBuffer.BindingPoint);
        _decalShader.BindUniformBlock("LightingBlock", LightingBuffer.BindingPoint);
        CreateDecalDepthResources();



        // Post-Process Pipeline
        _postPipeline = new PostProcessPipeline(_gl, assets, _scrWidth, _scrHeight);
        _postSettings = _postPipeline.Settings;
        try
        {
            _cloudRenderer = new VolumetricCloudRenderer(_gl);
        }
        catch (Exception ex)
        {
            _cloudRenderer = null;
            Logger.Warn($"Volumetric clouds disabled: {ex.Message}");
        }
        try
        {
            _fogRenderer = new VolumetricFogRenderer(
                _gl,
                _cloudRenderer?.BaseNoiseTexture ?? 0,
                _shadowMap);
            _fogRenderer.SetLocalShadowMaps(_spotShadowMap, _pointShadowMaps);
        }
        catch (Exception ex)
        {
            _fogRenderer = null;
            Logger.Warn($"Volumetric fog disabled: {ex.Message}");
        }
        try
        {
            _oceanRenderer = new OceanRenderer(_gl);
        }
        catch (Exception ex)
        {
            _oceanRenderer = null;
            Logger.Warn($"Ocean rendering disabled: {ex.Message}");
        }

        // SSAO Kernel + Noise
        InitSsao();
        _postPipeline.SetSsaoKernel(_ssaoKernel);
        _postPipeline.SetSsaoNoiseTex(_ssaoNoiseTex);

        float[] quadVerts = [
            -0.5f, -0.5f,  0, 0,
             0.5f, -0.5f,  1, 0,
             0.5f,  0.5f,  1, 1,
            -0.5f, -0.5f,  0, 0,
             0.5f,  0.5f,  1, 1,
            -0.5f,  0.5f,  0, 1,
        ];
        _bbVao = _gl.GenVertexArray();
        _bbVbo = _gl.GenBuffer();
        _gl.BindVertexArray(_bbVao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _bbVbo);
        unsafe
        {
            fixed (float* ptr = quadVerts)
                _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(quadVerts.Length * sizeof(float)), ptr, BufferUsageARB.DynamicDraw);
        }
        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 4 * sizeof(float), (void*)0);
         _gl.EnableVertexAttribArray(1);
         _gl.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, 4 * sizeof(float), (void*)(2 * sizeof(float)));
         _gl.BindVertexArray(0);
     }

    private void CacheBillboardUniforms()
    {
        if (_bbShader == null || _bbShader.ID == 0)
            return;

        _bbUView = _gl.GetUniformLocation(_bbShader.ID, "uView");
        _bbUProj = _gl.GetUniformLocation(_bbShader.ID, "uProj");
        _bbUWorldPos = _gl.GetUniformLocation(_bbShader.ID, "uWorldPos");
        _bbUSize = _gl.GetUniformLocation(_bbShader.ID, "uSize");
        _bbUColor = _gl.GetUniformLocation(_bbShader.ID, "uColor");
        _bbUTexture = _gl.GetUniformLocation(_bbShader.ID, "uTexture");
    }

    private void CreateDecalDepthResources()
    {
        DestroyDecalDepthResources();

        // --- HDR (DepthComponent32f) — combina com HdrFbo ---
        _decalDepthTexHdr = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, _decalDepthTexHdr);
        _gl.TexImage2D(TextureTarget.Texture2D, 0, (int)InternalFormat.DepthComponent32f,
            (uint)_scrWidth, (uint)_scrHeight, 0,
            PixelFormat.DepthComponent, PixelType.Float, null);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Nearest);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Nearest);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);

        _decalDepthFboHdr = _gl.GenFramebuffer();
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _decalDepthFboHdr);
        _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment,
            TextureTarget.Texture2D, _decalDepthTexHdr, 0);

        // Surface data copied from the HDR scene before decals are drawn.
        _decalSurfaceFboHdr = _gl.GenFramebuffer();
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _decalSurfaceFboHdr);

        _decalNormalTexHdr = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, _decalNormalTexHdr);
        _gl.TexImage2D(TextureTarget.Texture2D, 0,
            (int)InternalFormat.Rgba16f, (uint)_scrWidth, (uint)_scrHeight, 0,
            PixelFormat.Rgba, PixelType.Float, null);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Nearest);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Nearest);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
        _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer,
            FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, _decalNormalTexHdr, 0);

        _decalMaterialTexHdr = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, _decalMaterialTexHdr);
        _gl.TexImage2D(TextureTarget.Texture2D, 0,
            (int)InternalFormat.Rgba16f, (uint)_scrWidth, (uint)_scrHeight, 0,
            PixelFormat.Rgba, PixelType.Float, null);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Nearest);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Nearest);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
        _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer,
            FramebufferAttachment.ColorAttachment1, TextureTarget.Texture2D, _decalMaterialTexHdr, 0);
        _gl.DrawBuffers(new[]
        {
            DrawBufferMode.ColorAttachment0,
            DrawBufferMode.ColorAttachment1
        });

        if (_gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer) != GLEnum.FramebufferComplete)
            throw new Exception("Decal surface framebuffer incomplete.");

        // --- Default (DepthComponent24) — combina com FBO 0 ---
        _decalDepthTexDefault = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, _decalDepthTexDefault);
        _gl.TexImage2D(TextureTarget.Texture2D, 0, (int)InternalFormat.DepthComponent24,
            (uint)_scrWidth, (uint)_scrHeight, 0,
            PixelFormat.DepthComponent, PixelType.UnsignedInt, null);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Nearest);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Nearest);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);

        _decalDepthFboDefault = _gl.GenFramebuffer();
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _decalDepthFboDefault);
        _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment,
            TextureTarget.Texture2D, _decalDepthTexDefault, 0);

        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    private void DestroyDecalDepthResources()
    {
        if (_decalDepthFboHdr != 0) { _gl.DeleteFramebuffer(_decalDepthFboHdr); _decalDepthFboHdr = 0; }
        if (_decalDepthTexHdr != 0) { _gl.DeleteTexture(_decalDepthTexHdr); _decalDepthTexHdr = 0; }
        if (_decalSurfaceFboHdr != 0) { _gl.DeleteFramebuffer(_decalSurfaceFboHdr); _decalSurfaceFboHdr = 0; }
        if (_decalNormalTexHdr != 0) { _gl.DeleteTexture(_decalNormalTexHdr); _decalNormalTexHdr = 0; }
        if (_decalMaterialTexHdr != 0) { _gl.DeleteTexture(_decalMaterialTexHdr); _decalMaterialTexHdr = 0; }
        if (_decalDepthFboDefault != 0) { _gl.DeleteFramebuffer(_decalDepthFboDefault); _decalDepthFboDefault = 0; }
        if (_decalDepthTexDefault != 0) { _gl.DeleteTexture(_decalDepthTexDefault); _decalDepthTexDefault = 0; }
    }

    public void Resize(int width, int height)
    {
        _scrWidth = width;
        _scrHeight = height;
        _postPipeline?.Resize(width, height);
        _cloudRenderer?.InvalidateHistory();
        _fogRenderer?.InvalidateHistory();
        CreateDecalDepthResources();
    }


    public void RenderFrame(
        Scene scene,
        Camera camera,
        Physics.PhysicsWorld physics,
        Matrix4x4? renderViewOverride = null)
    {
        using var mainRenderScope = _profiler.Measure(ProfilerSection.MainRender);

        float aspect = (float)_scrWidth / _scrHeight;
        var view = renderViewOverride ?? camera.GetViewMatrix();
        var proj = camera.GetProjectionMatrix(aspect);

        // --- 0. Estado limpo no início do frame ---
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        _gl.Viewport(0, 0, (uint)_scrWidth, (uint)_scrHeight);
        _gl.ClearColor(0.0f, 0.0f, 0.0f, 1.0f);
        _gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

        // --- 0. Update Physics and Hierarchy ---
        scene.UpdateTransforms(physics);
        scene.UpdateTerrainLod(
            camera.Position,
            _scrHeight,
            camera.FOV);
        scene.PrepareShadowCasters();

        // Update Decals (age, cull expired)
        _decalQueue.ForEach(d => { /* age handled in UpdateDecals */});
        // We'll call UpdateDecals from Application.cs instead

        // ===== RENDER CENA NO HDR FBO (shadow pass usa framebuffer próprio) =====

        // --- 1. Select lights and prepare shadow data ---
        Light? dirLight = null;
        for (int i = 0; i < scene.Lights.Count; i++)
        {
            var l = scene.Lights[i];
            if (l.Enabled && l.Type == LightType.Directional) { dirLight = l; break; }
        }
        Vector3 lightDir = dirLight != null && dirLight.Direction.LengthSquared() > 1e-8f
            ? -Vector3.Normalize(dirLight.Direction)
            : ProceduralSky.FallbackSunDirection;
        Vector3 directionalLightColor = dirLight != null
            ? dirLight.Color * MathF.Max(dirLight.Intensity, 0.0f)
            : Vector3.Zero;
        Vector3 skyDirectionalLightColor = dirLight != null
            ? directionalLightColor
            : Vector3.One;
        if (IsProceduralSkybox)
        {
            _skyboxDominantColor = ProceduralSky.EstimateAmbientColor(
                _skyboxSettings,
                lightDir);
        }
        EnsureProceduralSkyboxIbl(lightDir, skyDirectionalLightColor);
        _cloudRenderer?.UpdateShadow(
            camera.Position,
            lightDir,
            _cloudSettings,
            simulationTimeSeconds: Engine.Time);
        bool fogNeedsDirectionalShadows = _fogSettings.Enabled &&
                                          _fogSettings.LightShaftsEnabled;
        bool renderDirShadows = dirLight != null && dirLight.CastShadows &&
                                (ShadowsEnabled || fogNeedsDirectionalShadows);

        CalculateCascadeSplits(camera.NearPlane, ShadowFarPlane, _cascadeLevels);
        int spotCount = SelectLights(scene.Lights, LightType.Spot, _spotLights,
            ForwardPlusLighting.MaxSpotLights, camera);
        int pointCount = SelectLights(scene.Lights, LightType.Point, _pointLights,
            ForwardPlusLighting.MaxPointLights, camera);
        int shadowPointLimit = int.Clamp(MaxShadowedPointLights, 1, MaxShadowedPointLightSlots);
        int shadowPointCount = SelectShadowedPointLights(
            _pointLights.AsSpan(0, pointCount), _shadowPointLights, shadowPointLimit, camera);
        // The shadow budget limits shadow maps only. Keep every selected point
        // light in the lighting buffer so crossing a room/floor cannot turn
        // artificial illumination off just because its shadow map slot changed.

        Array.Clear(_lightSpaceMatrices);
        Array.Clear(_spotSpaceMatrices);
        Array.Clear(_cascadeTexelSizes);

        if (renderDirShadows && _shadowShader.ID != 0)
        {
            using var shadowScope = _profiler.Measure(ProfilerSection.DirectionalShadows);
            RenderDirectionalShadowPass(scene, camera, aspect, lightDir);
        }
        if (ShadowsEnabled && _shadowShader.ID != 0)
        {
            using var shadowScope = _profiler.Measure(ProfilerSection.SpotShadows);
            RenderSpotShadowPass(scene, System.Math.Min(spotCount, LightingBuffer.MaxSpotLights));
        }
        if (ShadowsEnabled && _pointShadowShader.ID != 0)
        {
            using var shadowScope = _profiler.Measure(ProfilerSection.PointShadows);
            RenderPointShadowPass(scene, shadowPointCount);
        }

        _profiler.SetLightingCounts(scene.Lights.Count, pointCount, spotCount);

        _gl.Disable(EnableCap.PolygonOffsetFill);
        _gl.PolygonOffset(0.0f, 0.0f);

        float skyLuminance = _skyboxDominantColor.X * 0.2126f +
                             _skyboxDominantColor.Y * 0.7152f +
                             _skyboxDominantColor.Z * 0.0722f;
        float ambient = 0.02f + 0.28f * skyLuminance;
        Vector3 directionalColor = directionalLightColor;
        float fadeStart = ShadowFarPlane * (1.0f - float.Clamp(ShadowFadeFraction, 0.0f, 0.5f));
        _lightingBuffer.Upload(
            camera.Position,
            lightDir,
            directionalColor,
            ambient,
            renderDirShadows,
            ShadowsEnabled,
            EnableShadowFilter,
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
            _spotSpaceMatrices.AsSpan(0, System.Math.Min(spotCount, _spotSpaceMatrices.Length)));

        _forwardPlusLighting?.UploadLights(
            _pointLights.AsSpan(0, pointCount),
            _spotLights.AsSpan(0, spotCount),
            _shadowPointLights.AsSpan(0, shadowPointCount),
            ShadowsEnabled);
        _forwardPlusLighting?.Dispatch(view, proj, _scrWidth, _scrHeight);

        // --- 2. Regular Render Pass (sempre no HDR FBO) ---
        if (!_postPipeline.ValidateHdrFbo(_gl))
        {
            _postPipeline.Reset();
        }
        uint targetFbo = _postPipeline.HdrFbo;

        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, targetFbo);
        _gl.Viewport(0, 0, (uint)_scrWidth, (uint)_scrHeight);
        _gl.ClearColor(0.1f, 0.1f, 0.15f, 1.0f);
        _gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

        // O attachment emissivo precisa começar preto a cada frame. O clear
        // da cena usa uma cor de fundo diferente e limparia os dois attachments
        // com essa cor, gerando bloom no céu/fundo.
        float[] emissiveClear = [0.0f, 0.0f, 0.0f, 0.0f];
        fixed (float* clear = emissiveClear)
            _gl.ClearBuffer(BufferKind.Color, 1, clear);

        float[] normalClear = [0.5f, 0.5f, 1.0f, 1.0f];
        fixed (float* normalPtr = normalClear)
            _gl.ClearBuffer(BufferKind.Color, 2, normalPtr);

        float[] materialClear = [0.5f, 0.0f, 1.0f, 0.0f];
        fixed (float* materialPtr = materialClear)
            _gl.ClearBuffer(BufferKind.Color, 3, materialPtr);

        // Skybox
        bool renderProceduralSky = IsProceduralSkybox;
        if (_skyboxShader.ID != 0 &&
            _skyBoxCubeMesh != null &&
            (renderProceduralSky || _skyboxTexture.ID != 0))
        {
            _gl.DepthMask(false);
            _gl.DepthFunc(DepthFunction.Lequal);
            _gl.CullFace(GLEnum.Front);

            _skyboxShader.Use();
            var skyView = Matrix4x4.CreateFromQuaternion(Quaternion.CreateFromRotationMatrix(view));
            _skyboxShader.SetMat4("uView", skyView);
            _skyboxShader.SetMat4("uProj", proj);
            _skyboxShader.SetBool("uOutputSrgb", !_postPipeline.Settings.Enabled);
            ProceduralSky.ApplyShaderParameters(
                _skyboxShader,
                _skyboxSettings,
                lightDir,
                skyDirectionalLightColor);
            _skyboxShader.SetInt("uSkyTexture", 0);
            if (!renderProceduralSky)
                _skyboxTexture.Bind(0);
            _skyBoxCubeMesh.Draw();

            _gl.CullFace(GLEnum.Back);
            _gl.DepthFunc(DepthFunction.Less);
            _gl.DepthMask(true);
        }

        // World geometry
        if (_shader.ID != 0)
        {
            _gl.Enable(EnableCap.DepthTest);
            _gl.Enable(EnableCap.CullFace);
            _gl.CullFace(GLEnum.Back);
            _gl.DepthFunc(DepthFunction.Less);

            _shader.Use();
            SetupWorldUniforms(_shader, view, proj);

            Matrix4x4 cameraViewProjection = view * proj;
            using (var pbrScope = _profiler.Measure(ProfilerSection.Pbr))
            {
                int staticObjectsDrawn = scene.Render(
                    _shader,
                    _crateTexture,
                    cameraViewProjection,
                    materialShader => SetupWorldUniforms(materialShader, view, proj));

                // Skinned entities (main pass)
                _skinnedShader.Use();
                SetupWorldUniforms(_skinnedShader, view, proj);
                int skinnedObjectsDrawn = RenderSkinned(
                    scene, _skinnedShader, cameraViewProjection, view, proj);
                _profiler.SetRenderCounts(staticObjectsDrawn + skinnedObjectsDrawn);
            }
        }

        // ===== DECALS (Forward Lit in HDR FBO, after geometry) =====
        if (_decalQueue.Count > 0)
        {
            RenderDecals(view, proj, targetFbo);
        }

        // The ocean surface is rendered after opaque geometry and decals. It
        // copies attachment 0 plus depth before sampling them, so reflection
        // and refraction never read from the color attachment currently being
        // written. The underwater fullscreen pass is deferred until after
        // clouds and atmospheric fog.
        bool underwater = false;
        if (_oceanRenderer != null && _oceanSettings.Enabled)
        {
            _oceanRenderer.Render(
                targetFbo,
                _scrWidth,
                _scrHeight,
                view,
                proj,
                camera.Position,
                lightDir,
                skyDirectionalLightColor,
                _oceanSettings,
                _skyboxSettings,
                _skyboxDominantColor,
                _imageBasedLighting,
                sceneIsSrgb: !_postPipeline.Settings.Enabled,
                outputSrgb: !_postPipeline.Settings.Enabled,
                targetHasMrt: true,
                simulationTimeSeconds: Engine.Time);
            underwater = _oceanRenderer.LastFrameUnderwater;
        }

        // Clouds are evaluated after opaque geometry so the scene depth can
        // stop the ray march. Copy the composed color back into attachment 0;
        // transparent gameplay billboards are then drawn over it normally.
        if (_cloudRenderer != null && _cloudSettings.Enabled && !underwater)
        {
            CloudCompositeResult clouds = _cloudRenderer.Render(
                _postPipeline.HdrColorId,
                _postPipeline.HdrDepthId,
                _scrWidth,
                _scrHeight,
                view,
                proj,
                camera.Position,
                lightDir,
                skyDirectionalLightColor,
                _cloudSettings,
                sceneIsSrgb: !_postPipeline.Settings.Enabled,
                outputSrgb: !_postPipeline.Settings.Enabled,
                simulationTimeSeconds: Engine.Time);
            if (clouds.Framebuffer != 0)
            {
                _gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, clouds.Framebuffer);
                _gl.ReadBuffer(ReadBufferMode.ColorAttachment0);
                _gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, targetFbo);
                _gl.DrawBuffer(DrawBufferMode.ColorAttachment0);
                _gl.BlitFramebuffer(
                    0, 0, _scrWidth, _scrHeight,
                    0, 0, _scrWidth, _scrHeight,
                    ClearBufferMask.ColorBufferBit,
                    BlitFramebufferFilter.Nearest);
                _gl.BindFramebuffer(FramebufferTarget.Framebuffer, targetFbo);
                _gl.DrawBuffers(new[]
                {
                    DrawBufferMode.ColorAttachment0,
                    DrawBufferMode.ColorAttachment1,
                    DrawBufferMode.ColorAttachment2,
                    DrawBufferMode.ColorAttachment3
                });
                _gl.Viewport(0, 0, (uint)_scrWidth, (uint)_scrHeight);
                _gl.Enable(EnableCap.DepthTest);
                _gl.Enable(EnableCap.CullFace);
                _gl.CullFace(GLEnum.Back);
                _gl.DepthFunc(DepthFunction.Less);
                _gl.DepthMask(true);
            }
        }

        // Fog is composited after clouds so the already visible cloud layer is
        // attenuated by the same aerial volume as opaque geometry. It runs
        // before the deferred underwater pass, allowing both effects to be
        // visible in the same frame.
        if (_fogRenderer != null && _fogSettings.Enabled)
        {
            FogCompositeResult fog = _fogRenderer.Render(
                _postPipeline.HdrColorId,
                _postPipeline.HdrDepthId,
                _scrWidth,
                _scrHeight,
                view,
                proj,
                camera.Position,
                lightDir,
                skyDirectionalLightColor,
                _fogSettings,
                _skyboxSettings,
                _skyboxDominantColor,
                sceneIsSrgb: !_postPipeline.Settings.Enabled,
                outputSrgb: !_postPipeline.Settings.Enabled);
            if (fog.Framebuffer != 0)
            {
                _gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, fog.Framebuffer);
                _gl.ReadBuffer(ReadBufferMode.ColorAttachment0);
                _gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, targetFbo);
                _gl.DrawBuffer(DrawBufferMode.ColorAttachment0);
                _gl.BlitFramebuffer(
                    0, 0, _scrWidth, _scrHeight,
                    0, 0, _scrWidth, _scrHeight,
                    ClearBufferMask.ColorBufferBit,
                    BlitFramebufferFilter.Nearest);
                _gl.BindFramebuffer(FramebufferTarget.Framebuffer, targetFbo);
                _gl.DrawBuffers(new[]
                {
                    DrawBufferMode.ColorAttachment0,
                    DrawBufferMode.ColorAttachment1,
                    DrawBufferMode.ColorAttachment2,
                    DrawBufferMode.ColorAttachment3
                });
                _gl.Viewport(0, 0, (uint)_scrWidth, (uint)_scrHeight);
                _gl.Enable(EnableCap.DepthTest);
                _gl.Enable(EnableCap.CullFace);
                _gl.CullFace(GLEnum.Back);
                _gl.DepthFunc(DepthFunction.Less);
                _gl.DepthMask(true);
            }
        }

        // Fog is a fullscreen compositor. Applying it after the underwater
        // pass would overwrite the water tint and distortion that the ocean
        // produced, so finish the ocean effect after all atmospheric passes.
        if (_oceanRenderer != null &&
            _oceanSettings.Enabled &&
            _oceanRenderer.UnderwaterPassPending)
        {
            _oceanRenderer.ApplyUnderwater(
                targetFbo,
                _scrWidth,
                _scrHeight,
                view,
                proj,
                camera.Position,
                lightDir,
                skyDirectionalLightColor,
                _oceanSettings,
                sceneIsSrgb: !_postPipeline.Settings.Enabled,
                outputSrgb: !_postPipeline.Settings.Enabled,
                targetHasMrt: true);
        }



        // ===== BILLBOARDS (no HDR FBO) =====
        if (_billboardQueue.Count > 0)
        {
            _gl.Enable(EnableCap.Blend);
            _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            _gl.Disable(EnableCap.CullFace);
            _gl.DepthMask(false);

            _gl.UseProgram(_bbShader.ID);

            foreach (var bb in _billboardQueue)
            {
                float[] viewArr = [
                    bb.View.M11, bb.View.M12, bb.View.M13, bb.View.M14,
                    bb.View.M21, bb.View.M22, bb.View.M23, bb.View.M24,
                    bb.View.M31, bb.View.M32, bb.View.M33, bb.View.M34,
                    bb.View.M41, bb.View.M42, bb.View.M43, bb.View.M44,
                ];
                float[] projArr = [
                    bb.Proj.M11, bb.Proj.M12, bb.Proj.M13, bb.Proj.M14,
                    bb.Proj.M21, bb.Proj.M22, bb.Proj.M23, bb.Proj.M24,
                    bb.Proj.M31, bb.Proj.M32, bb.Proj.M33, bb.Proj.M34,
                    bb.Proj.M41, bb.Proj.M42, bb.Proj.M43, bb.Proj.M44,
                ];
                unsafe
                {
                    fixed (float* vp = viewArr, pp = projArr)
                    {
                        _gl.UniformMatrix4(_bbUView, 1, false, vp);
                        _gl.UniformMatrix4(_bbUProj, 1, false, pp);
                    }
                }
                _gl.Uniform3(_bbUWorldPos, bb.WorldPos.X, bb.WorldPos.Y, bb.WorldPos.Z);
                _gl.Uniform2(_bbUSize, bb.Size.X, bb.Size.Y);
                _gl.Uniform4(_bbUColor, bb.Color.X, bb.Color.Y, bb.Color.Z, bb.Color.W);
                _gl.Uniform1(_bbUTexture, 0);
                _gl.ActiveTexture(TextureUnit.Texture0);
                _gl.BindTexture(TextureTarget.Texture2D, bb.Texture);

                _gl.BindVertexArray(_bbVao);
                _gl.DrawArrays(GLEnum.Triangles, 0, 6);
            }

            _gl.BindVertexArray(0);
            _gl.DepthMask(true);
            _gl.Enable(EnableCap.CullFace);
            _gl.Disable(EnableCap.Blend);

            _billboardQueue.Clear();
        }

        // ===== POST-PROCESS =====
        if (_prevViewProj == Matrix4x4.Identity)
        {
            _prevViewProj = view * proj;
        }

        using (var postProcessScope = _profiler.Measure(ProfilerSection.PostProcess))
        {
            if (_postPipeline.Settings.Enabled)
            {
                _postPipeline.SetViewProj(_prevViewProj, view, proj);
                _postPipeline.Execute(_postPipeline.HdrColorId, 0, _postPipeline.HdrEmissiveId); // 0 = tela final
            }
            else
            {
                // Sem post-process: blit simples do HDR FBO para a tela
                _gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, 0);
                _gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, _postPipeline.HdrFbo);
                _gl.BlitFramebuffer(0, 0, _postPipeline.Width, _postPipeline.Height,
                                    0, 0, _scrWidth, _scrHeight,
                                    ClearBufferMask.ColorBufferBit, BlitFramebufferFilter.Linear);
                _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            }
        }

        _prevViewProj = view * proj;
    }

    private void RenderDirectionalShadowPass(Scene scene, Camera camera, float aspect, Vector3 lightDirection)
    {
        PrepareShadowRenderState();

        for (int cascade = 0; cascade < CascadeCount; cascade++)
        {
            float logicalNear = cascade == 0 ? camera.NearPlane : _cascadeLevels[cascade - 1];
            float far = _cascadeLevels[cascade];
            float near = logicalNear;
            if (cascade > 0)
            {
                float previousNear = cascade == 1 ? camera.NearPlane : _cascadeLevels[cascade - 2];
                float overlap = (logicalNear - previousNear) *
                    float.Clamp(CascadeBlendFraction, 0.0f, 0.35f);
                near = MathF.Max(camera.NearPlane, logicalNear - overlap);
            }
            Matrix4x4 matrix = GetLightSpaceMatrix(
                scene, camera, aspect, near, far, lightDirection, out float texelWorldSize);
            _lightSpaceMatrices[cascade] = matrix;
            _cascadeTexelSizes[cascade] = texelWorldSize;

            ref LayerShadowCache cache = ref _directionalShadowCache[cascade];
            bool staticCacheDirty = !cache.Valid ||
                                    cache.SceneRevision != scene.StaticShadowRevision ||
                                    !MatrixApproximatelyEqual(cache.Matrix, matrix);

            if (staticCacheDirty)
            {
                _shadowShader.Use();
                _shadowShader.SetMat4("uLightSpaceMatrix", matrix);
                _staticShadowMap.BindForWriting(cascade);
                scene.RenderShadowCasters(_shadowShader, matrix, ShadowCasterFilter.Static);
                RenderSkinnedShadowCasters(scene, _skinnedShadowShader, matrix, ShadowCasterFilter.Static);

                cache.SceneRevision = scene.StaticShadowRevision;
                cache.Matrix = matrix;
                cache.Valid = true;
            }

            _staticShadowMap.CopyLayerTo(_shadowMap, cascade);
            _shadowMap.BindForWriting(cascade, clear: false);
            _shadowShader.Use();
            _shadowShader.SetMat4("uLightSpaceMatrix", matrix);
            scene.RenderShadowCasters(_shadowShader, matrix, ShadowCasterFilter.Dynamic);
            RenderSkinnedShadowCasters(scene, _skinnedShadowShader, matrix, ShadowCasterFilter.Dynamic);
        }
    }

    private void RenderSpotShadowPass(Scene scene, int spotCount)
    {
        PrepareShadowRenderState();

        for (int slot = 0; slot < spotCount; slot++)
        {
            Light light = _spotLights[slot];
            Vector3 direction = light.Direction.LengthSquared() > 1e-8f
                ? Vector3.Normalize(light.Direction)
                : -Vector3.UnitY;
            Vector3 up = MathF.Abs(Vector3.Dot(direction, Vector3.UnitY)) > 0.999f
                ? Vector3.UnitZ
                : Vector3.UnitY;
            Matrix4x4 spotView = Matrix4x4.CreateLookAt(light.Position, light.Position + direction, up);
            float farPlane = MathF.Max(light.Radius, 0.11f);
            float nearPlane = MathF.Min(MathF.Max(farPlane * 0.01f, 0.05f), farPlane * 0.25f);
            float fieldOfView = float.Clamp(light.OuterConeAngle * 2.0f, 0.02f, MathF.PI - 0.02f);
            Matrix4x4 spotProjection = Matrix4x4.CreatePerspectiveFieldOfView(fieldOfView, 1.0f, nearPlane, farPlane);
            Matrix4x4 matrix = spotView * spotProjection;
            _spotSpaceMatrices[slot] = matrix;

            if (!light.CastShadows)
                continue;

            ref LayerShadowCache cache = ref _spotShadowCache[slot];
            bool staticCacheDirty = !cache.Valid ||
                                    !ReferenceEquals(cache.Light, light) ||
                                    cache.SceneRevision != scene.StaticShadowRevision ||
                                    !MatrixApproximatelyEqual(cache.Matrix, matrix);

            if (staticCacheDirty)
            {
                _shadowShader.Use();
                _shadowShader.SetMat4("uLightSpaceMatrix", matrix);
                _staticSpotShadowMap.BindForWriting(slot);
                scene.RenderShadowCasters(_shadowShader, matrix, ShadowCasterFilter.Static);
                RenderSkinnedShadowCasters(scene, _skinnedShadowShader, matrix, ShadowCasterFilter.Static);

                cache.Light = light;
                cache.SceneRevision = scene.StaticShadowRevision;
                cache.Matrix = matrix;
                cache.Valid = true;
            }

            _staticSpotShadowMap.CopyLayerTo(_spotShadowMap, slot);
            _spotShadowMap.BindForWriting(slot, clear: false);
            _shadowShader.Use();
            _shadowShader.SetMat4("uLightSpaceMatrix", matrix);
            scene.RenderShadowCasters(_shadowShader, matrix, ShadowCasterFilter.Dynamic);
            RenderSkinnedShadowCasters(scene, _skinnedShadowShader, matrix, ShadowCasterFilter.Dynamic);
        }
    }

    private void RenderPointShadowPass(Scene scene, int shadowPointCount)
    {
        PrepareShadowRenderState();
        _gl.Disable(EnableCap.CullFace);

        for (int slot = 0; slot < shadowPointCount; slot++)
        {
            Light light = _shadowPointLights[slot];
            float farPlane = MathF.Max(light.Radius, 0.11f);
            float nearPlane = MathF.Min(MathF.Max(farPlane * 0.01f, 0.05f), farPlane * 0.25f);
            Matrix4x4 projection = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI * 0.5f, 1.0f, nearPlane, farPlane);

            ref PointShadowCache cache = ref _pointShadowCache[slot];
            bool staticCacheDirty = !cache.Valid ||
                                    !ReferenceEquals(cache.Light, light) ||
                                    cache.SceneRevision != scene.StaticShadowRevision ||
                                    Vector3.DistanceSquared(cache.Position, light.Position) > 1e-8f ||
                                    MathF.Abs(cache.Radius - light.Radius) > 1e-5f;

            _pointShadowShader.Use();
            _pointShadowShader.SetVec3("uLightPos", light.Position);
            _pointShadowShader.SetFloat("uRadius", farPlane);
            _skinnedPointShadowShader.Use();
            _skinnedPointShadowShader.SetVec3("uLightPos", light.Position);
            _skinnedPointShadowShader.SetFloat("uRadius", farPlane);

            if (staticCacheDirty)
            {
                for (int face = 0; face < 6; face++)
                {
                    Matrix4x4 matrix = CreatePointShadowMatrix(light.Position, projection, face);
                    _pointShadowShader.Use();
                    _pointShadowShader.SetMat4("uLightSpaceMatrix", matrix);
                    _staticPointShadowMaps[slot].BindForWriting(face);
                    scene.RenderShadowCasters(_pointShadowShader, matrix, ShadowCasterFilter.Static);
                    RenderSkinnedShadowCasters(scene, _skinnedPointShadowShader, matrix, ShadowCasterFilter.Static);
                }

                cache.Light = light;
                cache.SceneRevision = scene.StaticShadowRevision;
                cache.Position = light.Position;
                cache.Radius = light.Radius;
                cache.Valid = true;
            }

            for (int face = 0; face < 6; face++)
            {
                Matrix4x4 matrix = CreatePointShadowMatrix(light.Position, projection, face);
                _staticPointShadowMaps[slot].CopyFaceTo(_pointShadowMaps[slot], face);
                _pointShadowMaps[slot].BindForWriting(face, clear: false);
                _pointShadowShader.Use();
                _pointShadowShader.SetMat4("uLightSpaceMatrix", matrix);
                scene.RenderShadowCasters(_pointShadowShader, matrix, ShadowCasterFilter.Dynamic);
                RenderSkinnedShadowCasters(scene, _skinnedPointShadowShader, matrix, ShadowCasterFilter.Dynamic);
            }
        }

        _gl.Enable(EnableCap.CullFace);
        _gl.CullFace(GLEnum.Back);
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

        _gl.Enable(EnableCap.PolygonOffsetFill);
        _gl.PolygonOffset(0.75f, 1.0f);
    }

    private void CalculateCascadeSplits(float nearPlane, float farPlane, float[] destination)
    {
        nearPlane = MathF.Max(nearPlane, 0.001f);
        farPlane = MathF.Max(farPlane, nearPlane + 0.001f);
        float lambda = float.Clamp(CascadeSplitLambda, 0.0f, 1.0f);
        for (int cascade = 1; cascade <= CascadeCount; cascade++)
        {
            float ratio = (float)cascade / CascadeCount;
            float logarithmic = nearPlane * MathF.Pow(farPlane / nearPlane, ratio);
            float uniform = nearPlane + (farPlane - nearPlane) * ratio;
            destination[cascade - 1] = uniform + (logarithmic - uniform) * lambda;
        }
        destination[CascadeCount - 1] = farPlane;
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

    private int SelectLights(
        IReadOnlyList<Light> lights,
        LightType type,
        Light[] destination,
        int max,
        Camera camera)
    {
        int candidateCount = 0;
        float retentionMultiplier = 1.0f + float.Clamp(LightSelectionHysteresis, 0.0f, 0.75f);

        for (int i = 0; i < lights.Count; i++)
        {
            Light light = lights[i];
            if (!light.Enabled || light.Type != type)
                continue;

            float score = CalculateVisualInfluence(light, camera);
            if (ContainsReference(destination, light))
                score *= retentionMultiplier;
            if (candidateCount < MaxLightCandidates)
            {
                _lightCandidates[candidateCount++] = new LightCandidate { Light = light, Score = score };
            }
            else
            {
                int weakest = 0;
                for (int candidate = 1; candidate < candidateCount; candidate++)
                {
                    if (_lightCandidates[candidate].Score < _lightCandidates[weakest].Score)
                        weakest = candidate;
                }
                if (score > _lightCandidates[weakest].Score)
                    _lightCandidates[weakest] = new LightCandidate { Light = light, Score = score };
            }
        }

        SortLightCandidates(candidateCount);

        int result = System.Math.Min(candidateCount, max);
        for (int i = 0; i < result; i++)
            destination[i] = _lightCandidates[i].Light;
        for (int i = result; i < destination.Length; i++)
            destination[i] = null!;
        return result;
    }

    private int SelectShadowedPointLights(
        ReadOnlySpan<Light> selectedPointLights,
        Light[] destination,
        int max,
        Camera camera)
    {
        int candidateCount = 0;
        float retentionMultiplier = 1.0f + float.Clamp(LightSelectionHysteresis, 0.0f, 0.75f);
        for (int i = 0; i < selectedPointLights.Length; i++)
        {
            Light light = selectedPointLights[i];
            if (!light.CastShadows)
                continue;

            float score = CalculateVisualInfluence(light, camera);
            if (ContainsReference(destination, light))
                score *= retentionMultiplier;
            _lightCandidates[candidateCount++] = new LightCandidate { Light = light, Score = score };
        }

        SortLightCandidates(candidateCount);
        int winnerCount = System.Math.Min(candidateCount, max);
        Array.Clear(_shadowPointSelectionScratch);
        Span<bool> winnerUsed = stackalloc bool[MaxShadowedPointLightSlots];

        // Preserve the physical cubemap slot of incumbents that still won. This
        // prevents cache invalidation and visible shadow-map swaps at boundaries.
        for (int slot = 0; slot < winnerCount; slot++)
        {
            Light incumbent = destination[slot];
            if (incumbent == null) continue;
            for (int winner = 0; winner < winnerCount; winner++)
            {
                if (!winnerUsed[winner] && ReferenceEquals(_lightCandidates[winner].Light, incumbent))
                {
                    _shadowPointSelectionScratch[slot] = incumbent;
                    winnerUsed[winner] = true;
                    break;
                }
            }
        }

        for (int slot = 0; slot < winnerCount; slot++)
        {
            if (_shadowPointSelectionScratch[slot] != null) continue;
            for (int winner = 0; winner < winnerCount; winner++)
            {
                if (winnerUsed[winner]) continue;
                _shadowPointSelectionScratch[slot] = _lightCandidates[winner].Light;
                winnerUsed[winner] = true;
                break;
            }
        }

        for (int i = 0; i < winnerCount; i++)
            destination[i] = _shadowPointSelectionScratch[i];
        for (int i = winnerCount; i < destination.Length; i++)
            destination[i] = null!;
        return winnerCount;
    }

    private static bool ContainsReference(ReadOnlySpan<Light> lights, Light target)
    {
        for (int i = 0; i < lights.Length; i++)
        {
            if (ReferenceEquals(lights[i], target))
                return true;
        }
        return false;
    }

    private static bool ContainsReference(Light[] lights, Light target) =>
        ContainsReference(lights.AsSpan(), target);

    private void SortLightCandidates(int candidateCount)
    {
        // The candidate set is intentionally small; insertion sort is allocation-free
        // and keeps ordering deterministic for equal scores.
        for (int i = 1; i < candidateCount; i++)
        {
            LightCandidate candidate = _lightCandidates[i];
            int j = i - 1;
            while (j >= 0 && _lightCandidates[j].Score < candidate.Score)
            {
                _lightCandidates[j + 1] = _lightCandidates[j];
                j--;
            }
            _lightCandidates[j + 1] = candidate;
        }
    }

    private static float CalculateVisualInfluence(Light light, Camera camera)
    {
        Vector3 cameraToLight = light.Position - camera.Position;
        float distanceSquared = MathF.Max(cameraToLight.LengthSquared(), 0.25f);
        float radiusSquared = MathF.Max(light.Radius * light.Radius, 0.01f);
        float spatialInfluence = radiusSquared /
            MathF.Max(distanceSquared, radiusSquared * 0.0625f);

        float coneInfluence = 1.0f;
        if (light.Type == LightType.Spot && cameraToLight.LengthSquared() > 1e-8f)
        {
            Vector3 lightToCamera = -Vector3.Normalize(cameraToLight);
            Vector3 direction = light.Direction.LengthSquared() > 1e-8f
                ? Vector3.Normalize(light.Direction)
                : -Vector3.UnitY;
            coneInfluence = float.Clamp((Vector3.Dot(direction, lightToCamera) - light.OuterCos) /
                MathF.Max(1.0f - light.OuterCos, 0.001f), 0.15f, 1.0f);
        }

        float luminance = light.Color.X * 0.2126f +
                          light.Color.Y * 0.7152f +
                          light.Color.Z * 0.0722f;
        float shadowPriority = light.CastShadows ? 1.15f : 1.0f;
        return MathF.Max(light.Intensity * MathF.Max(luminance, 0.001f), 0.001f) *
               spatialInfluence * coneInfluence * shadowPriority;
    }

    private void SetupWorldUniforms(Shader shader, Matrix4x4 view, Matrix4x4 proj)
    {
        shader.SetMat4("uView", view);
        shader.SetMat4("uProj", proj);

        shader.SetVec3("uColor", Vector3.One);
        shader.SetBool("uUseTexture", true);
        shader.SetInt("uMaterialAlphaMode", 0);
        shader.SetFloat("uMaterialAlphaCutoff", 0.5f);
        shader.SetBool("uMaterialReceiveShadows", true);
        shader.SetFloat("uIsViewmodel", 0.0f);
        shader.SetInt("uDebugView", _postPipeline.Settings.DebugView);
        shader.SetBool("uOutputSrgb", !_postPipeline.Settings.Enabled);
        if (_forwardPlusLighting != null)
            _forwardPlusLighting.ConfigureShader(shader);
        else
        {
            shader.SetBool("uUseForwardPlus", false);
            shader.SetInt("uForwardPlusTileCountX", 1);
            shader.SetInt("uForwardPlusTileCountY", 1);
            shader.SetInt("uForwardPlusPointCount", 0);
            shader.SetInt("uForwardPlusSpotCount", 0);
        }

        shader.SetInt("uTexture", 0);
        shader.SetInt("uShadowMap", 1);
        shader.SetInt("uSpotShadowMap", 2);
        shader.SetInt("uPointShadowMap0", 3);
        shader.SetInt("uPointShadowMap1", 4);
        shader.SetInt("uPointShadowMap2", 5);
        shader.SetInt("uPointShadowMap3", 6);
        _shadowMap.BindForReading(TextureUnit.Texture1);
        _spotShadowMap.BindForReading(TextureUnit.Texture2);
        _pointShadowMaps[0].BindForReading(TextureUnit.Texture3);
        _pointShadowMaps[1].BindForReading(TextureUnit.Texture4);
        _pointShadowMaps[2].BindForReading(TextureUnit.Texture5);
        _pointShadowMaps[3].BindForReading(TextureUnit.Texture6);
        if (_imageBasedLighting != null)
            _imageBasedLighting.Bind(shader);
        else
        {
            shader.SetBool("uUseIbl", false);
            shader.SetFloat("uIblIntensity", 1.0f);
        }
        _cloudRenderer?.BindWorldShadow(shader, _cloudSettings);
    }

    private unsafe void UploadBones(Matrix4x4[] bones)
    {
        if (bones.Length == 0) return;

        if (_sharedBonesSSBO == 0)
            _sharedBonesSSBO = _gl.GenBuffer();

        if (_sharedTransposedMatrices.Length < bones.Length)
            _sharedTransposedMatrices = new Matrix4x4[bones.Length];

        for (int i = 0; i < bones.Length; i++)
            _sharedTransposedMatrices[i] = Matrix4x4.Transpose(bones[i]);

        _gl.BindBuffer(GLEnum.ShaderStorageBuffer, _sharedBonesSSBO);
        fixed (Matrix4x4* ptr = _sharedTransposedMatrices)
        {
            _gl.BufferData(GLEnum.ShaderStorageBuffer, (nuint)(bones.Length * sizeof(Matrix4x4)), ptr, GLEnum.DynamicDraw);
        }
        _gl.BindBufferBase(GLEnum.ShaderStorageBuffer, 0, _sharedBonesSSBO);
        _gl.BindBuffer(GLEnum.ShaderStorageBuffer, 0);
    }

    private int RenderSkinned(
        Scene scene,
        Shader legacyShader,
        Matrix4x4? cullMatrix = null,
        Matrix4x4? view = null,
        Matrix4x4? proj = null,
        bool skipViewmodels = false)
    {
        ViewFrustum? frustum = cullMatrix.HasValue ? new ViewFrustum(cullMatrix.Value) : null;
        Shader? activeShader = null;
        bool cullEnabled = true;
        bool blendEnabled = false;
        int objectsDrawn = 0;
        foreach (var e in scene.Entities)
        {
            if (!e.Visible || e.SkinnedModel == null || e.Animator == null) continue;
            if (skipViewmodels && e.IsViewmodel) continue;
            if (!e.IsViewmodel && frustum.HasValue && !frustum.Value.Intersects(e.GetWorldRenderBounds()))
                continue;

            Matrix4x4 modelMatrix = e.RenderMatrix;
            UploadBones(e.Animator.FinalBoneMatrices);
            bool objectDrawn = false;

            for (int submeshIndex = 0; submeshIndex < e.SkinnedModel.Submeshes.Length; submeshIndex++)
            {
                var sub = e.SkinnedModel.Submeshes[submeshIndex];
                if (e.SkinnedModel.HiddenSubmeshes.Contains(sub.Name))
                    continue;

                Materials.MaterialRuntime? material = sub.Material ?? e.ResolveMaterial(sub.MaterialSlot >= 0 ? sub.MaterialSlot : submeshIndex);
                Shader shader = material?.SkinnedShader ?? legacyShader;
                if (!ReferenceEquals(activeShader, shader))
                {
                    shader.Use();
                    if (view.HasValue && proj.HasValue)
                        SetupWorldUniforms(shader, view.Value, proj.Value);
                    activeShader = shader;
                }

                bool wantsCull = material?.Asset.TwoSided != true;
                if (wantsCull != cullEnabled)
                {
                    if (wantsCull) _gl.Enable(EnableCap.CullFace);
                    else _gl.Disable(EnableCap.CullFace);
                    cullEnabled = wantsCull;
                }

                bool wantsBlend = material?.Asset.AlphaMode == Materials.MaterialAlphaMode.Blend;
                if (wantsBlend != blendEnabled)
                {
                    if (wantsBlend)
                    {
                        _gl.Enable(EnableCap.Blend);
                        _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
                        _gl.DepthMask(false);
                    }
                    else
                    {
                        _gl.Disable(EnableCap.Blend);
                        _gl.DepthMask(true);
                    }
                    blendEnabled = wantsBlend;
                }

                shader.SetMat4("uModel", modelMatrix);
                shader.SetVec2("uUvScale", e.UvScale);
                shader.SetVec2("uUvOffset", e.UvOffset);
                shader.SetFloat("uUvRotation", e.UvRotation);
                shader.SetFloat("uIsViewmodel", e.IsViewmodel ? 1.0f : 0.0f);

                if (material != null)
                {
                    material.Bind(shader);
                    shader.SetBool("uIsEmissive", false);
                }
                else
                {
                    var tex = sub.Texture ?? e.Texture ?? _crateTexture;
                    shader.SetBool("uUseTexture", tex != null);
                    shader.SetInt("uMaterialAlphaMode", 0);
                    shader.SetBool("uMaterialReceiveShadows", true);
                    tex?.Bind(0);
                }

                sub.Mesh.Draw();
                objectDrawn = true;
            }

            if (objectDrawn)
                objectsDrawn++;
        }

        if (!cullEnabled)
            _gl.Enable(EnableCap.CullFace);
        if (blendEnabled)
        {
            _gl.Disable(EnableCap.Blend);
            _gl.DepthMask(true);
        }

        return objectsDrawn;
    }

    private void RenderSkinnedShadowCasters(
        Scene scene,
        Shader shader,
        Matrix4x4 cullMatrix,
        ShadowCasterFilter filter)
    {
        var frustum = new ViewFrustum(cullMatrix);
        IReadOnlyList<Entity> casters = scene.GetShadowCasters(filter);

        for (int entityIndex = 0; entityIndex < casters.Count; entityIndex++)
        {
            Entity entity = casters[entityIndex];
            if (entity.SkinnedModel == null || entity.Animator == null || entity.IsViewmodel)
                continue;
            if (!frustum.Intersects(entity.GetWorldRenderBounds()))
                continue;

            shader.Use();
            shader.SetMat4("uLightSpaceMatrix", cullMatrix);
            shader.SetMat4("uModel", entity.RenderMatrix);
            shader.SetVec2("uUvScale", entity.UvScale);
            shader.SetVec2("uUvOffset", entity.UvOffset);
            shader.SetFloat("uUvRotation", entity.UvRotation);
            UploadBones(entity.Animator.FinalBoneMatrices);

            for (int submeshIndex = 0; submeshIndex < entity.SkinnedModel.Submeshes.Length; submeshIndex++)
            {
                var submesh = entity.SkinnedModel.Submeshes[submeshIndex];
                var material = submesh.Material ?? entity.ResolveMaterial(submesh.MaterialSlot >= 0 ? submesh.MaterialSlot : submeshIndex);
                if (material?.Asset.CastShadows == false)
                    continue;
                if (material != null)
                    material.BindShadow(shader);
                else
                    shader.SetBool("uShadowAlphaMask", false);
                if (!entity.SkinnedModel.HiddenSubmeshes.Contains(submesh.Name))
                    submesh.Mesh.Draw();
            }
        }
    }

    public unsafe void RenderBillboard(Matrix4x4 view, Matrix4x4 proj, uint texture, Vector3 worldPos, Vector2 size, Vector4 color)
    {
        _gl.Enable(GLEnum.Blend);
        _gl.BlendFunc(GLEnum.SrcAlpha, GLEnum.OneMinusSrcAlpha);

        _gl.UseProgram(_bbShader.ID);

        float[] viewArr = [
            view.M11, view.M12, view.M13, view.M14,
            view.M21, view.M22, view.M23, view.M24,
            view.M31, view.M32, view.M33, view.M34,
            view.M41, view.M42, view.M43, view.M44,
        ];
        float[] projArr = [
            proj.M11, proj.M12, proj.M13, proj.M14,
            proj.M21, proj.M22, proj.M23, proj.M24,
            proj.M31, proj.M32, proj.M33, proj.M34,
            proj.M41, proj.M42, proj.M43, proj.M44,
        ];
        fixed (float* vp = viewArr, pp = projArr)
        {
            _gl.UniformMatrix4(_bbUView, 1, false, vp);
            _gl.UniformMatrix4(_bbUProj, 1, false, pp);
        }
        _gl.Uniform3(_bbUWorldPos, worldPos.X, worldPos.Y, worldPos.Z);
        _gl.Uniform2(_bbUSize, size.X, size.Y);
        _gl.Uniform4(_bbUColor, color.X, color.Y, color.Z, color.W);
        _gl.Uniform1(_bbUTexture, 0);
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, texture);

        _gl.BindVertexArray(_bbVao);
        _gl.DrawArrays(GLEnum.Triangles, 0, 6);
        _gl.BindVertexArray(0);

        _gl.Disable(GLEnum.Blend);
    }

    private void RenderDecals(
        Matrix4x4 view,
        Matrix4x4 proj,
        uint targetFbo)
    {
        if (_decalQueue.Count == 0) return;

        // A cena SEMPRE é renderizada no HDR FBO, então o depth copy
        // precisa do formato DepthComponent32f independente do post-process.
        uint decalDepthFbo = _decalDepthFboHdr;
        uint decalDepthTex = _decalDepthTexHdr;

        // 1. Copy the depth buffer.
        _gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, targetFbo);
        _gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, decalDepthFbo);
        _gl.BlitFramebuffer(0, 0, _scrWidth, _scrHeight, 0, 0, _scrWidth, _scrHeight,
            ClearBufferMask.DepthBufferBit, BlitFramebufferFilter.Nearest);

        // 2. Copy the receiver's final normal and PBR parameters to separate
        // textures. Sampling an attachment of targetFbo while rendering into
        // targetFbo would create an OpenGL framebuffer feedback loop.
        _gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, targetFbo);
        _gl.ReadBuffer(ReadBufferMode.ColorAttachment2);
        _gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, _decalSurfaceFboHdr);
        _gl.DrawBuffer(DrawBufferMode.ColorAttachment0);
        _gl.BlitFramebuffer(0, 0, _scrWidth, _scrHeight, 0, 0, _scrWidth, _scrHeight,
            ClearBufferMask.ColorBufferBit, BlitFramebufferFilter.Nearest);

        _gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, targetFbo);
        _gl.ReadBuffer(ReadBufferMode.ColorAttachment3);
        _gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, _decalSurfaceFboHdr);
        _gl.DrawBuffer(DrawBufferMode.ColorAttachment1);
        _gl.BlitFramebuffer(0, 0, _scrWidth, _scrHeight, 0, 0, _scrWidth, _scrHeight,
            ClearBufferMask.ColorBufferBit, BlitFramebufferFilter.Nearest);

        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, targetFbo);
        _gl.ReadBuffer(ReadBufferMode.ColorAttachment0);

        // 3. Setup GL State para Box Decal Projection
        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        _gl.Disable(EnableCap.DepthTest);
        _gl.DepthMask(false);
        _gl.Enable(EnableCap.CullFace);
        _gl.CullFace(GLEnum.Front); // Render back faces do cubo para que a projeção funcione mesmo com a câmera dentro do cubo

        _decalShader.Use();
        SetupWorldUniforms(_decalShader, view, proj);

        Matrix4x4 viewProj = view * proj;
        Matrix4x4.Invert(viewProj, out Matrix4x4 invViewProj);

        _decalShader.SetMat4("uInvViewProj", invViewProj);
        _decalShader.SetVec2("uScreenSize", new Vector2(_scrWidth, _scrHeight));

        // Bind depth copy texture on unit 0.
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, decalDepthTex);
        _decalShader.SetInt("uDepthTex", 0);

        // Bind receiver surface data on units outside the lighting/IBL range.
        _gl.ActiveTexture(TextureUnit.Texture0 + 10);
        _gl.BindTexture(TextureTarget.Texture2D, _decalNormalTexHdr);
        _decalShader.SetInt("uReceiverNormal", 10);

        _gl.ActiveTexture(TextureUnit.Texture0 + 11);
        _gl.BindTexture(TextureTarget.Texture2D, _decalMaterialTexHdr);
        _decalShader.SetInt("uReceiverMaterial", 11);

        for (int i = 0; i < _decalQueue.Count; i++)
        {
            var decal = _decalQueue[i];
            float ageRatio = decal.Age / decal.LifeTime;
            float opacity = 1.0f;
            if (ageRatio > decal.FadeStart)
                opacity = 1.0f - (ageRatio - decal.FadeStart) / (1.0f - decal.FadeStart);

            _decalShader.SetFloat("uOpacity", opacity);
            _decalShader.SetMat4("uModel", decal.ModelMatrix);
            _decalShader.SetMat4("uInvDecalModel", decal.InvModelMatrix);

            _gl.ActiveTexture(TextureUnit.Texture0 + 12);
            _gl.BindTexture(TextureTarget.Texture2D, decal.AlbedoTexture);
            _decalShader.SetInt("uDecalAlbedo", 12);


            _skyBoxCubeMesh.Draw();
        }

        _gl.ActiveTexture(TextureUnit.Texture0 + 12);
        _gl.BindTexture(TextureTarget.Texture2D, 0);

        _gl.ActiveTexture(TextureUnit.Texture0 + 11);
        _gl.BindTexture(TextureTarget.Texture2D, 0);

        _gl.ActiveTexture(TextureUnit.Texture0 + 10);
        _gl.BindTexture(TextureTarget.Texture2D, 0);

        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, 0);

        _gl.CullFace(GLEnum.Back);
        _gl.Enable(EnableCap.DepthTest);
        _gl.DepthMask(true);
        _gl.DepthFunc(DepthFunction.Less);
        _gl.Disable(EnableCap.Blend);
    }




    private static void GetFrustumCornersWorldSpace(
        Matrix4x4 projection,
        Matrix4x4 view,
        Span<Vector3> destination)
    {
        if (destination.Length < 8)
            throw new ArgumentException("Frustum corner destination must contain at least 8 elements.", nameof(destination));
        Matrix4x4.Invert(view * projection, out Matrix4x4 inverseViewProjection);

        int index = 0;
        for (int x = 0; x < 2; ++x)
        {
            for (int y = 0; y < 2; ++y)
            {
                for (int z = 0; z < 2; ++z)
                {
                    Vector4 point = Vector4.Transform(new Vector4(
                        2.0f * x - 1.0f,
                        2.0f * y - 1.0f,
                        (float)z,
                        1.0f), inverseViewProjection);
                    point /= point.W;
                    destination[index++] = new Vector3(point.X, point.Y, point.Z);
                }
            }
        }
    }

    private Matrix4x4 GetLightSpaceMatrix(
        Scene scene,
        Camera camera,
        float aspect,
        float near,
        float far,
        Vector3 lightDirection,
        out float texelWorldSize)
    {
        Matrix4x4 projection = Matrix4x4.CreatePerspectiveFieldOfView(
            float.DegreesToRadians(camera.FOV), aspect, near, far);
        Matrix4x4 view = camera.GetViewMatrix();
        Span<Vector3> frustumCorners = stackalloc Vector3[8];
        GetFrustumCornersWorldSpace(projection, view, frustumCorners);

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

        texelWorldSize = radius * 2.0f / _shadowMap.Width;
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
        Vector3 eyeLightSpace = new(centerLightSpace.X, centerLightSpace.Y, maxZ);
        Vector3 targetLightSpace = new(centerLightSpace.X, centerLightSpace.Y, maxZ - 1.0f);
        Vector3 eye = Vector3.Transform(eyeLightSpace, inverseBaseView);
        Vector3 target = Vector3.Transform(targetLightSpace, inverseBaseView);

        Matrix4x4 lightView = Matrix4x4.CreateLookAt(eye, target, up);
        float nearDistance = MathF.Max(ShadowNearPlane, 0.01f);
        float farDistance = MathF.Max(nearDistance + 0.01f, maxZ - minZ);
        Matrix4x4 lightProjection = Matrix4x4.CreateOrthographicOffCenter(
            -radius, radius, -radius, radius, nearDistance, farDistance);
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
            if (!bounds.IsValid) continue;
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

    private void InitSsao()
    {
        // --- Kernel de 64 amostras hemisféricas ---
        var random = new Random(42); // seed fixa para consistência
        _ssaoKernel = new Vector3[64];
        for (int i = 0; i < 64; i++)
        {
            Vector3 sample = new(
                random.NextSingle() * 2.0f - 1.0f,
                random.NextSingle() * 2.0f - 1.0f,
                random.NextSingle()); // Z >= 0 (hemisfério)
            sample = Vector3.Normalize(sample);
            sample *= random.NextSingle();

            float lerpFactor = (float)i / 64.0f;
            lerpFactor = MathUtils.Lerp(0.1f, 1.0f, lerpFactor * lerpFactor);
            sample *= lerpFactor;
            _ssaoKernel[i] = sample;
        }

        // --- Textura de ruído 4x4 ---
        Vector3[] noiseData = new Vector3[16];
        for (int i = 0; i < 16; i++)
        {
            noiseData[i] = new Vector3(
                random.NextSingle() * 2.0f - 1.0f,
                random.NextSingle() * 2.0f - 1.0f,
                0.0f);
        }

        _ssaoNoiseTex = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, _ssaoNoiseTex);
        unsafe
        {
            fixed (Vector3* ptr = noiseData)
            {
                _gl.TexImage2D(TextureTarget.Texture2D, 0, (int)InternalFormat.Rgb16f,
                    4, 4, 0, PixelFormat.Rgb, PixelType.Float, ptr);
            }
        }
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Nearest);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Nearest);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.Repeat);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.Repeat);
        _gl.BindTexture(TextureTarget.Texture2D, 0);
    }

    public void Dispose()
    {
        if (_ssaoNoiseTex != 0) { _gl.DeleteTexture(_ssaoNoiseTex); _ssaoNoiseTex = 0; }
        if (_sharedBonesSSBO != 0) { _gl.DeleteBuffer(_sharedBonesSSBO); _sharedBonesSSBO = 0; }
        _forwardPlusLighting?.Dispose();
        _lightingBuffer?.Dispose();
        _imageBasedLighting?.Dispose();
        _shadowMap?.Dispose();
        _staticShadowMap?.Dispose();
        _spotShadowMap?.Dispose();
        _staticSpotShadowMap?.Dispose();
        if (_pointShadowMaps != null)
        {
            foreach (PointShadowMap map in _pointShadowMaps)
                map.Dispose();
        }
        if (_staticPointShadowMaps != null)
        {
            foreach (PointShadowMap map in _staticPointShadowMaps)
                map.Dispose();
        }
        DestroyDecalDepthResources();
        _fogRenderer?.Dispose();
        _cloudRenderer?.Dispose();
        _oceanRenderer?.Dispose();
        _postPipeline?.Dispose();
    }
}
