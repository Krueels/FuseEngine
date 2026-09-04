using System.Numerics;
using Fuse.Renderer;

namespace Fuse.UI;

public sealed class HUDWireBox : HUDElement
{
    private Vector2 _min;
    private Vector2 _max;

    public bool Visible { get; private set; }
    public Vector4 Color { get; set; } = new(1.0f, 0.55f, 0.05f, 0.95f);
    public float Thickness { get; set; } = 2.0f;
    public float Padding { get; set; } = 4.0f;

    public void SetScreenRect(Vector2 min, Vector2 max)
    {
        if (!IsFinite(min) || !IsFinite(max))
        {
            Hide();
            return;
        }

        Vector2 padding = new(MathF.Max(0.0f, Padding));
        _min = Vector2.Min(min, max) - padding;
        _max = Vector2.Max(min, max) + padding;
        Visible = true;
    }

    public void Hide() => Visible = false;

    public override void Draw(UIRenderer ui, int screenW, int screenH)
    {
        if (!Visible || screenW <= 0 || screenH <= 0)
            return;

        Vector2 screenMax = new(screenW, screenH);
        Vector2 min = Vector2.Clamp(_min, Vector2.Zero, screenMax);
        Vector2 max = Vector2.Clamp(_max, Vector2.Zero, screenMax);
        if (max.X <= min.X || max.Y <= min.Y)
            return;

        ui.DrawLine(new Vector2(min.X, min.Y), new Vector2(max.X, min.Y), Color, Thickness);
        ui.DrawLine(new Vector2(max.X, min.Y), new Vector2(max.X, max.Y), Color, Thickness);
        ui.DrawLine(new Vector2(max.X, max.Y), new Vector2(min.X, max.Y), Color, Thickness);
        ui.DrawLine(new Vector2(min.X, max.Y), new Vector2(min.X, min.Y), Color, Thickness);
    }

    private static bool IsFinite(Vector2 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y);
}
