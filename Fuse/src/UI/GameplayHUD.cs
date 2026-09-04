using System.Numerics;
using Silk.NET.OpenGL;
using Fuse.Core;
using Fuse.Renderer;
using Fuse.Player;
using Fuse.AssetManagement;
using Fuse.Enemy;
using Fuse.Input;
using Fuse.Math;
using Fuse.Physics;

namespace Fuse.UI;

public class GameplayHUD
{
    private readonly HUD _hud = new();
    private HUDText _fpsText = null!;
    private HUDImage _crosshairNode = null!;
    private HUDText _weaponDebugText = null!;
    private HUDText _ammoText = null!;
    private HUDText _reserveAmmoText = null!;
    private HUDText _enemyDebugSelectionMode = null!;
    private EnemyDebugHUD _enemyDebugHUD = null!;
    private HUDText? _playerHealth;
    private HUDWireBox _interactionBox = null!;

    private const int MaxNotifications = 5;
    private readonly HUDText[] _notifySlots = new HUDText[MaxNotifications];

    public HUDImage CrosshairNode => _crosshairNode;

    public void Init(AssetManager assets)
    {
        _fpsText = _hud.AddText("FPS: 0", HUDAnchor.TopLeft, new Vector2(20, 20), 2.0f, new Vector4(0, 1, 1, 1));

        // Draw the selection before the crosshair so the official HUD remains
        // readable when the crosshair is inside the projected bounds.
        _interactionBox = _hud.AddWireBox();
        
        var crosshairTexture = assets.GetTexture(Bible.Tex(Bible.Crosshair));
        _crosshairNode = _hud.AddImage(crosshairTexture, HUDAnchor.Center, Vector2.Zero, new Vector2(8, 8));

        _weaponDebugText = _hud.AddText("Weapon Debug", HUDAnchor.TopLeft, new Vector2(20, 50), 1.0f, new Vector4(0, 1, 0, 1));
        
        _ammoText = _hud.AddTextOutlined("0", HUDAnchor.BottomRight, new Vector2(-320, -125), 2.5f, new Vector4(1, 0.784f, 0, 1), 2f, new Vector4(0, 0, 0, 1));
        _reserveAmmoText = _hud.AddTextOutlined("0", HUDAnchor.BottomRight, new Vector2(-320, -100), 1.5f, Vector4.One, 2f, new Vector4(0, 0, 0, 1));

        _enemyDebugHUD = new EnemyDebugHUD();
        _enemyDebugSelectionMode = _hud.AddText("DEBUG: ENEMY SELECTION MODE ON", HUDAnchor.TopLeft, new Vector2(20, 80), 2.0f, new Vector4(0, 1, 0, 1));

        for (int i = 0; i < MaxNotifications; i++)
        {
            _notifySlots[i] = _hud.AddTextOutlined("",
                HUDAnchor.BottomLeft, new Vector2(0, -20),
                1.8f, Vector4.One, 2f, new Vector4(0,0,0,1));
        }

        _playerHealth = _hud.AddTextOutlined("0", HUDAnchor.BottomLeft, new Vector2(50, -100), 2f, Vector4.One, 2f, new Vector4(0, 0, 0, 1));
    }

    public void Update(
        WeaponSystem? weaponSystem,
        EnemySystem? enemySystem,
        bool enemySelectionMode,
        Player.Player player,
        Camera? camera,
        int width,
        int height,
        Interaction.PlayerInteraction? interaction,
        Renderer.Scene? scene,
        bool isPaused)
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
        {
            _enemyDebugHUD.Update(camera, width, height);
            if (enemySelectionMode)
            {
                _enemyDebugSelectionMode.Text = "DEBUG: ENEMY SELECTION MODE ON";
            }
            else
            {
                _enemyDebugSelectionMode.Text = "";
            }
        }

        if (_playerHealth != null)
        {
            _playerHealth.Text = $"Health: {player.Health}/{player.MaxHealth}";
        }

