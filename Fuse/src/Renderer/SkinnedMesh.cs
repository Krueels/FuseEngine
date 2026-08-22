using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.OpenGL;
using Fuse.Core;

namespace Fuse.Renderer;

[StructLayout(LayoutKind.Sequential)]
public struct SkinnedVertex
{
    public Vector3 Position;      // offset 0
    public Vector2 TexCoord;      // offset 12
    public Vector3 Normal;        // offset 20
    public int BoneIdX, BoneIdY, BoneIdZ, BoneIdW; // offset 32
    public Vector4 Weights;       // offset 48
}                                 // total 64

public unsafe class SkinnedMesh : IDisposable
{
    public const int MaxBones = 192;

    private readonly GL _gl;
    private readonly uint _vao;
    private readonly uint _vbo;
    private readonly uint _ebo;
    private readonly uint _indexCount;

    public SkinnedMesh(GL gl, SkinnedVertex[] vertices, uint[] indices)
    {
        _gl = gl;
        _indexCount = (uint)indices.Length;

        fixed (SkinnedVertex* vPtr = vertices)
        fixed (uint* iPtr = indices)
        {
            _vao = gl.GenVertexArray();
            _vbo = gl.GenBuffer();
            _ebo = gl.GenBuffer();

            gl.BindVertexArray(_vao);

            gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
            gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(vertices.Length * sizeof(SkinnedVertex)), vPtr, BufferUsageARB.StaticDraw);

            gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, _ebo);
            gl.BufferData(BufferTargetARB.ElementArrayBuffer, (nuint)(indices.Length * sizeof(uint)), iPtr, BufferUsageARB.StaticDraw);

            uint stride = (uint)sizeof(SkinnedVertex);

            gl.EnableVertexAttribArray(0);
            gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, (void*)0);

            gl.EnableVertexAttribArray(1);
            gl.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, stride, (void*)sizeof(Vector3));

            gl.EnableVertexAttribArray(2);
            gl.VertexAttribPointer(2, 3, VertexAttribPointerType.Float, false, stride, (void*)(sizeof(Vector3) + sizeof(Vector2)));

            gl.EnableVertexAttribArray(3);
            gl.VertexAttribIPointer(3, 4, VertexAttribIType.Int, stride, (void*)32);

            gl.EnableVertexAttribArray(4);
            gl.VertexAttribPointer(4, 4, VertexAttribPointerType.Float, false, stride, (void*)48);

            gl.BindVertexArray(0);
        }

        Logger.Info($"SkinnedMesh created with {vertices.Length} verts, {indices.Length} indices");
    }

    public void Dispose()
    {
        _gl.DeleteVertexArray(_vao);
        _gl.DeleteBuffer(_vbo);
        _gl.DeleteBuffer(_ebo);
    }

    public void Draw()
    {
        _gl.BindVertexArray(_vao);
        _gl.DrawElements(PrimitiveType.Triangles, _indexCount, DrawElementsType.UnsignedInt, (void*)0);
        _gl.BindVertexArray(0);
    }
}
