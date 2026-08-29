using Silk.NET.OpenGL;
using System;

namespace Fuse.Renderer;

public unsafe class PointShadowMap : IDisposable
{
    private readonly GL _gl;
    public uint FBO { get; private set; }
    public uint TextureID { get; private set; }
    public uint Size { get; private set; }

    public PointShadowMap(GL gl, uint size = 512)
    {
        _gl = gl;
        Size = size;

        FBO = _gl.GenFramebuffer();
        TextureID = _gl.GenTexture();

        _gl.BindTexture(TextureTarget.TextureCubeMap, TextureID);
        for (int i = 0; i < 6; i++)
        {
            _gl.TexImage2D(TextureTarget.TextureCubeMapPositiveX + i, 0, InternalFormat.DepthComponent32f, Size, Size, 0, PixelFormat.DepthComponent, PixelType.Float, null);
        }

        _gl.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        _gl.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        _gl.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureCompareMode, (int)TextureCompareMode.None);
        _gl.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureCompareFunc, (int)DepthFunction.Lequal);
        _gl.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        _gl.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        _gl.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureWrapR, (int)TextureWrapMode.ClampToEdge);

        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, FBO);
        _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, TextureTarget.TextureCubeMapPositiveX, TextureID, 0);
        _gl.DrawBuffer(DrawBufferMode.None);
        _gl.ReadBuffer(ReadBufferMode.None);
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    public void BindForWriting(int face, bool clear = true)
    {
        _gl.Viewport(0, 0, Size, Size);
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, FBO);
        _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, TextureTarget.TextureCubeMapPositiveX + face, TextureID, 0);
        if (clear)
            _gl.Clear(ClearBufferMask.DepthBufferBit);
    }

    public void CopyFaceTo(PointShadowMap destination, int face)
    {
        if (destination.Size != Size)
            throw new InvalidOperationException("Point shadow maps must have matching dimensions.");

        TextureTarget target = TextureTarget.TextureCubeMapPositiveX + face;
        _gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, FBO);
        _gl.FramebufferTexture2D(FramebufferTarget.ReadFramebuffer, FramebufferAttachment.DepthAttachment, target, TextureID, 0);
        _gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, destination.FBO);
        _gl.FramebufferTexture2D(FramebufferTarget.DrawFramebuffer, FramebufferAttachment.DepthAttachment, target, destination.TextureID, 0);
        _gl.BlitFramebuffer(0, 0, (int)Size, (int)Size, 0, 0, (int)destination.Size, (int)destination.Size,
            (uint)ClearBufferMask.DepthBufferBit, GLEnum.Nearest);
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    public void BindForReading(TextureUnit unit)
    {
        _gl.ActiveTexture(unit);
        _gl.BindTexture(TextureTarget.TextureCubeMap, TextureID);
    }

    public void Dispose()
    {
        _gl.DeleteFramebuffer(FBO);
        _gl.DeleteTexture(TextureID);
    }
}
