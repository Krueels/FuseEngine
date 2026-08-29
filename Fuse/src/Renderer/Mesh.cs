using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.OpenGL;
using Fuse.Core;
using Fuse.Math;

namespace Fuse.Renderer;

[StructLayout(LayoutKind.Sequential)]
public struct Vertex
{
    public Vector3 Position;
    public Vector2 TexCoord;
    public Vector3 Normal;
}

public unsafe class Mesh : IDisposable
{
    private readonly GL _gl;
    private uint _vao;
    private uint _vbo;
    private uint _ebo;
    private uint _lineEbo;
    private uint _indexCount;
    private uint _lineIndexCount;

    public uint Vao => _vao;
    public uint Vbo => _vbo;
    public uint Ebo => _ebo;
    public uint IndexCount => _indexCount;
    public AABB LocalBounds { get; }
    public BoundingSphere LocalBoundingSphere { get; }

    public bool HasLineBuffer => _lineEbo != 0;

    public Mesh(GL gl, Vertex[] vertices, uint[] indices, uint[]? lineIndices = null)
    {
        _gl = gl;
        _indexCount = (uint)indices.Length;

        Span<Vector3> positions = vertices.Length <= 512
            ? stackalloc Vector3[vertices.Length]
            : new Vector3[vertices.Length];
        for (int i = 0; i < vertices.Length; i++)
            positions[i] = vertices[i].Position;
        LocalBounds = AABB.FromPoints(positions);
        LocalBoundingSphere = BoundingSphere.FromAABB(LocalBounds);

        fixed (Vertex* vPtr = vertices)
        fixed (uint* iPtr = indices)
        {
            _vao = gl.GenVertexArray();
            _vbo = gl.GenBuffer();
            _ebo = gl.GenBuffer();

            gl.BindVertexArray(_vao);

            gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
            gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(vertices.Length * sizeof(Vertex)), vPtr, BufferUsageARB.StaticDraw);

            gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, _ebo);
            gl.BufferData(BufferTargetARB.ElementArrayBuffer, (nuint)(indices.Length * sizeof(uint)), iPtr, BufferUsageARB.StaticDraw);

