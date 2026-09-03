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

public sealed class MaterialExposedParameter
{
    public string Name { get; set; } = "Parameter";
    public MaterialValueType Type { get; set; } = MaterialValueType.Float;
    public JsonNode? DefaultValue { get; set; } = 0.0f;

    public MaterialExposedParameter Clone() => new()
    {
        Name = Name,
        Type = Type,
        DefaultValue = DefaultValue?.DeepClone()
    };
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
    public const int CurrentVersion = 2;

    public int Version { get; set; } = CurrentVersion;
    public string Name { get; set; } = "Material";
    public MaterialAlphaMode AlphaMode { get; set; } = MaterialAlphaMode.Opaque;
    public float AlphaCutoff { get; set; } = 0.5f;
    public bool TwoSided { get; set; }
    public bool CastShadows { get; set; } = true;
    public bool ReceiveShadows { get; set; } = true;
    public string ParentMaterialPath { get; set; } = "";
    public List<MaterialExposedParameter> ExposedParameters { get; set; } = [];
    public Dictionary<string, JsonNode?> ParameterOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public MaterialGraph Graph { get; set; } = new();

    public MaterialAsset Clone() => new()
    {
        Version = Version,
        Name = Name,
        AlphaMode = AlphaMode,
        AlphaCutoff = AlphaCutoff,
        TwoSided = TwoSided,
        CastShadows = CastShadows,
        ReceiveShadows = ReceiveShadows,
        ParentMaterialPath = ParentMaterialPath,
        ExposedParameters = ExposedParameters.Select(parameter => parameter.Clone()).ToList(),
        ParameterOverrides = ParameterOverrides.ToDictionary(
            pair => pair.Key,
            pair => pair.Value?.DeepClone(),
            StringComparer.OrdinalIgnoreCase),
        Graph = Graph.Clone()
    };

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
                ["alpha"] = 1.0f,
                ["ao"] = 1.0f
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
            ReceiveShadows = GetBool(root, "receive_shadows", true),
            ParentMaterialPath = GetString(root, "parent_material", "")
        };

        ParseParameters(root, material);

        if (root["graph"] is JsonObject graphObject)
            material.Graph = ParseGraph(graphObject);

        if (material.Graph.FindOutput() == null)
            material.Graph.Nodes.Add(CreateDefault(material.Name).Graph.FindOutput()!);

        return material;
    }

    public static MaterialAsset LoadResolved(string path)
    {
        return LoadResolved(path, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
    }

    private static MaterialAsset LoadResolved(string path, HashSet<string> visiting)
    {
        string fullPath = Path.GetFullPath(path);
        if (!visiting.Add(fullPath))
            throw new InvalidDataException($"Material instance cycle detected at '{fullPath}'.");

        MaterialAsset material = Load(fullPath);
        if (!string.IsNullOrWhiteSpace(material.ParentMaterialPath))
        {
            string parentPath = MaterialRuntime.ResolveAssetPath(material.ParentMaterialPath);
            if (File.Exists(parentPath))
            {
                MaterialAsset parent = LoadResolved(parentPath, visiting);
                if (material.Graph.Nodes.Count <= 1)
                    material.Graph = parent.Graph.Clone();
                if (material.ExposedParameters.Count == 0)
                    material.ExposedParameters = parent.ExposedParameters.Select(parameter => parameter.Clone()).ToList();
            }
            ApplyParameterOverrides(material);
        }

        visiting.Remove(fullPath);
        return material;
    }

    private static void ApplyParameterOverrides(MaterialAsset material)
    {
        foreach (MaterialGraphNode node in material.Graph.Nodes)
        {
            string name = GetString(node.Properties, "parameter_name", "").Trim();
            if (!GetBool(node.Properties, "expose", false) || string.IsNullOrWhiteSpace(name))
                continue;
            if (material.ParameterOverrides.TryGetValue(name, out JsonNode? value) && value != null)
                node.Properties["value"] = value.DeepClone();
        }
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
            ["parent_material"] = string.IsNullOrWhiteSpace(ParentMaterialPath) ? null : ParentMaterialPath,
            ["exposed_parameters"] = new JsonArray(ExposedParameters.Select(SerializeParameter).ToArray()),
            ["parameter_overrides"] = new JsonObject(ParameterOverrides.ToDictionary(
                pair => pair.Key,
                pair => pair.Value?.DeepClone())),
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

    private static JsonObject SerializeParameter(MaterialExposedParameter parameter) => new()
    {
        ["name"] = parameter.Name,
        ["type"] = parameter.Type.ToString(),
        ["default"] = parameter.DefaultValue?.DeepClone()
    };

    private static void ParseParameters(JsonObject root, MaterialAsset material)
    {
        if (root["exposed_parameters"] is JsonArray parameters)
        {
            foreach (JsonNode? value in parameters)
            {
                if (value is not JsonObject parameter)
                    continue;
                material.ExposedParameters.Add(new MaterialExposedParameter
                {
                    Name = GetString(parameter, "name", "Parameter"),
                    Type = ParseValueType(GetString(parameter, "type", nameof(MaterialValueType.Float))),
                    DefaultValue = parameter["default"]?.DeepClone()
                });
            }
        }

        if (root["parameter_overrides"] is JsonObject overrides)
        {
            foreach ((string key, JsonNode? value) in overrides)
                material.ParameterOverrides[key] = value?.DeepClone();
        }
    }

    public void SyncExposedParameters()
    {
        var existing = ExposedParameters.ToDictionary(parameter => parameter.Name, StringComparer.OrdinalIgnoreCase);
        var discovered = new List<MaterialExposedParameter>();
        foreach (MaterialGraphNode node in Graph.Nodes)
        {
            if (!GetBool(node.Properties, "expose", false))
                continue;
            string name = GetString(node.Properties, "parameter_name", "").Trim();
            if (string.IsNullOrWhiteSpace(name))
                continue;

            MaterialValueType type = node.Type switch
            {
                "Color" or "Vector3" => MaterialValueType.Vector3,
                "Vector2" => MaterialValueType.Vector2,
                _ => MaterialValueType.Float
            };
            JsonNode defaultValue = node.Properties["value"]?.DeepClone()
                ?? (type switch
                {
                    MaterialValueType.Vector2 => Vec2ToJson(Vector2.Zero),
                    MaterialValueType.Vector3 => Vec3ToJson(Vector3.Zero),
                    _ => JsonValue.Create(0.0f)!
                });
            discovered.Add(existing.TryGetValue(name, out MaterialExposedParameter? parameter)
                ? new MaterialExposedParameter { Name = name, Type = type, DefaultValue = parameter.DefaultValue?.DeepClone() ?? defaultValue }
                : new MaterialExposedParameter { Name = name, Type = type, DefaultValue = defaultValue });
        }
        ExposedParameters = discovered;
    }

    public JsonNode? GetParameterValue(MaterialGraphNode node)
    {
        string name = GetString(node.Properties, "parameter_name", "").Trim();
        if (GetBool(node.Properties, "expose", false) &&
            !string.IsNullOrWhiteSpace(name) &&
            ParameterOverrides.TryGetValue(name, out JsonNode? overrideValue))
            return overrideValue?.DeepClone();
        return node.Properties["value"]?.DeepClone();
    }

    public static Vector3 GetVector3(JsonObject properties, string key, Vector3 fallback)
    {
        if (properties[key] is not JsonArray array || array.Count < 3)
            return fallback;
        return new Vector3(array[0]!.GetValue<float>(), array[1]!.GetValue<float>(), array[2]!.GetValue<float>());
    }

    public static float GetFloat(JsonObject properties, string key, float fallback)
    {
        if (!properties.TryGetPropertyValue(key, out JsonNode? value) || value is not JsonValue jsonValue)
            return fallback;

        // JsonNode preserves integer literals as Int32/Int64. Material defaults
        // and hand-authored .fmat files commonly mix integer and floating-point
        // numbers, so do not require every scalar to be serialized as a float.
        if (jsonValue.TryGetValue<float>(out float single))
            return single;
        if (jsonValue.TryGetValue<double>(out double real))
            return (float)real;
        if (jsonValue.TryGetValue<int>(out int integer))
            return integer;
        if (jsonValue.TryGetValue<long>(out long longInteger))
            return longInteger;
        return fallback;
    }

    public static Vector2 GetVector2(JsonObject properties, string key, Vector2 fallback)
    {
        if (properties[key] is not JsonArray array || array.Count < 2)
            return fallback;
        return new Vector2(array[0]!.GetValue<float>(), array[1]!.GetValue<float>());
    }

    public static List<string> GetStringArray(JsonObject properties, string key)
    {
        if (properties[key] is JsonArray array)
            return array
                .OfType<JsonValue>()
                .Select(value => value.TryGetValue<string>(out string? text) ? text.Trim() : "")
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToList();

        string legacyValue = properties[key]?.GetValue<string>() ?? "";
        return legacyValue
            .Split([';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToList();
    }

    public static void SetStringArray(JsonObject properties, string key, IEnumerable<string> values)
    {
        var array = new JsonArray();
        foreach (string value in values)
        {
            string trimmed = value.Trim();
            if (!string.IsNullOrWhiteSpace(trimmed))
                array.Add(trimmed);
        }
        properties[key] = array;
    }

    public static string GetString(JsonObject properties, string key, string fallback) =>
        properties.TryGetPropertyValue(key, out JsonNode? value) && value != null
            ? value.GetValue<string>()
            : fallback;

    public static bool GetBool(JsonObject properties, string key, bool fallback) =>
        properties.TryGetPropertyValue(key, out JsonNode? value) && value != null
            ? value.GetValue<bool>()
            : fallback;

    public static JsonArray Vec2ToJson(Vector2 value) => new(value.X, value.Y);

    public static JsonArray Vec3ToJson(Vector3 value) => new(value.X, value.Y, value.Z);

    private static MaterialValueType ParseValueType(string value) => value.Trim().ToLowerInvariant() switch
    {
        "vector2" or "vec2" => MaterialValueType.Vector2,
        "vector3" or "vec3" or "color" => MaterialValueType.Vector3,
        _ => MaterialValueType.Float
    };

    private static MaterialAlphaMode ParseAlphaMode(string value) => value.ToLowerInvariant() switch
    {
        "mask" => MaterialAlphaMode.Mask,
        "blend" => MaterialAlphaMode.Blend,
        _ => MaterialAlphaMode.Opaque
    };
}
