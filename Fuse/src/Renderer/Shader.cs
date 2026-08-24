using System.Numerics;
using System.Text;
using Silk.NET.OpenGL;
using Fuse.Core;

namespace Fuse.Renderer;

public unsafe class Shader : IDisposable
{
    private readonly GL _gl;
    private readonly uint _id;

    public Shader(GL gl, string vertexSrc, string fragmentSrc)
    {
        _gl = gl;
        uint vs = Compile(ShaderType.VertexShader, vertexSrc);
        uint fs = Compile(ShaderType.FragmentShader, fragmentSrc);

        _id = gl.CreateProgram();
        gl.AttachShader(_id, vs);
        gl.AttachShader(_id, fs);
        gl.LinkProgram(_id);

        gl.GetProgram(_id, GLEnum.LinkStatus, out int success);
        if (success == 0)
        {
            string info = gl.GetProgramInfoLog(_id);
            Logger.Error($"Shader link error: {info}");
        }

        gl.DeleteShader(vs);
        gl.DeleteShader(fs);
    }

    public static Shader FromFile(GL gl, string vertexPath, string fragmentPath)
    {
        return new Shader(gl, File.ReadAllText(vertexPath), File.ReadAllText(fragmentPath));
    }

    public uint ID => _id;

    public void Use()
    {
        _gl.UseProgram(_id);
    }

    public void SetMat4(string name, Matrix4x4 mat)
    {
        _gl.UniformMatrix4(GetUniformLoc(name), 1, false, GetMatrixValues(mat));
    }

    public unsafe void SetMat4(string name, Matrix4x4[] matrices)
    {
        if (matrices.Length == 0) return;
        fixed (Matrix4x4* ptr = matrices)
        {
            _gl.UniformMatrix4(GetUniformLoc(name), (uint)matrices.Length, false, (float*)ptr);
        }
    }

    // SSBO (std430) para matrizes de ossos — binding point 0 conforme shaders
    private uint _bonesSSBO = 0;
    private Matrix4x4[] _transposedMatrices;

    public unsafe void SetBonesSSBO(Matrix4x4[] matrices)
    {
        if (matrices.Length == 0) return;

        if (_bonesSSBO == 0)
        {
            _bonesSSBO = _gl.GenBuffer();
        }

        // System.Numerics é row-major; GLSL std430 espera column-major.
        // Transpor cada matriz antes do upload.
        if (_transposedMatrices == null || _transposedMatrices.Length < matrices.Length)
        {
            _transposedMatrices = new Matrix4x4[matrices.Length];
        }

        for (int i = 0; i < matrices.Length; i++)
        {
            var m = matrices[i];
            _transposedMatrices[i] = Matrix4x4.Transpose(m);
        }

        if (_bonesSSBO == 0)
        {
            _bonesSSBO = _gl.GenBuffer();
        }

        _gl.BindBuffer(GLEnum.ShaderStorageBuffer, _bonesSSBO);
        fixed (Matrix4x4* ptr = _transposedMatrices)
        {
            _gl.BufferData(GLEnum.ShaderStorageBuffer, (nuint)(matrices.Length * sizeof(Matrix4x4)), ptr, GLEnum.DynamicDraw);
        }
        _gl.BindBufferBase(GLEnum.ShaderStorageBuffer, 0, _bonesSSBO);
        _gl.BindBuffer(GLEnum.ShaderStorageBuffer, 0);
    }

    public void Dispose()
    {
        if (_bonesSSBO != 0)
        {
            _gl.DeleteBuffer(_bonesSSBO);
            _bonesSSBO = 0;
        }
        _gl.DeleteProgram(_id);
    }

    public void SetVec3(string name, Vector3 vec)
    {
        _gl.Uniform3(GetUniformLoc(name), vec.X, vec.Y, vec.Z);
    }

    public void SetVec2(string name, Vector2 vec)
    {
        _gl.Uniform2(GetUniformLoc(name), vec.X, vec.Y);
    }

    public void SetFloat(string name, float value)
    {
        _gl.Uniform1(GetUniformLoc(name), value);
    }

    public void SetBool(string name, bool value)
    {
        _gl.Uniform1(GetUniformLoc(name), value ? 1 : 0);
    }

    public void SetInt(string name, int value)
    {
        _gl.Uniform1(GetUniformLoc(name), value);
    }

    private readonly Dictionary<string, int> _uniformCache = new();
    private int GetUniformLoc(string name)
    {
        if (_uniformCache.TryGetValue(name, out int loc))
            return loc;

        loc = _gl.GetUniformLocation(_id, name);
        _uniformCache[name] = loc;
        return loc;
    }

    private static float[] GetMatrixValues(Matrix4x4 mat)
    {
        return new float[16]
        {
            mat.M11, mat.M12, mat.M13, mat.M14,
            mat.M21, mat.M22, mat.M23, mat.M24,
            mat.M31, mat.M32, mat.M33, mat.M34,
            mat.M41, mat.M42, mat.M43, mat.M44,
        };
    }

    private uint Compile(ShaderType type, string source)
    {
        uint shader = _gl.CreateShader(type);

        byte[] srcBytes = Encoding.UTF8.GetBytes(source);
        fixed (byte* ptr = srcBytes)
        {
            int len = srcBytes.Length;
            _gl.ShaderSource(shader, 1, (byte**)&ptr, &len);
        }

        _gl.CompileShader(shader);

        _gl.GetShader(shader, GLEnum.CompileStatus, out int success);
        if (success == 0)
        {
            string info = _gl.GetShaderInfoLog(shader);
            string typeName = type == ShaderType.VertexShader ? "VERTEX" : "FRAGMENT";
            Logger.Error($"{typeName} shader compile error:\n{info}");
        }
        return shader;
    }
}
