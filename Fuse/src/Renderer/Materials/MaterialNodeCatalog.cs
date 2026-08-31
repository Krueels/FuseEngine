namespace Fuse.Renderer.Materials;

public enum MaterialValueType
{
    Float,
    Vector2,
    Vector3
}

public readonly record struct MaterialSocketDefinition(string Name, MaterialValueType Type);

public sealed record MaterialNodeDefinition(
    string Type,
    string DisplayName,
    MaterialSocketDefinition[] Inputs,
    MaterialSocketDefinition[] Outputs);

public static class MaterialNodeCatalog
{
    public static readonly MaterialNodeDefinition[] Definitions =
    [
        new("Texture2D", "Texture", [new("UV", MaterialValueType.Vector2)],
            [new("Color", MaterialValueType.Vector3), new("Alpha", MaterialValueType.Float)]),
        new("ScalarTexture", "Scalar Texture", [new("UV", MaterialValueType.Vector2)],
            [new("Value", MaterialValueType.Float)]),
        new("PackedMetallicRoughness", "Metallic/Roughness", [new("UV", MaterialValueType.Vector2)],
            [new("Metallic", MaterialValueType.Float), new("Roughness", MaterialValueType.Float)]),
        new("Color", "Color", [], [new("Color", MaterialValueType.Vector3)]),
        new("Float", "Float", [], [new("Value", MaterialValueType.Float)]),
        new("Vector3", "Vector", [], [new("Vector", MaterialValueType.Vector3)]),
        new("UV", "Texture Coordinate", [], [new("UV", MaterialValueType.Vector2)]),
        new("Multiply", "Multiply", [new("A", MaterialValueType.Vector3), new("B", MaterialValueType.Vector3)],
            [new("Result", MaterialValueType.Vector3)]),
        new("Add", "Add", [new("A", MaterialValueType.Vector3), new("B", MaterialValueType.Vector3)],
            [new("Result", MaterialValueType.Vector3)]),
        new("Lerp", "Mix", [new("A", MaterialValueType.Vector3), new("B", MaterialValueType.Vector3), new("Factor", MaterialValueType.Float)],
            [new("Result", MaterialValueType.Vector3)]),
        new("NormalMap", "Normal Map", [new("Color", MaterialValueType.Vector3), new("Strength", MaterialValueType.Float)],
            [new("Normal", MaterialValueType.Vector3)]),
        new("Reroute", "Reroute", [new("Input", MaterialValueType.Vector3)],
            [new("Output", MaterialValueType.Vector3)]),
        new("Frame", "Frame / Group", [], []),
        new("Comment", "Comment", [], []),
        new("PBROutput", "Material Output",
            [
                new("BaseColor", MaterialValueType.Vector3),
                new("Normal", MaterialValueType.Vector3),
                new("Roughness", MaterialValueType.Float),
                new("Metallic", MaterialValueType.Float),
                new("Emission", MaterialValueType.Vector3),
                new("Alpha", MaterialValueType.Float),
                new("AO", MaterialValueType.Float)
            ], [])
    ];

    public static MaterialNodeDefinition? Find(string type) =>
        Definitions.FirstOrDefault(definition => definition.Type.Equals(type, StringComparison.OrdinalIgnoreCase));

    public static MaterialGraphNode CreateNode(string type, System.Numerics.Vector2 position)
    {
        MaterialNodeDefinition definition = Find(type)
            ?? throw new ArgumentException($"Unknown material node type '{type}'.", nameof(type));
        var node = new MaterialGraphNode
        {
            Type = definition.Type,
            Name = definition.DisplayName,
            Position = position
        };

        switch (definition.Type)
        {
            case "Texture2D":
                node.Properties["path"] = "";
                node.Properties["color_space"] = "sRGB";
                break;
            case "ScalarTexture":
                node.Properties["path"] = "";
                node.Properties["color_space"] = "linear";
                break;
            case "PackedMetallicRoughness":
                node.Properties["path"] = "";
                node.Properties["color_space"] = "linear";
                break;
            case "Color":
                node.Properties["value"] = MaterialAsset.Vec3ToJson(System.Numerics.Vector3.One);
                break;
            case "Float":
                node.Properties["value"] = 0.5f;
                break;
            case "Vector3":
                node.Properties["value"] = MaterialAsset.Vec3ToJson(System.Numerics.Vector3.Zero);
                break;
            case "NormalMap":
                node.Properties["strength"] = 1.0f;
                break;
            case "Reroute":
                node.Properties["value_type"] = nameof(MaterialValueType.Vector3);
                break;
            case "Frame":
                node.Properties["comment"] = "Group";
                node.Properties["width"] = 360.0f;
                node.Properties["height"] = 220.0f;
                node.Properties["color"] = MaterialAsset.Vec3ToJson(new System.Numerics.Vector3(0.18f, 0.32f, 0.48f));
                break;
            case "Comment":
                node.Properties["comment"] = "Comment";
                node.Properties["width"] = 260.0f;
                node.Properties["height"] = 84.0f;
                break;
            case "PBROutput":
                node.Properties["base_color"] = MaterialAsset.Vec3ToJson(System.Numerics.Vector3.One);
                node.Properties["roughness"] = 0.5f;
                node.Properties["metallic"] = 0.0f;
                node.Properties["emission"] = MaterialAsset.Vec3ToJson(System.Numerics.Vector3.Zero);
                node.Properties["alpha"] = 1.0f;
                node.Properties["ao"] = 1.0f;
                break;
        }
        return node;
    }
}
