using System.Collections.Generic;
using System.Numerics;
using Silk.NET.OpenGL;
using Fuse.Core;
using Fuse.Renderer.PostProcess;
using Fuse.Math;

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
    private ShadowMap _spotShadowMap = null!;
    private Shader _pointShadowShader = null!;
    private PointShadowMap _pointShadowMap0 = null!;
    private PointShadowMap _pointShadowMap1 = null!;
    private PointShadowMap _pointShadowMap2 = null!;
    private PointShadowMap _pointShadowMap3 = null!;

    // billboard
    private uint _bbShader;
    private int _bbUView, _bbUProj, _bbUWorldPos, _bbUSize, _bbUColor, _bbUTexture;
    private uint _bbVao, _bbVbo;

    // Decal Shader (Box Projected Decals)
    private Shader _decalShader = null!;
    private uint _decalDepthFboHdr;     // DepthComponent32f — para HdrFbo (post-process ON)
    private uint _decalDepthTexHdr;
    private uint _decalDepthFboDefault; // DepthComponent24  — para FBO 0 (post-process OFF)
    private uint _decalDepthTexDefault;

    // Shared bone SSBO (all skinned shaders read from binding point 0)
    private uint _sharedBonesSSBO;
    private Matrix4x4[] _sharedTransposedMatrices = [];

    // Post-Process
    private PostProcessPipeline _postPipeline = null!;
    private PostProcessSettings _postSettings = new();
    public PostProcessPipeline PostPipeline => _postPipeline;
    private Matrix4x4 _prevViewProj = Matrix4x4.Identity;

    // SSAO
    private Vector3[] _ssaoKernel = [];
    private uint _ssaoNoiseTex;
    public uint SsaoNoiseTex => _ssaoNoiseTex;

    // Pre-allocated light buffers (zero LINQ allocations per frame)
    private readonly Light[] _allLightsBuf = new Light[16];

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
    public Texture SkyboxTexture => _skyboxTexture;
    public void SetSkyboxTexture(Texture tex)
    {
        _skyboxTexture = tex;
        if (_skyboxTexture.ID != 0)
        {
            _skyboxDominantColor = _skyboxTexture.GetDominantColor();
            _gl.BindTexture(TextureTarget.Texture2D, _skyboxTexture.ID);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
        }
    }

    // Meshes
    private Mesh _skyBoxCubeMesh = null!;

    // Shadow Settings
    public uint ShadowResolution = 512;
    public float ShadowBiasFactor = 0.0f;
    public float ShadowBiasBase = 0.000000f;
    public float ShadowNearPlane = 0.0f;
    public float ShadowFarPlane = 23.0f;
    public float ShadowSpread = 1.0f;
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

        _shader = assets.GetShader(Bible.Shader(Bible.ShaderDefaultVert), Bible.Shader(Bible.ShaderDefaultFrag))!;
        _skyboxShader = assets.GetShader(Bible.Shader(Bible.ShaderSkyboxVert), Bible.Shader(Bible.ShaderSkyboxFrag))!;
        _shadowShader = assets.GetShader(Bible.Shader(Bible.ShaderShadowVert), Bible.Shader(Bible.ShaderShadowFrag))!;
        _pointShadowShader = assets.GetShader(Bible.Shader(Bible.ShaderPointShadowVert), Bible.Shader(Bible.ShaderPointShadowFrag))!;
        _skinnedShader = assets.GetShader(Bible.Shader(Bible.ShaderSkinnedVert), Bible.Shader(Bible.ShaderDefaultFrag))!;
        _skinnedShadowShader = assets.GetShader(Bible.Shader(Bible.ShaderSkinnedShadowVert), Bible.Shader(Bible.ShaderShadowFrag))!;
        _skinnedPointShadowShader = assets.GetShader(Bible.Shader(Bible.ShaderPointShadowSkinnedVert), Bible.Shader(Bible.ShaderPointShadowFrag))!;
        
        _shadowMap = new ShadowMap(_gl, ShadowResolution * 2, ShadowResolution * 2);
        _spotShadowMap = new ShadowMap(_gl, ShadowResolution, ShadowResolution, 4);
        _pointShadowMap0 = new PointShadowMap(_gl, ShadowResolution);
        _pointShadowMap1 = new PointShadowMap(_gl, ShadowResolution);
        _pointShadowMap2 = new PointShadowMap(_gl, ShadowResolution);
        _pointShadowMap3 = new PointShadowMap(_gl, ShadowResolution);
        
        _skyBoxCubeMesh = assets.GetMesh("cube")!;
        
        _crateTexture = assets.GetTexture(Bible.Tex(Bible.Crate));
        _skyboxTexture = assets.GetTexture(Bible.Tex(Bible.Skybox));
        
        if (_skyboxTexture.ID != 0)
        {
            _skyboxDominantColor = _skyboxTexture.GetDominantColor();
            _gl.BindTexture(TextureTarget.Texture2D, _skyboxTexture.ID);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
        }

        // Billboard shader (muzzle flash) - carregar de arquivos
        _bbShader = assets.GetShader(Bible.Shader(Bible.ShaderBillboardVert), Bible.Shader(Bible.ShaderBillboardFrag))!.ID;
        _bbUView = _gl.GetUniformLocation(_bbShader, "uView");
        _bbUProj = _gl.GetUniformLocation(_bbShader, "uProj");
        _bbUWorldPos = _gl.GetUniformLocation(_bbShader, "uWorldPos");
        _bbUSize = _gl.GetUniformLocation(_bbShader, "uSize");
        _bbUColor = _gl.GetUniformLocation(_bbShader, "uColor");
        _bbUTexture = _gl.GetUniformLocation(_bbShader, "uTexture");

        // Decal Shader (Box Projected Decals)
        _decalShader = assets.GetShader(Bible.Shader(Bible.ShaderDecalVert), Bible.Shader(Bible.ShaderDecalFrag))!;
        CreateDecalDepthResources();



        // Post-Process Pipeline
        _postPipeline = new PostProcessPipeline(_gl, assets, _scrWidth, _scrHeight);
        _postSettings = _postPipeline.Settings;

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
        if (_decalDepthFboDefault != 0) { _gl.DeleteFramebuffer(_decalDepthFboDefault); _decalDepthFboDefault = 0; }
        if (_decalDepthTexDefault != 0) { _gl.DeleteTexture(_decalDepthTexDefault); _decalDepthTexDefault = 0; }
    }

    public void Resize(int width, int height)
    {
        _scrWidth = width;
        _scrHeight = height;
        _postPipeline?.Resize(width, height);
        CreateDecalDepthResources();
    }


    public void RenderFrame(Scene scene, Camera camera, Physics.PhysicsWorld physics)
    {
        float aspect = (float)_scrWidth / _scrHeight;
        var view = camera.GetViewMatrix();
        var proj = camera.GetProjectionMatrix(aspect);

        // --- 0. Estado limpo no início do frame ---
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        _gl.Viewport(0, 0, (uint)_scrWidth, (uint)_scrHeight);
        _gl.ClearColor(0.0f, 0.0f, 0.0f, 1.0f);
        _gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

        // --- 0. Update Physics and Hierarchy ---
        scene.UpdateTransforms(physics);

        // Update Decals (age, cull expired)
        _decalQueue.ForEach(d => { /* age handled in UpdateDecals */});
        // We'll call UpdateDecals from Application.cs instead

        // ===== RENDER CENA NO HDR FBO (shadow pass usa framebuffer próprio) =====

        // --- 1. Shadow Pass ---
        Light? dirLight = null;
        for (int i = 0; i < scene.Lights.Count; i++)
        {
            var l = scene.Lights[i];
            if (l.Enabled && l.Type == LightType.Directional) { dirLight = l; break; }
        }
        Vector3 lightDir = dirLight != null ? -Vector3.Normalize(dirLight.Direction) : Vector3.Normalize(new Vector3(1, 2, 1));
        bool renderDirShadows = dirLight != null && dirLight.CastShadows && ShadowsEnabled;
        
        float[] cascadeLevels = { ShadowFarPlane * 0.05f, ShadowFarPlane * 0.2f, ShadowFarPlane };
        Matrix4x4[] lightSpaceMatrices = new Matrix4x4[3];

        if (_shadowShader != null && _shadowShader.ID != 0 && renderDirShadows)
        {
            _gl.Enable(EnableCap.DepthTest);
            _gl.Enable(EnableCap.CullFace);
            _gl.CullFace(GLEnum.Back); // Use Back instead of Front to prevent backface clipping issues

            for (int i = 0; i < 3; i++)
            {
                float near = i == 0 ? camera.NearPlane : cascadeLevels[i - 1];
                float far = cascadeLevels[i];
                lightSpaceMatrices[i] = GetLightSpaceMatrix(camera, aspect, near, far, lightDir);

                _shadowShader.Use();
                _shadowShader.SetMat4("uLightSpaceMatrix", lightSpaceMatrices[i]);
                _shadowMap.BindForWriting(i);
                scene.Render(_shadowShader, _crateTexture, lightSpaceMatrices[i]);

                _skinnedShadowShader.Use();
                _skinnedShadowShader.SetMat4("uLightSpaceMatrix", lightSpaceMatrices[i]);
                RenderSkinned(scene, _skinnedShadowShader, true);
            }
        }

        // --- 1.5. Spot Light Shadow Pass ---
        Light[] spotLights = new Light[4];
        int spotCount = FilterLights(scene.Lights, LightType.Spot, false, spotLights, 4, camera.Position);
        Matrix4x4[] spotSpaceMatrices = new Matrix4x4[4];

        if (_shadowShader != null && _shadowShader.ID != 0 && ShadowsEnabled)
        {
            _gl.Enable(EnableCap.DepthTest);
            _gl.Enable(EnableCap.CullFace);
            _gl.CullFace(GLEnum.Back);

            for (int i = 0; i < spotCount; i++)
            {
                var sl = spotLights[i];
                if (!sl.CastShadows) continue;

                var viewDir = Vector3.Normalize(sl.Direction);
                // Evitar erro de lookat caso dir seja vetor UP
                var up = MathF.Abs(Vector3.Dot(viewDir, Vector3.UnitY)) > 0.999f ? Vector3.UnitZ : Vector3.UnitY;
                var spotView = Matrix4x4.CreateLookAt(sl.Position, sl.Position + viewDir, up);

                var projSpot = Matrix4x4.CreatePerspectiveFieldOfView(sl.OuterConeAngle * 2.0f, 1.0f, 0.1f, sl.Radius);
                spotSpaceMatrices[i] = spotView * projSpot;

                _shadowShader.Use();
                _shadowShader.SetMat4("uLightSpaceMatrix", spotSpaceMatrices[i]);
                _spotShadowMap.BindForWriting(i);
                scene.Render(_shadowShader, _crateTexture, spotSpaceMatrices[i]);

                _skinnedShadowShader.Use();
                _skinnedShadowShader.SetMat4("uLightSpaceMatrix", spotSpaceMatrices[i]);
                RenderSkinned(scene, _skinnedShadowShader, true);
            }
        }

        // --- 1.7. Point Light Shadow Pass ---
        Light[] shadowPointLights = new Light[4];
        int shadowPointCount = FilterLights(scene.Lights, LightType.Point, true, shadowPointLights, 4, camera.Position);
        if (_pointShadowShader != null && _pointShadowShader.ID != 0 && ShadowsEnabled)
        {
            _gl.Enable(EnableCap.DepthTest);
            _gl.Enable(EnableCap.CullFace);
            _gl.CullFace(GLEnum.Back);

            for (int i = 0; i < shadowPointCount; i++)
            {
                var pl = shadowPointLights[i];
                var shadowMap = i == 0 ? _pointShadowMap0 : i == 1 ? _pointShadowMap1 : i == 2 ? _pointShadowMap2 : _pointShadowMap3;

                _pointShadowShader.Use();
                _pointShadowShader.SetVec3("uLightPos", pl.Position);
                _pointShadowShader.SetFloat("uRadius", pl.Radius);

                _skinnedPointShadowShader.Use();
                _skinnedPointShadowShader.SetVec3("uLightPos", pl.Position);
                _skinnedPointShadowShader.SetFloat("uRadius", pl.Radius);

                var targets = new Vector3[]
                {
                    new Vector3(1, 0, 0), new Vector3(-1, 0, 0),
                    new Vector3(0, 1, 0), new Vector3(0, -1, 0),
                    new Vector3(0, 0, 1), new Vector3(0, 0, -1)
                };
                var ups = new Vector3[]
                {
                    new Vector3(0, -1, 0), new Vector3(0, -1, 0),
                    new Vector3(0, 0, 1), new Vector3(0, 0, -1),
                    new Vector3(0, -1, 0), new Vector3(0, -1, 0)
                };

                var projPoint = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 2.0f, 1.0f, 0.1f, pl.Radius);

                for (int face = 0; face < 6; face++)
                {
                    var viewPoint = Matrix4x4.CreateLookAt(pl.Position, pl.Position + targets[face], ups[face]);
                    var lightSpaceMatrix = viewPoint * projPoint;

                    _pointShadowShader.Use();
                    _pointShadowShader.SetMat4("uLightSpaceMatrix", lightSpaceMatrix);
                    shadowMap.BindForWriting(face);
                    scene.Render(_pointShadowShader, _crateTexture, lightSpaceMatrix);

                    _skinnedPointShadowShader.Use();
                    _skinnedPointShadowShader.SetMat4("uLightSpaceMatrix", lightSpaceMatrix);
                    RenderSkinned(scene, _skinnedPointShadowShader, true);
                }
            }
        }

        // --- 2. Regular Render Pass (no HDR FBO para post-process) ---
        uint targetFbo = _postPipeline.Settings.Enabled ? _postPipeline.HdrFbo : 0;
        
        // Validate HDR FBO before binding
        if (_postPipeline.Settings.Enabled)
        {
            if (!_postPipeline.ValidateHdrFbo(_gl))
            {
                _postPipeline.Reset();
            }
            targetFbo = _postPipeline.HdrFbo;
        }
        else
        {
            targetFbo = 0;
        }
        
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, targetFbo);
        _gl.Viewport(0, 0, (uint)_scrWidth, (uint)_scrHeight);
        _gl.ClearColor(0.1f, 0.1f, 0.15f, 1.0f);
        _gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

        // Skybox
        if (_skyboxShader.ID != 0 && _skyboxTexture.ID != 0)
        {
            _gl.DepthMask(false);
            _gl.DepthFunc(DepthFunction.Lequal);
            _gl.CullFace(GLEnum.Front);

            _skyboxShader.Use();
            var skyView = Matrix4x4.CreateFromQuaternion(Quaternion.CreateFromRotationMatrix(view));
            _skyboxShader.SetMat4("uView", skyView);
            _skyboxShader.SetMat4("uProj", proj);
            _skyboxShader.SetInt("uSkyTexture", 0);
            _skyboxTexture.Bind(0);
            _skyBoxCubeMesh.Draw();

            _gl.CullFace(GLEnum.Back);
            _gl.DepthFunc(DepthFunction.Less);
            _gl.DepthMask(true);
        }

        Light[] pointLights = new Light[8];
        int pointCount = FilterLights(scene.Lights, LightType.Point, false, pointLights, 8, camera.Position);

        // World geometry
        if (_shader.ID != 0)
        {
            _gl.Enable(EnableCap.DepthTest);
            _gl.Enable(EnableCap.CullFace);
            _gl.CullFace(GLEnum.Back);
            _gl.DepthFunc(DepthFunction.Less);

            _shader.Use();
            SetupWorldUniforms(_shader, camera, view, proj, lightDir, dirLight,
                cascadeLevels, lightSpaceMatrices, spotLights, spotCount, spotSpaceMatrices, pointLights, pointCount, shadowPointLights, shadowPointCount);

            scene.Render(_shader, _crateTexture, null);

            // Skinned entities (main pass)
            _skinnedShader.Use();
            SetupWorldUniforms(_skinnedShader, camera, view, proj, lightDir, dirLight,
                cascadeLevels, lightSpaceMatrices, spotLights, spotCount, spotSpaceMatrices, pointLights, pointCount, shadowPointLights, shadowPointCount);
            RenderSkinned(scene, _skinnedShader);
        }

        // ===== DECALS (Forward Lit in HDR FBO, after geometry) =====
        if (_decalQueue.Count > 0)
        {
            RenderDecals(scene, camera, view, proj, targetFbo, lightDir, dirLight, spotLights, spotCount, pointLights, pointCount);
        }



        // ===== BILLBOARDS (no HDR FBO) =====
        if (_billboardQueue.Count > 0)
        {
            _gl.Enable(EnableCap.Blend);
            _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            _gl.Disable(EnableCap.CullFace);
            _gl.DepthMask(false);

            _gl.UseProgram(_bbShader);

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

        if (_postPipeline.Settings.Enabled)
        {
            _postPipeline.SetViewProj(_prevViewProj, view, proj);
            _postPipeline.Execute(_postPipeline.HdrColorId, 0); // 0 = tela final
        }

        _prevViewProj = view * proj;
    }

    private int FilterLights(IReadOnlyList<Light> lights, LightType type, bool requireShadows, Light[] dest, int max, Vector3 cameraPos)
    {
        int count = 0;
        for (int i = 0; i < lights.Count && count < _allLightsBuf.Length; i++)
        {
            var l = lights[i];
            if (!l.Enabled || l.Type != type) continue;
            if (requireShadows && !l.CastShadows) continue;
            _allLightsBuf[count++] = l;
        }

        // Insertion sort by distance (max ~16 elements, trivial)
        for (int i = 1; i < count; i++)
        {
            var key = _allLightsBuf[i];
            float keyDist = Vector3.DistanceSquared(key.Position, cameraPos);
            int j = i - 1;
            while (j >= 0 && Vector3.DistanceSquared(_allLightsBuf[j].Position, cameraPos) > keyDist)
            {
                _allLightsBuf[j + 1] = _allLightsBuf[j];
                j--;
            }
            _allLightsBuf[j + 1] = key;
        }

        int result = count < max ? count : max;
        Array.Copy(_allLightsBuf, dest, result);
        return result;
    }

    private void SetupWorldUniforms(Shader shader, Camera camera, Matrix4x4 view, Matrix4x4 proj,
        Vector3 lightDir, Light? dirLight, float[] cascadeLevels, Matrix4x4[] lightSpaceMatrices,
        Light[] spotLights, int spotCount, Matrix4x4[] spotSpaceMatrices,
        Light[] pointLights, int pointCount, Light[] shadowPointLights, int shadowPointCount)
    {
        shader.SetVec3("uLightDir", lightDir);

        float lum = _skyboxDominantColor.X * 0.2126f + _skyboxDominantColor.Y * 0.7152f + _skyboxDominantColor.Z * 0.0722f;
        shader.SetFloat("uAmbient", 0.02f + 0.28f * lum);

        if (dirLight != null)
            shader.SetVec3("uLightColor", dirLight.Color * dirLight.Intensity);
        else
            shader.SetVec3("uLightColor", Vector3.Zero);

        shader.SetMat4("uView", view);
        shader.SetMat4("uProj", proj);

        for (int i = 0; i < 3; i++)
        {
            shader.SetMat4($"uLightSpaceMatrices[{i}]", lightSpaceMatrices[i]);
            shader.SetFloat($"uCascadePlaneDistances[{i}]", cascadeLevels[i]);
        }

        shader.SetBool("uEnableShadows", ShadowsEnabled);

        shader.SetFloat("uShadowBiasFactor", ShadowBiasFactor);
        shader.SetFloat("uShadowBiasBase", ShadowBiasBase);
        shader.SetFloat("uShadowSpread", ShadowSpread);

        shader.SetVec3("uColor", Vector3.One);
        shader.SetBool("uUseTexture", true);

        shader.SetInt("uTexture", 0);
        shader.SetInt("uShadowMap", 1);
        shader.SetInt("uSpotShadowMap", 2);
        shader.SetInt("uPointShadowMap0", 3);
        shader.SetInt("uPointShadowMap1", 4);
        shader.SetInt("uPointShadowMap2", 5);
        shader.SetInt("uPointShadowMap3", 6);
        _shadowMap.BindForReading(TextureUnit.Texture1);
        _spotShadowMap.BindForReading(TextureUnit.Texture2);
        _pointShadowMap0.BindForReading(TextureUnit.Texture3);
        _pointShadowMap1.BindForReading(TextureUnit.Texture4);
        _pointShadowMap2.BindForReading(TextureUnit.Texture5);
        _pointShadowMap3.BindForReading(TextureUnit.Texture6);

        shader.SetBool("uEnableShadowFilter", EnableShadowFilter);

        shader.SetVec3("uCameraPos", camera.Position);

        shader.SetInt("uPointLightCount", pointCount);
        for (int i = 0; i < pointCount; i++)
        {
            var l = pointLights[i];
            shader.SetVec3($"uPointLights[{i}].position", l.Position);
            shader.SetVec3($"uPointLights[{i}].color", l.Color * l.Intensity);
            shader.SetFloat($"uPointLights[{i}].radius", l.Radius);

            int shadowMapIndex = -1;
            if (ShadowsEnabled)
            {
                for (int s = 0; s < shadowPointCount; s++)
                {
                    if (shadowPointLights[s] == l) { shadowMapIndex = s; break; }
                }
            }
            shader.SetInt($"uPointLights[{i}].shadowMapIndex", shadowMapIndex);
            shader.SetFloat($"uPointLights[{i}].shadowBias", l.ShadowBias);
        }

        shader.SetInt("uSpotLightCount", spotCount);
        for (int i = 0; i < spotCount; i++)
        {
            var l = spotLights[i];
            shader.SetVec3($"uSpotLights[{i}].position", l.Position);
            shader.SetVec3($"uSpotLights[{i}].direction", Vector3.Normalize(l.Direction));
            shader.SetVec3($"uSpotLights[{i}].color", l.Color * l.Intensity);
            shader.SetFloat($"uSpotLights[{i}].radius", l.Radius);
            shader.SetFloat($"uSpotLights[{i}].innerCos", l.InnerCos);
            shader.SetFloat($"uSpotLights[{i}].outerCos", l.OuterCos);
            shader.SetBool($"uSpotLights[{i}].castShadows", l.CastShadows && ShadowsEnabled);
            shader.SetFloat($"uSpotLights[{i}].shadowBias", l.ShadowBias);
            shader.SetMat4($"uSpotLightSpaceMatrices[{i}]", spotSpaceMatrices[i]);
        }
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

    private void RenderSkinned(Scene scene, Shader shader, bool skipViewmodels = false)
    {
        foreach (var e in scene.Entities)
        {
            if (!e.Visible || e.SkinnedModel == null || e.Animator == null) continue;
            if (skipViewmodels && e.IsViewmodel) continue;

            shader.Use();

            var modelMatrix = Matrix4x4.CreateScale(e.Transform.Scale * e.ModelScale) *
                              Matrix4x4.CreateFromQuaternion(e.Transform.Rotation) *
                              Matrix4x4.CreateTranslation(e.Transform.Position + e.ModelOffset);
            shader.SetMat4("uModel", modelMatrix);
            shader.SetVec2("uUvScale", e.UvScale);
            shader.SetVec2("uUvOffset", e.UvOffset);
            shader.SetFloat("uUvRotation", e.UvRotation);
            shader.SetFloat("uIsViewmodel", e.IsViewmodel ? 1.0f : 0.0f);
            UploadBones(e.Animator.FinalBoneMatrices);

            foreach (var sub in e.SkinnedModel.Submeshes)
            {
                if (e.SkinnedModel.HiddenSubmeshes.Contains(sub.Name))
                    continue;

                var tex = sub.Texture ?? e.Texture ?? _crateTexture;
                if (tex != null)
                {
                    shader.SetBool("uUseTexture", true);
                    tex.Bind(0);
                }
                else
                {
                    shader.SetBool("uUseTexture", false);
                }

                sub.Mesh.Draw();
            }
        }
    }

    public unsafe void RenderBillboard(Matrix4x4 view, Matrix4x4 proj, uint texture, Vector3 worldPos, Vector2 size, Vector4 color)
    {
        _gl.Enable(GLEnum.Blend);
        _gl.BlendFunc(GLEnum.SrcAlpha, GLEnum.OneMinusSrcAlpha);

        _gl.UseProgram(_bbShader);

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
        Scene scene,
        Camera camera,
        Matrix4x4 view,
        Matrix4x4 proj,
        uint targetFbo,
        Vector3 lightDir,
        Light? dirLight,
        Light[] spotLights,
        int spotCount,
        Light[] pointLights,
        int pointCount)
    {
        if (_decalQueue.Count == 0) return;

        // Selecionar depth FBO compatível com o target atual
        bool isHdr = _postPipeline.Settings.Enabled;
        uint decalDepthFbo = isHdr ? _decalDepthFboHdr : _decalDepthFboDefault;
        uint decalDepthTex = isHdr ? _decalDepthTexHdr : _decalDepthTexDefault;

        // 1. Copia o Depth Buffer — formato agora é idêntico ao source
        _gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, targetFbo);
        _gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, decalDepthFbo);
        _gl.BlitFramebuffer(0, 0, _scrWidth, _scrHeight, 0, 0, _scrWidth, _scrHeight,
            ClearBufferMask.DepthBufferBit, BlitFramebufferFilter.Nearest);
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, targetFbo);

        // 2. Setup GL State para Box Decal Projection
        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        _gl.Disable(EnableCap.DepthTest);
        _gl.DepthMask(false);
        _gl.Enable(EnableCap.CullFace);
        _gl.CullFace(GLEnum.Front); // Render back faces do cubo para que a projeção funcione mesmo com a câmera dentro do cubo

        _decalShader.Use();

        Matrix4x4 viewProj = view * proj;
        Matrix4x4.Invert(viewProj, out Matrix4x4 invViewProj);

        _decalShader.SetMat4("uView", view);
        _decalShader.SetMat4("uProj", proj);
        _decalShader.SetMat4("uInvViewProj", invViewProj);
        _decalShader.SetVec2("uScreenSize", new Vector2(_scrWidth, _scrHeight));
        _decalShader.SetVec3("uCameraPos", camera.Position);

        // Forward Lighting Setup
        float lum = _skyboxDominantColor.X * 0.2126f + _skyboxDominantColor.Y * 0.7152f + _skyboxDominantColor.Z * 0.0722f;
        _decalShader.SetFloat("uAmbient", 0.02f + 0.28f * lum);
        _decalShader.SetVec3("uLightDir", lightDir);
        if (dirLight != null)
            _decalShader.SetVec3("uLightColor", dirLight.Color * dirLight.Intensity);
        else
            _decalShader.SetVec3("uLightColor", Vector3.Zero);

        _decalShader.SetInt("uPointLightCount", pointCount);
        for (int i = 0; i < pointCount; i++)
        {
            var l = pointLights[i];
            _decalShader.SetVec3($"uPointLights[{i}].position", l.Position);
            _decalShader.SetVec3($"uPointLights[{i}].color", l.Color * l.Intensity);
            _decalShader.SetFloat($"uPointLights[{i}].radius", l.Radius);
        }

        _decalShader.SetInt("uSpotLightCount", spotCount);
        for (int i = 0; i < spotCount; i++)
        {
            var l = spotLights[i];
            _decalShader.SetVec3($"uSpotLights[{i}].position", l.Position);
            _decalShader.SetVec3($"uSpotLights[{i}].direction", Vector3.Normalize(l.Direction));
            _decalShader.SetVec3($"uSpotLights[{i}].color", l.Color * l.Intensity);
            _decalShader.SetFloat($"uSpotLights[{i}].radius", l.Radius);
            _decalShader.SetFloat($"uSpotLights[{i}].innerCos", l.InnerCos);
            _decalShader.SetFloat($"uSpotLights[{i}].outerCos", l.OuterCos);
        }

        // Bind depth copy texture on unit 0
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, decalDepthTex);
        _decalShader.SetInt("uDepthTex", 0);

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

            _gl.ActiveTexture(TextureUnit.Texture1);
            _gl.BindTexture(TextureTarget.Texture2D, decal.AlbedoTexture);
            _decalShader.SetInt("uDecalAlbedo", 1);


            _skyBoxCubeMesh.Draw();
        }

        _gl.ActiveTexture(TextureUnit.Texture1);
        _gl.BindTexture(TextureTarget.Texture2D, 0);
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, 0);

        _gl.CullFace(GLEnum.Back);
        _gl.Enable(EnableCap.DepthTest);
        _gl.DepthMask(true);
        _gl.DepthFunc(DepthFunction.Less);
        _gl.Disable(EnableCap.Blend);
    }




    private Vector4[] GetFrustumCornersWorldSpace(Matrix4x4 proj, Matrix4x4 view)
    {
        Matrix4x4.Invert(view * proj, out Matrix4x4 invVP);
        
        Vector4[] corners = new Vector4[8];
        int i = 0;
        for (int x = 0; x < 2; ++x)
        {
            for (int y = 0; y < 2; ++y)
            {
                for (int z = 0; z < 2; ++z)
                {
                    Vector4 pt = Vector4.Transform(new Vector4(
                        2.0f * x - 1.0f,
                        2.0f * y - 1.0f,
                        (float)z,
                        1.0f), invVP);
                    corners[i++] = pt / pt.W;
                }
            }
        }
        return corners;
    }

    private Matrix4x4 GetLightSpaceMatrix(Camera camera, float aspect, float near, float far, Vector3 lightDir)
    {
        var proj = Matrix4x4.CreatePerspectiveFieldOfView(
            float.DegreesToRadians(camera.FOV), aspect, near, far);
        var view = camera.GetViewMatrix();
        
        var corners = GetFrustumCornersWorldSpace(proj, view);
        
        Vector3 center = Vector3.Zero;
        foreach (var v in corners)
        {
            center += new Vector3(v.X, v.Y, v.Z);
        }
        center /= corners.Length;

        // 1. Calculate bounding sphere radius
        float radius = 0.0f;
        foreach (var v in corners)
        {
            float distance = Vector3.Distance(center, new Vector3(v.X, v.Y, v.Z));
            radius = MathF.Max(radius, distance);
        }
        radius = MathF.Ceiling(radius * 16.0f) / 16.0f;

        float minX = -radius;
        float maxX = radius;
        float minY = -radius;
        float maxY = radius;
        float minZ = -2000.0f;
        float maxZ = 2000.0f;

        // 2. Texel Snapping to avoid shimmering
        var up = MathF.Abs(Vector3.Dot(lightDir, Vector3.UnitY)) > 0.999f ? Vector3.UnitZ : Vector3.UnitY;
        var baseView = Matrix4x4.CreateLookAt(Vector3.Zero, -lightDir, up);
        var centerLightSpace = Vector3.Transform(center, baseView);
        
        float shadowMapRes = (float)_shadowMap.Width;
        float texelSize = (radius * 2.0f) / shadowMapRes;
        
        centerLightSpace.X = MathF.Floor(centerLightSpace.X / texelSize) * texelSize;
        centerLightSpace.Y = MathF.Floor(centerLightSpace.Y / texelSize) * texelSize;
        
        Matrix4x4.Invert(baseView, out var invBaseView);
        center = Vector3.Transform(centerLightSpace, invBaseView);

        // 3. Final Matrices
        var lightView = Matrix4x4.CreateLookAt(center + lightDir, center, up);
        var lightProjection = Matrix4x4.CreateOrthographicOffCenter(minX, maxX, minY, maxY, minZ, maxZ);
        
        return lightView * lightProjection;
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
        _postPipeline?.Dispose();
    }
}
