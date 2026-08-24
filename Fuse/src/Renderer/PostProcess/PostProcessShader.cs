using Silk.NET.OpenGL;
using Fuse.AssetManagement;
using Fuse.Core;

namespace Fuse.Renderer.PostProcess;

public sealed class PostProcessShader : IDisposable
{
    private readonly GL _gl;
    private readonly Shader _shader;
    
    // Uniform locations
    public int UPass { get; private set; }
    public int UScene { get; private set; }
    public int UBloom { get; private set; }
    public int UExposure { get; private set; }
    public int UBloomStrength { get; private set; }
    public int UBloomThreshold { get; private set; }
    public int UBloomKnee { get; private set; }
    public int UTexelSize { get; private set; }
    public int UDebugView { get; private set; }
    public int UKawaseRadius { get; private set; }
    public int UKawaseIterations { get; private set; }
    public int UBloomScale { get; private set; }
    public int UBloomTint { get; private set; }
    public int UBloomAnamorphicRatio { get; private set; }

    public PostProcessShader(GL gl, AssetManager assets)
    {
        _gl = gl;
        _shader = assets.GetShader(
            Bible.Shader(Bible.PostProcessVert),
            Bible.Shader(Bible.PostProcessFrag))!;
        CacheUniforms();
    }

    private void CacheUniforms()
    {
        _shader.Use();
        UPass = _gl.GetUniformLocation(_shader.ID, "uPass");
        UScene = _gl.GetUniformLocation(_shader.ID, "uScene");
        UBloom = _gl.GetUniformLocation(_shader.ID, "uBloom");
        UExposure = _gl.GetUniformLocation(_shader.ID, "uExposure");
        UBloomStrength = _gl.GetUniformLocation(_shader.ID, "uBloomStrength");
        UBloomThreshold = _gl.GetUniformLocation(_shader.ID, "uBloomThreshold");
        UBloomKnee = _gl.GetUniformLocation(_shader.ID, "uBloomKnee");
        UTexelSize = _gl.GetUniformLocation(_shader.ID, "uTexelSize");
        UDebugView = _gl.GetUniformLocation(_shader.ID, "uDebugView");
        UKawaseRadius = _gl.GetUniformLocation(_shader.ID, "uKawaseRadius");
        UKawaseIterations = _gl.GetUniformLocation(_shader.ID, "uKawaseIterations");
        UBloomScale = _gl.GetUniformLocation(_shader.ID, "uBloomScale");
        UBloomTint = _gl.GetUniformLocation(_shader.ID, "uBloomTint");
        UBloomAnamorphicRatio = _gl.GetUniformLocation(_shader.ID, "uBloomAnamorphicRatio");
    }

    public void Use()
    {
        _shader.Use();
    }

    public void SetPass(int pass)
    {
        _gl.Uniform1(UPass, pass);
    }

    public void SetSceneTexture(int slot)
    {
        _gl.Uniform1(UScene, slot);
    }

    public void SetBloomTexture(int slot)
    {
        _gl.Uniform1(UBloom, slot);
    }

    public void SetParams(PostProcessSettings settings, int width, int height)
    {
        _gl.Uniform1(UExposure, settings.Exposure);
        _gl.Uniform1(UBloomStrength, settings.BloomStrength);
        _gl.Uniform1(UBloomThreshold, settings.BloomThreshold);
        _gl.Uniform1(UBloomKnee, settings.BloomKnee);
        _gl.Uniform2(UTexelSize, 1f / width, 1f / height);
        _gl.Uniform1(UDebugView, settings.DebugView);
        _gl.Uniform1(UBloomScale, settings.BloomScale);
        _gl.Uniform3(UBloomTint, settings.BloomTint.X, settings.BloomTint.Y, settings.BloomTint.Z);
        _gl.Uniform1(UBloomAnamorphicRatio, settings.BloomAnamorphicRatio);
    }

    public void Dispose()
    {
        _shader?.Dispose();
    }

    public void SetKawaseParams(int radius, int iterations)
    {
        _gl.Uniform1(UKawaseRadius, radius);
        _gl.Uniform1(UKawaseIterations, iterations);
    }
}