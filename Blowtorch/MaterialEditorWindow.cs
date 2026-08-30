using System.Numerics;
using ImGuiNET;
using Fuse.Core;
using Fuse.Renderer.Materials;

namespace Blowtorch;

public sealed class MaterialEditorWindow : IDisposable
{
    private const float NodeWidth = 190.0f;
    private const float HeaderHeight = 28.0f;
    private const float SocketSpacing = 24.0f;
    private const float MinCanvasZoom = 0.35f;
    private const float MaxCanvasZoom = 2.0f;
    private const float CanvasZoomStep = 1.15f;

    private MaterialAsset? _asset;
    private string _path = "";
    private string _selectedNodeId = "";
    private string _pendingNodeId = "";
    private string _pendingSocket = "";
    private Vector2 _canvasPan = new(40, 40);
    private float _canvasZoom = 1.0f;
    private Vector2 _lastCanvasMin;
    private Vector2 _lastCanvasMax;
    private bool _dirty;
    private string _status = "";
    private MaterialPreviewRenderer? _previewRenderer;
    private MaterialRuntime? _previewMaterial;
    private MaterialPreviewShape _previewShape = MaterialPreviewShape.Cube;
    private float _previewYaw = 0.72f;
    private float _previewPitch = -0.32f;
    private bool _previewDragging;
    private string _previewSignature = "";
    private string _failedPreviewSignature = "";

    public bool IsOpen { get; private set; }
    public string CurrentPath => _path;

    public void Open(string materialPath)
    {
        string fullPath = MaterialRuntime.ResolveAssetPath(materialPath);
        try
        {
            _asset = MaterialAsset.Load(fullPath);
            _path = fullPath;
            _selectedNodeId = _asset.Graph.FindOutput()?.Id ?? "";
            _pendingNodeId = "";
            _pendingSocket = "";
            _dirty = false;
            _status = "";
            DisposePreviewMaterial();
            _previewSignature = "";
            _failedPreviewSignature = "";
            IsOpen = true;
        }
        catch (Exception ex)
        {
            _status = ex.Message;
            Logger.Error($"Material editor: {ex.Message}");
        }
    }

