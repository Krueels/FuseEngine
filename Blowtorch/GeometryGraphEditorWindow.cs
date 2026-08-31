using System.Numerics;
using System.Text.Json.Nodes;
using ImGuiNET;
using Fuse.Core;
using Fuse.Scene.Geometry;

namespace Blowtorch;

/// <summary>
/// Lightweight Geometry Nodes editor. It intentionally shares the interaction
/// model of Material Graph while keeping geometry evaluation independent from
/// ImGui and OpenGL.
/// </summary>
public sealed class GeometryGraphEditorWindow : IDisposable
{
    private const float NodeWidth = 190.0f;
    private const float HeaderHeight = 28.0f;
    private const float RowHeight = 23.0f;
    private GeometryGraphAsset? _asset;
    private string _path = "";
    private string _selectedNodeId = "";
    private Vector2 _pan = new(40, 40);
    private float _zoom = 1.0f;
    private bool _dirty;
    private bool _draggingNode;
    private Vector2 _lastMouse;
    private string _pendingOutputNode = "";
    private Vector2 _contextPosition;
    private string _status = "";
    private string _filter = "";
    private string _livePreviewSignature = "";
    private string _livePreviewPath = "";

    public bool IsOpen { get; private set; }
    public bool IsInputContextActive { get; private set; }
    public string CurrentPath => _path;

    public void OpenStandalone()
    {
        _asset = null;
        _path = "";
        _selectedNodeId = "";
        _dirty = false;
        _livePreviewSignature = "";
        _status = "Select a .fgeo asset from the gallery, or create one.";
        IsOpen = true;
    }

    public void Open(string path)
    {
        string normalized = path.Replace('\\', '/');
        if (normalized.StartsWith("res/", StringComparison.OrdinalIgnoreCase))
            normalized = normalized[4..];
        string fullPath = Path.IsPathRooted(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Combine(Fuse.ResPath.Path, normalized.Replace('/', Path.DirectorySeparatorChar)));
        try
        {
            _asset = GeometryGraphAsset.Load(fullPath);
            _path = fullPath;
            _selectedNodeId = _asset.Graph.FindOutput()?.Id ?? "";
            _dirty = false;
            _livePreviewSignature = "";
            _pan = new Vector2(40, 40);
            _zoom = 1.0f;
            _pendingOutputNode = "";
            _status = "";
            IsOpen = true;
        }
        catch (Exception ex)
        {
            _status = ex.Message;
            Logger.Error($"Geometry graph editor: {ex.Message}");
        }
    }

    public void Draw(EditorAssetService assetService, EditorSceneService sceneService, EditorInputService inputService)
    {
        IsInputContextActive = false;
        if (!IsOpen)
            return;

        if (!string.IsNullOrWhiteSpace(_livePreviewPath) &&
            (string.IsNullOrWhiteSpace(_path) ||
             !string.Equals(
                 Path.GetFullPath(_livePreviewPath),
                 Path.GetFullPath(_path),
                 StringComparison.OrdinalIgnoreCase)))
        {
            assetService.ClearLiveGeometryGraph(_livePreviewPath, sceneService);
            _livePreviewPath = "";
        }

        ImGui.SetNextWindowSize(new Vector2(1120, 700), ImGuiCond.FirstUseEver);
        bool open = IsOpen;
        if (!ImGui.Begin("Geometry Graph##GeometryGraphWindow", ref open, ImGuiWindowFlags.MenuBar))
        {
            IsOpen = open;
            ImGui.End();
            return;
        }
        IsOpen = open;
        if (!open)
        {
            if (!string.IsNullOrWhiteSpace(_path))
                assetService.ClearLiveGeometryGraph(_path, sceneService);
            _livePreviewPath = "";
            ImGui.End();
            return;
        }
        IsInputContextActive = ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows);
        if (IsInputContextActive)
            inputService.SetContext(EditorInputContext.GeometryGraph);

        if (ImGui.BeginMenuBar())
        {
            if (ImGui.MenuItem("New", "Ctrl+N"))
                CreateNew(assetService, sceneService);
            if (ImGui.MenuItem("Save", "Ctrl+S", false, _asset != null))
                Save(assetService, sceneService);
            if (ImGui.MenuItem("Open Asset Browser"))
                _status = "Double-click a .fgeo asset in Asset Browser.";
            ImGui.EndMenuBar();
        }

