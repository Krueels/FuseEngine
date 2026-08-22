using System.Numerics;
using Fuse.Imgui;
using Fuse.Input;
using Fuse.Physics;
using Fuse.Renderer;
using Fuse.Player;
using Fuse.Player.Weapons;
using JoltPhysicsSharp;
using Silk.NET.OpenGL;
using System.Drawing.Printing;

namespace Fuse.Core;

public unsafe class Application : IDisposable
{
    private readonly Window _window;
    private double _lastTime;
    private bool _paused;
    private int _scrWidth = 1280, _scrHeight = 800;
    private bool _screenshotRequested;

    // Core Systems
    private readonly Physics.PhysicsWorld _physics;
    private AssetManagement.AssetManager _assets = null!;
    private Audio.AudioSystem _audio = null!;
    private Audio.ImpactSoundSystem _impactSound = null!;
    private Renderer.MasterRenderer _renderer = null!;
    private Scene.SceneManager _sceneManager = null!;
    private Interaction.PlayerInteraction _interaction = null!;

    // Player
    private Player.Player _player = null!;
    private Player.PickupController _pickup = null!;
    private Renderer.Light _flashlight = null!;
    private Player.WeaponSystem _weaponSystem = null!;

    // UI & Debug
    private Renderer.UIRenderer _ui = null!;
    private UI.HUD _hud = null!;
    private UI.HUDText _fpsText = null!;
    private UI.HUDImage _crosshairNode = null!;
    private UI.HUDText _weaponDebugText = null!;
    private Debug.DebugDrawer _debugDrawer = null!;
    private Imgui.ImGuiBackEnd _imgui = null!;
    private bool _showImgui = false;
    private bool _showSkinnedDebug = false;
    private Imgui.Console _console = null!;
    private float _loadProgress;
    private string _loadStatus = "";

    // ViewModel
    //private Entity? _glockViewModelEntity;
    //private Vector3 _glockLocalOffset = new Vector3(0.0f, -0.83f, 0.0f);
    //private Vector3 _glockLocalEulerDeg = new Vector3(0f, 90f, 0f);
    //private Quaternion _glockLocalRotation = Quaternion.Identity;
    //private bool _updateViewmodelTransform = true;
    //private bool _invertViewmodelRotation = false; // toggle if rotation is inverted

    // Skinned model test
    private Renderer.Entity? _skinnedTestEntity;

    public Application()
    {
        _window = new Window("Fuse", _scrWidth, _scrHeight);
        _physics = new Physics.PhysicsWorld(new Vector3(0, -9.81f, 0));
    }

