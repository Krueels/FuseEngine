using System.Numerics;
using Silk.NET.OpenGL;
using Fuse.Core;

namespace Fuse.Renderer;

public unsafe class UIRenderer : IDisposable
{
    private readonly GL _gl;
    private int _screenW, _screenH;
    private Matrix4x4 _proj;

    private readonly uint _textShader;
    private readonly int _textUProj, _textUTex;
    private readonly uint _textVAO, _textVBO, _textIBO;
    private const int KMaxQuads = 2048;

    private readonly uint _imgShader;
    private readonly int _imgUProj, _imgUTex;
    private readonly uint _imgVAO, _imgVBO, _imgEBO;

    private static readonly byte[] s_font = GenerateFontBitmap();
    private FontAtlas? _fontAtlas;
    private readonly uint _whiteTexture;

    public void SetFontAtlas(FontAtlas atlas) => _fontAtlas = atlas;

    public UIRenderer(GL gl, int screenW, int screenH)
    {
        _gl = gl;
        _screenW = screenW;
        _screenH = screenH;
        UpdateProjection();

        string textVS = """
            #version 330 core
            layout(location = 0) in vec3 aPos;
            layout(location = 1) in vec4 aColor;
            layout(location = 2) in vec2 aTexCoord;
            out vec4 vColor;
            out vec2 vTexCoord;
            uniform mat4 uProj;
            void main() {
                vColor = aColor;
                vTexCoord = aTexCoord;
                gl_Position = uProj * vec4(aPos, 1.0);
            }
            """;
        string textFS = """
            #version 330 core
            in vec4 vColor;
            in vec2 vTexCoord;
            out vec4 fragColor;
            uniform sampler2D uTexture;
            void main() { float a = texture(uTexture, vTexCoord).r; fragColor = vec4(vColor.rgb, vColor.a * a); }
            """;
        _textShader = LinkShader(gl, CompileShader(gl, ShaderType.VertexShader, textVS), CompileShader(gl, ShaderType.FragmentShader, textFS));
        _textUProj = gl.GetUniformLocation(_textShader, "uProj");
        _textUTex = gl.GetUniformLocation(_textShader, "uTexture");

        // 1x1 white texture for fallback (bitmap font / rects)
        _whiteTexture = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, _whiteTexture);
        byte[] white = [255];
        fixed (byte* p = white)
            gl.TexImage2D(TextureTarget.Texture2D, 0, (int)InternalFormat.R8, 1, 1, 0, PixelFormat.Red, PixelType.UnsignedByte, p);

        _textVAO = gl.GenVertexArray();
        _textVBO = gl.GenBuffer();
        _textIBO = gl.GenBuffer();