        HandleShortcuts(assetService, sceneService);
        Vector2 available = ImGui.GetContentRegionAvail();
        float galleryWidth = Math.Clamp(available.X * 0.20f, 180, 270);
        float inspectorWidth = Math.Clamp(available.X * 0.22f, 240, 330);

        ImGui.BeginChild("GeometryGraphGallery", new Vector2(galleryWidth, available.Y), ImGuiChildFlags.Borders);
        DrawGallery(assetService, sceneService);
        ImGui.EndChild();
        ImGui.SameLine();

        if (_asset == null)
        {
            ImGui.BeginChild("GeometryGraphEmpty", new Vector2(-1, available.Y), ImGuiChildFlags.Borders);
            ImGui.TextUnformatted("Geometry Nodes");
            ImGui.Separator();
            ImGui.TextWrapped("Create or select a .fgeo asset to build procedural geometry.");
            ImGui.EndChild();
        }
        else
        {
            ImGui.BeginChild("GeometryGraphCanvas", new Vector2(MathF.Max(220, available.X - galleryWidth - inspectorWidth - 16), available.Y), ImGuiChildFlags.Borders,
                ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
            DrawCanvas();
            ImGui.EndChild();
            ImGui.SameLine();
            ImGui.BeginChild("GeometryGraphInspector", new Vector2(inspectorWidth, available.Y), ImGuiChildFlags.Borders);
            DrawInspector(assetService);
            ImGui.EndChild();
        }
        UpdateLivePreview(assetService, sceneService);
        ImGui.End();
    }

    private void DrawGallery(EditorAssetService assetService, EditorSceneService sceneService)
    {
        ImGui.TextUnformatted("Geometry Graphs");
        ImGui.Separator();
        ImGui.InputTextWithHint("##GeometryFilter", "Filter graphs...", ref _filter, 128);
        foreach (string path in assetService.EnumerateGeometryGraphs()
                     .Where(path => string.IsNullOrEmpty(_filter) || path.Contains(_filter, StringComparison.OrdinalIgnoreCase)))
        {
            bool selected = _path.Equals(assetService.ResolveEditorAssetPath(path), StringComparison.OrdinalIgnoreCase);
            if (ImGui.Selectable(Path.GetFileNameWithoutExtension(path), selected))
                Open(path);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(path);
        }
        if (ImGui.Button("Create Geometry Graph", new Vector2(-1, 0)))
            CreateNew(assetService, sceneService);
        if (!string.IsNullOrWhiteSpace(_status))
        {
            ImGui.Spacing();
            ImGui.TextWrapped(_status);
        }
    }

    private void DrawCanvas()
    {
        GeometryGraph graph = _asset!.Graph;
        Vector2 min = ImGui.GetCursorScreenPos();
        Vector2 max = min + ImGui.GetContentRegionAvail();
        Vector2 mouse = ImGui.GetMousePos();
        ImDrawListPtr draw = ImGui.GetWindowDrawList();
        bool hovered = ImGui.IsWindowHovered();
        if (hovered && MathF.Abs(ImGui.GetIO().MouseWheel) > 0.001f)
            _zoom = Math.Clamp(_zoom * MathF.Pow(1.15f, ImGui.GetIO().MouseWheel), 0.35f, 2.5f);
        if (hovered && ImGui.IsMouseDragging(ImGuiMouseButton.Middle))
            _pan += ImGui.GetIO().MouseDelta;
        DrawGrid(draw, min, max);

        foreach (GeometryGraphLink link in graph.Links)
        {
            GeometryGraphNode? from = graph.FindNode(link.FromNode);
            GeometryGraphNode? to = graph.FindNode(link.ToNode);
            if (from == null || to == null) continue;
            Vector2 a = OutputPin(from, min);
            Vector2 b = InputPin(to, min, link.ToSocket);
            float curve = MathF.Max(45 * _zoom, MathF.Abs(b.X - a.X) * 0.45f);
            draw.AddBezierCubic(a, a + new Vector2(curve, 0), b - new Vector2(curve, 0), b,
                ImGui.ColorConvertFloat4ToU32(new Vector4(0.80f, 0.62f, 0.18f, 1)), 2.5f);
        }

        foreach (GeometryGraphNode node in graph.Nodes.ToArray())
            DrawNode(node, min, mouse, draw);

        if (_draggingNode)
        {
            if (ImGui.IsMouseDown(ImGuiMouseButton.Left))
            {
                GeometryGraphNode? selected = graph.FindNode(_selectedNodeId);
                if (selected != null)
                {
                    selected.Position += (mouse - _lastMouse) / _zoom;
                    _dirty = true;
                }
                _lastMouse = mouse;
            }
            else _draggingNode = false;
        }

        if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
        {
            _contextPosition = mouse;
            ImGui.OpenPopup("GeometryGraphCanvasContext");
        }
        if (ImGui.BeginPopup("GeometryGraphCanvasContext"))
        {
            ImGui.TextUnformatted("Add Node");
            foreach (string nodeType in NodeTypes)
            {
                if (ImGui.MenuItem(nodeType))
                {
                    Vector2 position = (mouse - min - _pan) / _zoom;
                    GeometryGraphNode node = CreateNode(nodeType, position);
                    graph.Nodes.Add(node);
                    _selectedNodeId = node.Id;
                    _dirty = true;
                }
            }
            ImGui.Separator();
            if (ImGui.MenuItem("Reset View")) { _pan = new Vector2(40, 40); _zoom = 1; }
            ImGui.EndPopup();
        }
    }

