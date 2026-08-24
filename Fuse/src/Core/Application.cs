using System.Numerics;
using Fuse.Enemy;
using Fuse.Imgui;
using Fuse.Input;
using Fuse.Physics;
using Fuse.Player;
using Fuse.Player.Weapons;
using Fuse.Renderer;
using Fuse.Scene;
using Fuse.UI;
using Silk.NET.OpenGL;

namespace Fuse.Core;

public unsafe class Application : IDisposable
{
    private readonly Window _window;
    private double _lastTime;
    private bool _paused;
    private int _scrWidth = 1280, _scrHeight = 800;
    private bool _screenshotRequested;

    // Core Systems
    private readonly PhysicsWorld _physics;
    private AssetManagement.AssetManager _assets = null!;
    private Audio.AudioSystem _audio = null!;
    private Audio.ImpactSoundSystem _impactSound = null!;
    private MasterRenderer _renderer = null!;
    private Scene.SceneManager _sceneManager = null!;
    private Interaction.PlayerInteraction _interaction = null!;

    // Player & Gameplay
    private Player.Player _player = null!;
    private PickupController _pickup = null!;
    private Light _flashlight = null!;
    private WeaponSystem _weaponSystem = null!;
    private EnemySystem _enemySystem = null!;

    // UI, HUD & Debug
    private UIRenderer _ui = null!;
    private GameplayHUD _hud = null!;
    private LoadingScreen _loadingScreen = null!;
    private Debug.DebugDrawer _debugDrawer = null!;
    private ImGuiBackEnd _imgui = null!;
    private Fuse.Imgui.Console _console = null!;
    private bool _showImgui;

    private bool _consoleJustToggled;

    private bool _enemySelectionMode = false;

    // Map Transition
    private string? _pendingMapLoad;
    private bool _pendingMapReload;

    public Application()
    {
        _window = new Window("Fuse", _scrWidth, _scrHeight);
        _physics = new PhysicsWorld(new Vector3(0, -9.81f, 0));
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
        _renderer = new MasterRenderer(gl);
        _sceneManager = new Scene.SceneManager(_physics, _assets, _renderer);
        _enemySystem = new EnemySystem(_physics, _sceneManager, _assets);
        _debugDrawer = new Debug.DebugDrawer(gl);

        // Audio
        _audio = new Audio.AudioSystem { GlobalVolume = 1.0f };
        _impactSound = new Audio.ImpactSoundSystem(_physics, _audio);

        // UI, HUD & ImGui
        _ui = new UIRenderer(gl, _scrWidth, _scrHeight);
        _loadingScreen = new LoadingScreen();
        _hud = new GameplayHUD();
        _hud.Init(_assets);
        _hud.GetEnemyDebugHUD().SetDebugDrawer(_debugDrawer);
        _imgui = new ImGuiBackEnd(gl);
        _imgui.Init();

        // Player setup
        _player = new Player.Player(_physics, new Vector3(0, 2, 0));
        _player.SetAudioSystem(_audio);
        _pickup = new PickupController(_physics, _player.Camera, default, _audio);
        _flashlight = CreatePlayerFlashlight();
        _player.SetFlashlight(_flashlight);

        // Console setup
        _console = new Fuse.Imgui.Console();
        _console.SetPlayer(_player);

        _console.StartCapture();
        _console.OnLoadMap = RequestMapLoad;
        _console.OnLoadSky = (fileName) =>
        {
            var tex = _assets.GetTexture($"{ResPath.Path}/Textures/{fileName}");
            if (tex.ID == 0) Logger.Error($"Failed to load skybox: {fileName}");
            else _renderer.SetSkyboxTexture(tex);
        };

        // Systems Initialization
        _renderer.Init(_assets, _scrWidth, _scrHeight);
        _interaction = new Interaction.PlayerInteraction(_sceneManager, _player, _hud.CrosshairNode,
            _assets.GetTexture(Bible.Tex(Bible.Crosshair)),
            _assets.GetTexture(Bible.Tex(Bible.CrosshairInteract)));

        // Weapon System
        _weaponSystem = new WeaponSystem(_player, _player.Camera, _physics, _assets, _audio, _sceneManager);
        _weaponSystem.RegisterWeapon(new GlockWeapon());
        _weaponSystem.EnemySystem = _enemySystem;

        // Default Map Loading
        LoadMap(initialMap, OnLoadProgress);

        RegisterWindowCallbacks();

        _lastTime = _window.GlfwApi.GetTime();
        Logger.Info(":: Application ready ::");
        return true;
    }

