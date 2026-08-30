using Silk.NET.GLFW;
using Silk.NET.OpenGL;
using Fuse.Input;

namespace Fuse.Core;

public unsafe class Window : IDisposable
{
    private static Window? s_instance;

    private readonly Glfw _glfw;
    private readonly WindowHandle* _handle;
    private readonly GL _gl = null!;
    private int _width;
    private int _height;
    private bool _firstMouse = true;
    private double _lastMX;
    private double _lastMY;

    public Window(string title, int width, int height)
    {
        _width = width;
        _height = height;

        _glfw = Glfw.GetApi();
        if (!_glfw.Init())
        {
            Logger.Error("Failed to initialize GLFW");
            return;
        }
        Logger.Important("GLFW Init");

        // Forward+ and the existing skinned shaders use OpenGL 4.3
        // shader-storage buffers and compute shaders.
        _glfw.WindowHint(WindowHintInt.ContextVersionMajor, 4);
        _glfw.WindowHint(WindowHintInt.ContextVersionMinor, 3);
        _glfw.WindowHint(WindowHintOpenGlProfile.OpenGlProfile, OpenGlProfile.Core);

        _handle = _glfw.CreateWindow(width, height, title, null, null);
        if (_handle == null)
        {
            Logger.Error("Failed to create window");
            _glfw.Terminate();
            return;
        }
        Logger.Important("WINDOW Init");

        _glfw.MakeContextCurrent(_handle);

        var monitor = _glfw.GetPrimaryMonitor();
        if (monitor != null)
        {
            var mode = _glfw.GetVideoMode(monitor);
            _glfw.GetMonitorPos(monitor, out int monitorX, out int monitorY);
            int x = monitorX + (mode->Width - width) / 2;
            int y = monitorY + (mode->Height - height) / 2;
            _glfw.SetWindowPos(_handle, x, y);
        }

        _gl = GL.GetApi(_glfw.GetProcAddress);
        Logger.Important("OpenGL Init");

        string renderer = _gl.GetStringS(StringName.Renderer);
        string vendor = _gl.GetStringS(StringName.Vendor);
        string version = _gl.GetStringS(StringName.Version);

        Logger.Important($"Vendor: {vendor}");
        Logger.Important($"GPU: {renderer}");
        Logger.Important($"OpenGL Version: {version}");

        _gl.Enable(EnableCap.DepthTest);
        _gl.Enable(EnableCap.CullFace);
        _gl.CullFace(GLEnum.Back);
        _gl.FrontFace(GLEnum.Ccw);

        s_instance = this;

        _glfw.SetFramebufferSizeCallback(_handle, OnFramebufferSizeCallback);
        _glfw.SetKeyCallback(_handle, OnKeyCallback);
        _glfw.SetMouseButtonCallback(_handle, OnMouseButtonCallback);
        _glfw.SetCursorPosCallback(_handle, OnCursorPosCallback);
        _glfw.SetScrollCallback(_handle, OnScrollCallback);
        _glfw.SetCharCallback(_handle, OnCharCallback);

        Input.Input.Init(_glfw, _handle);
        Logger.Important("Input Init");

    }

    public void Dispose()
    {
        if (_handle != null)
            _glfw.DestroyWindow(_handle);
        _glfw.Terminate();
        Logger.Info("GLFW shutdown");
        s_instance = null;
    }

    public GL GL => _gl;
    public Glfw GlfwApi => _glfw;
    public WindowHandle* Handle => _handle;

    public bool ShouldClose => _glfw.WindowShouldClose(_handle);
    public void SwapBuffers() => _glfw.SwapBuffers(_handle);
    public void PollEvents() => _glfw.PollEvents();

    public int Width => _width;
    public int Height => _height;
    public void SetSize(int w, int h) { _width = w; _height = h; }

    public bool FirstMouse => _firstMouse;
    public void SetFirstMouse(bool v) => _firstMouse = v;
    public double LastMX => _lastMX;
    public double LastMY => _lastMY;
    public void SetLastMouse(double x, double y) { _lastMX = x; _lastMY = y; }

    public bool CursorCaptureEnabled { get; set; } = true;

    public static Window? Instance => s_instance;

    public delegate void MouseMoveDelegate(double dx, double dy);
    public delegate void ScrollDelegate(double yoffset);
    public delegate void ResizeDelegate(int w, int h);
    public delegate void KeyPressDelegate(int key);
    public delegate void MouseButtonDelegate(int button, int action, int mods);

    public event MouseMoveDelegate? OnMouseMove;
    public event ScrollDelegate? OnScroll;
    public event ResizeDelegate? OnResize;
    public event KeyPressDelegate? OnKeyPress;

    private static void OnFramebufferSizeCallback(WindowHandle* w, int width, int height)
    {
        var win = s_instance;
        if (win == null) return;
        win._width = width;
        win._height = height;
        win._gl.Viewport(0, 0, (uint)width, (uint)height);
        win.OnResize?.Invoke(width, height);
    }

    private static void OnKeyCallback(WindowHandle* w, Keys key, int scanCode, InputAction action, KeyModifiers mods)
    {
        var imguiKey = GlfwKeyToImGuiKey(key);
        if (imguiKey != ImGuiNET.ImGuiKey.None)
            ImGuiNET.ImGui.GetIO().AddKeyEvent(imguiKey, action != InputAction.Release);

        if (key == Keys.Escape && action == InputAction.Press)
        {
            var win = s_instance!;
            if (win._glfw.GetInputMode(w, CursorStateAttribute.Cursor) == (int)CursorModeValue.CursorDisabled)
                win._glfw.SetInputMode(w, CursorStateAttribute.Cursor, CursorModeValue.CursorNormal);
            else if (!ImGuiNET.ImGui.GetIO().WantCaptureKeyboard)
                win._glfw.SetInputMode(w, CursorStateAttribute.Cursor, CursorModeValue.CursorDisabled);
            win.SetFirstMouse(true);
        }

        if (action == InputAction.Press && !ImGuiNET.ImGui.GetIO().WantCaptureKeyboard)
            s_instance?.OnKeyPress?.Invoke((int)key);
    }