    public bool Init(string initialMap)
    {
        if (_window.Handle == null)
        {
            Logger.Error("Window creation failed");
            return false;
        }

        var gl = _window.GL;

        // Managers & Core
        _assets = new AssetManagement.AssetManager(gl);
        _renderer = new Renderer.MasterRenderer(gl);
        _sceneManager = new Scene.SceneManager(_physics, _assets);
        _debugDrawer = new Debug.DebugDrawer(gl);

        // Audio
        _audio = new Audio.AudioSystem();
        _audio.GlobalVolume = 1.0f;
        _impactSound = new Audio.ImpactSoundSystem(_physics, _audio);
        
        // UI & ImGui
        _ui = new Renderer.UIRenderer(gl, _scrWidth, _scrHeight);
        _imgui = new Imgui.ImGuiBackEnd(gl);
        _imgui.Init();

        // Player setup
        _player = new Player.Player(_physics, new Vector3(0, 2, 0));
        _player.SetAudioSystem(_audio);
        var emptyID = new JoltPhysicsSharp.BodyID();
        _pickup = new Player.PickupController(_physics, _player.Camera, emptyID, _audio);
        _flashlight = new Renderer.Light
        {
            Id = "player_flashlight",
            Type = Renderer.LightType.Spot,
            Position = _player.Camera.Position,
            Direction = _player.Camera.Front,
            Color = new Vector3(1.0f, 0.95f, 0.8f),
            Radius = 25.0f,
            Intensity = 1.0f,
            InnerConeAngle = float.DegreesToRadians(15),
            OuterConeAngle = float.DegreesToRadians(35),
            Dynamic = true,
            Enabled = false
        };
        _player.SetFlashlight(_flashlight);

        // Console
        _console = new Imgui.Console();
        _console.SetPlayer(_player);
        _console.StartCapture();
        _console.OnLoadMap = (map) => LoadMap(map, OnLoadProgress);
        _console.OnLoadSky = (fileName) =>
        {
            var tex = _assets.GetTexture($"{Fuse.ResPath.Path}/Textures/{fileName}");
            if (tex.ID == 0)
                Logger.Error($"Failed to load skybox: {fileName}");
            else
                _renderer.SetSkyboxTexture(tex);
        };

        // HUD
        _hud = new UI.HUD();
        _fpsText = _hud.AddText("FPS: 0", UI.HUDAnchor.TopLeft, new Vector2(20, 20), 2.0f, new Vector4(0, 1, 1, 1));
        var crosshairTexture = _assets.GetTexture($"{Fuse.ResPath.Path}/Textures/UI/crosshair.png");
        var crosshairInteractTexture = _assets.GetTexture($"{Fuse.ResPath.Path}/Textures/UI/crosshair_interact.png");
        _crosshairNode = _hud.AddImage(crosshairTexture, UI.HUDAnchor.Center, Vector2.Zero, new Vector2(8, 8));
        _weaponDebugText = _hud.AddText("Weapon Debug", UI.HUDAnchor.TopLeft, new Vector2(20, 50), 1.0f, new Vector4(0, 1, 0, 1));

        // Initialization
        _renderer.Init(_assets, _scrWidth, _scrHeight);
        _interaction = new Interaction.PlayerInteraction(_physics, _player, _crosshairNode, crosshairTexture, crosshairInteractTexture);

        // Weapon System
        _weaponSystem = new Player.WeaponSystem(_player, _player.Camera, _physics, _assets, _audio, _sceneManager);
        _weaponSystem.RegisterWeapon(new GlockWeapon());

        // Default Map Loading
        LoadMap(initialMap, OnLoadProgress);
        _sceneManager.ActiveScene.AddLight(_flashlight);

        _weaponSystem.Equip("glock");
        RegisterWindowCallbacks();

        _lastTime = _window.GlfwApi.GetTime();
        Logger.Info(":: Application ready ::");
        return true;
    }

    private void LoadMap(string mapName, Action<float, string>? onProgress = null)
    {
        _impactSound?.Clear();
        var spawn = _sceneManager.LoadMap(mapName, onProgress);
        if (spawn.HasValue)
        {
            _player.NativeCharacter.Position = spawn.Value.Position;
            _player.NativeCharacter.LinearVelocity = Vector3.Zero;
            _player.Camera.SetRotation(spawn.Value.Yaw, spawn.Value.Pitch);
        }
        _sceneManager.InitTriggerSystem(_player);
        _sceneManager.ActiveScene.AddLight(_flashlight);
        //SpawnGlockViewModel();
    }
    
    private void ReloadMap(Action<float, string>? onProgress = null)
    {
        _impactSound?.Clear();
        var spawn = _sceneManager.ReloadMap(onProgress);
        if (spawn.HasValue)
        {
            _player.NativeCharacter.Position = spawn.Value.Position;
            _player.NativeCharacter.LinearVelocity = Vector3.Zero;
            _player.Camera.SetRotation(spawn.Value.Yaw, spawn.Value.Pitch);
        }
        _sceneManager.InitTriggerSystem(_player);
        //SpawnGlockViewModel();
        _sceneManager.InitTriggerSystem(_player);
        _sceneManager.ActiveScene.AddLight(_flashlight);
        _weaponSystem?.Equip("glock"); // Re-equipa a arma após reload (mapa foi limpo)
    }

