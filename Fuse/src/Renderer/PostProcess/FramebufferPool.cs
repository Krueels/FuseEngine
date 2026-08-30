using Silk.NET.OpenGL;
using Fuse.Core;

namespace Fuse.Renderer.PostProcess;

public sealed unsafe class FramebufferPool : IDisposable
{
    private readonly GL _gl;
    private int _width, _height;

    public uint HdrFbo { get; private set; }
    public uint HdrColorId { get; private set; }
    public uint HdrEmissiveId { get; private set; }
    public uint HdrDepthTexture { get; private set; }
    
    public int Width => _width;
    public int Height => _height;

    public uint PingPongFbo { get; private set; }
    public uint PingPongColorA { get; private set; }
    public uint PingPongColorB { get; private set; }

    // SSAO
    public uint SsaoFbo { get; private set; }
    public uint SsaoColorTex { get; private set; }
    public uint SsaoBlurFbo { get; private set; }
    public uint SsaoBlurColorTex { get; private set; }

    private uint _depthRbo;

    public FramebufferPool(GL gl, int width, int height)
    {
        _gl = gl;
        _width = width;
        _height = height;
        CreateAll();
    }

    private void CreateAll()
    {
        // HDR Framebuffer (RGBA16F)
        HdrFbo = _gl.GenFramebuffer();
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, HdrFbo);

        HdrColorId = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, HdrColorId);
        _gl.TexImage2D(TextureTarget.Texture2D, 0, (int)InternalFormat.Rgba16f, (uint)_width, (uint)_height, 0,
            PixelFormat.Rgba, PixelType.Float, null);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);

        _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, HdrColorId, 0);

        // Segundo alvo HDR: contém somente a radiância emissiva dos materiais.
        // O bloom usa este attachment em vez da cena completa, evitando que
        // reflexos especulares e o environment map sejam tratados como emissão.
        HdrEmissiveId = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, HdrEmissiveId);
        _gl.TexImage2D(TextureTarget.Texture2D, 0, (int)InternalFormat.Rgba16f, (uint)_width, (uint)_height, 0,
            PixelFormat.Rgba, PixelType.Float, null);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);

        _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment1,
            TextureTarget.Texture2D, HdrEmissiveId, 0);

        // DEPTH TEXTURE (not renderbuffer - so we can sample in shader)
        HdrDepthTexture = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, HdrDepthTexture);
        _gl.TexImage2D(TextureTarget.Texture2D, 0,
            (int)InternalFormat.DepthComponent32f, (uint)_width, (uint)_height, 0,
            PixelFormat.DepthComponent, PixelType.Float, null);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Nearest);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Nearest);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureCompareMode, (int)TextureCompareMode.None);

        _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, TextureTarget.Texture2D, HdrDepthTexture, 0);

        _gl.DrawBuffers(new[]
        {
            DrawBufferMode.ColorAttachment0,
            DrawBufferMode.ColorAttachment1
        });

        var status = _gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        if (status != GLEnum.FramebufferComplete)
            throw new Exception($"HDR FBO incomplete: {status}");

        // Ping-pong Framebuffer (duas texturas para blur)
        PingPongFbo = _gl.GenFramebuffer();
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, PingPongFbo);

        PingPongColorA = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, PingPongColorA);
        _gl.TexImage2D(TextureTarget.Texture2D, 0, (int)InternalFormat.Rgba16f, (uint)_width, (uint)_height, 0,
            PixelFormat.Rgba, PixelType.Float, null);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);

        PingPongColorB = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, PingPongColorB);
        _gl.TexImage2D(TextureTarget.Texture2D, 0, (int)InternalFormat.Rgba16f, (uint)_width, (uint)_height, 0,
            PixelFormat.Rgba, PixelType.Float, null);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);

        _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, PingPongColorA, 0);

        status = _gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        if (status != GLEnum.FramebufferComplete)
            throw new Exception($"Ping-pong FBO incomplete: {status}");

        // ==================== SSAO FBO (R8) ====================
        SsaoFbo = _gl.GenFramebuffer();
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, SsaoFbo);

        SsaoColorTex = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, SsaoColorTex);
        _gl.TexImage2D(TextureTarget.Texture2D, 0, (int)InternalFormat.R8,
            (uint)_width, (uint)_height, 0,
            PixelFormat.Red, PixelType.UnsignedByte, null);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Nearest);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Nearest);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);

        _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D, SsaoColorTex, 0);

        status = _gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        if (status != GLEnum.FramebufferComplete)
            throw new Exception($"SSAO FBO incomplete: {status}");

        // ==================== SSAO BLUR FBO (R8) ====================
        SsaoBlurFbo = _gl.GenFramebuffer();
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, SsaoBlurFbo);

        SsaoBlurColorTex = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, SsaoBlurColorTex);
        _gl.TexImage2D(TextureTarget.Texture2D, 0, (int)InternalFormat.R8,
            (uint)_width, (uint)_height, 0,
            PixelFormat.Red, PixelType.UnsignedByte, null);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Nearest);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Nearest);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);

        _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D, SsaoBlurColorTex, 0);

        status = _gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        if (status != GLEnum.FramebufferComplete)
            throw new Exception($"SSAO Blur FBO incomplete: {status}");

        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    public void Resize(int width, int height)
    {
        if (width == _width && height == _height && HdrFbo != 0) return;

        _width = width;
        _height = height;

        DisposeTextures();
        CreateAll();
    }

    /// <summary>Força recriação de todos os recursos GPU (mesmo com dimensões iguais)</summary>
    public void Recreate()
    {
        DisposeTextures();
        CreateAll();
    }

    private void DisposeTextures()
    {
        // Never delete attachments while a post-process FBO is still bound.
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        _gl.BindTexture(TextureTarget.Texture2D, 0);
        _gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, 0);

        if (HdrColorId != 0) { _gl.DeleteTexture(HdrColorId); HdrColorId = 0; }
        if (HdrEmissiveId != 0) { _gl.DeleteTexture(HdrEmissiveId); HdrEmissiveId = 0; }
        if (HdrDepthTexture != 0) { _gl.DeleteTexture(HdrDepthTexture); HdrDepthTexture = 0; }
        if (PingPongColorA != 0) { _gl.DeleteTexture(PingPongColorA); PingPongColorA = 0; }
        if (PingPongColorB != 0) { _gl.DeleteTexture(PingPongColorB); PingPongColorB = 0; }
        if (SsaoColorTex != 0) { _gl.DeleteTexture(SsaoColorTex); SsaoColorTex = 0; }
        if (SsaoBlurColorTex != 0) { _gl.DeleteTexture(SsaoBlurColorTex); SsaoBlurColorTex = 0; }
        if (_depthRbo != 0) { _gl.DeleteRenderbuffer(_depthRbo); _depthRbo = 0; }
        if (HdrFbo != 0) { _gl.DeleteFramebuffer(HdrFbo); HdrFbo = 0; }
        if (PingPongFbo != 0) { _gl.DeleteFramebuffer(PingPongFbo); PingPongFbo = 0; }
        if (SsaoFbo != 0) { _gl.DeleteFramebuffer(SsaoFbo); SsaoFbo = 0; }
        if (SsaoBlurFbo != 0) { _gl.DeleteFramebuffer(SsaoBlurFbo); SsaoBlurFbo = 0; }
    }

    public void Dispose()
    {
        DisposeTextures();
    }

    public bool Validate(GL gl)
    {
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, HdrFbo);
        var status = gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        return status == GLEnum.FramebufferComplete;
    }
}
