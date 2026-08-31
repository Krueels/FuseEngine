using System.Numerics;
using System.Text;
using Silk.NET.OpenGL;
using Fuse.Core;

namespace Fuse.Renderer;

public unsafe class Shader : IDisposable
{
    private readonly GL _gl;
    private uint _id;
    private string? _vertexPath;
    private string? _fragmentPath;
    private bool _disposed;
    private readonly Dictionary<string, uint> _uniformBlockBindings = new();
    public bool IsValid { get; private set; }

    public Shader(GL gl, string vertexSrc, string fragmentSrc)
    {
        _gl = gl;
        _id = BuildProgram(vertexSrc, fragmentSrc, out bool valid);
        IsValid = valid;
    }

    public static Shader FromFile(GL gl, string vertexPath, string fragmentPath)
    {
        string fullVertexPath = Path.GetFullPath(vertexPath);
        string fullFragmentPath = Path.GetFullPath(fragmentPath);
        string vertSrc = PreprocessIncludes(File.ReadAllText(fullVertexPath), Path.GetDirectoryName(fullVertexPath)!);
        string fragSrc = PreprocessIncludes(File.ReadAllText(fullFragmentPath), Path.GetDirectoryName(fullFragmentPath)!);
        return FromSources(gl, fullVertexPath, fullFragmentPath, vertSrc, fragSrc);
    }

    internal static Shader FromSources(
        GL gl,
        string vertexPath,
        string fragmentPath,
        string vertexSource,
        string fragmentSource)
    {
        var shader = new Shader(gl, vertexSource, fragmentSource)
        {
            _vertexPath = Path.GetFullPath(vertexPath),
            _fragmentPath = Path.GetFullPath(fragmentPath)
        };
        return shader;
    }

    /// <summary>
    /// Recompiles a file-backed shader and swaps the OpenGL program only after
    /// the replacement compiled and linked successfully. Existing references to
    /// this Shader therefore keep working after a hot reload.
    /// </summary>
    public bool Reload()
    {
        if (_disposed || string.IsNullOrWhiteSpace(_vertexPath) || string.IsNullOrWhiteSpace(_fragmentPath))
            return false;

        try
        {
            string vertexSource = PreprocessIncludes(
                File.ReadAllText(_vertexPath), Path.GetDirectoryName(_vertexPath)!);
            string fragmentSource = PreprocessIncludes(
                File.ReadAllText(_fragmentPath), Path.GetDirectoryName(_fragmentPath)!);
            return ReloadSources(vertexSource, fragmentSource);
        }
        catch (Exception ex)
        {
            Logger.Error($"[ShaderHotReload] Falha ao ler shader '{_vertexPath}'/'{_fragmentPath}': {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Rebuilds a generated shader (for example, a material graph shader) while
    /// preserving its object identity for all entities that already reference it.
    /// </summary>
    internal bool ReloadSources(string vertexSource, string fragmentSource)
    {
        if (_disposed)
            return false;

        uint replacement = BuildProgram(vertexSource, fragmentSource, out bool valid);
        if (!valid)
        {
            _gl.DeleteProgram(replacement);
            return false;
        }

        uint previous = _id;
        _id = replacement;
        IsValid = true;
        _uniformCache.Clear();

        foreach ((string blockName, uint bindingPoint) in _uniformBlockBindings)
        {
            uint blockIndex = _gl.GetUniformBlockIndex(_id, blockName);
            if (blockIndex != uint.MaxValue)
                _gl.UniformBlockBinding(_id, blockIndex, bindingPoint);
        }

        if (previous != 0)
            _gl.DeleteProgram(previous);
        return true;
    }

    internal static string PreprocessIncludes(string source, string dir)
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
    internal GL Gl => _gl;

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
    private Matrix4x4[] _transposedMatrices = [];

    public unsafe void SetBonesSSBO(Matrix4x4[] matrices)
    {
        if (matrices.Length == 0) return;

        if (_bonesSSBO == 0)
        {
            _bonesSSBO = _gl.GenBuffer();
        }

        // System.Numerics é row-major; GLSL std430 espera column-major.
        // Transpor cada matriz antes do upload.
        if (_transposedMatrices.Length < matrices.Length)
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
        if (_disposed)
            return;
        _disposed = true;
        if (_bonesSSBO != 0)
        {
            _gl.DeleteBuffer(_bonesSSBO);
            _bonesSSBO = 0;
        }
        if (_id != 0)
        {
            _gl.DeleteProgram(_id);
            _id = 0;
        }
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

    public void BindUniformBlock(string name, uint bindingPoint)
    {
        _uniformBlockBindings[name] = bindingPoint;
        uint blockIndex = _gl.GetUniformBlockIndex(_id, name);
        if (blockIndex != uint.MaxValue)
            _gl.UniformBlockBinding(_id, blockIndex, bindingPoint);
    }

    private readonly Dictionary<string, int> _uniformCache = new();
    public int GetUniformLoc(string name)
    {
        if (_uniformCache.TryGetValue(name, out int loc))
            return loc;

        loc = _gl.GetUniformLocation(_id, name);
        _uniformCache[name] = loc;
        return loc;
    }

    private uint Compile(ShaderType type, string source, out bool valid)
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
        valid = success != 0;
        if (!valid)
        {
            string info = _gl.GetShaderInfoLog(shader);
            string typeName = type == ShaderType.VertexShader ? "VERTEX" : "FRAGMENT";
            Logger.Error($"{typeName} shader compile error:\n{info}");
        }
        return shader;
    }

    private uint BuildProgram(string vertexSource, string fragmentSource, out bool valid)
    {
        uint vertexShader = Compile(ShaderType.VertexShader, vertexSource, out bool vertexValid);
        uint fragmentShader = Compile(ShaderType.FragmentShader, fragmentSource, out bool fragmentValid);

        uint program = _gl.CreateProgram();
        _gl.AttachShader(program, vertexShader);
        _gl.AttachShader(program, fragmentShader);
        _gl.LinkProgram(program);

        _gl.GetProgram(program, GLEnum.LinkStatus, out int linkStatus);
        bool linkValid = linkStatus != 0;
        if (!linkValid)
            Logger.Error($"Shader link error: {_gl.GetProgramInfoLog(program)}");

        _gl.DeleteShader(vertexShader);
        _gl.DeleteShader(fragmentShader);
        valid = vertexValid && fragmentValid && linkValid;
        return program;
    }
}
