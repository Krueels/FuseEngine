using System.Numerics;
using Fuse.Imgui;
using Fuse.Input;
using Fuse.Physics;
using Fuse.Renderer;
using Fuse.Player;
using Fuse.Player.Weapons;
using Fuse.Enemy;
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
    private Enemy.EnemySystem _enemySystem = null!;

    // UI & Debug
    private Renderer.UIRenderer _ui = null!;
    private UI.HUD _hud = null!;
    private UI.HUDText _fpsText = null!;
    private UI.HUDImage _crosshairNode = null!;
    private UI.HUDText _weaponDebugText = null!;
    private UI.HUDText _ammoText = null!;
    private UI.HUDText _reserveAmmoText = null!;
    private Debug.DebugDrawer _debugDrawer = null!;
    private Imgui.ImGuiBackEnd _imgui = null!;
    private bool _showImgui = false;
    private bool _showSkinnedDebug = false;
    private Imgui.Console _console = null!;
    private bool _consoleJustToggled = false;
    private float _loadProgress;
    private string _loadStatus = "";
    private string? _pendingMapLoad;
    private bool _pendingMapReload;

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
        _sceneManager = new Scene.SceneManager(_physics, _assets, _renderer);
        _enemySystem = new Enemy.EnemySystem(_physics, _sceneManager, _assets);
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
            CastShadows = true,
            Dynamic = true,
            Enabled = false
        };
        _player.SetFlashlight(_flashlight);

        // Console
        _console = new Imgui.Console();
        _console.SetPlayer(_player);
        _console.StartCapture();
        // A consola é desenhada durante o frame ImGui. Adiamos a troca de mapa
        // para o inicio do proximo frame, quando nenhum draw list/FBO esta em uso.
        _console.OnLoadMap = RequestMapLoad;
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
        var crosshairTexture = _assets.GetTexture(Bible.Tex(Bible.Crosshair));
        var crosshairInteractTexture = _assets.GetTexture(Bible.Tex(Bible.CrosshairInteract));
        _crosshairNode = _hud.AddImage(crosshairTexture, UI.HUDAnchor.Center, Vector2.Zero, new Vector2(8, 8));
        _weaponDebugText = _hud.AddText("Weapon Debug", UI.HUDAnchor.TopLeft, new Vector2(20, 50), 1.0f, new Vector4(0, 1, 0, 1));
        
        _ammoText = _hud.AddText("0", UI.HUDAnchor.BottomRight, new Vector2(-320, -120), 2.5f, new Vector4(1, 1, 1, 1));
        _reserveAmmoText = _hud.AddText("0", UI.HUDAnchor.BottomRight, new Vector2(-320, -100), 1.5f, new Vector4(0.7f, 0.7f, 0.7f, 1));

        // Initialization
        _renderer.Init(_assets, _scrWidth, _scrHeight);
        _interaction = new Interaction.PlayerInteraction(_physics, _player, _crosshairNode, crosshairTexture, crosshairInteractTexture);

        // Weapon System
        _weaponSystem = new Player.WeaponSystem(_player, _player.Camera, _physics, _assets, _audio, _sceneManager);
        _weaponSystem.RegisterWeapon(new GlockWeapon());
        _weaponSystem.EnemySystem = _enemySystem;

        // Default Map Loading
        LoadMap(initialMap, OnLoadProgress);
        _sceneManager.ActiveScene.AddLight(_flashlight);

        _weaponSystem.Equip("glock");

        //spawn inimigo teste
        _enemySystem.SpawnEnemy(new Vector3(5, 1, 5), 50);

        RegisterWindowCallbacks();

        _lastTime = _window.GlfwApi.GetTime();
        Logger.Info(":: Application ready ::");
        return true;
    }

    private void LoadMap(string mapName, Action<float, string>? onProgress = null)
    {
        _impactSound?.Clear();
        _enemySystem?.Clear();
        var spawn = _sceneManager.LoadMap(mapName, onProgress);
        if (spawn.HasValue)
        {
            _player.NativeCharacter.Position = spawn.Value.Position;
            _player.NativeCharacter.LinearVelocity = Vector3.Zero;
            _player.Camera.SetRotation(spawn.Value.Yaw, spawn.Value.Pitch);
        }
        _sceneManager.InitTriggerSystem(_player);
        _sceneManager.ActiveScene.AddLight(_flashlight);
        _weaponSystem?.Equip("glock");
    }

    private void ReloadMap(Action<float, string>? onProgress = null)
    {
        _impactSound?.Clear();
        _enemySystem?.Clear();
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
        _weaponSystem?.Equip("glock");
    }

    private void RequestMapLoad(string mapName)
    {
        _pendingMapLoad = mapName;
        _pendingMapReload = false;
    }

    private void RequestMapReload()
    {
        if (_pendingMapLoad == null)
            _pendingMapReload = true;
    }

    private void ProcessPendingMapChange()
    {
        if (_pendingMapLoad is { } mapName)
        {
            _pendingMapLoad = null;
            LoadMap(mapName, OnLoadProgress);
            return;
        }

        if (_pendingMapReload)
        {
            _pendingMapReload = false;
            ReloadMap(OnLoadProgress);
        }
    }

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
                _audio.SetPaused(_paused);
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

                // Processa loads fora da fase de render/ImGui. Isto evita trocar
                // recursos da cena enquanto o frame HDR anterior ainda esta ativo.
                ProcessPendingMapChange();

                // Update
                if (!_paused)
                {
                    _pickup.PhysicsUpdate(dt);
                    _physics.Step(float.Min(dt, 0.0333f));

                    // ImGui focus detection — gerencia contexto UI automaticamente
                    bool imguiWantsKeyboard = ImGuiNET.ImGui.GetIO().WantCaptureKeyboard;
                    bool imguiWantsMouse = ImGuiNET.ImGui.GetIO().WantCaptureMouse;
                    bool uiFocused = imguiWantsKeyboard || imguiWantsMouse;

                    if (uiFocused && !InputManager.IsContextActive(InputContext.UI))
                        InputManager.RequestContext(InputContext.UI);
                    else if (!uiFocused && InputManager.IsContextActive(InputContext.UI))
                        InputManager.ReleaseContext(InputContext.UI);

                    // Auto-close console se ImGui perdeu foco (clicou fora) — ignora frame do toggle
                    if (_console.IsOpen && !uiFocused && !_consoleJustToggled)
                    {
                        _console.Toggle();
                        Input.Input.DisableCursor();
                        InputManager.ReleaseContext(InputContext.UI);
                    }
                    _consoleJustToggled = false;

                    _player.Update(dt);

                    _audio.UpdateListener(_player.Camera.Position, _player.Camera.Front, _player.Camera.Up, _player.LinearVelocity);
                    _impactSound.Update(dt);
                    _pickup.Update(dt);

                    _sceneManager.Update(dt);
                    _weaponSystem?.Update(dt);
                    _weaponSystem?.PhysicsUpdate(dt);
                    _enemySystem?.Update(dt);

                    if (_sceneManager.CheckPendingResets())
                    {
                        RequestMapReload();
                    }
                }

                HandleInput();

                // Queue billboards for HDR FBO (muzzle flash)
                if (_weaponSystem?.MuzzleFlashVisible == true && _weaponSystem.MuzzleFlashTexture != null)
                {
                    var view = _player.Camera.GetViewMatrix();
                    var proj = _player.Camera.GetProjectionMatrix((float)_scrWidth / _scrHeight);
                    _renderer.QueueBillboard(view, proj,
                        _weaponSystem.MuzzleFlashTexture.ID,
                        _weaponSystem.MuzzleFlashPosition,
                        _weaponSystem.MuzzleFlashSize,
                        new Vector4(1, 1, 1, 1));
                }
                else if (_weaponSystem?.MuzzleFlashVisible == true)
                {
                    Logger.Warn("[MuzzleFlash] Visível mas textura é null!");
                }

                _renderer.UpdateDecals(dt); // age decals
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
                { 
                    _imgui.DrawWindows(_player, _renderer);
                }

                // Debug
                if (_debugDrawer.Enabled)
                {
                    _debugDrawer.Clear();
                    _sceneManager.DrawDebug(_debugDrawer);
                    _debugDrawer.DrawPlayerDebug(_player);
                    if (_enemySystem != null)
                    {
                        var enemyTex = _assets.GetTexture(Bible.Tex(Bible.EnemyIcon));
                        var view = _player.Camera.GetViewMatrix();
                        var proj = _player.Camera.GetProjectionMatrix((float)_scrWidth / _scrHeight);
                        foreach (var enemy in _enemySystem.GetEnemies())
                        {
                            if (!enemy.IsDead)
                            {
                                var pos = enemy.Entity.Transform.Position;
                                pos.Y += 2.0f; // acima da cápsula
                                _renderer.QueueBillboard(view, proj, enemyTex.ID, pos, new Vector2(0.5f, 0.5f), new Vector4(1, 0, 0, 0.8f));
                            }
                        }
                    }
                    // Skinned model skeleton debug
                    foreach (var e in _sceneManager.ActiveScene.Entities)
                    {
                        if (e.SkinnedModel != null && e.Animator != null && e.Visible)
                        {
                            // Usar a MESMA model matrix do renderer (com ModelScale + ModelOffset)
                            var modelMatrix = Matrix4x4.CreateScale(e.Transform.Scale * e.ModelScale) *
                                              Matrix4x4.CreateFromQuaternion(e.Transform.Rotation) *
                                              Matrix4x4.CreateTranslation(e.Transform.Position + e.ModelOffset);

                            _debugDrawer.DrawSkeletonFromBones(
                                e.Animator.FinalBoneMatrices,
                                e.SkinnedModel.Skeleton.Bones,
                                e.SkinnedModel.Skeleton.Nodes,
                                modelMatrix);
                        }
                    }
                    //foreach (var e in _sceneManager.ActiveScene.Entities)
                    //{
                    //    if (e.SkinnedModel != null && e.Animator != null && e.Visible)
                    //    {
                    //        var bones = e.SkinnedModel.Skeleton.Bones;
                    //        var mats = e.Animator.FinalBoneMatrices;
                    //        for (int b = 0; b < MathF.Min(bones.Length, 3); b++)
                    //        {
                    //            var bone = bones[b];
                    //            if (bone.NodeIndex < 0) continue;
                    //            var m = mats[bone.Index];
                    //            Logger.Info($"[BoneDebug] '{bone.Name}' pos=({m.M14:F2}, {m.M24:F2}, {m.M34:F2})");
                    //        }
                    //    }
                    //}
                    foreach (var light in _sceneManager.ActiveScene.Lights)
                        _debugDrawer.DrawLight(light);

                    if (_renderer != null)
                        _debugDrawer.DrawDecalsDebug(_renderer.DecalQueue);

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

                //Weapon Viewmodel Debug
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

                // Muzzle Flash Offset Debug
                //if (_weaponSystem?.HasWeapon == true && ImGuiNET.ImGui.Begin("Muzzle Flash Debug", ImGuiNET.ImGuiWindowFlags.AlwaysAutoResize))
                //{
                //    bool forceOn = _weaponSystem.ForceMuzzleFlash;
                //    if (ImGuiNET.ImGui.Checkbox("Force Always On", ref forceOn))
                //        _weaponSystem.ForceMuzzleFlash = forceOn;

                //    var offset = _weaponSystem.MuzzleFlashOffsetEdit;
                //    if (ImGuiNET.ImGui.DragFloat3("Offset (X, Y, Z)", ref offset, 0.01f))
                //        _weaponSystem.MuzzleFlashOffsetEdit = offset;

                //    var size = _weaponSystem.MuzzleFlashSizeEdit;
                //    if (ImGuiNET.ImGui.DragFloat2("Size (X, Y)", ref size, 0.01f))
                //        _weaponSystem.MuzzleFlashSizeEdit = size;

                //    ImGuiNET.ImGui.Text($"Visible: {_weaponSystem.MuzzleFlashVisible}");
                //    ImGuiNET.ImGui.Text($"Position: {_weaponSystem.MuzzleFlashPosition.X:F2}, {_weaponSystem.MuzzleFlashPosition.Y:F2}, {_weaponSystem.MuzzleFlashPosition.Z:F2}");
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


        if (Input.Input.KeyPressed(KeyCodes.F5)) RequestMapReload();

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

        // Console toggle — SEMPRE funciona (prioridade Debug/fora do bloqueio)
        if (Input.Input.KeyPressed(KeyCodes.GraveAccent))
        {
            _console.Toggle();
            _consoleJustToggled = true;
            if (_console.IsOpen)
            {
                Input.Input.ShowCursor();
                InputManager.RequestContext(InputContext.UI);
            }
            else
            {
                Input.Input.DisableCursor();
                InputManager.ReleaseContext(InputContext.UI);
            }
        }

        // Insert menu — SEMPRE funciona
        if (Input.Input.KeyPressed(KeyCodes.Insert))
        {
            _showImgui = !_showImgui;
            _showSkinnedDebug = _showImgui; // Abre o menu de skinned junto com o menu principal
            if (_showImgui)
            {
                Input.Input.ShowCursor();
                InputManager.RequestContext(InputContext.UI);
            }
            else
            {
                Input.Input.DisableCursor();
                InputManager.ReleaseContext(InputContext.UI);
            }
        }

        // Debug keys sempre funcionam (contexto Debug prioridade 100)
        if (Input.Input.KeyPressed(KeyCodes.F9)) _debugDrawer.Toggle();

        if (Input.Input.KeyPressed(KeyCodes.F10))
        {
            _renderer.PostPipeline.Settings.Enabled = !_renderer.PostPipeline.Settings.Enabled;
        }

        // G: spawnar explosão no raycast
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

        // J: spawnar inimigo no raycast
        if (Input.Input.KeyPressed(KeyCodes.J))
        {
            var cam = _player.Camera;
            var origin = cam.Position;
            var front = cam.Front;
            float maxDist = 30f;
            var dirScaled = front * maxDist;
            var ray = new Ray(ref origin, ref dirScaled);

            using var bpFilter = new DefaultBroadPhaseLayerFilter();
            using var olFilter = new DefaultObjectLayerFilter();
            using var bodyFilter = new DefaultBodyFilter();

            Vector3 spawnPos;
            if (_physics.NarrowPhaseQuery.CastRay(ray, out var hit, bpFilter, olFilter, bodyFilter))
                spawnPos = origin + front * maxDist * hit.Fraction - front * 1f;
            else
                spawnPos = origin + front * maxDist;

            _enemySystem?.SpawnEnemy(spawnPos, 50f);
        }

        // Switch weapon (1, 2, 3...)
        if (Input.Input.KeyPressed(KeyCodes.D1))
            _weaponSystem?.SwitchWeapon("glock");
        if (Input.Input.KeyPressed(KeyCodes.D0))
            _weaponSystem?.Unequip();

        // Weapon input só se CurrentContext == Weapon
        if (InputManager.CurrentContext == InputContext.Weapon)
        {
            // Shoot/Reload
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
            if (Input.Input.KeyPressed(KeyCodes.R))
                _weaponSystem?.Reload();
        }

        if (Input.Input.KeyPressed(KeyCodes.T))
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

            Vector3 hitPos;
            if (_physics.NarrowPhaseQuery.CastRay(ray, out var hit, bpFilter, olFilter, bodyFilter))
                hitPos = origin + front * maxDist * hit.Fraction;
            else
                hitPos = origin + front * maxDist;

            Vector3 hitNormal = -dirScaled;
            var rigidBody = _sceneManager.GetRigidBody(hit.BodyID);
            if (rigidBody != null)
            {
                Vector3 bodyPos = rigidBody.Position(_physics);
                Quaternion bodyRot = rigidBody.Rotation(_physics);
                Vector3 localHit = Vector3.Transform(hitPos - bodyPos, Quaternion.Inverse(bodyRot));

                Vector3 ext = rigidBody.BoxHalfExtents;
                float rx = MathF.Abs(localHit.X) / MathF.Max(ext.X, 0.001f);
                float ry = MathF.Abs(localHit.Y) / MathF.Max(ext.Y, 0.001f);
                float rz = MathF.Abs(localHit.Z) / MathF.Max(ext.Z, 0.001f);

                Vector3 localNormal = (ry >= rx && ry >= rz) ? (localHit.Y >= 0 ? Vector3.UnitY : -Vector3.UnitY) :
                                      (rx >= ry && rx >= rz) ? (localHit.X >= 0 ? Vector3.UnitX : -Vector3.UnitX) :
                                                               (localHit.Z >= 0 ? Vector3.UnitZ : -Vector3.UnitZ);

                hitNormal = Vector3.Normalize(Vector3.Transform(localNormal, bodyRot));
            }

            uint sprayTexId = _assets.GetTexture(Bible.Tex("decals/afx.png")).ID;

            _sceneManager.Renderer.SpawnDecal(hitPos, hitNormal, sprayTexId, 1.0f);
            _audio?.Play3D(Bible.Audio("Audio/Spray.wav"), hitPos);

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

        if (_ammoText != null && _weaponSystem?.CurrentWeapon != null)
        {
            _ammoText.Text = $"{_weaponSystem.CurrentWeapon.CurrentAmmo}";
            _reserveAmmoText.Text = $"/{_weaponSystem.CurrentWeapon.ReserveAmmo}";
        }
        else if (_ammoText != null)
        {
            _ammoText.Text = "";
            _reserveAmmoText.Text = "";
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
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
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
        _enemySystem?.Dispose();
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