    //private void SpawnGlockViewModel()
    //{
    //    string path = $"{Fuse.ResPath.Path}/skinned_models/Glock.fbx";
    //    var model = _assets.GetSkinnedModel(path);
    //    if (model == null)
    //    {
    //        Logger.Error($"Skinned test model failed to load: {path}");
    //        return;
    //    }

    //    var animator = new Animation.Animator(model.Skeleton);
    //    model.Link(animator);

    //    var entity = _sceneManager.ActiveScene.Add(null, "glock_viewmodel");
    //    entity.SkinnedModel = model;
    //    entity.Animator = animator;

    //    if (!string.IsNullOrEmpty(model.DefaultClipName))
    //        animator.Play(model.DefaultClipName);

    //    animator.Play("Idle");

    //    // Position in front of player initially (will be updated each frame)
    //    Vector3 front = _player.Camera.Front;
    //    front.Y = 0;
    //    if (front.LengthSquared() > 0.001f) front = Vector3.Normalize(front);
    //    else front = -Vector3.UnitZ;

    //    entity.Transform.Position = _player.NativeCharacter.Position + front * 2.5f;
    //    // GlobalScale já normaliza vértices (cm→m). Entity scale = 1.0 (como Hell2025).
    //    entity.Transform.Scale = new Vector3(1.0f);

    //    _glockViewModelEntity = entity;
    //    _skinnedTestEntity = entity;
    //    Logger.InfoGold($"[Skinned] Glock spawned. Clips: {string.Join(", ", model.Clips.Keys)} (F8 to cycle)");
    //}

    //private void UpdateViewmodelTransform()
    //{
    //    if (_glockViewModelEntity == null || !_updateViewmodelTransform) return;

    //    var cam = _player.Camera;
    //    var camPos = cam.Position;
        
    //    // Camera's rotation quaternion has inverted yaw (camera turns right = positive yaw, but math is CCW)
    //    // Fix: negate yaw component or use inverse rotation
    //    var camRot = cam.Rotation;
        
    //    // Fix inverted yaw: negate Y and W components of quaternion (negates rotation around Y)
    //    var camRotFixed = new Quaternion(camRot.X, -camRot.Y, camRot.Z, -camRot.W);

    //    // Viewmodel position: camera position + rotated offset
    //    var offset = Vector3.Transform(_glockLocalOffset, camRot);
    //    var viewmodelPos = camPos + offset;

    //    // Local rotation in camera space (Euler degrees -> quaternion)
    //    var rad = Vector3.DegreesToRadians(_glockLocalEulerDeg);
    //    _glockLocalRotation = Quaternion.CreateFromYawPitchRoll(rad.Y, rad.X, rad.Z);

    //    // Viewmodel rotation = FIXED camera rotation * local rotation
    //    var viewmodelRot = camRotFixed * _glockLocalRotation;

    //    _glockViewModelEntity.Transform.Position = camPos + Vector3.Transform(_glockLocalOffset, camRotFixed);
    //    _glockViewModelEntity.Transform.Rotation = viewmodelRot;
    //}

    //private void CycleSkinnedClip()
    //{
    //    if (_skinnedTestEntity?.SkinnedModel == null || _skinnedTestEntity.Animator == null)
    //        return;

    //    var clips = _skinnedTestEntity.SkinnedModel.Clips.Keys.OrderBy(k => k).ToList();
    //    if (clips.Count == 0) return;

    //    string current = _skinnedTestEntity.Animator.CurrentClip?.Name ?? "";
    //    int idx = clips.IndexOf(current);
    //    string next = clips[(idx + 1) % clips.Count];
    //    _skinnedTestEntity.Animator.Play(next);
    //    Logger.InfoGold($"[Skinned] Clip: {next}");
    //}