        uint[] idx = new uint[KMaxQuads * 6];
        for (int i = 0; i < KMaxQuads; i++)
        {
            uint base_ = (uint)i * 4;
            idx[i * 6 + 0] = base_ + 0;
            idx[i * 6 + 1] = base_ + 1;
            idx[i * 6 + 2] = base_ + 2;
            idx[i * 6 + 3] = base_ + 0;
            idx[i * 6 + 4] = base_ + 2;
            idx[i * 6 + 5] = base_ + 3;
        }
        gl.BindVertexArray(_textVAO);
        gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, _textIBO);
        fixed (uint* p = idx)
            gl.BufferData(BufferTargetARB.ElementArrayBuffer, (uint)(idx.Length * sizeof(uint)), p, BufferUsageARB.StaticDraw);
        gl.BindVertexArray(0);

        string imgVS = """
            #version 330 core
            layout(location = 0) in vec3 aPos;
            layout(location = 1) in vec2 aTexCoord;
            out vec2 vTexCoord;
            uniform mat4 uProj;
            void main() {
                vTexCoord = aTexCoord;
                gl_Position = uProj * vec4(aPos, 1.0);
            }
            """;
        string imgFS = """
            #version 330 core
            in vec2 vTexCoord;
            out vec4 fragColor;
            uniform sampler2D uTexture;
            void main() { fragColor = texture(uTexture, vTexCoord); }
            """;
        _imgShader = LinkShader(gl, CompileShader(gl, ShaderType.VertexShader, imgVS), CompileShader(gl, ShaderType.FragmentShader, imgFS));
        _imgUProj = gl.GetUniformLocation(_imgShader, "uProj");
        _imgUTex = gl.GetUniformLocation(_imgShader, "uTexture");

        _imgVAO = gl.GenVertexArray();
        _imgVBO = gl.GenBuffer();
        _imgEBO = gl.GenBuffer();

        uint[] imgIdx = [0, 1, 2, 2, 3, 0];
        gl.BindVertexArray(_imgVAO);
        gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, _imgEBO);
        fixed (uint* p = imgIdx)
            gl.BufferData(BufferTargetARB.ElementArrayBuffer, (uint)(imgIdx.Length * sizeof(uint)), p, BufferUsageARB.StaticDraw);
        gl.BindVertexArray(0);
    }

    public void Dispose()
    {
        _gl.DeleteProgram(_textShader);
        uint vao = _textVAO; _gl.DeleteVertexArrays(1, ref vao);
        uint vbo = _textVBO; _gl.DeleteBuffers(1, ref vbo);
        uint ibo = _textIBO; _gl.DeleteBuffers(1, ref ibo);
        _gl.DeleteProgram(_imgShader);
        vao = _imgVAO; _gl.DeleteVertexArrays(1, ref vao);
        vbo = _imgVBO; _gl.DeleteBuffers(1, ref vbo);
        ibo = _imgEBO; _gl.DeleteBuffers(1, ref ibo);
        _gl.DeleteTexture(_whiteTexture);
    }

    public void SetScreenSize(int w, int h)
    {
        _screenW = w;
        _screenH = h;
        UpdateProjection();
    }

    public Vector2 Center => new(_screenW * 0.5f, _screenH * 0.5f);
    public Vector2 Size => new(_screenW, _screenH);
    public int Width => _screenW;
    public int Height => _screenH;

    private void UpdateProjection()
    {
        _proj = Matrix4x4.CreateOrthographicOffCenter(0, _screenW, _screenH, 0, -1, 1);
    }

    public void DrawText(float x, float y, ReadOnlySpan<char> text, Vector4 color, float scale = 1.0f)
    {
        if (text.Length == 0) return;

        int quadCount = 0;
        int maxQuads = KMaxQuads;
        int stride = _fontAtlas != null ? 24 : 16;
        byte[] vertBuf = new byte[maxQuads * 4 * stride];

        fixed (byte* basePtr = vertBuf)
        {
            if (_fontAtlas != null)
            {
                for (int ci = 0; ci < text.Length && quadCount < maxQuads; ci++)
                {
                    char c = text[ci];
                    if (!_fontAtlas.TryGetGlyph(c, out var glyph))
                    {
                        x += _fontAtlas.FontSize * scale * 0.4f;
                        continue;
                    }

                    float px = x + glyph.XOff * scale;
                    float py = y + (glyph.YOff + _fontAtlas.FontSize) * scale;
                    float w = glyph.Width * scale;
                    float h = glyph.Height * scale;
                    float u0 = glyph.X0, v0 = glyph.Y0, u1 = glyph.X1, v1 = glyph.Y1;

                    byte* v = basePtr + quadCount * 4 * 24;
                    PutVert(v, px, py + h, color, u0, v1);
                    PutVert(v + 24, px + w, py + h, color, u1, v1);
                    PutVert(v + 48, px + w, py, color, u1, v0);
                    PutVert(v + 72, px, py, color, u0, v0);
                    quadCount++;

                    x += glyph.Advance * scale;
                }
            }
            else
            {
                float charW = 6 * scale;
                for (int ci = 0; ci < text.Length && quadCount < maxQuads; ci++)
                {
                    int ch = text[ci] - 32;
                    if (ch < 0 || ch >= 96) { x += charW; continue; }

                    for (int row = 0; row < 7 && quadCount < maxQuads; row++)
                    {
                        byte rowBits = s_font[ch * 7 + row];
                        for (int col = 0; col < 5 && quadCount < maxQuads; col++)
                        {
                            if ((rowBits & (1 << (4 - col))) == 0) continue;

                            float px = x + col * scale;
                            float py = y + row * scale;

                            byte* v = basePtr + quadCount * 4 * 16;
                            PutVert(v, px, py, color);
                            PutVert(v + 16, px + scale, py, color);
                            PutVert(v + 32, px + scale, py + scale, color);
                            PutVert(v + 48, px, py + scale, color);
                            quadCount++;
                        }
                    }
                    x += charW;
                }
            }
        }

        if (quadCount == 0) return;

        _gl.BindVertexArray(_textVAO);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _textVBO);
        fixed (byte* p = vertBuf)
            _gl.BufferData(BufferTargetARB.ArrayBuffer, (uint)(quadCount * 4 * stride), p, BufferUsageARB.DynamicDraw);

        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, (uint)stride, (void*)0);
        _gl.EnableVertexAttribArray(1);
        _gl.VertexAttribPointer(1, 4, VertexAttribPointerType.UnsignedByte, true, (uint)stride, (void*)12);

        if (_fontAtlas != null)
        {
            _gl.EnableVertexAttribArray(2);
            _gl.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, (uint)stride, (void*)16);
        }

        _gl.UseProgram(_textShader);
        _gl.UniformMatrix4(_textUProj, 1, false, GetMatrixValues(_proj));
        _gl.Uniform1(_textUTex, 0);

        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, _fontAtlas != null ? _fontAtlas.TextureId : _whiteTexture);

        _gl.DrawElements(PrimitiveType.Triangles, (uint)(quadCount * 6), DrawElementsType.UnsignedInt, null);
        _gl.BindVertexArray(0);

        if (_fontAtlas != null)
            _gl.DisableVertexAttribArray(2);
    }

    public void DrawImage(Texture tex, float x, float y, float w, float h)
    {
        if (tex == null || tex.ID == 0) return;

        float[] verts = [
            x,     y,     0.0f, 0.0f, 1.0f,
            x + w, y,     0.0f, 1.0f, 1.0f,
            x + w, y + h, 0.0f, 1.0f, 0.0f,
            x,     y + h, 0.0f, 0.0f, 0.0f,
        ];

        _gl.BindVertexArray(_imgVAO);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _imgVBO);
        fixed (float* p = verts)
            _gl.BufferData(BufferTargetARB.ArrayBuffer, (uint)(verts.Length * sizeof(float)), p, BufferUsageARB.DynamicDraw);

        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 5 * sizeof(float), (void*)0);
        _gl.EnableVertexAttribArray(1);
        _gl.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, 5 * sizeof(float), (void*)(3 * sizeof(float)));

        _gl.UseProgram(_imgShader);
        _gl.UniformMatrix4(_imgUProj, 1, false, GetMatrixValues(_proj));
        _gl.Uniform1(_imgUTex, 0);

        tex.Bind(0);
        _gl.DrawElements(PrimitiveType.Triangles, 6, DrawElementsType.UnsignedInt, null);
        _gl.BindVertexArray(0);
    }

    public void DrawRect(float x, float y, float w, float h, Vector4 color)
    {
        byte[] verts = new byte[4 * 16];
        fixed (byte* p = verts)
        {
            PutVert(p, x, y, color);
            PutVert(p + 16, x + w, y, color);
            PutVert(p + 32, x + w, y + h, color);
            PutVert(p + 48, x, y + h, color);
        }

        _gl.BindVertexArray(_textVAO);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _textVBO);
        fixed (byte* p = verts)
            _gl.BufferData(BufferTargetARB.ArrayBuffer, (uint)verts.Length, p, BufferUsageARB.DynamicDraw);

        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 16, (void*)0);
        _gl.EnableVertexAttribArray(1);
        _gl.VertexAttribPointer(1, 4, VertexAttribPointerType.UnsignedByte, true, 16, (void*)12);

        _gl.UseProgram(_textShader);
        _gl.UniformMatrix4(_textUProj, 1, false, GetMatrixValues(_proj));
        _gl.Uniform1(_textUTex, 0);

        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, _whiteTexture);

        _gl.DrawElements(PrimitiveType.Triangles, 6, DrawElementsType.UnsignedInt, null);
        _gl.BindVertexArray(0);
    }

    private static void PutVert(byte* dest, float x, float y, Vector4 color)
    {
        *(float*)dest = x;
        *(float*)(dest + 4) = y;
        *(float*)(dest + 8) = 0.0f;
        dest[12] = (byte)(color.X * 255);
        dest[13] = (byte)(color.Y * 255);
        dest[14] = (byte)(color.Z * 255);
        dest[15] = (byte)(color.W * 255);
    }

    private static void PutVert(byte* dest, float x, float y, Vector4 color, float u, float v)
    {
        *(float*)dest = x;
        *(float*)(dest + 4) = y;
        *(float*)(dest + 8) = 0.0f;
        dest[12] = (byte)(color.X * 255);
        dest[13] = (byte)(color.Y * 255);
        dest[14] = (byte)(color.Z * 255);
        dest[15] = (byte)(color.W * 255);
        *(float*)(dest + 16) = u;
        *(float*)(dest + 20) = v;
    }

    private static uint CompileShader(GL gl, ShaderType type, string source)
    {
        uint shader = gl.CreateShader(type);
        byte[] src = System.Text.Encoding.UTF8.GetBytes(source);
        fixed (byte* ptr = src)
        {
            int len = src.Length;
            gl.ShaderSource(shader, 1, (byte**)&ptr, &len);
        }
        gl.CompileShader(shader);
        gl.GetShader(shader, GLEnum.CompileStatus, out int ok);
        if (ok == 0)
            Logger.Error($"UI shader compile: {gl.GetShaderInfoLog(shader)}");
        return shader;
    }

    private static uint LinkShader(GL gl, uint vs, uint fs)
    {
        uint prog = gl.CreateProgram();
        gl.AttachShader(prog, vs);
        gl.AttachShader(prog, fs);
        gl.LinkProgram(prog);
        gl.GetProgram(prog, GLEnum.LinkStatus, out int ok);
        if (ok == 0)
            Logger.Error($"UI shader link: {gl.GetProgramInfoLog(prog)}");
        gl.DeleteShader(vs);
        gl.DeleteShader(fs);
        return prog;
    }

    private static float[] GetMatrixValues(Matrix4x4 mat) =>
        [mat.M11,mat.M12,mat.M13,mat.M14,
         mat.M21,mat.M22,mat.M23,mat.M24,
         mat.M31,mat.M32,mat.M33,mat.M34,
         mat.M41,mat.M42,mat.M43,mat.M44];

    private static byte[] GenerateFontBitmap()
    {
        return
        [
            0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x04,0x04,0x04,0x04,0x04,0x00,0x04,
            0x0A,0x0A,0x0A,0x00,0x00,0x00,0x00,0x0A,0x0A,0x1F,0x0A,0x1F,0x0A,0x0A,
            0x04,0x0F,0x14,0x0E,0x05,0x1E,0x04,0x18,0x19,0x02,0x04,0x08,0x13,0x03,
            0x0C,0x12,0x14,0x08,0x15,0x12,0x0D,0x04,0x04,0x04,0x00,0x00,0x00,0x00,
            0x02,0x04,0x08,0x08,0x08,0x04,0x02,0x08,0x04,0x02,0x02,0x02,0x04,0x08,
            0x04,0x15,0x0E,0x04,0x0E,0x15,0x04,0x00,0x04,0x04,0x1F,0x04,0x04,0x00,
            0x00,0x00,0x00,0x00,0x04,0x04,0x08,0x00,0x00,0x00,0x1F,0x00,0x00,0x00,
            0x00,0x00,0x00,0x00,0x00,0x00,0x04,0x01,0x02,0x02,0x04,0x08,0x08,0x10,
            0x0E,0x11,0x13,0x15,0x19,0x11,0x0E,0x04,0x0C,0x04,0x04,0x04,0x04,0x0E,
            0x0E,0x11,0x01,0x02,0x04,0x08,0x1F,0x0E,0x11,0x01,0x06,0x01,0x11,0x0E,
            0x02,0x06,0x0A,0x12,0x1F,0x02,0x02,0x1F,0x10,0x1E,0x01,0x01,0x11,0x0E,
            0x06,0x08,0x10,0x1E,0x11,0x11,0x0E,0x1F,0x01,0x02,0x04,0x08,0x08,0x08,
            0x0E,0x11,0x11,0x0E,0x11,0x11,0x0E,0x0E,0x11,0x11,0x0F,0x01,0x02,0x0C,
            0x00,0x04,0x00,0x00,0x00,0x04,0x00,0x00,0x04,0x00,0x00,0x04,0x04,0x08,
            0x02,0x04,0x08,0x10,0x08,0x04,0x02,0x00,0x00,0x1F,0x00,0x1F,0x00,0x00,
            0x08,0x04,0x02,0x01,0x02,0x04,0x08,0x0E,0x11,0x01,0x02,0x04,0x00,0x04,
            0x0E,0x11,0x01,0x0D,0x15,0x15,0x0E,0x04,0x0A,0x11,0x11,0x1F,0x11,0x11,
            0x1E,0x11,0x11,0x1E,0x11,0x11,0x1E,0x0E,0x11,0x10,0x10,0x10,0x11,0x0E,
            0x1C,0x12,0x11,0x11,0x11,0x12,0x1C,0x1F,0x10,0x10,0x1E,0x10,0x10,0x1F,
            0x1F,0x10,0x10,0x1E,0x10,0x10,0x10,0x0E,0x11,0x10,0x17,0x11,0x11,0x0F,
            0x11,0x11,0x11,0x1F,0x11,0x11,0x11,0x0E,0x04,0x04,0x04,0x04,0x04,0x0E,
            0x01,0x01,0x01,0x01,0x01,0x11,0x0E,0x11,0x12,0x14,0x18,0x14,0x12,0x11,
            0x10,0x10,0x10,0x10,0x10,0x10,0x1F,0x11,0x1B,0x15,0x15,0x11,0x11,0x11,
            0x11,0x11,0x19,0x15,0x13,0x11,0x11,0x0E,0x11,0x11,0x11,0x11,0x11,0x0E,
            0x1E,0x11,0x11,0x1E,0x10,0x10,0x10,0x0E,0x11,0x11,0x11,0x15,0x12,0x0D,
            0x1E,0x11,0x11,0x1E,0x14,0x12,0x11,0x0E,0x11,0x10,0x0E,0x01,0x11,0x0E,
            0x1F,0x04,0x04,0x04,0x04,0x04,0x04,0x11,0x11,0x11,0x11,0x11,0x11,0x0E,
            0x11,0x11,0x11,0x0A,0x0A,0x04,0x04,0x11,0x11,0x15,0x15,0x15,0x1B,0x11,
            0x11,0x11,0x0A,0x04,0x0A,0x11,0x11,0x11,0x11,0x0A,0x04,0x04,0x04,0x04,
            0x1F,0x01,0x02,0x04,0x08,0x10,0x1F,0x0E,0x08,0x08,0x08,0x08,0x08,0x0E,
            0x10,0x08,0x08,0x04,0x02,0x02,0x01,0x0E,0x02,0x02,0x02,0x02,0x02,0x0E,
            0x04,0x0A,0x11,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x1F,
            0x08,0x04,0x02,0x00,0x00,0x00,0x00,0x00,0x00,0x0E,0x01,0x0F,0x11,0x0F,
            0x10,0x10,0x1C,0x12,0x11,0x12,0x1C,0x00,0x00,0x0E,0x10,0x10,0x11,0x0E,
            0x01,0x01,0x0F,0x11,0x11,0x13,0x0D,0x00,0x00,0x0E,0x11,0x1F,0x10,0x0E,
            0x06,0x09,0x08,0x1C,0x08,0x08,0x08,0x00,0x0D,0x11,0x11,0x0F,0x01,0x0E,
            0x10,0x10,0x16,0x19,0x11,0x11,0x11,0x04,0x00,0x0C,0x04,0x04,0x04,0x0E,
            0x02,0x00,0x06,0x02,0x02,0x12,0x0C,0x10,0x10,0x12,0x14,0x18,0x14,0x13,
            0x0C,0x04,0x04,0x04,0x04,0x04,0x0E,0x00,0x00,0x1A,0x15,0x15,0x11,0x11,
            0x00,0x00,0x16,0x19,0x11,0x11,0x11,0x00,0x00,0x0E,0x11,0x11,0x11,0x0E,
            0x00,0x00,0x1C,0x12,0x11,0x12,0x1C,0x00,0x00,0x0D,0x13,0x11,0x0F,0x01,
            0x00,0x00,0x16,0x19,0x10,0x10,0x10,0x00,0x00,0x0F,0x10,0x0E,0x01,0x1E,
            0x08,0x08,0x1C,0x08,0x08,0x09,0x06,0x00,0x00,0x11,0x11,0x11,0x13,0x0D,
            0x00,0x00,0x11,0x11,0x0A,0x0A,0x04,0x00,0x00,0x11,0x15,0x15,0x15,0x0A,
            0x00,0x00,0x11,0x0A,0x04,0x0A,0x11,0x00,0x00,0x11,0x11,0x0F,0x01,0x0E,
            0x00,0x00,0x1F,0x02,0x04,0x08,0x1F,0x02,0x04,0x04,0x08,0x04,0x04,0x02,
            0x04,0x04,0x04,0x04,0x04,0x04,0x04,0x08,0x04,0x04,0x02,0x04,0x04,0x08,
            0x00,0x00,0x00,0x08,0x15,0x02,0x00,
        ];
    }
}
