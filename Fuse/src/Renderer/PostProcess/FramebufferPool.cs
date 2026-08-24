using Silk.NET.OpenGL;
using Fuse.Core;

namespace Fuse.Renderer.PostProcess;

public sealed unsafe class FramebufferPool : IDisposable
{
    private readonly GL _gl;
    private int _width, _height;

    public uint HdrFbo { get; private set; }
    public uint HdrColorId { get; private set; }
    public uint HdrDepthTexture { get; private set; }
    
    public int Width => _width;
    public int Height => _height;

    public uint PingPongFbo { get; private set; }
    public uint PingPongColorA { get; private set; }
    public uint PingPongColorB { get; private set; }

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

        // DEPTH TEXTURE (not renderbuffer - so we can sample in shader)
        HdrDepthTexture = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, HdrDepthTexture);
        _gl.TexImage2D(TextureTarget.Texture2D, 0, (int)InternalFormat.DepthComponent24, (uint)_width, (uint)_height, 0,
            PixelFormat.DepthComponent, PixelType.UnsignedInt, null);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Nearest);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Nearest);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);

        _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, TextureTarget.Texture2D, HdrDepthTexture, 0);

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
        if (HdrDepthTexture != 0) { _gl.DeleteTexture(HdrDepthTexture); HdrDepthTexture = 0; }
        if (PingPongColorA != 0) { _gl.DeleteTexture(PingPongColorA); PingPongColorA = 0; }
        if (PingPongColorB != 0) { _gl.DeleteTexture(PingPongColorB); PingPongColorB = 0; }
        if (_depthRbo != 0) { _gl.DeleteRenderbuffer(_depthRbo); _depthRbo = 0; }
        if (HdrFbo != 0) { _gl.DeleteFramebuffer(HdrFbo); HdrFbo = 0; }
        if (PingPongFbo != 0) { _gl.DeleteFramebuffer(PingPongFbo); PingPongFbo = 0; }
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