    private void RegisterWindowCallbacks()
    {
        _window.OnMouseMove += (dx, dy) => { _player.Camera.ProcessMouseMovement((float)dx, (float)dy); };
        _window.OnScroll += (yoffset) =>
        {
            if (!ImGuiNET.ImGui.GetIO().WantCaptureMouse)
            {
                float fov = _player.Camera.FOV;
                fov -= (float)yoffset * 2.0f;
                _player.Camera.FOV = float.Clamp(fov, 1.0f, 120.0f);
            }
        };

        _window.OnResize += (width, height) =>
        {
            _scrWidth = width;
            _scrHeight = height;
            _window.SetSize(width, height);
            _ui.SetScreenSize(width, height);
            _renderer.Resize(width, height);
        };

        _window.OnKeyPress += (key) =>
        {
            if (key == KeyCodes.F12) _renderer.ShadowsEnabled = !_renderer.ShadowsEnabled;
            if (key == KeyCodes.Escape)
            {
                _paused = !_paused;
                _window.CursorCaptureEnabled = !_paused;
                if (_paused) Input.Input.ShowCursor();
                else Input.Input.DisableCursor();
            }
        };
    }

    public void Run()
    {
        Logger.Info("Entering game loop");

        try
        {
            while (!_window.ShouldClose)
            {
                double now = _window.GlfwApi.GetTime();
                float dt = (float)(now - _lastTime);
                _lastTime = now;
                Engine.Tick(dt);

                Input.Input.Update();
                var gl = _window.GL;

                // Update
                if (!_paused)
                {
                    _pickup.PhysicsUpdate(dt);
                    _physics.Step(float.Min(dt, 0.0333f));
                    _player.Update(dt);
                    _audio.UpdateListener(_player.Camera.Position, _player.Camera.Front, _player.Camera.Up, _player.LinearVelocity);
                    _impactSound.Update(dt);
                    _pickup.Update(dt);
                    
                    _sceneManager.Update(dt);
                    _weaponSystem?.Update(dt);
                    _weaponSystem?.PhysicsUpdate(dt);

                    if (_sceneManager.CheckPendingResets())
                    {
                        ReloadMap(OnLoadProgress);
                    }
                }

                HandleInput();

                //UpdateViewmodelTransform();

                // Render
                _renderer.RenderFrame(_sceneManager.ActiveScene, _player.Camera, _physics);
                if (_screenshotRequested)
                {
                    _screenshotRequested = false;
                    TakeScreenshot(gl);
                }

                // UI
                DrawUI(gl);

                // ImGui
                _imgui.NewFrame(dt, _scrWidth, _scrHeight);
                if (_showImgui)
                    _imgui.DrawWindows(_player);

                // Debug
                if (_debugDrawer.Enabled)
                {
                    _debugDrawer.Clear();
                    _sceneManager.DrawDebug(_debugDrawer);
                    _debugDrawer.DrawPlayerDebug(_player);
                    foreach (var light in _sceneManager.ActiveScene.Lights)
                        _debugDrawer.DrawLight(light);
                    float aspect = (float)_scrWidth / _scrHeight;
                    _debugDrawer.Render(_player.Camera.GetViewMatrix(), _player.Camera.GetProjectionMatrix(aspect));
                    OrientationGizmo.Draw(_player.Camera);
                }

                if (_paused)
                {
                    ImGuiNET.ImGui.Begin("Shadow Settings");
                    ImGuiNET.ImGui.DragFloat("Bias Factor", ref _renderer.ShadowBiasFactor, 0.0001f, 0.0f, 0.1f, "%.5f");
                    ImGuiNET.ImGui.DragFloat("Bias Base", ref _renderer.ShadowBiasBase, 0.00001f, 0.0f, 0.01f, "%.6f");
                    ImGuiNET.ImGui.DragFloat("Near Plane", ref _renderer.ShadowNearPlane, 1.0f, -200.0f, 200.0f, "%.1f");
                    ImGuiNET.ImGui.DragFloat("Far Plane", ref _renderer.ShadowFarPlane, 1.0f, 10.0f, 1000.0f, "%.1f");
                    ImGuiNET.ImGui.DragFloat("Spread (Softness)", ref _renderer.ShadowSpread, 0.1f, 0.0f, 20.0f, "%.1f");
                    ImGuiNET.ImGui.End();
                }

                // Skinned Model Debug Window (separate from main menu)
                //if (_showSkinnedDebug)
                //{
                //    ImGuiNET.ImGui.Begin("Skinned Model Debug", ref _showSkinnedDebug, ImGuiNET.ImGuiWindowFlags.AlwaysAutoResize);
                    
                //    if (ImGuiNET.ImGui.CollapsingHeader("Skeleton Controls", ImGuiNET.ImGuiTreeNodeFlags.DefaultOpen))
                //    {
                //        bool bindPose = Animation.Skeleton.DebugBindPoseOnly;
                //        if (ImGuiNET.ImGui.Checkbox("Bind Pose Only", ref bindPose))
                //        {
                //            Animation.Skeleton.DebugBindPoseOnly = bindPose;
                //            Logger.InfoGold($"[Skinned] BindPoseOnly = {bindPose}");
                //        }
                        
                //        bool freezeTime = Animation.Skeleton.DebugFreezeTime;
                //        if (ImGuiNET.ImGui.Checkbox("Freeze Time (Key0)", ref freezeTime))
                //        {
                //            Animation.Skeleton.DebugFreezeTime = freezeTime;
                //            Logger.InfoGold($"[Skinned] FreezeTime = {freezeTime}");
                //        }
                        
                //        bool uploadRaw = Animation.Skeleton.DebugUploadRawGrid;
                //        if (ImGuiNET.ImGui.Checkbox("Upload Raw Grid", ref uploadRaw))
                //        {
                //            Animation.Skeleton.DebugUploadRawGrid = uploadRaw;
                //            Logger.InfoGold($"[Skinned] UploadRawGrid = {uploadRaw}");
                //        }
                //    }

                //    if (ImGuiNET.ImGui.CollapsingHeader("Animation Controls", ImGuiNET.ImGuiTreeNodeFlags.DefaultOpen))
                //    {
                //        if (_skinnedTestEntity?.Animator != null && _skinnedTestEntity.SkinnedModel != null)
                //        {
                //            var model = _skinnedTestEntity.SkinnedModel;
                //            var animator = _skinnedTestEntity.Animator;
                            
                //            ImGuiNET.ImGui.Text($"Current Clip: {animator.CurrentClip?.Name ?? "none"}");
                //            ImGuiNET.ImGui.Text($"Time: {animator.TimeSeconds:F3} / {animator.CurrentClip?.DurationTicks.ToString() ?? "?"} ticks");
                            
                //            float speed = animator.Speed;
                //            ImGuiNET.ImGui.Text($"Speed: {speed:F2}");
                //            if (ImGuiNET.ImGui.SliderFloat("Speed", ref speed, 0.0f, 3.0f))
                //                animator.Speed = speed;
                            
                //            bool playing = animator.Playing;
                //            if (ImGuiNET.ImGui.Checkbox("Playing", ref playing))
                //                animator.Playing = playing;
                            
                //            if (ImGuiNET.ImGui.Button("Dump Debug (F6)"))
                //                _skinnedTestEntity?.Animator?.DumpDebug();
                            
                //            ImGuiNET.ImGui.Separator();
                //            ImGuiNET.ImGui.Text("Available Clips:");
                //            var clipNames = new List<string>(model.Clips.Keys);
                //            foreach (var clipName in clipNames)
                //            {
                //                bool isCurrent = animator.CurrentClip?.Name == clipName;
                //                if (ImGuiNET.ImGui.Selectable(clipName, isCurrent))
                //                {
                //                    animator.Play(clipName);
                //                    Logger.InfoGold($"[Skinned] Clip: {clipName}");
                //                }
                //            }
                //        }
                //        else
                //        {
                //            ImGuiNET.ImGui.TextColored(new System.Numerics.Vector4(1, 0.5f, 0, 1), "No skinned entity spawned");
                //        }
                //    }

                //    //if (ImGuiNET.ImGui.CollapsingHeader("Debug Actions"))
                //    //{
                //    //    if (ImGuiNET.ImGui.Button("Dump Debug Now"))
                //    //        _skinnedTestEntity?.Animator?.DumpDebug();

                //    //    if (ImGuiNET.ImGui.Button("Spawn Glock Test"))
                //    //    {
                //    //        SpawnGlockTest();
                //    //        var keys = _skinnedTestEntity?.SkinnedModel?.Clips.Keys;
                //    //        var keyList = keys != null ? new List<string>(keys) : new List<string>();
                //    //        Logger.InfoGold($"[Skinned] Glock spawned. Clips: {string.Join(", ", keyList)} (UI to cycle)");
                //    //    }
                //    //}

                //    ImGuiNET.ImGui.End();
                //}

                // Weapon Viewmodel Debug
                //if (_weaponSystem != null && _weaponSystem.HasWeapon)
                //{
                //    if (ImGuiNET.ImGui.Begin("Weapon Viewmodel Debug", ImGuiNET.ImGuiWindowFlags.AlwaysAutoResize))
                //    {
                //        var vmOffset = _weaponSystem.ViewmodelOffset;
                //        if (ImGuiNET.ImGui.DragFloat3("Offset (X=Right, Y=Up, Z=Forward)", ref vmOffset, 0.01f, -2f, 2f))
                //            _weaponSystem.ViewmodelOffset = vmOffset;

                //        var vmRot = _weaponSystem.ViewmodelRotationDeg;
                //        if (ImGuiNET.ImGui.DragFloat3("Rotation Deg (Yaw, Pitch, Roll)", ref vmRot, 1f, -180f, 180f))
                //            _weaponSystem.ViewmodelRotationDeg = vmRot;

                //        if (ImGuiNET.ImGui.Button("Reset to Default"))
                //        {
                //            _weaponSystem.ViewmodelOffset = new Vector3(0.25f, -0.35f, 0.5f);
                //            _weaponSystem.ViewmodelRotationDeg = Vector3.Zero;
                //        }

                //        ImGuiNET.ImGui.Separator();

                //        // NOVO: Freeze viewmodel para debug
                //        bool freezeVM = _weaponSystem.FreezeViewmodel;
                //        if (ImGuiNET.ImGui.Checkbox("Freeze Viewmodel (stop following camera)", ref freezeVM))
                //            _weaponSystem.FreezeViewmodel = freezeVM;

                //        if (freezeVM)
                //        {
                //            ImGuiNET.ImGui.TextColored(new Vector4(1, 1, 0, 1), "VIEWMODEL FROZEN - Move camera to find it");
                //            if (ImGuiNET.ImGui.Button("Teleport to Camera"))
                //            {
                //                _weaponSystem.TeleportViewmodelToCamera();
                //            }
                //        }

                //        ImGuiNET.ImGui.Separator();
                //        ImGuiNET.ImGui.Text($"Current Weapon: {_weaponSystem.CurrentWeaponId}");
                //        ImGuiNET.ImGui.Text($"Viewmodel Entity: {(_weaponSystem.GetType().GetField("_viewmodelEntity", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(_weaponSystem) as Entity)?.Id ?? "null"}");
                //    }
                //    ImGuiNET.ImGui.End();
                //}


                _console.Draw();
                _imgui.Render();

                _window.SwapBuffers();
                _window.PollEvents();
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"Game loop exception: {ex.Message}\n{ex.StackTrace}");
        }

        Logger.Info("Exited game loop");
    }

