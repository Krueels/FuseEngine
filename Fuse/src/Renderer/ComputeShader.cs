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
    private readonly uint _id;

    public bool IsValid { get; }
    public uint ID => _id;

    private ComputeShader(GL gl, string source)
    {
        _gl = gl;
        uint shader = gl.CreateShader(ShaderType.ComputeShader);
        byte[] sourceBytes = Encoding.UTF8.GetBytes(source);
        fixed (byte* sourcePointer = sourceBytes)
        {
            byte* pointer = sourcePointer;
            int length = sourceBytes.Length;
            gl.ShaderSource(shader, 1, &pointer, &length);
        }

        gl.CompileShader(shader);
        gl.GetShader(shader, GLEnum.CompileStatus, out int compileStatus);
        bool compileValid = compileStatus != 0;
        if (!compileValid)
            Logger.Error($"COMPUTE shader compile error:\n{gl.GetShaderInfoLog(shader)}");

        _id = gl.CreateProgram();
        gl.AttachShader(_id, shader);
        gl.LinkProgram(_id);
        gl.GetProgram(_id, GLEnum.LinkStatus, out int linkStatus);
        bool linkValid = linkStatus != 0;
        if (!linkValid)
            Logger.Error($"COMPUTE shader link error: {gl.GetProgramInfoLog(_id)}");

        IsValid = compileValid && linkValid;
        gl.DeleteShader(shader);
    }

    public static ComputeShader FromFile(GL gl, string path)
    {
        string source = Shader.PreprocessIncludes(File.ReadAllText(path), Path.GetDirectoryName(path)!);
        return new ComputeShader(gl, source);
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

    public void Dispose() => _gl.DeleteProgram(_id);
}
