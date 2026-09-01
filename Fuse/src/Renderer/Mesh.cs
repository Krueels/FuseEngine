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
    public Vector3 Tangent;
    public Vector3 Bitangent;
}

public readonly record struct MeshPart(uint IndexOffset, uint IndexCount, int MaterialSlot);

public unsafe class Mesh : IDisposable
{
    private readonly GL _gl;
    private uint _vao;
    private uint _vbo;
    private uint _ebo;
    private uint _lineEbo;
    private uint _indexCount;
    private uint _vertexCount;
    private uint _lineIndexCount;
    private readonly MeshPart[] _parts;

    public uint Vao => _vao;
    public uint Vbo => _vbo;
    public uint Ebo => _ebo;
    public uint IndexCount => _indexCount;
    public IReadOnlyList<MeshPart> Parts => _parts;
    public AABB LocalBounds { get; private set; }
    public BoundingSphere LocalBoundingSphere { get; private set; }

    public bool HasLineBuffer => _lineEbo != 0;

    public Mesh(GL gl, Vertex[] vertices, uint[] indices, uint[]? lineIndices = null, MeshPart[]? parts = null)
    {
        _gl = gl;
        Vertex[] preparedVertices = EnsureTangents(vertices, indices);
        _indexCount = (uint)indices.Length;
        _vertexCount = (uint)preparedVertices.Length;
        _parts = parts is { Length: > 0 }
            ? parts
            : [new MeshPart(0, _indexCount, 0)];

        Span<Vector3> positions = preparedVertices.Length <= 512
            ? stackalloc Vector3[preparedVertices.Length]
            : new Vector3[preparedVertices.Length];
        for (int i = 0; i < preparedVertices.Length; i++)
            positions[i] = preparedVertices[i].Position;
        LocalBounds = AABB.FromPoints(positions);
        LocalBoundingSphere = BoundingSphere.FromAABB(LocalBounds);

        fixed (Vertex* vPtr = preparedVertices)
        fixed (uint* iPtr = indices)
        {
            _vao = gl.GenVertexArray();
            _vbo = gl.GenBuffer();
            _ebo = gl.GenBuffer();

            gl.BindVertexArray(_vao);

            gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
            gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(preparedVertices.Length * sizeof(Vertex)), vPtr, BufferUsageARB.StaticDraw);

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

            gl.EnableVertexAttribArray(3);
            gl.VertexAttribPointer(3, 3, VertexAttribPointerType.Float, false, (uint)sizeof(Vertex),
                (void*)(sizeof(Vector3) + sizeof(Vector2) + sizeof(Vector3)));

            gl.EnableVertexAttribArray(4);
            gl.VertexAttribPointer(4, 3, VertexAttribPointerType.Float, false, (uint)sizeof(Vertex),
                (void*)(sizeof(Vector3) + sizeof(Vector2) + sizeof(Vector3) * 2));

            gl.BindVertexArray(0);
        }

        Logger.Info($"Mesh created with {preparedVertices.Length} verts, {indices.Length} indices");
    }

    private static Vertex[] EnsureTangents(Vertex[] source, uint[] indices)
    {
        Vertex[] vertices = (Vertex[])source.Clone();
        Vector3[] tangentSum = new Vector3[vertices.Length];
        Vector3[] bitangentSum = new Vector3[vertices.Length];

        for (int i = 0; i + 2 < indices.Length; i += 3)
        {
            int i0 = (int)indices[i];
            int i1 = (int)indices[i + 1];
            int i2 = (int)indices[i + 2];
            if ((uint)i0 >= vertices.Length || (uint)i1 >= vertices.Length || (uint)i2 >= vertices.Length)
                continue;

            Vector3 edge1 = vertices[i1].Position - vertices[i0].Position;
            Vector3 edge2 = vertices[i2].Position - vertices[i0].Position;
            Vector2 uv1 = vertices[i1].TexCoord - vertices[i0].TexCoord;
            Vector2 uv2 = vertices[i2].TexCoord - vertices[i0].TexCoord;
            float determinant = uv1.X * uv2.Y - uv2.X * uv1.Y;
            if (MathF.Abs(determinant) < 0.000001f)
                continue;

            float inverse = 1.0f / determinant;
            Vector3 tangent = (edge1 * uv2.Y - edge2 * uv1.Y) * inverse;
            Vector3 bitangent = (edge2 * uv1.X - edge1 * uv2.X) * inverse;
            tangentSum[i0] += tangent; tangentSum[i1] += tangent; tangentSum[i2] += tangent;
            bitangentSum[i0] += bitangent; bitangentSum[i1] += bitangent; bitangentSum[i2] += bitangent;
        }

        for (int i = 0; i < vertices.Length; i++)
        {
            Vector3 normal = vertices[i].Normal.LengthSquared() > 0.000001f
                ? Vector3.Normalize(vertices[i].Normal)
                : Vector3.UnitY;
            Vector3 tangent = tangentSum[i];
            if (tangent.LengthSquared() < 0.000001f)
            {
                Vector3 reference = MathF.Abs(normal.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitZ;
                tangent = Vector3.Cross(reference, normal);
            }
            tangent = Vector3.Normalize(tangent - normal * Vector3.Dot(normal, tangent));
            Vector3 bitangent = bitangentSum[i];
            if (bitangent.LengthSquared() < 0.000001f)
                bitangent = Vector3.Cross(normal, tangent);
            else
                bitangent = Vector3.Normalize(bitangent - normal * Vector3.Dot(normal, bitangent));
            if (Vector3.Dot(Vector3.Cross(normal, tangent), bitangent) < 0.0f)
                bitangent = -bitangent;

            vertices[i].Tangent = tangent;
            vertices[i].Bitangent = bitangent;
        }
        return vertices;
    }

    public void UpdateVertices(Vertex[] vertices, uint[] indices)
    {
        if ((uint)vertices.Length != _vertexCount)
            throw new ArgumentException("The updated mesh must contain the same vertex count.", nameof(vertices));

        Vertex[] preparedVertices = EnsureTangents(vertices, indices);
        Span<Vector3> positions = preparedVertices.Length <= 512
            ? stackalloc Vector3[preparedVertices.Length]
            : new Vector3[preparedVertices.Length];
        for (int i = 0; i < preparedVertices.Length; i++)
            positions[i] = preparedVertices[i].Position;
        LocalBounds = AABB.FromPoints(positions);
        LocalBoundingSphere = BoundingSphere.FromAABB(LocalBounds);

        fixed (Vertex* vPtr = preparedVertices)
        {
            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
            _gl.BufferSubData(
                BufferTargetARB.ArrayBuffer,
                0,
                (nuint)(preparedVertices.Length * sizeof(Vertex)),
                vPtr);
            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, 0);
        }
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

    public void DrawPart(MeshPart part)
    {
        if (part.IndexCount == 0)
            return;
        _gl.BindVertexArray(_vao);
        _gl.DrawElements(
            PrimitiveType.Triangles,
            part.IndexCount,
            DrawElementsType.UnsignedInt,
            (void*)(nuint)(part.IndexOffset * sizeof(uint)));
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
