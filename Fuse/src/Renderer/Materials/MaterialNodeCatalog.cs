using System.Text.Json.Nodes;

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
        new("Vector2", "Vector 2", [], [new("Vector", MaterialValueType.Vector2)]),
        new("Vector3", "Vector", [], [new("Vector", MaterialValueType.Vector3)]),
        new("UV", "Texture Coordinate", [], [new("UV", MaterialValueType.Vector2)]),
        new("WorldPosition", "World Position", [], [new("Position", MaterialValueType.Vector3)]),
        new("WorldNormal", "World Normal", [], [new("Normal", MaterialValueType.Vector3)]),
        new("Swizzle", "Split / Swizzle", [new("Vector", MaterialValueType.Vector3)],
            [
                new("X", MaterialValueType.Float), new("Y", MaterialValueType.Float),
                new("Z", MaterialValueType.Float), new("XY", MaterialValueType.Vector2),
                new("XZ", MaterialValueType.Vector2), new("YZ", MaterialValueType.Vector2),
                new("Vector", MaterialValueType.Vector3)
            ]),
        new("Mapping", "Mapping", [new("Coordinates", MaterialValueType.Vector2)],
            [new("UV", MaterialValueType.Vector2)]),
        new("Multiply", "Multiply", [new("A", MaterialValueType.Vector3), new("B", MaterialValueType.Vector3)],
            [new("Result", MaterialValueType.Vector3)]),
        new("Add", "Add", [new("A", MaterialValueType.Vector3), new("B", MaterialValueType.Vector3)],
            [new("Result", MaterialValueType.Vector3)]),
        new("Subtract", "Subtract", [new("A", MaterialValueType.Vector3), new("B", MaterialValueType.Vector3)],
            [new("Result", MaterialValueType.Vector3)]),
        new("Divide", "Divide", [new("A", MaterialValueType.Vector3), new("B", MaterialValueType.Vector3)],
            [new("Result", MaterialValueType.Vector3)]),
        new("Min", "Minimum", [new("A", MaterialValueType.Vector3), new("B", MaterialValueType.Vector3)],
            [new("Result", MaterialValueType.Vector3)]),
        new("Max", "Maximum", [new("A", MaterialValueType.Vector3), new("B", MaterialValueType.Vector3)],
            [new("Result", MaterialValueType.Vector3)]),
        new("Power", "Power", [new("A", MaterialValueType.Vector3), new("B", MaterialValueType.Vector3)],
            [new("Result", MaterialValueType.Vector3)]),
        new("Math", "Math", [new("A", MaterialValueType.Vector3), new("B", MaterialValueType.Vector3)],
            [new("Result", MaterialValueType.Vector3)]),
        new("Abs", "Absolute", [new("Input", MaterialValueType.Vector3)],
            [new("Result", MaterialValueType.Vector3)]),
        new("Normalize", "Normalize", [new("Input", MaterialValueType.Vector3)],
            [new("Result", MaterialValueType.Vector3)]),
        new("Length", "Length", [new("Input", MaterialValueType.Vector3)],
            [new("Value", MaterialValueType.Float)]),
        new("Dot", "Dot Product", [new("A", MaterialValueType.Vector3), new("B", MaterialValueType.Vector3)],
            [new("Value", MaterialValueType.Float)]),
        new("OneMinus", "One Minus", [new("Input", MaterialValueType.Vector3)],
            [new("Result", MaterialValueType.Vector3)]),
        new("Clamp", "Clamp", [new("Value", MaterialValueType.Vector3), new("Min", MaterialValueType.Vector3), new("Max", MaterialValueType.Vector3)],
            [new("Result", MaterialValueType.Vector3)]),
        new("Saturate", "Saturate", [new("Input", MaterialValueType.Vector3)],
            [new("Result", MaterialValueType.Vector3)]),
        new("Smoothstep", "Smoothstep", [new("Value", MaterialValueType.Vector3), new("Edge0", MaterialValueType.Vector3), new("Edge1", MaterialValueType.Vector3)],
            [new("Value", MaterialValueType.Float)]),
        new("Remap", "Remap", [
                new("Value", MaterialValueType.Vector3), new("InMin", MaterialValueType.Vector3),
                new("InMax", MaterialValueType.Vector3), new("OutMin", MaterialValueType.Vector3),
                new("OutMax", MaterialValueType.Vector3)
            ], [new("Value", MaterialValueType.Float)]),
        new("TerrainHeight", "Terrain Height", [], [new("Height", MaterialValueType.Float)]),
        new("TerrainSlope", "Terrain Slope", [], [new("Slope", MaterialValueType.Float)]),
        new("Noise2D", "Noise 2D", [new("Coordinates", MaterialValueType.Vector2)],
            [new("Value", MaterialValueType.Float)]),
        new("FBMNoise", "FBM Noise", [new("Coordinates", MaterialValueType.Vector2)],
            [new("Value", MaterialValueType.Float)]),
        new("DomainWarp", "Domain Warp", [new("Coordinates", MaterialValueType.Vector2)],
            [new("Coordinates", MaterialValueType.Vector2)]),
        new("TriplanarTexture", "Triplanar Texture", [
                new("Position", MaterialValueType.Vector3), new("Normal", MaterialValueType.Vector3)
            ], [new("Color", MaterialValueType.Vector3), new("Alpha", MaterialValueType.Float)]),
        new("TriplanarNormal", "Triplanar Normal", [
                new("Position", MaterialValueType.Vector3), new("Normal", MaterialValueType.Vector3)
            ], [new("Normal", MaterialValueType.Vector3)]),
        new("Texture2DArray", "Texture 2D Array", [
                new("UV", MaterialValueType.Vector2), new("Layer", MaterialValueType.Float)
            ], [new("Color", MaterialValueType.Vector3), new("Alpha", MaterialValueType.Float)]),
        new("TerrainLayer", "Terrain Layer", [
                new("Position", MaterialValueType.Vector3), new("Normal", MaterialValueType.Vector3),
                new("Layer", MaterialValueType.Float)
            ], [
                new("Color", MaterialValueType.Vector3), new("Normal", MaterialValueType.Vector3),
                new("Roughness", MaterialValueType.Float), new("AO", MaterialValueType.Float),
                new("Height", MaterialValueType.Float)
            ]),
        new("NormalBlend", "Normal Blend", [
                new("A", MaterialValueType.Vector3), new("B", MaterialValueType.Vector3),
                new("Factor", MaterialValueType.Float)
            ], [new("Normal", MaterialValueType.Vector3)]),
        new("HeightBlend", "Height Blend", [
                new("A", MaterialValueType.Vector3), new("B", MaterialValueType.Vector3),
                new("HeightA", MaterialValueType.Float), new("HeightB", MaterialValueType.Float),
                new("Weight", MaterialValueType.Float)
            ], [new("Result", MaterialValueType.Vector3)]),
        new("TerrainLayerBlend", "Terrain Layer Blend", [
                new("Layer0Color", MaterialValueType.Vector3), new("Layer0Normal", MaterialValueType.Vector3),
                new("Layer0Roughness", MaterialValueType.Float), new("Layer0AO", MaterialValueType.Float),
                new("Layer0Height", MaterialValueType.Float), new("Layer0Weight", MaterialValueType.Float),
                new("Layer1Color", MaterialValueType.Vector3), new("Layer1Normal", MaterialValueType.Vector3),
                new("Layer1Roughness", MaterialValueType.Float), new("Layer1AO", MaterialValueType.Float),
                new("Layer1Height", MaterialValueType.Float), new("Layer1Weight", MaterialValueType.Float),
                new("Layer2Color", MaterialValueType.Vector3), new("Layer2Normal", MaterialValueType.Vector3),
                new("Layer2Roughness", MaterialValueType.Float), new("Layer2AO", MaterialValueType.Float),
                new("Layer2Height", MaterialValueType.Float), new("Layer2Weight", MaterialValueType.Float),
                new("Layer3Color", MaterialValueType.Vector3), new("Layer3Normal", MaterialValueType.Vector3),
                new("Layer3Roughness", MaterialValueType.Float), new("Layer3AO", MaterialValueType.Float),
                new("Layer3Height", MaterialValueType.Float), new("Layer3Weight", MaterialValueType.Float)
            ], [
                new("Color", MaterialValueType.Vector3), new("Normal", MaterialValueType.Vector3),
                new("Roughness", MaterialValueType.Float), new("AO", MaterialValueType.Float),
                new("Height", MaterialValueType.Float)
            ]),
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
            case "Vector2":
                node.Properties["value"] = MaterialAsset.Vec2ToJson(System.Numerics.Vector2.Zero);
                break;
            case "Vector3":
                node.Properties["value"] = MaterialAsset.Vec3ToJson(System.Numerics.Vector3.Zero);
                break;
            case "Swizzle":
                node.Properties["mode"] = "XZ";
                break;
            case "Mapping":
                node.Properties["scale"] = MaterialAsset.Vec2ToJson(System.Numerics.Vector2.One);
                node.Properties["offset"] = MaterialAsset.Vec2ToJson(System.Numerics.Vector2.Zero);
                node.Properties["rotation"] = 0.0f;
                break;
            case "Math":
                node.Properties["operation"] = "Multiply";
                break;
            case "Noise2D":
                node.Properties["scale"] = 0.01f;
                node.Properties["seed"] = 0.0f;
                break;
            case "FBMNoise":
                node.Properties["scale"] = 0.01f;
                node.Properties["octaves"] = 5;
                node.Properties["lacunarity"] = 2.0f;
                node.Properties["gain"] = 0.5f;
                node.Properties["seed"] = 0.0f;
                break;
            case "DomainWarp":
                node.Properties["scale"] = 0.01f;
                node.Properties["strength"] = 0.25f;
                node.Properties["octaves"] = 3;
                node.Properties["seed"] = 0.0f;
                break;
            case "TriplanarTexture":
                node.Properties["path"] = "";
                node.Properties["color_space"] = "sRGB";
                node.Properties["tiling"] = 0.01f;
                node.Properties["sharpness"] = 4.0f;
                break;
            case "TriplanarNormal":
                node.Properties["path"] = "";
                node.Properties["color_space"] = "data";
                node.Properties["tiling"] = 0.01f;
                node.Properties["sharpness"] = 4.0f;
                node.Properties["strength"] = 1.0f;
                break;
            case "Texture2DArray":
                node.Properties["paths"] = new JsonArray();
                node.Properties["color_space"] = "sRGB";
                break;
            case "TerrainLayer":
                node.Properties["albedo_paths"] = new JsonArray();
                node.Properties["normal_paths"] = new JsonArray();
                node.Properties["orm_paths"] = new JsonArray();
                node.Properties["height_paths"] = new JsonArray();
                node.Properties["layer"] = 0.0f;
                node.Properties["tiling"] = 0.01f;
                node.Properties["sharpness"] = 4.0f;
                break;
            case "TerrainLayerBlend":
                for (int layer = 0; layer < 4; layer++)
                {
                    node.Properties[$"layer{layer}_weight"] = layer == 0 ? 1.0f : 0.0f;
                    node.Properties[$"layer{layer}_height"] = 0.5f;
                    node.Properties[$"layer{layer}_roughness"] = 0.5f;
                    node.Properties[$"layer{layer}_ao"] = 1.0f;
                    node.Properties[$"layer{layer}_color"] = MaterialAsset.Vec3ToJson(System.Numerics.Vector3.One);
                    node.Properties[$"layer{layer}_normal"] = MaterialAsset.Vec3ToJson(new System.Numerics.Vector3(0, 0, 1));
                }
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
