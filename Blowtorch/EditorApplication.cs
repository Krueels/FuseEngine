using System;
using System.Diagnostics;
using Fuse.Imgui;
using Fuse.Core;

namespace Blowtorch;

public unsafe class EditorApplication : IDisposable
{
    private EditorWindow _window = null!;
    private EditorInputService _inputService = null!;
    private ImGuiBackEnd _imgui = null!;
    private EditorAssetService _assetService = null!;
    private EditorSceneService _sceneService = null!;
    private EditorViewport _viewport3D = null!;
    private EditorViewport _viewportTop = null!;
    private EditorViewport _viewportFront = null!;
    private EditorViewport _viewportSide = null!;
    private EditorUI _ui = null!;
    private CommandHistory _history = null!;

    public bool Init()
    {
        try
        {
            _window = new EditorWindow("Blowtorch", 1280, 800);
            var gl = _window.GL;
            var handle = _window.Handle;
            var glfw = _window.Glfw;

            // Initialize Services
            _inputService = new EditorInputService();
            _inputService.Initialize(glfw, handle);

            _imgui = new ImGuiBackEnd(gl);
            _imgui.Init();

        // The material graph uses custom-drawn nodes and pins. Restrict window
        // movement to title bars so dragging graph elements never drags the
        // containing ImGui window as well.
            ImGuiNET.ImGui.GetIO().ConfigWindowsMoveFromTitleBarOnly = true;

            _assetService = new EditorAssetService(gl);
            _assetService.Initialize(AppContext.BaseDirectory);

            _sceneService = new EditorSceneService();
            _sceneService.LoadMap(_assetService.FuseResPath);
            _sceneService.PopulateScene(_assetService);

            _viewport3D = new EditorViewport(gl, CameraViewType.Perspective3D, _assetService.ImageBasedLighting);
            _viewportTop = new EditorViewport(gl, CameraViewType.Top, _assetService.ImageBasedLighting);
            _viewportFront = new EditorViewport(gl, CameraViewType.Front, _assetService.ImageBasedLighting);
            _viewportSide = new EditorViewport(gl, CameraViewType.Side, _assetService.ImageBasedLighting);
            _ui = new EditorUI();
            _history = new CommandHistory();

            return true;
        }
        catch (Exception ex)
        {
            System.Console.Error.WriteLine($"Blowtorch initialization failed: {ex}");
            return false;
        }
    }

    public void Run()
    {
        double lastTime = _window.Glfw.GetTime();

        try
        {
            while (!_window.ShouldClose)
            {
                double now = _window.Glfw.GetTime();
                float dt = (float)(now - lastTime);
                lastTime = now;

                _window.Glfw.GetFramebufferSize(_window.Handle, out int fbWidth, out int fbHeight);
                if (fbWidth <= 0 || fbHeight <= 0)
                {
                    _window.PollEvents();
                    System.Threading.Thread.Sleep(16);
                    continue;
                }
                var gl = _window.GL;
                gl.Viewport(0, 0, (uint)fbWidth, (uint)fbHeight);
                gl.ClearColor(0.12f, 0.12f, 0.14f, 1.0f);
                gl.Clear(Silk.NET.OpenGL.ClearBufferMask.ColorBufferBit | Silk.NET.OpenGL.ClearBufferMask.DepthBufferBit);

                _inputService.Update();
                _inputService.BeginFrame();
                _assetService.UpdateFileChanges(_sceneService);
                _imgui.NewFrame(dt, fbWidth, fbHeight);

                // Build the UI first. It records which viewport images are actually visible.
                _ui.Draw(_window, _viewport3D, _viewportTop, _viewportFront, _viewportSide, _sceneService, _assetService, _history, _inputService);

                bool continuousViewportRender = _ui.RequiresContinuousViewportRender;
                RenderViewportIfNeeded(_viewport3D, fbWidth, fbHeight, continuousViewportRender);
                RenderViewportIfNeeded(_viewportTop, fbWidth, fbHeight, continuousViewportRender);
                RenderViewportIfNeeded(_viewportFront, fbWidth, fbHeight, continuousViewportRender);
                RenderViewportIfNeeded(_viewportSide, fbWidth, fbHeight, continuousViewportRender);

                _imgui.Render();
                _window.SwapBuffers();
                _window.PollEvents();
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"Blowtorch stopped after an unrecoverable frame error: {ex}");
            System.Console.Error.WriteLine(ex);
        }

        void RenderViewportIfNeeded(EditorViewport viewport, int fbWidth, int fbHeight, bool forceContinuous)
        {
            if (!viewport.ShouldRender(_sceneService.Revision, _assetService.AssetRevision, forceContinuous))
                return;

            long start = Stopwatch.GetTimestamp();
            viewport.BeginRender();
            viewport.RenderScene(_assetService, _sceneService, _ui.SnapGrid);
            viewport.RenderDebug(_assetService, _sceneService, _ui.DrawPreviewDebug);
            viewport.EndRender(fbWidth, fbHeight);
            viewport.LastRenderMilliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            viewport.MarkRendered(_sceneService.Revision, _assetService.AssetRevision);
        }
    }

    public void Dispose()
    {
        _viewport3D?.Dispose();
        _viewportTop?.Dispose();
        _viewportFront?.Dispose();
        _viewportSide?.Dispose();
        _ui?.Dispose();
        _assetService?.Dispose();
        _imgui?.Dispose();
        _window?.Dispose();
    }
}
