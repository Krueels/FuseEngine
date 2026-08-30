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
    private readonly HashSet<string> _selectedNodeIds = [];
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
    private string _materialFilter = "";
    private Vector2 _contextMenuPosition;
    private string _hoveredNodeId = "";
    private string _contextNodeId = "";
    private bool _draggingNodes;
    private Vector2 _nodeDragLastMouse;
    private bool _marqueeSelecting;
    private bool _marqueeAdditive;
    private Vector2 _marqueeStart;
    private Vector2 _marqueeCurrent;

    public bool IsOpen { get; private set; }
    public string CurrentPath => _path;
    public bool IsInputContextActive { get; private set; }

    public void OpenStandalone()
    {
        IsOpen = true;
        IsInputContextActive = false;
        _status = "Select a material from the gallery.";
    }

    public void Open(string materialPath)
    {
        string fullPath = MaterialRuntime.ResolveAssetPath(materialPath);
        try
        {
            _asset = MaterialAsset.Load(fullPath);
            _path = fullPath;
            _selectedNodeId = _asset.Graph.FindOutput()?.Id ?? "";
            _selectedNodeIds.Clear();
            if (!string.IsNullOrEmpty(_selectedNodeId))
                _selectedNodeIds.Add(_selectedNodeId);
            _pendingNodeId = "";
            _pendingSocket = "";
            _dirty = false;
            _status = "";
            _canvasPan = new Vector2(40, 40);
            _canvasZoom = 1.0f;
            _contextNodeId = "";
            _marqueeSelecting = false;
            _draggingNodes = false;
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

    public void Draw(
        EditorAssetService assetService,
        EditorSceneService sceneService,
        EditorInputService inputService)
    {
        IsInputContextActive = false;
        if (!IsOpen)
            return;

        ImGui.SetNextWindowSize(new Vector2(1180, 700), ImGuiCond.FirstUseEver);
        bool open = IsOpen;
        string title = _asset == null ? "Material Graph" : $"Material Graph - {_asset.Name}";
        if (!ImGui.Begin($"{title}##MaterialGraphWindow", ref open, ImGuiWindowFlags.MenuBar))
        {
            IsInputContextActive = ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows);
            if (IsInputContextActive)
                inputService.SetContext(EditorInputContext.MaterialGraph);
            IsOpen = open;
            ImGui.End();
            return;
        }
        IsOpen = open;

        IsInputContextActive = ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows);
        if (IsInputContextActive)
            inputService.SetContext(EditorInputContext.MaterialGraph);

        HandleKeyboardShortcuts(assetService, sceneService);

        DrawMenu(assetService, sceneService);

        Vector2 available = ImGui.GetContentRegionAvail();
        const float galleryWidth = 235.0f;
        ImGui.BeginChild("MaterialGallery", new Vector2(galleryWidth, available.Y), ImGuiChildFlags.Borders);
        DrawMaterialGallery(assetService);
        ImGui.EndChild();

        ImGui.SameLine();
        if (_asset == null)
        {
            ImGui.BeginChild("MaterialGraphEmpty", new Vector2(-1, available.Y), ImGuiChildFlags.Borders);
            ImGui.Spacing();
            ImGui.TextUnformatted("Material Graph");
            ImGui.Separator();
            ImGui.TextWrapped("Select a material in the gallery to open and edit its graph.");
            ImGui.EndChild();
        }
        else
        {
            float inspectorWidth = 300.0f;
            ImGui.BeginChild("MaterialGraphCanvas", new Vector2(MathF.Max(200, available.X - galleryWidth - inspectorWidth - 16), available.Y), ImGuiChildFlags.Borders,
                ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
            DrawCanvas();
            ImGui.EndChild();

            ImGui.SameLine();
            ImGui.BeginChild("MaterialGraphInspector", new Vector2(inspectorWidth, available.Y), ImGuiChildFlags.Borders);
            DrawInspector(assetService);
            ImGui.EndChild();
        }

        ImGui.End();
    }

    private void DrawMaterialGallery(EditorAssetService assetService)
    {
        ImGui.TextUnformatted("Materials");
        ImGui.Separator();
        ImGui.InputTextWithHint("##MaterialFilter", "Filter materials...", ref _materialFilter, 128);
        ImGui.Spacing();

        IReadOnlyList<string> materials = assetService.EnumerateMaterials();
        bool any = false;
        foreach (string material in materials)
        {
            string fileName = Path.GetFileNameWithoutExtension(material);
            if (!string.IsNullOrWhiteSpace(_materialFilter) &&
                !material.Contains(_materialFilter, StringComparison.OrdinalIgnoreCase))
                continue;

            any = true;
            bool selected = MaterialRuntime.ResolveAssetPath(material)
                .Equals(_path, StringComparison.OrdinalIgnoreCase);
            if (ImGui.Selectable(fileName, selected))
                Open(material);
            if (selected)
                ImGui.SetItemDefaultFocus();
            ImGui.TextDisabled($"  {material}");
        }

        if (!any)
            ImGui.TextDisabled(materials.Count == 0 ? "No .fmat materials found." : "No materials match the filter.");
    }

    private void DrawMenu(EditorAssetService assetService, EditorSceneService sceneService)
    {
        if (!ImGui.BeginMenuBar())
            return;

        if (ImGui.MenuItem("Save", "Ctrl+S", false, _asset != null))
            Save(assetService, sceneService);
        if (ImGui.MenuItem("Reload", "", false, _asset != null))
            Open(_path);

        if (ImGui.BeginMenu("Add Node", _asset != null))
        {
            DrawAddNodeItems(ImGui.GetMousePos());
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

    private void HandleKeyboardShortcuts(EditorAssetService assetService, EditorSceneService sceneService)
    {
        if (!IsInputContextActive || ImGui.GetIO().WantTextInput)
            return;

        if (ImGui.GetIO().KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.S) && _asset != null)
            Save(assetService, sceneService);

        if (ImGui.GetIO().KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.A) && _asset != null)
            SetSelection(_asset.Graph.Nodes.Select(node => node.Id), false);

        if (ImGui.IsKeyPressed(ImGuiKey.Delete))
            DeleteSelectedNode();

        if (ImGui.IsKeyPressed(ImGuiKey.Escape))
        {
            _pendingNodeId = "";
            _pendingSocket = "";
        }
    }

    private void DrawAddNodeItems(Vector2 screenPosition)
    {
        if (_asset == null)
            return;

        MaterialGraph graph = _asset.Graph;
        foreach (MaterialNodeDefinition definition in MaterialNodeCatalog.Definitions)
        {
            bool alreadyHasOutput = definition.Type == "PBROutput" && graph.FindOutput() != null;
            if (!ImGui.MenuItem(definition.DisplayName, "", false, !alreadyHasOutput))
                continue;

            Vector2 origin = _lastCanvasMin == Vector2.Zero ? ImGui.GetWindowPos() : _lastCanvasMin;
            Vector2 position = (screenPosition - origin - _canvasPan) / _canvasZoom;
            MaterialGraphNode node = MaterialNodeCatalog.CreateNode(definition.Type, position);
            graph.Nodes.Add(node);
            SelectNode(node.Id, false);
            _dirty = true;
        }
    }

    private void SelectNode(string nodeId, bool additive)
    {
        if (string.IsNullOrEmpty(nodeId))
        {
            if (!additive)
            {
                _selectedNodeIds.Clear();
                _selectedNodeId = "";
            }
            return;
        }

        if (!additive)
        {
            _selectedNodeIds.Clear();
            _selectedNodeIds.Add(nodeId);
            _selectedNodeId = nodeId;
            return;
        }

        if (!_selectedNodeIds.Add(nodeId))
            _selectedNodeIds.Remove(nodeId);

        _selectedNodeId = _selectedNodeIds.Contains(nodeId)
            ? nodeId
            : _selectedNodeIds.LastOrDefault() ?? "";
    }

    private void SetSelection(IEnumerable<string> nodeIds, bool additive)
    {
        if (!additive)
            _selectedNodeIds.Clear();

        foreach (string nodeId in nodeIds)
            _selectedNodeIds.Add(nodeId);

        _selectedNodeId = _selectedNodeIds.LastOrDefault() ?? "";
    }

    private void DeleteSelectedNode()
    {
        if (_asset == null || _selectedNodeIds.Count == 0)
            return;

        MaterialGraphNode[] nodesToDelete = _asset.Graph.Nodes
            .Where(node => _selectedNodeIds.Contains(node.Id) && node.Type != "PBROutput")
            .ToArray();
        if (nodesToDelete.Length == 0)
            return;

        HashSet<string> deletedIds = nodesToDelete.Select(node => node.Id).ToHashSet();
        _asset.Graph.Links.RemoveAll(link => deletedIds.Contains(link.FromNode) || deletedIds.Contains(link.ToNode));
        foreach (MaterialGraphNode node in nodesToDelete)
            _asset.Graph.Nodes.Remove(node);

        _selectedNodeIds.ExceptWith(deletedIds);
        _selectedNodeId = _selectedNodeIds.LastOrDefault() ?? "";
        _dirty = true;
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
        _hoveredNodeId = "";

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

        if (_draggingNodes)
        {
            if (ImGui.IsMouseDown(ImGuiMouseButton.Left))
            {
                Vector2 delta = (mouse - _nodeDragLastMouse) / _canvasZoom;
                if (delta.LengthSquared() > 0.000001f)
                {
                    foreach (MaterialGraphNode selectedNode in graph.Nodes
                                 .Where(selectedNode => _selectedNodeIds.Contains(selectedNode.Id)))
                    {
                        selectedNode.Position += delta;
                    }
                    _dirty = true;
                }
                _nodeDragLastMouse = mouse;
            }
            else
            {
                _draggingNodes = false;
            }
        }

        bool clickedNode = nodes.Any(node => IsInsideNode(node, canvasMin, mouse));
        if (canvasHovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left) &&
            !clickedNode && string.IsNullOrEmpty(_pendingNodeId))
        {
            _marqueeSelecting = true;
            _marqueeStart = mouse;
            _marqueeCurrent = mouse;
            _marqueeAdditive = ImGui.GetIO().KeyCtrl || ImGui.GetIO().KeyShift;
            if (!_marqueeAdditive)
                SelectNode("", false);
        }

        if (_marqueeSelecting)
        {
            _marqueeCurrent = mouse;
            if (ImGui.IsMouseReleased(ImGuiMouseButton.Left))
            {
                Vector2 selectionMin = Vector2.Min(_marqueeStart, _marqueeCurrent);
                Vector2 selectionMax = Vector2.Max(_marqueeStart, _marqueeCurrent);
                IEnumerable<string> selectedIds = nodes
                    .Where(node => IsNodeIntersecting(node, canvasMin, selectionMin, selectionMax))
                    .Select(node => node.Id);
                SetSelection(selectedIds, _marqueeAdditive);
                _marqueeSelecting = false;
            }
        }

        if (_marqueeSelecting)
        {
            Vector2 selectionMin = Vector2.Min(_marqueeStart, _marqueeCurrent);
            Vector2 selectionMax = Vector2.Max(_marqueeStart, _marqueeCurrent);
            uint fillColor = ImGui.ColorConvertFloat4ToU32(new Vector4(0.25f, 0.55f, 0.95f, 0.18f));
            uint borderColor = ImGui.ColorConvertFloat4ToU32(new Vector4(0.35f, 0.7f, 1.0f, 0.9f));
            drawList.AddRectFilled(selectionMin, selectionMax, fillColor);
            drawList.AddRect(selectionMin, selectionMax, borderColor, 0.0f, ImDrawFlags.None, 1.0f);
        }

        if (canvasHovered && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
        {
            _contextMenuPosition = mouse;
            if (!string.IsNullOrEmpty(_hoveredNodeId))
            {
                _contextNodeId = _hoveredNodeId;
                ImGui.OpenPopup("MaterialGraphNodeContext");
            }
            else
            {
                _contextNodeId = "";
                ImGui.OpenPopup("MaterialGraphCanvasContext");
            }
        }

        if (ImGui.BeginPopup("MaterialGraphNodeContext"))
        {
            MaterialGraphNode? contextNode = graph.FindNode(_contextNodeId);
            if (contextNode != null)
            {
                if (ImGui.MenuItem("Select"))
                    SelectNode(contextNode.Id, ImGui.GetIO().KeyCtrl || ImGui.GetIO().KeyShift);

                bool canDelete = contextNode.Type != "PBROutput";
                if (ImGui.MenuItem("Delete Node", "Delete", false, canDelete))
                {
                    SelectNode(contextNode.Id, false);
                    DeleteSelectedNode();
                }
            }
            ImGui.EndPopup();
        }

        if (ImGui.BeginPopup("MaterialGraphCanvasContext"))
        {
            if (ImGui.BeginMenu("Add Node"))
            {
                DrawAddNodeItems(_contextMenuPosition);
                ImGui.EndMenu();
            }
            if (ImGui.MenuItem("Reset View"))
            {
                _canvasPan = new Vector2(40, 40);
                _canvasZoom = 1.0f;
            }
            ImGui.EndPopup();
        }

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
        if (mouse.X >= min.X && mouse.X <= max.X && mouse.Y >= min.Y && mouse.Y <= max.Y)
            _hoveredNodeId = node.Id;
        bool selected = _selectedNodeIds.Contains(node.Id);
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
        ImGui.InvisibleButton($"node_surface_{node.Id}", new Vector2(NodeWidth, height) * _canvasZoom);
        if (ImGui.IsItemClicked())
        {
            bool additive = ImGui.GetIO().KeyCtrl || ImGui.GetIO().KeyShift;
            bool alreadySelected = _selectedNodeIds.Contains(node.Id);
            if (additive || !alreadySelected)
                SelectNode(node.Id, additive);
            else
                _selectedNodeId = node.Id;

            if (IsInsideAnyPin(node, canvasMin, mouse))
            {
                _draggingNodes = false;
            }
            else
            {
                _draggingNodes = true;
                _nodeDragLastMouse = mouse;
            }
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
            {
                _draggingNodes = false;
                if (string.IsNullOrEmpty(_pendingNodeId))
                    DisconnectInput(node.Id, socket.Name);
                else
                    CompleteLink(node, socket.Name, socket.Type);
            }
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
                _draggingNodes = false;
                _pendingNodeId = node.Id;
                _pendingSocket = socket.Name;
                SelectNode(node.Id, ImGui.GetIO().KeyCtrl || ImGui.GetIO().KeyShift);
            }
        }
    }

    private void DisconnectInput(string targetNodeId, string targetSocket)
    {
        if (_asset == null)
            return;

        int removed = _asset.Graph.Links.RemoveAll(link =>
            link.ToNode == targetNodeId && link.ToSocket == targetSocket);
        if (removed <= 0)
            return;

        _pendingNodeId = "";
        _pendingSocket = "";
        _dirty = true;
        _status = "Connection removed.";
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
        if (_selectedNodeIds.Count > 1)
        {
            ImGui.TextDisabled($"{_selectedNodeIds.Count} nodes selected. Ctrl/Shift-click toggles selection; drag on empty space selects a group.");
            ImGui.Separator();
        }

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
            string deleteLabel = _selectedNodeIds.Count > 1 ? "Delete Selected Nodes" : "Delete Node";
            if (ImGui.Button(deleteLabel, new Vector2(-1, 0)))
                DeleteSelectedNode();
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

    private bool IsNodeIntersecting(
        MaterialGraphNode node,
        Vector2 canvasMin,
        Vector2 selectionMin,
        Vector2 selectionMax)
    {
        MaterialNodeDefinition? definition = MaterialNodeCatalog.Find(node.Type);
        int rows = Math.Max(1, Math.Max(definition?.Inputs.Length ?? 0, definition?.Outputs.Length ?? 0));
        Vector2 nodeMin = canvasMin + _canvasPan + node.Position * _canvasZoom;
        Vector2 nodeMax = nodeMin + new Vector2(NodeWidth, HeaderHeight + 12 + rows * SocketSpacing) * _canvasZoom;
        return nodeMin.X <= selectionMax.X && nodeMax.X >= selectionMin.X &&
               nodeMin.Y <= selectionMax.Y && nodeMax.Y >= selectionMin.Y;
    }

    private bool IsInsideAnyPin(MaterialGraphNode node, Vector2 canvasMin, Vector2 mouse)
    {
        MaterialNodeDefinition? definition = MaterialNodeCatalog.Find(node.Type);
        if (definition == null)
            return false;

        float pinHitRadius = MathF.Max(8.0f, 8.0f * _canvasZoom);
        float pinHitRadiusSquared = pinHitRadius * pinHitRadius;
        foreach (MaterialSocketDefinition socket in definition.Inputs)
        {
            if (Vector2.DistanceSquared(mouse, GetInputPin(node, socket.Name, canvasMin)) <= pinHitRadiusSquared)
                return true;
        }
        foreach (MaterialSocketDefinition socket in definition.Outputs)
        {
            if (Vector2.DistanceSquared(mouse, GetOutputPin(node, socket.Name, canvasMin)) <= pinHitRadiusSquared)
                return true;
        }
        return false;
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
