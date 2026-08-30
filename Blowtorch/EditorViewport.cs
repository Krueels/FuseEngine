using System;
using System.IO;
using System.Numerics;
using System.Collections.Generic;
using Silk.NET.OpenGL;
using Fuse.Scene.Model;
using Fuse.Renderer;
using Fuse.AssetManagement;
using Fuse.Core;
using Shader = Fuse.Renderer.Shader;
using Mesh = Fuse.Renderer.Mesh;
using System.Security.Cryptography.X509Certificates;

namespace Blowtorch;

public unsafe class EditorViewport : IDisposable
{
    private readonly GL _gl;
    private uint _fbo;
    private uint _colorTex;
    private uint _depthRbo;
    private int _width = 800;
    private int _height = 600;

    private readonly ViewportCamera _camera;
    private readonly Mesh _gridMesh;
    private readonly Fuse.Debug.DebugDrawer _debugDrawer;
    private readonly EditorLightingSystem _lightingSystem;
    private Vector2 _lastMouse;
    private bool _isOrbiting;
    private bool _isPanning;
    private bool _firstMove;
    public static EditorViewport? ActiveViewport;

    public EditorViewport(GL gl, CameraViewType viewType, ImageBasedLighting? imageBasedLighting = null)
    {
        _gl = gl;
        _fbo = _gl.GenFramebuffer();
        _camera = new ViewportCamera { ViewType = viewType };
        _gridMesh = CreateGridMesh(_gl, 10000.0f);
        _debugDrawer = new Fuse.Debug.DebugDrawer(_gl) { Enabled = true };
        _lightingSystem = new EditorLightingSystem(gl, viewType == CameraViewType.Perspective3D, imageBasedLighting);
        CreateFbo(800, 600);
    }
    public bool ShowHitboxes
    {
        get => _debugDrawer.Enabled;
        set => _debugDrawer.Enabled = value;
    }

    public uint ColorTexture => _colorTex;
    public int Width => _width;
    public int Height => _height;
    public ViewportCamera Camera => _camera;
    public bool ShadowsEnabled { get; set; } = true;

