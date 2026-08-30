using System;
using Fuse.Imgui;

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
        }
        catch (Exception ex)
        {
            System.Console.Error.WriteLine(ex.Message);
            return false;
        }

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

        _viewport3D = new EditorViewport(gl, CameraViewType.Perspective3D);
        _viewportTop = new EditorViewport(gl, CameraViewType.Top);
        _viewportFront = new EditorViewport(gl, CameraViewType.Front);
        _viewportSide = new EditorViewport(gl, CameraViewType.Side);
        _ui = new EditorUI();
        _history = new CommandHistory();

        return true;
    }

    public void Run()
    {
        double lastTime = _window.Glfw.GetTime();

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
            _imgui.NewFrame(dt, fbWidth, fbHeight);

            // Render Viewports
            _viewport3D.BeginRender();
            _viewport3D.RenderScene(_assetService, _sceneService, _ui.SnapGrid);
            _viewport3D.RenderDebug(_assetService, _sceneService, _ui.DrawPreviewDebug);
            _viewport3D.EndRender(fbWidth, fbHeight);

            _viewportTop.BeginRender();
            _viewportTop.RenderScene(_assetService, _sceneService, _ui.SnapGrid);
            _viewportTop.RenderDebug(_assetService, _sceneService, _ui.DrawPreviewDebug);
            _viewportTop.EndRender(fbWidth, fbHeight);

            _viewportFront.BeginRender();
            _viewportFront.RenderScene(_assetService, _sceneService, _ui.SnapGrid);
            _viewportFront.RenderDebug(_assetService, _sceneService, _ui.DrawPreviewDebug);
            _viewportFront.EndRender(fbWidth, fbHeight);

            _viewportSide.BeginRender();
            _viewportSide.RenderScene(_assetService, _sceneService, _ui.SnapGrid);
            _viewportSide.RenderDebug(_assetService, _sceneService, _ui.DrawPreviewDebug);
            _viewportSide.EndRender(fbWidth, fbHeight);

            // Render UI
            _ui.Draw(_window, _viewport3D, _viewportTop, _viewportFront, _viewportSide, _sceneService, _assetService, _history);

            _imgui.Render();
            _window.SwapBuffers();
            _window.PollEvents();
        }
    }

    public void Dispose()
    {
        _viewport3D.Dispose();
        _viewportTop.Dispose();
        _viewportFront.Dispose();
        _viewportSide.Dispose();
        _ui?.Dispose();
        _assetService.Dispose();
        _imgui.Dispose();
        _window.Dispose();
    }
}
