using System;
using Silk.NET.OpenGL;
using Fuse.Core;
using Fuse.Renderer.PostProcess;
using Fuse.AssetManagement;

namespace Fuse.Renderer;

public sealed class DeathScreen : IDisposable
{
    private readonly GL _gl;
    private readonly Shader _shader;
    private readonly FullscreenQuad _quad;
    private float _deathTimer;
    private float _currentFade;

    private int _uScene;
    private int _uResolution;
    private int _uTime;
    private int _uDeathFade;
    private int _uDeathTimer;

    public DeathScreen(GL gl, AssetManager assetManager)
    {
        _gl = gl;
        _shader = Shader.FromFile(gl, Bible.Shader(Bible.DeathScreenVert), Bible.Shader(Bible.DeathScreenGlsl));
        _quad = new FullscreenQuad(gl);

        _uScene = _shader.GetUniformLoc("uScene");
        _uResolution = _shader.GetUniformLoc("uResolution");
        _uTime = _shader.GetUniformLoc("uTime");
        _uDeathFade = _shader.GetUniformLoc("uDeathFade");
        _uDeathTimer = _shader.GetUniformLoc("uDeathTimer");
    }

    public bool Active { get; private set; }

    public void Trigger()
    {
        Active = true;
        _deathTimer = 0f;
        _currentFade = 0f;
    }

    public void Reset()
    {
        Active = false;
        _deathTimer = 0f;
        _currentFade = 0f;
    }

    public void Update(float dt, bool isDead)
    {
        if (isDead)
        {
            _deathTimer += dt;
            _currentFade = float.Min(_currentFade + dt * 2.0f, 1.0f);
            Active = true;
        }
        else if (Active)
        {
            _currentFade -= dt * 3.0f;
            if (_currentFade <= 0f)
            {
                _currentFade = 0f;
                Active = false;
            }
        }
    }

    public void Render(uint sceneColorTexture, int width, int height, float totalTime)
    {
        if (_currentFade <= 0f) return;

        // Salva estado do GL para restaurar depois
        bool depthTestWasEnabled = _gl.IsEnabled(EnableCap.DepthTest);
        bool blendWasEnabled = _gl.IsEnabled(EnableCap.Blend);

        _gl.Disable(EnableCap.DepthTest);
        _gl.Disable(EnableCap.Blend);

        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        _gl.Viewport(0, 0, (uint)width, (uint)height);

        _shader.Use();
        _gl.Uniform1(_uScene, 0);
        _gl.Uniform2(_uResolution, (float)width, (float)height);
        _gl.Uniform1(_uTime, totalTime);
        _gl.Uniform1(_uDeathFade, _currentFade);
        _gl.Uniform1(_uDeathTimer, _deathTimer);

        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, sceneColorTexture);

        _quad.Draw();

        // Restaura estado do GL
        if (depthTestWasEnabled) _gl.Enable(EnableCap.DepthTest);
        if (blendWasEnabled) _gl.Enable(EnableCap.Blend);
    }

    public void Dispose()
    {
        _quad.Dispose();
        _shader.Dispose();
    }
}