using Silk.NET.GLFW;
using ImGuiNET;
using Fuse.Input;

namespace Blowtorch;

public enum EditorInputContext
{
    Map,
    MaterialGraph,
    GeometryGraph
}

public unsafe class EditorInputService
{
    public EditorInputContext ActiveContext { get; private set; } = EditorInputContext.Map;

    public bool IsMapContext => ActiveContext == EditorInputContext.Map;
    public bool IsMaterialGraphContext => ActiveContext == EditorInputContext.MaterialGraph;
    public bool IsGeometryGraphContext => ActiveContext == EditorInputContext.GeometryGraph;

    public void Initialize(Glfw glfw, WindowHandle* windowHandle)
    {
        Input.Init(glfw, windowHandle);

        glfw.SetKeyCallback(windowHandle, OnKeyCallback);
        glfw.SetCharCallback(windowHandle, OnCharCallback);
        glfw.SetScrollCallback(windowHandle, OnScrollCallback);
    }

    public void Update()
    {
        Input.Update();
    }

    public void BeginFrame()
    {
        // The map is the default context. A focused tool window can claim the
        // frame later while its ImGui window is being drawn.
        ActiveContext = EditorInputContext.Map;
    }

    public void SetContext(EditorInputContext context)
    {
        ActiveContext = context;
    }

    private void OnKeyCallback(WindowHandle* w, Keys key, int scanCode, InputAction action, KeyModifiers mods)
    {
        var io = ImGui.GetIO();
        io.AddKeyEvent(ImGuiKey.ModCtrl, (mods & KeyModifiers.Control) != 0);
        io.AddKeyEvent(ImGuiKey.ModShift, (mods & KeyModifiers.Shift) != 0);
        io.AddKeyEvent(ImGuiKey.ModAlt, (mods & KeyModifiers.Alt) != 0);
        io.AddKeyEvent(ImGuiKey.ModSuper, (mods & KeyModifiers.Super) != 0);

        var imguiKey = GlfwKeyToImGuiKey(key);
        if (imguiKey != ImGuiKey.None)
            io.AddKeyEvent(imguiKey, action != InputAction.Release);
    }

    private void OnCharCallback(WindowHandle* w, uint codepoint)
    {
        ImGui.GetIO().AddInputCharacter(codepoint);
    }

    private void OnScrollCallback(WindowHandle* w, double offsetX, double offsetY)
    {
        ImGui.GetIO().AddMouseWheelEvent(0, (float)offsetY);
    }

    private static ImGuiKey GlfwKeyToImGuiKey(Keys key)
    {
        int k = (int)key;
        if (k >= KeyCodes.A && k <= KeyCodes.Z)
            return ImGuiKey.A + (k - KeyCodes.A);
        if (k >= KeyCodes.D0 && k <= KeyCodes.D9)
            return ImGuiKey._0 + (k - KeyCodes.D0);

        return k switch
        {
            KeyCodes.Enter => ImGuiKey.Enter,
            KeyCodes.Escape => ImGuiKey.Escape,
            KeyCodes.Backspace => ImGuiKey.Backspace,
            KeyCodes.Tab => ImGuiKey.Tab,
            KeyCodes.Space => ImGuiKey.Space,
            KeyCodes.Apostrophe => ImGuiKey.Apostrophe,
            KeyCodes.Comma => ImGuiKey.Comma,
            KeyCodes.Minus => ImGuiKey.Minus,
            KeyCodes.Period => ImGuiKey.Period,
            KeyCodes.Slash => ImGuiKey.Slash,
            KeyCodes.Semicolon => ImGuiKey.Semicolon,
            KeyCodes.Equal => ImGuiKey.Equal,
            KeyCodes.LeftBracket => ImGuiKey.LeftBracket,
            KeyCodes.Backslash => ImGuiKey.Backslash,
            KeyCodes.RightBracket => ImGuiKey.RightBracket,
            KeyCodes.GraveAccent => ImGuiKey.GraveAccent,
            KeyCodes.Delete => ImGuiKey.Delete,
            KeyCodes.Insert => ImGuiKey.Insert,
            KeyCodes.Up => ImGuiKey.UpArrow,
            KeyCodes.Down => ImGuiKey.DownArrow,
            KeyCodes.Left => ImGuiKey.LeftArrow,
            KeyCodes.Right => ImGuiKey.RightArrow,
            KeyCodes.Home => ImGuiKey.Home,
            KeyCodes.End => ImGuiKey.End,
            KeyCodes.PageUp => ImGuiKey.PageUp,
            KeyCodes.PageDown => ImGuiKey.PageDown,
            KeyCodes.LeftShift => ImGuiKey.LeftShift,
            KeyCodes.LeftControl => ImGuiKey.LeftCtrl,
            KeyCodes.LeftAlt => ImGuiKey.LeftAlt,
            KeyCodes.LeftSuper => ImGuiKey.LeftSuper,
            KeyCodes.RightShift => ImGuiKey.RightShift,
            KeyCodes.RightControl => ImGuiKey.RightCtrl,
            KeyCodes.RightAlt => ImGuiKey.RightAlt,
            KeyCodes.RightSuper => ImGuiKey.RightSuper,
            KeyCodes.F1 => ImGuiKey.F1,
            KeyCodes.F2 => ImGuiKey.F2,
            KeyCodes.F3 => ImGuiKey.F3,
            KeyCodes.F4 => ImGuiKey.F4,
            KeyCodes.F5 => ImGuiKey.F5,
            KeyCodes.F6 => ImGuiKey.F6,
            KeyCodes.F7 => ImGuiKey.F7,
            KeyCodes.F8 => ImGuiKey.F8,
            KeyCodes.F9 => ImGuiKey.F9,
            KeyCodes.F10 => ImGuiKey.F10,
            KeyCodes.F11 => ImGuiKey.F11,
            KeyCodes.F12 => ImGuiKey.F12,
            _ => ImGuiKey.None
        };
    }
}
