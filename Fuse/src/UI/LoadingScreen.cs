using System.Numerics;
using Silk.NET.OpenGL;
using Fuse.Core;
using Fuse.Renderer;

namespace Fuse.UI;

public class LoadingScreen
{
    public float Progress { get; private set; }
    public string Status { get; private set; } = "";

    public void UpdateProgress(float progress, string status, Window window, GL gl, UIRenderer ui, int width, int height)
    {
        Progress = progress;
        Status = status;

        Render(gl, ui, width, height);
        window.SwapBuffers();
        window.PollEvents();
    }

    public void Render(GL gl, UIRenderer ui, int width, int height)
    {
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        gl.Viewport(0, 0, (uint)width, (uint)height);
        gl.ClearColor(0.0f, 0.0f, 0.0f, 1.0f);
        gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

        gl.Disable(EnableCap.DepthTest);
        gl.Disable(EnableCap.CullFace);
        gl.Enable(EnableCap.Blend);
        gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);

        // Title
        //string title = "LOADING";
        //float titleW = title.Length * 6 * 2.5f;
        //ui.DrawText((width - titleW) / 2, 50, title.AsSpan(), new Vector4(0, 1, 1, 1), 2.5f);

        // Progress bar background
        int barX = 100, barY = height - 150, barW = width - 200, barH = 8;
        ui.DrawRect(barX, barY, barW, barH, new Vector4(0.2f, 0.2f, 0.2f, 1));

        // Progress bar fill
        if (Progress > 0)
            ui.DrawRect(barX, barY, (int)(barW * Progress), barH, new Vector4(0, 0.6f, 0.8f, 1));

        // Progress text
        string pct = $"{Status} ({(int)(Progress * 100)}%)";
        ui.DrawText(barX, barY - 20, pct.AsSpan(), new Vector4(1, 1, 1, 1), 1.0f);

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
            ui.DrawText(barX, logY, text.AsSpan(), color, 0.7f);
            logY -= 14;
        }

        gl.Disable(EnableCap.Blend);
        gl.Enable(EnableCap.CullFace);
        gl.Enable(EnableCap.DepthTest);
    }
}
