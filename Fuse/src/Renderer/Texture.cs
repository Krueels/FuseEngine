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

public unsafe class Texture : IDisposable
{
    private readonly GL _gl;
    private readonly uint _id;
    private readonly int _width;
    private readonly int _height;
    private readonly byte[]? _pixelData;
    private readonly int _channels;
    public TextureColorSpace ColorSpace { get; }

    public Texture(GL gl, string filepath, TextureColorSpace colorSpace = TextureColorSpace.Srgb)
    {
        _gl = gl;
        ColorSpace = colorSpace;

        if (!File.Exists(filepath))
        {
            Logger.Error($"Texture file not found: {filepath}");
            return;
        }

        byte[] fileData = File.ReadAllBytes(filepath);
        ImageResult image;

        try
        {
            image = ImageResult.FromMemory(fileData, ColorComponents.RedGreenBlueAlpha);
        }
        catch (Exception ex)
        {
            Logger.Error($"Failed to load texture: {filepath} ({ex.Message})");
            return;
        }

        _width = image.Width;
        _height = image.Height;

        _channels = 4;
        var format = PixelFormat.Rgba;
        // Color textures are decoded from sRGB to linear by the sampler. Normal,
        // metallic/roughness and AO maps must remain linear data textures.
        var internalFormat = colorSpace == TextureColorSpace.Srgb
            ? InternalFormat.SrgbAlpha
            : InternalFormat.Rgba;

        // Keep original pixels for color analysis (before flip)
        _pixelData = image.Data;

        // Flip vertically (stb_image default is top-left, OpenGL expects bottom-left)
        int rowSize = _width * _channels;
        byte[] flipped = new byte[image.Data.Length];
        for (int y = 0; y < _height; y++)
            System.Buffer.BlockCopy(image.Data, y * rowSize, flipped, (_height - 1 - y) * rowSize, rowSize);
        image.Data = flipped;

        _id = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, _id);

        fixed (byte* dataPtr = image.Data)
        {
            gl.TexImage2D(TextureTarget.Texture2D, 0, (int)internalFormat, (uint)_width, (uint)_height, 0,
                format, PixelType.UnsignedByte, dataPtr);
        }

        gl.GenerateMipmap(TextureTarget.Texture2D);

        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.Repeat);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.Repeat);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.LinearMipmapLinear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);

        Logger.Asset($"Texture loaded: {filepath} ({_width}x{_height})");
    }

    public void Dispose()
    {
        _gl.DeleteTexture(_id);
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

                // Skip dark (max < 0.15) or desaturated (sat < 0.15) pixels
                if (max < 0.15 || sat < 0.15) continue;

                r += rn; g += gn; b += bn;
                count++;
            }
        }

        if (count == 0)
        {
            // Fallback: if nothing saturated, average the whole top 70%
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
