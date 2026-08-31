using System.Collections.Concurrent;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fuse.Renderer;
using Fuse.Scene.Model;

namespace Fuse.Scene.Geometry;

/// <summary>Socket types supported by the first Geometry Nodes implementation.</summary>
public enum GeometrySocketType
{
    Geometry,
    Float,
    Integer,
    Vector2,
    Vector3,
    String
}

public sealed record GeometryNodeDefinition(
    IReadOnlyDictionary<string, GeometrySocketType> Inputs,
    IReadOnlyDictionary<string, GeometrySocketType> Outputs);

public static class GeometryGraphNodeCatalog
{
    private static readonly IReadOnlyDictionary<string, GeometryNodeDefinition> Definitions =
        new Dictionary<string, GeometryNodeDefinition>(StringComparer.OrdinalIgnoreCase)
        {
            ["GroupInput"] = Definition([], [("Geometry", GeometrySocketType.Geometry)]),
            ["GroupOutput"] = Definition([("Geometry", GeometrySocketType.Geometry)], []),
            ["Cube"] = Definition([], [("Geometry", GeometrySocketType.Geometry)]),
            ["Plane"] = Definition([], [("Geometry", GeometrySocketType.Geometry)]),
            ["Cylinder"] = Definition([], [("Geometry", GeometrySocketType.Geometry)]),
            ["Transform"] = Definition(
                [("Geometry", GeometrySocketType.Geometry), ("Translation", GeometrySocketType.Vector3),
                 ("Rotation", GeometrySocketType.Vector3), ("Scale", GeometrySocketType.Vector3)],
                [("Geometry", GeometrySocketType.Geometry)]),
            ["Merge"] = Definition([("A", GeometrySocketType.Geometry), ("B", GeometrySocketType.Geometry)], [("Geometry", GeometrySocketType.Geometry)]),
            ["DistributePointsOnFaces"] = Definition([("Mesh", GeometrySocketType.Geometry), ("Density", GeometrySocketType.Float)], [("Geometry", GeometrySocketType.Geometry)]),
            ["InstanceOnPoints"] = Definition([("Points", GeometrySocketType.Geometry), ("Instance", GeometrySocketType.Geometry)], [("Geometry", GeometrySocketType.Geometry)]),
            ["RandomValue"] = Definition([("Min", GeometrySocketType.Float), ("Max", GeometrySocketType.Float), ("Seed", GeometrySocketType.Integer)], [("Value", GeometrySocketType.Float)]),
            ["SetMaterial"] = Definition([("Geometry", GeometrySocketType.Geometry), ("Material", GeometrySocketType.String)], [("Geometry", GeometrySocketType.Geometry)]),
            ["Subdivide"] = Definition([("Geometry", GeometrySocketType.Geometry), ("Level", GeometrySocketType.Integer)], [("Geometry", GeometrySocketType.Geometry)]),
            ["Bake"] = Definition([("Geometry", GeometrySocketType.Geometry)], [("Geometry", GeometrySocketType.Geometry)])
        };

    public static GeometrySocketType GetInputType(string nodeType, string socket) =>
        TryGet(nodeType, out GeometryNodeDefinition? definition) && definition is not null && definition.Inputs.TryGetValue(socket, out GeometrySocketType type)
            ? type : GeometrySocketType.Geometry;

    public static GeometrySocketType GetOutputType(string nodeType, string socket = "Geometry") =>
        TryGet(nodeType, out GeometryNodeDefinition? definition) && definition is not null && definition.Outputs.TryGetValue(socket, out GeometrySocketType type)
            ? type : GeometrySocketType.Geometry;

    public static bool IsCompatible(string sourceType, string sourceSocket, string targetType, string targetSocket) =>
        GetOutputType(sourceType, sourceSocket) == GetInputType(targetType, targetSocket);

    private static bool TryGet(string type, out GeometryNodeDefinition? definition)
    {
        string normalized = type.Replace("_", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal);
        definition = Definitions.FirstOrDefault(pair => pair.Key.Replace("_", "", StringComparison.Ordinal).Equals(normalized, StringComparison.OrdinalIgnoreCase)).Value;
        return definition != null;
    }

