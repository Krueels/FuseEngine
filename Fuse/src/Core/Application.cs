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
    private double _physicsAccumulator;
    private const float FixedPhysicsDelta = 1.0f / 60.0f;
    private const int MaxPhysicsStepsPerFrame = 8;
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
    private OceanPhysicsSystem _oceanPhysics = null!;
    private Interaction.PlayerInteraction _interaction = null!;

    // Player & Gameplay
    private Player.Player _player = null!;
    private PickupController _pickup = null!;
    private Light _flashlight = null!;
    private WeaponSystem _weaponSystem = null!;
    private EnemySystem _enemySystem = null!;
    private DeathScreen? _deathScreen;

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
    private bool _preloadQueued;

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
        _audio.Profiler = _renderer.Profiler;
        _impactSound = new Audio.ImpactSoundSystem(_physics, _audio);
        Bible.QueuePreload(_assets, _audio);
        _preloadQueued = true;

        // UI, HUD & ImGui
        _ui = new UIRenderer(gl, _scrWidth, _scrHeight);
        _loadingScreen = new LoadingScreen();
        _hud = new GameplayHUD();
        _hud.Init(_assets);
        _hud.GetEnemyDebugHUD().SetDebugDrawer(_debugDrawer);

        // Font TTF
        string fontPath = Bible.Font(Bible.DefaultFont);
        Logger.Info($"FontAtlas: loading font from '{fontPath}' exists={File.Exists(fontPath)}");
        if (File.Exists(fontPath))
        {
            var fontAtlas = new FontAtlas(gl, fontPath, 10);
            _ui.SetFontAtlas(fontAtlas);
        }
        _imgui = new ImGuiBackEnd(gl);
        _imgui.Init();

        // Player setup
        _player = new Player.Player(_physics, new Vector3(0, 2, 0));
        _player.SetAudioSystem(_audio);
        _pickup = new PickupController(_physics, _player.Camera, default, _audio);
        _flashlight = CreatePlayerFlashlight();
        _player.SetFlashlight(_flashlight);
        _deathScreen = new DeathScreen(_window.GL, _assets);
        _player.OnPlayerDeath(() =>
        {
            _deathScreen?.Trigger();
            _weaponSystem?.Unequip();
        });
        _player.OnPlayerRespawn(() => _deathScreen?.Reset());

        // Console setup
        _console = new Fuse.Imgui.Console();
        _console.SetPlayer(_player);

        _console.StartCapture();
        _console.OnLoadMap = RequestMapLoad;
        _console.OnLoadSky = (fileName) =>
        {
            var tex = _assets.GetTexture($"{ResPath.Path}/Textures/{fileName}", Renderer.TextureColorSpace.Srgb);
            if (tex.ID == 0) Logger.Error($"Failed to load skybox: {fileName}");
            else _renderer.SetSkyboxTexture(tex);
        };

        // Systems Initialization
        _renderer.Init(_assets, _scrWidth, _scrHeight);
        _oceanPhysics = new OceanPhysicsSystem(
            _physics,
            _sceneManager.ActiveScene,
            _renderer);
        _interaction = new Interaction.PlayerInteraction(_sceneManager, _player, _hud.CrosshairNode,
            _assets.GetTexture(Bible.Tex(Bible.Crosshair)),
            _assets.GetTexture(Bible.Tex(Bible.CrosshairInteract)));

        // Weapon System
        _weaponSystem = new WeaponSystem(_player, _player.Camera, _physics, _assets, _audio, _sceneManager);
        _weaponSystem.RegisterWeapon(new GlockWeapon());
        _weaponSystem.RegisterWeapon(new AKWeapon());
        _weaponSystem.EnemySystem = _enemySystem;
        _enemySystem.SetPlayer(_player);

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
        Intensity = 10.0f,
        InnerConeAngle = float.DegreesToRadians(15),
        OuterConeAngle = float.DegreesToRadians(35),
        CastShadows = true,
        Dynamic = true,
        Enabled = false
    };

    private void LoadMap(string mapName, Action<float, string>? onProgress = null)
    {
        _assets.QueueMapPreload(mapName, AssetManagement.AssetPriority.Critical);
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
        if (_sceneManager.CurrentMapPath is { } currentMap)
            _assets.QueueMapPreload(currentMap, AssetManagement.AssetPriority.Critical);
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
        Logger.Important("Entering game loop");

        try
        {
            while (!_window.ShouldClose)
            {
                _renderer.Profiler.BeginFrame();
                double now = _window.GlfwApi.GetTime();
                float dt = float.Clamp((float)(now - _lastTime), 0.0f, 0.25f);
                _lastTime = now;
                // Tick records render timing/FPS only. Simulation time advances
                // once per fixed step below, keeping the rendered ocean and the
                // rigid-body state on the same clock even after a slow frame.
                Engine.Tick(dt, advanceSimulation: false);

                Input.Input.Update();
                var gl = _window.GL;
                float aspect = (float)_scrWidth / _scrHeight;
                Player.Player player = _player ?? throw new InvalidOperationException("Player is not initialized.");
                Camera camera = player.Camera;

                ProcessPendingMapChange();
                _assets.PumpGpuUploads(2);
                _audio.PumpPreloads(2);
                _weaponSystem?.ProcessPendingEquip();

                if (!_paused)
                {
                    UpdateUIFocus();

                    // One-shot player input must be processed once per render frame.
                    // In particular, flashlight toggling must not run once per fixed
                    // physics step when a slow frame executes multiple steps.
                    player.FrameInputUpdate();

                    _physicsAccumulator = System.Math.Min(
                        _physicsAccumulator + dt,
                        FixedPhysicsDelta * MaxPhysicsStepsPerFrame);

                    int physicsSteps = 0;
                    while (_physicsAccumulator >= FixedPhysicsDelta && physicsSteps < MaxPhysicsStepsPerFrame)
                    {
                        Engine.AdvanceSimulation(FixedPhysicsDelta);
                        UpdateFixedSimulation(FixedPhysicsDelta);
                        _physicsAccumulator -= FixedPhysicsDelta;
                        physicsSteps++;
                    }

                    // Drop excess accumulated time after a long stall. This keeps
                    // the simulation deterministic without a spiral of physics steps.
                    if (physicsSteps == MaxPhysicsStepsPerFrame &&
                        _physicsAccumulator >= FixedPhysicsDelta)
                        _physicsAccumulator = 0.0;

                    _audio.UpdateListener(camera.Position, camera.Front, camera.Up, player.LinearVelocity);
                }
                else
                    _physicsAccumulator = 0.0;

                HandleInput();

                // Interaction and pickup input are edge-triggered and must be
                // evaluated once per rendered frame. The held body's force
                // application remains in UpdateFixedSimulation.
                if (!_paused)
                {
                    _interaction.Update();
                    _pickup.FrameUpdate(dt);
                }

                // The weapon state is fixed-timestep, but the viewmodel follows
                // the camera and must be refreshed for every rendered frame.
                _weaponSystem?.RenderUpdate(dt);

                Matrix4x4 renderView = _weaponSystem?.RenderViewMatrix ?? camera.GetViewMatrix();

                // Particle & Decal Updates
                _weaponSystem?.Render(_renderer, camera, aspect);
                _renderer.UpdateDecals(dt, _physics);


                // World Render
                _renderer.RenderFrame(_sceneManager.ActiveScene, camera, _physics, renderView);

                //deathscren
                _deathScreen?.Update(dt, _player.IsDead);
                _deathScreen?.Render(_renderer.PostPipeline!.HdrColorId, _scrWidth, _scrHeight, (float)now);


                // ImGui & Console Frame
                _imgui.NewFrame(dt, _scrWidth, _scrHeight);
                if (_showImgui)
                    _imgui.DrawWindows(player, _renderer);

                // Debug Drawer (OrientationGizmo uses ImGui foreground draw list, must run before _imgui.Render)
                if (_debugDrawer.Enabled)
                    RenderDebug(aspect, renderView);

                if (_screenshotRequested)
                {
                    _screenshotRequested = false;
                    ScreenshotService.Capture(gl, _scrWidth, _scrHeight);
                }

                // HUD Draw
                _hud.Update(_weaponSystem, _enemySystem, _enemySelectionMode, player, camera, _scrWidth, _scrHeight);
                GameNotify.Update(dt);
                _hud.Draw(gl, _ui, _scrWidth, _scrHeight, _paused, _interaction);

                _console.Draw();
                _imgui.Render();

                _window.SwapBuffers();
                _window.PollEvents();
                _renderer.Profiler.EndFrame();

            }
        }
        catch (Exception ex)
        {
            Logger.Error($"Game loop exception: {ex.Message}\n{ex.StackTrace}");
        }

        Logger.Important("Exited game loop");
    }

    private void UpdateFixedSimulation(float fixedDt)
    {
        using (var physicsScope = _renderer.Profiler.Measure(ProfilerSection.Physics))
        {
            _pickup.PhysicsUpdate(fixedDt);
            _oceanPhysics.DebugEnabled = _debugDrawer.Enabled;
            _oceanPhysics.ApplyBuoyancy(fixedDt, Engine.Time);
            _physics.Step(fixedDt);
        }

        using (var playerPhysicsScope = _renderer.Profiler.Measure(ProfilerSection.Physics))
        {
            OceanPlayerWaterState waterState = _oceanPhysics.SamplePlayerWater(
                _player.Position,
                _player.WaterCapsuleRadius,
                _player.WaterCapsuleCylinderHeight,
                Engine.Time);
            _player.Update(fixedDt, waterState, _renderer.Ocean);
        }

        _impactSound.Update(fixedDt);
        _sceneManager.Update(fixedDt);
        _sceneManager.UpdateTerrainStreaming(_player.Position);
        _weaponSystem?.Update(fixedDt);

        using (var weaponPhysicsScope = _renderer.Profiler.Measure(ProfilerSection.Physics))
            _weaponSystem?.PhysicsUpdate(fixedDt);

        using (var spiderAiScope = _renderer.Profiler.Measure(ProfilerSection.SpiderAi))
        {
            _enemySystem?.Update(fixedDt);
            _enemySystem?.UpdateContactDamage(fixedDt);
        }

        if (_sceneManager.CheckPendingResets())
            RequestMapReload();
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
        if (_player is not { } player) return;

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
        if (_enemySelectionMode && _enemySystem != null)
        {
            if (Input.Input.LeftMousePressed())
            {
                var mousePos = Input.Input.MousePosition;
                var ray = player.Camera.GetMouseRay(mousePos, _renderer.Width, _renderer.Height);

                IEnemy? hitEnemy = null;
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
            player,
            _weaponSystem,
            _enemySystem,
            _physics,
            _audio,
            _assets,
            _renderer,
            _deathScreen,
            _debugDrawer,
            ref _screenshotRequested,
            RequestMapReload);
    }
    private void RenderDebug(float aspect, Matrix4x4? renderViewOverride = null)
    {
        _debugDrawer.Clear();
        _debugDrawer.InvokeOnDrawGizmos();
        _sceneManager.DrawDebug(_debugDrawer);

        // Draw the exact active render mesh of visible terrain chunks. The
        // per-LOD colors make transitions and unexpected forced LODs obvious,
        // while the frustum prevents a large terrain from overwhelming the
        // diagnostic pass with off-screen chunks.
        ViewFrustum terrainDebugFrustum = new(
            _player.Camera.GetViewMatrix() * _player.Camera.GetProjectionMatrix(aspect));
        foreach (Entity entity in _sceneManager.ActiveScene.Entities)
        {
            if (entity.TerrainLod == null || !entity.Visible)
                continue;
            if (terrainDebugFrustum.Intersects(entity.GetWorldRenderBounds()))
                _debugDrawer.DrawTerrainLod(entity);
        }

        _debugDrawer.DrawPlayerDebug(_player);
        _oceanPhysics.DrawDebug(_debugDrawer);
        _enemySystem?.DrawDebug(_renderer, _player.Camera, aspect, renderViewOverride);

        // Skinned model skeleton debug
        foreach (var e in _sceneManager.ActiveScene.Entities)
        {
            if (e.SkinnedModel != null && e.Animator != null && e.Visible)
            {
                var modelMatrix = Matrix4x4.CreateScale(e.Transform.Scale * e.ModelScale) *
                                  Matrix4x4.CreateFromQuaternion(e.ModelRotation) *
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
        _debugDrawer.Render(
            renderViewOverride ?? _player.Camera.GetViewMatrix(),
            _player.Camera.GetProjectionMatrix(aspect));
        OrientationGizmo.Draw(_player.Camera);
    }

    private void OnLoadProgress(float progress, string status)
    {
        if (!_preloadQueued)
        {
            Bible.QueuePreload(_assets, _audio);
            _preloadQueued = true;
        }
        _assets.PumpGpuUploads(4);
        _audio.PumpPreloads(4);
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