    private void HandleInput()
    {
        if (_paused) return;

        if (Input.Input.KeyPressed(KeyCodes.F2)) _screenshotRequested = true;
        

        if (Input.Input.KeyPressed(KeyCodes.F5)) ReloadMap(OnLoadProgress);

        //use para edição de mapas futuros
        //if (Input.Input.KeyPressed(KeyCodes.F6))
        //{
        //    string savePath = _sceneManager.CurrentMapPath;
        //    var spawn = new Fuse.Scene.PlayerSpawn(
        //        _player.NativeCharacter.Position,
        //        _player.Camera.Yaw,
        //        _player.Camera.Pitch);
        //    Fuse.Scene.MapSerializer.SaveToFile(_sceneManager.ActiveScene, _physics, savePath, spawn);
        //}

        // Weapon input - troca/desequipar sempre funciona (fora do contexto)

        // Switch weapon (1, 2, 3...)
        if (Input.Input.KeyPressed(KeyCodes.D1))
            _weaponSystem?.SwitchWeapon("glock");
        if (Input.Input.KeyPressed(KeyCodes.D0))
            _weaponSystem?.Unequip();
        // Adicionar mais armas aqui no futuro

        // Shoot/Reload - só quando contexto Weapon ativo
        if (InputManager.IsContextActive(InputContext.Weapon))
        {
            // Shoot (Left Mouse - seguro para auto, pressionado para semi-auto)
            if (_weaponSystem?.CurrentWeapon?.IsAutomatic == true)
            {
                if (Input.Input.LeftMouseDown())
                    _weaponSystem?.TryShoot();
            }
            else
            {
                if (Input.Input.LeftMousePressed())
                    _weaponSystem?.TryShoot();
            }

            // Reload
            if (Input.Input.KeyPressed(KeyCodes.R))
            {
                _weaponSystem?.Reload();
            }
        }

        if (Input.Input.KeyPressed(KeyCodes.F9)) _debugDrawer.Toggle();

        if (Input.Input.KeyPressed(KeyCodes.GraveAccent))
        {
            _console.Toggle();
            if (_console.IsOpen) Input.Input.ShowCursor();
            else Input.Input.DisableCursor();
        }

        if (Input.Input.KeyPressed(KeyCodes.Insert))
            {
                _showImgui = !_showImgui;
                _showSkinnedDebug = _showImgui; // Abre o menu de skinned junto com o menu principal
            }

        if (Input.Input.KeyPressed(KeyCodes.G))
        {
            var cam = _player.Camera;
            var origin = cam.Position;
            var front = cam.Front;
            float maxDist = 20f;
            var dirScaled = front * maxDist;
            var ray = new Ray(ref origin, ref dirScaled);

            using var bpFilter = new DefaultBroadPhaseLayerFilter();
            using var olFilter = new DefaultObjectLayerFilter();
            using var bodyFilter = new DefaultBodyFilter();

            Vector3 target;
            if (_physics.NarrowPhaseQuery.CastRay(ray, out var hit, bpFilter, olFilter, bodyFilter))
                target = origin + front * maxDist * hit.Fraction;
            else
                target = origin + front * maxDist;

            Physics.Explosion.Apply(_physics, target, 105f, 10000.0f, _audio);
        }
    }