    private Light CreatePlayerFlashlight() => new()
    {
        Id = "player_flashlight",
        Type = LightType.Spot,
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
        _window.OnMouseMove += (dx, dy) => _player.Camera.ProcessMouseMovement((float)dx, (float)dy);

        _window.OnScroll += (yoffset) =>
        {
            if (!ImGuiNET.ImGui.GetIO().WantCaptureMouse)
            {
                float fov = _player.Camera.FOV - (float)yoffset * 2.0f;
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
                float aspect = (float)_scrWidth / _scrHeight;

                ProcessPendingMapChange();

                if (!_paused)
                {
                    _pickup.PhysicsUpdate(dt);
                    _physics.Step(float.Min(dt, 0.0333f));

                    UpdateUIFocus();

                    _player.Update(dt);
                    _audio.UpdateListener(_player.Camera.Position, _player.Camera.Front, _player.Camera.Up, _player.LinearVelocity);
                    _impactSound.Update(dt);
                    _pickup.Update(dt);

                    _sceneManager.Update(dt);
                    _weaponSystem?.Update(dt);
                    _weaponSystem?.PhysicsUpdate(dt);
                    _enemySystem?.Update(dt);

                    if (_sceneManager.CheckPendingResets())
                        RequestMapReload();
                }

                HandleInput();

                // Particle & Decal Updates
                _weaponSystem?.Render(_renderer, _player.Camera, aspect);
                _renderer.UpdateDecals(dt, _physics);


                // World Render
                _renderer.RenderFrame(_sceneManager.ActiveScene, _player.Camera, _physics);

                if (_screenshotRequested)
                {
                    _screenshotRequested = false;
                    ScreenshotService.Capture(gl, _scrWidth, _scrHeight);
                }

                // HUD Draw
                _hud.Update(_weaponSystem, _enemySystem, _player?.Camera, _scrWidth, _scrHeight);
                _hud.Draw(gl, _ui, _scrWidth, _scrHeight, _paused, _interaction);

                // ImGui & Console Frame
                _imgui.NewFrame(dt, _scrWidth, _scrHeight);
                if (_showImgui)
                    _imgui.DrawWindows(_player, _renderer);

                _console.Draw();

                // Debug Drawer (OrientationGizmo uses ImGui foreground draw list, must run before _imgui.Render)
                if (_debugDrawer.Enabled)
                    RenderDebug(aspect);

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

    private void UpdateUIFocus()
    {
        // Selection mode (F8) tem prioridade máxima - SEMPRE mantém UI context e cursor visível
        if (_enemySelectionMode)
        {
            if (!InputManager.IsContextActive(InputContext.UI))
                InputManager.RequestContext(InputContext.UI);
            if (Fuse.Input.Input.IsCursorDisabled())
                Fuse.Input.Input.ShowCursor();
            return;
        }

        bool imguiWantsKeyboard = ImGuiNET.ImGui.GetIO().WantCaptureKeyboard;
        bool imguiWantsMouse = ImGuiNET.ImGui.GetIO().WantCaptureMouse;
        bool uiFocused = imguiWantsKeyboard || imguiWantsMouse;

        if (uiFocused && !InputManager.IsContextActive(InputContext.UI))
            InputManager.RequestContext(InputContext.UI);
        else if (!uiFocused && InputManager.IsContextActive(InputContext.UI))
            InputManager.ReleaseContext(InputContext.UI);

        if (_console.IsOpen && !uiFocused && !_consoleJustToggled)
        {
            _console.Toggle();
            Fuse.Input.Input.DisableCursor();
            InputManager.ReleaseContext(InputContext.UI);
        }
        _consoleJustToggled = false;
    }

    private void HandleInput()
    {
        if (_paused) return;

        // Toggle Console
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

        // Toggle Menu
        if (Input.Input.KeyPressed(KeyCodes.Insert))
        {
            _showImgui = !_showImgui;
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

        if (_debugDrawer.Enabled)
        {
            // F8 - Toggle Enemy Selection Mode
            if (Input.Input.KeyPressed(KeyCodes.F8))
            {
                _enemySelectionMode = !_enemySelectionMode;
                if (_enemySelectionMode)
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
        }
        else
        {
            _enemySelectionMode = false;
        }
        // Garantir cursor visível durante selection mode (fallback)
        if (_enemySelectionMode && Fuse.Input.Input.IsCursorDisabled())
        {
            Fuse.Input.Input.ShowCursor();
        }

        // Enemy Selection Raycat (when in selection mode)
        if (_enemySelectionMode && _enemySystem != null && _player != null)
        {
            if (Input.Input.LeftMousePressed())
            {
                var mousePos = Input.Input.MousePosition;
                var ray = _player.Camera.GetMouseRay(mousePos, _renderer.Width, _renderer.Height);

                Fuse.Enemy.Enemy? hitEnemy = null;
                if (_physics.NarrowPhaseQuery.CastRay(ray, out var hit,
                    new Physics.DefaultBroadPhaseLayerFilter(),
                    new Physics.DefaultObjectLayerFilter(),
                    new Physics.DefaultBodyFilter()))
                {
                    if (hit.BodyID.IsValid)
                    {
                        _enemySystem.TryGetEnemy(hit.BodyID, out hitEnemy);
                    }
                }

                _hud.GetEnemyDebugHUD().SetTarget(hitEnemy);
            }
        }

        DevShortcuts.HandleInput(
            _sceneManager,
            _player,
            _weaponSystem,
            _enemySystem,
            _physics,
            _audio,
            _assets,
            _renderer,
            _debugDrawer,
            ref _screenshotRequested,
            RequestMapReload);
    }


    private void RenderDebug(float aspect)
    {
        _debugDrawer.Clear();
        _sceneManager.DrawDebug(_debugDrawer);
        _debugDrawer.DrawPlayerDebug(_player);
        _enemySystem?.DrawDebug(_renderer, _player.Camera, aspect);

        // Skinned model skeleton debug
        foreach (var e in _sceneManager.ActiveScene.Entities)
        {
            if (e.SkinnedModel != null && e.Animator != null && e.Visible)
            {
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

        foreach (var light in _sceneManager.ActiveScene.Lights)
            _debugDrawer.DrawLight(light);

        _debugDrawer.DrawDecalsDebug(_renderer.DecalQueue);
        _debugDrawer.Render(_player.Camera.GetViewMatrix(), _player.Camera.GetProjectionMatrix(aspect));
        OrientationGizmo.Draw(_player.Camera);
    }

    private void OnLoadProgress(float progress, string status)
    {
        _loadingScreen.UpdateProgress(progress, status, _window, _window.GL, _ui, _scrWidth, _scrHeight);
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