        UpdateInteractionBox(interaction, scene, camera, width, height, isPaused);
        _hud.Update(width, height);
    }

    public EnemyDebugHUD GetEnemyDebugHUD() => _enemyDebugHUD!;

    private void UpdateInteractionBox(
        Interaction.PlayerInteraction? interaction,
        Renderer.Scene? scene,
        Camera? camera,
        int width,
        int height,
        bool isPaused)
    {
        _interactionBox.Hide();

        if (isPaused || interaction == null || scene == null || camera == null ||
            width <= 0 || height <= 0 || !Input.Input.IsCursorDisabled() ||
            InputManager.CurrentContext == InputContext.UI)
        {
            return;
        }

        Renderer.Entity? target = interaction.LookingEntity;
        if (target == null || !target.Visible ||
            !TryGetSelectionBounds(scene, target, out AABB bounds))
        {
            return;
        }

        Span<Vector3> corners = stackalloc Vector3[8];
        bounds.GetCorners(corners);

        Vector2 min = new(float.PositiveInfinity);
        Vector2 max = new(float.NegativeInfinity);
        int projectedCorners = 0;

        foreach (Vector3 corner in corners)
        {
            Vector2 point = camera.WorldToScreenPoint(corner, width, height);
            if (!float.IsFinite(point.X) || !float.IsFinite(point.Y) ||
                point.X <= -9000.0f || point.Y <= -9000.0f)
            {
                continue;
            }

            min = Vector2.Min(min, point);
            max = Vector2.Max(max, point);
            projectedCorners++;
        }

        if (projectedCorners == 0 || !float.IsFinite(min.X) || !float.IsFinite(min.Y) ||
            !float.IsFinite(max.X) || !float.IsFinite(max.Y))
        {
            return;
        }

        _interactionBox.SetScreenRect(min, max);
    }

    private static bool TryGetSelectionBounds(
        Renderer.Scene scene,
        Renderer.Entity target,
        out AABB bounds)
    {
        bounds = new AABB();
        if (!target.Visible)
            return false;

        var entitiesById = new Dictionary<string, Renderer.Entity>(StringComparer.Ordinal);
        foreach (Renderer.Entity entity in scene.Entities)
        {
            if (!string.IsNullOrEmpty(entity.Id))
                entitiesById.TryAdd(entity.Id, entity);
        }

        bool hasVisualBounds = false;
        foreach (Renderer.Entity entity in scene.Entities)
        {
            if (!entity.Visible || !BelongsToSelection(entity, target, entitiesById))
                continue;

            AABB entityBounds = entity.GetWorldRenderBounds();
            if (!entityBounds.IsValid)
                continue;

            if (!hasVisualBounds)
            {
                bounds = entityBounds;
                hasVisualBounds = true;
            }
            else
            {
                bounds.Grow(entityBounds);
            }
        }

        if (hasVisualBounds)
            return true;

        // Some interactables are represented only by a physics body. Use its
        // conservative local extents as a last-resort screen-space box.
        if (target.Body == null || target.Body.Type == RigidBody.ShapeType.None)
            return false;

        Vector3 halfExtents = target.Body.BuoyancyHalfExtents;
        if (!IsFinite(halfExtents) || halfExtents.X <= 0.0f ||
            halfExtents.Y <= 0.0f || halfExtents.Z <= 0.0f)
        {
            return false;
        }

        bounds = new AABB(-halfExtents, halfExtents).Transformed(target.Transform.Matrix);
        return bounds.IsValid;
    }

    private static bool BelongsToSelection(
        Renderer.Entity entity,
        Renderer.Entity target,
        IReadOnlyDictionary<string, Renderer.Entity> entitiesById)
    {
        if (ReferenceEquals(entity, target))
            return true;

        if (string.IsNullOrEmpty(target.Id))
            return false;

        string parentId = entity.ParentId;
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (!string.IsNullOrEmpty(parentId) && visited.Add(parentId))
        {
            if (!entitiesById.TryGetValue(parentId, out Renderer.Entity? parent))
                return false;

            if (ReferenceEquals(parent, target) || parent.Id == target.Id)
                return true;

            parentId = parent.ParentId;
        }

        return false;
    }

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private void DrawNotifications()
    {
        var active = GameNotify.GetActive();

        for (int i = 0; i < MaxNotifications; i++)
        {
            if (i < active.Length)
            {
                var entry = active[i];
                float age = GameNotify._elapsed - entry.CreatedAt;
                float alpha = age > GameNotify.FadeStart
                    ? 1f - (age - GameNotify.FadeStart) / (GameNotify.Duration - GameNotify.FadeStart)
                    : 1f;

                Vector4 color = GameNotify.GetColor(entry.Level) with { W = alpha };
                Vector4 outline = new(0, 0, 0, alpha);

                string prefix = entry.Level switch
                {
                    NotifyLevel.Warn => "[!] ",
                    NotifyLevel.Error => "[X] ",
                    _ => "",
                };

                _notifySlots[i].Text = $"{prefix}{entry.Message}";
                _notifySlots[i].Color = color;
                _notifySlots[i].OutlineColor = outline;
            }
            else
            {
                _notifySlots[i].Text = "";
            }
        }
    }

    public void Draw(GL gl, UIRenderer ui, int width, int height, bool isPaused, Interaction.PlayerInteraction? interaction)
    {
        gl.Disable(EnableCap.DepthTest);
        gl.Disable(EnableCap.CullFace);
        gl.Enable(EnableCap.Blend);
        gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);

        _hud.Draw(ui, width, height);

        _enemyDebugHUD?.Draw(ui, width, height);

        if (isPaused)
        {
            Vector2 center = ui.Center;
            ui.DrawText(center.X - 60.0f, center.Y - 20.0f, "PAUSED".AsSpan(),
                new Vector4(1, 1, 0, 1), 3.0f);
        }

        DrawNotifications();

        gl.Disable(EnableCap.Blend);
        gl.Enable(EnableCap.CullFace);
        gl.Enable(EnableCap.DepthTest);
    }
}
