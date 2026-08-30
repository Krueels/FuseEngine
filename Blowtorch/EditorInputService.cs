using Silk.NET.GLFW;
using ImGuiNET;
using Fuse.Input;

namespace Blowtorch;

public enum EditorInputContext
{
    Map,
    MaterialGraph
}

public unsafe class EditorInputService
{
    public EditorInputContext ActiveContext { get; private set; } = EditorInputContext.Map;

    public bool IsMapContext => ActiveContext == EditorInputContext.Map;
    public bool IsMaterialGraphContext => ActiveContext == EditorInputContext.MaterialGraph;

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
            KeyCodes.F5 => ImGuiKey.F5,
            _ => ImGuiKey.None
        };
    }
}
