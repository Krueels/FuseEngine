using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Silk.NET.GLFW;
using Silk.NET.OpenGL;

namespace Blowtorch;

public unsafe class EditorWindow : IDisposable
{
    private readonly Glfw _glfw;
    private readonly WindowHandle* _handle;
    private readonly GL _gl;
    private readonly ConcurrentQueue<string> _droppedFiles = new();
    private readonly GlfwCallbacks.DropCallback _dropCallback;
    private bool _forceClose;
    private bool _closeRequested;

    public EditorWindow(string title, int width, int height)
    {
        _glfw = Glfw.GetApi();
        if (!_glfw.Init())
            throw new Exception("Failed to init GLFW");

        // Keep the editor context aligned with the runtime: material shaders
        // and the shared skinned pipeline require OpenGL 4.3.
        _glfw.WindowHint(WindowHintInt.ContextVersionMajor, 4);
        _glfw.WindowHint(WindowHintInt.ContextVersionMinor, 3);
        _glfw.WindowHint(WindowHintOpenGlProfile.OpenGlProfile, OpenGlProfile.Core);
        _glfw.WindowHint(WindowHintBool.Maximized, true);

        _handle = _glfw.CreateWindow(width, height, title, null, null);
        if (_handle == null)
        {
            _glfw.Terminate();
            throw new Exception("Failed to create window");
        }

        _dropCallback = HandleFileDrop;
        _glfw.SetDropCallback(_handle, _dropCallback);

        _glfw.MakeContextCurrent(_handle);
        _glfw.SwapInterval(1);

        //var monitor = _glfw.GetPrimaryMonitor();
        //if (monitor != null)
        //{
        //    var mode = _glfw.GetVideoMode(monitor);
        //    _glfw.GetMonitorPos(monitor, out int monitorX, out int monitorY);
        //    int x = monitorX + (mode->Width - width) / 2;
        //    int y = monitorY + (mode->Height - height) / 2;
        //    _glfw.SetWindowPos(_handle, x, y);
        //}

        _gl = GL.GetApi(_glfw.GetProcAddress);
        _gl.Enable(EnableCap.DepthTest);
        _gl.Enable(EnableCap.CullFace);
        _gl.CullFace(GLEnum.Back);

        SetWindowIcon();
    }

    public Glfw Glfw => _glfw;
    public WindowHandle* Handle => _handle;
    public GL GL => _gl;

    public bool ShouldClose
    {
        get
        {
            if (_forceClose)
                return true;
            if (_glfw.WindowShouldClose(_handle))
            {
                _glfw.SetWindowShouldClose(_handle, false);
                _closeRequested = true;
            }
            return false;
        }
    }
    public void SwapBuffers() => _glfw.SwapBuffers(_handle);
    public void PollEvents() => _glfw.PollEvents();

    public IReadOnlyList<string> ConsumeDroppedFiles()
    {
        var files = new List<string>();
        while (_droppedFiles.TryDequeue(out string? path))
            files.Add(path);
        return files;
    }

    public bool ConsumeCloseRequest()
    {
        bool requested = _closeRequested;
        _closeRequested = false;
        return requested;
    }

    public void Close()
    {
        _forceClose = true;
        _glfw.SetWindowShouldClose(_handle, true);
    }

    private void HandleFileDrop(WindowHandle* window, int count, IntPtr paths)
    {
        for (int i = 0; i < count; i++)
        {
            IntPtr pathPointer = Marshal.ReadIntPtr(paths, i * IntPtr.Size);
            string? path = Marshal.PtrToStringUTF8(pathPointer);
            if (!string.IsNullOrWhiteSpace(path))
                _droppedFiles.Enqueue(path);
        }
    }

    private void SetWindowIcon()
    {
        string iconPath = Path.Combine(Fuse.ResPath.Path, "Textures", "Icons", "blowtorch.ico");
        if (!File.Exists(iconPath)) return;

        using var icon = new System.Drawing.Icon(iconPath);
        using var bmp = icon.ToBitmap();
        var rect = new System.Drawing.Rectangle(0, 0, bmp.Width, bmp.Height);
        var data = bmp.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadOnly,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);

        int bytes = bmp.Width * bmp.Height * 4;
        byte[] pixels = new byte[bytes];
        System.Runtime.InteropServices.Marshal.Copy(data.Scan0, pixels, 0, bytes);

        // Swap R and B channels (BGRA → RGBA)
        for (int i = 0; i < bytes; i += 4)
        {
            (pixels[i], pixels[i + 2]) = (pixels[i + 2], pixels[i]);
        }

        fixed (byte* ptr = pixels)
        {
            var image = new Silk.NET.GLFW.Image
            {
                Width = bmp.Width,
                Height = bmp.Height,
                Pixels = ptr
            };
            _glfw.SetWindowIcon(_handle, 1, &image);
        }
        bmp.UnlockBits(data);
    }

    public void Dispose()
    {
        _glfw.SetDropCallback(_handle, null);
        _glfw.DestroyWindow(_handle);
        _glfw.Terminate();
    }
}
