using System.Numerics;
using Silk.NET.OpenGL;
using Fuse.Core;
using Fuse.Renderer;
using Fuse.Player;
using Fuse.AssetManagement;
using Fuse.Enemy;

namespace Fuse.UI;

public class GameplayHUD
{
    private readonly HUD _hud = new();
    private HUDText _fpsText = null!;
    private HUDImage _crosshairNode = null!;
    private HUDText _weaponDebugText = null!;
    private HUDText _ammoText = null!;
    private HUDText _reserveAmmoText = null!;
    private EnemyDebugHUD _enemyDebugHUD = null!;

    public HUDImage CrosshairNode => _crosshairNode;

    public void Init(AssetManager assets)
    {
        _fpsText = _hud.AddText("FPS: 0", HUDAnchor.TopLeft, new Vector2(20, 20), 2.0f, new Vector4(0, 1, 1, 1));
        
        var crosshairTexture = assets.GetTexture(Bible.Tex(Bible.Crosshair));
        _crosshairNode = _hud.AddImage(crosshairTexture, HUDAnchor.Center, Vector2.Zero, new Vector2(8, 8));

        _weaponDebugText = _hud.AddText("Weapon Debug", HUDAnchor.TopLeft, new Vector2(20, 50), 1.0f, new Vector4(0, 1, 0, 1));
        
        _ammoText = _hud.AddText("0", HUDAnchor.BottomRight, new Vector2(-320, -120), 2.5f, new Vector4(1, 1, 1, 1));
        _reserveAmmoText = _hud.AddText("0", HUDAnchor.BottomRight, new Vector2(-320, -100), 1.5f, new Vector4(0.7f, 0.7f, 0.7f, 1));

        _enemyDebugHUD = new EnemyDebugHUD();
    }

    public void Update(WeaponSystem? weaponSystem, EnemySystem? enemySystem, Camera? camera, int width, int height)
    {
        if (_fpsText != null)
            _fpsText.Text = $"FPS: {Engine.FPS}";

        // Weapon debug info
        if (_weaponDebugText != null && weaponSystem?.CurrentWeapon != null)
        {
            var w = weaponSystem.CurrentWeapon;
            _weaponDebugText.Text = $"Anim: {w.CurrentAnimState} | Time: {w.CurrentAnimTime:F2}s / {w.CurrentAnimDuration:F2}s";
        }
        else if (_weaponDebugText != null)
        {
            _weaponDebugText.Text = "No weapon equipped";
        }

        // Ammo counter
        if (_ammoText != null && weaponSystem?.CurrentWeapon != null)
        {
            _ammoText.Text = $"{weaponSystem.CurrentWeapon.CurrentAmmo}";
            _reserveAmmoText.Text = $"/{weaponSystem.CurrentWeapon.ReserveAmmo}";
        }
        else if (_ammoText != null)
        {
            _ammoText.Text = "";
            _reserveAmmoText.Text = "";
        }

        // Enemy debug HUD
        if (_enemyDebugHUD != null && camera != null)
            _enemyDebugHUD.Update(camera, width, height);

        _hud.Update(width, height);
    }

    public EnemyDebugHUD GetEnemyDebugHUD() => _enemyDebugHUD!;

    public void Draw(GL gl, UIRenderer ui, int width, int height, bool isPaused, Interaction.PlayerInteraction? interaction)
    {
        gl.Disable(EnableCap.DepthTest);
        gl.Disable(EnableCap.CullFace);
        gl.Enable(EnableCap.Blend);
        gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);

        if (!isPaused)
            interaction?.Update();

        _hud.Draw(ui, width, height);

        _enemyDebugHUD?.Draw(ui, width, height);

        if (isPaused)
        {
            Vector2 center = ui.Center;
            ui.DrawText(center.X - 60.0f, center.Y - 20.0f, "PAUSED".AsSpan(),
                new Vector4(1, 1, 0, 1), 3.0f);
        }

        gl.Disable(EnableCap.Blend);
        gl.Enable(EnableCap.CullFace);
        gl.Enable(EnableCap.DepthTest);
    }
}
