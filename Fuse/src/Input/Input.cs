using System.Numerics;
using Silk.NET.GLFW;

namespace Fuse.Input;

public static unsafe class Input
{
    private static Glfw? s_glfw;
    private static WindowHandle* s_window;
    private static bool s_initialized;

    private static bool[] s_keysDown = new bool[512];
    private static bool[] s_keysPressed = new bool[512];
    private static bool[] s_keysPrev = new bool[512];

    private static bool[] s_mouseDown = new bool[8];
    private static bool[] s_mousePressed = new bool[8];
    private static bool[] s_mousePrev = new bool[8];

    private static double s_mouseX;
    private static double s_mouseY;
    private static double s_mousePrevX;
    private static double s_mousePrevY;

    private static float s_wheelDelta;
    private static int s_wheelValue;

    private static bool s_preventRightHold;
    private static GlfwCallbacks.ScrollCallback? s_prevScrollCallback;

    public static void Init(Glfw glfw, WindowHandle* window)
    {
        s_glfw = glfw;
        s_window = window;
        s_initialized = true;

        s_prevScrollCallback = glfw.SetScrollCallback(s_window, ScrollCallback);

        s_glfw.GetCursorPos(s_window, out s_mouseX, out s_mouseY);
        s_mousePrevX = s_mouseX;
        s_mousePrevY = s_mouseY;
    }

    private static void ScrollCallback(WindowHandle* window, double offsetX, double offsetY)
    {
        s_wheelDelta += (float)offsetY;
        s_wheelValue += (int)offsetY;
        s_prevScrollCallback?.Invoke(window, offsetX, offsetY);
    }

    public static void Update()
    {
        if (!s_initialized || s_window == null) return;

        Array.Copy(s_keysDown, s_keysPrev, 512);
        Array.Copy(s_mouseDown, s_mousePrev, 8);

        for (int i = 0; i < 512; i++)
        {
            s_keysDown[i] = s_glfw!.GetKey(s_window, (Keys)i) == (int)InputAction.Press;
            s_keysPressed[i] = s_keysDown[i] && !s_keysPrev[i];
        }

        for (int i = 0; i < 8; i++)
        {
            s_mouseDown[i] = s_glfw!.GetMouseButton(s_window, i) == (int)InputAction.Press;
            s_mousePressed[i] = s_mouseDown[i] && !s_mousePrev[i];
        }

        s_mousePrevX = s_mouseX;
        s_mousePrevY = s_mouseY;
        s_glfw!.GetCursorPos(s_window, out s_mouseX, out s_mouseY);

        s_wheelDelta = 0.0f;
    }

    public static void ClearKeyStates()
    {
        Array.Clear(s_keysDown);
        Array.Clear(s_keysPressed);
        Array.Clear(s_keysPrev);
    }

    public static bool KeyPressed(int keycode)
    {
        if (keycode >= 512) return false;
        return s_keysPressed[keycode];
    }

    public static bool KeyDown(int keycode)
    {
        if (keycode >= 512) return false;
        return s_keysDown[keycode];
    }

    public static float MouseOffsetX => (float)(s_mouseX - s_mousePrevX);
    public static float MouseOffsetY => (float)(s_mouseY - s_mousePrevY);

    public static bool LeftMouseDown() => s_mouseDown[(int)MouseButton.Left];
    public static bool RightMouseDown() => !s_preventRightHold && s_mouseDown[(int)MouseButton.Right];
    public static bool MiddleMouseDown() => s_mouseDown[(int)MouseButton.Middle];
    public static bool LeftMousePressed() => s_mousePressed[(int)MouseButton.Left];
    public static bool MiddleMousePressed() => s_mousePressed[(int)MouseButton.Middle];
    public static bool RightMousePressed() => s_mousePressed[(int)MouseButton.Right];

    public static bool MouseWheelUp => s_wheelDelta > 0.0f;
    public static bool MouseWheelDown => s_wheelDelta < 0.0f;
    public static int MouseWheelValue => s_wheelValue;

    public static int MouseX => (int)s_mouseX;
    public static int MouseY => (int)s_mouseY;

    public static void PreventRightMouseHold() => s_preventRightHold = true;

    public static void DisableCursor()
    {
        if (s_window != null)
            s_glfw!.SetInputMode(s_window, CursorStateAttribute.Cursor, CursorModeValue.CursorDisabled);
    }

    public static bool IsCursorDisabled()
    {
        if (s_window == null) return false;
        return s_glfw!.GetInputMode(s_window, CursorStateAttribute.Cursor) == (int)CursorModeValue.CursorDisabled;
    }

    public static void HideCursor()
    {
        if (s_window != null)
            s_glfw!.SetInputMode(s_window, CursorStateAttribute.Cursor, CursorModeValue.CursorHidden);
    }

    public static void ShowCursor()
    {
        if (s_window != null)
            s_glfw!.SetInputMode(s_window, CursorStateAttribute.Cursor, CursorModeValue.CursorNormal);
    }

    public static void CenterMouseCursor()
    {
        if (s_window == null) return;
        s_glfw!.GetWindowSize(s_window, out int w, out int h);
        s_glfw.SetCursorPos(s_window, w / 2.0, h / 2.0);
    }

    public static int CursorScreenX => MouseX;
    public static int CursorScreenY => MouseY;

    public static Vector2 MousePosition => new Vector2(MouseX, MouseY);

    public static void SetCursorPosition(int x, int y)
    {
        if (s_window != null)
            s_glfw!.SetCursorPos(s_window, x, y);
    }

    public static int MouseXPreviousFrame => (int)s_mousePrevX;
    public static int MouseYPreviousFrame => (int)s_mousePrevY;
}
