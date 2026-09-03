using Silk.NET.OpenGL;
using Fuse.Core;

namespace Fuse.Renderer;

/// <summary>
/// A GPU texture array assembled from a list of ordinary image assets. Texture
/// arrays keep terrain materials from consuming one sampler for every layer and
/// keep all layers on the same filtering/wrapping contract.
///
/// Construction and disposal are render-thread operations, just like Texture.
/// File decoding happens before the GL upload, so this class can later be fed by
/// the AssetManager streaming queue without changing the shader contract.
/// </summary>
public unsafe sealed class TextureArray : IDisposable
{
    private readonly GL _gl;
    private uint _id;
    private bool _disposed;

    public TextureColorSpace ColorSpace { get; }
    public int Width { get; private set; }
    public int Height { get; private set; }
    public int LayerCount { get; private set; }
    public uint ID => _id;

    public TextureArray(GL gl, IReadOnlyList<string> paths, TextureColorSpace colorSpace)
    {
        _gl = gl;
        ColorSpace = colorSpace;
        Upload(paths);
    }

    private void Upload(IReadOnlyList<string> paths)
    {
        var decoded = new List<TextureUploadData?>(paths.Count);
        int width = 0;
        int height = 0;

        foreach (string path in paths)
        {
            TextureUploadData? data = Texture.DecodeFile(path);
            decoded.Add(data);
            if (data != null && width == 0)
            {
                width = data.Width;
                height = data.Height;
            }
        }

        if (width <= 0 || height <= 0)
        {
            width = 1;
            height = 1;
        }

        LayerCount = System.Math.Max(1, decoded.Count);
        Width = width;
        Height = height;

        byte[] fallback = ColorSpace == TextureColorSpace.Data
            ? [128, 128, 255, 255]
            : [128, 128, 128, 255];
        byte[] packed = new byte[checked(width * height * LayerCount * 4)];

        for (int layer = 0; layer < LayerCount; layer++)
        {
            TextureUploadData? data = layer < decoded.Count ? decoded[layer] : null;
            byte[] source = data?.FlippedPixels ?? fallback;
            int sourceWidth = data?.Width ?? 1;
            int sourceHeight = data?.Height ?? 1;
            int destinationOffset = layer * width * height * 4;

            if (sourceWidth == width && sourceHeight == height)
            {
                System.Buffer.BlockCopy(source, 0, packed, destinationOffset, width * height * 4);
                continue;
            }

            // Keep array layers valid even when source artists supplied images
            // with different dimensions. This is nearest-neighbour on purpose:
            // the GPU performs the final filtered lookup and mip generation.
            for (int y = 0; y < height; y++)
            {
                int sourceY = System.Math.Min(sourceHeight - 1, y * sourceHeight / height);
                for (int x = 0; x < width; x++)
                {
                    int sourceX = System.Math.Min(sourceWidth - 1, x * sourceWidth / width);
                    int sourceIndex = (sourceY * sourceWidth + sourceX) * 4;
                    int targetIndex = destinationOffset + (y * width + x) * 4;
                    if (sourceIndex + 3 < source.Length)
                    {
                        packed[targetIndex] = source[sourceIndex];
                        packed[targetIndex + 1] = source[sourceIndex + 1];
                        packed[targetIndex + 2] = source[sourceIndex + 2];
                        packed[targetIndex + 3] = source[sourceIndex + 3];
                    }
                    else
                    {
                        packed[targetIndex] = fallback[0];
                        packed[targetIndex + 1] = fallback[1];
                        packed[targetIndex + 2] = fallback[2];
                        packed[targetIndex + 3] = fallback[3];
                    }
                }
            }
        }

        _id = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2DArray, _id);
        InternalFormat internalFormat = ColorSpace == TextureColorSpace.Srgb
            ? InternalFormat.SrgbAlpha
            : InternalFormat.Rgba;

        fixed (byte* dataPtr = packed)
        {
            _gl.TexImage3D(
                TextureTarget.Texture2DArray,
                0,
                internalFormat,
                (uint)width,
                (uint)height,
                (uint)LayerCount,
                0,
                PixelFormat.Rgba,
                PixelType.UnsignedByte,
                dataPtr);
        }

        _gl.GenerateMipmap(TextureTarget.Texture2DArray);
        _gl.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureWrapS, (int)GLEnum.Repeat);
        _gl.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureWrapT, (int)GLEnum.Repeat);
        _gl.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureWrapR, (int)GLEnum.ClampToEdge);
        _gl.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureMinFilter, (int)GLEnum.LinearMipmapLinear);
        _gl.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);

        Logger.Asset($"Texture array loaded: {LayerCount} layer(s), {width}x{height}");
    }

    public void Bind(uint slot = 0)
    {
        _gl.ActiveTexture(TextureUnit.Texture0 + (int)slot);
        _gl.BindTexture(TextureTarget.Texture2DArray, _id);
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
}
