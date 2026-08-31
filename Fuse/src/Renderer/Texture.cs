using System.Numerics;
using Silk.NET.OpenGL;
using StbImageSharp;
using Fuse.Core;

namespace Fuse.Renderer;

public enum TextureColorSpace
{
    Linear,
    Srgb,
    Data
}

/// <summary>
/// CPU-side image data. It is independent of OpenGL so it can be produced by
/// the asset worker thread and uploaded later on the render thread.
/// </summary>
public sealed class TextureUploadData
{
    internal TextureUploadData(int width, int height, byte[] originalPixels, byte[] flippedPixels)
    {
        Width = width;
        Height = height;
        OriginalPixels = originalPixels;
        FlippedPixels = flippedPixels;
    }

    public int Width { get; }
    public int Height { get; }
    internal byte[] OriginalPixels { get; }
    internal byte[] FlippedPixels { get; }
}

public unsafe class Texture : IDisposable
{
    private readonly GL _gl;
    private uint _id;
    private int _width;
    private int _height;
    private byte[]? _pixelData;
    private readonly int _channels;
    private bool _disposed;
    private bool _isReady;
    private bool _isPlaceholder;
    private bool _isFailed;

    public TextureColorSpace ColorSpace { get; }
    public bool IsReady => _isReady;
    public bool IsPlaceholder => _isPlaceholder;
    public bool IsFailed => _isFailed;

    public Texture(GL gl, string filepath, TextureColorSpace colorSpace = TextureColorSpace.Srgb)
    {
        _gl = gl;
        ColorSpace = colorSpace;
        _channels = 4;

        TextureUploadData? data = DecodeFile(filepath);
        if (data == null)
        {
            _isFailed = true;
            return;
        }

        ApplyUpload(data, filepath);
    }

    private Texture(GL gl, TextureColorSpace colorSpace, bool placeholder)
    {
        _gl = gl;
        ColorSpace = colorSpace;
        _channels = 4;
        _isPlaceholder = placeholder;
    }

    /// <summary>
    /// Creates a valid 1x1 texture while the real image is being decoded. The
    /// same Texture object is updated in place when the upload finishes.
    /// </summary>
    public static Texture CreatePlaceholder(GL gl, TextureColorSpace colorSpace)
    {
        var texture = new Texture(gl, colorSpace, placeholder: true)
        {
            _width = 1,
            _height = 1,
            _pixelData = colorSpace == TextureColorSpace.Data
                ? new byte[] { 128, 128, 255, 255 }
                : new byte[] { 128, 128, 128, 255 }
        };

        texture.UploadPixels(texture._pixelData, 1, 1, logPath: null);
        texture._isReady = false;
        return texture;
    }

    /// <summary>
    /// Decodes an image without touching OpenGL. This is used by the AssetManager
    /// worker and is safe to call away from the render thread.
    /// </summary>
    public static TextureUploadData? DecodeFile(string filepath, int maxDimension = 0)
    {
        if (!File.Exists(filepath))
        {
            Logger.Error($"Texture file not found: {filepath}");
            return null;
        }

        try
        {
            byte[] fileData = File.ReadAllBytes(filepath);
            ImageResult image = ImageResult.FromMemory(fileData, ColorComponents.RedGreenBlueAlpha);
            int width = image.Width;
            int height = image.Height;
            byte[] original = image.Data;

            // Asset Browser thumbnails must never retain or upload the full
            // source image. A single 8K RGBA image is already 256 MiB.
            if (maxDimension > 0 && System.Math.Max(width, height) > maxDimension)
            {
                float scale = maxDimension / (float)System.Math.Max(width, height);
                int resizedWidth = System.Math.Max(1, (int)MathF.Round(width * scale));
                int resizedHeight = System.Math.Max(1, (int)MathF.Round(height * scale));
                byte[] resized = new byte[resizedWidth * resizedHeight * 4];
                for (int y = 0; y < resizedHeight; y++)
                {
                    int sourceY = System.Math.Min(height - 1, (int)(y / scale));
                    for (int x = 0; x < resizedWidth; x++)
                    {
                        int sourceX = System.Math.Min(width - 1, (int)(x / scale));
                        int sourceIndex = (sourceY * width + sourceX) * 4;
                        int targetIndex = (y * resizedWidth + x) * 4;
                        resized[targetIndex] = original[sourceIndex];
                        resized[targetIndex + 1] = original[sourceIndex + 1];
                        resized[targetIndex + 2] = original[sourceIndex + 2];
                        resized[targetIndex + 3] = original[sourceIndex + 3];
                    }
                }
                original = resized;
                width = resizedWidth;
                height = resizedHeight;
            }

            byte[] flipped = new byte[original.Length];
            int rowSize = width * 4;

            // stb_image is top-left based; OpenGL texture coordinates are bottom-left.
            for (int y = 0; y < height; y++)
                System.Buffer.BlockCopy(original, y * rowSize, flipped, (height - 1 - y) * rowSize, rowSize);

            return new TextureUploadData(width, height, original, flipped);
        }
        catch (Exception ex)
        {
            Logger.Error($"Failed to decode texture: {filepath} ({ex.Message})");
            return null;
        }
    }