    private static GeometryNodeDefinition Definition(
        IEnumerable<(string Name, GeometrySocketType Type)> inputs,
        IEnumerable<(string Name, GeometrySocketType Type)> outputs) =>
        new(inputs.ToDictionary(pair => pair.Name, pair => pair.Type, StringComparer.OrdinalIgnoreCase),
            outputs.ToDictionary(pair => pair.Name, pair => pair.Type, StringComparer.OrdinalIgnoreCase));
}

public sealed class GeometryGraphNode
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Type { get; set; } = "GroupOutput";
    public string Name { get; set; } = "Node";
    public Vector2 Position { get; set; }
    public JsonObject Properties { get; set; } = new();
}

public sealed class GeometryGraphLink
{
    public string FromNode { get; set; } = "";
    public string FromSocket { get; set; } = "Geometry";
    public string ToNode { get; set; } = "";
    public string ToSocket { get; set; } = "Geometry";
}

public sealed class GeometryGraph
{
    public List<GeometryGraphNode> Nodes { get; set; } = [];
    public List<GeometryGraphLink> Links { get; set; } = [];

    public GeometryGraphNode? FindNode(string id) =>
        Nodes.FirstOrDefault(node => node.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    public GeometryGraphNode? FindOutput() =>
        Nodes.FirstOrDefault(node => node.Type.Equals("GroupOutput", StringComparison.OrdinalIgnoreCase))
        ?? Nodes.LastOrDefault();
}

/// <summary>
/// A .fgeo asset. It is deliberately independent from the material graph so
/// geometry evaluation can also be used by the runtime without ImGui.
/// </summary>
public sealed class GeometryGraphAsset
{
    public int Version { get; set; } = 1;
    public string Name { get; set; } = "GeometryGraph";
    public GeometryGraph Graph { get; set; } = new();

    public static GeometryGraphAsset CreateDefault(string name = "GeometryGraph")
    {
        var input = new GeometryGraphNode
        {
            Id = "group_input",
            Type = "GroupInput",
            Name = "Group Input",
            Position = new Vector2(40, 150)
        };
        var cube = new GeometryGraphNode
        {
            Id = "cube",
            Type = "Cube",
            Name = "Cube",
            Position = new Vector2(280, 150),
            Properties = new JsonObject { ["size"] = new JsonArray(1.0f, 1.0f, 1.0f) }
        };
        var output = new GeometryGraphNode
        {
            Id = "group_output",
            Type = "GroupOutput",
            Name = "Group Output",
            Position = new Vector2(560, 150)
        };
        return new GeometryGraphAsset
        {
            Name = name,
            Graph = new GeometryGraph
            {
                Nodes = [input, cube, output],
                Links =
                [
                    new GeometryGraphLink
                    {
                        FromNode = cube.Id,
                        FromSocket = "Geometry",
                        ToNode = output.Id,
                        ToSocket = "Geometry"
                    }
                ]
            }
        };
    }