    private unsafe void TakeScreenshot(GL gl)
    {
        var pixels = new byte[_scrWidth * _scrHeight * 4];
        fixed (byte* ptr = pixels)
        {
            gl.ReadPixels(0, 0, (uint)_scrWidth, (uint)_scrHeight,
                PixelFormat.Bgra, PixelType.UnsignedByte, ptr);
        }

        // Flip Y: OpenGL y=0 é bottom, PNG y=0 é top
        var flipped = new byte[pixels.Length];
        int stride = _scrWidth * 4;
        for (int y = 0; y < _scrHeight; y++)
            System.Buffer.BlockCopy(pixels, y * stride,
                flipped, (_scrHeight - 1 - y) * stride, stride);

        string filename = $"screenshot_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.png";

        fixed (byte* ptr = flipped)
        {
            using var bmp = new System.Drawing.Bitmap(
                _scrWidth, _scrHeight, stride,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb,
                (nint)ptr);
            bmp.Save(filename, System.Drawing.Imaging.ImageFormat.Png);
        }

        Logger.Info($"Screenshot saved: {Path.GetFullPath(filename)}");
    }

    private void DrawUI(GL gl)
    {
        gl.Disable(EnableCap.DepthTest);
        gl.Disable(EnableCap.CullFace);
        gl.Enable(EnableCap.Blend);
        gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);

