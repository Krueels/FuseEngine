using System.Numerics;

namespace Fuse.UI;

public class HUD
{
    private readonly List<HUDElement> _elements = [];

    public HUDImage AddImage(Renderer.Texture tex, HUDLayout layout, Vector2 size)
    {
        var el = new HUDImage(tex, layout, size);
        _elements.Add(el);
        return el;
    }

    public HUDImage AddImage(Renderer.Texture tex, HUDAnchor anchor, Vector2 offset, Vector2 size)
    {
        return AddImage(tex, HUDHelper.MakeLayout(anchor, offset), size);
    }

    public HUDText AddText(string text, HUDLayout layout, float scale = 1.0f, Vector4 color = default)
    {
        var el = new HUDText(text, layout, scale, color);
        _elements.Add(el);
        return el;
    }

    public HUDText AddText(string text, HUDAnchor anchor, Vector2 offset, float scale = 1.0f, Vector4 color = default)
    {
        return AddText(text, HUDHelper.MakeLayout(anchor, offset), scale, color);
    }

    public HUDText AddTextOutlined(string text, HUDLayout layout, float scale, Vector4 color, float outlineWidth, Vector4 outlineColor)
    {
        var el = new HUDText(text, layout, scale, color)
        {
            OutlineWidth = outlineWidth,
            OutlineColor = outlineColor
        };
        _elements.Add(el);
        return el;
    }

    public HUDText AddTextOutlined(string text, HUDAnchor anchor, Vector2 offset, float scale, Vector4 color, float outlineWidth, Vector4 outlineColor)
    {
        return AddTextOutlined(text, HUDHelper.MakeLayout(anchor, offset), scale, color, outlineWidth, outlineColor);
    }

    public HUDPanel AddPanel(HUDLayout layout, Vector2 size, Vector4 color)
    {
        var el = new HUDPanel(layout, size, color);
        _elements.Add(el);
        return el;
    }

    public void Remove(HUDElement element)
    {
        _elements.Remove(element);
    }

    public void Clear()
    {
        _elements.Clear();
    }

    public void Update(int screenW, int screenH)
    {
        foreach (var el in _elements)
            el.Update(screenW, screenH);
    }

    public void Draw(Renderer.UIRenderer ui, int screenW, int screenH)
    {
        foreach (var el in _elements)
            el.Draw(ui, screenW, screenH);
    }
}