    public static GeometryGraphAsset Load(string path)
    {
        JsonNode? node = JsonNode.Parse(File.ReadAllText(path));
        if (node is not JsonObject root)
            throw new InvalidDataException("The geometry graph root must be an object.");

        var asset = new GeometryGraphAsset
        {
            Version = root.TryGetPropertyValue("version", out JsonNode? version) ? (int)version! : 1,
            Name = root.TryGetPropertyValue("name", out JsonNode? name) ? (string?)name ?? "GeometryGraph" : "GeometryGraph"
        };

        if (root.TryGetPropertyValue("nodes", out JsonNode? nodesNode) && nodesNode is JsonArray nodes)
        {
            foreach (JsonNode? child in nodes)
            {
                if (child is not JsonObject obj)
                    continue;
                var graphNode = new GeometryGraphNode
                {
                    Id = obj.TryGetPropertyValue("id", out JsonNode? id) ? (string?)id ?? Guid.NewGuid().ToString("N") : Guid.NewGuid().ToString("N"),
                    Type = obj.TryGetPropertyValue("type", out JsonNode? type) ? (string?)type ?? "Unknown" : "Unknown",
                    Name = obj.TryGetPropertyValue("name", out JsonNode? label) ? (string?)label ?? "Node" : "Node",
                    Position = ReadVector2(obj, "position", Vector2.Zero),
                    Properties = obj.TryGetPropertyValue("properties", out JsonNode? properties) && properties is JsonObject propertyObject
                        ? propertyObject
                        : new JsonObject()
                };
                asset.Graph.Nodes.Add(graphNode);
            }
        }

        if (root.TryGetPropertyValue("links", out JsonNode? linksNode) && linksNode is JsonArray links)
        {
            foreach (JsonNode? child in links)
            {
                if (child is not JsonObject obj)
                    continue;
                asset.Graph.Links.Add(new GeometryGraphLink
                {
                    FromNode = StringProperty(obj, "from_node"),
                    FromSocket = StringProperty(obj, "from_socket", "Geometry"),
                    ToNode = StringProperty(obj, "to_node"),
                    ToSocket = StringProperty(obj, "to_socket", "Geometry")
                });
            }
        }

        if (asset.Graph.Nodes.Count == 0)
            return CreateDefault(asset.Name);
        return asset;
    }

    public void Save(string path)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var root = new JsonObject
        {
            ["version"] = Version,
            ["name"] = Name,
            ["nodes"] = new JsonArray(),
            ["links"] = new JsonArray()
        };
        var nodes = (JsonArray)root["nodes"]!;
        foreach (GeometryGraphNode node in Graph.Nodes)
        {
            nodes.Add(new JsonObject
            {
                ["id"] = node.Id,
                ["type"] = node.Type,
                ["name"] = node.Name,
                ["position"] = new JsonArray(node.Position.X, node.Position.Y),
                ["properties"] = JsonNode.Parse(node.Properties.ToJsonString())
            });
        }

        var links = (JsonArray)root["links"]!;
        foreach (GeometryGraphLink link in Graph.Links)
        {
            links.Add(new JsonObject
            {
                ["from_node"] = link.FromNode,
                ["from_socket"] = link.FromSocket,
                ["to_node"] = link.ToNode,
                ["to_socket"] = link.ToSocket
            });
        }
        File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string StringProperty(JsonObject obj, string name, string fallback = "") =>
        obj.TryGetPropertyValue(name, out JsonNode? value) ? (string?)value ?? fallback : fallback;

    private static Vector2 ReadVector2(JsonObject obj, string name, Vector2 fallback)
    {
        if (obj.TryGetPropertyValue(name, out JsonNode? value) && value is JsonArray array && array.Count >= 2)
            return new Vector2((float)array[0]!, (float)array[1]!);
        return fallback;
    }
}

public sealed class GeometryEvaluationResult
{
    public required MeshData Mesh { get; init; }
    public string? MaterialPath { get; init; }
}

public sealed class GeometryEvaluationContext
{
    public MeshData? InputMesh { get; init; }
}

/// <summary>CPU evaluator for the initial, useful subset of Geometry Nodes.</summary>
public static class GeometryGraphEvaluator
{
    private sealed class EvaluationState
    {
        public string Error = "";
    }

    private sealed class Value
    {
        public MeshData? Geometry { get; init; }
        public float Float { get; init; }
        public int Integer { get; init; }
        public Vector2 Vector2 { get; init; }
        public Vector3 Vector3 { get; init; }
        public string? String { get; init; }
        public string? MaterialPath { get; init; }
        public IReadOnlyList<Vector3>? Points { get; init; }
    }

