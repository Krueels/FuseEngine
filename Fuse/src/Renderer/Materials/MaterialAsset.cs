using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Fuse.Renderer.Materials;

public enum MaterialAlphaMode
{
    Opaque,
    Mask,
    Blend
}

public sealed class MaterialGraphNode
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Type { get; set; } = "Float";
    public string Name { get; set; } = "Node";
    public Vector2 Position { get; set; }
    public JsonObject Properties { get; set; } = new();

    public MaterialGraphNode Clone() => new()
    {
        Id = Id,
        Type = Type,
        Name = Name,
        Position = Position,
        Properties = JsonNode.Parse(Properties.ToJsonString())?.AsObject() ?? new JsonObject()
    };
}

public sealed class MaterialGraphLink
{
    public string FromNode { get; set; } = "";
    public string FromSocket { get; set; } = "";
    public string ToNode { get; set; } = "";
    public string ToSocket { get; set; } = "";

    public MaterialGraphLink Clone() => new()
    {
        FromNode = FromNode,
        FromSocket = FromSocket,
        ToNode = ToNode,
        ToSocket = ToSocket
    };
}

public sealed class MaterialGraph
{
    public List<MaterialGraphNode> Nodes { get; set; } = [];
    public List<MaterialGraphLink> Links { get; set; } = [];

    public MaterialGraphNode? FindNode(string id) => Nodes.FirstOrDefault(node => node.Id == id);
    public MaterialGraphNode? FindOutput() => Nodes.FirstOrDefault(node => node.Type == "PBROutput");

    public MaterialGraph Clone() => new()
    {
        Nodes = Nodes.Select(node => node.Clone()).ToList(),
        Links = Links.Select(link => link.Clone()).ToList()
    };
}

