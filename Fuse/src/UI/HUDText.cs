using System.Numerics;

namespace Fuse.UI;

public class HUDText : HUDElement
{
    private string _text = "";
    private HUDLayout _layout;
    private float _scale;
    private Vector4 _color;
    private Vector2? _customScreenPos;
    private float _outlineWidth;
    private Vector4 _outlineColor;

    public HUDText(string text, HUDLayout layout, float scale = 1.0f, Vector4 color = default)
    {
        _text = text;
        _layout = layout;
        _scale = scale;
        _color = color == default ? Vector4.One : color;
    }

    public string Text { get => _text; set => _text = value; }
    public Vector4 Color { get => _color; set => _color = value; }
    public float Scale { get => _scale; set => _scale = value; }
    public HUDLayout Layout { get => _layout; set => _layout = value; }
    public float OutlineWidth { get => _outlineWidth; set => _outlineWidth = value; }
    public Vector4 OutlineColor { get => _outlineColor; set => _outlineColor = value; }

    /// <summary>Define posição customizada em pixels (screen space). Null = usa Layout anchor.</summary
    public void SetScreenPosition(Vector2? screenPos) => _customScreenPos = screenPos;

    public override void Draw(Renderer.UIRenderer ui, int screenW, int screenH)
    {
        //if (string.IsNullOrEmpty(_text)) return;
        //Vector2 size = Vector2.Zero;
        //Vector2 pos = HUDHelper.ResolvePosition(_layout, screenW, screenH, size);
        //ui.DrawText(pos.X, pos.Y, _text.AsSpan(), _color, _scale);
        if (string.IsNullOrEmpty(_text)) return;

        Vector2 pos;
        if (_customScreenPos.HasValue)
        {
            pos = _customScreenPos.Value;
        }
        else
        {
            Vector2 size = Vector2.Zero;
            pos = HUDHelper.ResolvePosition(_layout, screenW, screenH, size);
        }

        if (_outlineWidth > 0f)
            ui.DrawTextOutlined(pos.X, pos.Y, _text.AsSpan(), _color, _outlineWidth, _outlineColor, _scale);
        else
            ui.DrawText(pos.X, pos.Y, _text.AsSpan(), _color, _scale);
    }
}