    public void CreateFbo(int w, int h)
    {
        if (_colorTex != 0) _gl.DeleteTexture(_colorTex);
        if (_depthRbo != 0) _gl.DeleteRenderbuffer(_depthRbo);

        _colorTex = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, _colorTex);
        _gl.TexImage2D(TextureTarget.Texture2D, 0, (int)InternalFormat.Rgba, (uint)w, (uint)h, 0,
            PixelFormat.Rgba, PixelType.UnsignedByte, null);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);

        _depthRbo = _gl.GenRenderbuffer();
        _gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, _depthRbo);
        _gl.RenderbufferStorage(RenderbufferTarget.Renderbuffer, InternalFormat.DepthComponent24, (uint)w, (uint)h);

        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);
        _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D, _colorTex, 0);
        _gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment,
            RenderbufferTarget.Renderbuffer, _depthRbo);

        if (_gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer) != GLEnum.FramebufferComplete)
            Logger.Error("Viewport FBO incomplete");

        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        _width = w;
        _height = h;
    }

    public void BeginRender()
    {
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);
        _gl.Viewport(0, 0, (uint)_width, (uint)_height);
        _gl.ClearColor(0.25f, 0.25f, 0.3f, 1.0f);
        _gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
        _gl.Enable(EnableCap.DepthTest);
        _gl.Enable(EnableCap.CullFace);
    }

    public void EndRender(int windowWidth, int windowHeight)
    {
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        _gl.Viewport(0, 0, (uint)windowWidth, (uint)windowHeight);
    }

    public void RenderScene(EditorAssetService assetService, EditorSceneService sceneService, float snapGrid = 1.0f)
    {
        var shader = assetService.DefaultShader;
        if (shader.ID == 0) return;

        var scene = sceneService.Scene;
        var view = _camera.ViewMatrix;
        var proj = _camera.ProjectionMatrix((float)_width / _height);

        _lightingSystem.Prepare(
            scene,
            _camera,
            (float)_width / _height,
            ShadowsEnabled && !_camera.IsOrthographic,
            _camera.IsOrthographic,
            assetService.ShadowShader,
            assetService.PointShadowShader);

        // Shadow passes render into their own FBOs. Restore this viewport before
        // drawing its color image.
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);
        _gl.Viewport(0, 0, (uint)_width, (uint)_height);
        _gl.Enable(EnableCap.DepthTest);
        _gl.Enable(EnableCap.CullFace);
        _gl.CullFace(GLEnum.Back);
        _gl.DepthMask(true);
        _gl.DepthFunc(DepthFunction.Less);

        void PrepareMaterialShader(Shader target)
        {
            target.Use();
            target.SetFloat("uIsViewmodel", 1.0f);
            target.SetBool("uOutputSrgb", true);
            target.SetMat4("uView", view);
            target.SetMat4("uProj", proj);
            target.SetInt("uTexture", 0);
            target.SetInt("uMaterialAlphaMode", 0);
            target.SetFloat("uMaterialAlphaCutoff", 0.5f);
            target.SetBool("uMaterialReceiveShadows", true);
            target.SetBool("uIsEmissive", false);
            target.SetVec3("uEmissiveColor", Vector3.Zero);
            target.SetFloat("uEmissiveStrength", 0.0f);
            _lightingSystem.BindShadowMaps(target);
            _lightingSystem.BindImageBasedLighting(target);
        }

        PrepareMaterialShader(shader);

        // Draw Grid
        var gridShader = assetService.GridShader;
        if (gridShader != null && gridShader.ID != 0)
        {
            gridShader.Use();
            gridShader.SetMat4("uView", view);
            gridShader.SetMat4("uProj", proj);
            gridShader.SetVec3("uColor", new Vector3(0.35f, 0.35f, 0.4f));
            
            // Distância de Fade do Grid (infinita para 2D, 1500 unidades para 3D)
            float fadeDist = _camera.IsOrthographic ? 10000.0f : 100.0f;
            gridShader.SetFloat("uFadeDistance", fadeDist);
            gridShader.SetFloat("uSnapGrid", snapGrid);
            gridShader.SetVec3("uCameraPos", _camera.Position);

            _gl.Disable(EnableCap.CullFace);
            _gl.Enable(EnableCap.Blend);
            _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);

            Vector3 camPos = _camera.Position;
            Matrix4x4 model = Matrix4x4.CreateTranslation(camPos.X, 0, camPos.Z);
            
            if (_camera.ViewType == CameraViewType.Front)
            {
                model = Matrix4x4.CreateRotationX(MathF.PI / 2.0f) * Matrix4x4.CreateTranslation(camPos.X, camPos.Y, 0);
            }
            else if (_camera.ViewType == CameraViewType.Side)
            {
                model = Matrix4x4.CreateRotationZ(MathF.PI / 2.0f) * Matrix4x4.CreateTranslation(0, camPos.Y, camPos.Z);
            }
            gridShader.SetMat4("uModel", model);
            
            // Agora desenhamos um plano usando triângulos
            _gridMesh.Draw(PrimitiveType.Triangles);
            
            _gl.Disable(EnableCap.Blend);
            _gl.Enable(EnableCap.CullFace);
            
            // Restaura o shader original para o resto da cena
            shader.Use();
        }

        // Draw Entities
        bool isWireframe = _camera.IsOrthographic;
        if (isWireframe)
        {
            _gl.Disable(EnableCap.DepthTest);
            _gl.LineWidth(2.0f);
            shader.SetBool("uUseTexture", false);
            shader.SetVec3("uColor", new Vector3(0.8f, 0.8f, 0.8f));
        }
        else
        {
            _gl.Enable(EnableCap.DepthTest);
            _gl.PolygonMode(GLEnum.FrontAndBack, GLEnum.Fill);
            shader.SetVec3("uColor", Vector3.One);
        }

        Shader? activeShader = shader;
        bool cullEnabled = true;
        bool blendEnabled = false;
        foreach (var entity in scene.Entities)
        {
            if (!entity.Visible || entity.Mesh == null) continue;

            if (!isWireframe)
            {
                foreach (var part in entity.Mesh.Parts)
                {
                    var material = entity.ResolveMaterial(part.MaterialSlot);
                    Shader targetShader = material?.StaticShader ?? shader;
                    if (!ReferenceEquals(activeShader, targetShader))
                    {
                        PrepareMaterialShader(targetShader);
                        activeShader = targetShader;
                    }

                    bool wantsCull = material?.Asset.TwoSided != true;
                    if (wantsCull != cullEnabled)
                    {
                        if (wantsCull) _gl.Enable(EnableCap.CullFace);
                        else _gl.Disable(EnableCap.CullFace);
                        cullEnabled = wantsCull;
                    }

                    bool wantsBlend = material?.Asset.AlphaMode == Fuse.Renderer.Materials.MaterialAlphaMode.Blend;
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

                    targetShader.SetMat4("uModel", entity.Transform.Matrix);
                    targetShader.SetVec2("uUvScale", entity.UvScale);
                    targetShader.SetVec2("uUvOffset", entity.UvOffset);
                    targetShader.SetFloat("uUvRotation", entity.UvRotation);

                    if (material != null)
                    {
                        material.Bind(targetShader);
                    }
                    else
                    {
                        uint texId = assetService.GetOrCreateTexture(entity.TexturePath);
                        if (texId == 0)
                            texId = assetService.DefaultTexture;

                        targetShader.SetBool("uUseTexture", texId != 0);
                        targetShader.SetInt("uMaterialAlphaMode", 0);
                        targetShader.SetBool("uMaterialReceiveShadows", true);
                        if (texId != 0)
                        {
                            targetShader.SetInt("uTexture", 0);
                            _gl.ActiveTexture(TextureUnit.Texture0);
                            _gl.BindTexture(TextureTarget.Texture2D, texId);
                        }
                    }

                    entity.Mesh.DrawPart(part);
                }
            }
            else
            {
                if (!ReferenceEquals(activeShader, shader))
                {
                    PrepareMaterialShader(shader);
                    shader.SetBool("uUseTexture", false);
                    shader.SetVec3("uColor", new Vector3(0.8f, 0.8f, 0.8f));
                    activeShader = shader;
                }
                shader.SetMat4("uModel", entity.Transform.Matrix);
                shader.SetVec2("uUvScale", entity.UvScale);
                shader.SetVec2("uUvOffset", entity.UvOffset);
                shader.SetFloat("uUvRotation", entity.UvRotation);
                if (entity.Mesh.HasLineBuffer)
                {
                    _gl.PolygonMode(GLEnum.FrontAndBack, GLEnum.Fill);
                    entity.Mesh.DrawLineBuffer();
                }
                else
                {
                    _gl.PolygonMode(GLEnum.FrontAndBack, GLEnum.Line);
                    entity.Mesh.Draw();
                }
            }
        }

        if (!cullEnabled)
            _gl.Enable(EnableCap.CullFace);
        if (blendEnabled)
        {
            _gl.Disable(EnableCap.Blend);
            _gl.DepthMask(true);
        }

        if (isWireframe)
        {
            _gl.PolygonMode(GLEnum.FrontAndBack, GLEnum.Fill);
            _gl.Enable(EnableCap.DepthTest);
        }
    }

    public void RenderDebug(EditorAssetService assetService, EditorSceneService sceneService, Action<Fuse.Debug.DebugDrawer, EditorAssetService>? onDrawDebug = null)
    {
        var view = _camera.ViewMatrix;
        var proj = _camera.ProjectionMatrix((float)_width / _height);

        if (!_debugDrawer.Enabled) return;
        _debugDrawer.Clear();

        var doc = sceneService.Document;
        var assets = assetService.AssetManager;
        var fuseResPath = assetService.FuseResPath;

        // Object shapes
        foreach (var mapObj in doc.Objects)
        {
            if (mapObj.Body == null) continue;

            var body = mapObj.Body;
            var color = body.Mass > 0 ? new Vector3(1, 1, 0) : new Vector3(1, 0, 0);

            switch (body.Shape)
            {
                case MapShapeType.Box when body.HalfExtents.HasValue:
                    _debugDrawer.DrawBox(body.Position, body.Rotation, body.HalfExtents.Value, color);
                    break;
                case MapShapeType.Sphere when body.Radius.HasValue:
                    _debugDrawer.DrawSphere(body.Position, body.Rotation, body.Radius.Value, color);
                    break;
                case MapShapeType.Capsule when body.Radius.HasValue && body.Height.HasValue:
                    _debugDrawer.DrawCapsule(body.Position, body.Rotation, body.Height.Value * 0.5f, body.Radius.Value, color);
                    break;
                case MapShapeType.Trimesh when mapObj.IsModel && mapObj.Model != null:
                    string modelPath = Path.GetFullPath(Path.Combine(fuseResPath, mapObj.Model));
                    var model = assets.GetModel(modelPath);
                    if (model != null && model.CollMesh != null)
                    {
                        _debugDrawer.DrawCachedLineMesh(model.CollMesh, body.Position, body.Rotation, mapObj.ModelScale, color, view, proj);
                    }
                    break;
                case MapShapeType.ConvexHull when mapObj.IsModel && mapObj.Model != null:
                    string modelPathHull = Path.GetFullPath(Path.Combine(fuseResPath, mapObj.Model));
                    var modelHull = assets.GetModel(modelPathHull);
                    if (modelHull != null)
                    {
                        if (modelHull.ConvexCollMesh != null)
                            _debugDrawer.DrawCachedLineMesh(modelHull.ConvexCollMesh, body.Position, body.Rotation, mapObj.ModelScale, color, view, proj);
                        else if (modelHull.CollMesh != null)
                            _debugDrawer.DrawCachedLineMesh(modelHull.CollMesh, body.Position, body.Rotation, mapObj.ModelScale, color, view, proj);
                    }
                    break;
            }
        }

        if (doc.PlayerSpawn != null)
        {
            var sp = doc.PlayerSpawn;
            _debugDrawer.DrawCapsule(sp.Position, Quaternion.Identity, 0.9f, 0.5f, new Vector3(0, 1, 0));

            float yawRad = float.DegreesToRadians(sp.Yaw);
            float pitchRad = float.DegreesToRadians(sp.Pitch);
            var fwd = new Vector3(
                MathF.Cos(yawRad) * MathF.Cos(pitchRad),
                MathF.Sin(pitchRad),
                MathF.Sin(yawRad) * MathF.Cos(pitchRad)
            );
            Vector3 eyePos = sp.Position + new Vector3(0, 0.9f, 0);
            _debugDrawer.PushLine(eyePos, eyePos + fwd * 1.5f, new Vector3(0, 1, 1));
        }

        // Lights
        foreach (var light in sceneService.Scene.Lights)
            _debugDrawer.DrawLight(light);

        onDrawDebug?.Invoke(_debugDrawer, assetService);

        _debugDrawer.Render(view, proj);
    }

    public void HandleInput(ImGuiNET.ImGuiIOPtr io, float dt, Silk.NET.GLFW.Glfw? glfw = null, Silk.NET.GLFW.WindowHandle* win = null, System.Numerics.Vector2 vpPos = default, System.Numerics.Vector2 vpSize = default)
    {
        if (glfw != null && win != null && !ImGuiNET.ImGui.IsMouseDown(ImGuiNET.ImGuiMouseButton.Right) && !ImGuiNET.ImGui.IsMouseDown(ImGuiNET.ImGuiMouseButton.Middle))
            glfw.SetInputMode(win, Silk.NET.GLFW.CursorStateAttribute.Cursor, Silk.NET.GLFW.CursorModeValue.CursorNormal);

        float scroll = io.MouseWheel;
        bool wantPan = false;
        bool wantLook = false;


        if (_camera.IsOrthographic)
        {
            wantPan = ImGuiNET.ImGui.IsMouseDown(ImGuiNET.ImGuiMouseButton.Middle);
        }
        else
        {
            wantLook = ImGuiNET.ImGui.IsMouseDown(ImGuiNET.ImGuiMouseButton.Right);
        }

        //if (scroll != 0)
        //{
        //    Vector2 localMousePos = io.MousePos - vpPos;
        //    _camera.Zoom(scroll, localMousePos, vpSize);
        //}
        if (scroll != 0)
        {
            if (!_camera.IsOrthographic && (wantLook || wantPan))
            {
                _camera.AdjustFlySpeed(scroll);
            }
            else
            {
                Vector2 localMousePos = io.MousePos - vpPos;
                _camera.Zoom(scroll, localMousePos, vpSize);
            }
        }

        if (ActiveViewport != null && ActiveViewport != this)
        {
            wantPan = false;
            wantLook = false;
        }

        if (wantPan)
        {
            if (!_isPanning)
            {
                _isPanning = true;
                _firstMove = true;
                ActiveViewport = this;
                if (glfw != null && win != null)
                    glfw.SetInputMode(win, Silk.NET.GLFW.CursorStateAttribute.Cursor, Silk.NET.GLFW.CursorModeValue.CursorHidden);
            }
            else
            {
                if (_firstMove)
                {
                    _firstMove = false;
                    _lastMouse = io.MousePos;
                }
                Vector2 mouse = io.MousePos;
                float dx = mouse.X - _lastMouse.X;
                float dy = mouse.Y - _lastMouse.Y;
                _camera.Pan(dx, dy, vpSize.Y);
                _lastMouse = mouse;
                if (glfw != null && win != null)
                {
                    float cx = MathF.Round(vpPos.X + vpSize.X * 0.5f);
                    float cy = MathF.Round(vpPos.Y + vpSize.Y * 0.5f);
                    float distSq = (mouse.X - cx) * (mouse.X - cx) + (mouse.Y - cy) * (mouse.Y - cy);
                    
                    // Teleporta o mouse de volta pro centro APENAS se ele se afastar mais de 100 pixels,
                    // evitando a perda massiva de delta-mouse do sistema operacional em framerates baixos.
                    if (distSq > 10000.0f)
                    {
                        glfw.SetCursorPos(win, cx, cy);
                        _lastMouse = new Vector2(cx, cy);
                    }
                }
            }
        }
        else
        {
            _isPanning = false;
            if (ActiveViewport == this) ActiveViewport = null;
        }

        if (wantLook)
        {
            if (!_isOrbiting)
            {
                _isOrbiting = true;
                _firstMove = true;
                ActiveViewport = this;
                if (glfw != null && win != null)
                    glfw.SetInputMode(win, Silk.NET.GLFW.CursorStateAttribute.Cursor, Silk.NET.GLFW.CursorModeValue.CursorHidden);
            }
            else
            {
                if (_firstMove)
                {
                    _firstMove = false;
                    _lastMouse = io.MousePos;
                }
                Vector2 mouse = io.MousePos;
                float dx = mouse.X - _lastMouse.X;
                float dy = mouse.Y - _lastMouse.Y;
                _camera.Look(dx, dy);
                _lastMouse = mouse;

                if (glfw != null && win != null)
                {
                    float cx = MathF.Round(vpPos.X + vpSize.X * 0.5f);
                    float cy = MathF.Round(vpPos.Y + vpSize.Y * 0.5f);
                    float distSq = (mouse.X - cx) * (mouse.X - cx) + (mouse.Y - cy) * (mouse.Y - cy);
                    
                    if (distSq > 10000.0f)
                    {
                        glfw.SetCursorPos(win, cx, cy);
                        _lastMouse = new Vector2(cx, cy);
                    }
                }
            }

            // Keyboard navigation in 3D (FPS Noclip)
            if (!_camera.IsOrthographic)
            {
                float fwd = 0, right = 0, up = 0;
                if (ImGuiNET.ImGui.IsKeyDown(ImGuiNET.ImGuiKey.W)) fwd += 1;
                if (ImGuiNET.ImGui.IsKeyDown(ImGuiNET.ImGuiKey.S)) fwd -= 1;
                if (ImGuiNET.ImGui.IsKeyDown(ImGuiNET.ImGuiKey.D)) right += 1;
                if (ImGuiNET.ImGui.IsKeyDown(ImGuiNET.ImGuiKey.A)) right -= 1;
                if (ImGuiNET.ImGui.IsKeyDown(ImGuiNET.ImGuiKey.E)) up += 1;
                if (ImGuiNET.ImGui.IsKeyDown(ImGuiNET.ImGuiKey.Q)) up -= 1;

                if (fwd != 0 || right != 0 || up != 0)
                {
                    float speedMul = ImGuiNET.ImGui.IsKeyDown(ImGuiNET.ImGuiKey.LeftShift) ? 2.0f : 1.0f;
                    _camera.Fly(fwd, right, up, dt * speedMul);
                }
            }
        }
        else
        {
            if (_isOrbiting)
            {
                _isOrbiting = false;
                if (glfw != null && win != null)
                    glfw.SetInputMode(win, Silk.NET.GLFW.CursorStateAttribute.Cursor, Silk.NET.GLFW.CursorModeValue.CursorNormal);
            }
            if (ActiveViewport == this) ActiveViewport = null;
        }
    }

    private Mesh CreateGridMesh(GL gl, float extent)
    {
        var verts = new[]
        {
            new Vertex { Position = new Vector3(-extent, 0, -extent), Normal = Vector3.UnitY },
            new Vertex { Position = new Vector3( extent, 0, -extent), Normal = Vector3.UnitY },
            new Vertex { Position = new Vector3( extent, 0,  extent), Normal = Vector3.UnitY },
            new Vertex { Position = new Vector3(-extent, 0,  extent), Normal = Vector3.UnitY },
        };
        var idxs = new uint[] { 0, 1, 2, 0, 2, 3 };

        return new Mesh(gl, verts, idxs);
    }

    public void Dispose()
    {
        _debugDrawer.Dispose();
        _gl.DeleteFramebuffer(_fbo);
        _gl.DeleteTexture(_colorTex);
        _gl.DeleteRenderbuffer(_depthRbo);
        _gridMesh.Dispose();
        _lightingSystem.Dispose();
    }
}