    public void Draw(EditorAssetService assetService, EditorSceneService sceneService)
    {
        if (!IsOpen || _asset == null)
            return;

        ImGui.SetNextWindowSize(new Vector2(1080, 700), ImGuiCond.FirstUseEver);
        bool open = IsOpen;
        if (!ImGui.Begin($"Material Graph - {_asset.Name}##MaterialGraphWindow", ref open, ImGuiWindowFlags.MenuBar))
        {
            IsOpen = open;
            ImGui.End();
            return;
        }
        IsOpen = open;

        DrawMenu(assetService, sceneService);

        float inspectorWidth = 300.0f;
        Vector2 available = ImGui.GetContentRegionAvail();
        ImGui.BeginChild("MaterialGraphCanvas", new Vector2(MathF.Max(200, available.X - inspectorWidth - 8), available.Y), ImGuiChildFlags.Borders,
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        DrawCanvas();
        ImGui.EndChild();

        ImGui.SameLine();
        ImGui.BeginChild("MaterialGraphInspector", new Vector2(inspectorWidth, available.Y), ImGuiChildFlags.Borders);
        DrawInspector(assetService);
        ImGui.EndChild();

        ImGui.End();
    }

    private void DrawMenu(EditorAssetService assetService, EditorSceneService sceneService)
    {
        if (!ImGui.BeginMenuBar())
            return;

        if (ImGui.MenuItem("Save", "Ctrl+S"))
            Save(assetService, sceneService);
        if (ImGui.MenuItem("Reload"))
            Open(_path);

        if (ImGui.BeginMenu("Add Node"))
        {
            MaterialGraph graph = _asset!.Graph;
            foreach (MaterialNodeDefinition definition in MaterialNodeCatalog.Definitions)
            {
                bool alreadyHasOutput = definition.Type == "PBROutput" && graph.FindOutput() != null;
                if (ImGui.MenuItem(definition.DisplayName, "", false, !alreadyHasOutput))
                {
                    Vector2 origin = _lastCanvasMin == Vector2.Zero ? ImGui.GetWindowPos() : _lastCanvasMin;
                    Vector2 position = (ImGui.GetMousePos() - origin - _canvasPan) / _canvasZoom;
                    MaterialGraphNode node = MaterialNodeCatalog.CreateNode(definition.Type, position);
                    graph.Nodes.Add(node);
                    _selectedNodeId = node.Id;
                    _dirty = true;
                }
            }
            ImGui.EndMenu();
        }

        if (ImGui.BeginMenu("View"))
        {
            if (ImGui.MenuItem("Zoom In"))
                ZoomFromMenu(_canvasZoom * CanvasZoomStep);
            if (ImGui.MenuItem("Zoom Out"))
                ZoomFromMenu(_canvasZoom / CanvasZoomStep);
            if (ImGui.MenuItem("Reset Zoom"))
                ZoomFromMenu(1.0f);
            ImGui.Separator();
            ImGui.TextDisabled($"Zoom: {_canvasZoom * 100.0f:0}%");
            ImGui.TextDisabled("Mouse wheel over canvas");
            ImGui.EndMenu();
        }

        ImGui.Separator();
        ImGui.TextUnformatted(_dirty ? "Modified" : "Saved");
        if (!string.IsNullOrEmpty(_status))
        {
            ImGui.Separator();
            ImGui.TextColored(new Vector4(1, 0.45f, 0.25f, 1), _status);
        }

        ImGui.EndMenuBar();
    }

    private void DrawCanvas()
    {
        MaterialGraph graph = _asset!.Graph;
        Vector2 canvasMin = ImGui.GetCursorScreenPos();
        Vector2 canvasMax = canvasMin + ImGui.GetContentRegionAvail();
        _lastCanvasMin = canvasMin;
        _lastCanvasMax = canvasMax;
        Vector2 mouse = ImGui.GetMousePos();
        ImDrawListPtr drawList = ImGui.GetWindowDrawList();

        bool canvasHovered = ImGui.IsWindowHovered();
        float wheel = ImGui.GetIO().MouseWheel;
        if (canvasHovered && MathF.Abs(wheel) > 0.001f)
        {
            float requestedZoom = _canvasZoom * MathF.Pow(CanvasZoomStep, wheel);
            SetCanvasZoom(requestedZoom, mouse, canvasMin);
        }

        DrawGrid(drawList, canvasMin, canvasMax);

        if (canvasHovered && ImGui.IsMouseDragging(ImGuiMouseButton.Middle))
            _canvasPan += ImGui.GetIO().MouseDelta;

        foreach (MaterialGraphLink link in graph.Links)
        {
            MaterialGraphNode? from = graph.FindNode(link.FromNode);
            MaterialGraphNode? to = graph.FindNode(link.ToNode);
            if (from == null || to == null)
                continue;
            Vector2 a = GetOutputPin(from, link.FromSocket, canvasMin);
            Vector2 b = GetInputPin(to, link.ToSocket, canvasMin);
            float curve = MathF.Max(50 * _canvasZoom, MathF.Abs(b.X - a.X) * 0.45f);
            drawList.AddBezierCubic(a, a + new Vector2(curve, 0), b - new Vector2(curve, 0), b,
                ImGui.ColorConvertFloat4ToU32(new Vector4(0.82f, 0.64f, 0.2f, 1)), 2.5f);
        }

        MaterialGraphNode[] nodes = graph.Nodes.ToArray();
        foreach (MaterialGraphNode node in nodes)
            DrawNode(node, canvasMin, mouse, drawList);

        if (!string.IsNullOrEmpty(_pendingNodeId))
        {
            MaterialGraphNode? source = graph.FindNode(_pendingNodeId);
            if (source != null)
            {
                Vector2 a = GetOutputPin(source, _pendingSocket, canvasMin);
                float curve = 60 * _canvasZoom;
                drawList.AddBezierCubic(a, a + new Vector2(curve, 0), mouse - new Vector2(curve, 0), mouse,
                    ImGui.ColorConvertFloat4ToU32(new Vector4(1, 0.75f, 0.2f, 1)), 2.0f);
            }
            if (ImGui.IsMouseClicked(ImGuiMouseButton.Right))
            {
                _pendingNodeId = "";
                _pendingSocket = "";
            }
        }

        if (canvasHovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left) &&
            !nodes.Any(node => IsInsideNode(node, canvasMin, mouse)))
            _selectedNodeId = "";
    }

