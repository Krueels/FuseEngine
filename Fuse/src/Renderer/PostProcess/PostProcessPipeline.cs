using System.Numerics;
using Fuse.AssetManagement;
using Fuse.Core;
using Silk.NET.OpenGL;

namespace Fuse.Renderer.PostProcess;

public sealed class PostProcessPipeline : IDisposable
{
    private readonly GL _gl;
    private readonly FramebufferPool _fbPool;
    private readonly FullscreenQuad _quad;
    private readonly PostProcessShader _shader;
    private readonly PostProcessSettings _settings;
    private Matrix4x4 _prevViewProj = Matrix4x4.Identity;
    private Matrix4x4 _currentViewProj = Matrix4x4.Identity;

    public PostProcessPipeline(GL gl, AssetManager assets, int width, int height)
    {
        _gl = gl;
        _fbPool = new FramebufferPool(gl, width, height);
        _quad = new FullscreenQuad(gl);
        _shader = new PostProcessShader(gl, assets);
        _settings = new PostProcessSettings();
    }

    public uint HdrFbo => _fbPool.HdrFbo;
    public uint HdrColorId => _fbPool.HdrColorId;
    public uint HdrDepthId => _fbPool.HdrDepthTexture;
    public int Width => _fbPool.Width;
    public int Height => _fbPool.Height;

    public PostProcessSettings Settings => _settings;

    public void SetViewProj(Matrix4x4 prevViewProj, Matrix4x4 view, Matrix4x4 proj)
    {
        _prevViewProj = prevViewProj;
        _currentViewProj = view * proj;
    }

    public void Resize(int width, int height)
    {
        _fbPool.Resize(width, height);
    }

    /// <summary>
    /// Executa pipeline completo: cena HDR -> bloom extract -> blur H/V -> composite final
    /// </summary>
    /// <param name="sceneColorId">ID da textura da cena renderizada (HDR)</param>
    /// <param name="targetFbo">Framebuffer destino (0 = tela, ou FBO custom)</param>
public void Execute(uint sceneColorId, uint targetFbo = 0)
    {
        if (sceneColorId == 0)
        {
            Logger.Error("[PostProcess] Invalid sceneColorId (0)");
            return;
        }

        if (!_fbPool.Validate(_gl))
            _fbPool.Resize(_fbPool.Width, _fbPool.Height);

        bool anyEffect = _settings.Enabled && (_settings.BloomEnabled || _settings.MotionBlurEnabled);
        if (!anyEffect)
        {
            BlitWithTonemap(sceneColorId, targetFbo);
            return;
        }

        if (!CheckFramebufferComplete(_fbPool.HdrFbo) || !CheckFramebufferComplete(_fbPool.PingPongFbo))
        {
            Logger.Error("[PostProcess] Framebuffer incompleto!");
            BlitWithTonemap(sceneColorId, targetFbo);
            return;
        }

        _shader.Use();
        _shader.SetParams(_settings, _fbPool.Width, _fbPool.Height);
        _shader.SetKawaseParams(_settings.KawaseRadius, _settings.KawaseIterations);

        // ============================================================
        // CAMADA 0 → Camada 1: Bloom (só se habilitado)
        // ============================================================
        uint currentLayer = sceneColorId;
        bool useBloom = _settings.BloomEnabled && _settings.BloomStrength > 0f;

        if (useBloom)
        {
            // Bloom Extract → PingPongColorA
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _fbPool.PingPongFbo);
            _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, _fbPool.PingPongColorA, 0);
            _gl.Viewport(0, 0, (uint)_fbPool.Width, (uint)_fbPool.Height);
            _gl.Clear(ClearBufferMask.ColorBufferBit);

            _shader.SetPass(1);
            _shader.SetSceneTexture(0);
            _gl.ActiveTexture(TextureUnit.Texture0);
            _gl.BindTexture(TextureTarget.Texture2D, sceneColorId);
            _quad.Draw();

            // Kawase Blur iterations
            uint lastBloom = _fbPool.PingPongColorA;
            for (int i = 0; i < _settings.KawaseIterations; i++)
            {
                _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, _fbPool.PingPongColorB, 0);
                _gl.Clear(ClearBufferMask.ColorBufferBit);
                _shader.SetPass(2);
                _shader.SetSceneTexture(0);
                _gl.ActiveTexture(TextureUnit.Texture0);
                _gl.BindTexture(TextureTarget.Texture2D, lastBloom);
                _quad.Draw();

