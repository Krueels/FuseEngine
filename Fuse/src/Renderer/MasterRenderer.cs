using System.Linq;
using System.Numerics;
using Silk.NET.OpenGL;
using Fuse.Core;

namespace Fuse.Renderer;

public class MasterRenderer
{
    private readonly GL _gl;
    private int _scrWidth, _scrHeight;

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
    private uint _bbShader;
    private int _bbUView, _bbUProj, _bbUWorldPos, _bbUSize, _bbUColor, _bbUTexture;
    private uint _bbVao, _bbVbo;

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

    public void Resize(int width, int height)
    {
        _scrWidth = width;
        _scrHeight = height;
    }

    public void RenderFrame(Scene scene, Camera camera, Physics.PhysicsWorld physics)
    {
        float aspect = (float)_scrWidth / _scrHeight;
        var view = camera.GetViewMatrix();
        var proj = camera.GetProjectionMatrix(aspect);

        // --- 0. Update Physics and Hierarchy ---
        scene.UpdateTransforms(physics);

        // --- 1. Shadow Pass ---
        var dirLight = scene.Lights.FirstOrDefault(l => l.Enabled && l.Type == LightType.Directional);
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
                RenderSkinned(scene, _skinnedShadowShader);
            }
        }

        // --- 1.5. Spot Light Shadow Pass ---
        var spotLights = scene.Lights.Where(l => l.Enabled && l.Type == LightType.Spot)
            .OrderBy(l => Vector3.DistanceSquared(l.Position, camera.Position))
            .Take(4).ToList();
        Matrix4x4[] spotSpaceMatrices = new Matrix4x4[4];

        if (_shadowShader != null && _shadowShader.ID != 0 && ShadowsEnabled)
        {
            _gl.Enable(EnableCap.DepthTest);
            _gl.Enable(EnableCap.CullFace);
            _gl.CullFace(GLEnum.Back);

            for (int i = 0; i < spotLights.Count; i++)
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
                RenderSkinned(scene, _skinnedShadowShader);
            }
        }

        // --- 1.7. Point Light Shadow Pass ---
        var shadowPointLights = scene.Lights.Where(l => l.Enabled && l.Type == LightType.Point && l.CastShadows)
            .OrderBy(l => Vector3.DistanceSquared(l.Position, camera.Position))
            .Take(4).ToList();
        if (_pointShadowShader != null && _pointShadowShader.ID != 0 && ShadowsEnabled)
        {
            _gl.Enable(EnableCap.DepthTest);
            _gl.Enable(EnableCap.CullFace);
            _gl.CullFace(GLEnum.Back);

            for (int i = 0; i < shadowPointLights.Count; i++)
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
                    RenderSkinned(scene, _skinnedPointShadowShader);
                }
            }
        }

        // --- 2. Regular Render Pass ---
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
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

        // World geometry
        if (_shader.ID != 0)
        {
            _gl.Enable(EnableCap.DepthTest);
            _gl.Enable(EnableCap.CullFace);
            _gl.CullFace(GLEnum.Back);
            _gl.DepthFunc(DepthFunction.Less);
            var pointLights = scene.Lights.Where(l => l.Enabled && l.Type == LightType.Point)
                .OrderBy(l => Vector3.DistanceSquared(l.Position, camera.Position))
                .Take(8).ToList();

            _shader.Use();
            SetupWorldUniforms(_shader, camera, view, proj, lightDir, dirLight,
                cascadeLevels, lightSpaceMatrices, spotLights, spotSpaceMatrices, pointLights, shadowPointLights);

            scene.Render(_shader, _crateTexture, null);

            // Skinned entities (main pass)
            _skinnedShader.Use();
            SetupWorldUniforms(_skinnedShader, camera, view, proj, lightDir, dirLight,
                cascadeLevels, lightSpaceMatrices, spotLights, spotSpaceMatrices, pointLights, shadowPointLights);
            RenderSkinned(scene, _skinnedShader);
        }
    }

    private void SetupWorldUniforms(Shader shader, Camera camera, Matrix4x4 view, Matrix4x4 proj,
        Vector3 lightDir, Light? dirLight, float[] cascadeLevels, Matrix4x4[] lightSpaceMatrices,
        List<Light> spotLights, Matrix4x4[] spotSpaceMatrices, List<Light> pointLights, List<Light> shadowPointLights)
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

        shader.SetInt("uPointLightCount", pointLights.Count);
        for (int i = 0; i < pointLights.Count; i++)
        {
            var l = pointLights[i];
            shader.SetVec3($"uPointLights[{i}].position", l.Position);
            shader.SetVec3($"uPointLights[{i}].color", l.Color * l.Intensity);
            shader.SetFloat($"uPointLights[{i}].radius", l.Radius);

            int shadowMapIndex = ShadowsEnabled ? shadowPointLights.IndexOf(l) : -1;
            shader.SetInt($"uPointLights[{i}].shadowMapIndex", shadowMapIndex);
            shader.SetFloat($"uPointLights[{i}].shadowBias", l.ShadowBias);
        }

        shader.SetInt("uSpotLightCount", spotLights.Count);
        for (int i = 0; i < spotLights.Count; i++)
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

    private void RenderSkinned(Scene scene, Shader shader)
    {
        foreach (var e in scene.Entities)
        {
            if (!e.Visible || e.SkinnedModel == null || e.Animator == null) continue;

            shader.Use();

            var modelMatrix = Matrix4x4.CreateScale(e.Transform.Scale * e.ModelScale) *
                              Matrix4x4.CreateFromQuaternion(e.Transform.Rotation) *
                              Matrix4x4.CreateTranslation(e.Transform.Position + e.ModelOffset);
            shader.SetMat4("uModel", modelMatrix);
            shader.SetVec2("uUvScale", e.UvScale);
            shader.SetVec2("uUvOffset", e.UvOffset);
            shader.SetFloat("uUvRotation", e.UvRotation);
            shader.SetBonesSSBO(e.Animator.FinalBoneMatrices);

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
}