    public static bool TryEvaluate(
        GeometryGraphAsset asset,
        GeometryEvaluationContext context,
        out GeometryEvaluationResult? result,
        out string error)
    {
        result = null;
        error = "";
        GeometryGraphNode? output = asset.Graph.FindOutput();
        if (output == null)
        {
            error = "Geometry graph has no output node.";
            return false;
        }

        var values = new Dictionary<string, Value>(StringComparer.OrdinalIgnoreCase);
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var state = new EvaluationState();
        Value? outputValue = EvaluateNode(output, asset.Graph, context, values, visiting, state);
        error = state.Error;
        if (outputValue?.Geometry == null || outputValue.Geometry.Indices.Length == 0)
        {
            if (string.IsNullOrEmpty(error))
                error = "Geometry graph produced no triangles.";
            return false;
        }

        result = new GeometryEvaluationResult
        {
            Mesh = outputValue.Geometry,
            MaterialPath = outputValue.MaterialPath
        };
        return true;
    }

    private static Value? EvaluateNode(
        GeometryGraphNode node,
        GeometryGraph graph,
        GeometryEvaluationContext context,
        Dictionary<string, Value> values,
        HashSet<string> visiting,
        EvaluationState state)
    {
        if (values.TryGetValue(node.Id, out Value? cached))
            return cached;
        if (!visiting.Add(node.Id))
        {
            state.Error = $"Geometry graph contains a cycle at '{node.Name}'.";
            return null;
        }

        Value? Input(string socket)
        {
            GeometryGraphLink? link = graph.Links.LastOrDefault(candidate =>
                candidate.ToNode.Equals(node.Id, StringComparison.OrdinalIgnoreCase) &&
                candidate.ToSocket.Equals(socket, StringComparison.OrdinalIgnoreCase));
            if (link == null)
                return null;
            GeometryGraphNode? source = graph.FindNode(link.FromNode);
            return source == null ? null : EvaluateNode(source, graph, context, values, visiting, state);
        }

        Value MakeGeometry(MeshData mesh, string? material = null) => new() { Geometry = mesh, MaterialPath = material };
        Value PassGeometry(string socket = "Geometry")
        {
            Value? value = Input(socket);
            return value ?? MakeGeometry(context.InputMesh ?? EmptyMesh());
        }

        string type = node.Type.Trim().ToLowerInvariant();
        Value value;
        switch (type)
        {
            case "groupinput":
            case "group_input":
                value = MakeGeometry(context.InputMesh ?? EmptyMesh());
                break;
            case "groupoutput":
            case "group_output":
                value = PassGeometry();
                break;
            case "cube":
            case "meshcube":
                value = MakeGeometry(GenerateCube(ReadVector3(node, Input("Size"), "size", Vector3.One)));
                break;
            case "plane":
                value = MakeGeometry(GeneratePlane(ReadVector2(node, Input("Size"), "size", Vector2.One)));
                break;
            case "cylinder":
                value = MakeGeometry(GenerateCylinder(
                    ReadFloat(node, Input("Radius"), "radius", 0.5f),
                    ReadFloat(node, Input("Depth"), "depth", 1.0f),
                    System.Math.Clamp(ReadInt(node, Input("Segments"), "segments", 24), 3, 128)));
                break;
            case "transform":
                {
                    Value source = PassGeometry();
                    value = MakeGeometry(Transform(source.Geometry!,
                        ReadVector3(node, Input("Translation"), "translation", Vector3.Zero),
                        ReadVector3(node, Input("Rotation"), "rotation", Vector3.Zero),
                        ReadVector3(node, Input("Scale"), "scale", Vector3.One)), source.MaterialPath);
                    break;
                }
            case "merge":
            case "joingeometry":
            case "join_geometry":
                {
                    MeshData first = Input("A")?.Geometry ?? context.InputMesh ?? EmptyMesh();
                    MeshData second = Input("B")?.Geometry ?? EmptyMesh();
                    value = MakeGeometry(Merge(first, second), Input("A")?.MaterialPath ?? Input("B")?.MaterialPath);
                    break;
                }
            case "distributepointsonfaces":
            case "distribute_points_on_faces":
                {
                    Value source = Input("Mesh") ?? PassGeometry();
                    float density = ReadFloat(node, Input("Density"), "density", 1.0f);
                    value = new Value
                    {
                        Geometry = source.Geometry,
                        MaterialPath = source.MaterialPath,
                        Points = DistributePoints(source.Geometry!, density)
                    };
                    break;
                }
            case "instanceonpoints":
            case "instance_on_points":
                {
                    Value points = Input("Points") ?? new Value { Points = [] };
                    Value instance = Input("Instance") ?? new Value { Geometry = EmptyMesh() };
                    value = MakeGeometry(InstanceOnPoints(points.Points ?? [], instance.Geometry!), instance.MaterialPath);
                    break;
                }
            case "randomvalue":
            case "random_value":
                {
                    float min = ReadFloat(node, Input("Min"), "min", 0.0f);
                    float max = ReadFloat(node, Input("Max"), "max", 1.0f);
                    int seed = ReadInt(node, Input("Seed"), "seed", 1);
                    var random = new Random(seed);
                    value = new Value { Float = min + (float)random.NextDouble() * (max - min) };
                    break;
                }
            case "setmaterial":
            case "set_material":
                {
                    Value source = PassGeometry();
                    string material = ReadString(node, Input("Material"), "material", "");
                    value = MakeGeometry(source.Geometry!, string.IsNullOrWhiteSpace(material) ? source.MaterialPath : material);
                    break;
                }
            case "subdivide":
                {
                    Value source = PassGeometry();
                    int level = System.Math.Clamp(ReadInt(node, Input("Level"), "level", 1), 1, 2);
                    MeshData mesh = source.Geometry!;
                    for (int i = 0; i < level; i++) mesh = Subdivide(mesh);
                    value = MakeGeometry(mesh, source.MaterialPath);
                    break;
                }
            case "bake":
                value = PassGeometry();
                break;
            default:
                state.Error = $"Unsupported geometry node '{node.Type}'.";
                visiting.Remove(node.Id);
                return null;
        }

        visiting.Remove(node.Id);
        values[node.Id] = value;
        return value;
    }

