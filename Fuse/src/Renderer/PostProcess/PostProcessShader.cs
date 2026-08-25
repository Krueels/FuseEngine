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
    public int UInvViewProj { get; private set; }
    public int UPrevVP { get; private set; }
    public int UScreenSize { get; private set; }
    public int UMotionBlurIntensity { get; private set; }
    public int UMotionBlurSamples { get; private set; }
    public int UDepth { get; private set; }

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
        UInvViewProj = _gl.GetUniformLocation(_shader.ID, "uInvViewProj");
        UPrevVP = _gl.GetUniformLocation(_shader.ID, "uPrevVP");
        UScreenSize = _gl.GetUniformLocation(_shader.ID, "uScreenSize");
        UMotionBlurIntensity = _gl.GetUniformLocation(_shader.ID, "uMotionBlurIntensity");
        UMotionBlurSamples = _gl.GetUniformLocation(_shader.ID, "uMotionBlurSamples");
        UDepth = _gl.GetUniformLocation(_shader.ID, "uDepth");
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
        _gl.Uniform1(UMotionBlurIntensity, settings.MotionBlurIntensity);
        _gl.Uniform1(UMotionBlurSamples, settings.MotionBlurSamples);
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

    public unsafe void SetMotionBlurMatrices(System.Numerics.Matrix4x4 currentVP, System.Numerics.Matrix4x4 prevVP, int width, int height)
    {
        // Inverse current VP for depth reconstruction
        System.Numerics.Matrix4x4.Invert(currentVP, out var invCurrentVP);
        float[] m1 = [
            invCurrentVP.M11, invCurrentVP.M12, invCurrentVP.M13, invCurrentVP.M14,
            invCurrentVP.M21, invCurrentVP.M22, invCurrentVP.M23, invCurrentVP.M24,
            invCurrentVP.M31, invCurrentVP.M32, invCurrentVP.M33, invCurrentVP.M34,
            invCurrentVP.M41, invCurrentVP.M42, invCurrentVP.M43, invCurrentVP.M44,
        ];
        fixed (float* p = m1)
            _gl.UniformMatrix4(UInvViewProj, 1, false, p);

        // Previous VP for velocity computation
        float[] m2 = [
            prevVP.M11, prevVP.M12, prevVP.M13, prevVP.M14,
            prevVP.M21, prevVP.M22, prevVP.M23, prevVP.M24,
            prevVP.M31, prevVP.M32, prevVP.M33, prevVP.M34,
            prevVP.M41, prevVP.M42, prevVP.M43, prevVP.M44,
        ];
        fixed (float* p = m2)
            _gl.UniformMatrix4(UPrevVP, 1, false, p);

        // Screen size for noise function
        _gl.Uniform2(UScreenSize, (float)width, (float)height);
    }

    public void SetDepthTexture(int slot)
    {
        _gl.Uniform1(UDepth, slot);
    }
}