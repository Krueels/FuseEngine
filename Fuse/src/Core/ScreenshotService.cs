using Silk.NET.OpenGL;
using Fuse.Core;

namespace Fuse.Core;

public static class ScreenshotService
{
    public static unsafe void Capture(GL gl, int width, int height)
    {
        var pixels = new byte[width * height * 4];
        fixed (byte* ptr = pixels)
        {
            gl.ReadPixels(0, 0, (uint)width, (uint)height,
                PixelFormat.Bgra, PixelType.UnsignedByte, ptr);
        }

        // Força alpha para 255 (opaco). O canal alpha do default.frag
        // (uIsViewmodel) vaza para a tela quando post-process está desligado,
        // tornando o mundo transparente no PNG.
        for (int i = 3; i < pixels.Length; i += 4)
            pixels[i] = 255;

        // Flip Y: OpenGL y=0 é bottom, PNG y=0 é top
        var flipped = new byte[pixels.Length];
        int stride = width * 4;
        for (int y = 0; y < height; y++)
        {
            System.Buffer.BlockCopy(pixels, y * stride,
                flipped, (height - 1 - y) * stride, stride);
        }

        string filename = $"screenshot_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.png";

        fixed (byte* ptr = flipped)
        {
            using var bmp = new System.Drawing.Bitmap(
                width, height, stride,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb,
                (nint)ptr);
            bmp.Save(filename, System.Drawing.Imaging.ImageFormat.Png);
        }

        Logger.Info($"Screenshot saved: {Path.GetFullPath(filename)}");
    }
}
