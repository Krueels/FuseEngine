using System.Numerics;
using Silk.NET.OpenGL;
using Fuse.AssetManagement;
using Fuse.Renderer;
using Fuse.Renderer.Materials;

namespace Blowtorch;

public enum MaterialPreviewShape
{
    Cube,
    Sphere
}

/// <summary>
/// Small self-contained renderer used by the material editor. It intentionally
/// uses the same default material shader and lighting UBO as the game.
/// </summary>
public unsafe sealed class MaterialPreviewRenderer : IDisposable
{
    private readonly GL _gl;
    private readonly LightingBuffer _lighting;
    private readonly Mesh _cube;
    private readonly Mesh _sphere;
    private readonly Light[] _pointLights;
    private readonly ImageBasedLighting? _imageBasedLighting;

    private uint _framebuffer;
    private uint _colorTexture;
    private uint _depthBuffer;
    private int _width;
    private int _height;

    public uint ColorTexture => _colorTexture;

    public MaterialPreviewRenderer(GL gl, AssetManager assets, ImageBasedLighting? imageBasedLighting = null)
    {
        _gl = gl;
        _imageBasedLighting = imageBasedLighting;
        _lighting = new LightingBuffer(gl);
        _cube = assets.GetMesh("cube")!;
        _sphere = CreateSphere(gl);
        _pointLights =
        [
            new Light
            {
                Type = LightType.Point,
                Position = new Vector3(2.2f, 2.0f, 2.4f),
                Radius = 6.0f,
                Color = new Vector3(1.0f, 0.91f, 0.78f),
                Intensity = 1.15f
            }
        ];
    }

    public void Render(
        MaterialRuntime material,
        MaterialPreviewShape shape,
        int width,
        int height,
        float yaw,
        float pitch)
    {
        EnsureFramebuffer(Math.Max(width, 64), Math.Max(height, 64));

        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _framebuffer);
        _gl.Viewport(0, 0, (uint)_width, (uint)_height);
        _gl.ClearColor(0.075f, 0.085f, 0.105f, 1.0f);
        _gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
        _gl.Enable(EnableCap.DepthTest);
        _gl.Enable(EnableCap.CullFace);
        _gl.CullFace(GLEnum.Back);
        _gl.DepthMask(true);
        _gl.Disable(EnableCap.Blend);

        Vector3 cameraPosition = new(2.3f, 1.6f, 2.8f);
        _lighting.Upload(
            cameraPosition,
            Vector3.Normalize(new Vector3(0.35f, 1.0f, 0.25f)),
            new Vector3(0.72f, 0.80f, 1.0f),
            0.22f,
            false,
            false,
            true,
            0.0f,
            0.0f,
            1.0f,
            20.0f,
            0.0f,
            20.0f,
            ReadOnlySpan<float>.Empty,
            ReadOnlySpan<float>.Empty,
            ReadOnlySpan<Matrix4x4>.Empty,
            _pointLights,
            ReadOnlySpan<Light>.Empty,
            ReadOnlySpan<Light>.Empty,
            ReadOnlySpan<Matrix4x4>.Empty);

        Fuse.Renderer.Shader shader = material.StaticShader;
        shader.Use();
        shader.SetMat4("uModel", Matrix4x4.CreateRotationY(yaw) * Matrix4x4.CreateRotationX(pitch));
        shader.SetMat4("uView", Matrix4x4.CreateLookAt(cameraPosition, Vector3.Zero, Vector3.UnitY));
        shader.SetMat4("uProj", Matrix4x4.CreatePerspectiveFieldOfView(
            float.DegreesToRadians(42.0f), (float)_width / _height, 0.1f, 20.0f));
        shader.SetVec2("uUvScale", Vector2.One);
        shader.SetVec2("uUvOffset", Vector2.Zero);
        shader.SetFloat("uUvRotation", 0.0f);
        shader.SetBool("uUseTexture", false);
        shader.SetVec3("uColor", Vector3.One);
        shader.SetBool("uIsEmissive", false);
        shader.SetVec3("uEmissiveColor", Vector3.Zero);
        shader.SetFloat("uEmissiveStrength", 0.0f);
        shader.SetFloat("uIsViewmodel", 0.0f);
        shader.SetBool("uOutputSrgb", true);
        if (_imageBasedLighting != null)
            _imageBasedLighting.Bind(shader);
        else
        {
            shader.SetBool("uUseIbl", false);
            shader.SetFloat("uIblIntensity", 1.0f);
        }
        material.Bind(shader);