    private void DrawNode(GeometryGraphNode node, Vector2 canvasMin, Vector2 mouse, ImDrawListPtr draw)
    {
        int inputs = InputSockets(node.Type).Length;
        float height = HeaderHeight + 12 + Math.Max(1, inputs) * RowHeight;
        Vector2 nodeMin = canvasMin + _pan + node.Position * _zoom;
        Vector2 nodeMax = nodeMin + new Vector2(NodeWidth, height) * _zoom;
        bool selected = node.Id.Equals(_selectedNodeId, StringComparison.OrdinalIgnoreCase);
        uint body = ImGui.ColorConvertFloat4ToU32(new Vector4(0.11f, 0.13f, 0.17f, 0.98f));
        uint header = ImGui.ColorConvertFloat4ToU32(node.Type.Equals("GroupOutput", StringComparison.OrdinalIgnoreCase)
            ? new Vector4(0.42f, 0.16f, 0.12f, 1) : new Vector4(0.14f, 0.29f, 0.44f, 1));
        uint border = ImGui.ColorConvertFloat4ToU32(selected
            ? new Vector4(1, 0.58f, 0.16f, 1) : new Vector4(0.35f, 0.40f, 0.47f, 1));
        draw.AddRectFilled(nodeMin, nodeMax, body, 6);
        draw.AddRectFilled(nodeMin, nodeMin + new Vector2(NodeWidth, HeaderHeight) * _zoom, header, 6, ImDrawFlags.RoundCornersTop);
        draw.AddRect(nodeMin, nodeMax, border, 6, ImDrawFlags.None, selected ? 2.5f : 1);
        draw.AddText(nodeMin + new Vector2(10, 6) * _zoom, 0xffffffff, node.Name);

        ImGui.SetCursorScreenPos(nodeMin);
        ImGui.InvisibleButton($"geometry_node_{node.Id}", new Vector2(NodeWidth, height) * _zoom);
        if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
        {
            _selectedNodeId = node.Id;
            _draggingNode = true;
            _lastMouse = mouse;
        }

        string[] inputsForNode = InputSockets(node.Type);
        for (int i = 0; i < inputsForNode.Length; i++)
        {
            Vector2 pin = InputPin(node, canvasMin, inputsForNode[i]);
            draw.AddCircleFilled(pin, MathF.Max(4, 5 * _zoom), 0xffb6b6b6);
            draw.AddText(pin + new Vector2(9, -8) * _zoom, 0xffd7d7d7, inputsForNode[i]);
            if (Vector2.DistanceSquared(mouse, pin) <= MathF.Pow(MathF.Max(9, 9 * _zoom), 2) && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            {
                GeometryGraphLink? existing = _asset!.Graph.Links.LastOrDefault(link => link.ToNode == node.Id && link.ToSocket == inputsForNode[i]);
                if (!string.IsNullOrEmpty(_pendingOutputNode))
                {
                    GeometryGraphNode? source = _asset.Graph.FindNode(_pendingOutputNode);
                    string sourceSocket = OutputSocket(source?.Type ?? "");
                    if (source != null && GeometryGraphNodeCatalog.IsCompatible(source.Type, sourceSocket, node.Type, inputsForNode[i]))
                    {
                        if (existing != null) _asset.Graph.Links.Remove(existing);
                        _asset.Graph.Links.Add(new GeometryGraphLink { FromNode = _pendingOutputNode, FromSocket = sourceSocket, ToNode = node.Id, ToSocket = inputsForNode[i] });
                        _dirty = true;
                    }
                    else
                        _status = $"Incompatible socket: {source?.Type ?? "unknown"} -> {inputsForNode[i]}.";
                    _pendingOutputNode = "";
                }
                else if (existing != null)
                    _asset.Graph.Links.Remove(existing);
            }
        }

        Vector2 outputPin = OutputPin(node, canvasMin);
        draw.AddCircleFilled(outputPin, MathF.Max(4, 5 * _zoom), 0xffe6ae35);
        if (Vector2.DistanceSquared(mouse, outputPin) <= MathF.Pow(MathF.Max(9, 9 * _zoom), 2) && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            _pendingOutputNode = node.Id;
    }

    private static string OutputSocket(string type) =>
        type.Equals("RandomValue", StringComparison.OrdinalIgnoreCase) || type.Equals("Random_Value", StringComparison.OrdinalIgnoreCase)
            ? "Value" : "Geometry";

    private void DrawInspector(EditorAssetService assetService)
    {
        ImGui.TextUnformatted(_asset!.Name);
        ImGui.SameLine();
        ImGui.TextColored(_dirty ? new Vector4(1, 0.72f, 0.25f, 1) : new Vector4(0.35f, 0.9f, 0.5f, 1), _dirty ? "Modified" : "Saved");
        ImGui.TextDisabled(_path);
        ImGui.Separator();
        GeometryGraphNode? node = _asset.Graph.FindNode(_selectedNodeId);
        if (node == null)
        {
            ImGui.TextDisabled("Select a node.");
            return;
        }
        ImGui.TextColored(new Vector4(0.75f, 0.86f, 1, 1), node.Name);
        ImGui.TextDisabled(node.Type);
        ImGui.Separator();
        string propertiesBefore = node.Properties.ToJsonString();
        string name = node.Name;
        if (ImGui.InputText("Label", ref name, 128)) { node.Name = name; _dirty = true; }
        DrawProperty(node, "size", "Size", Vector3.One);
        DrawProperty(node, "translation", "Translation", Vector3.Zero);
        DrawProperty(node, "rotation", "Rotation", Vector3.Zero);
        DrawProperty(node, "scale", "Scale", Vector3.One);
        DrawProperty(node, "radius", "Radius", 0.5f, 0.01f, 100);
        DrawProperty(node, "depth", "Depth", 1.0f, 0.01f, 100);
        DrawProperty(node, "segments", "Segments", 24, 3, 128);
        DrawProperty(node, "density", "Density", 1.0f, 0.01f, 100.0f);
        DrawProperty(node, "min", "Min", 0.0f, -100.0f, 100.0f);
        DrawProperty(node, "max", "Max", 1.0f, -100.0f, 100.0f);
        DrawProperty(node, "seed", "Seed", 1, 0, 100000);
        DrawProperty(node, "level", "Level", 1, 1, 2);
        if (node.Type.Equals("SetMaterial", StringComparison.OrdinalIgnoreCase) || node.Type.Equals("Set Material", StringComparison.OrdinalIgnoreCase) || node.Type.Equals("Set_Material", StringComparison.OrdinalIgnoreCase))
        {
            string material = node.Properties.TryGetPropertyValue("material", out JsonNode? value) ? (string?)value ?? "" : "";
            if (ImGui.InputText("Material", ref material, 256)) { node.Properties["material"] = material; _dirty = true; }
            ImGui.TextDisabled("Use a res-relative .fmat path.");
        }
        if (ImGui.Button("Delete Node", new Vector2(-1, 0)) && !node.Type.Equals("GroupOutput", StringComparison.OrdinalIgnoreCase))
        {
            _asset.Graph.Links.RemoveAll(link => link.FromNode == node.Id || link.ToNode == node.Id);
            _asset.Graph.Nodes.Remove(node);
            _selectedNodeId = _asset.Graph.FindOutput()?.Id ?? "";
            _dirty = true;
        }
        if (!propertiesBefore.Equals(node.Properties.ToJsonString(), StringComparison.Ordinal))
            _dirty = true;
        ImGui.Separator();
        if (GeometryGraphCache.TryEvaluateFile(_path, null, out GeometryEvaluationResult? result, out string error) && result != null)
        {
            ImGui.TextColored(new Vector4(0.35f, 0.9f, 0.5f, 1), $"Preview: {result.Mesh.Vertices.Length} vertices / {result.Mesh.Indices.Length / 3} triangles");
        }
        else
            ImGui.TextColored(new Vector4(1, 0.45f, 0.35f, 1), error);
    }

    private void HandleShortcuts(EditorAssetService assetService, EditorSceneService sceneService)
    {
        if (!IsInputContextActive || ImGui.GetIO().WantTextInput) return;
        if (ImGui.GetIO().KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.S)) Save(assetService, sceneService);
        if (ImGui.IsKeyPressed(ImGuiKey.Delete))
        {
            GeometryGraphNode? node = _asset?.Graph.FindNode(_selectedNodeId);
            if (node != null && !node.Type.Equals("GroupOutput", StringComparison.OrdinalIgnoreCase))
            {
                _asset!.Graph.Nodes.Remove(node);
                _asset.Graph.Links.RemoveAll(link => link.FromNode == node.Id || link.ToNode == node.Id);
                _selectedNodeId = _asset.Graph.FindOutput()?.Id ?? "";
                _dirty = true;
            }
        }
    }

    private void CreateNew(EditorAssetService assetService, EditorSceneService sceneService)
    {
        string name = "GeometryGraph";
        string path = Path.Combine(assetService.FuseResPath, "Geometry", name + ".fgeo");
        int suffix = 1;
        while (File.Exists(path)) path = Path.Combine(assetService.FuseResPath, "Geometry", $"{name}{suffix++}.fgeo");
        _asset = GeometryGraphAsset.CreateDefault(Path.GetFileNameWithoutExtension(path));
        _path = path;
        _selectedNodeId = "group_output";
        _dirty = true;
        Save(assetService, sceneService);
        assetService.RefreshCatalogs();
    }

    private void Save(EditorAssetService assetService, EditorSceneService sceneService)
    {
        if (_asset == null) return;
        if (string.IsNullOrWhiteSpace(_path)) { CreateNew(assetService, sceneService); return; }
        try
        {
            _asset.Save(_path);
            _dirty = false;
            _status = "Saved.";
            assetService.ClearLiveGeometryGraph(_path);
            assetService.RefreshCatalogs();
            _livePreviewSignature = BuildEvaluationSignature();
            _livePreviewPath = "";

            if (sceneService.Document.Objects.Any(obj =>
                    !string.IsNullOrWhiteSpace(obj.GeometryGraphPath) &&
                    string.Equals(
                        Path.GetFullPath(assetService.ResolveEditorAssetPath(obj.GeometryGraphPath!)),
                        Path.GetFullPath(_path),
                        StringComparison.OrdinalIgnoreCase)))
            {
                assetService.InvalidateGeneratedGeometryMeshes();
                sceneService.PopulateScene(assetService);
            }
        }
        catch (Exception ex)
        {
            _status = ex.Message;
            Logger.Error($"Geometry graph save failed: {ex.Message}");
        }
    }

    private void UpdateLivePreview(EditorAssetService assetService, EditorSceneService sceneService)
    {
        if (_asset == null || !_dirty || string.IsNullOrWhiteSpace(_path))
            return;

        string signature = BuildEvaluationSignature();
        if (signature.Equals(_livePreviewSignature, StringComparison.Ordinal))
            return;

        _livePreviewSignature = signature;
        assetService.SetLiveGeometryGraph(_path, _asset, sceneService);
        _livePreviewPath = _path;
    }

    private string BuildEvaluationSignature()
    {
        if (_asset == null)
            return "";

        string nodes = string.Join("|", _asset.Graph.Nodes
            .OrderBy(node => node.Id, StringComparer.OrdinalIgnoreCase)
            .Select(node => $"{node.Id}:{node.Type}:{node.Properties.ToJsonString()}"));
        string links = string.Join("|", _asset.Graph.Links
            .OrderBy(link => link.FromNode, StringComparer.OrdinalIgnoreCase)
            .ThenBy(link => link.ToNode, StringComparer.OrdinalIgnoreCase)
            .ThenBy(link => link.ToSocket, StringComparer.OrdinalIgnoreCase)
            .Select(link => $"{link.FromNode}:{link.FromSocket}>{link.ToNode}:{link.ToSocket}"));
        return $"{nodes}::{links}";
    }

    private static GeometryGraphNode CreateNode(string type, Vector2 position) => new()
    {
        Id = Guid.NewGuid().ToString("N"), Type = type, Name = type, Position = position,
        Properties = type switch
        {
            "Cube" => new JsonObject { ["size"] = new JsonArray(1.0f, 1.0f, 1.0f) },
            "Plane" => new JsonObject { ["size"] = new JsonArray(2.0f, 2.0f) },
            "Cylinder" => new JsonObject { ["radius"] = 0.5f, ["depth"] = 1.0f, ["segments"] = 24 },
            "DistributePointsOnFaces" => new JsonObject { ["density"] = 1.0f },
            "RandomValue" => new JsonObject { ["min"] = 0.0f, ["max"] = 1.0f, ["seed"] = 1 },
            "Transform" => new JsonObject { ["translation"] = new JsonArray(0.0f, 0.0f, 0.0f), ["rotation"] = new JsonArray(0.0f, 0.0f, 0.0f), ["scale"] = new JsonArray(1.0f, 1.0f, 1.0f) },
            "SetMaterial" => new JsonObject { ["material"] = "" },
            _ => new JsonObject()
        }
    };

    private static readonly string[] NodeTypes = ["Cube", "Plane", "Cylinder", "Transform", "Merge", "DistributePointsOnFaces", "InstanceOnPoints", "RandomValue", "SetMaterial", "Subdivide", "Bake", "GroupInput", "GroupOutput"];

    private static string[] InputSockets(string type) => type.Trim().ToLowerInvariant() switch
    {
        "transform" => ["Geometry", "Translation", "Rotation", "Scale"],
        "merge" or "joingeometry" or "join_geometry" => ["A", "B"],
        "distributepointsonfaces" or "distribute_points_on_faces" => ["Mesh", "Density"],
        "instanceonpoints" or "instance_on_points" => ["Points", "Instance"],
        "randomvalue" or "random_value" => ["Min", "Max", "Seed"],
        "setmaterial" or "set_material" => ["Geometry", "Material"],
        "subdivide" => ["Geometry", "Level"],
        "bake" => ["Geometry"],
        "groupoutput" or "group_output" => ["Geometry"],
        _ => []
    };

    private Vector2 InputPin(GeometryGraphNode node, Vector2 canvasMin, string socket)
    {
        int index = Math.Max(0, Array.IndexOf(InputSockets(node.Type), socket));
        return canvasMin + _pan + node.Position * _zoom + new Vector2(0, (HeaderHeight + 12 + RowHeight * (index + 0.5f)) * _zoom);
    }

    private Vector2 OutputPin(GeometryGraphNode node, Vector2 canvasMin)
    {
        int count = Math.Max(1, InputSockets(node.Type).Length);
        float height = HeaderHeight + 12 + count * RowHeight;
        return canvasMin + _pan + node.Position * _zoom + new Vector2(NodeWidth * _zoom, (HeaderHeight + 12 + RowHeight * 0.5f) * _zoom);
    }

    private static void DrawProperty(GeometryGraphNode node, string key, string label, Vector3 fallback)
    {
        Vector3 value = ReadVector3(node, key, fallback);
        if (ImGui.DragFloat3(label, ref value, 0.05f)) node.Properties[key] = new JsonArray(value.X, value.Y, value.Z);
    }

    private static void DrawProperty(GeometryGraphNode node, string key, string label, float fallback, float min, float max)
    {
        float value = node.Properties.TryGetPropertyValue(key, out JsonNode? v) ? (float)v! : fallback;
        if (ImGui.DragFloat(label, ref value, 0.02f, min, max)) node.Properties[key] = value;
    }

    private static void DrawProperty(GeometryGraphNode node, string key, string label, int fallback, int min, int max)
    {
        int value = node.Properties.TryGetPropertyValue(key, out JsonNode? v) ? (int)v! : fallback;
        if (ImGui.DragInt(label, ref value, 1, min, max)) node.Properties[key] = value;
    }

    private static Vector3 ReadVector3(GeometryGraphNode node, string key, Vector3 fallback)
    {
        if (node.Properties.TryGetPropertyValue(key, out JsonNode? value) && value is JsonArray array && array.Count >= 3)
            return new Vector3((float)array[0]!, (float)array[1]!, (float)array[2]!);
        return fallback;
    }

    private static void DrawGrid(ImDrawListPtr draw, Vector2 min, Vector2 max)
    {
        uint color = ImGui.ColorConvertFloat4ToU32(new Vector4(0.18f, 0.20f, 0.24f, 0.55f));
        for (float x = min.X; x < max.X; x += 32) draw.AddLine(new Vector2(x, min.Y), new Vector2(x, max.Y), color);
        for (float y = min.Y; y < max.Y; y += 32) draw.AddLine(new Vector2(min.X, y), new Vector2(max.X, y), color);
    }

    public void Dispose() { }
}
