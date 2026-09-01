using System;
using System.IO;
using StbImageSharp;
using TinyEXR;

namespace Blowtorch;

/// <summary>
/// CPU representation of a terrain sculpt mask. The samples are normalized to
/// [0, 1], stored row-major from the top of the source image to its bottom,
/// and can be sampled independently of the OpenGL preview texture.
/// </summary>
public sealed class TerrainHeightmapBrush
{
    private TerrainHeightmapBrush(string sourcePath, int width, int height, float[] samples)
    {
        SourcePath = sourcePath;
        Width = width;
        Height = height;
        Samples = samples;
    }

    public string SourcePath { get; }
    public int Width { get; }
    public int Height { get; }
    public float[] Samples { get; }

    public static TerrainHeightmapBrush Load(string path)
    {
        string extension = Path.GetExtension(path);
        if (extension.Equals(".exr", StringComparison.OrdinalIgnoreCase))
            return LoadOpenExr(path);

        byte[] fileData = File.ReadAllBytes(path);
        ImageResult image = ImageResult.FromMemory(fileData, ColorComponents.Grey);
        if (image.Width < 2 || image.Height < 2)
            throw new InvalidDataException("A terrain brush must be at least 2 x 2 pixels.");

        float[] samples = new float[checked(image.Width * image.Height)];
        for (int i = 0; i < samples.Length; i++)
            samples[i] = image.Data[i] / 255.0f;

        return new TerrainHeightmapBrush(path, image.Width, image.Height, samples);
    }

    public float Sample(float u, float v)
    {
        if (Samples.Length == 0)
            return 0.0f;

        u = Math.Clamp(u, 0.0f, 1.0f);
        v = Math.Clamp(v, 0.0f, 1.0f);
        float x = u * (Width - 1);
        float y = v * (Height - 1);
        int x0 = Math.Clamp((int)MathF.Floor(x), 0, Width - 1);
        int y0 = Math.Clamp((int)MathF.Floor(y), 0, Height - 1);
        int x1 = Math.Min(x0 + 1, Width - 1);
        int y1 = Math.Min(y0 + 1, Height - 1);
        float tx = x - x0;
        float ty = y - y0;

        float top = Samples[y0 * Width + x0] +
            (Samples[y0 * Width + x1] - Samples[y0 * Width + x0]) * tx;
        float bottom = Samples[y1 * Width + x0] +
            (Samples[y1 * Width + x1] - Samples[y1 * Width + x0]) * tx;
        return top + (bottom - top) * ty;
    }

    /// <summary>
    /// Creates a white/grayscale mask for ImGui/OpenGL. Black portions become
    /// transparent so the terrain remains visible outside the brush shape.
    /// </summary>
    public byte[] CreatePreviewPixels()
    {
        byte[] pixels = new byte[checked(Width * Height * 4)];
        for (int i = 0; i < Samples.Length; i++)
        {
            byte value = (byte)Math.Clamp(
                (int)MathF.Round(Samples[i] * 255.0f),
                0,
                255);
            int pixel = i * 4;
            pixels[pixel] = value;
            pixels[pixel + 1] = value;
            pixels[pixel + 2] = value;
            pixels[pixel + 3] = value;
        }

        return pixels;
    }

    private static TerrainHeightmapBrush LoadOpenExr(string path)
    {
        ResultCode result = Exr.LoadEXR(
            path,
            out float[] rgba,
            out int width,
            out int height);
        if (result != ResultCode.Success)
            throw new InvalidDataException($"OpenEXR decoder returned {result}.");

        int pixelCount = checked(width * height);
        if (width < 2 || height < 2 || rgba.Length < pixelCount)
            throw new InvalidDataException("The OpenEXR brush has invalid dimensions or pixel data.");

        int channelCount = Math.Max(1, rgba.Length / pixelCount);
        float[] samples = new float[pixelCount];
        for (int i = 0; i < pixelCount; i++)
        {
            float value = rgba[i * channelCount];
            samples[i] = float.IsFinite(value)
                ? Math.Clamp(value, 0.0f, 1.0f)
                : 0.0f;
        }

        return new TerrainHeightmapBrush(path, width, height, samples);
    }
}