        bool blend = material.Asset.AlphaMode == MaterialAlphaMode.Blend;
        if (blend)
        {
            _gl.Enable(EnableCap.Blend);
            _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            _gl.DepthMask(false);
        }

        (shape == MaterialPreviewShape.Cube ? _cube : _sphere).Draw();

        if (blend)
        {
            _gl.Disable(EnableCap.Blend);
            _gl.DepthMask(true);
        }

        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        Vector2 displaySize = ImGuiNET.ImGui.GetIO().DisplaySize;
        _gl.Viewport(0, 0, (uint)Math.Max(1, displaySize.X), (uint)Math.Max(1, displaySize.Y));
    }

    public void Dispose()
    {
        _sphere.Dispose();
        _lighting.Dispose();
        if (_colorTexture != 0) _gl.DeleteTexture(_colorTexture);
        if (_depthBuffer != 0) _gl.DeleteRenderbuffer(_depthBuffer);
        if (_framebuffer != 0) _gl.DeleteFramebuffer(_framebuffer);
    }

    private void EnsureFramebuffer(int width, int height)
    {
        if (_framebuffer != 0 && _width == width && _height == height)
            return;

        if (_colorTexture != 0) _gl.DeleteTexture(_colorTexture);
        if (_depthBuffer != 0) _gl.DeleteRenderbuffer(_depthBuffer);
        if (_framebuffer == 0) _framebuffer = _gl.GenFramebuffer();

        _colorTexture = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, _colorTexture);
        _gl.TexImage2D(TextureTarget.Texture2D, 0, (int)InternalFormat.Rgba,
            (uint)width, (uint)height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, null);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);

        _depthBuffer = _gl.GenRenderbuffer();
        _gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, _depthBuffer);
        _gl.RenderbufferStorage(RenderbufferTarget.Renderbuffer, InternalFormat.DepthComponent24, (uint)width, (uint)height);

        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _framebuffer);
        _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D, _colorTexture, 0);
        _gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment,
            RenderbufferTarget.Renderbuffer, _depthBuffer);
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);

        _width = width;
        _height = height;
    }

    private static Mesh CreateSphere(GL gl)
    {
        const int rings = 24;
        const int segments = 32;
        var vertices = new List<Vertex>((rings + 1) * (segments + 1));
        var indices = new List<uint>(rings * segments * 6);

        for (int ring = 0; ring <= rings; ring++)
        {
            float v = (float)ring / rings;
            float theta = v * MathF.PI;
            float y = MathF.Cos(theta);
            float radius = MathF.Sin(theta);
            for (int segment = 0; segment <= segments; segment++)
            {
                float u = (float)segment / segments;
                float phi = u * MathF.Tau;
                Vector3 normal = new(radius * MathF.Cos(phi), y, radius * MathF.Sin(phi));
                vertices.Add(new Vertex
                {
                    Position = normal * 0.78f,
                    Normal = normal,
                    TexCoord = new Vector2(u, 1.0f - v)
                });
            }
        }

        for (int ring = 0; ring < rings; ring++)
        {
            for (int segment = 0; segment < segments; segment++)
            {
                uint a = (uint)(ring * (segments + 1) + segment);
                uint b = a + 1;
                uint c = a + (uint)(segments + 1);
                uint d = c + 1;
                indices.Add(b); indices.Add(a); indices.Add(d);
                indices.Add(d); indices.Add(a); indices.Add(c);
            }
        }

        return new Mesh(gl, [.. vertices], [.. indices]);
    }
}