public sealed class MaterialAsset
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;
    public string Name { get; set; } = "Material";
    public MaterialAlphaMode AlphaMode { get; set; } = MaterialAlphaMode.Opaque;
    public float AlphaCutoff { get; set; } = 0.5f;
    public bool TwoSided { get; set; }
    public bool CastShadows { get; set; } = true;
    public bool ReceiveShadows { get; set; } = true;
    public MaterialGraph Graph { get; set; } = new();

    public static MaterialAsset CreateDefault(string name, string? baseColorTexture = null)
    {
        var output = new MaterialGraphNode
        {
            Id = "output",
            Type = "PBROutput",
            Name = "Material Output",
            Position = new Vector2(520, 160),
            Properties = new JsonObject
            {
                ["base_color"] = Vec3ToJson(Vector3.One),
                ["roughness"] = 0.5f,
                ["metallic"] = 0.0f,
                ["emission"] = Vec3ToJson(Vector3.Zero),
                ["alpha"] = 1.0f
            }
        };

        var result = new MaterialAsset { Name = name };
        result.Graph.Nodes.Add(output);

        if (!string.IsNullOrWhiteSpace(baseColorTexture))
        {
            var texture = new MaterialGraphNode
            {
                Id = "base_color_texture",
                Type = "Texture2D",
                Name = "Base Color",
                Position = new Vector2(120, 160),
                Properties = new JsonObject
                {
                    ["path"] = NormalizeAssetPath(baseColorTexture),
                    ["color_space"] = "sRGB"
                }
            };
            result.Graph.Nodes.Add(texture);
            result.Graph.Links.Add(new MaterialGraphLink
            {
                FromNode = texture.Id,
                FromSocket = "Color",
                ToNode = output.Id,
                ToSocket = "BaseColor"
            });
            result.Graph.Links.Add(new MaterialGraphLink
            {
                FromNode = texture.Id,
                FromSocket = "Alpha",
                ToNode = output.Id,
                ToSocket = "Alpha"
            });
        }

        return result;
    }

    public static MaterialAsset Load(string path)
    {
        JsonNode? rootNode = JsonNode.Parse(File.ReadAllText(path));
        if (rootNode is not JsonObject root)
            throw new InvalidDataException($"Material '{path}' does not contain a JSON object.");

        int version = root.TryGetPropertyValue("version", out JsonNode? versionNode)
            ? versionNode!.GetValue<int>()
            : 1;
        if (version > CurrentVersion)
            throw new InvalidDataException($"Material version {version} is newer than supported version {CurrentVersion}.");

        var material = new MaterialAsset
        {
            Version = version,
            Name = GetString(root, "name", Path.GetFileNameWithoutExtension(path)),
            AlphaMode = ParseAlphaMode(GetString(root, "alpha_mode", "opaque")),
            AlphaCutoff = GetFloat(root, "alpha_cutoff", 0.5f),
            TwoSided = GetBool(root, "two_sided", false),
            CastShadows = GetBool(root, "cast_shadows", true),
            ReceiveShadows = GetBool(root, "receive_shadows", true)
        };

        if (root["graph"] is JsonObject graphObject)
            material.Graph = ParseGraph(graphObject);

        if (material.Graph.FindOutput() == null)
            material.Graph.Nodes.Add(CreateDefault(material.Name).Graph.FindOutput()!);

        return material;
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, ToJson().ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    public JsonObject ToJson()
    {
        var graphObject = new JsonObject
        {
            ["nodes"] = new JsonArray(Graph.Nodes.Select(SerializeNode).ToArray()),
            ["links"] = new JsonArray(Graph.Links.Select(SerializeLink).ToArray())
        };

        return new JsonObject
        {
            ["version"] = CurrentVersion,
            ["name"] = Name,
            ["alpha_mode"] = AlphaMode.ToString().ToLowerInvariant(),
            ["alpha_cutoff"] = AlphaCutoff,
            ["two_sided"] = TwoSided,
            ["cast_shadows"] = CastShadows,
            ["receive_shadows"] = ReceiveShadows,
            ["graph"] = graphObject
        };
    }

    public static string NormalizeAssetPath(string path)
    {
        string normalized = path.Replace('\\', '/').Trim();
        if (normalized.StartsWith("res/", StringComparison.OrdinalIgnoreCase))
            normalized = normalized[4..];
        return normalized;
    }

    private static MaterialGraph ParseGraph(JsonObject graphObject)
    {
        var graph = new MaterialGraph();
        if (graphObject["nodes"] is JsonArray nodes)
        {
            foreach (JsonNode? nodeValue in nodes)
            {
                if (nodeValue is not JsonObject nodeObject)
                    continue;
                graph.Nodes.Add(new MaterialGraphNode
                {
                    Id = GetString(nodeObject, "id", Guid.NewGuid().ToString("N")),
                    Type = GetString(nodeObject, "type", "Float"),
                    Name = GetString(nodeObject, "name", GetString(nodeObject, "type", "Node")),
                    Position = nodeObject["position"] is JsonArray position && position.Count >= 2
                        ? new Vector2(position[0]!.GetValue<float>(), position[1]!.GetValue<float>())
                        : Vector2.Zero,
                    Properties = nodeObject["properties"] is JsonObject properties
                        ? JsonNode.Parse(properties.ToJsonString())!.AsObject()
                        : new JsonObject()
                });
            }
        }

        if (graphObject["links"] is JsonArray links)
        {
            foreach (JsonNode? linkValue in links)
            {
                if (linkValue is not JsonObject linkObject)
                    continue;
                graph.Links.Add(new MaterialGraphLink
                {
                    FromNode = GetString(linkObject, "from_node", ""),
                    FromSocket = GetString(linkObject, "from_socket", ""),
                    ToNode = GetString(linkObject, "to_node", ""),
                    ToSocket = GetString(linkObject, "to_socket", "")
                });
            }
        }

        return graph;
    }

    private static JsonObject SerializeNode(MaterialGraphNode node) => new()
    {
        ["id"] = node.Id,
        ["type"] = node.Type,
        ["name"] = node.Name,
        ["position"] = new JsonArray(node.Position.X, node.Position.Y),
        ["properties"] = JsonNode.Parse(node.Properties.ToJsonString())
    };

    private static JsonObject SerializeLink(MaterialGraphLink link) => new()
    {
        ["from_node"] = link.FromNode,
        ["from_socket"] = link.FromSocket,
        ["to_node"] = link.ToNode,
        ["to_socket"] = link.ToSocket
    };

    public static Vector3 GetVector3(JsonObject properties, string key, Vector3 fallback)
    {
        if (properties[key] is not JsonArray array || array.Count < 3)
            return fallback;
        return new Vector3(array[0]!.GetValue<float>(), array[1]!.GetValue<float>(), array[2]!.GetValue<float>());
    }

    public static float GetFloat(JsonObject properties, string key, float fallback) =>
        properties.TryGetPropertyValue(key, out JsonNode? value) && value != null
            ? value.GetValue<float>()
            : fallback;

    public static string GetString(JsonObject properties, string key, string fallback) =>
        properties.TryGetPropertyValue(key, out JsonNode? value) && value != null
            ? value.GetValue<string>()
            : fallback;

    private static bool GetBool(JsonObject properties, string key, bool fallback) =>
        properties.TryGetPropertyValue(key, out JsonNode? value) && value != null
            ? value.GetValue<bool>()
            : fallback;

    public static JsonArray Vec3ToJson(Vector3 value) => new(value.X, value.Y, value.Z);

    private static MaterialAlphaMode ParseAlphaMode(string value) => value.ToLowerInvariant() switch
    {
        "mask" => MaterialAlphaMode.Mask,
        "blend" => MaterialAlphaMode.Blend,
        _ => MaterialAlphaMode.Opaque
    };
}
