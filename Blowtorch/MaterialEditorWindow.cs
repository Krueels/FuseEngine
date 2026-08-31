using System.Numerics;
using System.Text.Json.Nodes;
using ImGuiNET;
using Fuse.Core;
using Fuse.Renderer.Materials;

namespace Blowtorch;

public sealed unsafe class MaterialEditorWindow : IDisposable
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
    private int _previewOutputMode;
    private bool _previewDragging;
    private string _previewSignature = "";
    private string _failedPreviewSignature = "";
    private string _materialFilter = "";
    private string _nodeSearch = "";
    private Vector2 _contextMenuPosition;
    private string _hoveredNodeId = "";
    private string _contextNodeId = "";
    private bool _draggingNodes;
    private Vector2 _nodeDragLastMouse;
    private bool _marqueeSelecting;
    private bool _marqueeAdditive;
    private Vector2 _marqueeStart;
    private Vector2 _marqueeCurrent;
    private bool _showUnsavedMaterialDialog;
    private string _pendingMaterialPath = "";
    private enum PendingMaterialAction { None, Open, Reload, Close }
    private PendingMaterialAction _pendingMaterialAction;
    private readonly Dictionary<string, (long Stamp, Vector4 Color)> _materialSwatches = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<MaterialGraphNode> _clipboardNodes = [];
    private readonly List<MaterialGraphLink> _clipboardLinks = [];
    private string _instanceName = "MaterialInstance";
    private bool _showCreateInstanceDialog;
    private string _pendingDeleteMaterialPath = "";
    private bool _showDeleteMaterialDialog;

    public bool IsOpen { get; private set; }
    public string CurrentPath => _path;
    public bool IsInputContextActive { get; private set; }

    public void HandleDeletedMaterial(string materialPath)
    {
        string fullPath = MaterialRuntime.ResolveAssetPath(materialPath);
        if (!_path.Equals(fullPath, StringComparison.OrdinalIgnoreCase))
            return;

        DisposePreviewMaterial();
        _asset = null;
        _path = "";
        _selectedNodeId = "";
        _selectedNodeIds.Clear();
        _dirty = false;
        _previewSignature = "";
        _failedPreviewSignature = "";
        _status = "The open material was moved to the Recycle Bin.";
    }

    public void OpenStandalone()
    {
        IsOpen = true;
        IsInputContextActive = false;
        _status = "Select a material from the gallery.";
    }

    public void Open(string materialPath)
    {
        string fullPath = MaterialRuntime.ResolveAssetPath(materialPath);
        if (_dirty && fullPath.Equals(_path, StringComparison.OrdinalIgnoreCase))
        {
            IsOpen = true;
            return;
        }
        if (_dirty)
        {
            _pendingMaterialAction = PendingMaterialAction.Open;
            _pendingMaterialPath = fullPath;
            _showUnsavedMaterialDialog = true;
            IsOpen = true;
            return;
        }

        OpenImmediate(fullPath);
    }

    public void AssignSelectedTexture(string texturePath)
    {
        if (_asset == null)
            return;

        MaterialGraphNode? node = _asset.Graph.FindNode(_selectedNodeId);
        if (node == null ||
            node.Type is not ("Texture2D" or "ScalarTexture" or "PackedMetallicRoughness"))
            return;

        node.Properties["path"] = MaterialAsset.NormalizeAssetPath(texturePath);
        _dirty = true;
        _status = "Texture selected from Asset Browser.";
    }

    private void OpenImmediate(string materialPath)
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
            _previewOutputMode = 0;
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
        EditorInputService inputService,
        Action? openTextureBrowser = null)
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
            if (!open && _dirty)
            {
                _pendingMaterialAction = PendingMaterialAction.Close;
                _showUnsavedMaterialDialog = true;
                IsOpen = true;
            }
            else
            {
                IsOpen = open;
            }
            ImGui.End();
            DrawUnsavedMaterialDialog(assetService, sceneService);
            DrawMaterialDeleteConfirmation(assetService, sceneService);
            return;
        }
        if (!open && _dirty)
        {
            _pendingMaterialAction = PendingMaterialAction.Close;
            _showUnsavedMaterialDialog = true;
            IsOpen = true;
        }
        else
        {
            IsOpen = open;
        }

        IsInputContextActive = ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows);
        if (IsInputContextActive)
            inputService.SetContext(EditorInputContext.MaterialGraph);

        HandleKeyboardShortcuts(assetService, sceneService);

        DrawMenu(assetService, sceneService);

        Vector2 available = ImGui.GetContentRegionAvail();
        float galleryWidth = Math.Clamp(available.X * 0.20f, 180.0f, 280.0f);
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
            float inspectorWidth = Math.Clamp(available.X * 0.25f, 250.0f, 360.0f);
            ImGui.BeginChild("MaterialGraphCanvas", new Vector2(MathF.Max(200, available.X - galleryWidth - inspectorWidth - 16), available.Y), ImGuiChildFlags.Borders,
                ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
            DrawCanvas(assetService);
            ImGui.EndChild();

            ImGui.SameLine();
            ImGui.BeginChild("MaterialGraphInspector", new Vector2(inspectorWidth, available.Y), ImGuiChildFlags.Borders);
            DrawInspector(assetService, openTextureBrowser);
            ImGui.EndChild();
        }

        ImGui.End();
        DrawUnsavedMaterialDialog(assetService, sceneService);
        DrawCreateInstanceDialog(assetService, sceneService);
        DrawMaterialDeleteConfirmation(assetService, sceneService);
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
            Vector4 swatch = GetMaterialSwatch(material);
            if (ImGui.ColorButton($"##swatch_{material}", swatch,
                    ImGuiColorEditFlags.NoTooltip | ImGuiColorEditFlags.NoDragDrop, new Vector2(22, 22)))
                Open(material);
            ImGui.SameLine();
            if (ImGui.Selectable(fileName, selected, ImGuiSelectableFlags.None, new Vector2(0, 22)))
                Open(material);
            if (ImGui.BeginPopupContextItem($"MaterialContext##{material}"))
            {
                ImGui.TextDisabled(material);
                ImGui.Separator();
                if (ImGui.MenuItem("Delete material..."))
                    RequestMaterialDelete(material);
                ImGui.EndPopup();
            }
            if (selected)
                ImGui.SetItemDefaultFocus();
            ImGui.TextDisabled($"  {material}");
        }

        if (!any)
            ImGui.TextDisabled(materials.Count == 0 ? "No .fmat materials found." : "No materials match the filter.");
    }

    private void RequestMaterialDelete(string materialPath)
    {
        _pendingDeleteMaterialPath = materialPath;
        _showDeleteMaterialDialog = true;
    }

    private void DrawMaterialDeleteConfirmation(EditorAssetService assetService, EditorSceneService sceneService)
    {
        if (!_showDeleteMaterialDialog)
            return;

        ImGui.OpenPopup("Dangerous Material Deletion##MaterialGraph");
        bool popupOpen = true;
        if (ImGui.BeginPopupModal("Dangerous Material Deletion##MaterialGraph", ref popupOpen,
                ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.TextColored(new Vector4(1.0f, 0.28f, 0.22f, 1.0f), "WARNING: dangerous operation");
            ImGui.Spacing();
            ImGui.TextWrapped("This will remove the material from the project and send it to the Windows " +
                              "Recycle Bin. Objects using it may lose their material until the file is restored.");
            ImGui.Spacing();
            ImGui.TextDisabled(_pendingDeleteMaterialPath);
            ImGui.Spacing();
            if (ImGui.Button("Send to Recycle Bin", new Vector2(190, 0)))
            {
                string deletedPath = _pendingDeleteMaterialPath;
                if (assetService.SendAssetToRecycleBin(EditorAssetKind.Material, deletedPath, out string error))
                {
                    HandleDeletedMaterial(deletedPath);
                    _materialSwatches.Remove(MaterialRuntime.ResolveAssetPath(deletedPath));
                    _status = $"Moved {Path.GetFileName(deletedPath)} to the Recycle Bin.";
                    sceneService.RefreshMaterials(assetService);
                }
                else
                {
                    _status = error;
                }

                _showDeleteMaterialDialog = false;
                _pendingDeleteMaterialPath = "";
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if (ImGui.Button("Cancel", new Vector2(100, 0)))
            {
                _showDeleteMaterialDialog = false;
                _pendingDeleteMaterialPath = "";
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndPopup();
        }

        if (!popupOpen)
        {
            _showDeleteMaterialDialog = false;
            _pendingDeleteMaterialPath = "";
        }
    }

    private Vector4 GetMaterialSwatch(string materialPath)
    {
        try
        {
            string fullPath = MaterialRuntime.ResolveAssetPath(materialPath);
            long stamp = File.GetLastWriteTimeUtc(fullPath).Ticks;
            if (_materialSwatches.TryGetValue(fullPath, out var cached) && cached.Stamp == stamp)
                return cached.Color;

            MaterialAsset asset = MaterialAsset.Load(fullPath);
            MaterialGraphNode? output = asset.Graph.FindOutput();
            Vector3 baseColor = output == null
                ? Vector3.One
                : MaterialAsset.GetVector3(output.Properties, "base_color", Vector3.One);
            Vector4 color = new(Vector3.Clamp(baseColor, Vector3.Zero, Vector3.One), 1.0f);
            _materialSwatches[fullPath] = (stamp, color);
            return color;
        }
        catch
        {
            return new Vector4(0.35f, 0.35f, 0.35f, 1.0f);
        }
    }

    private void DrawMenu(EditorAssetService assetService, EditorSceneService sceneService)
    {
        if (!ImGui.BeginMenuBar())
            return;

        if (ImGui.MenuItem("Save", "Ctrl+S", false, _asset != null))
            Save(assetService, sceneService);
        if (ImGui.MenuItem("Reload", "", false, _asset != null))
        {
            if (_dirty)
            {
                _pendingMaterialAction = PendingMaterialAction.Reload;
                _showUnsavedMaterialDialog = true;
            }
            else
            {
                OpenImmediate(_path);
            }
        }
        if (ImGui.MenuItem("Create Material Instance...", "", false, _asset != null))
            CreateMaterialInstance(assetService);

        if (ImGui.BeginMenu("Edit", _asset != null))
        {
            if (ImGui.MenuItem("Copy Nodes", "Ctrl+C", false, _selectedNodeIds.Count > 0))
                CopySelectedNodes();
            if (ImGui.MenuItem("Paste Nodes", "Ctrl+V", false, _clipboardNodes.Count > 0))
                PasteNodes(_contextMenuPosition == Vector2.Zero ? ImGui.GetMousePos() : _contextMenuPosition);
            if (ImGui.MenuItem("Duplicate Nodes", "Ctrl+D", false, _selectedNodeIds.Count > 0))
                DuplicateSelectedNodes();
            ImGui.Separator();
            if (ImGui.MenuItem("Create Frame / Group", "Ctrl+G", false, _selectedNodeIds.Count > 0))
                CreateFrameForSelection();
            if (ImGui.MenuItem("Auto Layout", "Ctrl+L", false, _asset != null))
                AutoLayout();
            ImGui.EndMenu();
        }

        if (ImGui.BeginMenu("Add Node", _asset != null))
        {
            ImGui.InputTextWithHint("##AddNodeSearch", "Search nodes...", ref _nodeSearch, 96);
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
            if (ImGui.MenuItem("Frame All", "Home", false, _asset != null))
                FrameNodes(_asset!.Graph.Nodes);
            if (ImGui.MenuItem("Frame Selected", "F", false, _selectedNodeIds.Count > 0))
                FrameNodes(_asset!.Graph.Nodes.Where(node => _selectedNodeIds.Contains(node.Id)));
            if (ImGui.MenuItem("Auto Layout", "Ctrl+L", false, _asset != null))
                AutoLayout();
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

        if (ImGui.GetIO().KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.C) && _selectedNodeIds.Count > 0)
            CopySelectedNodes();

        if (ImGui.GetIO().KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.V) && _clipboardNodes.Count > 0)
            PasteNodes(ImGui.GetMousePos());

        if (ImGui.GetIO().KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.D) && _selectedNodeIds.Count > 0)
            DuplicateSelectedNodes();

        if (ImGui.GetIO().KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.G) && _selectedNodeIds.Count > 0)
            CreateFrameForSelection();

        if (ImGui.GetIO().KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.L) && _asset != null)
            AutoLayout();

        if (ImGui.IsKeyPressed(ImGuiKey.Home) && _asset != null)
            FrameNodes(_asset.Graph.Nodes);

        if (ImGui.IsKeyPressed(ImGuiKey.F) && _asset != null && _selectedNodeIds.Count > 0)
            FrameNodes(_asset.Graph.Nodes.Where(node => _selectedNodeIds.Contains(node.Id)));

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
            if (!string.IsNullOrWhiteSpace(_nodeSearch) &&
                !definition.DisplayName.Contains(_nodeSearch, StringComparison.OrdinalIgnoreCase) &&
                !definition.Type.Contains(_nodeSearch, StringComparison.OrdinalIgnoreCase))
                continue;
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

    private void CopySelectedNodes()
    {
        if (_asset == null || _selectedNodeIds.Count == 0)
            return;

        _clipboardNodes.Clear();
        _clipboardLinks.Clear();
        _clipboardNodes.AddRange(_asset.Graph.Nodes
            .Where(node => _selectedNodeIds.Contains(node.Id))
            .Select(node => node.Clone()));
        _clipboardLinks.AddRange(_asset.Graph.Links
            .Where(link => _selectedNodeIds.Contains(link.FromNode) && _selectedNodeIds.Contains(link.ToNode))
            .Select(link => link.Clone()));
        _status = $"Copied {_clipboardNodes.Count} node(s).";
    }

    private void PasteNodes(Vector2 screenPosition)
    {
        if (_asset == null || _clipboardNodes.Count == 0)
            return;

        Vector2 origin = _clipboardNodes.Aggregate(Vector2.Zero, (sum, node) => sum + node.Position) / _clipboardNodes.Count;
        Vector2 target = screenPosition == Vector2.Zero || _lastCanvasMin == Vector2.Zero
            ? origin + new Vector2(40, 40)
            : (screenPosition - _lastCanvasMin - _canvasPan) / _canvasZoom;
        Vector2 offset = target - origin;
        var idMap = new Dictionary<string, string>(StringComparer.Ordinal);
        var pasted = new List<MaterialGraphNode>(_clipboardNodes.Count);

        foreach (MaterialGraphNode source in _clipboardNodes)
        {
            MaterialGraphNode node = source.Clone();
            node.Id = Guid.NewGuid().ToString("N");
            node.Position += offset;
            idMap[source.Id] = node.Id;
            pasted.Add(node);
            _asset.Graph.Nodes.Add(node);
        }

        foreach (MaterialGraphLink source in _clipboardLinks)
        {
            if (idMap.TryGetValue(source.FromNode, out string? from) && idMap.TryGetValue(source.ToNode, out string? to))
                _asset.Graph.Links.Add(new MaterialGraphLink
                {
                    FromNode = from,
                    FromSocket = source.FromSocket,
                    ToNode = to,
                    ToSocket = source.ToSocket
                });
        }

        SetSelection(pasted.Select(node => node.Id), false);
        _dirty = true;
        _status = $"Pasted {pasted.Count} node(s).";
    }

    private void DuplicateSelectedNodes()
    {
        CopySelectedNodes();
        if (_clipboardNodes.Count > 0)
            PasteNodes(Vector2.Zero);
    }

    private void CreateFrameForSelection()
    {
        if (_asset == null || _selectedNodeIds.Count == 0)
            return;

        MaterialGraphNode[] selected = _asset.Graph.Nodes
            .Where(node => _selectedNodeIds.Contains(node.Id) && node.Type is not "Frame" and not "Comment")
            .ToArray();
        if (selected.Length == 0)
            return;

        Vector2 min = selected.Select(node => node.Position).Aggregate(Vector2.Min);
        Vector2 max = selected.Select(node => node.Position + GetNodeSize(node)).Aggregate(Vector2.Max);
        const float padding = 28.0f;
        MaterialGraphNode frame = MaterialNodeCatalog.CreateNode("Frame", min - new Vector2(padding));
        frame.Name = MaterialAsset.GetString(frame.Properties, "comment", "Group");
        frame.Properties["width"] = MathF.Max(220, max.X - min.X + padding * 2);
        frame.Properties["height"] = MathF.Max(120, max.Y - min.Y + padding * 2);
        _asset.Graph.Nodes.Insert(0, frame);
        foreach (MaterialGraphNode node in selected)
            node.Properties["frame_id"] = frame.Id;
        SelectNode(frame.Id, false);
        _dirty = true;
        _status = "Frame created around selected nodes.";
    }

    private void AutoLayout()
    {
        if (_asset == null)
            return;

        MaterialGraph graph = _asset.Graph;
        MaterialGraphNode[] nodes = graph.Nodes
            .Where(node => node.Type is not "Frame" and not "Comment")
            .ToArray();
        MaterialGraphNode? output = graph.FindOutput();
        if (nodes.Length == 0 || output == null)
            return;

        var depths = new Dictionary<string, int>(StringComparer.Ordinal) { [output.Id] = 0 };
        var pending = new Queue<string>();
        pending.Enqueue(output.Id);
        while (pending.Count > 0)
        {
            string targetId = pending.Dequeue();
            int targetDepth = depths[targetId];
            foreach (MaterialGraphLink link in graph.Links.Where(candidate => candidate.ToNode == targetId))
            {
                if (depths.ContainsKey(link.FromNode))
                    continue;
                depths[link.FromNode] = targetDepth + 1;
                pending.Enqueue(link.FromNode);
            }
        }

        int fallbackDepth = depths.Values.DefaultIfEmpty(0).Max() + 1;
        foreach (MaterialGraphNode node in nodes)
        {
            int depth = depths.GetValueOrDefault(node.Id, fallbackDepth);
            int index = nodes.Where(candidate =>
                    depths.GetValueOrDefault(candidate.Id, fallbackDepth) == depth)
                .OrderBy(candidate => candidate.Id, StringComparer.Ordinal)
                .ToList().IndexOf(node);
            node.Position = new Vector2(720 - depth * 260, 80 + Math.Max(0, index) * 150);
        }

        _dirty = true;
        _status = "Graph automatically arranged.";
    }

    private void CreateMaterialInstance(EditorAssetService assetService)
    {
        if (_asset == null || string.IsNullOrWhiteSpace(_path))
            return;
        _instanceName = _asset.Name + "_Instance";
        _showCreateInstanceDialog = true;
    }

    private void DrawCreateInstanceDialog(EditorAssetService assetService, EditorSceneService sceneService)
    {
        if (!_showCreateInstanceDialog || _asset == null)
            return;

        ImGui.OpenPopup("Create Material Instance");
        bool open = true;
        if (ImGui.BeginPopupModal("Create Material Instance", ref open, ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.TextUnformatted("Create an editable material instance from the current graph.");
            ImGui.InputText("Name", ref _instanceName, 128);
            ImGui.Separator();
            if (ImGui.Button("Create", new Vector2(110, 0)))
            {
                string safeName = string.Join("_", _instanceName.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries)).Trim();
                if (string.IsNullOrWhiteSpace(safeName))
                    safeName = "MaterialInstance";
                string directory = Path.GetDirectoryName(_path)!;
                string instancePath = Path.Combine(directory, safeName + ".fmat");
                int suffix = 2;
                while (File.Exists(instancePath))
                    instancePath = Path.Combine(directory, $"{safeName}_{suffix++}.fmat");

                MaterialAsset instance = _asset.Clone();
                instance.Name = Path.GetFileNameWithoutExtension(instancePath);
                instance.ParentMaterialPath = MaterialAsset.NormalizeAssetPath(
                    Path.GetRelativePath(assetService.FuseResPath, _path));
                instance.ParameterOverrides.Clear();
                instance.Save(instancePath);
                assetService.ReloadMaterial(instancePath);
                sceneService.RefreshMaterials(assetService);
                _showCreateInstanceDialog = false;
                ImGui.CloseCurrentPopup();
                OpenImmediate(instancePath);
            }
            ImGui.SameLine();
            if (ImGui.Button("Cancel", new Vector2(110, 0)))
            {
                _showCreateInstanceDialog = false;
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndPopup();
        }
        if (!open)
            _showCreateInstanceDialog = false;
    }

    private void DrawCanvas(EditorAssetService assetService)
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

        MaterialGraphNode[] nodes = graph.Nodes.ToArray();
        foreach (MaterialGraphNode frame in nodes.Where(node => node.Type is "Frame" or "Comment"))
            DrawNode(frame, canvasMin, mouse, drawList);

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

        foreach (MaterialGraphNode node in nodes.Where(node => node.Type is not "Frame" and not "Comment"))
            DrawNode(node, canvasMin, mouse, drawList);

        if (_draggingNodes)
        {
            if (ImGui.IsMouseDown(ImGuiMouseButton.Left))
            {
                Vector2 delta = (mouse - _nodeDragLastMouse) / _canvasZoom;
                if (delta.LengthSquared() > 0.000001f)
                {
                    HashSet<string> movingIds = _selectedNodeIds.ToHashSet(StringComparer.Ordinal);
                    foreach (MaterialGraphNode selectedFrame in graph.Nodes.Where(node =>
                                 _selectedNodeIds.Contains(node.Id) && node.Type == "Frame"))
                    {
                        foreach (MaterialGraphNode child in graph.Nodes.Where(node =>
                                     MaterialAsset.GetString(node.Properties, "frame_id", "") == selectedFrame.Id))
                            movingIds.Add(child.Id);
                    }
                    foreach (MaterialGraphNode selectedNode in graph.Nodes.Where(node => movingIds.Contains(node.Id)))
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

                if (ImGui.MenuItem("Copy"))
                {
                    SelectNode(contextNode.Id, false);
                    CopySelectedNodes();
                }
                if (ImGui.MenuItem("Duplicate", "Ctrl+D"))
                {
                    SelectNode(contextNode.Id, false);
                    DuplicateSelectedNodes();
                }
                if (ImGui.MenuItem("Create Frame Around Selection", "Ctrl+G", false,
                        _selectedNodeIds.Count > 0))
                    CreateFrameForSelection();

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
                ImGui.InputTextWithHint("##ContextAddNodeSearch", "Search nodes...", ref _nodeSearch, 96);
                DrawAddNodeItems(_contextMenuPosition);
                ImGui.EndMenu();
            }
            if (ImGui.MenuItem("Paste", "Ctrl+V", false, _clipboardNodes.Count > 0))
                PasteNodes(_contextMenuPosition);
            if (ImGui.MenuItem("Auto Layout", "Ctrl+L", false, _asset != null))
                AutoLayout();
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

        // This target is created only during an active drag. Keeping it out of
        // the normal item stack prevents it from overlapping node click targets.
        var activePayload = ImGui.GetDragDropPayload();
        if (activePayload.NativePtr != null && canvasHovered)
        {
            ImGui.SetCursorScreenPos(canvasMin);
            ImGui.InvisibleButton("MaterialGraphAssetDropTarget", canvasMax - canvasMin);
            if (ImGui.BeginDragDropTarget())
            {
                var payload = ImGui.AcceptDragDropPayload("BLOWTORCH_ASSET");
                if (payload.NativePtr != null &&
                    !string.IsNullOrWhiteSpace(AssetDragDrop.CurrentPath))
                {
                    if (AssetDragDrop.CurrentKind is EditorAssetKind.Texture or EditorAssetKind.Skybox)
                    {
                        Vector2 position = (mouse - canvasMin - _canvasPan) / _canvasZoom;
                        MaterialGraphNode node = MaterialNodeCatalog.CreateNode("Texture2D", position);
                        node.Name = Path.GetFileNameWithoutExtension(AssetDragDrop.CurrentPath);
                        node.Properties["path"] = MaterialAsset.NormalizeAssetPath(AssetDragDrop.CurrentPath);
                        node.Properties["color_space"] = "sRGB";
                        graph.Nodes.Add(node);
                        SelectNode(node.Id, false);
                        _dirty = true;
                        _status = "Texture node added from Asset Browser.";
                    }
                    else if (AssetDragDrop.CurrentKind == EditorAssetKind.Material)
                    {
                        _status = "Double-click a material in Asset Browser to open it.";
                    }
                }
                ImGui.EndDragDropTarget();
            }
        }

    }

    private void DrawNode(MaterialGraphNode node, Vector2 canvasMin, Vector2 mouse, ImDrawListPtr drawList)
    {
        MaterialNodeDefinition? definition = MaterialNodeCatalog.Find(node.Type);
        if (definition == null)
            return;

        Vector2 nodeSize = GetNodeSize(node);
        float height = nodeSize.Y;
        Vector2 min = canvasMin + _canvasPan + node.Position * _canvasZoom;
        Vector2 max = min + nodeSize * _canvasZoom;
        if (mouse.X >= min.X && mouse.X <= max.X && mouse.Y >= min.Y && mouse.Y <= max.Y)
            _hoveredNodeId = node.Id;
        bool selected = _selectedNodeIds.Contains(node.Id);
        float rounding = MathF.Max(2.0f, 6.0f * _canvasZoom);
        float fontSize = MathF.Max(7.0f, ImGui.GetFontSize() * _canvasZoom);
        float textScale = fontSize / ImGui.GetFontSize();

        uint bodyColor = ImGui.ColorConvertFloat4ToU32(new Vector4(0.12f, 0.13f, 0.16f, 0.98f));
        uint headerColor = ImGui.ColorConvertFloat4ToU32(node.Type == "PBROutput"
            ? new Vector4(0.42f, 0.16f, 0.12f, 1)
            : node.Type == "Frame"
                ? new Vector4(0.18f, 0.36f, 0.55f, 0.92f)
                : node.Type == "Comment"
                    ? new Vector4(0.34f, 0.30f, 0.16f, 0.95f)
            : new Vector4(0.14f, 0.28f, 0.42f, 1));
        uint borderColor = ImGui.ColorConvertFloat4ToU32(selected
            ? new Vector4(1, 0.58f, 0.16f, 1)
            : new Vector4(0.35f, 0.38f, 0.44f, 1));

        if (node.Type is "Frame" or "Comment")
        {
            Vector3 frameColor = MaterialAsset.GetVector3(node.Properties, "color",
                node.Type == "Comment" ? new Vector3(0.34f, 0.30f, 0.16f) : new Vector3(0.18f, 0.32f, 0.48f));
            bodyColor = ImGui.ColorConvertFloat4ToU32(new Vector4(frameColor, 0.16f));
        }
        drawList.AddRectFilled(min, max, bodyColor, rounding);
        drawList.AddRectFilled(min, min + new Vector2(NodeWidth, HeaderHeight) * _canvasZoom, headerColor, rounding,
            ImDrawFlags.RoundCornersTop);
        drawList.AddRect(min, max, borderColor, rounding, ImDrawFlags.None,
            (selected ? 2.5f : 1.0f) * MathF.Max(0.65f, _canvasZoom));
        string title = node.Type is "Frame" or "Comment"
            ? MaterialAsset.GetString(node.Properties, "comment", node.Name)
            : node.Name;
        drawList.AddText(ImGui.GetFont(), fontSize, min + new Vector2(10, 6) * _canvasZoom, 0xffffffff, title);

        ImGui.SetCursorScreenPos(min);
        ImGui.InvisibleButton($"node_surface_{node.Id}", nodeSize * _canvasZoom);
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

        if (node.Type is "Frame" or "Comment")
            return;

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

        bool compatible = MaterialGraphValidator.CanConvert(sourceSocket.Value.Type, targetType);
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
        _status = sourceSocket.Value.Type == targetType
            ? ""
            : $"Connected with automatic {sourceSocket.Value.Type} → {targetType} conversion.";
    }

    private void DrawInspector(EditorAssetService assetService, Action? openTextureBrowser)
    {
        DrawPreview(assetService);
        DrawGraphDiagnostics();
        DrawOutputPreviews();
        DrawExposedParameters();
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
        DrawNodeProperties(node, assetService, openTextureBrowser);

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

    private void DrawGraphDiagnostics()
    {
        IReadOnlyList<MaterialGraphDiagnostic> diagnostics = MaterialGraphValidator.Validate(_asset!);
        string label = diagnostics.Any(diagnostic => diagnostic.Severity == MaterialGraphDiagnosticSeverity.Error)
            ? $"Validation ({diagnostics.Count} issue(s))"
            : diagnostics.Count == 0 ? "Validation (OK)" : $"Validation ({diagnostics.Count} warning(s))";
        if (!ImGui.CollapsingHeader(label, ImGuiTreeNodeFlags.DefaultOpen))
            return;

        if (diagnostics.Count == 0)
        {
            ImGui.TextColored(new Vector4(0.35f, 0.9f, 0.5f, 1), "Graph is valid.");
            return;
        }

        foreach (MaterialGraphDiagnostic diagnostic in diagnostics.Take(12))
        {
            Vector4 color = diagnostic.Severity switch
            {
                MaterialGraphDiagnosticSeverity.Error => new Vector4(1, 0.3f, 0.25f, 1),
                MaterialGraphDiagnosticSeverity.Warning => new Vector4(1, 0.75f, 0.25f, 1),
                _ => new Vector4(0.55f, 0.75f, 1, 1)
            };
            ImGui.TextColored(color, $"[{diagnostic.Severity}] {diagnostic.Message}");
        }
        if (diagnostics.Count > 12)
            ImGui.TextDisabled($"...and {diagnostics.Count - 12} more.");
    }

    private void DrawOutputPreviews()
    {
        if (!ImGui.CollapsingHeader("Output Previews", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        MaterialGraphNode? output = _asset!.Graph.FindOutput();
        MaterialNodeDefinition? definition = MaterialNodeCatalog.Find("PBROutput");
        if (output == null || definition == null)
        {
            ImGui.TextDisabled("Add a Material Output node to preview outputs.");
            return;
        }

        foreach (MaterialSocketDefinition socket in definition.Inputs)
        {
            Vector4 color = EvaluateOutputPreview(output, socket.Name, 0);
            MaterialGraphLink? link = _asset.Graph.Links.LastOrDefault(candidate =>
                candidate.ToNode == output.Id && candidate.ToSocket == socket.Name);
            if (ImGui.ColorButton($"##output_preview_{socket.Name}", color,
                ImGuiColorEditFlags.NoTooltip | ImGuiColorEditFlags.NoDragDrop, new Vector2(18, 18)))
            {
                _previewOutputMode = OutputPreviewMode(socket.Name);
            }
            ImGui.SameLine();
            string active = _previewOutputMode == OutputPreviewMode(socket.Name) ? " [previewing]" : "";
            ImGui.TextDisabled($"{socket.Name}: {(link == null ? "default" : "connected")}{active}");
        }
    }

    private static int OutputPreviewMode(string socket) => socket switch
    {
        "BaseColor" => 1,
        "Normal" => 2,
        "Roughness" => 3,
        "Metallic" => 4,
        "Emission" => 5,
        "Alpha" => 6,
        "AO" => 7,
        _ => 0
    };

    private Vector4 EvaluateOutputPreview(MaterialGraphNode target, string socket, int depth)
    {
        if (depth > 24)
            return new Vector4(1, 0, 1, 1);
        MaterialGraphLink? link = _asset!.Graph.Links.LastOrDefault(candidate =>
            candidate.ToNode == target.Id && candidate.ToSocket == socket);
        if (link == null)
        {
            return socket switch
            {
                "BaseColor" => new Vector4(MaterialAsset.GetVector3(target.Properties, "base_color", Vector3.One), 1),
                "Normal" => new Vector4(0.5f, 0.5f, 1, 1),
                "Roughness" => new Vector4(MaterialAsset.GetFloat(target.Properties, "roughness", 0.5f)),
                "Metallic" => new Vector4(MaterialAsset.GetFloat(target.Properties, "metallic", 0)),
                "Emission" => new Vector4(MaterialAsset.GetVector3(target.Properties, "emission", Vector3.Zero), 1),
                "Alpha" => new Vector4(MaterialAsset.GetFloat(target.Properties, "alpha", 1)),
                "AO" => new Vector4(MaterialAsset.GetFloat(target.Properties, "ao", 1)),
                _ => new Vector4(0.5f, 0.5f, 0.5f, 1)
            };
        }

        MaterialGraphNode? source = _asset.Graph.FindNode(link.FromNode);
        if (source == null)
            return new Vector4(1, 0, 1, 1);
        return EvaluateNodePreview(source, link.FromSocket, depth + 1);
    }

    private Vector4 EvaluateNodePreview(MaterialGraphNode node, string socket, int depth)
    {
        if (depth > 24)
            return new Vector4(1, 0, 1, 1);
        switch (node.Type)
        {
            case "Color":
            case "Vector3":
                return new Vector4(MaterialAsset.GetVector3(node.Properties, "value", Vector3.Zero), 1);
            case "Float":
                return new Vector4(MaterialAsset.GetFloat(node.Properties, "value", 0.5f));
            case "Texture2D":
                return new Vector4(0.55f, 0.55f, 0.55f, 1);
            case "NormalMap":
                return new Vector4(0.5f, 0.5f, 1, 1);
            case "Reroute":
            {
                MaterialGraphLink? link = _asset!.Graph.Links.LastOrDefault(candidate =>
                    candidate.ToNode == node.Id && candidate.ToSocket == "Input");
                MaterialGraphNode? source = link == null ? null : _asset.Graph.FindNode(link.FromNode);
                return source == null ? new Vector4(0.5f, 0.5f, 0.5f, 1) : EvaluateNodePreview(source, link!.FromSocket, depth + 1);
            }
            case "Add":
            case "Multiply":
            {
                Vector4 a = EvaluateInputPreview(node, "A", depth + 1);
                Vector4 b = EvaluateInputPreview(node, "B", depth + 1);
                return node.Type == "Add" ? Vector4.Clamp(a + b, Vector4.Zero, Vector4.One) : a * b;
            }
            case "Lerp":
            {
                Vector4 a = EvaluateInputPreview(node, "A", depth + 1);
                Vector4 b = EvaluateInputPreview(node, "B", depth + 1);
                float factor = EvaluateInputPreview(node, "Factor", depth + 1).X;
                return Vector4.Lerp(a, b, Math.Clamp(factor, 0, 1));
            }
            default:
                return new Vector4(0.5f, 0.5f, 0.5f, 1);
        }
    }

    private Vector4 EvaluateInputPreview(MaterialGraphNode node, string socket, int depth)
    {
        MaterialGraphLink? link = _asset!.Graph.Links.LastOrDefault(candidate =>
            candidate.ToNode == node.Id && candidate.ToSocket == socket);
        MaterialGraphNode? source = link == null ? null : _asset.Graph.FindNode(link.FromNode);
        return source == null ? new Vector4(0.5f, 0.5f, 0.5f, 1) : EvaluateNodePreview(source, link!.FromSocket, depth);
    }

    private void DrawExposedParameters()
    {
        _asset!.SyncExposedParameters();
        if (!ImGui.CollapsingHeader("Exposed Parameters", ImGuiTreeNodeFlags.DefaultOpen))
            return;
        if (_asset.ExposedParameters.Count == 0)
        {
            ImGui.TextDisabled("Expose a Color, Vector3, or Float node to create material parameters.");
            return;
        }

        foreach (MaterialExposedParameter parameter in _asset.ExposedParameters)
        {
            MaterialGraphNode? node = _asset.Graph.Nodes.FirstOrDefault(candidate =>
                MaterialAsset.GetBool(candidate.Properties, "expose", false) &&
                MaterialAsset.GetString(candidate.Properties, "parameter_name", "")
                    .Equals(parameter.Name, StringComparison.OrdinalIgnoreCase));
            if (node == null)
                continue;

            JsonNode? value = _asset.GetParameterValue(node);
            bool changed = parameter.Type == MaterialValueType.Float
                ? DrawExposedFloat(parameter.Name, value?.GetValue<float>() ?? 0)
                : DrawExposedVector(parameter.Name, value is JsonArray array && array.Count >= 3
                    ? new Vector3(array[0]!.GetValue<float>(), array[1]!.GetValue<float>(), array[2]!.GetValue<float>())
                    : Vector3.Zero);
            if (!changed)
                continue;

            if (parameter.Type == MaterialValueType.Float)
            {
                float edited = _lastEditedFloat;
                SetExposedValue(node, edited);
            }
            else
            {
                SetExposedValue(node, _lastEditedVector);
            }
        }
    }

    private float _lastEditedFloat;
    private Vector3 _lastEditedVector;

    private bool DrawExposedFloat(string label, float value)
    {
        _lastEditedFloat = value;
        return ImGui.DragFloat(label, ref _lastEditedFloat, 0.01f);
    }

    private bool DrawExposedVector(string label, Vector3 value)
    {
        _lastEditedVector = value;
        return ImGui.ColorEdit3(label, ref _lastEditedVector);
    }

    private void SetExposedValue(MaterialGraphNode node, object value)
    {
        JsonNode jsonValue = value is float scalar
            ? JsonValue.Create(scalar)!
            : MaterialAsset.Vec3ToJson((Vector3)value);
        if (!string.IsNullOrWhiteSpace(_asset!.ParentMaterialPath))
            _asset.ParameterOverrides[MaterialAsset.GetString(node.Properties, "parameter_name", "")] = jsonValue;
        else
            node.Properties["value"] = jsonValue;
        _dirty = true;
    }

    private void DrawNodeProperties(
        MaterialGraphNode node,
        EditorAssetService assetService,
        Action? openTextureBrowser)
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
                if (ImGui.Button("Open Asset Browser"))
                    openTextureBrowser?.Invoke();
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
                DrawParameterExposure(node);
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
                DrawParameterExposure(node);
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
            case "Frame":
            case "Comment":
            {
                string comment = MaterialAsset.GetString(node.Properties, "comment", node.Name);
                if (ImGui.InputText("Comment", ref comment, 256))
                {
                    node.Properties["comment"] = comment;
                    node.Name = comment;
                    _dirty = true;
                }
                float width = MaterialAsset.GetFloat(node.Properties, "width", node.Type == "Frame" ? 360 : 260);
                float height = MaterialAsset.GetFloat(node.Properties, "height", node.Type == "Frame" ? 220 : 84);
                if (ImGui.DragFloat("Width", ref width, 1, 120, 2000)) { node.Properties["width"] = width; _dirty = true; }
                if (ImGui.DragFloat("Height", ref height, 1, 48, 2000)) { node.Properties["height"] = height; _dirty = true; }
                break;
            }
        }
    }

    private void DrawParameterExposure(MaterialGraphNode node)
    {
        bool exposed = MaterialAsset.GetBool(node.Properties, "expose", false);
        if (ImGui.Checkbox("Expose as Material Parameter", ref exposed))
        {
            node.Properties["expose"] = exposed;
            _asset!.SyncExposedParameters();
            _dirty = true;
        }
        if (!exposed)
            return;

        string parameterName = MaterialAsset.GetString(node.Properties, "parameter_name", node.Name);
        if (ImGui.InputText("Parameter Name", ref parameterName, 96))
        {
            node.Properties["parameter_name"] = parameterName;
            _asset!.SyncExposedParameters();
            _dirty = true;
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
                    _previewPitch,
                    _previewOutputMode);
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

    private bool Save(EditorAssetService assetService, EditorSceneService sceneService)
    {
        string? previousFile = null;
        try
        {
            previousFile = File.Exists(_path) ? File.ReadAllText(_path) : null;
            _asset!.Save(_path);
            assetService.ReloadMaterial(_path);
            sceneService.RefreshMaterials(assetService);
            _dirty = false;
            _status = "Saved and recompiled.";
            return true;
        }
        catch (Exception ex)
        {
            try
            {
                if (previousFile != null)
                {
                    File.WriteAllText(_path, previousFile);
                    assetService.ReloadMaterial(_path);
                    sceneService.RefreshMaterials(assetService);
                }
            }
            catch (Exception restoreEx)
            {
                Logger.Error($"Material rollback failed: {restoreEx.Message}");
            }
            _status = ex.Message;
            Logger.Error($"Material save failed: {ex.Message}");
            return false;
        }
    }

    private void DrawUnsavedMaterialDialog(EditorAssetService assetService, EditorSceneService sceneService)
    {
        if (!_showUnsavedMaterialDialog)
            return;

        ImGui.OpenPopup("Unsaved Material Changes");
        bool open = true;
        if (ImGui.BeginPopupModal("Unsaved Material Changes", ref open, ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.TextUnformatted("This material contains unsaved graph changes.");
            ImGui.TextUnformatted("Save them before continuing?");
            ImGui.Separator();
            if (ImGui.Button("Save", new Vector2(105, 0)))
            {
                if (Save(assetService, sceneService))
                    CompletePendingMaterialAction();
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if (ImGui.Button("Discard", new Vector2(105, 0)))
            {
                _dirty = false;
                CompletePendingMaterialAction();
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if (ImGui.Button("Cancel", new Vector2(105, 0)))
            {
                CancelPendingMaterialAction();
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndPopup();
        }

        if (!open)
            CancelPendingMaterialAction();
    }

    private void CompletePendingMaterialAction()
    {
        PendingMaterialAction action = _pendingMaterialAction;
        string path = _pendingMaterialPath;
        CancelPendingMaterialAction();
        switch (action)
        {
            case PendingMaterialAction.Open:
                OpenImmediate(path);
                break;
            case PendingMaterialAction.Reload:
                OpenImmediate(_path);
                break;
            case PendingMaterialAction.Close:
                IsOpen = false;
                IsInputContextActive = false;
                break;
        }
    }

    private void CancelPendingMaterialAction()
    {
        _showUnsavedMaterialDialog = false;
        _pendingMaterialAction = PendingMaterialAction.None;
        _pendingMaterialPath = "";
    }

    private void FrameNodes(IEnumerable<MaterialGraphNode> nodes)
    {
        MaterialGraphNode[] selection = nodes.ToArray();
        Vector2 canvasSize = _lastCanvasMax - _lastCanvasMin;
        if (selection.Length == 0 || canvasSize.X <= 1 || canvasSize.Y <= 1)
            return;

        Vector2 min = new(float.MaxValue);
        Vector2 max = new(float.MinValue);
        foreach (MaterialGraphNode node in selection)
        {
            Vector2 nodeSize = GetNodeSize(node);
            min = Vector2.Min(min, node.Position);
            max = Vector2.Max(max, node.Position + nodeSize);
        }

        Vector2 graphSize = Vector2.Max(max - min, new Vector2(1));
        float zoom = Math.Clamp(MathF.Min((canvasSize.X - 80) / graphSize.X, (canvasSize.Y - 80) / graphSize.Y),
            MinCanvasZoom, MaxCanvasZoom);
        _canvasZoom = zoom;
        _canvasPan = canvasSize * 0.5f - (min + graphSize * 0.5f) * zoom;
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

    private static Vector2 GetNodeSize(MaterialGraphNode node)
    {
        if (node.Type is "Frame" or "Comment")
        {
            float width = MaterialAsset.GetFloat(node.Properties, "width", node.Type == "Frame" ? 360.0f : 260.0f);
            float height = MaterialAsset.GetFloat(node.Properties, "height", node.Type == "Frame" ? 220.0f : 84.0f);
            return new Vector2(MathF.Max(120.0f, width), MathF.Max(48.0f, height));
        }

        MaterialNodeDefinition? definition = MaterialNodeCatalog.Find(node.Type);
        int rows = Math.Max(1, Math.Max(definition?.Inputs.Length ?? 0, definition?.Outputs.Length ?? 0));
        return new Vector2(NodeWidth, HeaderHeight + 12 + rows * SocketSpacing);
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
        Vector2 min = canvasMin + _canvasPan + node.Position * _canvasZoom;
        Vector2 max = min + GetNodeSize(node) * _canvasZoom;
        return mouse.X >= min.X && mouse.X <= max.X && mouse.Y >= min.Y && mouse.Y <= max.Y;
    }

    private bool IsNodeIntersecting(
        MaterialGraphNode node,
        Vector2 canvasMin,
        Vector2 selectionMin,
        Vector2 selectionMax)
    {
        Vector2 nodeMin = canvasMin + _canvasPan + node.Position * _canvasZoom;
        Vector2 nodeMax = nodeMin + GetNodeSize(node) * _canvasZoom;
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
