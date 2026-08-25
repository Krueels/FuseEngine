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
        string vertSrc = PreprocessIncludes(File.ReadAllText(vertexPath), Path.GetDirectoryName(vertexPath)!);
        string fragSrc = PreprocessIncludes(File.ReadAllText(fragmentPath), Path.GetDirectoryName(fragmentPath)!);
        return new Shader(gl, vertSrc, fragSrc);
    }

    private static string PreprocessIncludes(string source, string dir)
    {
        var included = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return ProcessIncludes(source, dir, included);
    }

    private static string ProcessIncludes(string source, string dir, HashSet<string> included)
    {
        var lines = source.Split('\n');
        var result = new List<string>();

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("#include"))
            {
                string path = trimmed.Substring(8).Trim().Trim('"').Trim('<', '>');
                string fullPath = Path.Combine(dir, path);

                if (included.Add(fullPath))
                {
                    string incSrc = File.ReadAllText(fullPath);
                    string incDir = Path.GetDirectoryName(fullPath)!;
                    result.Add(ProcessIncludes(incSrc, incDir, included));
                }
            }
            else
            {
                result.Add(line);
            }
        }

        return string.Join(Environment.NewLine, result);
    }

    public uint ID => _id;

    public void Use()
    {
        _gl.UseProgram(_id);
    }

    public unsafe void SetMat4(string name, Matrix4x4 mat)
    {
        float* values = stackalloc float[16];
        values[0] = mat.M11; values[1] = mat.M12; values[2] = mat.M13; values[3] = mat.M14;
        values[4] = mat.M21; values[5] = mat.M22; values[6] = mat.M23; values[7] = mat.M24;
        values[8] = mat.M31; values[9] = mat.M32; values[10] = mat.M33; values[11] = mat.M34;
        values[12] = mat.M41; values[13] = mat.M42; values[14] = mat.M43; values[15] = mat.M44;
        _gl.UniformMatrix4(GetUniformLoc(name), 1, false, values);
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