        if (_fpsText != null) _fpsText.Text = $"FPS: {Engine.FPS}";

        // Weapon debug
        if (_weaponDebugText != null && _weaponSystem?.CurrentWeapon != null)
        {
            var w = _weaponSystem.CurrentWeapon;
            _weaponDebugText.Text = $"Anim: {w.CurrentAnimState} | Time: {w.CurrentAnimTime:F2}s / {w.CurrentAnimDuration:F2}s";
        }
        else if (_weaponDebugText != null)
        {
            _weaponDebugText.Text = "No weapon equipped";
        }

        if (!_paused) _interaction.Update();

        _hud.Update(_scrWidth, _scrHeight);
        _hud.Draw(_ui, _scrWidth, _scrHeight);

        if (_paused)
        {
            Vector2 center = _ui.Center;
            _ui.DrawText(center.X - 60.0f, center.Y - 20.0f, "PAUSED".AsSpan(),
                new Vector4(1, 1, 0, 1), 3.0f);
        }

        gl.Disable(EnableCap.Blend);
        gl.Enable(EnableCap.CullFace);
        gl.Enable(EnableCap.DepthTest);
    }

    private void OnLoadProgress(float progress, string status)
    {
        _loadProgress = progress;
        _loadStatus = status;
        RenderLoadingScreen();
        _window.SwapBuffers();
        _window.PollEvents();
    }

    private void RenderLoadingScreen()
    {
        var gl = _window.GL;
        gl.Viewport(0, 0, (uint)_scrWidth, (uint)_scrHeight);
        gl.ClearColor(0.0f, 0.0f, 0.0f, 1.0f);
        gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

        gl.Disable(EnableCap.DepthTest);
        gl.Disable(EnableCap.CullFace);
        gl.Enable(EnableCap.Blend);
        gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);

        // Title
        string title = "LOADING";
        float titleW = title.Length * 6 * 2.5f;
        _ui.DrawText((_scrWidth - titleW) / 2, 50, title.AsSpan(), new Vector4(0, 1, 1, 1), 2.5f);

        // Progress bar background
        int barX = 100, barY = _scrHeight - 150, barW = _scrWidth - 200, barH = 24;
        _ui.DrawRect(barX, barY, barW, barH, new Vector4(0.2f, 0.2f, 0.2f, 1));

        // Progress bar fill
        if (_loadProgress > 0)
            _ui.DrawRect(barX, barY, (int)(barW * _loadProgress), barH, new Vector4(0, 0.6f, 0.8f, 1));

        // Progress text
        string pct = $"{_loadStatus} ({(int)(_loadProgress * 100)}%)";
        _ui.DrawText(barX, barY - 20, pct.AsSpan(), new Vector4(1, 1, 1, 1), 1.0f);

        // Recent logs
        var logs = Logger.GetRecentLogs(20);
        float logY = barY - 30;
        for (int i = logs.Length - 1; i >= 0 && logY > 60; i--)
        {
            var entry = logs[i];
            var color = entry.Level switch
            {
                LogLevel.Warn => new Vector4(1, 1, 0, 1),
                LogLevel.Error => new Vector4(1, 0.3f, 0.3f, 1),
                LogLevel.Important => new Vector4(0.4f, 0.6f, 1, 1),
                LogLevel.Asset => new Vector4(0.3f, 0.8f, 0.3f, 1),
                _ => new Vector4(0.7f, 0.7f, 0.7f, 1)
            };
            string text = $"[{entry.Level}] {entry.Message}";
            if (text.Length > 80) text = text[..80] + "...";
            _ui.DrawText(barX, logY, text.AsSpan(), color, 0.7f);
            logY -= 14;
        }

        gl.Disable(EnableCap.Blend);
        gl.Enable(EnableCap.CullFace);
        gl.Enable(EnableCap.DepthTest);
    }

    public void Dispose()
    {
        _sceneManager.Dispose();
        _weaponSystem?.Dispose();
        _console.StopCapture();
        _player.Dispose();
        _impactSound.Dispose();
        _audio.Dispose();
        _imgui.Shutdown();
        _ui.Dispose();
        _debugDrawer.Dispose();
        _assets.Clear();
        _physics.Dispose();
        _window.Dispose();
        Logger.Important("Application shutdown");
    }
}