    private static ImGuiNET.ImGuiKey GlfwKeyToImGuiKey(Keys key)
    {
        int k = (int)key;
        if (k >= Fuse.Input.KeyCodes.A && k <= Fuse.Input.KeyCodes.Z)
            return ImGuiNET.ImGuiKey.A + (k - Fuse.Input.KeyCodes.A);
        if (k >= Fuse.Input.KeyCodes.D0 && k <= Fuse.Input.KeyCodes.D9)
            return ImGuiNET.ImGuiKey._0 + (k - Fuse.Input.KeyCodes.D0);

        return k switch
        {
            Fuse.Input.KeyCodes.Enter => ImGuiNET.ImGuiKey.Enter,
            Fuse.Input.KeyCodes.Escape => ImGuiNET.ImGuiKey.Escape,
            Fuse.Input.KeyCodes.Backspace => ImGuiNET.ImGuiKey.Backspace,
            Fuse.Input.KeyCodes.Tab => ImGuiNET.ImGuiKey.Tab,
            Fuse.Input.KeyCodes.Space => ImGuiNET.ImGuiKey.Space,
            Fuse.Input.KeyCodes.Delete => ImGuiNET.ImGuiKey.Delete,
            Fuse.Input.KeyCodes.Insert => ImGuiNET.ImGuiKey.Insert,
            Fuse.Input.KeyCodes.Up => ImGuiNET.ImGuiKey.UpArrow,
            Fuse.Input.KeyCodes.Down => ImGuiNET.ImGuiKey.DownArrow,
            Fuse.Input.KeyCodes.Left => ImGuiNET.ImGuiKey.LeftArrow,
            Fuse.Input.KeyCodes.Right => ImGuiNET.ImGuiKey.RightArrow,
            Fuse.Input.KeyCodes.Home => ImGuiNET.ImGuiKey.Home,
            Fuse.Input.KeyCodes.End => ImGuiNET.ImGuiKey.End,
            Fuse.Input.KeyCodes.PageUp => ImGuiNET.ImGuiKey.PageUp,
            Fuse.Input.KeyCodes.PageDown => ImGuiNET.ImGuiKey.PageDown,
            Fuse.Input.KeyCodes.LeftShift => ImGuiNET.ImGuiKey.LeftShift,
            Fuse.Input.KeyCodes.LeftControl => ImGuiNET.ImGuiKey.LeftCtrl,
            Fuse.Input.KeyCodes.LeftAlt => ImGuiNET.ImGuiKey.LeftAlt,
            Fuse.Input.KeyCodes.LeftSuper => ImGuiNET.ImGuiKey.LeftSuper,
            Fuse.Input.KeyCodes.RightShift => ImGuiNET.ImGuiKey.RightShift,
            Fuse.Input.KeyCodes.RightControl => ImGuiNET.ImGuiKey.RightCtrl,
            Fuse.Input.KeyCodes.RightAlt => ImGuiNET.ImGuiKey.RightAlt,
            Fuse.Input.KeyCodes.RightSuper => ImGuiNET.ImGuiKey.RightSuper,
            _ => ImGuiNET.ImGuiKey.None
        };
    }

    private static void OnMouseButtonCallback(WindowHandle* w, MouseButton button, InputAction action, KeyModifiers mods)
    {
        if (button == MouseButton.Left && action == InputAction.Press)
        {
            var win = s_instance;
            if (win == null) return;
            bool isDisabled = win._glfw.GetInputMode(w, CursorStateAttribute.Cursor) == (int)CursorModeValue.CursorDisabled;
            if (isDisabled && ImGuiNET.ImGui.GetIO().WantCaptureMouse)
                win._glfw.SetInputMode(w, CursorStateAttribute.Cursor, CursorModeValue.CursorNormal);
            else if (!isDisabled && !ImGuiNET.ImGui.GetIO().WantCaptureMouse && win.CursorCaptureEnabled)
            {
                win.SetFirstMouse(true);
                win._glfw.SetInputMode(w, CursorStateAttribute.Cursor, CursorModeValue.CursorDisabled);
            }
        }
    }

    private static void OnCursorPosCallback(WindowHandle* w, double xpos, double ypos)
    {
        var win = s_instance;
        if (win == null) return;
        if (win._glfw.GetInputMode(w, CursorStateAttribute.Cursor) != (int)CursorModeValue.CursorDisabled)
            return;

        if (win._firstMouse)
        {
            win.SetLastMouse(xpos, ypos);
            win.SetFirstMouse(false);
            return;
        }

        double dx = xpos - win._lastMX;
        double dy = ypos - win._lastMY;
        win.SetLastMouse(xpos, ypos);

        win.OnMouseMove?.Invoke(dx, dy);
    }

    private static void OnScrollCallback(WindowHandle* w, double offsetX, double offsetY)
    {
        s_instance?.OnScroll?.Invoke(offsetY);
    }

    private static void OnCharCallback(WindowHandle* w, uint codepoint)
    {
        ImGuiNET.ImGui.GetIO().AddInputCharacter(codepoint);
    }
}
