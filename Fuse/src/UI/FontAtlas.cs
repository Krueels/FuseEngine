using Silk.NET.OpenGL;
using StbTrueTypeSharp;
using Fuse.Core;

namespace Fuse.Renderer;

public unsafe class FontAtlas : IDisposable
{
    private readonly GL _gl;
    public uint TextureId { get; private set; }
    public int AtlasWidth { get; private set; }
    public int AtlasHeight { get; private set; }
    public float FontSize { get; private set; }
    public float LineHeight { get; private set; }
    private readonly Dictionary<char, GlyphInfo> _glyphs = new();

    public struct GlyphInfo
    {
        public float X0, Y0, X1, Y1;
        public float XOff, YOff;
        public float Advance;
        public float Width;
        public float Height;
    }

    public FontAtlas(GL gl, string ttfPath, int fontSize = 24)
    {
        _gl = gl;
        FontSize = fontSize;

        byte[] ttfData = File.ReadAllBytes(ttfPath);
        int atlasW = 2048;
        int atlasH = 2048;
        byte[] bitmap = new byte[atlasW * atlasH];
        var packedChars = new StbTrueType.stbtt_packedchar[96];

        fixed (byte* bmpPtr = bitmap)
        fixed (byte* ttfPtr = ttfData)
        fixed (StbTrueType.stbtt_packedchar* pcPtr = packedChars)
        {
            var spc = new StbTrueType.stbtt_pack_context();
            StbTrueType.stbtt_PackBegin(spc, bmpPtr, atlasW, atlasH, 0, 1, null);
            StbTrueType.stbtt_PackSetOversampling(spc, 3, 3);
            StbTrueType.stbtt_PackFontRange(spc, ttfPtr, 0, fontSize, 32, 96, pcPtr);
            StbTrueType.stbtt_PackEnd(spc);
        }

        for (int i = 0; i < 96; i++)
        {
            var pc = packedChars[i];
            char c = (char)(32 + i);
            _glyphs[c] = new GlyphInfo
            {
                X0 = pc.x0 / (float)atlasW,
                Y0 = pc.y0 / (float)atlasH,
                X1 = pc.x1 / (float)atlasW,
                Y1 = pc.y1 / (float)atlasH,
                XOff = pc.xoff,
                YOff = pc.yoff,
                Advance = pc.xadvance,
                Width = pc.xoff2 - pc.xoff,
                Height = pc.yoff2 - pc.yoff
            };
        }

        AtlasWidth = atlasW;
        AtlasHeight = atlasH;
        LineHeight = fontSize;
        Logger.Info($"FontAtlas: packed '{ttfPath}' size={fontSize} 3x oversample glyphs={_glyphs.Count}");

        TextureId = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, TextureId);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
        _gl.TexImage2D(TextureTarget.Texture2D, 0, (int)InternalFormat.R8, (uint)atlasW, (uint)atlasH, 0,
            PixelFormat.Red, PixelType.UnsignedByte, bitmap);
    }

    public bool TryGetGlyph(char c, out GlyphInfo glyph) => _glyphs.TryGetValue(c, out glyph);

    public void Dispose() => _gl.DeleteTexture(TextureId);
}