            if (lineIndices != null && lineIndices.Length > 0)
            {
                _lineIndexCount = (uint)lineIndices.Length;
                _lineEbo = gl.GenBuffer();
                fixed (uint* lPtr = lineIndices)
                {
                    // Bind and upload line indices
                    gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, _lineEbo);
                    gl.BufferData(BufferTargetARB.ElementArrayBuffer, (nuint)(lineIndices.Length * sizeof(uint)), lPtr, BufferUsageARB.StaticDraw);
                }
                // Rebind the default triangle EBO so the VAO captures it as default
                gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, _ebo);
            }
            else
            {
                _lineEbo = 0;
                _lineIndexCount = 0;
            }

            gl.EnableVertexAttribArray(0);
            gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, (uint)sizeof(Vertex), (void*)0);

            gl.EnableVertexAttribArray(1);
            gl.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, (uint)sizeof(Vertex), (void*)sizeof(Vector3));

            gl.EnableVertexAttribArray(2);
            gl.VertexAttribPointer(2, 3, VertexAttribPointerType.Float, false, (uint)sizeof(Vertex), (void*)(sizeof(Vector3) + sizeof(Vector2)));

            gl.BindVertexArray(0);
        }

        Logger.Info($"Mesh created with {vertices.Length} verts, {indices.Length} indices");
    }

    public void Dispose()
    {
        _gl.DeleteVertexArray(_vao);
        _gl.DeleteBuffer(_vbo);
        _gl.DeleteBuffer(_ebo);
        if (_lineEbo != 0) _gl.DeleteBuffer(_lineEbo);
    }

    public void DrawLineBuffer()
    {
        if (_lineEbo == 0) return;
        _gl.BindVertexArray(_vao);
        _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, _lineEbo);
        _gl.DrawElements(PrimitiveType.Lines, _lineIndexCount, DrawElementsType.UnsignedInt, (void*)0);
        // Restore default EBO for this VAO
        _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, _ebo);
        _gl.BindVertexArray(0);
    }

    public void Draw()
    {
        _gl.BindVertexArray(_vao);
        _gl.DrawElements(PrimitiveType.Triangles, _indexCount, DrawElementsType.UnsignedInt, (void*)0);
        _gl.BindVertexArray(0);
    }

    public void Draw(PrimitiveType mode)
    {
        _gl.BindVertexArray(_vao);
        _gl.DrawElements(mode, _indexCount, DrawElementsType.UnsignedInt, (void*)0);
        _gl.BindVertexArray(0);
    }

    public static Mesh CreateCube(GL gl)
    {
        var vertices = new Vertex[]
        {
            // Back face (-Z)
            new() { Position = new(-0.5f, -0.5f, -0.5f), TexCoord = new(0, 0), Normal = new(0, 0, -1) },
            new() { Position = new( 0.5f, -0.5f, -0.5f), TexCoord = new(1, 0), Normal = new(0, 0, -1) },
            new() { Position = new( 0.5f,  0.5f, -0.5f), TexCoord = new(1, 1), Normal = new(0, 0, -1) },
            new() { Position = new(-0.5f,  0.5f, -0.5f), TexCoord = new(0, 1), Normal = new(0, 0, -1) },
            // Right face (+X)
            new() { Position = new( 0.5f, -0.5f, -0.5f), TexCoord = new(0, 0), Normal = new(1, 0, 0) },
            new() { Position = new( 0.5f, -0.5f,  0.5f), TexCoord = new(1, 0), Normal = new(1, 0, 0) },
            new() { Position = new( 0.5f,  0.5f,  0.5f), TexCoord = new(1, 1), Normal = new(1, 0, 0) },
            new() { Position = new( 0.5f,  0.5f, -0.5f), TexCoord = new(0, 1), Normal = new(1, 0, 0) },
            // Front face (+Z)
            new() { Position = new( 0.5f, -0.5f,  0.5f), TexCoord = new(0, 0), Normal = new(0, 0, 1) },
            new() { Position = new(-0.5f, -0.5f,  0.5f), TexCoord = new(1, 0), Normal = new(0, 0, 1) },
            new() { Position = new(-0.5f,  0.5f,  0.5f), TexCoord = new(1, 1), Normal = new(0, 0, 1) },
            new() { Position = new( 0.5f,  0.5f,  0.5f), TexCoord = new(0, 1), Normal = new(0, 0, 1) },
            // Left face (-X)
            new() { Position = new(-0.5f, -0.5f,  0.5f), TexCoord = new(0, 0), Normal = new(-1, 0, 0) },
            new() { Position = new(-0.5f, -0.5f, -0.5f), TexCoord = new(1, 0), Normal = new(-1, 0, 0) },
            new() { Position = new(-0.5f,  0.5f, -0.5f), TexCoord = new(1, 1), Normal = new(-1, 0, 0) },
            new() { Position = new(-0.5f,  0.5f,  0.5f), TexCoord = new(0, 1), Normal = new(-1, 0, 0) },
            // Top face (+Y)
            new() { Position = new(-0.5f,  0.5f, -0.5f), TexCoord = new(0, 0), Normal = new(0, 1, 0) },
            new() { Position = new( 0.5f,  0.5f, -0.5f), TexCoord = new(1, 0), Normal = new(0, 1, 0) },
            new() { Position = new( 0.5f,  0.5f,  0.5f), TexCoord = new(1, 1), Normal = new(0, 1, 0) },
            new() { Position = new(-0.5f,  0.5f,  0.5f), TexCoord = new(0, 1), Normal = new(0, 1, 0) },
            // Bottom face (-Y)
            new() { Position = new(-0.5f, -0.5f,  0.5f), TexCoord = new(0, 0), Normal = new(0, -1, 0) },
            new() { Position = new( 0.5f, -0.5f,  0.5f), TexCoord = new(1, 0), Normal = new(0, -1, 0) },
            new() { Position = new( 0.5f, -0.5f, -0.5f), TexCoord = new(1, 1), Normal = new(0, -1, 0) },
            new() { Position = new(-0.5f, -0.5f, -0.5f), TexCoord = new(0, 1), Normal = new(0, -1, 0) },
        };

        var indices = new uint[36];
        for (uint i = 0; i < 6; i++)
        {
            uint baseIdx = i * 4;
            indices[i * 6 + 0] = baseIdx + 1;
            indices[i * 6 + 1] = baseIdx + 0;
            indices[i * 6 + 2] = baseIdx + 2;
            indices[i * 6 + 3] = baseIdx + 3;
            indices[i * 6 + 4] = baseIdx + 2;
            indices[i * 6 + 5] = baseIdx + 0;
        }

        return new Mesh(gl, vertices, indices);
    }

    public static Mesh CreateGround(GL gl, float size = 10.0f, float tiles = 10.0f)
    {
        float h = size * 0.5f;
        var up = new Vector3(0, 1, 0);
        var vertices = new Vertex[]
        {
            new() { Position = new(-h, 0, -h), TexCoord = new(0, 0), Normal = up },
            new() { Position = new( h, 0, -h), TexCoord = new(tiles, 0), Normal = up },
            new() { Position = new( h, 0,  h), TexCoord = new(tiles, tiles), Normal = up },
            new() { Position = new(-h, 0,  h), TexCoord = new(0, tiles), Normal = up },
        };
        uint[] indices = { 1, 0, 2, 3, 2, 0 };
        return new Mesh(gl, vertices, indices);
    }
}