    /// <summary>Must only be called on the thread that owns the OpenGL context.</summary>
    internal void ApplyUpload(TextureUploadData data, string? logPath, bool keepCpuPixels = true)
    {
        if (_disposed)
            return;

        _width = data.Width;
        _height = data.Height;
        _pixelData = keepCpuPixels ? data.OriginalPixels : null;
        _isFailed = false;
        _isPlaceholder = false;
        UploadPixels(data.FlippedPixels, data.Width, data.Height, logPath);
    }

    internal void MarkFailed()
    {
        if (!_disposed)
            _isFailed = true;
    }

    private void UploadPixels(byte[] pixels, int width, int height, string? logPath)
    {
        if (_id != 0)
            _gl.DeleteTexture(_id);

        var format = PixelFormat.Rgba;
        var internalFormat = ColorSpace == TextureColorSpace.Srgb
            ? InternalFormat.SrgbAlpha
            : InternalFormat.Rgba;

        _id = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, _id);

        fixed (byte* dataPtr = pixels)
        {
            _gl.TexImage2D(TextureTarget.Texture2D, 0, (int)internalFormat,
                (uint)width, (uint)height, 0, format, PixelType.UnsignedByte, dataPtr);
        }

        _gl.GenerateMipmap(TextureTarget.Texture2D);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.Repeat);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.Repeat);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.LinearMipmapLinear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);

        _isReady = true;
        if (!string.IsNullOrWhiteSpace(logPath))
            Logger.Asset($"Texture loaded: {logPath} ({width}x{height})");
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_id != 0)
            _gl.DeleteTexture(_id);
        _id = 0;
    }

    public uint ID => _id;
    public int Width => _width;
    public int Height => _height;

    public void Bind(uint slot = 0)
    {
        _gl.ActiveTexture(TextureUnit.Texture0 + (int)slot);
        _gl.BindTexture(TextureTarget.Texture2D, _id);
    }

    public Vector3 GetDominantColor()
    {
        if (_pixelData == null || _pixelData.Length == 0)
            return new Vector3(1, 1, 1);

        int stepX = System.Math.Max(1, _width / 64);
        int stepY = System.Math.Max(1, _height / 64);

        double r = 0, g = 0, b = 0;
        int count = 0;
        int skyLimit = (int)(_height * 0.7);

        for (int y = 0; y < skyLimit; y += stepY)
        {
            for (int x = 0; x < _width; x += stepX)
            {
                int idx = (y * _width + x) * _channels;
                if (idx + 2 >= _pixelData.Length) continue;

                double rn = _pixelData[idx] / 255.0;
                double gn = _pixelData[idx + 1] / 255.0;
                double bn = _pixelData[idx + 2] / 255.0;

                double max = rn > gn ? (rn > bn ? rn : bn) : (gn > bn ? gn : bn);
                double min = rn < gn ? (rn < bn ? rn : bn) : (gn < bn ? gn : bn);
                double sat = max < 0.01 ? 0.0 : (max - min) / max;

                if (max < 0.15 || sat < 0.15) continue;

                r += rn; g += gn; b += bn;
                count++;
            }
        }

        if (count == 0)
        {
            for (int y = 0; y < skyLimit; y += stepY)
            {
                for (int x = 0; x < _width; x += stepX)
                {
                    int idx = (y * _width + x) * _channels;
                    if (idx + 2 >= _pixelData.Length) continue;
                    r += _pixelData[idx] / 255.0;
                    g += _pixelData[idx + 1] / 255.0;
                    b += _pixelData[idx + 2] / 255.0;
                    count++;
                }
            }
            if (count == 0) return new Vector3(1, 1, 1);
        }

        return new Vector3((float)(r / count), (float)(g / count), (float)(b / count));
    }
}