    private static float ReadFloat(GeometryGraphNode node, Value? input, string property, float fallback) =>
        input?.Float ?? (node.Properties.TryGetPropertyValue(property, out JsonNode? value) ? (float)value! : fallback);

    private static int ReadInt(GeometryGraphNode node, Value? input, string property, int fallback) =>
        input?.Integer ?? (node.Properties.TryGetPropertyValue(property, out JsonNode? value) ? (int)value! : fallback);

    private static string ReadString(GeometryGraphNode node, Value? input, string property, string fallback) =>
        input?.String ?? (node.Properties.TryGetPropertyValue(property, out JsonNode? value) ? (string?)value ?? fallback : fallback);

    private static Vector2 ReadVector2(GeometryGraphNode node, Value? input, string property, Vector2 fallback)
    {
        if (input != null && input.Vector2 != Vector2.Zero)
            return input.Vector2;
        if (node.Properties.TryGetPropertyValue(property, out JsonNode? value) && value is JsonArray array && array.Count >= 2)
            return new Vector2((float)array[0]!, (float)array[1]!);
        return fallback;
    }

    private static Vector3 ReadVector3(GeometryGraphNode node, Value? input, string property, Vector3 fallback)
    {
        if (input != null && input.Vector3 != Vector3.Zero)
            return input.Vector3;
        if (node.Properties.TryGetPropertyValue(property, out JsonNode? value) && value is JsonArray array && array.Count >= 3)
            return new Vector3((float)array[0]!, (float)array[1]!, (float)array[2]!);
        return fallback;
    }

    private static MeshData EmptyMesh() => new([], []);

