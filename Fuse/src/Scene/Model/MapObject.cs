namespace Fuse.Scene.Model;

public class MapObject
{
    public string Id { get; set; } = "";
    public bool Visible { get; set; } = true;
    public string? ParentId { get; set; }

    public string? Mesh { get; set; }
    public string? Model { get; set; }
    /// <summary>Optional CPU geometry graph asset, stored relative to res/.</summary>
    public string? GeometryGraphPath { get; set; }
    public System.Numerics.Vector3 ModelScale { get; set; } = System.Numerics.Vector3.One;
    public System.Numerics.Vector2 UvScale { get; set; } = System.Numerics.Vector2.One;
    public System.Numerics.Vector2 UvOffset { get; set; } = System.Numerics.Vector2.Zero;
    public float UvRotation { get; set; } = 0f;

    /// <summary>Material asset applied to the whole object. Stored relative to res/.</summary>
    public string? MaterialPath { get; set; }
    /// <summary>Optional Blender-style material slots. Brush faces and model parts reference these by index.</summary>
    public List<string> MaterialSlots { get; set; } = new();
    /// <summary>Legacy texture path. Kept so version-1 maps remain fully compatible.</summary>
    public string? Texture { get; set; }
    public string? Interactable { get; set; }
    public List<Fuse.Behaviours.BehaviourData> Behaviours { get; set; } = new();
    public MapBody? Body { get; set; }

    // Light properties
    public string? LightType { get; set; }
    public System.Numerics.Vector3 LightColor { get; set; } = System.Numerics.Vector3.One;
    public float LightIntensity { get; set; } = 1.0f;
    public float LightRadius { get; set; } = 10.0f;
    public float LightInnerCone { get; set; } = float.DegreesToRadians(20);
    public float LightOuterCone { get; set; } = float.DegreesToRadians(30);
    public bool LightCastShadows { get; set; } = false;
    public float LightShadowBias { get; set; } = 0.00100f;
    public bool LightDynamic { get; set; } = false;

    public bool IsLight => !string.IsNullOrEmpty(LightType);
    public bool IsModel => !string.IsNullOrEmpty(Model);

    public bool IsGloballyVisible(MapDocument doc)
    {
        MapObject? current = this;
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        while (current != null)
        {
            if (!current.Visible)
                return false;
            if (!visited.Add(current.Id))
                return false;
            if (string.IsNullOrEmpty(current.ParentId))
                return true;

            string parentId = current.ParentId;
            current = doc.Objects.FirstOrDefault(candidate =>
                candidate.Id.Equals(parentId, StringComparison.OrdinalIgnoreCase));
        }

        return true;
    }
}