    private void DrawNode(MaterialGraphNode node, Vector2 canvasMin, Vector2 mouse, ImDrawListPtr drawList)
    {
        MaterialNodeDefinition? definition = MaterialNodeCatalog.Find(node.Type);
        if (definition == null)
            return;

        int rowCount = Math.Max(1, Math.Max(definition.Inputs.Length, definition.Outputs.Length));
        float height = HeaderHeight + 12 + rowCount * SocketSpacing;
        Vector2 min = canvasMin + _canvasPan + node.Position * _canvasZoom;
        Vector2 max = min + new Vector2(NodeWidth, height) * _canvasZoom;
        bool selected = node.Id == _selectedNodeId;
        float rounding = MathF.Max(2.0f, 6.0f * _canvasZoom);
        float fontSize = MathF.Max(7.0f, ImGui.GetFontSize() * _canvasZoom);
        float textScale = fontSize / ImGui.GetFontSize();

        uint bodyColor = ImGui.ColorConvertFloat4ToU32(new Vector4(0.12f, 0.13f, 0.16f, 0.98f));
        uint headerColor = ImGui.ColorConvertFloat4ToU32(node.Type == "PBROutput"
            ? new Vector4(0.42f, 0.16f, 0.12f, 1)
            : new Vector4(0.14f, 0.28f, 0.42f, 1));
        uint borderColor = ImGui.ColorConvertFloat4ToU32(selected
            ? new Vector4(1, 0.58f, 0.16f, 1)
            : new Vector4(0.35f, 0.38f, 0.44f, 1));

        drawList.AddRectFilled(min, max, bodyColor, rounding);
        drawList.AddRectFilled(min, min + new Vector2(NodeWidth, HeaderHeight) * _canvasZoom, headerColor, rounding,
            ImDrawFlags.RoundCornersTop);
        drawList.AddRect(min, max, borderColor, rounding, ImDrawFlags.None,
            (selected ? 2.5f : 1.0f) * MathF.Max(0.65f, _canvasZoom));
        drawList.AddText(ImGui.GetFont(), fontSize, min + new Vector2(10, 6) * _canvasZoom, 0xffffffff, node.Name);

        ImGui.SetCursorScreenPos(min);
        ImGui.InvisibleButton($"node_header_{node.Id}", new Vector2(NodeWidth, HeaderHeight) * _canvasZoom);
        if (ImGui.IsItemClicked())
            _selectedNodeId = node.Id;
        if (ImGui.IsItemActive() && ImGui.IsMouseDragging(ImGuiMouseButton.Left))
        {
            node.Position += ImGui.GetIO().MouseDelta / _canvasZoom;
            _dirty = true;
        }

        float pinHitRadius = MathF.Max(8.0f, 8.0f * _canvasZoom);
        float pinHitRadiusSquared = pinHitRadius * pinHitRadius;

        for (int i = 0; i < definition.Inputs.Length; i++)
        {
            MaterialSocketDefinition socket = definition.Inputs[i];
            Vector2 pin = GetInputPin(node, socket.Name, canvasMin);
            DrawPin(drawList, pin, socket.Type, false, _canvasZoom);
            drawList.AddText(ImGui.GetFont(), fontSize, pin + new Vector2(9, -8) * _canvasZoom,
                0xffd7d7d7, socket.Name);
            if (Vector2.DistanceSquared(mouse, pin) <= pinHitRadiusSquared && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                CompleteLink(node, socket.Name, socket.Type);
        }

        for (int i = 0; i < definition.Outputs.Length; i++)
        {
            MaterialSocketDefinition socket = definition.Outputs[i];
            Vector2 pin = GetOutputPin(node, socket.Name, canvasMin);
            DrawPin(drawList, pin, socket.Type, true, _canvasZoom);
            Vector2 textSize = ImGui.CalcTextSize(socket.Name) * textScale;
            drawList.AddText(ImGui.GetFont(), fontSize,
                pin - new Vector2(textSize.X + 9 * _canvasZoom, 8 * _canvasZoom),
                0xffd7d7d7, socket.Name);
            if (Vector2.DistanceSquared(mouse, pin) <= pinHitRadiusSquared && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            {
                _pendingNodeId = node.Id;
                _pendingSocket = socket.Name;
                _selectedNodeId = node.Id;
            }
        }
    }

    private void CompleteLink(MaterialGraphNode targetNode, string targetSocket, MaterialValueType targetType)
    {
        if (string.IsNullOrEmpty(_pendingNodeId) || _pendingNodeId == targetNode.Id)
            return;
        MaterialGraphNode? sourceNode = _asset!.Graph.FindNode(_pendingNodeId);
        MaterialNodeDefinition? sourceDefinition = sourceNode == null ? null : MaterialNodeCatalog.Find(sourceNode.Type);
        MaterialSocketDefinition? sourceSocket = sourceDefinition?.Outputs.FirstOrDefault(socket => socket.Name == _pendingSocket);
        if (sourceNode == null || sourceSocket == null)
            return;

        bool compatible = sourceSocket.Value.Type == targetType ||
            sourceSocket.Value.Type == MaterialValueType.Float || targetType == MaterialValueType.Float;
        if (!compatible)
        {
            _status = $"Cannot connect {sourceSocket.Value.Type} to {targetType}.";
            return;
        }

        _asset.Graph.Links.RemoveAll(link => link.ToNode == targetNode.Id && link.ToSocket == targetSocket);
        _asset.Graph.Links.Add(new MaterialGraphLink
        {
            FromNode = sourceNode.Id,
            FromSocket = _pendingSocket,
            ToNode = targetNode.Id,
            ToSocket = targetSocket
        });
        _pendingNodeId = "";
        _pendingSocket = "";
        _dirty = true;
        _status = "";
    }

    private void DrawInspector(EditorAssetService assetService)
    {
        DrawPreview(assetService);
        ImGui.TextUnformatted("Material Settings");
        ImGui.Separator();
        string name = _asset!.Name;
        if (ImGui.InputText("Name", ref name, 128))
        {
            _asset.Name = name;
            _dirty = true;
        }

        int alphaMode = (int)_asset.AlphaMode;
        if (ImGui.Combo("Alpha Mode", ref alphaMode, ["Opaque", "Mask", "Blend"], 3))
        {
            _asset.AlphaMode = (MaterialAlphaMode)alphaMode;
            _dirty = true;
        }
        if (_asset.AlphaMode == MaterialAlphaMode.Mask)
        {
            float cutoff = _asset.AlphaCutoff;
            if (ImGui.SliderFloat("Alpha Cutoff", ref cutoff, 0, 1))
            {
                _asset.AlphaCutoff = cutoff;
                _dirty = true;
            }
        }

        bool twoSided = _asset.TwoSided;
        if (ImGui.Checkbox("Two Sided", ref twoSided)) { _asset.TwoSided = twoSided; _dirty = true; }
        bool castShadows = _asset.CastShadows;
        if (ImGui.Checkbox("Cast Shadows", ref castShadows)) { _asset.CastShadows = castShadows; _dirty = true; }
        bool receiveShadows = _asset.ReceiveShadows;
        if (ImGui.Checkbox("Receive Shadows", ref receiveShadows)) { _asset.ReceiveShadows = receiveShadows; _dirty = true; }

        ImGui.Spacing();
        ImGui.TextUnformatted("Selected Node");
        ImGui.Separator();
        MaterialGraphNode? node = _asset.Graph.FindNode(_selectedNodeId);
        if (node == null)
        {
            ImGui.TextDisabled("Select a node on the canvas.");
            return;
        }

        string nodeName = node.Name;
        if (ImGui.InputText("Label", ref nodeName, 96))
        {
            node.Name = nodeName;
            _dirty = true;
        }
        ImGui.TextDisabled(node.Type);
        DrawNodeProperties(node, assetService);

        MaterialGraphLink[] links = _asset.Graph.Links
            .Where(link => link.FromNode == node.Id || link.ToNode == node.Id)
            .ToArray();
        if (links.Length > 0)
        {
            ImGui.Spacing();
            ImGui.TextUnformatted("Connections");
            ImGui.Separator();
            foreach (MaterialGraphLink link in links)
            {
                MaterialGraphNode? from = _asset.Graph.FindNode(link.FromNode);
                MaterialGraphNode? to = _asset.Graph.FindNode(link.ToNode);
                ImGui.PushID($"unlink_{link.FromNode}_{link.FromSocket}_{link.ToNode}_{link.ToSocket}");
                if (ImGui.SmallButton("X"))
                {
                    _asset.Graph.Links.Remove(link);
                    _dirty = true;
                    ImGui.PopID();
                    break;
                }
                ImGui.SameLine();
                ImGui.TextWrapped($"{from?.Name ?? "?"}.{link.FromSocket} -> {to?.Name ?? "?"}.{link.ToSocket}");
                ImGui.PopID();
            }
        }

        if (node.Type != "PBROutput")
        {
            ImGui.Spacing();
            if (ImGui.Button("Delete Node", new Vector2(-1, 0)))
            {
                _asset.Graph.Links.RemoveAll(link => link.FromNode == node.Id || link.ToNode == node.Id);
                _asset.Graph.Nodes.Remove(node);
                _selectedNodeId = "";
                _dirty = true;
            }
        }
    }

    private void DrawNodeProperties(MaterialGraphNode node, EditorAssetService assetService)
    {
        switch (node.Type)
        {
            case "Texture2D":
            case "ScalarTexture":
            case "PackedMetallicRoughness":
            {
                string path = MaterialAsset.GetString(node.Properties, "path", "");
                if (ImGui.InputText("Texture", ref path, 512))
                {
                    node.Properties["path"] = MaterialAsset.NormalizeAssetPath(path);
                    _dirty = true;
                }
                if (ImGui.BeginCombo("Browse", string.IsNullOrEmpty(path) ? "Select texture..." : Path.GetFileName(path)))
                {
                    foreach (string texture in assetService.EnumerateTextures())
                    {
                        if (ImGui.Selectable(texture, texture.Equals(path, StringComparison.OrdinalIgnoreCase)))
                        {
                            node.Properties["path"] = texture;
                            _dirty = true;
                        }
                    }
                    ImGui.EndCombo();
                }
                string defaultSpace = node.Type == "Texture2D" ? "sRGB" : "linear";
                string colorSpace = MaterialAsset.GetString(node.Properties, "color_space", defaultSpace);
                if (ImGui.BeginCombo("Color Space", colorSpace))
                {
                    foreach (string option in new[] { "sRGB", "linear" })
                    {
                        if (ImGui.Selectable(option, option.Equals(colorSpace, StringComparison.OrdinalIgnoreCase)))
                        {
                            node.Properties["color_space"] = option;
                            _dirty = true;
                        }
                    }
                    ImGui.EndCombo();
                }
                break;
            }
            case "Color":
            case "Vector3":
            {
                Vector3 value = MaterialAsset.GetVector3(node.Properties, "value", node.Type == "Color" ? Vector3.One : Vector3.Zero);
                bool changed = node.Type == "Color"
                    ? ImGui.ColorEdit3("Value", ref value)
                    : ImGui.DragFloat3("Value", ref value, 0.01f);
                if (changed)
                {
                    node.Properties["value"] = MaterialAsset.Vec3ToJson(value);
                    _dirty = true;
                }
                break;
            }
            case "Float":
            {
                float value = MaterialAsset.GetFloat(node.Properties, "value", 0.5f);
                if (ImGui.DragFloat("Value", ref value, 0.01f))
                {
                    node.Properties["value"] = value;
                    _dirty = true;
                }
                break;
            }
            case "NormalMap":
            {
                float strength = MaterialAsset.GetFloat(node.Properties, "strength", 1.0f);
                if (ImGui.SliderFloat("Strength", ref strength, 0, 2))
                {
                    node.Properties["strength"] = strength;
                    _dirty = true;
                }
                break;
            }
            case "PBROutput":
                DrawOutputDefaults(node);
                break;
        }
    }

    private void DrawOutputDefaults(MaterialGraphNode node)
    {
        Vector3 baseColor = MaterialAsset.GetVector3(node.Properties, "base_color", Vector3.One);
        if (ImGui.ColorEdit3("Base Color", ref baseColor)) { node.Properties["base_color"] = MaterialAsset.Vec3ToJson(baseColor); _dirty = true; }
        float roughness = MaterialAsset.GetFloat(node.Properties, "roughness", 0.5f);
        if (ImGui.SliderFloat("Roughness", ref roughness, 0.02f, 1)) { node.Properties["roughness"] = roughness; _dirty = true; }
        float metallic = MaterialAsset.GetFloat(node.Properties, "metallic", 0);
        if (ImGui.SliderFloat("Metallic", ref metallic, 0, 1)) { node.Properties["metallic"] = metallic; _dirty = true; }
        Vector3 emission = MaterialAsset.GetVector3(node.Properties, "emission", Vector3.Zero);
        if (ImGui.ColorEdit3("Emission", ref emission)) { node.Properties["emission"] = MaterialAsset.Vec3ToJson(emission); _dirty = true; }
        float alpha = MaterialAsset.GetFloat(node.Properties, "alpha", 1);
        if (ImGui.SliderFloat("Alpha", ref alpha, 0, 1)) { node.Properties["alpha"] = alpha; _dirty = true; }
        float ao = MaterialAsset.GetFloat(node.Properties, "ao", 1);
        if (ImGui.SliderFloat("AO", ref ao, 0, 1)) { node.Properties["ao"] = ao; _dirty = true; }
    }

    private void DrawPreview(EditorAssetService assetService)
    {
        ImGui.TextUnformatted("3D Preview");
        int previewShape = (int)_previewShape;
        if (ImGui.Combo("Preview Shape", ref previewShape, ["Cube", "Sphere"], 2))
            _previewShape = (MaterialPreviewShape)previewShape;

        float size = float.Clamp(ImGui.GetContentRegionAvail().X, 150.0f, 268.0f);
        try
        {
            EnsurePreviewMaterial(assetService);
            if (_previewMaterial != null)
            {
                _previewRenderer ??= new MaterialPreviewRenderer(assetService.AssetManager.Gl, assetService.AssetManager, assetService.ImageBasedLighting);
                _previewRenderer.Render(
                    _previewMaterial,
                    _previewShape,
                    (int)size,
                    (int)size,
                    _previewYaw,
                    _previewPitch);
                ImGui.Image((IntPtr)_previewRenderer.ColorTexture, new Vector2(size, size),
                    new Vector2(0, 1), new Vector2(1, 0));

                // Rotate only while dragging the rendered preview image. This
                // is intentionally separate from the material graph canvas.
                if (ImGui.IsItemHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                    _previewDragging = true;
                if (!ImGui.IsMouseDown(ImGuiMouseButton.Left))
                    _previewDragging = false;
                if (_previewDragging)
                {
                    Vector2 mouseDelta = ImGui.GetIO().MouseDelta;
                    _previewYaw += mouseDelta.X * 0.01f;
                    _previewPitch = float.Clamp(
                        _previewPitch + mouseDelta.Y * 0.01f,
                        -1.45f,
                        1.45f);
                }
            }
            else
            {
                ImGui.Dummy(new Vector2(size, size));
                ImGui.TextDisabled("Preview unavailable. Fix the graph error first.");
            }
        }
        catch (Exception ex)
        {
            _status = $"Preview: {ex.Message}";
            ImGui.Dummy(new Vector2(size, size));
            ImGui.TextWrapped(_status);
        }

        ImGui.TextDisabled("Updates automatically after graph edits.");
        ImGui.Separator();
    }

    private void EnsurePreviewMaterial(EditorAssetService assetService)
    {
        string signature = _asset!.ToJson().ToJsonString();
        if (signature == _previewSignature || signature == _failedPreviewSignature)
            return;

        // Do not create/destroy OpenGL programs every frame while dragging a
        // node. The new preview is compiled as soon as the mouse is released.
        if (ImGui.IsMouseDown(ImGuiMouseButton.Left))
            return;

        try
        {
            MaterialRuntime replacement = MaterialRuntime.CreateInMemory(
                assetService.AssetManager, _asset, "preview://" + _path);
            DisposePreviewMaterial();
            _previewMaterial = replacement;
            _previewSignature = signature;
            _failedPreviewSignature = "";
        }
        catch
        {
            _failedPreviewSignature = signature;
            throw;
        }
    }

    private void DisposePreviewMaterial()
    {
        _previewMaterial?.Dispose();
        _previewMaterial = null;
    }

    private void Save(EditorAssetService assetService, EditorSceneService sceneService)
    {
        string? previousFile = File.Exists(_path) ? File.ReadAllText(_path) : null;
        try
        {
            _asset!.Save(_path);
            assetService.ReloadMaterial(_path);
            sceneService.RefreshMaterials(assetService);
            _dirty = false;
            _status = "Saved and recompiled.";
        }
        catch (Exception ex)
        {
            if (previousFile != null)
                File.WriteAllText(_path, previousFile);
            _status = ex.Message;
            Logger.Error($"Material save failed: {ex.Message}");
        }
    }

    private Vector2 GetInputPin(MaterialGraphNode node, string socket, Vector2 canvasMin)
    {
        MaterialNodeDefinition? definition = MaterialNodeCatalog.Find(node.Type);
        int index = definition == null ? 0 : Array.FindIndex(definition.Inputs, input => input.Name == socket);
        return canvasMin + _canvasPan +
            (node.Position + new Vector2(0, HeaderHeight + 20 + Math.Max(index, 0) * SocketSpacing)) * _canvasZoom;
    }

    private Vector2 GetOutputPin(MaterialGraphNode node, string socket, Vector2 canvasMin)
    {
        MaterialNodeDefinition? definition = MaterialNodeCatalog.Find(node.Type);
        int index = definition == null ? 0 : Array.FindIndex(definition.Outputs, output => output.Name == socket);
        return canvasMin + _canvasPan +
            (node.Position + new Vector2(NodeWidth, HeaderHeight + 20 + Math.Max(index, 0) * SocketSpacing)) * _canvasZoom;
    }

    private static void DrawPin(ImDrawListPtr drawList, Vector2 position, MaterialValueType type, bool output, float zoom)
    {
        Vector4 color = type switch
        {
            MaterialValueType.Float => new Vector4(0.72f, 0.72f, 0.72f, 1),
            MaterialValueType.Vector2 => new Vector4(0.3f, 0.75f, 0.95f, 1),
            _ => new Vector4(0.95f, 0.72f, 0.2f, 1)
        };
        float radius = MathF.Max(2.5f, (output ? 5.5f : 5.0f) * zoom);
        drawList.AddCircleFilled(position, radius, ImGui.ColorConvertFloat4ToU32(color));
    }

    private bool IsInsideNode(MaterialGraphNode node, Vector2 canvasMin, Vector2 mouse)
    {
        MaterialNodeDefinition? definition = MaterialNodeCatalog.Find(node.Type);
        int rows = Math.Max(1, Math.Max(definition?.Inputs.Length ?? 0, definition?.Outputs.Length ?? 0));
        Vector2 min = canvasMin + _canvasPan + node.Position * _canvasZoom;
        Vector2 max = min + new Vector2(NodeWidth, HeaderHeight + 12 + rows * SocketSpacing) * _canvasZoom;
        return mouse.X >= min.X && mouse.X <= max.X && mouse.Y >= min.Y && mouse.Y <= max.Y;
    }

    private void DrawGrid(ImDrawListPtr drawList, Vector2 min, Vector2 max)
    {
        float grid = 32.0f * _canvasZoom;
        uint color = ImGui.ColorConvertFloat4ToU32(new Vector4(0.24f, 0.25f, 0.28f, 0.5f));
        float xOffset = _canvasPan.X % grid;
        float yOffset = _canvasPan.Y % grid;
        for (float x = min.X + xOffset; x < max.X; x += grid)
            drawList.AddLine(new Vector2(x, min.Y), new Vector2(x, max.Y), color);
        for (float y = min.Y + yOffset; y < max.Y; y += grid)
            drawList.AddLine(new Vector2(min.X, y), new Vector2(max.X, y), color);
    }

    private void ZoomFromMenu(float requestedZoom)
    {
        if (_lastCanvasMax.X <= _lastCanvasMin.X || _lastCanvasMax.Y <= _lastCanvasMin.Y)
        {
            _canvasZoom = Math.Clamp(requestedZoom, MinCanvasZoom, MaxCanvasZoom);
            return;
        }

        Vector2 focus = (_lastCanvasMin + _lastCanvasMax) * 0.5f;
        SetCanvasZoom(requestedZoom, focus, _lastCanvasMin);
    }

    private void SetCanvasZoom(float requestedZoom, Vector2 focus, Vector2 canvasMin)
    {
        float newZoom = Math.Clamp(requestedZoom, MinCanvasZoom, MaxCanvasZoom);
        if (MathF.Abs(newZoom - _canvasZoom) < 0.0001f)
            return;

        Vector2 focusInCanvas = focus - canvasMin;
        Vector2 graphPointAtFocus = (focusInCanvas - _canvasPan) / _canvasZoom;
        _canvasPan = focusInCanvas - graphPointAtFocus * newZoom;
        _canvasZoom = newZoom;
    }

    public void Dispose()
    {
        DisposePreviewMaterial();
        _previewRenderer?.Dispose();
        _previewRenderer = null;
    }
}