                _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, _fbPool.PingPongColorA, 0);
                _gl.Clear(ClearBufferMask.ColorBufferBit);
                _shader.SetPass(3);
                _shader.SetSceneTexture(0);
                _gl.ActiveTexture(TextureUnit.Texture0);
                _gl.BindTexture(TextureTarget.Texture2D, _fbPool.PingPongColorB);
                _quad.Draw();

                lastBloom = _fbPool.PingPongColorA;
            }

            // Composite scene + bloom → PingPongColorB (HDR final com bloom)
            _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, _fbPool.PingPongColorB, 0);
            _gl.Clear(ClearBufferMask.ColorBufferBit);

            _shader.SetPass(4);
            _shader.SetSceneTexture(0);
            _shader.SetBloomTexture(1);
            _gl.ActiveTexture(TextureUnit.Texture0);
            _gl.BindTexture(TextureTarget.Texture2D, sceneColorId);
            _gl.ActiveTexture(TextureUnit.Texture1);
            _gl.BindTexture(TextureTarget.Texture2D, lastBloom);
            _quad.Draw();

            currentLayer = _fbPool.PingPongColorB;
        }

        // ============================================================
        // CAMADA 1 → Camada 2: Motion Blur (só se habilitado)
        // ============================================================
        bool useMotionBlur = _settings.MotionBlurEnabled && _settings.MotionBlurIntensity > 0f;

        if (useMotionBlur)
        {
            uint motionTarget = useBloom ? _fbPool.PingPongColorA : _fbPool.PingPongColorB;
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _fbPool.PingPongFbo);
            _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, motionTarget, 0);
            _gl.Viewport(0, 0, (uint)_fbPool.Width, (uint)_fbPool.Height);
            _gl.Clear(ClearBufferMask.ColorBufferBit);

            _shader.SetPass(5);
            _shader.SetSceneTexture(0);
            _shader.SetDepthTexture(1);
            _shader.SetMotionBlurMatrices(_currentViewProj, _prevViewProj, _fbPool.Width, _fbPool.Height);

            _gl.ActiveTexture(TextureUnit.Texture0);
            _gl.BindTexture(TextureTarget.Texture2D, currentLayer);
            _gl.ActiveTexture(TextureUnit.Texture1);
            _gl.BindTexture(TextureTarget.Texture2D, _fbPool.HdrDepthTexture);
            _quad.Draw();

            currentLayer = motionTarget;
        }

        // ============================================================
        // CAMADA FINAL: Tonemap → target
        // ============================================================
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, targetFbo);
        _gl.Viewport(0, 0, (uint)_fbPool.Width, (uint)_fbPool.Height);
        _gl.Clear(ClearBufferMask.ColorBufferBit);

        _shader.SetPass(6);
        _shader.SetSceneTexture(0);
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, currentLayer);
        _quad.Draw();

        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    private void BlitWithTonemap(uint sceneColorId, uint targetFbo)
    {
        bool useMotionBlur = _settings.MotionBlurEnabled && _settings.MotionBlurIntensity > 0f;

        _shader.Use();
        _shader.SetParams(_settings, _fbPool.Width, _fbPool.Height);

        uint sceneToComposite = sceneColorId;

        if (useMotionBlur)
        {
            // Motion blur em HDR → PingPongColorA
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _fbPool.PingPongFbo);
            _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, _fbPool.PingPongColorA, 0);
            _gl.Viewport(0, 0, (uint)_fbPool.Width, (uint)_fbPool.Height);
            _gl.Clear(ClearBufferMask.ColorBufferBit);

            _shader.SetPass(5);
            _shader.SetSceneTexture(0);
            _shader.SetDepthTexture(1);
            _shader.SetMotionBlurMatrices(_currentViewProj, _prevViewProj, _fbPool.Width, _fbPool.Height);

            _gl.ActiveTexture(TextureUnit.Texture0);
            _gl.BindTexture(TextureTarget.Texture2D, sceneColorId);
            _gl.ActiveTexture(TextureUnit.Texture1);
            _gl.BindTexture(TextureTarget.Texture2D, _fbPool.HdrDepthTexture);
            _quad.Draw();

            sceneToComposite = _fbPool.PingPongColorA;
        }

        // Composite (tonemap) → target, sem bloom
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, targetFbo);
        _gl.Viewport(0, 0, (uint)_fbPool.Width, (uint)_fbPool.Height);
        _gl.Clear(ClearBufferMask.ColorBufferBit);

        _shader.SetPass(6);
        _shader.SetSceneTexture(0);
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, sceneToComposite);
        _quad.Draw();

        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    public void Dispose()
    {
        _fbPool.Dispose();
        _quad.Dispose();
        _shader.Dispose();
    }

    public void Reset()
    {
        // Recreate() força destruição+recriação dos FBOs/texturas,
        // pois Resize(w,h) é no-op quando as dimensões não mudaram
        _fbPool.Recreate();
    }

    /// <summary>
    /// Validates HDR FBO is complete and usable
    /// </summary>
    public bool ValidateHdrFbo(GL gl)
    {
        if (_fbPool.HdrFbo == 0) return false;
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, _fbPool.HdrFbo);
        var status = gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        return status == GLEnum.FramebufferComplete;
    }

    private bool CheckFramebufferComplete(uint fbo)
    {
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
        var status = _gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        return status == GLEnum.FramebufferComplete;
    }
}