    private static MeshData GenerateCube(Vector3 size)
    {
        Vector3 half = Vector3.Max(Vector3.Abs(size) * 0.5f, new Vector3(0.001f));
        var vertices = new List<Vertex>(24);
        var indices = new List<uint>(36);
        AddFace(new Vector3(-half.X, -half.Y, half.Z), new Vector3(half.X, -half.Y, half.Z), new Vector3(half.X, half.Y, half.Z), new Vector3(-half.X, half.Y, half.Z), Vector3.UnitZ);
        AddFace(new Vector3(half.X, -half.Y, -half.Z), new Vector3(-half.X, -half.Y, -half.Z), new Vector3(-half.X, half.Y, -half.Z), new Vector3(half.X, half.Y, -half.Z), -Vector3.UnitZ);
        AddFace(new Vector3(-half.X, half.Y, half.Z), new Vector3(half.X, half.Y, half.Z), new Vector3(half.X, half.Y, -half.Z), new Vector3(-half.X, half.Y, -half.Z), Vector3.UnitY);
        AddFace(new Vector3(-half.X, -half.Y, -half.Z), new Vector3(half.X, -half.Y, -half.Z), new Vector3(half.X, -half.Y, half.Z), new Vector3(-half.X, -half.Y, half.Z), -Vector3.UnitY);
        AddFace(new Vector3(half.X, -half.Y, half.Z), new Vector3(half.X, -half.Y, -half.Z), new Vector3(half.X, half.Y, -half.Z), new Vector3(half.X, half.Y, half.Z), Vector3.UnitX);
        AddFace(new Vector3(-half.X, -half.Y, -half.Z), new Vector3(-half.X, -half.Y, half.Z), new Vector3(-half.X, half.Y, half.Z), new Vector3(-half.X, half.Y, -half.Z), -Vector3.UnitX);
        return new MeshData(vertices.ToArray(), indices.ToArray());

        void AddFace(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 normal)
        {
            uint start = (uint)vertices.Count;
            vertices.Add(new Vertex { Position = a, Normal = normal, TexCoord = new Vector2(0, 0) });
            vertices.Add(new Vertex { Position = b, Normal = normal, TexCoord = new Vector2(1, 0) });
            vertices.Add(new Vertex { Position = c, Normal = normal, TexCoord = new Vector2(1, 1) });
            vertices.Add(new Vertex { Position = d, Normal = normal, TexCoord = new Vector2(0, 1) });
            indices.Add(start); indices.Add(start + 1); indices.Add(start + 2);
            indices.Add(start); indices.Add(start + 2); indices.Add(start + 3);
        }
    }

    private static MeshData GeneratePlane(Vector2 size)
    {
        Vector2 half = Vector2.Max(Vector2.Abs(size) * 0.5f, new Vector2(0.001f));
        var vertices = new[]
        {
            new Vertex { Position = new Vector3(-half.X, 0, -half.Y), Normal = Vector3.UnitY, TexCoord = new Vector2(0, 0) },
            new Vertex { Position = new Vector3(half.X, 0, -half.Y), Normal = Vector3.UnitY, TexCoord = new Vector2(1, 0) },
            new Vertex { Position = new Vector3(half.X, 0, half.Y), Normal = Vector3.UnitY, TexCoord = new Vector2(1, 1) },
            new Vertex { Position = new Vector3(-half.X, 0, half.Y), Normal = Vector3.UnitY, TexCoord = new Vector2(0, 1) }
        };
        return new MeshData(vertices, [0, 2, 1, 0, 3, 2]);
    }

