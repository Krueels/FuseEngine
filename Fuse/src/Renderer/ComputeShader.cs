using System.Numerics;
using System.Text;
using Fuse.Core;
using Silk.NET.OpenGL;

namespace Fuse.Renderer;

/// <summary>
/// Small compute-shader wrapper used by GPU-driven renderer preparation passes.
/// It intentionally stays separate from Shader because compute programs do not
/// have vertex/fragment stages or a vertex pipeline state.
/// </summary>
public unsafe sealed class ComputeShader : IDisposable
{
    private readonly GL _gl;
    private uint _id;
    private readonly string? _sourcePath;
    private bool _disposed;

    public bool IsValid { get; private set; }
    public uint ID => _id;

    private ComputeShader(GL gl, string source, string? sourcePath)
    {
        _gl = gl;
        _sourcePath = sourcePath;
        _id = BuildProgram(source, out bool valid);
        IsValid = valid;
    }

    public bool Reload()
    {
        if (_disposed || string.IsNullOrWhiteSpace(_sourcePath))
            return false;

        try
        {
            string source = Shader.PreprocessIncludes(
                File.ReadAllText(_sourcePath), Path.GetDirectoryName(_sourcePath)!);
            uint replacement = BuildProgram(source, out bool valid);
            if (!valid)
            {
                _gl.DeleteProgram(replacement);
                return false;
            }

            uint previous = _id;
            _id = replacement;
            IsValid = true;
            _uniformCache.Clear();
            if (previous != 0)
                _gl.DeleteProgram(previous);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error($"[ShaderHotReload] Falha ao ler compute shader '{_sourcePath}': {ex.Message}");
            return false;
        }
    }

    private uint BuildProgram(string source, out bool valid)
    {
        uint shader = _gl.CreateShader(ShaderType.ComputeShader);
        byte[] sourceBytes = Encoding.UTF8.GetBytes(source);
        fixed (byte* sourcePointer = sourceBytes)
        {
            byte* pointer = sourcePointer;
            int length = sourceBytes.Length;
            _gl.ShaderSource(shader, 1, &pointer, &length);
        }

        _gl.CompileShader(shader);
        _gl.GetShader(shader, GLEnum.CompileStatus, out int compileStatus);
        bool compileValid = compileStatus != 0;
        if (!compileValid)
            Logger.Error($"COMPUTE shader compile error:\n{_gl.GetShaderInfoLog(shader)}");

        uint program = _gl.CreateProgram();
        _gl.AttachShader(program, shader);
        _gl.LinkProgram(program);
        _gl.GetProgram(program, GLEnum.LinkStatus, out int linkStatus);
        bool linkValid = linkStatus != 0;
        if (!linkValid)
            Logger.Error($"COMPUTE shader link error: {_gl.GetProgramInfoLog(program)}");

        valid = compileValid && linkValid;
        _gl.DeleteShader(shader);
        return program;
    }

    public static ComputeShader FromFile(GL gl, string path)
    {
        string fullPath = Path.GetFullPath(path);
        string source = Shader.PreprocessIncludes(File.ReadAllText(fullPath), Path.GetDirectoryName(fullPath)!);
        return new ComputeShader(gl, source, fullPath);
    }

    public void Use() => _gl.UseProgram(_id);

    public void SetMat4(string name, Matrix4x4 matrix)
    {
        float* values = stackalloc float[16];
        values[0] = matrix.M11; values[1] = matrix.M12; values[2] = matrix.M13; values[3] = matrix.M14;
        values[4] = matrix.M21; values[5] = matrix.M22; values[6] = matrix.M23; values[7] = matrix.M24;
        values[8] = matrix.M31; values[9] = matrix.M32; values[10] = matrix.M33; values[11] = matrix.M34;
        values[12] = matrix.M41; values[13] = matrix.M42; values[14] = matrix.M43; values[15] = matrix.M44;
        _gl.UniformMatrix4(GetUniformLoc(name), 1, false, values);
    }

    public void SetVec2(string name, Vector2 value) =>
        _gl.Uniform2(GetUniformLoc(name), value.X, value.Y);

    public void SetInt(string name, int value) =>
        _gl.Uniform1(GetUniformLoc(name), value);

    private readonly Dictionary<string, int> _uniformCache = new();
    private int GetUniformLoc(string name)
    {
        if (_uniformCache.TryGetValue(name, out int location))
            return location;

        location = _gl.GetUniformLocation(_id, name);
        _uniformCache[name] = location;
        return location;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_id != 0)
        {
            _gl.DeleteProgram(_id);
            _id = 0;
        }
    }
}
