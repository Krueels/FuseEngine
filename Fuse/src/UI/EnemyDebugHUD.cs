using System.Numerics;
using Fuse.Enemy;
using Fuse.Renderer;

namespace Fuse.UI;

public sealed class EnemyDebugHUD
{
    private readonly HUD _hud = new();
    private readonly List<HUDText> _lines = [];
    private Fuse.Enemy.Enemy? _target;
    private bool _visible;
    private Debug.DebugDrawer? _debugDrawer;

    public void SetDebugDrawer(Debug.DebugDrawer debugDrawer) => _debugDrawer = debugDrawer;

    public EnemyDebugHUD()
    {
        // Criar linhas do HUD (vazias inicialmente)
        for (int i = 0; i < 6; i++)
        {
            var text = _hud.AddText("", HUDAnchor.TopLeft, new Vector2(0, 0), 2.0f, Vector4.One);
            text.SetScreenPosition(null); // será atualizado no Update
            _lines.Add(text);
        }
    }

    public void SetTarget(Fuse.Enemy.Enemy? enemy)
    {
        _target = enemy;
        _visible = enemy != null;

        if (!_visible)
        {
            foreach (var line in _lines)
                line.Text = "";
        }
    }

    public void Show() => _visible = _target != null;
    public void Hide() => _visible = false;
    public bool IsVisible => _visible && _target != null;

    public void Update(Camera camera, int screenW, int screenH)
    {
        bool debugDrawerOn = _debugDrawer == null || _debugDrawer.Enabled;
        if (!debugDrawerOn)
        {
            if (_visible)
            {
                _visible = false;
                foreach (var line in _lines)
                    line.Text = "";
            }
            return;
        }

        if (!_visible || _target == null) return;

        var e = _target;
        var worldPos = e.Entity.Transform.Position + new Vector3(0, 0.2f, 0); // acima da cabeça
        var screenPos = camera.WorldToScreenPoint(worldPos, screenW, screenH);

        if (screenPos.X < 0 || screenPos.Y < 0) return; // atrás da câmera

        // Offset para não ficar em cima do inimigo
        float lineHeight = 15f;
        var basePos = screenPos + new Vector2(12, -lineHeight * _lines.Count / 2f);

        var anim = e.Entity.Animator;
        var clip = anim?.CurrentClip;
        float animTime = (float)(anim?.TimeSeconds ?? 0.0);
        float animDur = (float)(clip?.DurationSeconds ?? 0.0);
        bool animLoop = clip?.Loop ?? false;

        _lines[0].Text = $"ID: {e.Id}";
        _lines[1].Text = $"Vida: {e.Health:F1} / {e.MaxHealth:F1}";
        _lines[2].Text = $"Anim: {clip?.Name ?? "none"}";
        _lines[3].Text = $"Tempo: {animTime:F2}s / {animDur:F2}s";
        _lines[4].Text = $"Loop: {(animLoop ? "ON" : "OFF")}";
        _lines[5].Text = $"BodyID: {e.Body.Native}";

        for (int i = 0; i < _lines.Count; i++)
        {
            _lines[i].SetScreenPosition(basePos + new Vector2(0, i * lineHeight));
            _lines[i].Color = i == 1 && e.Health < e.MaxHealth * 0.3f ? new Vector4(1, 0.3f, 0.3f, 1) : Vector4.One;
        }
    }

    public void Draw(Renderer.UIRenderer ui, int w, int h) => _hud.Draw(ui, w, h);
}