    private static MeshData GenerateCylinder(float radius, float depth, int segments)
    {
        radius = MathF.Max(0.001f, MathF.Abs(radius));
        depth = MathF.Max(0.001f, MathF.Abs(depth));
        var vertices = new List<Vertex>();
        var indices = new List<uint>();
        float half = depth * 0.5f;
        for (int i = 0; i < segments; i++)
        {
            float a = i * MathF.Tau / segments;
            float x = MathF.Cos(a) * radius;
            float z = MathF.Sin(a) * radius;
            Vector3 normal = Vector3.Normalize(new Vector3(x, 0, z));
            vertices.Add(new Vertex { Position = new Vector3(x, -half, z), Normal = normal, TexCoord = new Vector2((float)i / segments, 0) });
            vertices.Add(new Vertex { Position = new Vector3(x, half, z), Normal = normal, TexCoord = new Vector2((float)i / segments, 1) });
        }
        uint bottomCenter = (uint)vertices.Count;
        vertices.Add(new Vertex { Position = new Vector3(0, -half, 0), Normal = -Vector3.UnitY, TexCoord = new Vector2(0.5f) });
        uint topCenter = (uint)vertices.Count;
        vertices.Add(new Vertex { Position = new Vector3(0, half, 0), Normal = Vector3.UnitY, TexCoord = new Vector2(0.5f) });
        for (int i = 0; i < segments; i++)
        {
            int next = (i + 1) % segments;
            uint b = (uint)(i * 2), t = b + 1, nb = (uint)(next * 2), nt = nb + 1;
            indices.Add(b); indices.Add(nb); indices.Add(nt);
            indices.Add(b); indices.Add(nt); indices.Add(t);
            indices.Add(bottomCenter); indices.Add(nb); indices.Add(b);
            indices.Add(topCenter); indices.Add(t); indices.Add(nt);
        }
        return new MeshData(vertices.ToArray(), indices.ToArray());
    }

    private static MeshData Transform(MeshData source, Vector3 translation, Vector3 rotationDegrees, Vector3 scale)
    {
        Vector3 radians = rotationDegrees * (MathF.PI / 180.0f);
        Matrix4x4 matrix = Matrix4x4.CreateScale(scale) *
                           Matrix4x4.CreateFromYawPitchRoll(radians.Y, radians.X, radians.Z) *
                           Matrix4x4.CreateTranslation(translation);
        Matrix4x4 normalMatrix = Matrix4x4.Invert(matrix, out Matrix4x4 inverse)
            ? Matrix4x4.Transpose(inverse) : matrix;
        var vertices = new Vertex[source.Vertices.Length];
        for (int i = 0; i < vertices.Length; i++)
        {
            Vertex vertex = source.Vertices[i];
            vertex.Position = Vector3.Transform(vertex.Position, matrix);
            vertex.Normal = Vector3.Normalize(Vector3.TransformNormal(vertex.Normal, normalMatrix));
            vertex.Tangent = Vector3.Normalize(Vector3.TransformNormal(vertex.Tangent, normalMatrix));
            vertex.Bitangent = Vector3.Normalize(Vector3.TransformNormal(vertex.Bitangent, normalMatrix));
            vertices[i] = vertex;
        }
        return new MeshData(vertices, source.Indices.ToArray(), source.LineIndices.ToArray(), source.Parts.ToArray());
    }

    private static MeshData Merge(MeshData first, MeshData second)
    {
        var vertices = first.Vertices.Concat(second.Vertices).ToArray();
        var indices = first.Indices.Concat(second.Indices.Select(index => index + (uint)first.Vertices.Length)).ToArray();
        var parts = first.Parts.Concat(second.Parts.Select(part => new MeshPart(
            part.IndexOffset + (uint)first.Indices.Length, part.IndexCount, part.MaterialSlot))).ToArray();
        var lines = first.LineIndices.Concat(second.LineIndices.Select(index => index + (uint)first.Vertices.Length)).ToArray();
        return new MeshData(vertices, indices, lines, parts);
    }

    private static MeshData Subdivide(MeshData source)
    {
        var vertices = source.Vertices.ToList();
        var indices = new List<uint>(source.Indices.Length * 4);
        for (int i = 0; i + 2 < source.Indices.Length; i += 3)
        {
            uint ia = source.Indices[i], ib = source.Indices[i + 1], ic = source.Indices[i + 2];
            uint ab = AddMidpoint(ia, ib), bc = AddMidpoint(ib, ic), ca = AddMidpoint(ic, ia);
            indices.AddRange([ia, ab, ca, ab, ib, bc, ca, bc, ic, ab, bc, ca]);
        }
        return new MeshData(vertices.ToArray(), indices.ToArray());

        uint AddMidpoint(uint a, uint b)
        {
            Vertex va = vertices[(int)a], vb = vertices[(int)b];
            Vector3 position = (va.Position + vb.Position) * 0.5f;
            Vector3 normal = Vector3.Normalize(va.Normal + vb.Normal);
            Vector2 uv = (va.TexCoord + vb.TexCoord) * 0.5f;
            vertices.Add(new Vertex { Position = position, Normal = normal, TexCoord = uv });
            return (uint)vertices.Count - 1;
        }
    }

    private static IReadOnlyList<Vector3> DistributePoints(MeshData source, float density)
    {
        int triangleCount = source.Indices.Length / 3;
        int count = System.Math.Clamp((int)MathF.Round(MathF.Max(0.01f, density) * triangleCount), 1, 4096);
        var points = new List<Vector3>(count);
        for (int i = 0; i < count; i++)
        {
            if (triangleCount == 0)
                break;
            int triangle = i % triangleCount;
            Vector3 a = source.Vertices[(int)source.Indices[triangle * 3]].Position;
            Vector3 b = source.Vertices[(int)source.Indices[triangle * 3 + 1]].Position;
            Vector3 c = source.Vertices[(int)source.Indices[triangle * 3 + 2]].Position;
            // Deterministic low-discrepancy barycentric sequence: the same
            // graph always produces the same instances and cache result.
            float u = (i * 0.75487766f) % 1.0f;
            float v = (i * 0.56984029f + 0.37f) % 1.0f;
            if (u + v > 1.0f) { u = 1.0f - u; v = 1.0f - v; }
            points.Add(a + (b - a) * u + (c - a) * v);
        }
        return points;
    }

    private static MeshData InstanceOnPoints(IReadOnlyList<Vector3> points, MeshData instance)
    {
        if (points.Count == 0 || instance.Vertices.Length == 0)
            return EmptyMesh();
        MeshData result = EmptyMesh();
        foreach (Vector3 point in points)
        {
            MeshData placed = Transform(instance, point, Vector3.Zero, Vector3.One);
            result = result.Vertices.Length == 0 ? placed : Merge(result, placed);
        }
        return result;
    }
}

/// <summary>Small path/timestamp cache shared by the editor and game loader.</summary>
public static class GeometryGraphCache
{
    private sealed record Entry(long Stamp, long InputSignature, GeometryEvaluationResult Result);
    private static readonly ConcurrentDictionary<string, Entry> Entries = new(StringComparer.OrdinalIgnoreCase);

    public static bool TryEvaluateFile(string path, MeshData? inputMesh, out GeometryEvaluationResult? result, out string error)
    {
        result = null;
        error = "";
        string fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            error = $"Geometry graph not found: {fullPath}";
            return false;
        }

        long stamp = File.GetLastWriteTimeUtc(fullPath).Ticks;
        long inputSignature = ComputeInputSignature(inputMesh);
        if (Entries.TryGetValue(fullPath, out Entry? cached) &&
            cached.Stamp == stamp && cached.InputSignature == inputSignature)
        {
            result = cached.Result;
            return true;
        }

        try
        {
            GeometryGraphAsset asset = GeometryGraphAsset.Load(fullPath);
            if (!GeometryGraphEvaluator.TryEvaluate(asset, new GeometryEvaluationContext { InputMesh = inputMesh }, out result, out error) || result == null)
                return false;
            Entries[fullPath] = new Entry(stamp, inputSignature, result);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static void Invalidate(string? path = null)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            Entries.Clear();
            return;
        }
        Entries.TryRemove(Path.GetFullPath(path), out _);
    }

    private static long ComputeInputSignature(MeshData? mesh)
    {
        if (mesh == null)
            return 0;
        var hash = new HashCode();
        hash.Add(mesh.Vertices.Length);
        hash.Add(mesh.Indices.Length);
        foreach (Vertex vertex in mesh.Vertices)
        {
            hash.Add(vertex.Position.X);
            hash.Add(vertex.Position.Y);
            hash.Add(vertex.Position.Z);
        }
        foreach (uint index in mesh.Indices)
            hash.Add(index);
        return hash.ToHashCode();
    }
}
