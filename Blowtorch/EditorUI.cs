using System;
using System.IO;
using System.Numerics;
using System.Linq;
using ImGuiNET;
using Fuse.Scene.Model;
using Fuse.Renderer;
using Fuse.Core;
using Fuse;
using Fuse.Renderer.Materials;
using Fuse.Scene.Terrain;
using Brush = Fuse.Scene.Model.Brush;

namespace Blowtorch;

public unsafe class EditorUI : IDisposable
{
    private const string DefaultMaterialPath = "Materials/Default.fmat";
    private const string DevBrushMaterialPath = "Materials/DevMeasureCrate.fmat";
    private const string ProceduralTerrainPreviewMaterialPath = "Materials/GRASS.fmat";

    private readonly BlowtorchSettings _settings = BlowtorchSettings.Load();
    private bool _showOpenDialog;
    private string[] _availableMaps = [];
    private int _selectedOpenMapIndex = -1;
    private bool _newDocumentRequested;
    private bool _focusCameraRequested;
    private bool _showSaveAsDialog;
    private bool _showHollowDialog;
    private bool _showHitBoxes = false;
    private float _hollowThickness = 0.5f;
    private string _saveMapName = "map.bth";
    private bool _showDiagnostics;
    private string _hierarchyFilter = "";
    private string _documentError = "";
    private bool _showUnsavedChangesDialog;
    private bool _showBlowtorchSettings;
    private string _fuseExecutableDraft = "";
    private string _settingsStatus = "";
    private bool _showOverwriteDialog;
    private string _pendingOpenPath = "";
    private string _pendingSavePath = "";
    private bool _resumePendingActionAfterSave;
    private bool _executePendingDocumentAction;
    private bool _initialMapStatusChecked;
    private string? _previewSkyboxDocumentPath;
    private ulong _previewSkyboxSettingsSignature;
    private bool _cloudPreviewAnimated;

    private enum PendingDocumentAction { None, New, Open, Exit }
    private PendingDocumentAction _pendingDocumentAction;
    private enum ViewportLayout { Quad, PerspectiveOnly }
    private ViewportLayout _viewportLayout = ViewportLayout.Quad;

    private bool _showMapWindow = true;
    private bool _showJsonWindow = false;
    private bool _showAssetBrowser = false;
    private readonly MaterialEditorWindow _materialEditor = new();
    private readonly GeometryGraphEditorWindow _geometryEditor = new();
    private readonly AssetBrowserWindow _assetBrowser = new();
    private bool _newMaterialPopupRequested;
    private string _newMaterialName = "NewMaterial";
    private string _newMaterialTexture = "";
    private readonly List<MapObject> _newMaterialTargets = [];

    // Snapping
    private bool _snapEnabled = true;
    private float _snapGrid = 1.0f;
    public float SnapGrid => _snapGrid;
    private float _snapAngle = 15.0f;

    // Undo/Redo state
    private string _frameBeginState = "";
    public UndoManager Undo { get; } = new UndoManager();

    // Selection & Modes
    public enum EditorMode { Select, DrawBrush, TerrainSculpt }
    public enum GizmoOperation { Translate, Rotate, Scale, Shear }
    private enum BrushComponentMode { Object, Vertex, Edge, Face }
    private enum BrushEditTool { None, Knife }
    
    private EditorMode _currentMode = EditorMode.Select;
    private GizmoOperation _gizmoOperation = GizmoOperation.Translate;
    private MapObject? _selectedObject;
    private MapObject? _draggedObject;
    private List<MapObject> _draggedObjects = [];
    private HashSet<MapObject> _selectedObjects = new();
    private HashSet<string> _lastSelectedObjectIds = new();
    private bool _selectionBoundsCacheValid;
    private bool _selectionBoundsCacheHasBounds;
    private int _selectionBoundsCacheKey;
    private Vector3 _selectionBoundsCacheMin;
    private Vector3 _selectionBoundsCacheMax;
    private double _lastSelectionTime = 0.0;
    private bool _showModelImportDialog = false;
    private bool _showTerrainCreateDialog;
    private bool _terrainCreateWaitingForHeightmap;
    private string _terrainName = "terrain";
    private int _terrainSourceMode;
    private int _terrainWidth = 65;
    private int _terrainDepth = 65;
    private float _terrainCellSize = 1.0f;
    private float _terrainHeightScale = 8.0f;
    private string _terrainHeightmapPath = "";
    private int _terrainChunkQuads = TerrainSceneBuilder.DefaultChunkQuads;
    private int _terrainProceduralSeed = 1337;
    private float _terrainProceduralWorldSizeKm = 80_000.0f;
    private float _terrainProceduralTileSize = 2048.0f;
    private int _terrainProceduralResolution = 65;
    private float _terrainProceduralMinHeight = -512.0f;
    private float _terrainProceduralMaxHeight = 4096.0f;
    private float _terrainProceduralSeaLevel;
    private float _terrainProceduralBaseHeight = 32.0f;
    private float _terrainProceduralContinentalAmplitude = 420.0f;
    private float _terrainProceduralContinentalScale = 0.000004f;
    private int _terrainProceduralContinentalOctaves = 5;
    private float _terrainProceduralNoiseLacunarity = 2.03f;
    private float _terrainProceduralNoiseGain = 0.5f;
    private float _terrainProceduralMountainHeight = 1800.0f;
    private float _terrainProceduralMountainScale = 0.000028f;
    private int _terrainProceduralMountainOctaves = 5;
    private float _terrainProceduralMountainMaskStart = 0.48f;
    private float _terrainProceduralMountainMaskEnd = 0.76f;
    private float _terrainProceduralValleyDepth = 180.0f;
    private float _terrainProceduralValleyScale = 0.000075f;
    private int _terrainProceduralValleyOctaves = 4;
    private float _terrainProceduralDetailHeight = 28.0f;
    private float _terrainProceduralDetailScale = 0.00035f;
    private int _terrainProceduralDetailOctaves = 4;
    private float _terrainProceduralWarpStrength = 0.28f;
    private float _terrainProceduralWarpScale = 0.000018f;
    private int _terrainProceduralWarpOctaves = 3;
    private float _terrainProceduralErosion = 0.32f;
    private float _terrainProceduralRiverDepth;
    private float _terrainProceduralRiverScale = 0.000085f;
    private int _terrainProceduralRiverOctaves = 3;
    private int _terrainProceduralPreviewRadius = 1;
    private int _terrainProceduralStreamingRadius = 2;
    private int _terrainProceduralCollisionRadius = 1;
    private int _terrainProceduralMaxResidentTiles = 25;
    private int _terrainProceduralMaxGenerationTasks = 2;
    private int _terrainProceduralMaxUploadsPerFrame = 1;
    private float _terrainProceduralLodPixelError = 5.0f;
    private bool _terrainProceduralPreviewDirty;
    private bool _terrainProceduralPreviewNeedsRender;
    private EditorViewport? _terrainGeneratorPreviewViewport;
    private bool _terrainGeneratorPreviewCameraInitialized;
    private Vector3 _terrainGeneratorPreviewTarget = new(1024.0f, 400.0f, 1024.0f);
    private int _terrainNeighborSourceX;
    private int _terrainNeighborSourceZ;
    private bool _terrainNeighborEditMode = true;
    private string _terrainNeighborStatus = "";
    private static readonly string[] TerrainSculptToolLabels =
    [
        "Raise / Lower",
        "Set Height",
        "Smooth",
        "Stamp",
        "Noise"
    ];
    private TerrainSculptTool _terrainSculptTool = TerrainSculptTool.RaiseLower;
    private float _terrainBrushRadius = 2.0f;
    private float _terrainBrushStrength = 1.0f;
    private float _terrainSetHeight;
    private float _terrainNoiseScale = 0.25f;
    private int _terrainNoiseSeed = 1337;
    private bool _terrainSculptLower;
    private string _terrainHeightmapBrushPath = "";
    private string _terrainHeightmapBrushLoadedPath = "";
    private TerrainHeightmapBrush? _terrainHeightmapBrush;
    private bool _terrainSculptActive;
    private string _terrainSculptAssetPath = "";
    private TerrainTileSetAsset? _terrainSculptAsset;
    private TerrainTileSetSnapshot? _terrainSculptBefore;
    private List<string> _modelFiles = new();
    private int _selectedModelIndex = -1;
    private string? _detectedTexturePath = null;
    private bool _wasUsingGizmo = false;
    private EditorViewport? _activeDraggingViewport;

    // Editable brush state. Component IDs live in the persistent brush topology,
    // not in transient render vertices, so selections survive a mesh rebuild.
    private BrushComponentMode _brushComponentMode = BrushComponentMode.Object;
    private BrushEditTool _brushEditTool = BrushEditTool.None;
    private readonly HashSet<int> _selectedBrushVertices = [];
    private readonly HashSet<EditableBrushEdge> _selectedBrushEdges = [];
    private readonly HashSet<int> _selectedBrushFaces = [];
    private int _knifeFaceId = -1;
    private Vector3? _knifeFirstPoint;
    private float _brushExtrudeDistance = 0.25f;
    private float _brushInsetAmount = 0.15f;
    private float _brushBevelWidth = 0.1f;
    private float _brushLoopCutFactor = 0.5f;
    private double _lastBrushComponentSelectionTime = -10.0;

    // Shift + drag in Face mode is a direct, normal-constrained extrude. The
    // topology is created only after the pointer moves far enough, avoiding a
    // zero-length extrude when the user was simply selecting a face.
    private bool _isFaceExtrudeDragging;
    private bool _faceExtrudeTopologyCreated;
    private EditorViewport? _faceExtrudeViewport;
    private Brush? _faceExtrudeBrush;
    private int _faceExtrudeFaceId = -1;
    private Vector2 _faceExtrudeStartMouse;
    private Vector2 _faceExtrudeScreenDirection;
    private float _faceExtrudeWorldUnitsPerPixel;
    private Vector3 _faceExtrudeLocalNormal;
    private float _faceExtrudeInitialDistance;
    private float _faceExtrudeCurrentDistance;
    private Dictionary<int, Vector3>? _faceExtrudeInitialPositions;
    private string? _faceExtrudePreState;

    // Brush Tool State
    private BrushPreviewManager _previewManager = new BrushPreviewManager();

    // Handle Dragging State
    public enum HandleType
    {
        None,
        Left,
        Right,
        Top,
        Bottom,
        TopLeft,
        TopRight,
        BottomLeft,
        BottomRight,
        Center
    }
    private bool _isDraggingHandle = false;
    private HandleType _activeHandle = HandleType.None;
    private EditorViewport? _draggingHandleViewport;
    private Vector3 _centerDragLastHit;
    private Vector3 _shearLastHit;
    private void SyncSelection(MapDocument doc)
    {
        if (_selectedObject != null && !doc.Objects.Contains(_selectedObject))
        {
            _selectedObject = doc.Objects.FirstOrDefault(o => o.Id == _selectedObject.Id);
        }
        var newSelectedObjects = new HashSet<MapObject>();
        foreach (var obj in _selectedObjects)
        {
            if (doc.Objects.Contains(obj))
            {
                newSelectedObjects.Add(obj);
            }
            else
            {
                var matched = doc.Objects.FirstOrDefault(o => o.Id == obj.Id);
                if (matched != null)
                {
                    newSelectedObjects.Add(matched);
                }
            }
        }
        _selectedObjects = newSelectedObjects;
    }

    public bool ShowMapWindow => _showMapWindow;
    public bool ShowJsonWindow => _showJsonWindow;
    public bool RequiresContinuousViewportRender =>
        _currentMode is EditorMode.DrawBrush or EditorMode.TerrainSculpt ||
        _isDraggingHandle || EditorGizmo.IsUsing() || ImGui.IsAnyItemActive();
    public bool RequiresContinuousPerspectiveViewportRender => _cloudPreviewAnimated;

    public void Dispose()
    {
        _materialEditor.Dispose();
        _geometryEditor.Dispose();
        _terrainGeneratorPreviewViewport?.Dispose();
    }

    public void Draw(
        EditorWindow window,
        EditorViewport viewport3D,
        EditorViewport viewportTop,
        EditorViewport viewportFront,
        EditorViewport viewportSide,
        EditorSceneService sceneService,
        EditorAssetService assetService,
        CommandHistory history,
        EditorInputService inputService)
    {
        viewport3D.SetUiVisible(false);
        viewportTop.SetUiVisible(false);
        viewportFront.SetUiVisible(false);
        viewportSide.SetUiVisible(false);

        if (window.ConsumeCloseRequest())
            RequestDocumentAction(PendingDocumentAction.Exit, sceneService);
        if (!_initialMapStatusChecked)
        {
            _initialMapStatusChecked = true;
            if (!string.IsNullOrEmpty(sceneService.LastError))
                ShowDocumentError(sceneService.LastError);
        }

        if (_currentMode != EditorMode.DrawBrush)
        {
            _previewManager.Reset();
        }
        SyncSelection(sceneService.Document);
        _cloudPreviewAnimated = sceneService.Document.Clouds.Enabled ||
            sceneService.Document.Fog.Enabled;
        var currentIds = new HashSet<string>(_selectedObjects.Select(o => o.Id));
        if (!_lastSelectedObjectIds.SetEquals(currentIds))
        {
            _lastSelectionTime = ImGui.GetTime();
            _lastSelectedObjectIds = currentIds;
            viewport3D.RequestRender();
            viewportTop.RequestRender();
            viewportFront.RequestRender();
            viewportSide.RequestRender();
        }
        _frameBeginState = sceneService.CaptureSnapshot();

        if (!ImGui.IsMouseDown(ImGuiMouseButton.Left))
        {
            EndFaceExtrudeDrag(sceneService, assetService, history);
            EndEditorGizmoInteraction(sceneService, assetService, history, finalizeEditableBrush: true);
            EndTerrainSculpt(sceneService, assetService, history);
        }

        if (_focusCameraRequested && _selectedObject != null)
        {
            _focusCameraRequested = false;
            FocusCameraOnObject(_selectedObject, sceneService, viewport3D, viewportTop, viewportFront, viewportSide);
        }

        // --- Dockspace Fullscreen ---
        var mainViewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(mainViewport.WorkPos);
        ImGui.SetNextWindowSize(mainViewport.WorkSize);
        ImGui.SetNextWindowViewport(mainViewport.ID);
        
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 0.0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0.0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);

        ImGuiWindowFlags dockWindowFlags = ImGuiWindowFlags.MenuBar | ImGuiWindowFlags.NoDocking;
        dockWindowFlags |= ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove;
        dockWindowFlags |= ImGuiWindowFlags.NoBringToFrontOnFocus | ImGuiWindowFlags.NoNavFocus;

        ImGui.Begin("MainDockSpaceWindow", dockWindowFlags);
        ImGui.PopStyleVar(3);

        uint dockspaceId = ImGui.GetID("MainDockSpace");
        ImGui.DockSpace(dockspaceId, Vector2.Zero, ImGuiDockNodeFlags.None);



        DrawMenuBar(window, sceneService, assetService, history, viewport3D);

        DrawOpenDialog(window, sceneService, assetService, history);
        DrawSaveAsDialog(window, sceneService, assetService, history);
        DrawUnsavedChangesDialog(window, sceneService, assetService, history);
        DrawOverwriteDialog(window, sceneService, assetService, history);
        DrawDocumentErrorDialog();
        if (_executePendingDocumentAction)
            ExecutePendingDocumentAction(window, sceneService, assetService, history);
        DrawHollowDialog(sceneService, assetService, history);
        DrawNewMaterialDialog(sceneService, assetService, history);
        DrawTerrainCreateDialog(window, sceneService, assetService, history);
        //DrawLaunchErrorDialog();

        if (_newDocumentRequested)
        {
            _newDocumentRequested = false;
            viewport3D.Camera.Position = Vector3.Zero;
            viewportTop.Camera.Position = Vector3.Zero;
            viewportFront.Camera.Position = Vector3.Zero;
            viewportSide.Camera.Position = Vector3.Zero;
        }

        ImGui.End();

        DrawBlowtorchSettingsWindow();

        SyncSkyboxPreview(sceneService, assetService, viewport3D, viewportTop, viewportFront, viewportSide);

        // Draw the graph before map input is processed so it can claim the
        // MaterialGraph context in the same frame when it has focus.
        _materialEditor.Draw(
            assetService,
            sceneService,
            inputService,
            () =>
            {
                _showAssetBrowser = true;
                _assetBrowser.OpenTexturePicker(_materialEditor.AssignSelectedTexture);
            });
        _assetBrowser.IsOpen = _showAssetBrowser;
        _assetBrowser.Draw(
            assetService,
            sceneService,
            _materialEditor,
            _geometryEditor,
            entry => ActivateAssetFromBrowser(entry, sceneService, assetService, history, viewport3D));
        _showAssetBrowser = _assetBrowser.IsOpen;
        if (_terrainCreateWaitingForHeightmap && !_assetBrowser.IsOpen)
            _terrainCreateWaitingForHeightmap = false;

        _geometryEditor.Draw(assetService, sceneService, inputService);

        DrawViewportWindow(window, viewport3D, viewportTop, viewportFront, viewportSide, sceneService, assetService, history, inputService);

        viewport3D.ShowHitboxes = _showHitBoxes;
        viewportTop.ShowHitboxes = _showHitBoxes;
        viewportFront.ShowHitboxes = _showHitBoxes;
        viewportSide.ShowHitboxes = _showHitBoxes;

        string? selectedLightId = _selectedObject?.IsLight == true ? _selectedObject.Id : null;
        viewport3D.SelectedLightId = selectedLightId;
        viewportTop.SelectedLightId = selectedLightId;
        viewportFront.SelectedLightId = selectedLightId;
        viewportSide.SelectedLightId = selectedLightId;

        if (_showMapWindow)
            DrawMapWindow(sceneService, assetService, history, viewport3D, viewportTop, viewportFront, viewportSide);

        DrawBrushEditWindow(sceneService, assetService, history);

        if (_showJsonWindow)
            DrawJsonWindow(sceneService);

        if (_showDiagnostics)
            DrawDiagnosticsWindow(sceneService, assetService, history, viewport3D, viewportTop, viewportFront, viewportSide);

        HandleKeyboardShortcuts(sceneService, assetService, history, inputService);
        // RenderDebug runs after the UI pass. Preparing the cache here also
        // captures selection changes made by the hierarchy later in this
        // frame, without doing any terrain generation from the render path.
        GetSelectionAABB(sceneService, assetService, out _, out _);
        Undo.EndFrame(history, sceneService, assetService);
    }

    private void ActivateAssetFromBrowser(
        EditorAssetEntry entry,
        EditorSceneService sceneService,
        EditorAssetService assetService,
        CommandHistory history,
        EditorViewport viewport3D)
    {
        switch (entry.Kind)
        {
            case EditorAssetKind.Model:
                AddModelFromAsset(entry.RelativePath, sceneService, assetService, history, viewport3D);
                break;
            case EditorAssetKind.Texture:
                ApplyTextureFromAsset(entry.RelativePath, sceneService, assetService, history);
                break;
            case EditorAssetKind.Skybox:
                ApplySkyboxFromAsset(entry.RelativePath, sceneService, assetService, history, viewport3D);
                break;
            case EditorAssetKind.GeometryGraph:
                _geometryEditor.Open(entry.RelativePath);
                break;
        }
    }

    private void HandleAssetDropOnViewport(
        EditorViewport viewport,
        Vector2 viewportPosition,
        Vector2 viewportSize,
        EditorSceneService sceneService,
        EditorAssetService assetService,
        CommandHistory history)
    {
        if (!ImGui.BeginDragDropTarget())
            return;

        var payload = ImGui.AcceptDragDropPayload("BLOWTORCH_ASSET");
        if (payload.NativePtr != null && !string.IsNullOrWhiteSpace(AssetDragDrop.CurrentPath))
        {
            string path = AssetDragDrop.CurrentPath;
            switch (AssetDragDrop.CurrentKind)
            {
                case EditorAssetKind.Model when viewport.Camera.ViewType == CameraViewType.Perspective3D:
                    AddModelFromAsset(path, sceneService, assetService, history, viewport);
                    break;
                case EditorAssetKind.Material:
                    ApplyMaterialFromAsset(path, sceneService, assetService, history);
                    break;
                case EditorAssetKind.Texture:
                    ApplyTextureFromAsset(path, sceneService, assetService, history);
                    break;
                case EditorAssetKind.Skybox:
                    ApplySkyboxFromAsset(path, sceneService, assetService, history, viewport);
                    break;
                case EditorAssetKind.GeometryGraph when viewport.Camera.ViewType == CameraViewType.Perspective3D:
                    AddGeometryGraphFromAsset(path, sceneService, assetService, history, viewport);
                    break;
            }
        }
        ImGui.EndDragDropTarget();
    }

    private void AddModelFromAsset(
        string modelPath,
        EditorSceneService sceneService,
        EditorAssetService assetService,
        CommandHistory history,
        EditorViewport viewport)
    {
        string fullPath = assetService.ResolveEditorAssetPath(modelPath);
        if (!File.Exists(fullPath))
            return;

        var document = sceneService.Document;
        string pre = document.Serialize();
        string baseName = Path.GetFileNameWithoutExtension(modelPath);
        var obj = new MapObject
        {
            Id = baseName,
            Visible = true,
            Model = modelPath.Replace('\\', '/'),
            ModelScale = Vector3.One,
            MaterialPath = DefaultMaterialPath,
            Body = new MapBody
            {
                Shape = MapShapeType.Trimesh,
                Position = GetAssetDropPosition(viewport),
                Rotation = Quaternion.Identity,
                Mass = 0,
                Friction = 0.5f,
                Restitution = 0
            }
        };
        document.Objects.Add(obj);
        SceneNameManager.EnsureAllUnique(document);
        _selectedObject = obj;
        _selectedObjects.Clear();
        _selectedObjects.Add(obj);
        sceneService.PopulateScene(assetService);

        string post = document.Serialize();
        sceneService.MarkModified(post);
        history.PushCommand(new SnapshotCommand(sceneService, assetService, pre, post));
    }

    private void AddGeometryGraphFromAsset(
        string graphPath,
        EditorSceneService sceneService,
        EditorAssetService assetService,
        CommandHistory history,
        EditorViewport viewport)
    {
        string fullPath = assetService.ResolveEditorAssetPath(graphPath);
        if (!File.Exists(fullPath))
            return;

        MapDocument document = sceneService.Document;
        string pre = document.Serialize();
        string baseName = Path.GetFileNameWithoutExtension(graphPath);
        var obj = new MapObject
        {
            Id = baseName,
            Visible = true,
            GeometryGraphPath = graphPath.Replace('\\', '/'),
            MaterialPath = DefaultMaterialPath,
            Body = new MapBody
            {
                Shape = MapShapeType.Trimesh,
                HalfExtents = new Vector3(0.5f),
                Position = GetAssetDropPosition(viewport),
                Rotation = Quaternion.Identity,
                Mass = 0,
                Friction = 0.5f,
                Restitution = 0
            }
        };
        document.Objects.Add(obj);
        SceneNameManager.EnsureAllUnique(document);
        _selectedObject = obj;
        _selectedObjects.Clear();
        _selectedObjects.Add(obj);
        sceneService.PopulateScene(assetService);
        string post = document.Serialize();
        sceneService.MarkModified(post);
        history.PushCommand(new SnapshotCommand(sceneService, assetService, pre, post));
    }

    private static Vector3 GetAssetDropPosition(EditorViewport viewport)
    {
        Vector3 position = viewport.Camera.Position + viewport.Camera.Front * 5.0f;
        if (viewport.Camera.ViewType == CameraViewType.Top)
            position.Y = 0;
        else if (viewport.Camera.ViewType == CameraViewType.Front)
            position.Z = 0;
        else if (viewport.Camera.ViewType == CameraViewType.Side)
            position.X = 0;
        return position;
    }

    private void ApplyMaterialFromAsset(
        string materialPath,
        EditorSceneService sceneService,
        EditorAssetService assetService,
        CommandHistory history)
    {
        MapObject[] targets = _selectedObjects.Count > 0
            ? _selectedObjects.Where(obj => !obj.IsLight).ToArray()
            : _selectedObject != null && !_selectedObject.IsLight ? [_selectedObject] : [];
        if (targets.Length == 0)
            return;

        string pre = sceneService.Document.Serialize();
        foreach (MapObject target in targets)
            AssignMaterial(target, materialPath, sceneService, assetService);
        sceneService.PopulateScene(assetService);
        string post = sceneService.Document.Serialize();
        sceneService.MarkModified(post);
        history.PushCommand(new SnapshotCommand(sceneService, assetService, pre, post));
    }

    private void ApplyTextureFromAsset(
        string texturePath,
        EditorSceneService sceneService,
        EditorAssetService assetService,
        CommandHistory history)
    {
        MapObject[] targets = _selectedObjects.Count > 0
            ? _selectedObjects.Where(obj => !obj.IsLight).ToArray()
            : _selectedObject != null && !_selectedObject.IsLight ? [_selectedObject] : [];
        if (targets.Length == 0)
            return;

        string pre = sceneService.Document.Serialize();
        foreach (MapObject target in targets)
        {
            target.Texture = texturePath;
            target.MaterialPath = EnsureMaterialForTexture(
                assetService, texturePath, Path.GetFileNameWithoutExtension(texturePath));
        }
        sceneService.PopulateScene(assetService);
        string post = sceneService.Document.Serialize();
        sceneService.MarkModified(post);
        history.PushCommand(new SnapshotCommand(sceneService, assetService, pre, post));
    }

    private static void ApplySkyboxFromAsset(
        string skyboxPath,
        EditorSceneService sceneService,
        EditorAssetService assetService,
        CommandHistory history,
        EditorViewport viewport)
    {
        if (sceneService.Document.SkyboxPath.Equals(skyboxPath, StringComparison.OrdinalIgnoreCase))
            return;

        string pre = sceneService.Document.Serialize();
        sceneService.Document.Skybox.Mode = SkyboxMode.Texture;
        sceneService.Document.SkyboxPath = skyboxPath.Replace('\\', '/');
        assetService.SetSkyboxTexture(sceneService.Document.SkyboxPath);
        viewport.RequestRender();
        string post = sceneService.Document.Serialize();
        sceneService.MarkModified(post);
        history.PushCommand(new SnapshotCommand(sceneService, assetService, pre, post));
    }

    private void SyncSkyboxPreview(
        EditorSceneService sceneService,
        EditorAssetService assetService,
        EditorViewport viewport3D,
        EditorViewport viewportTop,
        EditorViewport viewportFront,
        EditorViewport viewportSide)
    {
        if (sceneService.Document.Skybox.Mode == SkyboxMode.Procedural)
        {
            ulong settingsSignature = ProceduralSky.ComputeSettingsSignature(
                sceneService.Document.Skybox);
            if (string.Equals(
                    _previewSkyboxDocumentPath,
                    "__procedural__",
                    StringComparison.Ordinal) &&
                _previewSkyboxSettingsSignature == settingsSignature)
            {
                return;
            }

            _previewSkyboxDocumentPath = "__procedural__";
            _previewSkyboxSettingsSignature = settingsSignature;
            assetService.SetProceduralSkybox(sceneService.Document.Skybox);
            viewport3D.RequestRender();
            viewportTop.RequestRender();
            viewportFront.RequestRender();
            viewportSide.RequestRender();
            return;
        }

        string configuredPath = sceneService.Document.SkyboxPath ?? "";
        if (string.Equals(_previewSkyboxDocumentPath, configuredPath, StringComparison.OrdinalIgnoreCase) &&
            _previewSkyboxSettingsSignature == 0)
            return;

        _previewSkyboxDocumentPath = configuredPath;
        _previewSkyboxSettingsSignature = 0;
        if (!assetService.SetSkyboxTexture(configuredPath))
        {
            Logger.Warn($"Skybox '{configuredPath}' could not be loaded in the editor. Using the default skybox.");
            assetService.SetSkyboxTexture(null);
        }

        viewport3D.RequestRender();
        viewportTop.RequestRender();
        viewportFront.RequestRender();
        viewportSide.RequestRender();
    }

    private void ApplySkyboxPreview(
        string configuredPath,
        EditorAssetService assetService,
        EditorViewport viewport3D,
        EditorViewport viewportTop,
        EditorViewport viewportFront,
        EditorViewport viewportSide)
    {
        _previewSkyboxDocumentPath = configuredPath;
        _previewSkyboxSettingsSignature = 0;
        if (!assetService.SetSkyboxTexture(configuredPath))
        {
            Logger.Warn($"Skybox '{configuredPath}' could not be loaded in the editor. Using the default skybox.");
            assetService.SetSkyboxTexture(null);
        }

        viewport3D.RequestRender();
        viewportTop.RequestRender();
        viewportFront.RequestRender();
        viewportSide.RequestRender();
    }

    private void DuplicateObject(MapObject obj, EditorSceneService sceneService, EditorAssetService assetService, CommandHistory history)
    {
        DuplicateObjects(new List<MapObject> { obj }, sceneService, assetService, history);
    }

    private void DeleteObject(MapObject obj, EditorSceneService sceneService, EditorAssetService assetService, CommandHistory history)
    {
        DeleteObjects(new List<MapObject> { obj }, sceneService, assetService, history);
    }

    private void AddWithDescendants(MapObject obj, MapDocument doc, HashSet<MapObject> result)
    {
        if (result.Add(obj))
        {
            var children = doc.Objects.Where(o => o.ParentId == obj.Id);
            foreach (var child in children)
            {
                AddWithDescendants(child, doc, result);
            }
        }
    }

    private HashSet<MapObject> GetObjectsToTransform(MapDocument doc)
    {
        var result = new HashSet<MapObject>();
        foreach (var obj in _selectedObjects)
        {
            AddWithDescendants(obj, doc, result);
        }
        return result;
    }

    private bool IsDescendantOf(MapObject potentialDescendant, MapObject potentialAncestor, MapDocument doc)
    {
        string? parentId = potentialDescendant.ParentId;
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (!string.IsNullOrEmpty(parentId))
        {
            if (!visited.Add(parentId)) return false;
            if (parentId.Equals(potentialAncestor.Id, StringComparison.OrdinalIgnoreCase)) return true;
            var parent = doc.Objects.FirstOrDefault(o => o.Id.Equals(parentId, StringComparison.OrdinalIgnoreCase));
            parentId = parent?.ParentId;
        }
        return false;
    }

    private float GetGroupDefaultMass(MapObject group, MapDocument doc)
    {
        // A group's children are not separate runtime bodies anymore. When a
        // user first enables the compound collider, seed its mass from the
        // authored child masses so an already-dynamic selection does not turn
        // into an unexpected static body. The group mass remains editable as
        // the single authoritative value afterward.
        float mass = 0.0f;
        foreach (MapObject candidate in doc.Objects)
        {
            if (candidate == group ||
                !IsDescendantOf(candidate, group, doc) ||
                candidate.Body == null)
            {
                continue;
            }

            if (float.IsFinite(candidate.Body.Mass) && candidate.Body.Mass > 0.0f)
                mass += candidate.Body.Mass;
        }

        return float.IsFinite(mass) ? mass : 0.0f;
    }

    private void UpdateEntitiesVisibilityRecursive(MapDocument doc, Fuse.Renderer.Scene scene, MapObject obj)
    {
        // A group does not have a render entity of its own. Recalculate the
        // complete sub-tree and update both mesh entities and light objects.
        // The previous implementation only visited descendants and only
        // touched Scene.Entities, so hidden groups could still render lights
        // and some descendants could keep their old visibility state.
        foreach (MapObject candidate in doc.Objects)
        {
            if (!candidate.Id.Equals(obj.Id, StringComparison.OrdinalIgnoreCase) &&
                !IsDescendantOf(candidate, obj, doc))
                continue;

            bool isVisible = candidate.IsGloballyVisible(doc);
            var entity = scene.Entities.FirstOrDefault(e => e.Id == candidate.Id);
            if (entity != null)
                entity.Visible = isVisible;

            // Terrain objects have a renderless root entity plus one render
            // entity per chunk. Visibility must be propagated to the chunks;
            // changing only the root leaves the terrain visible or prevents
            // it from coming back after the scene is rebuilt.
            if (candidate.IsTerrain)
            {
                foreach (var chunkEntity in scene.Entities)
                {
                    if (chunkEntity.TerrainLod != null &&
                        chunkEntity.TerrainChunkGroupId.Equals(
                            candidate.Id,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        chunkEntity.Visible = isVisible;
                    }
                }
            }

            var light = scene.Lights.FirstOrDefault(l => l.Id == candidate.Id);
            if (light != null)
                light.Enabled = isVisible;
        }
    }

    private void ToggleObjectVisibility(
        MapObject obj,
        MapDocument doc,
        Fuse.Renderer.Scene scene,
        EditorSceneService sceneService,
        EditorAssetService assetService,
        CommandHistory history)
    {
        Undo.RecordState(_frameBeginState);
        obj.Visible = !obj.Visible;
        UpdateEntitiesVisibilityRecursive(doc, scene, obj);
        SyncLight(sceneService, obj);
        Undo.ForceEnd(history, sceneService, assetService);
    }

    private void GroupSelected(EditorSceneService sceneService, EditorAssetService assetService, CommandHistory history)
    {
        if (_selectedObjects.Count == 0) return;

        var pre = sceneService.Document.Serialize();
        var doc = sceneService.Document;

        // Calculate center position
        Vector3 sum = Vector3.Zero;
        int count = 0;
        float groupMass = 0.0f;
        float groupFriction = 0.5f;
        float groupRestitution = 0.0f;
        bool copiedMaterialPhysics = false;
        foreach (var obj in _selectedObjects)
        {
            if (obj.Body != null && !obj.IsLight)
            {
                sum += obj.Body.Position;
                count++;
                if (float.IsFinite(obj.Body.Mass) && obj.Body.Mass > 0.0f)
                    groupMass += obj.Body.Mass;
                if (!copiedMaterialPhysics)
                {
                    groupFriction = obj.Body.Friction;
                    groupRestitution = obj.Body.Restitution;
                    copiedMaterialPhysics = true;
                }
            }
        }
        Vector3 center = count > 0 ? sum / count : Vector3.Zero;

        // Generate unique group ID
        int groupIndex = 1;
        string groupId = $"group_{groupIndex}";
        while (doc.Objects.Any(o => o.Id == groupId))
        {
            groupIndex++;
            groupId = $"group_{groupIndex}";
        }

        // Create group object
        var groupObj = new MapObject
        {
            Id = groupId,
            Visible = true,
            Body = new MapBody
            {
                Shape = MapShapeType.None,
                Position = center,
                Rotation = Quaternion.Identity,
                Mass = float.IsFinite(groupMass) ? groupMass : 0.0f,
                Friction = groupFriction,
                Restitution = groupRestitution
            }
        };

        doc.Objects.Add(groupObj);

        // Parent all selected objects to groupObj
        foreach (var obj in _selectedObjects)
        {
            obj.ParentId = groupObj.Id;
        }

        SceneNameManager.EnsureAllUnique(doc);

        var post = doc.Serialize();
        sceneService.MarkModified(post);
        history.PushCommand(new SnapshotCommand(sceneService, assetService, pre, post));
        sceneService.PopulateScene(assetService);

        // Select the newly created group object
        _selectedObjects.Clear();
        _selectedObjects.Add(groupObj);
        _selectedObject = groupObj;
    }

    private void UngroupSelected(EditorSceneService sceneService, EditorAssetService assetService, CommandHistory history)
    {
        if (_selectedObjects.Count == 0) return;

        var pre = sceneService.Document.Serialize();
        var doc = sceneService.Document;

        foreach (var obj in _selectedObjects)
        {
            obj.ParentId = null;
        }

        var post = doc.Serialize();
        sceneService.MarkModified(post);
        history.PushCommand(new SnapshotCommand(sceneService, assetService, pre, post));
        sceneService.PopulateScene(assetService);
    }

    private enum HierarchyIconKind
    {
        Group,
        Brush,
        Terrain,
        Model,
        Box,
        Sphere,
        Capsule,
        PointLight,
        SpotLight,
        DirectionalLight,
        Other
    }

    private static HierarchyIconKind GetHierarchyIconKind(MapObject obj)
    {
        if (obj.IsLight)
        {
            return obj.LightType switch
            {
                "spot" => HierarchyIconKind.SpotLight,
                "directional" => HierarchyIconKind.DirectionalLight,
                _ => HierarchyIconKind.PointLight
            };
        }
        if (obj.IsTerrain) return HierarchyIconKind.Terrain;
        if (IsGroupObject(obj))
            return HierarchyIconKind.Group;
        if (obj.IsModel) return HierarchyIconKind.Model;
        if (obj is Brush) return HierarchyIconKind.Brush;
        return obj.Body?.Shape switch
        {
            MapShapeType.Sphere => HierarchyIconKind.Sphere,
            MapShapeType.Capsule => HierarchyIconKind.Capsule,
            MapShapeType.Box or MapShapeType.Trimesh => HierarchyIconKind.Box,
            _ => HierarchyIconKind.Other
        };
    }

    private static bool IsGroupObject(MapObject obj) =>
        !obj.IsLight &&
        !obj.IsTerrain &&
        !obj.IsModel &&
        obj is not Brush &&
        string.IsNullOrEmpty(obj.Mesh) &&
        string.IsNullOrEmpty(obj.GeometryGraphPath);

    private static string GetHierarchyTypeLabel(MapObject obj)
    {
        return GetHierarchyIconKind(obj) switch
        {
            HierarchyIconKind.Group => "Group",
            HierarchyIconKind.Brush => "Brush",
            HierarchyIconKind.Terrain => "Terrain",
            HierarchyIconKind.Model => "Model",
            HierarchyIconKind.Box => "Box",
            HierarchyIconKind.Sphere => "Sphere",
            HierarchyIconKind.Capsule => "Capsule",
            HierarchyIconKind.PointLight => "Point light",
            HierarchyIconKind.SpotLight => "Spot light",
            HierarchyIconKind.DirectionalLight => "Directional light",
            _ => "Object"
        };
    }

    private static Vector4 GetHierarchyIconColor(HierarchyIconKind kind)
    {
        return kind switch
        {
            HierarchyIconKind.PointLight or HierarchyIconKind.SpotLight or HierarchyIconKind.DirectionalLight
                => new Vector4(1.0f, 0.78f, 0.22f, 1.0f),
            HierarchyIconKind.Group => new Vector4(0.46f, 0.70f, 0.95f, 1.0f),
            HierarchyIconKind.Terrain => new Vector4(0.64f, 0.82f, 0.35f, 1.0f),
            HierarchyIconKind.Model => new Vector4(0.70f, 0.52f, 0.95f, 1.0f),
            HierarchyIconKind.Brush or HierarchyIconKind.Box => new Vector4(0.35f, 0.82f, 0.68f, 1.0f),
            HierarchyIconKind.Sphere or HierarchyIconKind.Capsule => new Vector4(0.32f, 0.72f, 0.95f, 1.0f),
            _ => new Vector4(0.70f, 0.72f, 0.76f, 1.0f)
        };
    }

    // Small vector icons keep the hierarchy independent from emoji/icon fonts.
    private static void DrawHierarchyIcon(ImDrawListPtr drawList, Vector2 center, HierarchyIconKind kind, uint color)
    {
        const float size = 6.0f;
        switch (kind)
        {
            case HierarchyIconKind.Group:
                drawList.AddRectFilled(center + new Vector2(-size, -size + 2), center + new Vector2(size, size), color, 1.5f);
                drawList.AddRectFilled(center + new Vector2(-size + 1, -size), center + new Vector2(-1, -size + 2), color, 1.0f);
                break;
            case HierarchyIconKind.Sphere:
                drawList.AddCircle(center, size, color, 12, 1.8f);
                drawList.AddCircle(center - new Vector2(2, 2), 1.2f, color, 8, 1.2f);
                break;
            case HierarchyIconKind.Capsule:
                drawList.AddLine(center - new Vector2(0, size), center + new Vector2(0, size), color, 2.5f);
                drawList.AddCircle(center - new Vector2(0, size - 1), 3.0f, color, 10, 1.5f);
                drawList.AddCircle(center + new Vector2(0, size - 1), 3.0f, color, 10, 1.5f);
                break;
            case HierarchyIconKind.Model:
                drawList.AddLine(center + new Vector2(0, -size), center + new Vector2(size, 0), color, 1.8f);
                drawList.AddLine(center + new Vector2(size, 0), center + new Vector2(0, size), color, 1.8f);
                drawList.AddLine(center + new Vector2(0, size), center + new Vector2(-size, 0), color, 1.8f);
                drawList.AddLine(center + new Vector2(-size, 0), center + new Vector2(0, -size), color, 1.8f);
                drawList.AddLine(center + new Vector2(0, -size), center + new Vector2(0, 1), color, 1.2f);
                break;
            case HierarchyIconKind.PointLight:
                drawList.AddCircle(center, 3.0f, color, 10, 1.8f);
                drawList.AddLine(center - new Vector2(size, 0), center - new Vector2(4, 0), color, 1.5f);
                drawList.AddLine(center + new Vector2(4, 0), center + new Vector2(size, 0), color, 1.5f);
                drawList.AddLine(center - new Vector2(0, size), center - new Vector2(0, 4), color, 1.5f);
                drawList.AddLine(center + new Vector2(0, 4), center + new Vector2(0, size), color, 1.5f);
                break;
            case HierarchyIconKind.SpotLight:
                drawList.AddCircleFilled(center - new Vector2(0, 4), 2.2f, color);
                drawList.AddLine(center - new Vector2(4, 1), center + new Vector2(-size, size), color, 1.5f);
                drawList.AddLine(center + new Vector2(4, 1), center + new Vector2(size, size), color, 1.5f);
                drawList.AddLine(center + new Vector2(-size, size), center + new Vector2(size, size), color, 1.5f);
                break;
            case HierarchyIconKind.DirectionalLight:
                drawList.AddLine(center - new Vector2(size, size), center + new Vector2(size, size), color, 1.8f);
                drawList.AddLine(center + new Vector2(size, size), center + new Vector2(size - 3, size - 1), color, 1.8f);
                drawList.AddLine(center + new Vector2(size, size), center + new Vector2(size - 1, size - 3), color, 1.8f);
                break;
            default:
                drawList.AddRect(center - new Vector2(size - 1), center + new Vector2(size - 1), color, 1.0f, ImDrawFlags.None, 1.8f);
                break;
        }
    }

    private static void DrawVisibilityIcon(ImDrawListPtr drawList, Vector2 center, bool visible, uint color)
    {
        if (visible)
        {
            drawList.AddLine(center + new Vector2(-8, 0), center + new Vector2(-3, -4), color, 1.5f);
            drawList.AddLine(center + new Vector2(-3, -4), center + new Vector2(3, -4), color, 1.5f);
            drawList.AddLine(center + new Vector2(3, -4), center + new Vector2(8, 0), color, 1.5f);
            drawList.AddLine(center + new Vector2(-8, 0), center + new Vector2(-3, 4), color, 1.5f);
            drawList.AddLine(center + new Vector2(-3, 4), center + new Vector2(3, 4), color, 1.5f);
            drawList.AddLine(center + new Vector2(3, 4), center + new Vector2(8, 0), color, 1.5f);
            drawList.AddCircleFilled(center, 2.2f, color);
        }
        else
        {
            drawList.AddLine(center - new Vector2(7, 7), center + new Vector2(7, 7), color, 1.8f);
            drawList.AddCircle(center, 5.0f, color, 10, 1.2f);
        }
    }

    private static void DrawInspectorObjectHeader(MapObject obj)
    {
        HierarchyIconKind iconKind = GetHierarchyIconKind(obj);
        Vector2 iconStart = ImGui.GetCursorScreenPos();
        ImGui.Dummy(new Vector2(28.0f, 30.0f));
        DrawHierarchyIcon(ImGui.GetWindowDrawList(), iconStart + new Vector2(14.0f, 15.0f), iconKind,
            ImGui.ColorConvertFloat4ToU32(GetHierarchyIconColor(iconKind)));
        ImGui.SameLine();
        ImGui.BeginGroup();
        ImGui.TextColored(new Vector4(0.90f, 0.93f, 0.98f, 1.0f), obj.Id);
        ImGui.TextDisabled(GetHierarchyTypeLabel(obj));
        ImGui.EndGroup();
        ImGui.Separator();
    }

    private static void DrawInspectorMultiHeader(int count)
    {
        ImGui.TextColored(new Vector4(0.38f, 0.78f, 0.98f, 1.0f), $"{count} objects selected");
        ImGui.TextDisabled("Changes below apply to the whole selection.");
        ImGui.Separator();
    }

    private void DrawInspectorMultiSelection(
        IReadOnlyList<MapObject> selection,
        MapDocument doc,
        Fuse.Renderer.Scene scene,
        EditorSceneService sceneService,
        EditorAssetService assetService,
        CommandHistory history)
    {
        DrawInspectorMultiHeader(selection.Count);

        bool allVisible = selection.All(obj => obj.Visible);
        bool visible = allVisible;
        if (ImGui.Checkbox("Visible##multiVis", ref visible))
        {
            Undo.RecordState(_frameBeginState);
            foreach (MapObject obj in selection)
            {
                obj.Visible = visible;
                Entity? entity = scene.Entities.FirstOrDefault(candidate => candidate.Id == obj.Id);
                if (entity != null)
                    entity.Visible = visible;
                UpdateEntitiesVisibilityRecursive(doc, scene, obj);
            }
            Undo.ForceEnd(history, sceneService, assetService);
        }

        bool allHaveMaterial = selection.All(obj => !obj.IsLight);
        if (allHaveMaterial)
        {
            string commonMaterial = selection.Select(obj => obj.MaterialPath ?? "").Distinct().Count() == 1
                ? selection[0].MaterialPath ?? ""
                : "";
            if (DrawMaterialPicker("Material##multiMaterial", commonMaterial, assetService, out string selectedMaterial))
            {
                Undo.RecordState(_frameBeginState);
                foreach (MapObject obj in selection)
                    AssignMaterial(obj, selectedMaterial, sceneService, assetService);
                Undo.ForceEnd(history, sceneService, assetService);
            }
            ImGui.SameLine();
            if (ImGui.Button("New##multiMaterial"))
                RequestNewMaterial(selection.ToList());
        }

        bool allSupportLegacyTexture = selection.All(obj => !obj.IsLight);
        if (allSupportLegacyTexture &&
            ImGui.TreeNode("Legacy Texture Compatibility##multiLegacyTexture"))
        {
            string commonTexture = selection.Select(obj => obj.Texture ?? "").Distinct().Count() == 1
                ? selection[0].Texture ?? ""
                : "";
            string texture = commonTexture;
            if (ImGui.InputText("Texture##multiTex", ref texture, 256))
            {
                Undo.TrackItem(_frameBeginState);
                foreach (MapObject obj in selection)
                {
                    obj.Texture = texture;
                    Entity? entity = scene.Entities.FirstOrDefault(candidate => candidate.Id == obj.Id);
                    if (entity == null)
                        continue;

                    entity.TexturePath = texture;
                    if (string.IsNullOrWhiteSpace(entity.MaterialPath) &&
                        !string.IsNullOrWhiteSpace(texture))
                    {
                        entity.Material = assetService.AssetManager.GetLegacyMaterial(texture);
                    }
                }
            }
            ImGui.TreePop();
        }

        bool allSupportUv = selection.All(obj => !obj.IsModel && !obj.IsLight);
        if (allSupportUv)
        {
            Vector2 commonUvScale = selection.Select(obj => obj.UvScale).Distinct().Count() == 1
                ? selection[0].UvScale
                : Vector2.One;
            Vector2 uvScale = commonUvScale;
            if (ImGui.DragFloat2("UV Scale##multiUv", ref uvScale, 0.05f))
            {
                Undo.TrackItem(_frameBeginState);
                foreach (MapObject obj in selection)
                {
                    obj.UvScale = uvScale;
                    Entity? entity = scene.Entities.FirstOrDefault(candidate => candidate.Id == obj.Id);
                    if (entity != null)
                        entity.UvScale = uvScale;
                }
            }

            Vector2 commonUvOffset = selection.Select(obj => obj.UvOffset).Distinct().Count() == 1
                ? selection[0].UvOffset
                : Vector2.Zero;
            Vector2 uvOffset = commonUvOffset;
            if (ImGui.DragFloat2("UV Offset##multiUvOff", ref uvOffset, 0.01f))
            {
                Undo.TrackItem(_frameBeginState);
                foreach (MapObject obj in selection)
                {
                    obj.UvOffset = uvOffset;
                    Entity? entity = scene.Entities.FirstOrDefault(candidate => candidate.Id == obj.Id);
                    if (entity != null)
                        entity.UvOffset = uvOffset;
                }
            }

            float commonUvRotation = selection.Select(obj => obj.UvRotation).Distinct().Count() == 1
                ? selection[0].UvRotation
                : 0.0f;
            float uvRotationDegrees = commonUvRotation * (180.0f / MathF.PI);
            if (ImGui.DragFloat("UV Rotation##multiUvRot", ref uvRotationDegrees,
                    0.5f, -360.0f, 360.0f, "%.1f deg"))
            {
                Undo.TrackItem(_frameBeginState);
                float uvRotation = uvRotationDegrees * (MathF.PI / 180.0f);
                foreach (MapObject obj in selection)
                {
                    obj.UvRotation = uvRotation;
                    Entity? entity = scene.Entities.FirstOrDefault(candidate => candidate.Id == obj.Id);
                    if (entity != null)
                        entity.UvRotation = uvRotation;
                }
            }
        }

        // Transform is a common option for every selectable object. Position
        // edits preserve the arrangement by applying a delta; rotation edits
        // use the first selected object as the shared pivot.
        if (ImGui.CollapsingHeader("Transform##multiTransform", ImGuiTreeNodeFlags.DefaultOpen))
        {
            MapObject pivotObject = selection[0];
            Vector3 pivotPosition = GetInspectorWorldPosition(pivotObject, scene);
            Quaternion pivotRotation = GetInspectorWorldRotation(pivotObject, scene);
            Vector3 position = pivotPosition;
            Vector3 rotationEuler = InspectorQuaternionToEuler(pivotRotation);

            ImGui.TextDisabled("Position uses a delta; rotation uses the first selected object as pivot.");
            bool positionChanged = ImGui.DragFloat3("Location##multiPosition", ref position, 0.1f);
            Undo.TrackItem(_frameBeginState);
            bool rotationChanged = ImGui.DragFloat3(
                "Rotation##multiRotation",
                ref rotationEuler,
                0.5f,
                -360.0f,
                360.0f,
                "%.1f deg");
            Undo.TrackItem(_frameBeginState);

            bool allScaleEditable = selection.All(obj => CanEditInspectorScale(obj));
            Vector3 firstScale = GetInspectorScale(selection[0], scene);
            Vector3 scale = firstScale;
            bool scaleChanged = false;
            if (allScaleEditable)
            {
                scaleChanged = ImGui.DragFloat3("Scale##multiScale", ref scale, 0.05f);
                Undo.TrackItem(_frameBeginState);
            }
            else
            {
                ImGui.TextDisabled("Scale is shown only when every selected object supports the same scale operation.");
            }

            if (positionChanged || rotationChanged || scaleChanged)
            {
                Vector3 positionDelta = position - pivotPosition;
                Quaternion rotationDelta = rotationChanged
                    ? Quaternion.Normalize(
                        InspectorEulerToQuaternion(rotationEuler) * Quaternion.Inverse(pivotRotation))
                    : Quaternion.Identity;
                Vector3 scaleFactor = new(
                    MathF.Abs(firstScale.X) > 0.0001f ? scale.X / firstScale.X : 1.0f,
                    MathF.Abs(firstScale.Y) > 0.0001f ? scale.Y / firstScale.Y : 1.0f,
                    MathF.Abs(firstScale.Z) > 0.0001f ? scale.Z / firstScale.Z : 1.0f);

                foreach (MapObject obj in GetObjectsToTransform(doc).ToList())
                {
                    Vector3 objectPosition = GetInspectorWorldPosition(obj, scene);
                    Quaternion objectRotation = GetInspectorWorldRotation(obj, scene);
                    if (positionChanged)
                        objectPosition += positionDelta;
                    if (rotationChanged)
                    {
                        objectPosition = pivotPosition +
                            Vector3.Transform(objectPosition - pivotPosition, rotationDelta);
                        objectRotation = Quaternion.Normalize(rotationDelta * objectRotation);
                    }

                    if (positionChanged || rotationChanged)
                        SetInspectorWorldPose(obj, objectPosition, objectRotation, scene, sceneService);

                    if (scaleChanged)
                        ApplyInspectorScale(obj, scaleFactor, scene, sceneService, assetService);
                }
            }
        }

        // A light has no material or collision inspector, but all light
        // objects share a useful set of properties. Keep those controls when
        // the selection is made exclusively of lights instead of dropping the
        // whole inspector to the old visibility-only subset.
        if (selection.All(obj => obj.IsLight))
            DrawInspectorMultiLightSelection(selection, sceneService);

        // Terrain settings are asset-independent controls common to every
        // selected terrain. Neighbor creation remains intentionally per asset
        // and therefore is not exposed in a mixed selection.
        if (selection.All(obj => obj.IsTerrain))
            DrawInspectorMultiTerrainSelection(selection, sceneService, assetService);

        // Lights also carry a MapBody for their transform, but that body is
        // not an editable collision body. Excluding lights here keeps a mixed
        // light/mesh selection from exposing physics controls that would
        // silently assign colliders to lights.
        List<MapObject> bodyObjects = selection
            .Where(obj => !obj.IsLight && obj.Body != null)
            .ToList();
        bool allHaveBodies = bodyObjects.Count == selection.Count;
        bool allHaveCollision = allHaveBodies &&
            bodyObjects.All(obj => obj.Body!.Shape != MapShapeType.None);
        bool refreshScene = false;

        if (allHaveBodies)
        {
            ImGui.SeparatorText("Physics (common options)");

            bool allModels = bodyObjects.All(obj => obj.IsModel);
            bool allGroups = bodyObjects.All(IsGroupObject);
            bool allDefaultGeometry = bodyObjects.All(obj =>
                !obj.IsModel && !IsGroupObject(obj) && !obj.IsTerrain);
            bool allTerrains = bodyObjects.All(obj => obj.IsTerrain);
            bool canEditCollisionShape = allModels || allGroups ||
                allDefaultGeometry || allTerrains;
            if (canEditCollisionShape)
            {
                int collisionIndex;
                string[] collisionLabels;
                if (allModels)
                {
                    collisionLabels = ["Trimesh", "Convex Hull", "No Collision"];
                    collisionIndex = bodyObjects.Select(obj => obj.Body!.Shape).Distinct().Count() == 1
                        ? bodyObjects[0].Body!.Shape switch
                        {
                            MapShapeType.Trimesh => 0,
                            MapShapeType.ConvexHull => 1,
                            _ => 2
                        }
                        : 2;
                }
                else if (allGroups)
                {
                    collisionLabels = ["Compound Group", "No Collision"];
                    collisionIndex = bodyObjects.All(obj => obj.Body!.Shape == MapShapeType.None) ? 1 : 0;
                }
                else if (allTerrains)
                {
                    collisionLabels = ["Terrain", "No Collision"];
                    collisionIndex = bodyObjects.All(obj => obj.Body!.Shape == MapShapeType.None) ? 1 : 0;
                }
                else
                {
                    collisionLabels = ["Default", "No Collision"];
                    collisionIndex = bodyObjects.All(obj => obj.Body!.Shape == MapShapeType.None) ? 1 : 0;
                }

                bool collisionChanged = ImGui.Combo(
                    "Collision Shape##multiCollision",
                    ref collisionIndex,
                    collisionLabels,
                    collisionLabels.Length);
                Undo.TrackItem(_frameBeginState);
                if (collisionChanged)
                {
                    foreach (MapObject obj in bodyObjects)
                    {
                        if (allModels)
                        {
                            obj.Body!.Shape = collisionIndex switch
                            {
                                0 => MapShapeType.Trimesh,
                                1 => MapShapeType.ConvexHull,
                                _ => MapShapeType.None
                            };
                        }
                        else if (collisionIndex == 1)
                        {
                            obj.Body!.Shape = MapShapeType.None;
                        }
                        else if (allGroups)
                        {
                            obj.Body!.Shape = MapShapeType.Compound;
                            if (obj.Body.Mass <= 0.0f)
                                obj.Body.Mass = GetGroupDefaultMass(obj, doc);
                        }
                        else if (allTerrains)
                        {
                            obj.Body!.Shape = MapShapeType.Trimesh;
                        }
                        else
                        {
                            obj.Body!.Shape = IsGroupObject(obj)
                                ? MapShapeType.Compound
                                : obj.Mesh == "sphere"
                                    ? MapShapeType.Sphere
                                    : obj.Mesh == "capsule"
                                        ? MapShapeType.Capsule
                                        : MapShapeType.Box;
                            if (IsGroupObject(obj) && obj.Body.Mass <= 0.0f)
                                obj.Body.Mass = GetGroupDefaultMass(obj, doc);
                        }
                    }
                    refreshScene = true;
                }
            }
            else
            {
                ImGui.TextDisabled("Collision shape is not common for this selection; shared physics values remain available below.");
            }

            if (allHaveBodies)
            {
                bool mixedMass = bodyObjects.Select(obj => obj.Body!.Mass).Distinct().Count() != 1;
                float mass = bodyObjects[0].Body!.Mass;
                if (mixedMass)
                    ImGui.TextDisabled("Mixed values use the first selected object until edited.");
                if (ImGui.DragFloat("Mass##multiMass", ref mass, 0.1f, 0.0f, 100000.0f, "%.3f"))
                {
                    Undo.TrackItem(_frameBeginState);
                    foreach (MapObject obj in bodyObjects)
                        obj.Body!.Mass = mass;
                }

                float commonBuoyancyVolume = bodyObjects.Select(obj => obj.Body!.BuoyancyVolume ?? 0.0f).Distinct().Count() == 1
                    ? bodyObjects[0].Body!.BuoyancyVolume ?? 0.0f
                    : 0.0f;
                float buoyancyVolume = commonBuoyancyVolume;
                if (ImGui.DragFloat("Buoyancy volume (m³)##multiBuoyancyVolume", ref buoyancyVolume,
                        0.05f, 0.0f, 100000.0f, "%.3f"))
                {
                    Undo.TrackItem(_frameBeginState);
                    foreach (MapObject obj in bodyObjects)
                    {
                        obj.Body!.BuoyancyVolume = buoyancyVolume > 0.0001f
                            ? buoyancyVolume
                            : null;
                    }
                }
                ImGui.SameLine();
                ImGui.TextDisabled("0 = collider volume");

                float friction = bodyObjects[0].Body!.Friction;
                if (ImGui.DragFloat("Friction##multiFriction", ref friction, 0.05f, 0.0f, 10.0f, "%.2f"))
                {
                    Undo.TrackItem(_frameBeginState);
                    foreach (MapObject obj in bodyObjects)
                        obj.Body!.Friction = friction;
                }

                float restitution = bodyObjects[0].Body!.Restitution;
                if (ImGui.DragFloat("Restitution##multiRestitution", ref restitution, 0.05f, 0.0f, 1.0f, "%.2f"))
                {
                    Undo.TrackItem(_frameBeginState);
                    foreach (MapObject obj in bodyObjects)
                        obj.Body!.Restitution = restitution;
                }

                bool commonTrigger = bodyObjects.Select(obj => obj.Body!.IsTrigger).Distinct().Count() == 1
                    ? bodyObjects[0].Body!.IsTrigger
                    : false;
                bool trigger = commonTrigger;
                if (ImGui.Checkbox("Is Trigger##multiTrigger", ref trigger))
                {
                    Undo.TrackItem(_frameBeginState);
                    foreach (MapObject obj in bodyObjects)
                    {
                        obj.Body!.IsTrigger = trigger;
                        Entity? entity = scene.Entities.FirstOrDefault(candidate => candidate.Id == obj.Id);
                        if (entity != null)
                            ApplyTriggerPreviewMaterial(obj, entity, trigger, assetService);
                    }
                }

            }

            if (allHaveCollision)
            {
                MapShapeType commonShape = bodyObjects.Select(obj => obj.Body!.Shape).Distinct().Count() == 1
                    ? bodyObjects[0].Body!.Shape
                    : MapShapeType.None;
                if ((commonShape is MapShapeType.Box or MapShapeType.Trimesh) &&
                    bodyObjects.All(obj => obj.Body!.HalfExtents.HasValue))
                {
                    Vector3 halfExtents = bodyObjects[0].Body!.HalfExtents!.Value;
                    if (ImGui.DragFloat3("Half Extents##multiHalfExtents", ref halfExtents, 0.05f, 0.0f, 1000.0f, "%.3f"))
                    {
                        Undo.TrackItem(_frameBeginState);
                        foreach (MapObject obj in bodyObjects)
                        {
                            Vector3 oldExtents = obj.Body!.HalfExtents!.Value;
                            obj.Body.HalfExtents = Vector3.Max(new Vector3(0.05f), halfExtents);
                            if (obj is Brush brush)
                            {
                                brush.ScalePlanes(obj.Body.HalfExtents.Value / Vector3.Max(oldExtents, new Vector3(0.05f)));
                                assetService.InvalidateMesh(brush.Id);
                            }
                            Entity? entity = scene.Entities.FirstOrDefault(candidate => candidate.Id == obj.Id);
                            if (entity != null)
                            {
                                entity.Transform.Scale = obj is Brush
                                    ? Vector3.One
                                    : obj.Body.HalfExtents.Value * 2.0f;
                                if (obj is Brush brushObject)
                                    entity.Mesh = assetService.GetOrCreateMesh(brushObject);
                            }
                        }
                        refreshScene = refreshScene || ImGui.IsItemDeactivatedAfterEdit();
                    }
                }
                else if (commonShape == MapShapeType.Sphere &&
                         bodyObjects.All(obj => obj.Body!.Radius.HasValue))
                {
                    float radius = bodyObjects[0].Body!.Radius!.Value;
                    if (ImGui.DragFloat("Radius##multiRadius", ref radius, 0.05f, 0.0f, 1000.0f, "%.3f"))
                    {
                        Undo.TrackItem(_frameBeginState);
                        foreach (MapObject obj in bodyObjects)
                        {
                            obj.Body!.Radius = MathF.Max(0.05f, radius);
                            Entity? entity = scene.Entities.FirstOrDefault(candidate => candidate.Id == obj.Id);
                            if (entity != null)
                                entity.Transform.Scale = new Vector3(obj.Body.Radius.Value * 2.0f);
                        }
                        refreshScene = refreshScene || ImGui.IsItemDeactivatedAfterEdit();
                    }
                }
                else if (commonShape == MapShapeType.Capsule &&
                         bodyObjects.All(obj => obj.Body!.Radius.HasValue && obj.Body.Height.HasValue))
                {
                    float radius = bodyObjects[0].Body!.Radius!.Value;
                    if (ImGui.DragFloat("Radius##multiCapsuleRadius", ref radius, 0.05f, 0.0f, 1000.0f, "%.3f"))
                    {
                        Undo.TrackItem(_frameBeginState);
                        foreach (MapObject obj in bodyObjects)
                        {
                            obj.Body!.Radius = MathF.Max(0.05f, radius);
                            Entity? entity = scene.Entities.FirstOrDefault(candidate => candidate.Id == obj.Id);
                            if (entity != null && obj.Body.Height.HasValue)
                                entity.Transform.Scale = MeshGenerator.GetCapsuleRenderScale(
                                    obj.Body.Radius.Value,
                                    obj.Body.Height.Value);
                        }
                        refreshScene = refreshScene || ImGui.IsItemDeactivatedAfterEdit();
                    }

                    float height = bodyObjects[0].Body!.Height!.Value;
                    if (ImGui.DragFloat("Height##multiCapsuleHeight", ref height, 0.05f, 0.0f, 1000.0f, "%.3f"))
                    {
                        Undo.TrackItem(_frameBeginState);
                        foreach (MapObject obj in bodyObjects)
                        {
                            obj.Body!.Height = MathF.Max(0.05f, height);
                            Entity? entity = scene.Entities.FirstOrDefault(candidate => candidate.Id == obj.Id);
                            if (entity != null && obj.Body.Radius.HasValue)
                                entity.Transform.Scale = MeshGenerator.GetCapsuleRenderScale(
                                    obj.Body.Radius.Value,
                                    obj.Body.Height.Value);
                        }
                        refreshScene = refreshScene || ImGui.IsItemDeactivatedAfterEdit();
                    }
                }
            }
        }

        if (refreshScene)
            sceneService.PopulateScene(assetService);
    }

    private void DrawInspectorMultiLightSelection(
        IReadOnlyList<MapObject> selection,
        EditorSceneService sceneService)
    {
        ImGui.SeparatorText("Light (common options)");
        Action syncLights = () =>
        {
            foreach (MapObject obj in selection)
                SyncLight(sceneService, obj);
        };

        string[] lightTypes = ["point", "spot", "directional"];
        bool commonType = selection.Select(obj => obj.LightType ?? "point").Distinct().Count() == 1;
        int typeIndex = selection[0].LightType switch
        {
            "spot" => 1,
            "directional" => 2,
            _ => 0
        };
        if (ImGui.Combo("Type##multiLightType", ref typeIndex, lightTypes, lightTypes.Length))
        {
            Undo.TrackItem(_frameBeginState);
            string type = lightTypes[Math.Clamp(typeIndex, 0, lightTypes.Length - 1)];
            foreach (MapObject obj in selection)
                obj.LightType = type;
            syncLights();
        }
        else
        {
            Undo.TrackItem(_frameBeginState);
        }
        if (!commonType)
            ImGui.TextDisabled("Mixed values use the first selected light until edited.");

        Vector3 color = selection[0].LightColor;
        if (ImGui.ColorEdit3("Color##multiLightColor", ref color, ImGuiColorEditFlags.Float))
        {
            Undo.TrackItem(_frameBeginState);
            foreach (MapObject obj in selection)
                obj.LightColor = color;
            syncLights();
        }
        else
        {
            Undo.TrackItem(_frameBeginState);
        }

        float intensity = selection[0].LightIntensity;
        if (ImGui.DragFloat("Intensity##multiLightIntensity", ref intensity, 0.05f, 0.0f, 100.0f))
        {
            Undo.TrackItem(_frameBeginState);
            foreach (MapObject obj in selection)
                obj.LightIntensity = MathF.Max(0.0f, intensity);
            syncLights();
        }
        else
        {
            Undo.TrackItem(_frameBeginState);
        }

        float radius = selection[0].LightRadius;
        if (ImGui.DragFloat("Radius##multiLightRadius", ref radius, 0.1f, 0.1f, 500.0f))
        {
            Undo.TrackItem(_frameBeginState);
            foreach (MapObject obj in selection)
                obj.LightRadius = MathF.Max(0.1f, radius);
            syncLights();
        }
        else
        {
            Undo.TrackItem(_frameBeginState);
        }

        if (selection.All(obj => obj.LightType == "spot"))
        {
            float innerDegrees = float.RadiansToDegrees(selection[0].LightInnerCone);
            if (ImGui.DragFloat("Inner Cone##multiLightInner", ref innerDegrees, 0.5f, 0.0f, 90.0f))
            {
                Undo.TrackItem(_frameBeginState);
                float inner = float.DegreesToRadians(innerDegrees);
                foreach (MapObject obj in selection)
                    obj.LightInnerCone = inner;
                syncLights();
            }
            else
            {
                Undo.TrackItem(_frameBeginState);
            }

            float outerDegrees = float.RadiansToDegrees(selection[0].LightOuterCone);
            if (ImGui.DragFloat("Outer Cone##multiLightOuter", ref outerDegrees, 0.5f, 0.0f, 90.0f))
            {
                Undo.TrackItem(_frameBeginState);
                float outer = float.DegreesToRadians(outerDegrees);
                foreach (MapObject obj in selection)
                    obj.LightOuterCone = outer;
                syncLights();
            }
            else
            {
                Undo.TrackItem(_frameBeginState);
            }
        }

        bool castShadows = selection[0].LightCastShadows;
        if (ImGui.Checkbox("Cast Shadows##multiLightShadows", ref castShadows))
        {
            Undo.TrackItem(_frameBeginState);
            foreach (MapObject obj in selection)
                obj.LightCastShadows = castShadows;
            syncLights();
        }
        else
        {
            Undo.TrackItem(_frameBeginState);
        }

        float shadowBias = selection[0].LightShadowBias;
        if (ImGui.DragFloat("Shadow Bias##multiLightShadowBias", ref shadowBias, 0.0001f, 0.0f, 0.1f, "%.5f"))
        {
            Undo.TrackItem(_frameBeginState);
            foreach (MapObject obj in selection)
                obj.LightShadowBias = MathF.Max(0.0f, shadowBias);
            syncLights();
        }
        else
        {
            Undo.TrackItem(_frameBeginState);
        }

        bool dynamic = selection[0].LightDynamic;
        if (ImGui.Checkbox("Dynamic (Follow Parent)##multiLightDynamic", ref dynamic))
        {
            Undo.TrackItem(_frameBeginState);
            foreach (MapObject obj in selection)
                obj.LightDynamic = dynamic;
            syncLights();
        }
        else
        {
            Undo.TrackItem(_frameBeginState);
        }
    }

    private void DrawInspectorMultiTerrainSelection(
        IReadOnlyList<MapObject> selection,
        EditorSceneService sceneService,
        EditorAssetService assetService)
    {
        ImGui.SeparatorText("Terrain (common options)");

        int chunkQuads = selection[0].TerrainChunkQuads;
        if (ImGui.DragInt("Chunk quads##multiTerrainChunkQuads", ref chunkQuads, 1.0f, 1, 256))
        {
            Undo.TrackItem(_frameBeginState);
            foreach (MapObject obj in selection)
                obj.TerrainChunkQuads = Math.Clamp(chunkQuads, 1, 256);
            sceneService.PopulateScene(assetService);
        }
        else
        {
            Undo.TrackItem(_frameBeginState);
        }

        float pixelError = selection[0].TerrainPixelError;
        if (ImGui.DragFloat("Pixel error##multiTerrainPixelError", ref pixelError, 0.1f, 0.1f, 100.0f, "%.2f px"))
        {
            Undo.TrackItem(_frameBeginState);
            foreach (MapObject obj in selection)
                obj.TerrainPixelError = MathF.Max(0.1f, pixelError);
            sceneService.PopulateScene(assetService);
        }
        else
        {
            Undo.TrackItem(_frameBeginState);
        }

        int collisionLod = selection[0].TerrainCollisionLod;
        if (ImGui.DragInt(
                "Collision LOD##multiTerrainCollisionLod",
                ref collisionLod,
                1.0f,
                0,
                TerrainMeshGenerator.MaxLodLevels - 1))
        {
            Undo.TrackItem(_frameBeginState);
            foreach (MapObject obj in selection)
                obj.TerrainCollisionLod = Math.Clamp(
                    collisionLod,
                    0,
                    TerrainMeshGenerator.MaxLodLevels - 1);
            sceneService.PopulateScene(assetService);
        }
        else
        {
            Undo.TrackItem(_frameBeginState);
        }
    }

    private static Vector3 GetInspectorWorldPosition(MapObject obj, Fuse.Renderer.Scene scene)
    {
        Entity? entity = scene.Entities.FirstOrDefault(candidate => candidate.Id == obj.Id);
        return obj.Body?.Position ?? entity?.Transform.Position ?? Vector3.Zero;
    }

    private static Quaternion GetInspectorWorldRotation(MapObject obj, Fuse.Renderer.Scene scene)
    {
        Entity? entity = scene.Entities.FirstOrDefault(candidate => candidate.Id == obj.Id);
        return obj.Body?.Rotation ?? entity?.Transform.Rotation ?? Quaternion.Identity;
    }

    private static void SetInspectorWorldPose(
        MapObject obj,
        Vector3 position,
        Quaternion rotation,
        Fuse.Renderer.Scene scene,
        EditorSceneService sceneService)
    {
        if (obj.Body != null)
        {
            obj.Body.Position = position;
            obj.Body.Rotation = rotation;
        }

        Entity? entity = scene.Entities.FirstOrDefault(candidate => candidate.Id == obj.Id);
        if (entity != null)
        {
            entity.Transform.Position = position;
            entity.Transform.Rotation = rotation;
        }
        SyncLight(sceneService, obj);
    }

    private static bool CanEditInspectorScale(MapObject obj)
    {
        if (obj.IsModel)
            return true;
        if (obj.Body == null)
            return false;
        return obj.Body.Shape switch
        {
            MapShapeType.Box or MapShapeType.Trimesh => obj.Body.HalfExtents.HasValue,
            MapShapeType.Sphere => obj.Body.Radius.HasValue,
            MapShapeType.Capsule => obj.Body.Radius.HasValue && obj.Body.Height.HasValue,
            _ => false
        };
    }

    private static Vector3 GetInspectorScale(MapObject obj, Fuse.Renderer.Scene scene)
    {
        if (obj.IsModel)
            return obj.ModelScale;
        if (obj.Body != null)
        {
            if ((obj.Body.Shape is MapShapeType.Box or MapShapeType.Trimesh) && obj.Body.HalfExtents.HasValue)
                return obj.Body.HalfExtents.Value * 2.0f;
            if (obj.Body.Shape == MapShapeType.Sphere && obj.Body.Radius.HasValue)
                return new Vector3(obj.Body.Radius.Value * 2.0f);
            if (obj.Body.Shape == MapShapeType.Capsule && obj.Body.Radius.HasValue && obj.Body.Height.HasValue)
                return new Vector3(obj.Body.Radius.Value * 2.0f, obj.Body.Height.Value, obj.Body.Radius.Value * 2.0f);
        }
        return scene.Entities.FirstOrDefault(candidate => candidate.Id == obj.Id)?.Transform.Scale ?? Vector3.One;
    }

    private static void ApplyInspectorScale(
        MapObject obj,
        Vector3 factor,
        Fuse.Renderer.Scene scene,
        EditorSceneService sceneService,
        EditorAssetService assetService)
    {
        if (obj.IsModel)
        {
            obj.ModelScale = Vector3.Max(new Vector3(0.01f), obj.ModelScale * factor);
        }
        else if (obj.Body != null)
        {
            switch (obj.Body.Shape)
            {
                case MapShapeType.Box:
                case MapShapeType.Trimesh:
                    if (obj.Body.HalfExtents.HasValue)
                    {
                        Vector3 oldExtents = obj.Body.HalfExtents.Value;
                        obj.Body.HalfExtents = Vector3.Max(new Vector3(0.05f), oldExtents * factor);
                        if (obj is Brush brush)
                        {
                            brush.ScalePlanes(obj.Body.HalfExtents.Value / Vector3.Max(oldExtents, new Vector3(0.05f)));
                            assetService.InvalidateMesh(brush.Id);
                        }
                    }
                    break;
                case MapShapeType.Sphere:
                    if (obj.Body.Radius.HasValue)
                    {
                        float averageFactor = (factor.X + factor.Y + factor.Z) / 3.0f;
                        obj.Body.Radius = MathF.Max(0.05f, obj.Body.Radius.Value * averageFactor);
                    }
                    break;
                case MapShapeType.Capsule:
                    if (obj.Body.Radius.HasValue)
                        obj.Body.Radius = MathF.Max(0.05f, obj.Body.Radius.Value *
                            (factor.X + factor.Z) * 0.5f);
                    if (obj.Body.Height.HasValue)
                        obj.Body.Height = MathF.Max(0.05f, obj.Body.Height.Value * factor.Y);
                    break;
            }
        }

        Entity? entity = scene.Entities.FirstOrDefault(candidate => candidate.Id == obj.Id);
        if (entity != null)
        {
            entity.Transform.Scale = GetInspectorScale(obj, scene);
            SyncPrimitiveVisualScale(obj, entity);
            if (obj is Brush brushObject)
                entity.Mesh = assetService.GetOrCreateMesh(brushObject);
        }
        SyncLight(sceneService, obj);
    }

    private static void SyncPrimitiveVisualScale(MapObject obj, Entity entity)
    {
        if (obj.IsModel || obj.Body == null)
            return;

        if (obj.Body.Shape == MapShapeType.Sphere && obj.Body.Radius.HasValue)
        {
            entity.Transform.Scale = MeshGenerator.GetSphereRenderScale(obj.Body.Radius.Value);
        }
        else if (obj.Body.Shape == MapShapeType.Capsule &&
                 obj.Body.Radius.HasValue && obj.Body.Height.HasValue)
        {
            entity.Transform.Scale = MeshGenerator.GetCapsuleRenderScale(
                obj.Body.Radius.Value,
                obj.Body.Height.Value);
        }
    }

    private static Vector3 InspectorQuaternionToEuler(Quaternion q)
    {
        float t0 = 2.0f * (q.W * q.X + q.Y * q.Z);
        float t1 = 1.0f - 2.0f * (q.X * q.X + q.Y * q.Y);
        float pitch = MathF.Atan2(t0, t1);
        float t2 = float.Clamp(2.0f * (q.W * q.Y - q.Z * q.X), -1.0f, 1.0f);
        float yaw = MathF.Asin(t2);
        float t3 = 2.0f * (q.W * q.Z + q.X * q.Y);
        float t4 = 1.0f - 2.0f * (q.Y * q.Y + q.Z * q.Z);
        float roll = MathF.Atan2(t3, t4);
        return new Vector3(
            float.RadiansToDegrees(pitch),
            float.RadiansToDegrees(yaw),
            float.RadiansToDegrees(roll));
    }

    private static Quaternion InspectorEulerToQuaternion(Vector3 euler)
    {
        Quaternion pitch = Quaternion.CreateFromAxisAngle(Vector3.UnitX, float.DegreesToRadians(euler.X));
        Quaternion yaw = Quaternion.CreateFromAxisAngle(Vector3.UnitY, float.DegreesToRadians(euler.Y));
        Quaternion roll = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, float.DegreesToRadians(euler.Z));
        return Quaternion.Normalize(roll * yaw * pitch);
    }

    private List<MapObject> GetHierarchyDragObjects(MapObject source, MapDocument doc)
    {
        IEnumerable<MapObject> candidates = _selectedObjects.Contains(source)
            ? _selectedObjects.Where(doc.Objects.Contains)
            : [source];

        List<MapObject> selected = candidates
            .Distinct()
            .ToList();

        // If a parent and one of its children are both selected, moving the
        // parent already moves the complete subtree. Drag only the selected
        // roots so the operation cannot create redundant or cyclic parenting.
        return selected
            .Where(candidate => !selected.Any(other =>
                other != candidate && IsDescendantOf(candidate, other, doc)))
            .OrderBy(candidate => doc.Objects.IndexOf(candidate))
            .ToList();
    }

    private static void UpdateEntityParent(
        Fuse.Renderer.Scene scene,
        MapObject obj,
        string? parentId)
    {
        var entity = scene.Entities.FirstOrDefault(e => e.Id == obj.Id);
        if (entity == null)
            return;

        entity.ParentId = parentId ?? "";
        var parentEntity = string.IsNullOrEmpty(parentId)
            ? null
            : scene.Entities.FirstOrDefault(e => e.Id == parentId);
        if (parentEntity != null)
        {
            entity.InitialRelativePosition = entity.Transform.Position - parentEntity.Transform.Position;
            entity.InitialRelativeRotation = Quaternion.Inverse(parentEntity.Transform.Rotation) * entity.Transform.Rotation;
        }
    }

    private bool CanDropHierarchyObjects(
        IReadOnlyCollection<MapObject> draggedObjects,
        MapObject target,
        MapDocument doc)
    {
        return draggedObjects.Count > 0 &&
            !draggedObjects.Any(dragged =>
                dragged == target || IsDescendantOf(target, dragged, doc));
    }

    private void DrawObjectNode(MapObject obj, MapDocument doc, EditorSceneService sceneService, EditorAssetService assetService, CommandHistory history, EditorViewport viewport3D, EditorViewport viewportTop, EditorViewport viewportFront, EditorViewport viewportSide, string filter, ref MapObject? objectToDelete, ref MapObject? objectToDuplicate)
    {
        if (!HierarchyMatchesFilter(obj, doc, filter))
            return;
        var children = doc.Objects.Where(o => o.ParentId == obj.Id).ToList();
        bool isSelected = _selectedObjects.Contains(obj);
        bool hasChildren = children.Count > 0;

        ImGuiTreeNodeFlags flags = ImGuiTreeNodeFlags.FramePadding | ImGuiTreeNodeFlags.SpanAvailWidth |
                                   (isSelected ? ImGuiTreeNodeFlags.Selected : 0);
        if (!hasChildren)
        {
            flags |= ImGuiTreeNodeFlags.Leaf;
        }
        else
        {
            flags |= ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.OpenOnDoubleClick;
        }

        bool isGloballyVisible = obj.IsGloballyVisible(doc);

        bool isOpen = ImGui.TreeNodeEx($"##node_{obj.Id}", flags, "");
        Vector2 nodeMin = ImGui.GetItemRectMin();
        Vector2 nodeMax = ImGui.GetItemRectMax();
        float labelStart = nodeMin.X + ImGui.GetTreeNodeToLabelSpacing();
        HierarchyIconKind iconKind = GetHierarchyIconKind(obj);
        uint iconColor = ImGui.ColorConvertFloat4ToU32(GetHierarchyIconColor(iconKind));
        ImDrawListPtr nodeDrawList = ImGui.GetWindowDrawList();
        Vector2 iconCenter = new(labelStart + 8.0f, (nodeMin.Y + nodeMax.Y) * 0.5f);
        DrawHierarchyIcon(nodeDrawList, iconCenter, iconKind, iconColor);
        nodeDrawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), new Vector2(labelStart + 20.0f, nodeMin.Y + ImGui.GetStyle().FramePadding.Y),
            ImGui.GetColorU32(ImGuiCol.Text), obj.Id);

        if (ImGui.BeginDragDropSource())
        {
            _draggedObject = obj;
            _draggedObjects = GetHierarchyDragObjects(obj, doc);
            if (_draggedObjects.Count > 1)
                ImGui.Text($"Moving/Grouping: {_draggedObjects.Count} objects");
            else
                ImGui.Text($"Moving/Grouping: {obj.Id}");
            ImGui.SetDragDropPayload("HIERARCHY_NODE", IntPtr.Zero, 0);
            ImGui.EndDragDropSource();
        }

        if (ImGui.BeginDragDropTarget())
        {
            var payload = ImGui.AcceptDragDropPayload("HIERARCHY_NODE", ImGuiDragDropFlags.AcceptNoDrawDefaultRect);
            
            var rectMin = ImGui.GetItemRectMin();
            var rectMax = ImGui.GetItemRectMax();
            var mousePos = ImGui.GetMousePos();
            float height = rectMax.Y - rectMin.Y;
            bool isReorderAbove = mousePos.Y < rectMin.Y + height * 0.25f;
            bool isReorderBelow = mousePos.Y > rectMax.Y - height * 0.25f;

            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem))
            {
                var drawList = ImGui.GetWindowDrawList();
                uint color = ImGui.GetColorU32(ImGuiCol.DragDropTarget);

                if (isReorderAbove)
                {
                    drawList.AddLine(new Vector2(rectMin.X, rectMin.Y), new Vector2(rectMax.X, rectMin.Y), color, 2.0f);
                }
                else if (isReorderBelow)
                {
                    drawList.AddLine(new Vector2(rectMin.X, rectMax.Y), new Vector2(rectMax.X, rectMax.Y), color, 2.0f);
                }
                else
                {
                    drawList.AddRect(rectMin, rectMax, color, 0.0f, ImDrawFlags.None, 2.0f);
                }
            }

            if (payload.NativePtr != null)
            {
                List<MapObject> draggedObjects = _draggedObjects.Count > 0
                    ? _draggedObjects
                    : _draggedObject != null ? [_draggedObject] : [];

                if (CanDropHierarchyObjects(draggedObjects, obj, doc))
                {
                    var pre = doc.Serialize();

                    if (isReorderAbove || isReorderBelow)
                    {
                        // REORDER
                        foreach (MapObject draggedObject in draggedObjects)
                        {
                            draggedObject.ParentId = obj.ParentId;
                            doc.Objects.Remove(draggedObject);
                        }

                        int insertIndex = doc.Objects.IndexOf(obj);
                        if (isReorderBelow) insertIndex++;
                        if (insertIndex < 0) insertIndex = 0;
                        if (insertIndex > doc.Objects.Count) insertIndex = doc.Objects.Count;

                        foreach (MapObject draggedObject in draggedObjects)
                        {
                            doc.Objects.Insert(insertIndex++, draggedObject);
                            UpdateEntityParent(sceneService.Scene, draggedObject, obj.ParentId);
                        }
                    }
                    else
                    {
                        // REPARENT
                        foreach (MapObject draggedObject in draggedObjects)
                        {
                            draggedObject.ParentId = obj.Id;
                            UpdateEntityParent(sceneService.Scene, draggedObject, obj.Id);
                        }
                    }

                    var post = doc.Serialize();
                    sceneService.MarkModified(post);
                    history.PushCommand(new SnapshotCommand(sceneService, assetService, pre, post));
                    sceneService.PopulateScene(assetService);
                }
                _draggedObject = null;
                _draggedObjects.Clear();
            }
            ImGui.EndDragDropTarget();
        }

        if (ImGui.IsItemClicked() && !ImGui.IsItemToggledOpen())
        {
            if (ImGui.GetIO().KeyCtrl)
            {
                if (_selectedObjects.Contains(obj))
                {
                    _selectedObjects.Remove(obj);
                    if (_selectedObject == obj)
                        _selectedObject = _selectedObjects.FirstOrDefault();
                }
                else
                {
                    _selectedObjects.Add(obj);
                    _selectedObject = obj;
                }
            }
            else
            {
                // Clicking an already selected row is also the beginning of
                // a possible drag. Keep the rest of the selection intact so
                // dragging one selected object can move the whole selection.
                if (!_selectedObjects.Contains(obj))
                {
                    _selectedObjects.Clear();
                    _selectedObjects.Add(obj);
                }
                _selectedObject = obj;
            }
            _lastSelectionTime = ImGui.GetTime();
        }

        if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
        {
            FocusCameraOnObject(obj, sceneService, viewport3D, viewportTop, viewportFront, viewportSide);
        }

        if (ImGui.BeginPopupContextItem($"context_{obj.Id}"))
        {
            if (!_selectedObjects.Contains(obj))
            {
                _selectedObjects.Clear();
                _selectedObjects.Add(obj);
                _selectedObject = obj;
            }

            if (ImGui.MenuItem("Focus Camera"))
            {
                FocusCameraOnObject(obj, sceneService, viewport3D, viewportTop, viewportFront, viewportSide);
            }
            if (ImGui.MenuItem("Toggle Visibility"))
            {
                ToggleObjectVisibility(obj, doc, sceneService.Scene, sceneService, assetService, history);
            }
            ImGui.Separator();
            if (ImGui.MenuItem("Group Selected"))
            {
                GroupSelected(sceneService, assetService, history);
            }
            if (_selectedObjects.Any(o => !string.IsNullOrEmpty(o.ParentId)))
            {
                if (ImGui.MenuItem("Ungroup Selected"))
                {
                    UngroupSelected(sceneService, assetService, history);
                }
            }
            ImGui.Separator();
            if (ImGui.MenuItem("Duplicate"))
            {
                objectToDuplicate = obj;
            }
            if (ImGui.MenuItem("Delete"))
            {
                objectToDelete = obj;
            }
            ImGui.EndPopup();
        }

        ImGui.SameLine();
        float rightAlignPos = ImGui.GetWindowWidth() - 35;
        ImGui.SetCursorPosX(rightAlignPos);

        bool inheritedHidden = !isGloballyVisible && obj.Visible;

        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0, 0, 0, 0));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0, 0, 0, 0));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.2f, 0.2f, 0.2f, 0.5f));

        ImGui.PushID($"visibility_{obj.Id}");
        bool visibilityButtonClicked = ImGui.Button("##toggle", new Vector2(24, 20));
        Vector2 visibilityMin = ImGui.GetItemRectMin();
        Vector2 visibilityMax = ImGui.GetItemRectMax();
        // Keep the icon clickable even when a tree row overlaps the right-side item.
        bool visibilityHitClicked = !visibilityButtonClicked &&
            ImGui.IsMouseClicked(ImGuiMouseButton.Left) &&
            ImGui.IsMouseHoveringRect(visibilityMin, visibilityMax);
        ImGui.PopID();

        if (visibilityButtonClicked || visibilityHitClicked)
        {
            ToggleObjectVisibility(obj, doc, sceneService.Scene, sceneService, assetService, history);
        }
        ImGui.PopStyleColor(3);
        uint visibilityColor = ImGui.ColorConvertFloat4ToU32(inheritedHidden
            ? new Vector4(0.50f, 0.50f, 0.52f, 0.65f)
            : new Vector4(0.78f, 0.82f, 0.88f, 1.0f));
        DrawVisibilityIcon(ImGui.GetWindowDrawList(), (visibilityMin + visibilityMax) * 0.5f, obj.Visible && isGloballyVisible, visibilityColor);
        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.TextUnformatted(obj.Visible ? (isGloballyVisible ? "Visible" : "Hidden by parent") : "Hidden");
            ImGui.EndTooltip();
        }

        if (isOpen)
        {
            if (hasChildren)
            {
                foreach (var child in children)
                {
                    DrawObjectNode(child, doc, sceneService, assetService, history, viewport3D, viewportTop, viewportFront, viewportSide, filter, ref objectToDelete, ref objectToDuplicate);
                }
            }
            ImGui.TreePop();
        }
    }

    private void DrawHierarchyCategory(
        string categoryId,
        string label,
        IEnumerable<MapObject> rootObjects,
        MapDocument doc,
        EditorSceneService sceneService,
        EditorAssetService assetService,
        CommandHistory history,
        EditorViewport viewport3D,
        EditorViewport viewportTop,
        EditorViewport viewportFront,
        EditorViewport viewportSide,
        string filter,
        ref MapObject? objectToDelete,
        ref MapObject? objectToDuplicate)
    {
        List<MapObject> matchingObjects = rootObjects
            .Where(obj => HierarchyMatchesFilter(obj, doc, filter))
            .ToList();
        if (matchingObjects.Count == 0)
            return;

        ImGui.PushStyleColor(ImGuiCol.Header, new Vector4(0.10f, 0.15f, 0.22f, 1.0f));
        ImGui.PushStyleColor(ImGuiCol.HeaderHovered, new Vector4(0.15f, 0.23f, 0.33f, 1.0f));
        ImGui.PushStyleColor(ImGuiCol.HeaderActive, new Vector4(0.18f, 0.29f, 0.40f, 1.0f));
        bool isOpen = ImGui.CollapsingHeader($"{label}  ({matchingObjects.Count})##hierarchyCategory_{categoryId}", ImGuiTreeNodeFlags.DefaultOpen);
        ImGui.PopStyleColor(3);

        if (!isOpen)
            return;

        ImGui.PushStyleVar(ImGuiStyleVar.IndentSpacing, 18.0f);
        foreach (MapObject obj in matchingObjects)
        {
            DrawObjectNode(obj, doc, sceneService, assetService, history, viewport3D, viewportTop, viewportFront, viewportSide,
                filter, ref objectToDelete, ref objectToDuplicate);
        }
        ImGui.PopStyleVar();
    }

    private static bool HierarchyMatchesFilter(MapObject obj, MapDocument doc, string filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
            return true;

        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool Matches(MapObject candidate)
        {
            if (!visited.Add(candidate.Id))
                return false;
            if (candidate.Id.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                (candidate.LightType?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (candidate.Model?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (candidate.MaterialPath?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false))
                return true;

            foreach (MapObject child in doc.Objects.Where(child =>
                         string.Equals(child.ParentId, candidate.Id, StringComparison.OrdinalIgnoreCase)))
            {
                if (Matches(child))
                    return true;
            }
            return false;
        }

        return Matches(obj);
    }

    private void DuplicateObjects(List<MapObject> objs, EditorSceneService sceneService, EditorAssetService assetService, CommandHistory history)
    {
        if (objs == null || objs.Count == 0) return;
        var doc = sceneService.Document;
        var pre = doc.Serialize();
        
        var toDuplicate = new HashSet<MapObject>();
        foreach (var obj in objs)
        {
            AddWithDescendants(obj, doc, toDuplicate);
        }
        
        var oldToNewMap = new Dictionary<string, MapObject>();
        var duplicates = new List<MapObject>();
        
        foreach (var obj in toDuplicate)
        {
            var serialized = MapDocument.SerializeObject(obj);
            var clone = MapDocument.ParseObject(serialized);
            
            string newId = obj.Id + "_copy";
            clone.Id = newId;
            oldToNewMap[obj.Id] = clone;
            duplicates.Add(clone);
        }
        
        foreach (var clone in duplicates)
        {
            var original = toDuplicate.FirstOrDefault(o => o.Id + "_copy" == clone.Id);
            if (original != null && !string.IsNullOrEmpty(original.ParentId) && oldToNewMap.TryGetValue(original.ParentId, out var newParent))
            {
                clone.ParentId = newParent.Id;
            }
        }
        
        foreach (var clone in duplicates)
        {
            doc.Objects.Add(clone);
        }
        SceneNameManager.EnsureAllUnique(doc);
        
        _selectedObjects.Clear();
        foreach (var dup in duplicates)
        {
            _selectedObjects.Add(dup);
        }
        _selectedObject = duplicates.LastOrDefault();
        
        var post = doc.Serialize();
        sceneService.MarkModified(post);
        history.PushCommand(new SnapshotCommand(sceneService, assetService, pre, post));
        sceneService.PopulateScene(assetService);
    }

    private void DeleteObjects(List<MapObject> objs, EditorSceneService sceneService, EditorAssetService assetService, CommandHistory history)
    {
        if (objs == null || objs.Count == 0) return;
        var doc = sceneService.Document;
        var pre = doc.Serialize();
        
        var toDelete = new HashSet<MapObject>();
        foreach (var obj in objs)
        {
            AddWithDescendants(obj, doc, toDelete);
        }
        
        bool anyRemoved = false;
        foreach (var obj in toDelete)
        {
            if (doc.Objects.Remove(obj))
            {
                _selectedObjects.Remove(obj);
                anyRemoved = true;
            }
        }
        
        if (anyRemoved)
        {
            if (_selectedObject != null && !doc.Objects.Contains(_selectedObject))
            {
                _selectedObject = _selectedObjects.FirstOrDefault();
            }
            var post = doc.Serialize();
            sceneService.MarkModified(post);
            history.PushCommand(new SnapshotCommand(sceneService, assetService, pre, post));
            sceneService.PopulateScene(assetService);
        }
    }

    private void LaunchGame(EditorSceneService sceneService)
    {
        var existing = System.Diagnostics.Process.GetProcessesByName("Fuse");
        if (existing.Length > 0)
        {
            Logger.WarnPopup("The game is already running.", "The game is already running.\nPlease close it before opening a new instance.");
            return;
        }
        
        if (string.IsNullOrEmpty(sceneService.MapPath))
        {
            _showSaveAsDialog = true;
            _saveMapName = "map.bth";
            return;
        }
        if (!sceneService.SaveMap())
        {
            ShowDocumentError(sceneService.LastError);
            return;
        }

        try
        {
            string mapFile = Path.GetFileName(sceneService.MapPath);
            string configuredFusePath = _settings.FuseExecutablePath.Trim().Trim('"');
            string fuseExecutable = string.IsNullOrWhiteSpace(configuredFusePath)
                ? Path.Combine(AppContext.BaseDirectory, "Fuse.exe")
                : ResolveFusePath(configuredFusePath);
            if (!File.Exists(fuseExecutable))
            {
                string source = string.IsNullOrWhiteSpace(configuredFusePath)
                    ? "Fuse.exe was not found beside Blowtorch. Build Blowtorch first"
                    : "The configured Fuse executable was not found. Choose another one in Edit > Blowtorch Settings";
                ShowDocumentError($"{source}: {fuseExecutable}");
                return;
            }

            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = fuseExecutable,
                WorkingDirectory = AppContext.BaseDirectory,
                UseShellExecute = false
            };
            startInfo.ArgumentList.Add(mapFile);
            System.Diagnostics.Process.Start(startInfo);
        }
        catch (Exception ex)
        {
            ShowDocumentError($"Could not launch Fuse: {ex.Message}");
        }
    }

    private void OpenBlowtorchSettings()
    {
        _fuseExecutableDraft = _settings.FuseExecutablePath;
        _settingsStatus = "";
        _showBlowtorchSettings = true;
    }

    private void DrawBlowtorchSettingsWindow()
    {
        if (!_showBlowtorchSettings)
            return;

        ImGui.SetNextWindowSize(new Vector2(620, 230), ImGuiCond.FirstUseEver);
        bool open = _showBlowtorchSettings;
        if (ImGui.Begin("Blowtorch Settings##BlowtorchSettings", ref open,
                ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.TextColored(new Vector4(0.75f, 0.86f, 1.0f, 1.0f), "Blowtorch settings");
            ImGui.Separator();
            ImGui.TextWrapped("Configure which Fuse executable is launched by Play/F5. " +
                              "Leave this empty to use Fuse.exe generated beside Blowtorch.");
            ImGui.Spacing();

            ImGui.TextUnformatted("Fuse executable");
            ImGui.SetNextItemWidth(-110);
            ImGui.InputText("##FuseExecutableSetting", ref _fuseExecutableDraft, 1024);
            ImGui.SameLine();
            if (ImGui.Button("Browse..."))
                BrowseForFuseExecutable();

            string previewPath = _fuseExecutableDraft.Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(previewPath))
            {
                ImGui.TextColored(new Vector4(0.60f, 0.75f, 0.90f, 1.0f),
                    $"Automatic path: {Path.Combine(AppContext.BaseDirectory, "Fuse.exe")}");
            }
            else if (TryResolveFusePath(previewPath, out string resolvedPreviewPath) &&
                     File.Exists(resolvedPreviewPath))
            {
                ImGui.TextColored(new Vector4(0.35f, 0.90f, 0.50f, 1.0f), "Executable found.");
            }
            else
            {
                ImGui.TextColored(new Vector4(1.0f, 0.68f, 0.25f, 1.0f),
                    "File not found. Correct the path before using Play/F5.");
            }

            ImGui.Spacing();
            if (ImGui.Button("Save settings", new Vector2(130, 0)))
            {
                _settings.FuseExecutablePath = previewPath;
                _settingsStatus = _settings.Save(out string error)
                    ? "Settings saved."
                    : error;
            }
            ImGui.SameLine();
            if (ImGui.Button("Use automatic path", new Vector2(150, 0)))
            {
                _fuseExecutableDraft = "";
                _settings.FuseExecutablePath = "";
                _settingsStatus = _settings.Save(out string error)
                    ? "Automatic Fuse path restored."
                    : error;
            }
            ImGui.SameLine();
            if (ImGui.Button("Close", new Vector2(80, 0)))
                open = false;

            if (!string.IsNullOrEmpty(_settingsStatus))
            {
                ImGui.Spacing();
                ImGui.TextDisabled(_settingsStatus);
            }
            ImGui.Spacing();
            ImGui.TextDisabled($"Stored in: {BlowtorchSettings.StoragePath}");
        }
        ImGui.End();
        _showBlowtorchSettings = open;
    }

    private void BrowseForFuseExecutable()
    {
        using var dialog = new System.Windows.Forms.OpenFileDialog
        {
            Title = "Select Fuse executable",
            Filter = "Fuse executable (Fuse.exe)|Fuse.exe|Executable files (*.exe)|*.exe|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
            FileName = "Fuse.exe"
        };

        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            _fuseExecutableDraft = dialog.FileName;
            _settingsStatus = "Executable selected. Save settings to keep it.";
        }
    }

    private static string ResolveFusePath(string path)
    {
        if (Path.IsPathRooted(path))
            return Path.GetFullPath(path);
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, path));
    }

    private static bool TryResolveFusePath(string path, out string resolvedPath)
    {
        try
        {
            resolvedPath = ResolveFusePath(path);
            return true;
        }
        catch (ArgumentException)
        {
            resolvedPath = "";
            return false;
        }
        catch (NotSupportedException)
        {
            resolvedPath = "";
            return false;
        }
    }

    private void SaveMapOrPrompt(EditorSceneService sceneService)
    {
        if (string.IsNullOrEmpty(sceneService.MapPath))
        {
            _showSaveAsDialog = true;
            _saveMapName = "map.bth";
        }
        else
        {
            if (!sceneService.SaveMap())
                ShowDocumentError(sceneService.LastError);
        }
    }

    private static string? OpenFileDialog(string initialDir, string filter)
    {
        if (!Directory.Exists(initialDir))
            initialDir = AppContext.BaseDirectory;
        var files = Directory.GetFiles(initialDir, "*.bth");
        return files.Length > 0 ? files[0] : null;
    }

    private void HandleKeyboardShortcuts(
        EditorSceneService sceneService,
        EditorAssetService assetService,
        CommandHistory history,
        EditorInputService inputService)
    {
        var io = ImGui.GetIO();

        if (!inputService.IsMapContext)
            return;
        
        // Handle shortcuts only if we're not typing in text inputs
        if (io.WantTextInput || (io.WantCaptureKeyboard && ImGui.IsAnyItemActive())) return;

        // Match the conventional 3D-editor shortcut: Ctrl+Alt+Q toggles the
        // viewport layout without changing any camera state.
        if (io.KeyCtrl && io.KeyAlt && ImGui.IsKeyPressed(ImGuiKey.Q))
        {
            _viewportLayout = _viewportLayout == ViewportLayout.Quad
                ? ViewportLayout.PerspectiveOnly
                : ViewportLayout.Quad;
        }

        if (io.KeyCtrl)
        {
            if (ImGui.IsKeyPressed(ImGuiKey.Z)) history.Undo();
            if (ImGui.IsKeyPressed(ImGuiKey.Y)) history.Redo();
            if (ImGui.IsKeyPressed(ImGuiKey.S)) SaveMapOrPrompt(sceneService);
            if (ImGui.IsKeyPressed(ImGuiKey.N)) RequestDocumentAction(PendingDocumentAction.New, sceneService);
            if (ImGui.IsKeyPressed(ImGuiKey.O)) OpenMapDialog();
            if (ImGui.IsKeyPressed(ImGuiKey.D) && _selectedObjects.Count > 0)
            {
                DuplicateObjects(_selectedObjects.ToList(), sceneService, assetService, history);
            }
        }
        //else if (io.KeyShift)
        //{
        //    if (ImGui.IsKeyPressed(ImGuiKey.D) && _selectedObjects.Count > 0)
        //    {
        //        DuplicateObjects(_selectedObjects.ToList(), sceneService, assetService, history);
        //    }
        //}
        // avoids conflict with the camera sprint.

        if (io.KeyAlt && ImGui.IsKeyPressed(ImGuiKey.B) &&
            IsEditingBrushComponents && _brushComponentMode == BrushComponentMode.Edge)
        {
            ExecuteBrushEditOperation(sceneService, assetService, history, "BridgeEdges");
        }

        if (ImGui.IsKeyPressed(ImGuiKey.Delete) &&
            IsEditingBrushComponents &&
            _brushComponentMode == BrushComponentMode.Face &&
            _selectedBrushFaces.Count > 0)
        {
            ExecuteBrushEditOperation(sceneService, assetService, history, "DeleteFaces");
        }
        else if (ImGui.IsKeyPressed(ImGuiKey.Delete) && _selectedObjects.Count > 0)
        {
            DeleteObjects(_selectedObjects.ToList(), sceneService, assetService, history);
        }

        if (ImGui.IsKeyPressed(ImGuiKey.F) && _selectedObject != null)
        {
            _focusCameraRequested = true;
        }
        
        if (ImGui.IsKeyPressed(ImGuiKey.F5))
        {
            LaunchGame(sceneService);
        }

        if (ImGui.IsKeyPressed(ImGuiKey.LeftBracket))
        {
            _snapGrid = MathF.Max(0.0625f, _snapGrid * 0.5f);
        }
        if (ImGui.IsKeyPressed(ImGuiKey.RightBracket))
        {
            _snapGrid = MathF.Min(64.0f, _snapGrid * 2.0f);
        }
    }

    private void DrawMenuBar(
        EditorWindow window,
        EditorSceneService sceneService,
        EditorAssetService assetService,
        CommandHistory history,
        EditorViewport viewport3D)
    {
        if (ImGui.BeginMainMenuBar())
        {
            if (ImGui.BeginMenu("File"))
            {
                if (ImGui.MenuItem("New", "Ctrl+N"))
                {
                    RequestDocumentAction(PendingDocumentAction.New, sceneService);
                }
                if (ImGui.MenuItem("Open...", "Ctrl+O"))
                {
                    OpenMapDialog();
                }
                if (ImGui.MenuItem("Save", "Ctrl+S"))
                {
                    SaveMapOrPrompt(sceneService);
                }
                if (ImGui.MenuItem("Save As..."))
                {
                    _showSaveAsDialog = true;
                    _saveMapName = !string.IsNullOrEmpty(sceneService.MapPath)
                        ? Path.GetFileName(sceneService.MapPath)
                        : "map.bth";
                }
                ImGui.Separator();
                if (ImGui.MenuItem("Play", "F5"))
                {
                    LaunchGame(sceneService);
                }
                ImGui.Separator();
                if (ImGui.MenuItem("Exit"))
                    RequestDocumentAction(PendingDocumentAction.Exit, sceneService);
                ImGui.EndMenu();
            }
            if (ImGui.BeginMenu("Edit"))
            {
                if (ImGui.MenuItem("Undo", "Ctrl+Z", false, history.CanUndo)) history.Undo();
                if (ImGui.MenuItem("Redo", "Ctrl+Y", false, history.CanRedo)) history.Redo();
                if (ImGui.MenuItem("Blowtorch Settings..."))
                    OpenBlowtorchSettings();
                ImGui.EndMenu();
            }
            if (ImGui.BeginMenu("CSG"))
            {
                int brushCount = _selectedObjects.Count(o => o is Brush);
                if (ImGui.MenuItem("Subtract (Carve)", "", false, brushCount >= 2))
                {
                    PerformCsgOperation(sceneService, assetService, history, "Subtract");
                }
                if (ImGui.MenuItem("Intersect", "", false, brushCount >= 2))
                {
                    PerformCsgOperation(sceneService, assetService, history, "Intersect");
                }
                if (ImGui.MenuItem("Union (Merge)", "", false, brushCount >= 2))
                {
                    PerformCsgOperation(sceneService, assetService, history, "Union");
                }
                if (ImGui.MenuItem("Make Hollow...", "", false, brushCount >= 1))
                {
                    _showHollowDialog = true;
                }
                ImGui.EndMenu();
            }
            if (ImGui.BeginMenu("Materials"))
            {
                if (ImGui.MenuItem("Open Material Graph"))
                    _materialEditor.OpenStandalone();

                if (ImGui.MenuItem("New Material..."))
                    RequestNewMaterial(_selectedObjects.Count > 0 ? _selectedObjects : (_selectedObject != null ? [_selectedObject] : []));

                bool canOpenSelected = _selectedObject != null && !string.IsNullOrWhiteSpace(_selectedObject.MaterialPath);
                if (ImGui.MenuItem("Open Selected Material", "", false, canOpenSelected))
                    _materialEditor.Open(_selectedObject!.MaterialPath!);

                ImGui.Separator();
                if (ImGui.MenuItem("Convert Legacy Textures to Materials"))
                    ConvertLegacyTexturesToMaterials(sceneService, assetService, history);
                ImGui.EndMenu();
            }
            if (ImGui.BeginMenu("Geometry"))
            {
                if (ImGui.MenuItem("Open Geometry Graph"))
                    _geometryEditor.OpenStandalone();
                if (ImGui.MenuItem("Create Geometry Graph"))
                {
                    _geometryEditor.OpenStandalone();
                    _showAssetBrowser = false;
                }
                ImGui.EndMenu();
            }
            if (ImGui.BeginMenu("View"))
            {
                ImGui.MenuItem("Map Objects", "", ref _showMapWindow);
                ImGui.MenuItem("Asset Browser", "", ref _showAssetBrowser);
                ImGui.MenuItem("Raw JSON", "", ref _showJsonWindow);
                ImGui.MenuItem("Diagnostics", "", ref _showDiagnostics);
                ImGui.MenuItem("Show hitboxes", "", ref _showHitBoxes);
                ImGui.Separator();
                if (ImGui.MenuItem("Quad View", "Ctrl+Alt+Q", _viewportLayout == ViewportLayout.Quad))
                    _viewportLayout = ViewportLayout.Quad;
                if (ImGui.MenuItem("3D View Only", "Ctrl+Alt+Q", _viewportLayout == ViewportLayout.PerspectiveOnly))
                    _viewportLayout = ViewportLayout.PerspectiveOnly;
                ImGui.Separator();
                if (ImGui.MenuItem("3D Viewport Shadows", "", viewport3D.ShadowsEnabled))
                    viewport3D.ShadowsEnabled = !viewport3D.ShadowsEnabled;
                ImGui.EndMenu();
            }
            ImGui.Separator();
            ImGui.TextDisabled(sceneService.IsDirty ? "Modified" : "Saved");
            ImGui.EndMainMenuBar();
        }
    }

    private bool DrawToolbarButton(string label, string shortcut, bool selected)
    {
        Vector2 textSize = ImGui.CalcTextSize(label);
        float width = MathF.Max(62.0f, textSize.X + 24.0f);
        ImGui.PushStyleColor(ImGuiCol.Button, selected
            ? new Vector4(0.16f, 0.39f, 0.62f, 1.0f)
            : new Vector4(0.10f, 0.12f, 0.16f, 1.0f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.22f, 0.48f, 0.70f, 1.0f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.12f, 0.32f, 0.52f, 1.0f));
        bool clicked = ImGui.Button($"{label}##editorToolbar_{label}", new Vector2(width, 0));
        ImGui.PopStyleColor(3);

        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.TextUnformatted(label);
            ImGui.TextDisabled($"Shortcut: {shortcut}");
            ImGui.EndTooltip();
        }
        return clicked;
    }

    private void DrawEditorToolbar(EditorSceneService sceneService, EditorAssetService assetService, CommandHistory history)
    {
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 3.0f);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(8.0f, 5.0f));
        ImGui.BeginChild("EditorToolbar", new Vector2(0, 38), ImGuiChildFlags.Borders);

        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(new Vector4(0.55f, 0.75f, 0.95f, 1.0f), "TOOLS");
        ImGui.SameLine();
        ImGui.TextDisabled("MODE");
        ImGui.SameLine();
        if (DrawToolbarButton("Select", "Esc", _currentMode == EditorMode.Select))
        {
            EndTerrainSculpt(sceneService, assetService, history);
            _currentMode = EditorMode.Select;
        }
        ImGui.SameLine(0, 4);
        if (DrawToolbarButton("Brush", "B", _currentMode == EditorMode.DrawBrush))
        {
            EndTerrainSculpt(sceneService, assetService, history);
            EndEditorGizmoInteraction(sceneService, assetService, history, finalizeEditableBrush: true);
            _currentMode = EditorMode.DrawBrush;
            ClearBrushComponentSelection();
        }
        ImGui.SameLine(0, 4);
        if (DrawToolbarButton("Terrain", "G", _currentMode == EditorMode.TerrainSculpt))
        {
            EndFaceExtrudeDrag(sceneService, assetService, history);
            EndEditorGizmoInteraction(sceneService, assetService, history, finalizeEditableBrush: true);
            _currentMode = EditorMode.TerrainSculpt;
            ClearBrushComponentSelection();
        }

        ImGui.SameLine(0, 12);
        ImGui.TextDisabled("COMPONENT");
        ImGui.SameLine();
        if (DrawToolbarButton("Object", "1", _brushComponentMode == BrushComponentMode.Object))
            SetBrushComponentMode(BrushComponentMode.Object, sceneService, assetService, history);
        ImGui.SameLine(0, 3);
        if (DrawToolbarButton("Vertex", "2", _brushComponentMode == BrushComponentMode.Vertex))
            SetBrushComponentMode(BrushComponentMode.Vertex, sceneService, assetService, history);
        ImGui.SameLine(0, 3);
        if (DrawToolbarButton("Edge", "3", _brushComponentMode == BrushComponentMode.Edge))
            SetBrushComponentMode(BrushComponentMode.Edge, sceneService, assetService, history);
        ImGui.SameLine(0, 3);
        if (DrawToolbarButton("Face", "4", _brushComponentMode == BrushComponentMode.Face))
            SetBrushComponentMode(BrushComponentMode.Face, sceneService, assetService, history);

        ImGui.SameLine(0, 12);
        ImGui.TextDisabled("TRANSFORM");
        ImGui.SameLine();
        if (DrawToolbarButton("Move", "W", _gizmoOperation == GizmoOperation.Translate))
            SetGizmoOperation(GizmoOperation.Translate, sceneService, assetService, history);
        ImGui.SameLine(0, 4);
        if (DrawToolbarButton("Rotate", "E", _gizmoOperation == GizmoOperation.Rotate))
            SetGizmoOperation(GizmoOperation.Rotate, sceneService, assetService, history);
        ImGui.SameLine(0, 4);
        if (DrawToolbarButton("Scale", "R", _gizmoOperation == GizmoOperation.Scale))
            SetGizmoOperation(GizmoOperation.Scale, sceneService, assetService, history);
        ImGui.SameLine(0, 4);
        if (DrawToolbarButton("Shear", "T", _gizmoOperation == GizmoOperation.Shear))
            SetGizmoOperation(GizmoOperation.Shear, sceneService, assetService, history);

        if (IsEditingBrushComponents)
        {
            ImGui.SameLine(0, 12);
            ImGui.TextDisabled("MODEL");
            ImGui.SameLine();
            if (DrawToolbarButton("Extrude", "", false))
                ExecuteBrushEditOperation(sceneService, assetService, history, "Extrude");
            ImGui.SameLine(0, 3);
            if (DrawToolbarButton("Inset", "", false))
                ExecuteBrushEditOperation(sceneService, assetService, history, "Inset");
            ImGui.SameLine(0, 3);
            if (DrawToolbarButton("Bevel", "", false))
                ExecuteBrushEditOperation(sceneService, assetService, history, "Bevel");
            ImGui.SameLine(0, 3);
            if (DrawToolbarButton("Loop Cut", "", false))
                ExecuteBrushEditOperation(sceneService, assetService, history, "LoopCut");
            ImGui.SameLine(0, 3);
            if (_brushComponentMode == BrushComponentMode.Face &&
                DrawToolbarButton("Delete Face", "Delete", false))
            {
                ExecuteBrushEditOperation(sceneService, assetService, history, "DeleteFaces");
            }
            if (_brushComponentMode == BrushComponentMode.Edge &&
                DrawToolbarButton("Bridge Edges", "Alt+B", false))
            {
                ExecuteBrushEditOperation(sceneService, assetService, history, "BridgeEdges");
            }
            if (_brushComponentMode is BrushComponentMode.Face or BrushComponentMode.Edge)
                ImGui.SameLine(0, 3);
            if (DrawToolbarButton(_brushEditTool == BrushEditTool.Knife ? "Knife: Click" : "Knife", "", _brushEditTool == BrushEditTool.Knife))
            {
                _brushEditTool = _brushEditTool == BrushEditTool.Knife ? BrushEditTool.None : BrushEditTool.Knife;
                _knifeFirstPoint = null;
                _knifeFaceId = -1;
            }
        }

        string toolbarStatus = _selectedObjects.Count == 0
            ? (sceneService.IsDirty ? "Modified" : "Saved")
            : $"{_selectedObjects.Count} selected  |  {(sceneService.IsDirty ? "Modified" : "Saved")}";
        float rightEdge = ImGui.GetWindowSize().X - ImGui.GetStyle().WindowPadding.X - ImGui.CalcTextSize(toolbarStatus).X - 16.0f;
        if (ImGui.GetCursorPosX() < rightEdge)
        {
            ImGui.SameLine(rightEdge);
            ImGui.TextDisabled(toolbarStatus);
        }

        ImGui.EndChild();
        ImGui.PopStyleVar(2);
    }

    private bool IsEditingBrushComponents =>
        _brushComponentMode != BrushComponentMode.Object &&
        _currentMode == EditorMode.Select &&
        _selectedObject is Brush brush && brush.IsEditableMesh;

    private Brush? ActiveEditableBrush =>
        _selectedObject is Brush brush && brush.IsEditableMesh ? brush : null;

    private bool BeginFaceExtrudeDrag(
        EditorViewport viewport,
        Vector2 vpPos,
        Vector2 vpSize,
        Vector3 worldRayOrigin,
        Vector3 worldRayDirection,
        int? requestedFaceId = null)
    {
        if (_isFaceExtrudeDragging ||
            _brushComponentMode != BrushComponentMode.Face ||
            _brushEditTool != BrushEditTool.None ||
            ActiveEditableBrush is not Brush brush ||
            brush.EditableMesh == null ||
            brush.Body == null)
        {
            return false;
        }

        Matrix4x4 transform = Matrix4x4.CreateFromQuaternion(brush.Body.Rotation) *
                              Matrix4x4.CreateTranslation(brush.Body.Position);
        if (!Matrix4x4.Invert(transform, out Matrix4x4 inverse))
            return false;

        Vector3 localRayOrigin = Vector3.Transform(worldRayOrigin, inverse);
        Vector3 localRayDirection = Vector3.Normalize(Vector3.TransformNormal(worldRayDirection, inverse));
        int faceId;
        if (requestedFaceId is int selectedFaceId && brush.EditableMesh.FindFace(selectedFaceId) != null)
        {
            // When the drag begins on the transform gizmo, the cursor is not a
            // reliable face picker: another face can be visually in front of
            // the gizmo. Preserve the explicit face selection instead.
            faceId = selectedFaceId;
        }
        else if (!brush.EditableMesh.TryRaycastFace(localRayOrigin, localRayDirection, out faceId, out _, out _))
        {
            return false;
        }

        EditableBrushFace? face = brush.EditableMesh.FindFace(faceId);
        if (face == null)
            return false;

        Vector3 localNormal = brush.EditableMesh.CalculateFaceNormal(face);
        if (localNormal.LengthSquared() < 0.000001f)
            return false;

        Vector3 localCenter = brush.EditableMesh.CalculateFaceCenter(face);
        Vector3 worldCenter = brush.Body.Position + Vector3.Transform(localCenter, brush.Body.Rotation);
        Vector3 worldNormal = Vector3.Normalize(Vector3.Transform(localNormal, brush.Body.Rotation));
        if (!TryWorldToScreen(worldCenter, viewport, vpPos, vpSize, out Vector2 centerOnScreen))
            return false;

        Vector2 screenDirection;
        float worldUnitsPerPixel;
        if (TryWorldToScreen(worldCenter + worldNormal, viewport, vpPos, vpSize, out Vector2 normalOnScreen))
        {
            Vector2 projectedNormal = normalOnScreen - centerOnScreen;
            float projectedLength = projectedNormal.Length();
            if (projectedLength >= 2.0f)
            {
                screenDirection = projectedNormal / projectedLength;
                worldUnitsPerPixel = 1.0f / projectedLength;
            }
            else
            {
                // A face pointing straight at the camera has no visible normal
                // direction. Vertical dragging keeps that common case usable.
                screenDirection = -Vector2.UnitY;
                worldUnitsPerPixel = GetFaceExtrudeFallbackScale(viewport, worldCenter, vpSize);
            }
        }
        else
        {
            screenDirection = -Vector2.UnitY;
            worldUnitsPerPixel = GetFaceExtrudeFallbackScale(viewport, worldCenter, vpSize);
        }

        _selectedBrushFaces.Clear();
        _selectedBrushFaces.Add(faceId);
        _lastBrushComponentSelectionTime = ImGui.GetTime();
        _isFaceExtrudeDragging = true;
        _faceExtrudeTopologyCreated = false;
        _faceExtrudeViewport = viewport;
        _faceExtrudeBrush = brush;
        _faceExtrudeFaceId = faceId;
        _faceExtrudeStartMouse = ImGui.GetIO().MousePos;
        _faceExtrudeScreenDirection = screenDirection;
        _faceExtrudeWorldUnitsPerPixel = worldUnitsPerPixel;
        _faceExtrudeLocalNormal = localNormal;
        _faceExtrudeInitialDistance = 0.0f;
        _faceExtrudeCurrentDistance = 0.0f;
        _faceExtrudeInitialPositions = null;
        _faceExtrudePreState = null;
        return true;
    }

    private void HandleFaceExtrudeDrag(
        EditorViewport viewport,
        EditorSceneService sceneService,
        EditorAssetService assetService)
    {
        if (!_isFaceExtrudeDragging || _faceExtrudeViewport != viewport ||
            _faceExtrudeBrush?.EditableMesh == null || _faceExtrudeBrush.Body == null)
        {
            return;
        }

        Vector2 mouseDelta = ImGui.GetIO().MousePos - _faceExtrudeStartMouse;
        float screenDistance = Vector2.Dot(mouseDelta, _faceExtrudeScreenDirection);
        if (MathF.Abs(screenDistance) < 3.0f)
            return;

        float desiredDistance = screenDistance * _faceExtrudeWorldUnitsPerPixel;
        if (_snapEnabled && _snapGrid > 0.0f)
            desiredDistance = MathF.Round(desiredDistance / _snapGrid) * _snapGrid;
        if (MathF.Abs(desiredDistance) < 0.0001f)
            return;

        EditableBrushMesh topology = _faceExtrudeBrush.EditableMesh;
        if (!_faceExtrudeTopologyCreated)
        {
            _faceExtrudePreState = sceneService.Document.Serialize();
            if (!topology.TryExtrude([_faceExtrudeFaceId], desiredDistance, out string error))
            {
                ShowDocumentError(error);
                ResetFaceExtrudeState();
                return;
            }

            EditableBrushFace? extrudedFace = topology.FindFace(_faceExtrudeFaceId);
            if (extrudedFace == null)
            {
                ShowDocumentError("Não foi possível localizar a face extrudada.");
                ResetFaceExtrudeState();
                return;
            }

            _faceExtrudeInitialPositions = extrudedFace.Vertices.ToDictionary(
                vertexId => vertexId,
                topology.GetPosition);
            _faceExtrudeInitialDistance = desiredDistance;
            _faceExtrudeCurrentDistance = desiredDistance;
            _faceExtrudeTopologyCreated = true;
            RefreshEditableBrush(_faceExtrudeBrush, sceneService, assetService, normalizeOrigin: false);
            return;
        }

        // Do not pass through a collapsed extrusion during a single drag. To
        // extrude to the opposite side, release and start a new Shift drag.
        if (MathF.Sign(desiredDistance) != MathF.Sign(_faceExtrudeInitialDistance))
            desiredDistance = MathF.Sign(_faceExtrudeInitialDistance) * 0.0001f;
        if (MathF.Abs(desiredDistance - _faceExtrudeCurrentDistance) < 0.000001f ||
            _faceExtrudeInitialPositions == null)
        {
            return;
        }

        float localDelta = desiredDistance - _faceExtrudeInitialDistance;
        foreach ((int vertexId, Vector3 initialPosition) in _faceExtrudeInitialPositions)
        {
            EditableBrushVertex? vertex = topology.FindVertex(vertexId);
            if (vertex != null)
                vertex.Position = initialPosition + _faceExtrudeLocalNormal * localDelta;
        }
        _faceExtrudeCurrentDistance = desiredDistance;
        RefreshEditableBrush(_faceExtrudeBrush, sceneService, assetService, normalizeOrigin: false);
    }

    private void EndFaceExtrudeDrag(
        EditorSceneService sceneService,
        EditorAssetService assetService,
        CommandHistory history)
    {
        Brush? brush = _faceExtrudeBrush;
        string? preState = _faceExtrudePreState;
        bool commit = _faceExtrudeTopologyCreated && brush?.EditableMesh != null && !string.IsNullOrEmpty(preState);
        ResetFaceExtrudeState();

        if (!commit || brush == null || preState == null)
            return;

        RefreshEditableBrush(brush, sceneService, assetService, normalizeOrigin: true);
        CommitBrushTopologySnapshot(preState, sceneService, assetService, history);
    }

    private void ResetFaceExtrudeState()
    {
        _isFaceExtrudeDragging = false;
        _faceExtrudeTopologyCreated = false;
        _faceExtrudeViewport = null;
        _faceExtrudeBrush = null;
        _faceExtrudeFaceId = -1;
        _faceExtrudeStartMouse = Vector2.Zero;
        _faceExtrudeScreenDirection = Vector2.Zero;
        _faceExtrudeWorldUnitsPerPixel = 0.0f;
        _faceExtrudeLocalNormal = Vector3.Zero;
        _faceExtrudeInitialDistance = 0.0f;
        _faceExtrudeCurrentDistance = 0.0f;
        _faceExtrudeInitialPositions = null;
        _faceExtrudePreState = null;
    }

    private static float GetFaceExtrudeFallbackScale(EditorViewport viewport, Vector3 worldCenter, Vector2 viewportSize)
    {
        float height = MathF.Max(viewportSize.Y, 1.0f);
        if (viewport.Camera.IsOrthographic)
            return viewport.Camera.OrthoSize / height;

        float distance = MathF.Max(Vector3.Distance(viewport.Camera.Position, worldCenter), 0.1f);
        return 2.0f * distance * MathF.Tan(float.DegreesToRadians(22.5f)) / height;
    }

    private void EndEditorGizmoInteraction(
        EditorSceneService sceneService,
        EditorAssetService assetService,
        CommandHistory history,
        bool finalizeEditableBrush)
    {
        bool hadActiveGizmo = EditorGizmo.IsUsing() || _wasUsingGizmo;
        if (hadActiveGizmo && finalizeEditableBrush && _wasUsingGizmo && IsEditingBrushComponents && ActiveEditableBrush is Brush brush)
        {
            // Keep the pivot fixed throughout a component drag, then recenter
            // once on release. This preserves world placement without making
            // the gizmo jump while the mouse is still pressed.
            RefreshEditableBrush(brush, sceneService, assetService, normalizeOrigin: true);
        }

        EditorGizmo.Reset();
        _activeDraggingViewport = null;
        if (_wasUsingGizmo)
            Undo.ForceEnd(history, sceneService, assetService);
        _wasUsingGizmo = false;
    }

    private void SetGizmoOperation(
        GizmoOperation operation,
        EditorSceneService sceneService,
        EditorAssetService assetService,
        CommandHistory history)
    {
        if (_gizmoOperation == operation)
            return;
        EndFaceExtrudeDrag(sceneService, assetService, history);
        EndEditorGizmoInteraction(sceneService, assetService, history, finalizeEditableBrush: true);
        _gizmoOperation = operation;
    }

    private void SetBrushComponentMode(
        BrushComponentMode mode,
        EditorSceneService sceneService,
        EditorAssetService assetService,
        CommandHistory history)
    {
        EndFaceExtrudeDrag(sceneService, assetService, history);
        EndEditorGizmoInteraction(sceneService, assetService, history, finalizeEditableBrush: true);
        _brushComponentMode = mode;
        _brushEditTool = BrushEditTool.None;
        _knifeFirstPoint = null;
        _knifeFaceId = -1;
        ClearBrushComponentSelection();

        if (mode == BrushComponentMode.Object || _selectedObject is not Brush brush || brush.IsEditableMesh)
            return;

        string pre = sceneService.Document.Serialize();
        MapShapeType? previousShape = brush.Body?.Shape;
        Vector3 previousPosition = brush.Body?.Position ?? Vector3.Zero;
        Vector3? previousHalfExtents = brush.Body?.HalfExtents;
        EditableBrushMesh topology = brush.EnsureEditableMesh();
        if (!topology.TryValidate(out string error))
        {
            // The conversion is kept only when it produced valid topology. A
            // malformed CSG brush can still be used by the legacy tools.
            brush.EditableMesh = null;
            brush.GeometryMode = BrushGeometryMode.PlaneCsg;
            if (brush.Body != null && previousShape.HasValue)
            {
                brush.Body.Shape = previousShape.Value;
                brush.Body.Position = previousPosition;
                brush.Body.HalfExtents = previousHalfExtents;
            }
            ShowDocumentError($"Não foi possível converter o brush para edição de componentes: {error}");
            return;
        }

        RefreshEditableBrush(brush, sceneService, assetService, normalizeOrigin: false);
        CommitBrushTopologySnapshot(pre, sceneService, assetService, history);
    }

    private void ClearBrushComponentSelection()
    {
        _selectedBrushVertices.Clear();
        _selectedBrushEdges.Clear();
        _selectedBrushFaces.Clear();
    }

    private IEnumerable<int> GetSelectedComponentVertices(EditableBrushMesh topology)
    {
        return _brushComponentMode switch
        {
            BrushComponentMode.Vertex => _selectedBrushVertices,
            BrushComponentMode.Edge => _selectedBrushEdges.SelectMany(edge => new[] { edge.A, edge.B }),
            BrushComponentMode.Face => topology.Faces
                .Where(face => _selectedBrushFaces.Contains(face.Id))
                .SelectMany(face => face.Vertices),
            _ => []
        };
    }

    private void ExecuteBrushEditOperation(
        EditorSceneService sceneService,
        EditorAssetService assetService,
        CommandHistory history,
        string operation)
    {
        Brush? brush = ActiveEditableBrush;
        if (brush?.EditableMesh == null)
        {
            ShowDocumentError("Selecione um brush e entre no modo Vertex, Edge ou Face antes de editar a geometria.");
            return;
        }

        string pre = sceneService.Document.Serialize();
        EditableBrushMesh topology = brush.EditableMesh;
        bool changed;
        string error = string.Empty;
        switch (operation)
        {
            case "Extrude":
                changed = topology.TryExtrude(_selectedBrushFaces, _brushExtrudeDistance, out error);
                break;
            case "Inset":
                changed = topology.TryInset(_selectedBrushFaces, _brushInsetAmount, out error);
                break;
            case "Bevel":
                changed = topology.TryBevel(_selectedBrushEdges, _brushBevelWidth, out error);
                break;
            case "LoopCut":
                EditableBrushEdge firstEdge = _selectedBrushEdges.FirstOrDefault();
                changed = _selectedBrushEdges.Count > 0 && topology.TryLoopCut(firstEdge, _brushLoopCutFactor, out error);
                if (_selectedBrushEdges.Count == 0)
                    error = "Selecione uma aresta para criar o Loop Cut.";
                break;
            case "DeleteFaces":
                changed = topology.TryDeleteFaces(_selectedBrushFaces, out error);
                break;
            case "BridgeEdges":
                changed = topology.TryBridgeEdges(_selectedBrushEdges, out error);
                break;
            default:
                return;
        }

        if (!changed)
        {
            ShowDocumentError(error);
            return;
        }

        RefreshEditableBrush(brush, sceneService, assetService);
        if (operation is "Bevel" or "LoopCut" or "BridgeEdges")
            _selectedBrushEdges.Clear();
        if (operation == "DeleteFaces")
            _selectedBrushFaces.Clear();
        CommitBrushTopologySnapshot(pre, sceneService, assetService, history);
    }

    private void CommitBrushTopologySnapshot(
        string pre,
        EditorSceneService sceneService,
        EditorAssetService assetService,
        CommandHistory history)
    {
        string post = sceneService.Document.Serialize();
        if (string.Equals(pre, post, StringComparison.Ordinal))
            return;
        sceneService.MarkModified(post);
        history.PushCommand(new SnapshotCommand(sceneService, assetService, pre, post));
    }

    private void RefreshEditableBrush(
        Brush brush,
        EditorSceneService sceneService,
        EditorAssetService assetService,
        bool normalizeOrigin = true)
    {
        if (normalizeOrigin)
            brush.MarkGeometryChanged();
        else
            brush.UpdateEditableBounds();
        assetService.InvalidateMesh(brush.Id);
        var entity = sceneService.Scene.Entities.FirstOrDefault(candidate => candidate.Id == brush.Id);
        if (entity == null)
            return;
        entity.Mesh = assetService.GetOrCreateMesh(brush);
        entity.Transform.Scale = Vector3.One;
        if (brush.Body != null)
        {
            entity.Transform.Position = brush.Body.Position;
            entity.Transform.Rotation = brush.Body.Rotation;
        }
    }

    private void DrawBrushEditWindow(EditorSceneService sceneService, EditorAssetService assetService, CommandHistory history)
    {
        if (!IsEditingBrushComponents || ActiveEditableBrush?.EditableMesh == null)
            return;

        ImGui.SetNextWindowSize(new Vector2(310, 215), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("Brush Edit"))
        {
            ImGui.End();
            return;
        }

        EditableBrushMesh topology = ActiveEditableBrush.EditableMesh;
        ImGui.TextDisabled($"{topology.Vertices.Count} vertices  |  {topology.GetEdges().Count} edges  |  {topology.Faces.Count} faces");
        if (_brushComponentMode == BrushComponentMode.Face)
            ImGui.TextDisabled("Shift + drag a face: direct extrude");
        ImGui.Separator();
        ImGui.TextUnformatted("Face operations");
        ImGui.SetNextItemWidth(115);
        ImGui.DragFloat("Extrude distance", ref _brushExtrudeDistance, 0.01f, -10.0f, 10.0f, "%.3f");
        ImGui.SameLine();
        if (ImGui.Button("Extrude")) ExecuteBrushEditOperation(sceneService, assetService, history, "Extrude");
        if (_brushComponentMode == BrushComponentMode.Face)
        {
            ImGui.SameLine();
            if (ImGui.Button("Delete Face"))
                ExecuteBrushEditOperation(sceneService, assetService, history, "DeleteFaces");
        }
        ImGui.SetNextItemWidth(115);
        ImGui.DragFloat("Inset amount", ref _brushInsetAmount, 0.01f, 0.001f, 0.95f, "%.3f");
        ImGui.SameLine();
        if (ImGui.Button("Inset")) ExecuteBrushEditOperation(sceneService, assetService, history, "Inset");

        ImGui.Separator();
        ImGui.TextUnformatted("Edge operations");
        ImGui.SetNextItemWidth(115);
        ImGui.DragFloat("Bevel width", ref _brushBevelWidth, 0.01f, 0.001f, 10.0f, "%.3f");
        ImGui.SameLine();
        if (ImGui.Button("Bevel")) ExecuteBrushEditOperation(sceneService, assetService, history, "Bevel");
        ImGui.SetNextItemWidth(115);
        ImGui.SliderFloat("Loop Cut", ref _brushLoopCutFactor, 0.02f, 0.98f, "%.2f");
        ImGui.SameLine();
        if (ImGui.Button("Cut")) ExecuteBrushEditOperation(sceneService, assetService, history, "LoopCut");
        if (_brushComponentMode == BrushComponentMode.Edge)
        {
            ImGui.SameLine();
            if (ImGui.Button("Bridge Edges"))
                ExecuteBrushEditOperation(sceneService, assetService, history, "BridgeEdges");
        }

        ImGui.Separator();
        ImGui.TextDisabled(_brushEditTool == BrushEditTool.Knife
            ? "Knife: clique duas vezes na mesma face. Os pontos encaixam na borda."
            : "Knife divide uma face entre dois pontos da borda.");
        ImGui.End();
    }

    private void DrawViewportWindow(
        EditorWindow window,
        EditorViewport viewport3D, 
        EditorViewport viewportTop, 
        EditorViewport viewportFront, 
        EditorViewport viewportSide, 
        EditorSceneService sceneService, 
        EditorAssetService assetService, 
        CommandHistory history,
        EditorInputService inputService)
    {
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);

        if (ImGui.Begin("Scene Viewports", ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
            DrawEditorToolbar(sceneService, assetService, history);

            if (inputService.IsMapContext &&
                !ImGui.IsMouseDown(ImGuiMouseButton.Right) &&
                !ImGui.GetIO().WantTextInput)
            {
                if (ImGui.IsKeyPressed(ImGuiKey.Escape))
                {
                    EndFaceExtrudeDrag(sceneService, assetService, history);
                    EndEditorGizmoInteraction(sceneService, assetService, history, finalizeEditableBrush: true);
                    if (_currentMode is EditorMode.DrawBrush or EditorMode.TerrainSculpt)
                    {
                        EndTerrainSculpt(sceneService, assetService, history);
                        _currentMode = EditorMode.Select;
                        _activeHandle = HandleType.None;
                        _previewManager.Reset();
                    }
                    else
                    {
                        _selectedObject = null;
                        _selectedObjects.Clear();
                        ClearBrushComponentSelection();
                    }
                }
                if (ImGui.IsKeyPressed(ImGuiKey.B) && !ImGui.GetIO().KeyAlt)
                {
                    EndTerrainSculpt(sceneService, assetService, history);
                    EndFaceExtrudeDrag(sceneService, assetService, history);
                    EndEditorGizmoInteraction(sceneService, assetService, history, finalizeEditableBrush: true);
                    _currentMode = EditorMode.DrawBrush;
                    ClearBrushComponentSelection();
                }
                if (ImGui.IsKeyPressed(ImGuiKey.G))
                {
                    EndFaceExtrudeDrag(sceneService, assetService, history);
                    EndEditorGizmoInteraction(sceneService, assetService, history, finalizeEditableBrush: true);
                    _currentMode = EditorMode.TerrainSculpt;
                    ClearBrushComponentSelection();
                }
                if (ImGui.IsKeyPressed(ImGuiKey._1)) SetBrushComponentMode(BrushComponentMode.Object, sceneService, assetService, history);
                if (ImGui.IsKeyPressed(ImGuiKey._2)) SetBrushComponentMode(BrushComponentMode.Vertex, sceneService, assetService, history);
                if (ImGui.IsKeyPressed(ImGuiKey._3)) SetBrushComponentMode(BrushComponentMode.Edge, sceneService, assetService, history);
                if (ImGui.IsKeyPressed(ImGuiKey._4)) SetBrushComponentMode(BrushComponentMode.Face, sceneService, assetService, history);
                if (ImGui.IsKeyPressed(ImGuiKey.W)) SetGizmoOperation(GizmoOperation.Translate, sceneService, assetService, history);
                if (ImGui.IsKeyPressed(ImGuiKey.E)) SetGizmoOperation(GizmoOperation.Rotate, sceneService, assetService, history);
                if (ImGui.IsKeyPressed(ImGuiKey.R)) SetGizmoOperation(GizmoOperation.Scale, sceneService, assetService, history);
                if (ImGui.IsKeyPressed(ImGuiKey.T)) SetGizmoOperation(GizmoOperation.Shear, sceneService, assetService, history);
                
                if (_currentMode == EditorMode.DrawBrush && _previewManager.HasPreview && ImGui.IsKeyPressed(ImGuiKey.Enter))
                {
                    CommitBrush(sceneService, assetService, history);
                }
            }

            var availSize = ImGui.GetContentRegionAvail();
            if (_viewportLayout == ViewportLayout.PerspectiveOnly || availSize.X < 700 || availSize.Y < 450)
            {
                DrawSubViewport(window, viewport3D, "Camera 3D", availSize, sceneService, assetService, history, inputService);
            }
            else
            {
                var size = new Vector2(availSize.X / 2f - 4, availSize.Y / 2f - 4);

                // Row 1: Top & Front
                DrawSubViewport(window, viewportTop, "Top (X/Z)", size, sceneService, assetService, history, inputService);
                ImGui.SameLine();
                DrawSubViewport(window, viewportFront, "Front (X/Y)", size, sceneService, assetService, history, inputService);

                // Row 2: Side & 3D Perspective
                DrawSubViewport(window, viewportSide, "Side (Z/Y)", size, sceneService, assetService, history, inputService);
                ImGui.SameLine();
                DrawSubViewport(window, viewport3D, "Camera 3D", size, sceneService, assetService, history, inputService);
            }
        }
        ImGui.End();
        ImGui.PopStyleVar(1);
    }

    private void DrawSubViewport(
        EditorWindow window,
        EditorViewport viewport, 
        string title, 
        Vector2 size, 
        EditorSceneService sceneService, 
        EditorAssetService assetService, 
        CommandHistory history,
        EditorInputService inputService)
    {
        ImGui.BeginChild(title, size, ImGuiChildFlags.Borders, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        
        ImGui.Text(title);
        DrawTerrainSculptViewportToolbar(title);

        var vpPos = ImGui.GetCursorScreenPos();
        var vpSize = ImGui.GetContentRegionAvail();
        viewport.SetUiVisible(vpSize.X >= 8 && vpSize.Y >= 8);

        int targetWidth = Math.Max(8, ((int)vpSize.X + 3) & ~3);
        int targetHeight = Math.Max(8, ((int)vpSize.Y + 3) & ~3);

        if (targetWidth != viewport.Width || targetHeight != viewport.Height)
        {
            viewport.CreateFbo(targetWidth, targetHeight);
        }

        ImGui.Image((IntPtr)viewport.ColorTexture, vpSize, new Vector2(0, 1), new Vector2(1, 0));

        // Editor-only overlays stay outside the OpenGL scene texture, so they
        // cannot affect picking or the scene depth buffer.
        DrawViewportOverlays(viewport, vpPos, vpSize, sceneService, assetService);

        HandleAssetDropOnViewport(viewport, vpPos, vpSize, sceneService, assetService, history);
        bool isHovered = ImGui.IsItemHovered() && inputService.IsMapContext;
        bool terrainNeighborClickHandled = isHovered && TryHandleTerrainNeighborClick(
            viewport,
            vpPos,
            vpSize,
            sceneService,
            assetService);

        // 2D Handle Detection & State Setup
        bool showHandles = false;
        Vector3 boxMin = Vector3.Zero;
        Vector3 boxMax = Vector3.Zero;
        bool isPreview = false;
        Span<Vector2> handlePositions = stackalloc Vector2[10];

        // Component mode owns the left mouse button. The brush-level Hammer
        // resize handles must stay out of this path or they can capture a
        // vertex/edge click and leave the viewport in a drag state.
        if (viewport.Camera.IsOrthographic && !IsEditingBrushComponents)
        {
            if (_currentMode == EditorMode.DrawBrush && _previewManager.HasPreview)
            {
                showHandles = true;
                boxMin = Vector3.Min(_previewManager.Min, _previewManager.Max);
                boxMax = Vector3.Max(_previewManager.Min, _previewManager.Max);
                isPreview = true;
            }
            else if (_currentMode == EditorMode.Select && _selectedObjects.Count > 0)
            {
                if (GetSelectionAABB(sceneService, assetService, out Vector3 tMin, out Vector3 tMax))
                {
                    showHandles = true;
                    boxMin = tMin;
                    boxMax = tMax;
                    isPreview = false;
                }
            }
        }

        if (showHandles)
        {
            Span<Vector3> corners = stackalloc Vector3[8]
            {
                new Vector3(boxMin.X, boxMin.Y, boxMin.Z),
                new Vector3(boxMax.X, boxMin.Y, boxMin.Z),
                new Vector3(boxMin.X, boxMax.Y, boxMin.Z),
                new Vector3(boxMax.X, boxMax.Y, boxMin.Z),
                new Vector3(boxMin.X, boxMin.Y, boxMax.Z),
                new Vector3(boxMax.X, boxMin.Y, boxMax.Z),
                new Vector3(boxMin.X, boxMax.Y, boxMax.Z),
                new Vector3(boxMax.X, boxMax.Y, boxMax.Z)
            };

            float sMinX = float.MaxValue, sMinY = float.MaxValue;
            float sMaxX = float.MinValue, sMaxY = float.MinValue;
            foreach (var c in corners)
            {
                Vector2 screenPos = WorldToScreen(c, viewport, vpPos, vpSize);
                if (screenPos.X < sMinX) sMinX = screenPos.X;
                if (screenPos.Y < sMinY) sMinY = screenPos.Y;
                if (screenPos.X > sMaxX) sMaxX = screenPos.X;
                if (screenPos.Y > sMaxY) sMaxY = screenPos.Y;
            }

            handlePositions[(int)HandleType.Left] = new Vector2(sMinX, (sMinY + sMaxY) * 0.5f);
            handlePositions[(int)HandleType.Right] = new Vector2(sMaxX, (sMinY + sMaxY) * 0.5f);
            handlePositions[(int)HandleType.Top] = new Vector2((sMinX + sMaxX) * 0.5f, sMinY);
            handlePositions[(int)HandleType.Bottom] = new Vector2((sMinX + sMaxX) * 0.5f, sMaxY);
            handlePositions[(int)HandleType.TopLeft] = new Vector2(sMinX, sMinY);
            handlePositions[(int)HandleType.TopRight] = new Vector2(sMaxX, sMinY);
            handlePositions[(int)HandleType.BottomLeft] = new Vector2(sMinX, sMaxY);
            handlePositions[(int)HandleType.BottomRight] = new Vector2(sMaxX, sMaxY);
            handlePositions[(int)HandleType.Center] = new Vector2((sMinX + sMaxX) * 0.5f, (sMinY + sMaxY) * 0.5f);

            // Handle hover and click interaction BEFORE picking or drawing code
            var mousePos = ImGui.GetMousePos();
            if (!_isDraggingHandle)
            {
                bool selectionDelayActive = (ImGui.GetTime() - _lastSelectionTime) < 0.5;
                if (isHovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left) && (isPreview || !selectionDelayActive))
                {
                    // Check edge/corner handles (1-8)
                    for (int h = 1; h <= 8; h++)
                    {
                        if (Vector2.Distance(mousePos, handlePositions[h]) < 8f)
                        {
                            _isDraggingHandle = true;
                            _activeHandle = (HandleType)h;
                            _draggingHandleViewport = viewport;
                            _previewManager.IsDraggingHandle = isPreview;
                            Undo.RecordState(sceneService.Document.Serialize());
                            
                            EditorGizmo.GetMouseRay(mousePos, viewport.Camera.ViewMatrix, viewport.Camera.ProjectionMatrix(vpSize.X / vpSize.Y), vpPos, vpSize, out Vector3 ro, out Vector3 rd);
                            _shearLastHit = ComputeHitPoint(viewport.Camera.ViewType, ro, rd);
                            _shearLastHit = ApplySnap(_shearLastHit, _snapGrid);
                            
                            break;
                        }
                    }

                    // Check center move handle (only for brush selections, not preview)
                    if (!_isDraggingHandle && !isPreview)
                    {
                        bool allBrushes = _selectedObjects.Count > 0 && _selectedObjects.All(o => o is Brush);
                        if (allBrushes && Vector2.Distance(mousePos, handlePositions[(int)HandleType.Center]) < 10f)
                        {
                            _isDraggingHandle = true;
                            _activeHandle = HandleType.Center;
                            _draggingHandleViewport = viewport;
                            _previewManager.IsDraggingHandle = false;
                            Undo.RecordState(sceneService.Document.Serialize());

                            // Record initial world-space hit for delta computation
                            EditorGizmo.GetMouseRay(mousePos, viewport.Camera.ViewMatrix, viewport.Camera.ProjectionMatrix(vpSize.X / vpSize.Y), vpPos, vpSize, out Vector3 ro, out Vector3 rd);
                            _centerDragLastHit = ComputeHitPoint(viewport.Camera.ViewType, ro, rd);
                            _centerDragLastHit = ApplySnap(_centerDragLastHit, _snapGrid);
                        }
                    }
                }
            }
        }

        bool isDraggingActiveInThisViewport = _isDraggingHandle && _draggingHandleViewport == viewport;
        //bool normalInteractionAllowed = isHovered && !EditorGizmo.IsUsing() && !EditorGizmo.IsHovered && !_isDraggingHandle;

        bool gizmoActive = EditorGizmo.IsUsing();
        bool allowViewportInput = isHovered && !terrainNeighborClickHandled && !gizmoActive && !_isDraggingHandle;
        bool allowPicking = allowViewportInput && !EditorGizmo.IsHovered;

        // Hammer-style: suppress translation and scale gizmos when the entire selection is made of brushes
        bool allSelectedAreBrushes = _selectedObjects.Count > 0 && _selectedObjects.All(o => o is Brush);
        bool editingBrushComponents = IsEditingBrushComponents;
        bool suppressGizmoForBrushes = editingBrushComponents || (allSelectedAreBrushes && _gizmoOperation != GizmoOperation.Rotate);

        // This intentionally runs before the component gizmo. A selected face
        // usually has its translation gizmo on top of it; without this priority
        // the gizmo consumes Shift + drag and only translates the old face.
        bool faceExtrudeStartedThisFrame = false;
        if (editingBrushComponents &&
            !_isDraggingHandle &&
            !gizmoActive &&
            isHovered &&
            _brushComponentMode == BrushComponentMode.Face &&
            _brushEditTool == BrushEditTool.None &&
            ImGui.GetIO().KeyShift &&
            ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            EditorGizmo.GetMouseRay(
                ImGui.GetIO().MousePos,
                viewport.Camera.ViewMatrix,
                viewport.Camera.ProjectionMatrix(vpSize.X / vpSize.Y),
                vpPos,
                vpSize,
                out Vector3 rayOrigin,
                out Vector3 rayDir);
            int? selectedFaceId = _selectedBrushFaces.Count == 1
                ? _selectedBrushFaces.First()
                : null;
            faceExtrudeStartedThisFrame = BeginFaceExtrudeDrag(
                viewport,
                vpPos,
                vpSize,
                rayOrigin,
                rayDir,
                selectedFaceId);
        }

        if (editingBrushComponents && !_isDraggingHandle && !_isFaceExtrudeDragging && !faceExtrudeStartedThisFrame)
        {
            HandleBrushComponentGizmo(viewport, vpPos, vpSize, isHovered, sceneService, assetService, history);
        }
        if (_isFaceExtrudeDragging)
            HandleFaceExtrudeDrag(viewport, sceneService, assetService);

        if (_selectedObject != null && _selectedObject.Body != null && sceneService.Document.Objects.Contains(_selectedObject) &&
            !_isDraggingHandle && !suppressGizmoForBrushes && _currentMode != EditorMode.TerrainSculpt)
        {
            var body = _selectedObject.Body;
            var view = viewport.Camera.ViewMatrix;
            var proj = viewport.Camera.ProjectionMatrix(vpSize.X / vpSize.Y);

            float snapVal = _snapEnabled ? _snapGrid : 0.0f;
            float angleSnap = _snapEnabled ? _snapAngle : 0.0f;
            bool changed = false;

            bool canManipulate = (isHovered && _activeDraggingViewport == null) || (_activeDraggingViewport == viewport);

            if (canManipulate)
            {
                bool selectionDelayActive = (ImGui.GetTime() - _lastSelectionTime) < 0.5;

                var objectsToTransform = GetObjectsToTransform(sceneService.Document);

                if (_gizmoOperation == GizmoOperation.Translate)
                {
                    if (EditorGizmo.ManipulateTranslation(body.Position, view, proj, vpPos, vpSize, out Vector3 newPos, snapVal, !selectionDelayActive))
                    {
                        Vector3 delta = newPos - body.Position;
                        if (delta.LengthSquared() > 0.00001f)
                        {
                            foreach (var obj in objectsToTransform)
                            {
                                if (obj.Body != null)
                                {
                                    obj.Body.Position += delta;
                                }
                            }
                            changed = true;
                        }
                    }
                }
                else if (_gizmoOperation == GizmoOperation.Rotate)
                {
                    if (EditorGizmo.ManipulateRotation(body.Position, body.Rotation, view, proj, vpPos, vpSize, out Quaternion newRot, angleSnap, !selectionDelayActive))
                    {
                        Quaternion normalizedNewRot = Quaternion.Normalize(newRot);
                        Quaternion deltaRot = normalizedNewRot * Quaternion.Inverse(body.Rotation);
                        Vector3 pivot = body.Position;

                        foreach (var obj in objectsToTransform)
                        {
                            if (obj.Body != null)
                            {
                                if (obj != _selectedObject)
                                {
                                    Vector3 relativePos = obj.Body.Position - pivot;
                                    Vector3 rotatedPos = Vector3.Transform(relativePos, deltaRot);
                                    obj.Body.Position = pivot + rotatedPos;
                                }
                                obj.Body.Rotation = Quaternion.Normalize(deltaRot * obj.Body.Rotation);
                            }
                        }
                        changed = true;
                    }
                }
                else if (_gizmoOperation == GizmoOperation.Scale)
                {
                    Vector3 currentScale = Vector3.One;
                    if ((body.Shape == MapShapeType.Box || body.Shape == MapShapeType.Trimesh) && body.HalfExtents.HasValue) currentScale = body.HalfExtents.Value * 2.0f;
                    else if (body.Shape == MapShapeType.Sphere && body.Radius.HasValue) currentScale = new Vector3(body.Radius.Value * 2.0f);
                    else if (body.Shape == MapShapeType.Capsule && body.Radius.HasValue && body.Height.HasValue)
                        currentScale = new Vector3(body.Radius.Value * 2.0f, body.Height.Value, body.Radius.Value * 2.0f);
                    else currentScale = _selectedObject.ModelScale;

                    if (EditorGizmo.ManipulateScale(body.Position, currentScale, view, proj, vpPos, vpSize, out Vector3 newScale, snapVal, !selectionDelayActive))
                    {
                        Vector3 scaleMult = new Vector3(
                            currentScale.X > 0.0001f ? newScale.X / currentScale.X : 1f,
                            currentScale.Y > 0.0001f ? newScale.Y / currentScale.Y : 1f,
                            currentScale.Z > 0.0001f ? newScale.Z / currentScale.Z : 1f
                        );

                        Vector3 pivot = body.Position;

                        foreach (var obj in objectsToTransform)
                        {
                            if (obj.Body != null)
                            {
                                if (obj != _selectedObject)
                                {
                                    Vector3 relativePos = obj.Body.Position - pivot;
                                    obj.Body.Position = pivot + relativePos * scaleMult;
                                }

                                if ((obj.Body.Shape == MapShapeType.Box || obj.Body.Shape == MapShapeType.Trimesh) && obj.Body.HalfExtents.HasValue)
                                {
                                    Vector3 oldExtents = obj.Body.HalfExtents.Value;
                                    obj.Body.HalfExtents = Vector3.Max(new Vector3(0.05f), obj.Body.HalfExtents.Value * scaleMult);
                                    if (obj is Brush b) b.ScalePlanes(obj.Body.HalfExtents.Value / oldExtents);
                                }
                                else if (obj.Body.Shape == MapShapeType.Sphere && obj.Body.Radius.HasValue)
                                {
                                    float avgMult = (scaleMult.X + scaleMult.Y + scaleMult.Z) / 3.0f;
                                    obj.Body.Radius = MathF.Max(0.05f, obj.Body.Radius.Value * avgMult);
                                }
                                else if (obj.Body.Shape == MapShapeType.Capsule &&
                                         obj.Body.Radius.HasValue && obj.Body.Height.HasValue)
                                {
                                    obj.Body.Radius = MathF.Max(0.05f, obj.Body.Radius.Value *
                                        (scaleMult.X + scaleMult.Z) * 0.5f);
                                    obj.Body.Height = MathF.Max(0.05f, obj.Body.Height.Value * scaleMult.Y);
                                }
                                else
                                {
                                    float maxMult = MathF.Max(scaleMult.X, MathF.Max(scaleMult.Y, scaleMult.Z));
                                    obj.ModelScale = Vector3.Max(new Vector3(0.01f), obj.ModelScale * maxMult);
                                }
                            }
                        }
                        changed = true;
                    }
                }

                if (changed)
                {
                    foreach (var obj in objectsToTransform)
                    {
                        if (obj.Body == null) continue;

                        if (obj is Brush brush)
                        {
                            assetService.InvalidateMesh(brush.Id);
                        }

                        var entity = sceneService.Scene.Entities.FirstOrDefault(e => e.Id == obj.Id);
                        if (entity != null)
                        {
                            entity.Transform.Position = obj.Body.Position;
                            entity.Transform.Rotation = obj.Body.Rotation;
                            
                            if (obj is Brush brushObj)
                            {
                                entity.Transform.Scale = Vector3.One;
                                entity.Mesh = assetService.GetOrCreateMesh(brushObj);
                            }
                            else if ((obj.Body.Shape == MapShapeType.Box || obj.Body.Shape == MapShapeType.Trimesh) && obj.Body.HalfExtents.HasValue)
                                entity.Transform.Scale = obj.Body.HalfExtents.Value * 2.0f;
                            else if (obj.Body.Shape == MapShapeType.Sphere && obj.Body.Radius.HasValue)
                                entity.Transform.Scale = new Vector3(obj.Body.Radius.Value * 2.0f);
                            else if (obj.Body.Shape == MapShapeType.Capsule &&
                                     obj.Body.Radius.HasValue && obj.Body.Height.HasValue)
                                entity.Transform.Scale = MeshGenerator.GetCapsuleRenderScale(
                                    obj.Body.Radius.Value,
                                    obj.Body.Height.Value);
                            else
                                entity.Transform.Scale = obj.ModelScale;
                        }

                        SyncLight(sceneService, obj);
                    }

                    if (objectsToTransform.Any(obj => obj.IsTerrain))
                        sceneService.PopulateScene(assetService);
                }

                bool isUsingNow = EditorGizmo.IsUsing();
                
                if (isUsingNow && _activeDraggingViewport == null)
                {
                    _activeDraggingViewport = viewport;
                }

                if (isUsingNow && !_wasUsingGizmo) 
                {
                    Undo.RecordState(_frameBeginState);
                }

                if (!isUsingNow && _wasUsingGizmo)
                {
                    Undo.ForceEnd(history, sceneService, assetService);
                }
                _wasUsingGizmo = isUsingNow;
            }
        }

        if (allowViewportInput)
        {
            if (_currentMode == EditorMode.TerrainSculpt)
            {
                viewport.HandleInput(ImGui.GetIO(), ImGui.GetIO().DeltaTime, window.Glfw, window.Handle, vpPos, vpSize);

                if (_selectedObject?.IsTerrain == true &&
                    ImGui.IsMouseDown(ImGuiMouseButton.Left))
                {
                    BeginTerrainSculpt(sceneService, assetService);
                    if (_terrainSculptActive)
                    {
                        EditorGizmo.GetMouseRay(
                            ImGui.GetIO().MousePos,
                            viewport.Camera.ViewMatrix,
                            viewport.Camera.ProjectionMatrix(vpSize.X / vpSize.Y),
                            vpPos,
                            vpSize,
                            out Vector3 rayOrigin,
                            out Vector3 rayDirection);

                        float terrainBrushStrength = _terrainSculptTool == TerrainSculptTool.RaiseLower
                            ? _terrainBrushStrength
                            : _terrainBrushStrength * MathF.Max(0.001f, ImGui.GetIO().DeltaTime);
                        bool terrainChanged = sceneService.ApplyTerrainToolAtRay(
                            _selectedObject,
                            assetService,
                            rayOrigin,
                            rayDirection,
                            _terrainSculptTool,
                            _terrainBrushRadius,
                            terrainBrushStrength,
                            _terrainSculptLower || ImGui.GetIO().KeyShift,
                            _terrainSetHeight,
                            _terrainNoiseScale,
                            _terrainNoiseSeed,
                            _terrainHeightmapBrush);
                        if (terrainChanged)
                            viewport.RequestRender();
                    }
                }
            }
            else if (_currentMode == EditorMode.Select)
            {
                viewport.HandleInput(ImGui.GetIO(), ImGui.GetIO().DeltaTime, window.Glfw, window.Handle, vpPos, vpSize);
            }
            else if (_currentMode == EditorMode.DrawBrush)
            {
                // Always call HandleInput so camera panning/orbiting cursor state is properly restored
                viewport.HandleInput(ImGui.GetIO(), ImGui.GetIO().DeltaTime, window.Glfw, window.Handle, vpPos, vpSize);

                if (viewport.Camera.IsOrthographic)
                {
                    EditorGizmo.GetMouseRay(ImGui.GetIO().MousePos, viewport.Camera.ViewMatrix, viewport.Camera.ProjectionMatrix(vpSize.X / vpSize.Y), vpPos, vpSize, out Vector3 rayOrigin, out Vector3 rayDir);
                    
                    Vector3 hitPoint = Vector3.Zero;
                    float t = 0;
                    if (viewport.Camera.ViewType == CameraViewType.Top && MathF.Abs(rayDir.Y) > 0.001f) { t = -rayOrigin.Y / rayDir.Y; hitPoint = rayOrigin + rayDir * t; }
                    else if (viewport.Camera.ViewType == CameraViewType.Front && MathF.Abs(rayDir.Z) > 0.001f) { t = -rayOrigin.Z / rayDir.Z; hitPoint = rayOrigin + rayDir * t; }
                    else if (viewport.Camera.ViewType == CameraViewType.Side && MathF.Abs(rayDir.X) > 0.001f) { t = -rayOrigin.X / rayDir.X; hitPoint = rayOrigin + rayDir * t; }

                    hitPoint = ApplySnap(hitPoint, _snapGrid);

                    bool isMouseClicked = ImGui.IsMouseClicked(ImGuiMouseButton.Left);

                    // The top view only gives the brush tool an X/Z position.
                    // When a new brush starts over an existing brush, use the
                    // highest vertical surface under the cursor as its base
                    // instead of placing the new brush around world Y=0.
                    if (isMouseClicked && !_previewManager.HasPreview &&
                        viewport.Camera.ViewType == CameraViewType.Top)
                    {
                        hitPoint.Y = FindTopBrushSupportHeight(
                            hitPoint,
                            sceneService.Document);
                    }
                    
                    if (isMouseClicked && !_previewManager.HasPreview)
                    {
                        _selectedObjects.Clear();
                        _selectedObject = null;
                    }

                    _previewManager.HandleDrawingInput(viewport, hitPoint, isMouseClicked);
                }
            }
        }

        bool componentClickHandled = faceExtrudeStartedThisFrame;
        if (!componentClickHandled && allowPicking && _currentMode == EditorMode.Select && IsEditingBrushComponents && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            EditorGizmo.GetMouseRay(ImGui.GetIO().MousePos, viewport.Camera.ViewMatrix, viewport.Camera.ProjectionMatrix(vpSize.X / vpSize.Y), vpPos, vpSize, out Vector3 rayOrigin, out Vector3 rayDir);
            componentClickHandled = _brushEditTool == BrushEditTool.Knife
                ? HandleKnifeClick(rayOrigin, rayDir, sceneService, assetService, history)
                : TryPickBrushComponent(ImGui.GetIO().MousePos, rayOrigin, rayDir, viewport, vpPos, vpSize, ImGui.GetIO().KeyCtrl);
        }

        if (allowPicking && _currentMode == EditorMode.Select && !componentClickHandled)
        {
            if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            {
                EditorGizmo.GetMouseRay(ImGui.GetIO().MousePos, viewport.Camera.ViewMatrix, viewport.Camera.ProjectionMatrix(vpSize.X / vpSize.Y), vpPos, vpSize, out Vector3 rayOrigin, out Vector3 rayDir);
                
                // Billboard icons are drawn as editor overlays, so give their
                // screen-space hit area priority over geometry behind them.
                var hitObjects = PickLightGizmos(ImGui.GetIO().MousePos, viewport, vpPos, vpSize, sceneService);
                foreach (MapObject objectHit in PickObjects(rayOrigin, rayDir, sceneService, assetService))
                {
                    if (!hitObjects.Contains(objectHit))
                        hitObjects.Add(objectHit);
                }
                if (hitObjects.Count > 0)
                {
                    MapObject hitObj;
                    
                    if (ImGui.GetIO().KeyCtrl)
                    {
                        hitObj = hitObjects[0];
                        if (_selectedObjects.Contains(hitObj))
                        {
                            _selectedObjects.Remove(hitObj);
                            if (_selectedObject == hitObj)
                            {
                                _selectedObject = _selectedObjects.FirstOrDefault();
                            }
                        }
                        else
                        {
                            _selectedObjects.Add(hitObj);
                            _selectedObject = hitObj;
                        }
                    }
                    else
                    {
                        if (_selectedObject != null)
                        {
                            int currentIndex = hitObjects.IndexOf(_selectedObject);
                            if (currentIndex >= 0 && _selectedObjects.Count == 1)
                            {
                                int nextIndex = (currentIndex + 1) % hitObjects.Count;
                                hitObj = hitObjects[nextIndex];
                            }
                            else
                            {
                                hitObj = hitObjects[0];
                            }
                        }
                        else
                        {
                            hitObj = hitObjects[0];
                        }

                        _selectedObjects.Clear();
                        _selectedObjects.Add(hitObj);
                        _selectedObject = hitObj;
                        if (_brushComponentMode != BrushComponentMode.Object && _selectedObject is Brush selectedBrush && !selectedBrush.IsEditableMesh)
                            SetBrushComponentMode(_brushComponentMode, sceneService, assetService, history);
                    }
                }
                else
                {
                    if (!ImGui.GetIO().KeyCtrl)
                    {
                        _selectedObjects.Clear();
                        _selectedObject = null;
                    }
                }
            }
        }

        if (ImGui.IsMouseReleased(ImGuiMouseButton.Left))
        {
            _previewManager.EndDrawing();
        }

        // Handle Dragging Update (Mouse position follow-up)
        if (isDraggingActiveInThisViewport)
        {
            EditorGizmo.GetMouseRay(ImGui.GetIO().MousePos, viewport.Camera.ViewMatrix, viewport.Camera.ProjectionMatrix(vpSize.X / vpSize.Y), vpPos, vpSize, out Vector3 rayOrigin, out Vector3 rayDir);
            Vector3 hitPoint = Vector3.Zero;
            float t = 0;
            if (viewport.Camera.ViewType == CameraViewType.Top && MathF.Abs(rayDir.Y) > 0.001f) { t = -rayOrigin.Y / rayDir.Y; hitPoint = rayOrigin + rayDir * t; }
            else if (viewport.Camera.ViewType == CameraViewType.Front && MathF.Abs(rayDir.Z) > 0.001f) { t = -rayOrigin.Z / rayDir.Z; hitPoint = rayOrigin + rayDir * t; }
            else if (viewport.Camera.ViewType == CameraViewType.Side && MathF.Abs(rayDir.X) > 0.001f) { t = -rayOrigin.X / rayDir.X; hitPoint = rayOrigin + rayDir * t; }

            hitPoint = ApplySnap(hitPoint, _snapGrid);

            // Center handle = move all selected brushes by drag delta
            if (_activeHandle == HandleType.Center && !_previewManager.IsDraggingHandle)
            {
                Vector3 delta = hitPoint - _centerDragLastHit;
                if (delta.LengthSquared() > 0.000001f)
                {
                    foreach (var obj in _selectedObjects)
                    {
                        if (obj is Brush brush && brush.Body != null)
                        {
                            brush.Body.Position += delta;

                            var entity = sceneService.Scene.Entities.FirstOrDefault(e => e.Id == brush.Id);
                            if (entity != null)
                                entity.Transform.Position = brush.Body.Position;
                        }
                    }
                    _centerDragLastHit = hitPoint;
                }
            }
            else if (_gizmoOperation == GizmoOperation.Shear && _activeHandle != HandleType.Center && _activeHandle != HandleType.None && !_previewManager.IsDraggingHandle && _selectedObjects.All(o => o is Brush))
            {
                int hAxis = 0, vAxis = 0;
                bool hInverted = false, vInverted = false;
                if (viewport.Camera.ViewType == CameraViewType.Top) { hAxis = 0; vAxis = 2; }
                else if (viewport.Camera.ViewType == CameraViewType.Front) { hAxis = 0; vAxis = 1; vInverted = true; }
                else if (viewport.Camera.ViewType == CameraViewType.Side) { hAxis = 2; vAxis = 1; hInverted = true; vInverted = true; }

                bool dragLeft = _activeHandle == HandleType.Left || _activeHandle == HandleType.TopLeft || _activeHandle == HandleType.BottomLeft;
                bool dragRight = _activeHandle == HandleType.Right || _activeHandle == HandleType.TopRight || _activeHandle == HandleType.BottomRight;
                bool dragTop = _activeHandle == HandleType.Top || _activeHandle == HandleType.TopLeft || _activeHandle == HandleType.TopRight;
                bool dragBottom = _activeHandle == HandleType.Bottom || _activeHandle == HandleType.BottomLeft || _activeHandle == HandleType.BottomRight;

                bool shearH = dragTop || dragBottom;
                bool shearV = dragLeft || dragRight;

                if (shearH && !dragLeft && !dragRight) 
                {
                    float deltaH = GetComp(hitPoint, hAxis) - GetComp(_shearLastHit, hAxis);
                    float fixedV = (dragTop && !vInverted) || (dragBottom && vInverted) ? GetComp(boxMax, vAxis) : GetComp(boxMin, vAxis);
                    float movingV = (dragTop && !vInverted) || (dragBottom && vInverted) ? GetComp(boxMin, vAxis) : GetComp(boxMax, vAxis);
                    
                    float height = movingV - fixedV;
                    if (MathF.Abs(height) > 0.001f && MathF.Abs(deltaH) > 0.00001f)
                    {
                        float k = deltaH / height;
                        var shearMat = Matrix4x4.Identity;
                        if (vAxis == 0 && hAxis == 1) shearMat.M12 = k;
                        else if (vAxis == 0 && hAxis == 2) shearMat.M13 = k;
                        else if (vAxis == 1 && hAxis == 0) shearMat.M21 = k;
                        else if (vAxis == 1 && hAxis == 2) shearMat.M23 = k;
                        else if (vAxis == 2 && hAxis == 0) shearMat.M31 = k;
                        else if (vAxis == 2 && hAxis == 1) shearMat.M32 = k;

                        foreach (Brush brush in _selectedObjects)
                        {
                            if (brush.Body == null)
                                continue;
                            MapBody brushBody = brush.Body;
                            brush.ApplyTransformMatrix(shearMat);
                            float localFixedV = fixedV - GetComp(brushBody.Position, vAxis);
                            float shiftH = -k * localFixedV;
                            var pos = brushBody.Position;
                            SetComponent(ref pos, hAxis, GetComp(pos, hAxis) + shiftH);
                            brushBody.Position = pos;
                            assetService.InvalidateMesh(brush.Id);
                            var entity = sceneService.Scene.Entities.FirstOrDefault(e => e.Id == brush.Id);
                            if (entity != null)
                            {
                                entity.Transform.Position = brushBody.Position;
                                entity.Transform.Scale = Vector3.One;
                                entity.Mesh = assetService.GetOrCreateMesh(brush);
                            }
                            
                            var meshData = Fuse.Scene.Model.MeshGenerator.Generate(brush);
                            if (meshData.Vertices.Length > 0)
                            {
                                System.Numerics.Vector3 min = new System.Numerics.Vector3(float.MaxValue);
                                System.Numerics.Vector3 max = new System.Numerics.Vector3(float.MinValue);
                                foreach (var v in meshData.Vertices)
                                {
                                    min = System.Numerics.Vector3.Min(min, v.Position);
                                    max = System.Numerics.Vector3.Max(max, v.Position);
                                }
                                brushBody.HalfExtents = (max - min) / 2f;
                                brushBody.Shape = Fuse.Scene.Model.MapShapeType.Trimesh;
                            }
                        }
                    }
                }
                else if (shearV && !dragTop && !dragBottom)
                {
                    float deltaV = GetComp(hitPoint, vAxis) - GetComp(_shearLastHit, vAxis);
                    float fixedH = (dragLeft && !hInverted) || (dragRight && hInverted) ? GetComp(boxMax, hAxis) : GetComp(boxMin, hAxis);
                    float movingH = (dragLeft && !hInverted) || (dragRight && hInverted) ? GetComp(boxMin, hAxis) : GetComp(boxMax, hAxis);

                    float width = movingH - fixedH;
                    if (MathF.Abs(width) > 0.001f && MathF.Abs(deltaV) > 0.00001f)
                    {
                        float k = deltaV / width;
                        var shearMat = Matrix4x4.Identity;
                        if (hAxis == 0 && vAxis == 1) shearMat.M12 = k;
                        else if (hAxis == 0 && vAxis == 2) shearMat.M13 = k;
                        else if (hAxis == 1 && vAxis == 0) shearMat.M21 = k;
                        else if (hAxis == 1 && vAxis == 2) shearMat.M23 = k;
                        else if (hAxis == 2 && vAxis == 0) shearMat.M31 = k;
                        else if (hAxis == 2 && vAxis == 1) shearMat.M32 = k;

                        foreach (Brush brush in _selectedObjects)
                        {
                            if (brush.Body == null)
                                continue;
                            MapBody brushBody = brush.Body;
                            brush.ApplyTransformMatrix(shearMat);
                            float localFixedH = fixedH - GetComp(brushBody.Position, hAxis);
                            float shiftV = -k * localFixedH;
                            var pos = brushBody.Position;
                            SetComponent(ref pos, vAxis, GetComp(pos, vAxis) + shiftV);
                            brushBody.Position = pos;
                            assetService.InvalidateMesh(brush.Id);
                            var entity = sceneService.Scene.Entities.FirstOrDefault(e => e.Id == brush.Id);
                            if (entity != null)
                            {
                                entity.Transform.Position = brushBody.Position;
                                entity.Mesh = assetService.GetOrCreateMesh(brush);
                            }
                                
                            var meshData = Fuse.Scene.Model.MeshGenerator.Generate(brush);
                            if (meshData.Vertices.Length > 0)
                            {
                                System.Numerics.Vector3 min = new System.Numerics.Vector3(float.MaxValue);
                                System.Numerics.Vector3 max = new System.Numerics.Vector3(float.MinValue);
                                foreach (var v in meshData.Vertices)
                                {
                                    min = System.Numerics.Vector3.Min(min, v.Position);
                                    max = System.Numerics.Vector3.Max(max, v.Position);
                                }
                                brushBody.HalfExtents = (max - min) / 2f;
                                brushBody.Shape = Fuse.Scene.Model.MapShapeType.Trimesh;
                            }
                        }
                    }
                }
                
                _shearLastHit = hitPoint;
            }
            else
            {

            Vector3 currentMin = boxMin;
            Vector3 currentMax = boxMax;

            UpdateBoundsFromDrag(viewport.Camera.ViewType, _activeHandle, hitPoint, ref currentMin, ref currentMax);

            if (_previewManager.IsDraggingHandle)
            {
                _previewManager.UpdateBoundsFromDrag(currentMin, currentMax);
            }
            else if (_selectedObjects.Count > 0)
            {
                Vector3 newSize = currentMax - currentMin;
                if (newSize.X > 0.1f && newSize.Y > 0.1f && newSize.Z > 0.1f)
                {
                    Vector3 frameOldSize = boxMax - boxMin;
                    if (frameOldSize.X < 0.001f) frameOldSize.X = 1f;
                    if (frameOldSize.Y < 0.001f) frameOldSize.Y = 1f;
                    if (frameOldSize.Z < 0.001f) frameOldSize.Z = 1f;

                    Vector3 frameScale = newSize / frameOldSize;

                    foreach (var obj in _selectedObjects)
                    {
                        if (obj is Brush brush && brush.Body != null && brush.Body.HalfExtents.HasValue)
                        {
                            Vector3 relativePos = brush.Body.Position - boxMin;
                            brush.Body.Position = currentMin + (relativePos * frameScale);
                            brush.Body.HalfExtents = brush.Body.HalfExtents.Value * frameScale;

                            brush.ScalePlanes(frameScale);
                            assetService.InvalidateMesh(brush.Id);

                            var entity = sceneService.Scene.Entities.FirstOrDefault(e => e.Id == brush.Id);
                            if (entity != null)
                            {
                                entity.Transform.Position = brush.Body.Position;
                                entity.Transform.Scale = Vector3.One;
                                entity.Mesh = assetService.GetOrCreateMesh(brush);
                            }
                        }
                    }
                }
            }
            } // end else (resize handles)

            if (ImGui.IsMouseReleased(ImGuiMouseButton.Left))
            {
                _isDraggingHandle = false;
                _activeHandle = HandleType.None;
                _draggingHandleViewport = null;
                
                if (!_previewManager.IsDraggingHandle)
                {
                    Undo.ForceEnd(history, sceneService, assetService);
                }
                _previewManager.IsDraggingHandle = false;
            }
        }

        // Draw selection outlines for all other selected objects
        if (viewport.Camera.IsOrthographic && _currentMode == EditorMode.Select && _selectedObjects.Count > 1)
        {
            var drawList = ImGui.GetWindowDrawList();
            uint otherSelColor = ImGui.GetColorU32(new Vector4(0.8f, 0.8f, 0.8f, 0.7f));

            foreach (var selObj in _selectedObjects)
            {
                if (selObj == _selectedObject) continue;
                if (selObj.Body == null || !selObj.Visible) continue;
                MapBody selectedBody = selObj.Body;

                Vector3 sMin = Vector3.Zero;
                Vector3 sMax = Vector3.Zero;
                bool drawAABB = true;
                
                if (selObj is Fuse.Scene.Model.Brush selBrush)
                {
                    drawAABB = false; // We draw the actual wireframe for brushes
                    var meshData = Fuse.Scene.Model.MeshGenerator.Generate(selBrush);
                    if (meshData.LineIndices != null)
                    {
                        for (int i = 0; i < meshData.LineIndices.Length; i += 2)
                        {
                            var v1 = meshData.Vertices[meshData.LineIndices[i]].Position + selectedBody.Position;
                            var v2 = meshData.Vertices[meshData.LineIndices[i + 1]].Position + selectedBody.Position;
                            var p1 = WorldToScreen(v1, viewport, vpPos, vpSize);
                            var p2 = WorldToScreen(v2, viewport, vpPos, vpSize);
                            if (p1.X > 0 && p2.X > 0)
                            {
                                drawList.AddLine(p1, p2, otherSelColor, 2.0f);
                            }
                        }
                    }
                }
                else if ((selectedBody.Shape == MapShapeType.Box || selectedBody.Shape == MapShapeType.Trimesh) && selectedBody.HalfExtents.HasValue)
                {
                    sMin = selectedBody.Position - selectedBody.HalfExtents.Value;
                    sMax = selectedBody.Position + selectedBody.HalfExtents.Value;
                }
                else if (selectedBody.Shape == MapShapeType.Sphere && selectedBody.Radius.HasValue)
                {
                    float r = selectedBody.Radius.Value;
                    sMin = selectedBody.Position - new Vector3(r);
                    sMax = selectedBody.Position + new Vector3(r);
                }
                else
                {
                    float r = 1.0f;
                    if (selectedBody.Shape == MapShapeType.Capsule && selectedBody.Height.HasValue) r = selectedBody.Height.Value;
                    if (selObj.IsModel) r = selObj.ModelScale.Length() * 1.5f;
                    sMin = selectedBody.Position - new Vector3(r);
                    sMax = selectedBody.Position + new Vector3(r);
                }

                if (drawAABB)
                {
                    Vector3[] sCorners = new Vector3[8]
                    {
                        new Vector3(sMin.X, sMin.Y, sMin.Z),
                        new Vector3(sMax.X, sMin.Y, sMin.Z),
                        new Vector3(sMin.X, sMax.Y, sMin.Z),
                        new Vector3(sMax.X, sMax.Y, sMin.Z),
                        new Vector3(sMin.X, sMin.Y, sMax.Z),
                        new Vector3(sMax.X, sMin.Y, sMax.Z),
                        new Vector3(sMin.X, sMax.Y, sMax.Z),
                        new Vector3(sMax.X, sMax.Y, sMax.Z)
                    };

                    float selMinX = float.MaxValue, selMinY = float.MaxValue;
                    float selMaxX = float.MinValue, selMaxY = float.MinValue;
                    foreach (var c in sCorners)
                    {
                        Vector2 screenPos = WorldToScreen(c, viewport, vpPos, vpSize);
                        if (screenPos.X < selMinX) selMinX = screenPos.X;
                        if (screenPos.Y < selMinY) selMinY = screenPos.Y;
                        if (screenPos.X > selMaxX) selMaxX = screenPos.X;
                        if (screenPos.Y > selMaxY) selMaxY = screenPos.Y;
                    }

                    drawList.AddRect(new Vector2(selMinX, selMinY), new Vector2(selMaxX, selMaxY), otherSelColor, 0f, ImDrawFlags.None, 1.0f);
                }
            }
        }

        // Draw Handles & Bounding Box Outline
        if (showHandles)
        {
            Vector3 finalMin = _previewManager.IsDraggingHandle ? _previewManager.Min : boxMin;
            Vector3 finalMax = _previewManager.IsDraggingHandle ? _previewManager.Max : boxMax;

            Vector3 orderedMin = Vector3.Min(finalMin, finalMax);
            Vector3 orderedMax = Vector3.Max(finalMin, finalMax);

            Vector3[] finalCorners = new Vector3[8]
            {
                new Vector3(orderedMin.X, orderedMin.Y, orderedMin.Z),
                new Vector3(orderedMax.X, orderedMin.Y, orderedMin.Z),
                new Vector3(orderedMin.X, orderedMax.Y, orderedMin.Z),
                new Vector3(orderedMax.X, orderedMax.Y, orderedMin.Z),
                new Vector3(orderedMin.X, orderedMin.Y, orderedMax.Z),
                new Vector3(orderedMax.X, orderedMin.Y, orderedMax.Z),
                new Vector3(orderedMin.X, orderedMax.Y, orderedMax.Z),
                new Vector3(orderedMax.X, orderedMax.Y, orderedMax.Z)
            };

            float sMinX = float.MaxValue, sMinY = float.MaxValue;
            float sMaxX = float.MinValue, sMaxY = float.MinValue;
            foreach (var c in finalCorners)
            {
                Vector2 screenPos = WorldToScreen(c, viewport, vpPos, vpSize);
                if (screenPos.X < sMinX) sMinX = screenPos.X;
                if (screenPos.Y < sMinY) sMinY = screenPos.Y;
                if (screenPos.X > sMaxX) sMaxX = screenPos.X;
                if (screenPos.Y > sMaxY) sMaxY = screenPos.Y;
            }

            var drawList = ImGui.GetWindowDrawList();
            uint boxColor = isPreview ? ImGui.GetColorU32(new Vector4(0.0f, 1.0f, 1.0f, 1.0f)) : ImGui.GetColorU32(new Vector4(1.0f, 1.0f, 1.0f, 1.0f));
            drawList.AddRect(new Vector2(sMinX, sMinY), new Vector2(sMaxX, sMaxY), boxColor, 0f, ImDrawFlags.None, 1.5f);

            Vector2[] finalHandlePositions = new Vector2[10];
            finalHandlePositions[(int)HandleType.Left] = new Vector2(sMinX, (sMinY + sMaxY) * 0.5f);
            finalHandlePositions[(int)HandleType.Right] = new Vector2(sMaxX, (sMinY + sMaxY) * 0.5f);
            finalHandlePositions[(int)HandleType.Top] = new Vector2((sMinX + sMaxX) * 0.5f, sMinY);
            finalHandlePositions[(int)HandleType.Bottom] = new Vector2((sMinX + sMaxX) * 0.5f, sMaxY);
            finalHandlePositions[(int)HandleType.TopLeft] = new Vector2(sMinX, sMinY);
            finalHandlePositions[(int)HandleType.TopRight] = new Vector2(sMaxX, sMinY);
            finalHandlePositions[(int)HandleType.BottomLeft] = new Vector2(sMinX, sMaxY);
            finalHandlePositions[(int)HandleType.BottomRight] = new Vector2(sMaxX, sMaxY);
            finalHandlePositions[(int)HandleType.Center] = new Vector2((sMinX + sMaxX) * 0.5f, (sMinY + sMaxY) * 0.5f);

            for (int h = 1; h <= 8; h++)
            {
                Vector2 p = finalHandlePositions[h];
                drawList.AddRectFilled(p - new Vector2(4), p + new Vector2(4), ImGui.GetColorU32(new Vector4(1.0f, 1.0f, 1.0f, 1.0f)));
                drawList.AddRect(p - new Vector2(4), p + new Vector2(4), ImGui.GetColorU32(new Vector4(0.0f, 0.0f, 0.0f, 1.0f)));
            }

            // Draw center move handle (orange crosshair) only for brush-only selections
            if (!isPreview && _selectedObjects.Count > 0 && _selectedObjects.All(o => o is Brush))
            {
                Vector2 cp = finalHandlePositions[(int)HandleType.Center];
                uint orange = ImGui.GetColorU32(new Vector4(1.0f, 0.55f, 0.0f, 1.0f));
                uint orangeDark = ImGui.GetColorU32(new Vector4(0.5f, 0.25f, 0.0f, 1.0f));
                drawList.AddRectFilled(cp - new Vector2(6), cp + new Vector2(6), orange);
                drawList.AddRect(cp - new Vector2(6), cp + new Vector2(6), orangeDark);
                // Crosshair lines
                drawList.AddLine(cp - new Vector2(10, 0), cp + new Vector2(10, 0), orange, 1.5f);
                drawList.AddLine(cp - new Vector2(0, 10), cp + new Vector2(0, 10), orange, 1.5f);
            }
        }

        ImGui.EndChild();
    }

    private List<MapObject> PickLightGizmos(
        Vector2 mousePosition,
        EditorViewport viewport,
        Vector2 vpPos,
        Vector2 vpSize,
        EditorSceneService sceneService)
    {
        var hits = new List<(MapObject obj, float distance)>();
        foreach (var light in sceneService.Scene.Lights)
        {
            if (!light.Enabled)
                continue;

            MapObject? mapObject = sceneService.Document.Objects.FirstOrDefault(obj =>
                obj.IsLight && obj.Id.Equals(light.Id, StringComparison.OrdinalIgnoreCase));
            if (mapObject == null || !mapObject.Visible)
                continue;

            if (!TryWorldToScreen(light.Position, viewport, vpPos, vpSize, out Vector2 screenPosition))
                continue;

            float distance = Vector2.Distance(mousePosition, screenPosition);
            if (distance <= 18.0f)
                hits.Add((mapObject, distance));
        }

        return hits
            .OrderBy(hit => hit.distance)
            .Select(hit => hit.obj)
            .ToList();
    }

    private List<MapObject> PickObjects(Vector3 rayOrigin, Vector3 rayDir, EditorSceneService sceneService, EditorAssetService assetService)
    {
        var hits = new List<(MapObject obj, float dist)>();
        
        foreach (var obj in sceneService.Document.Objects)
        {
            if (obj.Body == null || !obj.Visible) continue;

            float dist = float.MaxValue;
            bool hit = false;

            Matrix4x4 modelInv;
            Matrix4x4.Invert(Matrix4x4.CreateFromQuaternion(obj.Body.Rotation) * Matrix4x4.CreateTranslation(obj.Body.Position), out modelInv);
            Vector3 localOrigin = Vector3.Transform(rayOrigin, modelInv);
            Vector3 localDir = Vector3.Normalize(Vector3.TransformNormal(rayDir, modelInv));
            
            if (obj.IsTerrain && !string.IsNullOrWhiteSpace(obj.TerrainAssetPath))
            {
                TerrainTileSetAsset? terrain = sceneService.TryLoadTerrainTileSet(obj, assetService);
                hit = terrain != null &&
                    terrain.Raycast(localOrigin, localDir, out dist, out _, out _);
            }
            else if (obj.Body.Shape == MapShapeType.Sphere && obj.Body.Radius.HasValue)
            {
                hit = RaySphereIntersect(localOrigin, localDir, Vector3.Zero, obj.Body.Radius.Value, out dist);
            }
            else if ((obj.Body.Shape == MapShapeType.Box || obj.Body.Shape == MapShapeType.Trimesh) && obj.Body.HalfExtents.HasValue)
            {
                hit = RayAABBIntersect(localOrigin, localDir, -obj.Body.HalfExtents.Value, obj.Body.HalfExtents.Value, out dist);
            }
            else if (obj.Body.Shape == MapShapeType.Trimesh && obj.IsModel && obj.Model != null)
            {
                string modelPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(assetService.FuseResPath, obj.Model));
                var model = assetService.AssetManager.GetModel(modelPath);
                if (model != null && model.CollVertices.Length > 0)
                {
                    Vector3 min = new(float.MaxValue);
                    Vector3 max = new(float.MinValue);
                    foreach (var v in model.CollVertices)
                    {
                        Vector3 scaledV = v * obj.ModelScale;
                        min = Vector3.Min(min, scaledV);
                        max = Vector3.Max(max, scaledV);
                    }
                    hit = RayAABBIntersect(localOrigin, localDir, min, max, out dist);
                }
                else
                {
                    hit = RaySphereIntersect(localOrigin, localDir, Vector3.Zero, 1.0f, out dist);
                }
            }
            else 
            {
                float r = 1.0f;
                if (obj.Body.Shape == MapShapeType.Capsule && obj.Body.Height.HasValue) r = obj.Body.Height.Value;
                hit = RaySphereIntersect(localOrigin, localDir, Vector3.Zero, r, out dist);
            }
            
            if (hit)
            {
                hits.Add((obj, dist));
            }
        }
        return hits.OrderBy(h => h.dist).Select(h => h.obj).ToList();
    }

    private bool TryPickBrushComponent(
        Vector2 mousePosition,
        Vector3 worldRayOrigin,
        Vector3 worldRayDirection,
        EditorViewport viewport,
        Vector2 vpPos,
        Vector2 vpSize,
        bool toggleSelection)
    {
        Brush? brush = ActiveEditableBrush;
        if (brush?.EditableMesh == null || brush.Body == null)
            return false;

        EditableBrushMesh topology = brush.EditableMesh;
        Matrix4x4 transform = Matrix4x4.CreateFromQuaternion(brush.Body.Rotation) * Matrix4x4.CreateTranslation(brush.Body.Position);
        if (!Matrix4x4.Invert(transform, out Matrix4x4 inverse))
            return false;

        Vector3 localRayOrigin = Vector3.Transform(worldRayOrigin, inverse);
        Vector3 localRayDirection = Vector3.Normalize(Vector3.TransformNormal(worldRayDirection, inverse));
        Vector3 ToWorld(Vector3 local) => brush.Body.Position + Vector3.Transform(local, brush.Body.Rotation);
        int visibleFaceId = topology.TryRaycastFace(localRayOrigin, localRayDirection, out int rayFaceId, out _, out _)
            ? rayFaceId
            : -1;
        EditableBrushFace? visibleFace = visibleFaceId >= 0 ? topology.FindFace(visibleFaceId) : null;

        switch (_brushComponentMode)
        {
            case BrushComponentMode.Vertex:
            {
                int closest = -1;
                float closestDistance = 11.0f;
                foreach (EditableBrushVertex vertex in topology.Vertices)
                {
                    if (visibleFace != null && !visibleFace.Vertices.Contains(vertex.Id))
                        continue;
                    if (!TryWorldToScreen(ToWorld(vertex.Position), viewport, vpPos, vpSize, out Vector2 screenPosition))
                        continue;
                    float distance = Vector2.Distance(mousePosition, screenPosition);
                    if (distance < closestDistance)
                    {
                        closest = vertex.Id;
                        closestDistance = distance;
                    }
                }
                if (closest < 0)
                    return false;
                UpdateComponentSelection(_selectedBrushVertices, closest, toggleSelection);
                _lastBrushComponentSelectionTime = ImGui.GetTime();
                return true;
            }
            case BrushComponentMode.Edge:
            {
                EditableBrushEdge? closest = null;
                float closestDistance = 9.0f;
                foreach (EditableBrushEdge edge in topology.GetEdges())
                {
                    if (visibleFaceId >= 0 && !topology.GetFacesUsingEdge(edge).Any(face => face.Id == visibleFaceId))
                        continue;
                    if (!TryWorldToScreen(ToWorld(topology.GetPosition(edge.A)), viewport, vpPos, vpSize, out Vector2 first) ||
                        !TryWorldToScreen(ToWorld(topology.GetPosition(edge.B)), viewport, vpPos, vpSize, out Vector2 second))
                        continue;
                    float distance = DistanceToSegment(mousePosition, first, second);
                    if (distance < closestDistance)
                    {
                        closest = edge;
                        closestDistance = distance;
                    }
                }
                if (closest is not EditableBrushEdge edgeHit)
                    return false;
                UpdateComponentSelection(_selectedBrushEdges, edgeHit, toggleSelection);
                _lastBrushComponentSelectionTime = ImGui.GetTime();
                return true;
            }
            case BrushComponentMode.Face:
            {
                if (!topology.TryRaycastFace(localRayOrigin, localRayDirection, out int faceId, out _, out _))
                    return false;
                UpdateComponentSelection(_selectedBrushFaces, faceId, toggleSelection);
                _lastBrushComponentSelectionTime = ImGui.GetTime();
                return true;
            }
            default:
                return false;
        }
    }

    private void HandleBrushComponentGizmo(
        EditorViewport viewport,
        Vector2 vpPos,
        Vector2 vpSize,
        bool isHovered,
        EditorSceneService sceneService,
        EditorAssetService assetService,
        CommandHistory history)
    {
        Brush? brush = ActiveEditableBrush;
        if (brush?.EditableMesh == null || brush.Body == null)
            return;

        int[] vertexIds = GetSelectedComponentVertices(brush.EditableMesh).Distinct().ToArray();
        if (vertexIds.Length == 0)
        {
            // Mode changes clear component selection. Never leave a static
            // EditorGizmo drag alive while there is no possible owner.
            if (EditorGizmo.IsUsing() || _wasUsingGizmo)
                EndEditorGizmoInteraction(sceneService, assetService, history, finalizeEditableBrush: true);
            return;
        }

        Vector3 localPivot = Vector3.Zero;
        foreach (int vertexId in vertexIds)
            localPivot += brush.EditableMesh.GetPosition(vertexId);
        localPivot /= vertexIds.Length;

        Vector3 worldPivot = brush.Body.Position + Vector3.Transform(localPivot, brush.Body.Rotation);
        Matrix4x4 view = viewport.Camera.ViewMatrix;
        Matrix4x4 projection = viewport.Camera.ProjectionMatrix(vpSize.X / vpSize.Y);
        float snap = _snapEnabled ? _snapGrid : 0.0f;
        float angleSnap = _snapEnabled ? _snapAngle : 0.0f;
        bool changed = false;
        bool canManipulate = (isHovered && _activeDraggingViewport == null) || _activeDraggingViewport == viewport;
        bool selectionDelayActive = ImGui.GetTime() - _lastBrushComponentSelectionTime < 0.12;
        bool interactive = canManipulate && (!selectionDelayActive || EditorGizmo.IsUsing());

        // A component drag is transactional per frame: geometry that becomes
        // self-intersecting or impossible to triangulate is restored to the
        // last valid shape instead of reaching the renderer/collision system.
        bool ApplyValidVertexMutation(Action mutation)
        {
            var previousPositions = vertexIds.ToDictionary(
                vertexId => vertexId,
                brush.EditableMesh.GetPosition);
            mutation();
            if (brush.EditableMesh.TryValidate(out _))
                return true;

            foreach ((int vertexId, Vector3 position) in previousPositions)
            {
                EditableBrushVertex? vertex = brush.EditableMesh.FindVertex(vertexId);
                if (vertex != null)
                    vertex.Position = position;
            }
            return false;
        }

        if (canManipulate && _gizmoOperation == GizmoOperation.Translate &&
            EditorGizmo.ManipulateTranslation(worldPivot, view, projection, vpPos, vpSize, out Vector3 newPivot, snap, interactive))
        {
            Vector3 worldDelta = newPivot - worldPivot;
            if (worldDelta.LengthSquared() > 0.000001f)
            {
                Quaternion inverseRotation = Quaternion.Inverse(brush.Body.Rotation);
                Vector3 localDelta = Vector3.Transform(worldDelta, inverseRotation);
                changed = ApplyValidVertexMutation(() =>
                {
                    foreach (int vertexId in vertexIds)
                    {
                        EditableBrushVertex? vertex = brush.EditableMesh.FindVertex(vertexId);
                        if (vertex != null)
                            vertex.Position += localDelta;
                    }
                });
            }
        }
        else if (canManipulate && _gizmoOperation == GizmoOperation.Rotate &&
            EditorGizmo.ManipulateRotation(worldPivot, Quaternion.Identity, view, projection, vpPos, vpSize, out Quaternion rotation, angleSnap, interactive))
        {
            Quaternion delta = Quaternion.Normalize(rotation);
            if (MathF.Abs(delta.X) + MathF.Abs(delta.Y) + MathF.Abs(delta.Z) > 0.00001f)
            {
                Quaternion inverseRotation = Quaternion.Inverse(brush.Body.Rotation);
                changed = ApplyValidVertexMutation(() =>
                {
                    foreach (int vertexId in vertexIds)
                    {
                        EditableBrushVertex? vertex = brush.EditableMesh.FindVertex(vertexId);
                        if (vertex == null)
                            continue;
                        Vector3 world = brush.Body.Position + Vector3.Transform(vertex.Position, brush.Body.Rotation);
                        Vector3 rotatedWorld = worldPivot + Vector3.Transform(world - worldPivot, delta);
                        vertex.Position = Vector3.Transform(rotatedWorld - brush.Body.Position, inverseRotation);
                    }
                });
            }
        }
        else if (canManipulate && _gizmoOperation == GizmoOperation.Scale &&
            EditorGizmo.ManipulateScale(worldPivot, Vector3.One, view, projection, vpPos, vpSize, out Vector3 newScale, snap, interactive))
        {
            Vector3 scale = Vector3.Max(new Vector3(0.001f), newScale);
            if (Vector3.DistanceSquared(scale, Vector3.One) > 0.000001f)
            {
                changed = ApplyValidVertexMutation(() =>
                {
                    foreach (int vertexId in vertexIds)
                    {
                        EditableBrushVertex? vertex = brush.EditableMesh.FindVertex(vertexId);
                        if (vertex != null)
                            vertex.Position = localPivot + (vertex.Position - localPivot) * scale;
                    }
                });
            }
        }

        if (changed)
            RefreshEditableBrush(brush, sceneService, assetService, normalizeOrigin: false);

        bool usingNow = EditorGizmo.IsUsing();
        if (usingNow && _activeDraggingViewport == null)
            _activeDraggingViewport = viewport;
        if (usingNow && !_wasUsingGizmo)
            Undo.RecordState(_frameBeginState);
        if (!usingNow && _wasUsingGizmo)
            Undo.ForceEnd(history, sceneService, assetService);
        _wasUsingGizmo = usingNow;
    }

    private bool HandleKnifeClick(
        Vector3 worldRayOrigin,
        Vector3 worldRayDirection,
        EditorSceneService sceneService,
        EditorAssetService assetService,
        CommandHistory history)
    {
        Brush? brush = ActiveEditableBrush;
        if (brush?.EditableMesh == null || brush.Body == null)
            return false;

        Matrix4x4 transform = Matrix4x4.CreateFromQuaternion(brush.Body.Rotation) * Matrix4x4.CreateTranslation(brush.Body.Position);
        if (!Matrix4x4.Invert(transform, out Matrix4x4 inverse))
            return false;

        Vector3 localOrigin = Vector3.Transform(worldRayOrigin, inverse);
        Vector3 localDirection = Vector3.Normalize(Vector3.TransformNormal(worldRayDirection, inverse));
        if (!brush.EditableMesh.TryRaycastFace(localOrigin, localDirection, out int faceId, out Vector3 point, out _))
            return false;

        if (_knifeFirstPoint == null || _knifeFaceId != faceId)
        {
            _knifeFaceId = faceId;
            _knifeFirstPoint = point;
            _selectedBrushFaces.Clear();
            _selectedBrushFaces.Add(faceId);
            return true;
        }

        string pre = sceneService.Document.Serialize();
        if (!brush.EditableMesh.TryKnifeCut(faceId, _knifeFirstPoint.Value, point, out string error))
        {
            ShowDocumentError(error);
            _knifeFirstPoint = null;
            _knifeFaceId = -1;
            return true;
        }

        RefreshEditableBrush(brush, sceneService, assetService);
        _selectedBrushFaces.Clear();
        _selectedBrushFaces.Add(faceId);
        _knifeFirstPoint = null;
        _knifeFaceId = -1;
        CommitBrushTopologySnapshot(pre, sceneService, assetService, history);
        return true;
    }

    private static void UpdateComponentSelection<T>(HashSet<T> selection, T item, bool toggleSelection) where T : notnull
    {
        if (toggleSelection)
        {
            if (!selection.Add(item))
                selection.Remove(item);
            return;
        }
        selection.Clear();
        selection.Add(item);
    }

    private static float DistanceToSegment(Vector2 point, Vector2 first, Vector2 second)
    {
        Vector2 segment = second - first;
        float lengthSquared = segment.LengthSquared();
        if (lengthSquared < 0.0001f)
            return Vector2.Distance(point, first);
        float t = float.Clamp(Vector2.Dot(point - first, segment) / lengthSquared, 0.0f, 1.0f);
        return Vector2.Distance(point, Vector2.Lerp(first, second, t));
    }

    private bool RaySphereIntersect(Vector3 ro, Vector3 rd, Vector3 center, float radius, out float t)
    {
        t = 0;
        Vector3 m = ro - center;
        float b = Vector3.Dot(m, rd);
        float c = Vector3.Dot(m, m) - radius * radius;
        if (c > 0.0f && b > 0.0f) return false;
        float discr = b * b - c;
        if (discr < 0.0f) return false;
        t = -b - MathF.Sqrt(discr);
        if (t < 0.0f) t = 0.0f;
        return true;
    }

    private bool RayAABBIntersect(Vector3 ro, Vector3 rd, Vector3 min, Vector3 max, out float t)
    {
        t = 0;
        float t1 = (min.X - ro.X) / rd.X;
        float t2 = (max.X - ro.X) / rd.X;
        float t3 = (min.Y - ro.Y) / rd.Y;
        float t4 = (max.Y - ro.Y) / rd.Y;
        float t5 = (min.Z - ro.Z) / rd.Z;
        float t6 = (max.Z - ro.Z) / rd.Z;

        float tmin = MathF.Max(MathF.Max(MathF.Min(t1, t2), MathF.Min(t3, t4)), MathF.Min(t5, t6));
        float tmax = MathF.Min(MathF.Min(MathF.Max(t1, t2), MathF.Max(t3, t4)), MathF.Max(t5, t6));

        if (tmax < 0 || tmin > tmax) return false;
        t = tmin;
        return true;
    }

    

    

    private float ApplySnap(float val, float snap)
    {
        if (!_snapEnabled || snap <= 0) return val;
        return MathF.Round(val / snap) * snap;
    }

    private Vector3 ApplySnap(Vector3 val, float snap)
    {
        return new Vector3(ApplySnap(val.X, snap), ApplySnap(val.Y, snap), ApplySnap(val.Z, snap));
    }

    private static void FocusCameraOnObject(
        MapObject obj,
        EditorSceneService sceneService,
        EditorViewport vp3D,
        EditorViewport vpTop,
        EditorViewport vpFront,
        EditorViewport vpSide)
    {
        if (!TryGetFocusSphere(obj, sceneService, out Vector3 center, out float radius))
            return;

        FrameCameraOnSphere(vp3D, center, radius);
        FrameCameraOnSphere(vpTop, center, radius);
        FrameCameraOnSphere(vpFront, center, radius);
        FrameCameraOnSphere(vpSide, center, radius);
    }

    private static bool TryGetFocusSphere(
        MapObject obj,
        EditorSceneService sceneService,
        out Vector3 center,
        out float radius)
    {
        center = obj.Body?.Position ?? Vector3.Zero;
        radius = 1.0f;

        // Prefer the rendered bounds. This includes imported models, editable
        // brushes and primitive meshes after their authored transform/scale is
        // applied, so the focus target is the visual object rather than merely
        // the physics origin.
        var combinedBounds = new Fuse.Math.AABB();
        bool hasVisualBounds = false;
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void IncludeObjectBounds(MapObject candidate)
        {
            if (!visited.Add(candidate.Id))
                return;

            Entity? entity = sceneService.Scene.Entities.FirstOrDefault(
                item => item.Id.Equals(candidate.Id, StringComparison.OrdinalIgnoreCase));
            if (entity != null)
            {
                Fuse.Math.AABB bounds = entity.GetWorldRenderBounds();
                if (bounds.IsValid)
                {
                    if (!hasVisualBounds)
                    {
                        combinedBounds = bounds;
                        hasVisualBounds = true;
                    }
                    else
                    {
                        combinedBounds.Grow(bounds);
                    }
                }
            }

            // A group has no mesh of its own. Include all descendants so F on
            // a group frames the complete composition, as in Unity/Godot.
            foreach (MapObject child in sceneService.Document.Objects.Where(
                         item => string.Equals(item.ParentId, candidate.Id,
                             StringComparison.OrdinalIgnoreCase)))
            {
                IncludeObjectBounds(child);
            }
        }

        IncludeObjectBounds(obj);
        if (hasVisualBounds)
        {
            center = combinedBounds.GetCenter();
            radius = Vector3.Distance(center, combinedBounds.GetBoundsMax());
            if (float.IsFinite(radius) && radius > 0.0001f)
                return true;
        }

        // Collision fallback keeps focus useful for invisible/collision-only
        // objects and lights, which do not have render bounds.
        if (TryGetBodyFocusSphere(obj.Body, out center, out radius))
            return true;

        if (obj.IsLight)
        {
            center = obj.Body?.Position ?? Vector3.Zero;
            radius = 1.0f;
            return true;
        }

        return false;
    }

    private static bool TryGetBodyFocusSphere(
        MapBody? body,
        out Vector3 center,
        out float radius)
    {
        center = body?.Position ?? Vector3.Zero;
        radius = 1.0f;
        if (body == null)
            return false;

        switch (body.Shape)
        {
            case MapShapeType.Box when body.HalfExtents.HasValue:
                radius = body.HalfExtents.Value.Length();
                break;
            case MapShapeType.Sphere when body.Radius.HasValue:
                radius = MathF.Abs(body.Radius.Value);
                break;
            case MapShapeType.Capsule when body.Radius.HasValue && body.Height.HasValue:
                radius = MathF.Abs(body.Height.Value) * 0.5f + MathF.Abs(body.Radius.Value);
                break;
            default:
                return false;
        }

        return float.IsFinite(radius) && radius > 0.0001f &&
               float.IsFinite(center.X) && float.IsFinite(center.Y) && float.IsFinite(center.Z);
    }

    private static void FrameCameraOnSphere(
        EditorViewport viewport,
        Vector3 center,
        float radius)
    {
        ViewportCamera camera = viewport.Camera;
        float safeRadius = MathF.Max(MathF.Abs(radius), 0.5f);

        if (camera.IsOrthographic)
        {
            camera.Position = center;
            camera.OrthoSize = Math.Clamp(safeRadius * 2.0f * 1.25f, 0.1f, 10000.0f);
            return;
        }

        float aspect = MathF.Max(0.01f, (float)viewport.Width / MathF.Max(viewport.Height, 1));
        float verticalHalfFov = float.DegreesToRadians(
            float.Clamp(camera.FieldOfView, 1.0f, 170.0f)) * 0.5f;
        float horizontalHalfFov = MathF.Atan(MathF.Tan(verticalHalfFov) * aspect);
        float limitingHalfFov = MathF.Min(verticalHalfFov, horizontalHalfFov);
        float visibleSin = MathF.Max(MathF.Sin(limitingHalfFov), 0.01f);
        float distance = safeRadius / visibleSin * 1.15f;
        distance = MathF.Max(distance, safeRadius + camera.NearClipPlane + 0.05f);

        Vector3 front = camera.Front;
        if (front.LengthSquared() <= 0.000001f)
            front = -Vector3.UnitZ;
        else
            front = Vector3.Normalize(front);

        // Keep the current orbit direction and place the object at the center
        // of the view, instead of putting the camera inside the object.
        camera.Position = center - front * distance;
    }

    private void DrawMapWindow(
        EditorSceneService sceneService, 
        EditorAssetService assetService, 
        CommandHistory history,
        EditorViewport viewport3D, 
        EditorViewport viewportTop, 
        EditorViewport viewportFront, 
        EditorViewport viewportSide)
    {
        var doc = sceneService.Document;
        var scene = sceneService.Scene;

        static Vector3 QuaternionToEuler(Quaternion q)
        {
            float t0 = 2.0f * (q.W * q.X + q.Y * q.Z);
            float t1 = 1.0f - 2.0f * (q.X * q.X + q.Y * q.Y);
            float pitch = MathF.Atan2(t0, t1);

            float t2 = 2.0f * (q.W * q.Y - q.Z * q.X);
            t2 = float.Clamp(t2, -1.0f, 1.0f);
            float yaw = MathF.Asin(t2);

            float t3 = 2.0f * (q.W * q.Z + q.X * q.Y);
            float t4 = 1.0f - 2.0f * (q.Y * q.Y + q.Z * q.Z);
            float roll = MathF.Atan2(t3, t4);

            return new Vector3(
                float.RadiansToDegrees(pitch),
                float.RadiansToDegrees(yaw),
                float.RadiansToDegrees(roll)
            );
        }

        static Quaternion EulerToQuaternion(Vector3 euler)
        {
            float pitch = float.DegreesToRadians(euler.X);
            float yaw = float.DegreesToRadians(euler.Y);
            float roll = float.DegreesToRadians(euler.Z);

            var qx = Quaternion.CreateFromAxisAngle(Vector3.UnitX, pitch);
            var qy = Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw);
            var qz = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, roll);
            return qz * qy * qx;
        }

        ImGui.SetNextWindowSize(new Vector2(400, 600), ImGuiCond.FirstUseEver);

        if (!ImGui.Begin("Map Objects", ImGuiWindowFlags.NoCollapse))
        {
            ImGui.End();
            return;
        }

        // --- Editor Settings ---
        if (ImGui.CollapsingHeader("Editor Settings & Snapping", ImGuiTreeNodeFlags.None))
        {
            ImGui.Separator();
            ImGui.TextColored(new Vector4(0.72f, 0.84f, 1.0f, 1.0f), "Camera");
            ImGui.Separator();

            float cameraFov = viewport3D.Camera.FieldOfView;
            ImGui.SetNextItemWidth(220.0f);
            if (ImGui.SliderFloat("3D Camera FOV", ref cameraFov, 20.0f, 120.0f, "%.1f deg"))
            {
                viewport3D.Camera.FieldOfView = cameraFov;
                viewport3D.RequestRender();
            }
            float cameraFarClipPlane = viewport3D.Camera.FarClipPlane;
            ImGui.TextDisabled("Affects only the perspective 3D viewport.");
            if (ImGui.SliderFloat("3D Camera Far Clip Plane", ref cameraFarClipPlane, 20.0f, 10000.0f, "%.1f deg"))
            {
                viewport3D.Camera.FarClipPlane = cameraFarClipPlane;
                viewport3D.RequestRender();
            }
            ImGui.TextDisabled("Far render distance");
            

            ImGui.Spacing();
            ImGui.Separator();
            ImGui.TextColored(new Vector4(0.72f, 0.84f, 1.0f, 1.0f), "Snapping");
            ImGui.Separator();

            ImGui.Checkbox("Enable Snapping", ref _snapEnabled);
            if (_snapEnabled)
            {
                float[] gridSizes = { 0.0625f, 0.125f, 0.25f, 0.5f, 1.0f, 2.0f, 4.0f, 8.0f, 16.0f, 32.0f, 64.0f };
                int currentIdx = Array.IndexOf(gridSizes, _snapGrid);
                if (currentIdx == -1) currentIdx = 3; // Default 0.5
                
                string[] gridSizeLabels = gridSizes.Select(g => g.ToString("0.0000").TrimEnd('0').TrimEnd(',').TrimEnd('.')).ToArray();
                ImGui.SetNextItemWidth(120);
                if (ImGui.Combo("Grid Snap", ref currentIdx, gridSizeLabels, gridSizeLabels.Length))
                {
                    _snapGrid = gridSizes[currentIdx];
                }
                ImGui.SameLine();
                ImGui.Text("[Halve with [, Double with ]]");

                ImGui.DragFloat("Angle Snap", ref _snapAngle, 1.0f, 1.0f, 90.0f);
            }
        }

        // --- Creation Controls ---
        if (ImGui.CollapsingHeader("Create Objects", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.TextDisabled($"Objects in scene: {doc.Objects.Count}");

            ImGui.Separator();
            ImGui.TextColored(new Vector4(0.72f, 0.84f, 1.0f, 1.0f), "Primitives");
            ImGui.Separator();

            float itemSpacing = ImGui.GetStyle().ItemSpacing.X;
            float primitiveWidth = MathF.Max(70.0f,
                (ImGui.GetContentRegionAvail().X - itemSpacing * 2.0f) / 3.0f);
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.16f, 0.25f, 0.36f, 1.0f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.24f, 0.38f, 0.52f, 1.0f));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.30f, 0.46f, 0.62f, 1.0f));
            if (ImGui.Button("Box", new Vector2(primitiveWidth, 0)))
                AddNewObject(sceneService, assetService, history, MapShapeType.Box);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Create a box brush.");
            ImGui.SameLine();
            if (ImGui.Button("Sphere", new Vector2(primitiveWidth, 0)))
                AddNewObject(sceneService, assetService, history, MapShapeType.Sphere);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Create a sphere brush.");
            ImGui.SameLine();
            if (ImGui.Button("Capsule", new Vector2(primitiveWidth, 0)))
                AddNewObject(sceneService, assetService, history, MapShapeType.Capsule);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Create a capsule brush.");
            ImGui.PopStyleColor(3);

            ImGui.Spacing();
            ImGui.Separator();
            ImGui.TextColored(new Vector4(0.78f, 0.66f, 0.95f, 1.0f), "Assets");
            ImGui.Separator();
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.25f, 0.18f, 0.34f, 1.0f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.38f, 0.27f, 0.50f, 1.0f));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.46f, 0.34f, 0.60f, 1.0f));
            if (ImGui.Button("Model...", new Vector2(-1, 0)))
            {
                _showModelImportDialog = true;
                RefreshModelFileList(assetService.FuseResPath);
            }
            ImGui.PopStyleColor(3);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Import a model from the project assets.");

            ImGui.Spacing();
            if (ImGui.Button("Terrain...", new Vector2(-1, 0)))
            {
                _showTerrainCreateDialog = true;
                _terrainProceduralPreviewDirty = true;
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Create a heightmap terrain asset and add it to the map.");

            ImGui.Spacing();
            if (ImGui.Button("Ocean", new Vector2(-1, 0)))
            {
                Undo.RecordState(_frameBeginState);
                doc.Ocean.Enabled = true;
                Undo.ForceEnd(history, sceneService, assetService);
                viewport3D.RequestRender();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Enable the render-only global ocean. It does not add player physics.");

            ImGui.Spacing();
            ImGui.Separator();
            ImGui.TextColored(new Vector4(1.0f, 0.82f, 0.38f, 1.0f), "Lights");
            ImGui.Separator();
            float lightWidth = MathF.Max(70.0f,
                (ImGui.GetContentRegionAvail().X - itemSpacing * 2.0f) / 3.0f);
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.34f, 0.28f, 0.12f, 1.0f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.50f, 0.40f, 0.16f, 1.0f));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.62f, 0.50f, 0.20f, 1.0f));
            if (ImGui.Button("Point Light", new Vector2(lightWidth, 0)))
                AddNewLight(sceneService, assetService, history, "point");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Create a point light.");
            ImGui.SameLine();
            if (ImGui.Button("Spot Light", new Vector2(lightWidth, 0)))
                AddNewLight(sceneService, assetService, history, "spot");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Create a spot light.");
            ImGui.SameLine();
            if (ImGui.Button("Directional Light", new Vector2(lightWidth, 0)))
                AddNewLight(sceneService, assetService, history, "directional");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Create a directional light.");
            ImGui.PopStyleColor(3);
        }

        if (ImGui.CollapsingHeader("Environment", ImGuiTreeNodeFlags.DefaultOpen))
        {
            SkyboxMode currentSkyboxMode = doc.Skybox.Mode;
            bool currentProceduralSky = currentSkyboxMode == SkyboxMode.Procedural;
            string currentSkyboxPath = doc.SkyboxPath ?? "";
            string currentSkyboxLabel = currentProceduralSky
                ? "Procedural Sky"
                : string.IsNullOrWhiteSpace(currentSkyboxPath)
                ? $"Default ({Path.GetFileName(EditorAssetService.DefaultSkyboxPath)})"
                : currentSkyboxPath;
            SkyboxMode selectedSkyboxMode = currentSkyboxMode;
            string selectedSkyboxPath = currentSkyboxPath;
            bool skyboxChanged = false;

            ImGui.SetNextItemWidth(-1);
            if (ImGui.BeginCombo("Skybox##mapSkybox", currentSkyboxLabel))
            {
                if (ImGui.Selectable(
                        "Procedural Sky##proceduralSkybox",
                        currentProceduralSky))
                {
                    selectedSkyboxMode = SkyboxMode.Procedural;
                    selectedSkyboxPath = "";
                    skyboxChanged = true;
                }

                if (ImGui.Selectable(
                        $"Default ({Path.GetFileName(EditorAssetService.DefaultSkyboxPath)})##defaultSkybox",
                        !currentProceduralSky && string.IsNullOrWhiteSpace(currentSkyboxPath)))
                {
                    selectedSkyboxMode = SkyboxMode.Texture;
                    selectedSkyboxPath = "";
                    skyboxChanged = true;
                }

                foreach (string skyboxPath in assetService.EnumerateSkyboxes())
                {
                    bool selected = !currentProceduralSky &&
                        skyboxPath.Equals(currentSkyboxPath, StringComparison.OrdinalIgnoreCase);
                    if (ImGui.Selectable(skyboxPath, selected))
                    {
                        selectedSkyboxMode = SkyboxMode.Texture;
                        selectedSkyboxPath = skyboxPath;
                        skyboxChanged = true;
                    }
                    if (selected)
                        ImGui.SetItemDefaultFocus();
                }

                ImGui.EndCombo();
            }

            ImGui.TextDisabled(currentProceduralSky
                ? "Sun direction follows the first enabled Directional Light."
                : "Use an equirectangular image from res/Textures/Skybox.");
            bool environmentModeChanged = selectedSkyboxMode != currentSkyboxMode;
            bool environmentPathChanged = !selectedSkyboxPath.Equals(
                currentSkyboxPath,
                StringComparison.OrdinalIgnoreCase);
            if (skyboxChanged && (environmentModeChanged || environmentPathChanged))
            {
                Undo.RecordState(_frameBeginState);
                doc.Skybox.Mode = selectedSkyboxMode;
                doc.SkyboxPath = selectedSkyboxPath;
                if (selectedSkyboxMode == SkyboxMode.Procedural)
                {
                    assetService.SetProceduralSkybox(doc.Skybox);
                    _previewSkyboxDocumentPath = "__procedural__";
                    _previewSkyboxSettingsSignature = ProceduralSky.ComputeSettingsSignature(doc.Skybox);
                    viewport3D.RequestRender();
                    viewportTop.RequestRender();
                    viewportFront.RequestRender();
                    viewportSide.RequestRender();
                }
                else
                {
                    ApplySkyboxPreview(
                        selectedSkyboxPath,
                        assetService,
                        viewport3D,
                        viewportTop,
                        viewportFront,
                        viewportSide);
                }
                Undo.ForceEnd(history, sceneService, assetService);
            }

            if (doc.Skybox.Mode == SkyboxMode.Procedural)
            {
                ImGui.Separator();
                ImGui.TextUnformatted("Procedural sky");

                bool proceduralSettingsChanged = false;
                Vector3 zenithColor = doc.Skybox.ZenithColor;
                if (ImGui.ColorEdit3(
                        "Zenith color##skyZenithColor",
                        ref zenithColor,
                        ImGuiColorEditFlags.Float))
                {
                    doc.Skybox.ZenithColor = zenithColor;
                    proceduralSettingsChanged = true;
                }
                Undo.TrackItem(_frameBeginState);

                Vector3 horizonColor = doc.Skybox.HorizonColor;
                if (ImGui.ColorEdit3(
                        "Horizon color##skyHorizonColor",
                        ref horizonColor,
                        ImGuiColorEditFlags.Float))
                {
                    doc.Skybox.HorizonColor = horizonColor;
                    proceduralSettingsChanged = true;
                }
                Undo.TrackItem(_frameBeginState);

                Vector3 groundColor = doc.Skybox.GroundColor;
                if (ImGui.ColorEdit3(
                        "Ground color##skyGroundColor",
                        ref groundColor,
                        ImGuiColorEditFlags.Float))
                {
                    doc.Skybox.GroundColor = groundColor;
                    proceduralSettingsChanged = true;
                }
                Undo.TrackItem(_frameBeginState);

                Vector3 nightZenithColor = doc.Skybox.NightZenithColor;
                if (ImGui.ColorEdit3(
                        "Night zenith color##skyNightZenithColor",
                        ref nightZenithColor,
                        ImGuiColorEditFlags.Float))
                {
                    doc.Skybox.NightZenithColor = nightZenithColor;
                    proceduralSettingsChanged = true;
                }
                Undo.TrackItem(_frameBeginState);

                Vector3 nightHorizonColor = doc.Skybox.NightHorizonColor;
                if (ImGui.ColorEdit3(
                        "Night horizon color##skyNightHorizonColor",
                        ref nightHorizonColor,
                        ImGuiColorEditFlags.Float))
                {
                    doc.Skybox.NightHorizonColor = nightHorizonColor;
                    proceduralSettingsChanged = true;
                }
                Undo.TrackItem(_frameBeginState);

                Vector3 sunColor = doc.Skybox.SunColor;
                if (ImGui.ColorEdit3(
                        "Sun color##skySunColor",
                        ref sunColor,
                        ImGuiColorEditFlags.Float))
                {
                    doc.Skybox.SunColor = sunColor;
                    proceduralSettingsChanged = true;
                }
                Undo.TrackItem(_frameBeginState);

                Vector3 starColor = doc.Skybox.StarColor;
                if (ImGui.ColorEdit3(
                        "Star color##skyStarColor",
                        ref starColor,
                        ImGuiColorEditFlags.Float))
                {
                    doc.Skybox.StarColor = starColor;
                    proceduralSettingsChanged = true;
                }
                Undo.TrackItem(_frameBeginState);

                float sunIntensity = doc.Skybox.SunIntensity;
                if (ImGui.DragFloat(
                        "Sun intensity##skySunIntensity",
                        ref sunIntensity,
                        0.1f,
                        0.0f,
                        100.0f,
                        "%.2f"))
                {
                    doc.Skybox.SunIntensity = MathF.Max(0.0f, sunIntensity);
                    proceduralSettingsChanged = true;
                }
                Undo.TrackItem(_frameBeginState);

                float sunRadius = doc.Skybox.SunAngularRadiusDegrees;
                if (ImGui.DragFloat(
                        "Sun angular radius##skySunRadius",
                        ref sunRadius,
                        0.01f,
                        0.01f,
                        10.0f,
                        "%.2f deg"))
                {
                    doc.Skybox.SunAngularRadiusDegrees = Math.Clamp(sunRadius, 0.01f, 10.0f);
                    proceduralSettingsChanged = true;
                }
                Undo.TrackItem(_frameBeginState);

                float starIntensity = doc.Skybox.StarIntensity;
                if (ImGui.DragFloat(
                        "Star intensity##skyStarIntensity",
                        ref starIntensity,
                        0.05f,
                        0.0f,
                        20.0f,
                        "%.2f"))
                {
                    doc.Skybox.StarIntensity = MathF.Max(0.0f, starIntensity);
                    proceduralSettingsChanged = true;
                }
                Undo.TrackItem(_frameBeginState);

                float starDensity = doc.Skybox.StarDensity;
                if (ImGui.DragFloat(
                        "Star density##skyStarDensity",
                        ref starDensity,
                        0.05f,
                        0.0f,
                        2.0f,
                        "%.2f"))
                {
                    doc.Skybox.StarDensity = Math.Clamp(starDensity, 0.0f, 2.0f);
                    proceduralSettingsChanged = true;
                }
                Undo.TrackItem(_frameBeginState);

                float atmosphere = doc.Skybox.AtmosphereStrength;
                if (ImGui.DragFloat(
                        "Atmosphere strength##skyAtmosphere",
                        ref atmosphere,
                        0.05f,
                        0.0f,
                        4.0f,
                        "%.2f"))
                {
                    doc.Skybox.AtmosphereStrength = MathF.Max(0.0f, atmosphere);
                    proceduralSettingsChanged = true;
                }
                Undo.TrackItem(_frameBeginState);

                float rayleigh = doc.Skybox.RayleighStrength;
                if (ImGui.DragFloat(
                        "Rayleigh strength##skyRayleigh",
                        ref rayleigh,
                        0.05f,
                        0.0f,
                        4.0f,
                        "%.2f"))
                {
                    doc.Skybox.RayleighStrength = MathF.Max(0.0f, rayleigh);
                    proceduralSettingsChanged = true;
                }
                Undo.TrackItem(_frameBeginState);

                float mie = doc.Skybox.MieStrength;
                if (ImGui.DragFloat(
                        "Mie strength##skyMie",
                        ref mie,
                        0.05f,
                        0.0f,
                        4.0f,
                        "%.2f"))
                {
                    doc.Skybox.MieStrength = MathF.Max(0.0f, mie);
                    proceduralSettingsChanged = true;
                }
                Undo.TrackItem(_frameBeginState);

                float exposure = doc.Skybox.Exposure;
                if (ImGui.DragFloat(
                        "Sky exposure##skyExposure",
                        ref exposure,
                        0.05f,
                        0.001f,
                        8.0f,
                        "%.2f"))
                {
                    doc.Skybox.Exposure = MathF.Max(0.001f, exposure);
                    proceduralSettingsChanged = true;
                }
                Undo.TrackItem(_frameBeginState);

                if (proceduralSettingsChanged)
                {
                    assetService.SetProceduralSkybox(doc.Skybox);
                    assetService.FogRenderer?.InvalidateHistory();
                    viewport3D.RequestRender();
                    viewportTop.RequestRender();
                    viewportFront.RequestRender();
                    viewportSide.RequestRender();
                }
            }

            ImGui.Separator();
            ImGui.TextUnformatted("Volumetric clouds");
            VolumetricCloudSettings clouds = doc.Clouds;
            bool cloudSettingsChanged = false;

            bool cloudsEnabled = clouds.Enabled;
            if (ImGui.Checkbox("Enabled##volumetricCloudsEnabled", ref cloudsEnabled))
            {
                clouds.Enabled = cloudsEnabled;
                cloudSettingsChanged = true;
            }
            Undo.TrackItem(_frameBeginState);
            ImGui.SameLine();
            ImGui.TextDisabled("Rendered only in the 3D viewport.");

            if (clouds.Enabled)
            {
                string[] cloudPresetLabels =
                ["Weather mix", "Stratus", "Stratocumulus", "Cumulus"];
                int cloudPresetIndex = Math.Clamp((int)clouds.Preset, 0, cloudPresetLabels.Length - 1);
                if (ImGui.Combo(
                        "Cloud preset##volumetricCloudPreset",
                        ref cloudPresetIndex,
                        cloudPresetLabels,
                        cloudPresetLabels.Length))
                {
                    clouds.ApplyPreset((VolumetricCloudPreset)cloudPresetIndex);
                    cloudSettingsChanged = true;
                }
                Undo.TrackItem(_frameBeginState);
            }

            if (clouds.Enabled && ImGui.TreeNodeEx(
                    "Cloud layer settings##volumetricCloudSettings",
                    ImGuiTreeNodeFlags.DefaultOpen))
            {
                float baseHeight = clouds.BaseHeight;
                if (ImGui.DragFloat("Base height##cloudBaseHeight", ref baseHeight, 1.0f, -5000.0f, 10000.0f, "%.1f"))
                {
                    clouds.BaseHeight = baseHeight;
                    cloudSettingsChanged = true;
                }
                Undo.TrackItem(_frameBeginState);

                float thickness = clouds.Thickness;
                if (ImGui.DragFloat("Thickness##cloudThickness", ref thickness, 1.0f, 1.0f, 5000.0f, "%.1f"))
                {
                    clouds.Thickness = MathF.Max(1.0f, thickness);
                    cloudSettingsChanged = true;
                }
                Undo.TrackItem(_frameBeginState);

                float coverage = clouds.Coverage;
                if (ImGui.SliderFloat("Coverage##cloudCoverage", ref coverage, 0.0f, 1.0f, "%.2f"))
                {
                    clouds.Coverage = Math.Clamp(coverage, 0.0f, 1.0f);
                    cloudSettingsChanged = true;
                }
                Undo.TrackItem(_frameBeginState);

                float density = clouds.Density;
                if (ImGui.SliderFloat("Density##cloudDensity", ref density, 0.0f, 4.0f, "%.2f"))
                {
                    clouds.Density = Math.Clamp(density, 0.0f, 8.0f);
                    cloudSettingsChanged = true;
                }
                Undo.TrackItem(_frameBeginState);

                float scale = clouds.Scale;
                if (ImGui.DragFloat("World scale##cloudScale", ref scale, 0.0001f, 0.0001f, 0.15f, "%.4f"))
                {
                    clouds.Scale = Math.Clamp(scale, 0.00001f, 1.0f);
                    cloudSettingsChanged = true;
                }
                Undo.TrackItem(_frameBeginState);

                float detailScale = clouds.DetailScale;
                if (ImGui.SliderFloat("Detail scale##cloudDetailScale", ref detailScale, 1.0f, 12.0f, "%.2f"))
                {
                    clouds.DetailScale = detailScale;
                    cloudSettingsChanged = true;
                }
                Undo.TrackItem(_frameBeginState);

                float detailStrength = clouds.DetailStrength;
                if (ImGui.SliderFloat("Detail erosion##cloudDetailStrength", ref detailStrength, 0.0f, 1.0f, "%.2f"))
                {
                    clouds.DetailStrength = detailStrength;
                    cloudSettingsChanged = true;
                }
                Undo.TrackItem(_frameBeginState);

                ImGui.SeparatorText("Cloud shape");
                float shapeFactor = clouds.ShapeFactor;
                if (ImGui.SliderFloat("Macro shape##cloudShapeFactor", ref shapeFactor, 0.0f, 1.0f, "%.2f"))
                {
                    clouds.ShapeFactor = shapeFactor;
                    cloudSettingsChanged = true;
                }
                Undo.TrackItem(_frameBeginState);

                float erosionFactor = clouds.ErosionFactor;
                if (ImGui.SliderFloat("Edge erosion##cloudErosionFactor", ref erosionFactor, 0.0f, 2.0f, "%.2f"))
                {
                    clouds.ErosionFactor = erosionFactor;
                    cloudSettingsChanged = true;
                }
                Undo.TrackItem(_frameBeginState);

                float erosionOcclusion = clouds.ErosionOcclusion;
                if (ImGui.SliderFloat("Erosion ambient occlusion##cloudErosionOcclusion", ref erosionOcclusion, 0.0f, 2.0f, "%.2f"))
                {
                    clouds.ErosionOcclusion = erosionOcclusion;
                    cloudSettingsChanged = true;
                }
                Undo.TrackItem(_frameBeginState);

                float domainWarpStrength = clouds.DomainWarpStrength;
                if (ImGui.SliderFloat("Domain warp##cloudDomainWarp", ref domainWarpStrength, 0.0f, 1.0f, "%.2f"))
                {
                    clouds.DomainWarpStrength = domainWarpStrength;
                    cloudSettingsChanged = true;
                }
                Undo.TrackItem(_frameBeginState);

                float secondaryShapeStrength = clouds.SecondaryShapeStrength;
                if (ImGui.SliderFloat("Secondary shape##cloudSecondaryShape", ref secondaryShapeStrength, 0.0f, 1.0f, "%.2f"))
                {
                    clouds.SecondaryShapeStrength = secondaryShapeStrength;
                    cloudSettingsChanged = true;
                }
                Undo.TrackItem(_frameBeginState);

                Vector2 windDirection = clouds.WindDirection;
                if (ImGui.DragFloat2("Wind direction X/Z##cloudWindDirection", ref windDirection, 0.01f, -1.0f, 1.0f, "%.2f"))
                {
                    clouds.WindDirection = windDirection.LengthSquared() > 1e-8f
                        ? Vector2.Normalize(windDirection)
                        : Vector2.UnitX;
                    cloudSettingsChanged = true;
                }
                Undo.TrackItem(_frameBeginState);

                float windSpeed = clouds.WindSpeed;
                if (ImGui.DragFloat("Wind speed##cloudWindSpeed", ref windSpeed, 0.1f, -100.0f, 100.0f, "%.1f"))
                {
                    clouds.WindSpeed = windSpeed;
                    cloudSettingsChanged = true;
                }
                Undo.TrackItem(_frameBeginState);

                float maxDistance = clouds.MaxDistance;
                if (ImGui.DragFloat("Maximum distance##cloudMaxDistance", ref maxDistance, 10.0f, 100.0f, 20000.0f, "%.0f"))
                {
                    clouds.MaxDistance = Math.Clamp(maxDistance, 10.0f, 20000.0f);
                    cloudSettingsChanged = true;
                }
                Undo.TrackItem(_frameBeginState);

                ImGui.SeparatorText("Quality and lighting");
                int primarySteps = clouds.PrimarySteps;
                if (ImGui.SliderInt("Ray-march steps##cloudPrimarySteps", ref primarySteps, 64, 128))
                {
                    clouds.PrimarySteps = Math.Clamp(primarySteps, 64, 128);
                    cloudSettingsChanged = true;
                }
                Undo.TrackItem(_frameBeginState);

                int lightSteps = clouds.LightSteps;
                if (ImGui.SliderInt("Light steps##cloudLightSteps", ref lightSteps, 6, 24))
                {
                    clouds.LightSteps = Math.Clamp(lightSteps, 6, 24);
                    cloudSettingsChanged = true;
                }
                Undo.TrackItem(_frameBeginState);

                float resolutionScale = clouds.ResolutionScale;
                if (ImGui.SliderFloat("Render resolution##cloudResolution", ref resolutionScale, 0.25f, 1.0f, "%.2fx"))
                {
                    clouds.ResolutionScale = Math.Clamp(resolutionScale, 0.25f, 1.0f);
                    cloudSettingsChanged = true;
                }
                Undo.TrackItem(_frameBeginState);

                float temporalBlend = clouds.TemporalBlend;
                if (ImGui.SliderFloat("Temporal reuse##cloudTemporal", ref temporalBlend, 0.0f, 0.98f, "%.2f"))
                {
                    clouds.TemporalBlend = Math.Clamp(temporalBlend, 0.0f, 0.98f);
                    cloudSettingsChanged = true;
                }
                Undo.TrackItem(_frameBeginState);

                float anisotropy = clouds.Anisotropy;
                if (ImGui.SliderFloat("Forward scattering##cloudAnisotropy", ref anisotropy, -0.8f, 0.9f, "%.2f"))
                {
                    clouds.Anisotropy = anisotropy;
                    cloudSettingsChanged = true;
                }
                Undo.TrackItem(_frameBeginState);

                float absorption = clouds.Absorption;
                if (ImGui.SliderFloat("Light absorption##cloudAbsorption", ref absorption, 0.05f, 4.0f, "%.2f"))
                {
                    clouds.Absorption = absorption;
                    cloudSettingsChanged = true;
                }
                Undo.TrackItem(_frameBeginState);

                float ambientStrength = clouds.AmbientStrength;
                if (ImGui.SliderFloat("Ambient light##cloudAmbient", ref ambientStrength, 0.0f, 2.0f, "%.2f"))
                {
                    clouds.AmbientStrength = ambientStrength;
                    cloudSettingsChanged = true;
                }
                Undo.TrackItem(_frameBeginState);

                float powderEffect = clouds.PowderEffect;
                if (ImGui.SliderFloat("Powder effect##cloudPowderEffect", ref powderEffect, 0.0f, 2.0f, "%.2f"))
                {
                    clouds.PowderEffect = powderEffect;
                    cloudSettingsChanged = true;
                }
                Undo.TrackItem(_frameBeginState);

                float multiScattering = clouds.MultiScattering;
                if (ImGui.SliderFloat("Multi-scattering##cloudMultiScattering", ref multiScattering, 0.0f, 1.0f, "%.2f"))
                {
                    clouds.MultiScattering = multiScattering;
                    cloudSettingsChanged = true;
                }
                Undo.TrackItem(_frameBeginState);

                ImGui.SeparatorText("World shadows");
                bool shadowsEnabled = clouds.ShadowsEnabled;
                if (ImGui.Checkbox("Cast cloud shadows##cloudShadows", ref shadowsEnabled))
                {
                    clouds.ShadowsEnabled = shadowsEnabled;
                    cloudSettingsChanged = true;
                }
                Undo.TrackItem(_frameBeginState);

                if (clouds.ShadowsEnabled)
                {
                    float shadowStrength = clouds.ShadowStrength;
                    if (ImGui.SliderFloat("Shadow strength##cloudShadowStrength", ref shadowStrength, 0.0f, 1.0f, "%.2f"))
                    {
                        clouds.ShadowStrength = shadowStrength;
                        cloudSettingsChanged = true;
                    }
                    Undo.TrackItem(_frameBeginState);

                    float shadowExtent = clouds.ShadowExtent;
                    if (ImGui.DragFloat("Shadow area radius##cloudShadowExtent", ref shadowExtent, 10.0f, 50.0f, 20000.0f, "%.0f"))
                    {
                        clouds.ShadowExtent = Math.Clamp(shadowExtent, 50.0f, 20000.0f);
                        cloudSettingsChanged = true;
                    }
                    Undo.TrackItem(_frameBeginState);

                    int[] shadowResolutions = [64, 128, 256, 512, 1024];
                    string[] shadowResolutionLabels = ["64", "128", "256", "512", "1024"];
                    int shadowResolutionIndex = Array.IndexOf(shadowResolutions, clouds.ShadowResolution);
                    if (shadowResolutionIndex < 0) shadowResolutionIndex = 2;
                    if (ImGui.Combo("Shadow resolution##cloudShadowResolution", ref shadowResolutionIndex, shadowResolutionLabels, shadowResolutionLabels.Length))
                    {
                        clouds.ShadowResolution = shadowResolutions[shadowResolutionIndex];
                        cloudSettingsChanged = true;
                    }
                    Undo.TrackItem(_frameBeginState);

                    float shadowInterval = clouds.ShadowUpdateInterval;
                    if (ImGui.SliderFloat("Shadow update interval##cloudShadowInterval", ref shadowInterval, 0.0f, 1.0f, "%.2f s"))
                    {
                        clouds.ShadowUpdateInterval = shadowInterval;
                        cloudSettingsChanged = true;
                    }
                    Undo.TrackItem(_frameBeginState);
                }

                ImGui.TreePop();
            }

            if (cloudSettingsChanged)
            {
                assetService.CloudRenderer?.InvalidateHistory();
                viewport3D.RequestRender();
                _cloudPreviewAnimated = clouds.Enabled || doc.Fog.Enabled;
            }

            ImGui.Separator();
            ImGui.TextUnformatted("Volumetric fog");
            VolumetricFogSettings fog = doc.Fog;
            bool fogSettingsChanged = false;

            bool fogEnabled = fog.Enabled;
            if (ImGui.Checkbox("Enabled##volumetricFogEnabled", ref fogEnabled))
            {
                fog.Enabled = fogEnabled;
                fogSettingsChanged = true;
            }
            Undo.TrackItem(_frameBeginState);
            ImGui.SameLine();
            ImGui.TextDisabled("Rendered in the 3D viewport and game.");

            if (fog.Enabled && ImGui.TreeNodeEx(
                    "Fog settings##volumetricFogSettings",
                    ImGuiTreeNodeFlags.DefaultOpen))
            {
                float fogDensity = fog.Density;
                if (ImGui.SliderFloat("Density##fogDensity", ref fogDensity, 0.0f, 0.20f, "%.4f"))
                {
                    fog.Density = Math.Clamp(fogDensity, 0.0f, 1.0f);
                    fogSettingsChanged = true;
                }
                Undo.TrackItem(_frameBeginState);

                float fogBaseHeight = fog.BaseHeight;
                if (ImGui.DragFloat("Base height##fogBaseHeight", ref fogBaseHeight, 1.0f, -10000.0f, 10000.0f, "%.1f"))
                {
                    fog.BaseHeight = fogBaseHeight;
                    fogSettingsChanged = true;
                }
                Undo.TrackItem(_frameBeginState);

                float heightFalloff = fog.HeightFalloff;
                if (ImGui.DragFloat("Height falloff##fogHeightFalloff", ref heightFalloff, 1.0f, 0.1f, 10000.0f, "%.1f"))
                {
                    fog.HeightFalloff = MathF.Max(0.1f, heightFalloff);
                    fogSettingsChanged = true;
                }
                Undo.TrackItem(_frameBeginState);

                ImGui.SeparatorText("Sky / atmospheric layer");
                float fogSkyDensity = fog.SkyDensity;
                if (ImGui.SliderFloat("Sky density##fogSkyDensity", ref fogSkyDensity, 0.0f, 0.01f, "%.5f"))
                {
                    fog.SkyDensity = Math.Clamp(fogSkyDensity, 0.0f, 1.0f);
                    fogSettingsChanged = true;
                }
                Undo.TrackItem(_frameBeginState);

                float fogSkyHeightFalloff = fog.SkyHeightFalloff;
                if (ImGui.DragFloat("Sky height falloff##fogSkyHeightFalloff", ref fogSkyHeightFalloff, 10.0f, 0.1f, 10000.0f, "%.1f"))
                {
                    fog.SkyHeightFalloff = Math.Clamp(fogSkyHeightFalloff, 0.1f, 10000.0f);
                    fogSettingsChanged = true;
                }
                Undo.TrackItem(_frameBeginState);

                float fogMaxDistance = fog.MaxDistance;
                if (ImGui.DragFloat("Maximum distance##fogMaxDistance", ref fogMaxDistance, 10.0f, 10.0f, 50000.0f, "%.0f"))
                {
                    fog.MaxDistance = Math.Clamp(fogMaxDistance, 10.0f, 50000.0f);
                    fogSettingsChanged = true;
                }
                Undo.TrackItem(_frameBeginState);

                ImGui.SeparatorText("Noise and wind");
                float fogNoiseScale = fog.NoiseScale;
                if (ImGui.DragFloat("Noise scale##fogNoiseScale", ref fogNoiseScale, 0.0001f, 0.00001f, 1.0f, "%.5f"))
                {
                    fog.NoiseScale = Math.Clamp(fogNoiseScale, 0.00001f, 1.0f);
                    fogSettingsChanged = true;
                }
                Undo.TrackItem(_frameBeginState);

                float fogNoiseStrength = fog.NoiseStrength;
                if (ImGui.SliderFloat("Noise strength##fogNoiseStrength", ref fogNoiseStrength, 0.0f, 3.0f, "%.2f"))
                {
                    fog.NoiseStrength = Math.Clamp(fogNoiseStrength, 0.0f, 3.0f);
                    fogSettingsChanged = true;
                }
                Undo.TrackItem(_frameBeginState);

                float fogNoiseContrast = fog.NoiseContrast;
                if (ImGui.SliderFloat("Noise contrast##fogNoiseContrast", ref fogNoiseContrast, 0.25f, 4.0f, "%.2f"))
                {
                    fog.NoiseContrast = Math.Clamp(fogNoiseContrast, 0.25f, 4.0f);
                    fogSettingsChanged = true;
                }
                Undo.TrackItem(_frameBeginState);

                Vector2 fogWindDirection = fog.WindDirection;
                if (ImGui.DragFloat2("Wind direction X/Z##fogWindDirection", ref fogWindDirection, 0.01f, -1.0f, 1.0f, "%.2f"))
                {
                    fog.WindDirection = fogWindDirection.LengthSquared() > 1e-8f
                        ? Vector2.Normalize(fogWindDirection)
                        : Vector2.UnitX;
                    fogSettingsChanged = true;
                }
                Undo.TrackItem(_frameBeginState);

                float fogWindSpeed = fog.WindSpeed;
                if (ImGui.DragFloat("Wind speed##fogWindSpeed", ref fogWindSpeed, 0.1f, -100.0f, 100.0f, "%.1f"))
                {
                    fog.WindSpeed = fogWindSpeed;
                    fogSettingsChanged = true;
                }
                Undo.TrackItem(_frameBeginState);

                ImGui.SeparatorText("Lighting and quality");
                float fogAnisotropy = fog.Anisotropy;
                if (ImGui.SliderFloat("Anisotropy##fogAnisotropy", ref fogAnisotropy, -0.8f, 0.9f, "%.2f"))
                {
                    fog.Anisotropy = fogAnisotropy;
                    fogSettingsChanged = true;
                }
                Undo.TrackItem(_frameBeginState);

                float fogAbsorption = fog.Absorption;
                if (ImGui.SliderFloat("Absorption##fogAbsorption", ref fogAbsorption, 0.01f, 8.0f, "%.2f"))
                {
                    fog.Absorption = Math.Clamp(fogAbsorption, 0.01f, 20.0f);
                    fogSettingsChanged = true;
                }
                Undo.TrackItem(_frameBeginState);

                float fogAmbientStrength = fog.AmbientStrength;
                if (ImGui.SliderFloat("Ambient strength##fogAmbient", ref fogAmbientStrength, 0.0f, 4.0f, "%.2f"))
                {
                    fog.AmbientStrength = fogAmbientStrength;
                    fogSettingsChanged = true;
                }
                Undo.TrackItem(_frameBeginState);

                float fogSunScattering = fog.SunScattering;
                if (ImGui.SliderFloat("Sun scattering##fogSunScattering", ref fogSunScattering, 0.0f, 8.0f, "%.2f"))
                {
                    fog.SunScattering = fogSunScattering;
                    fogSettingsChanged = true;
                }
                Undo.TrackItem(_frameBeginState);

                bool lightShaftsEnabled = fog.LightShaftsEnabled;
                if (ImGui.Checkbox("Volumetric light shafts##fogLightShafts", ref lightShaftsEnabled))
                {
                    fog.LightShaftsEnabled = lightShaftsEnabled;
                    fogSettingsChanged = true;
                }
                Undo.TrackItem(_frameBeginState);

                if (fog.LightShaftsEnabled)
                {
                    float lightShaftStrength = fog.LightShaftStrength;
                    if (ImGui.SliderFloat("Light shaft strength##fogLightShaftStrength", ref lightShaftStrength, 0.0f, 4.0f, "%.2f"))
                    {
                        fog.LightShaftStrength = Math.Clamp(lightShaftStrength, 0.0f, 4.0f);
                        fogSettingsChanged = true;
                    }
                    Undo.TrackItem(_frameBeginState);
                }

                int fogRaySteps = fog.RaySteps;
                if (ImGui.SliderInt("Ray-march steps##fogRaySteps", ref fogRaySteps, 8, 128))
                {
                    fog.RaySteps = Math.Clamp(fogRaySteps, 8, 128);
                    fogSettingsChanged = true;
                }
                Undo.TrackItem(_frameBeginState);

                float fogResolutionScale = fog.ResolutionScale;
                if (ImGui.SliderFloat("Render resolution##fogResolution", ref fogResolutionScale, 0.25f, 1.0f, "%.2fx"))
                {
                    fog.ResolutionScale = Math.Clamp(fogResolutionScale, 0.25f, 1.0f);
                    fogSettingsChanged = true;
                }
                Undo.TrackItem(_frameBeginState);

                float fogTemporalBlend = fog.TemporalBlend;
                if (ImGui.SliderFloat("Temporal reuse##fogTemporal", ref fogTemporalBlend, 0.0f, 0.98f, "%.2f"))
                {
                    fog.TemporalBlend = Math.Clamp(fogTemporalBlend, 0.0f, 0.98f);
                    fogSettingsChanged = true;
                }
                Undo.TrackItem(_frameBeginState);

                ImGui.TreePop();
            }

            if (fogSettingsChanged)
            {
                assetService.FogRenderer?.InvalidateHistory();
                viewport3D.RequestRender();
                _cloudPreviewAnimated = clouds.Enabled || fog.Enabled;
            }

            ImGui.Separator();
            ImGui.TextUnformatted("Ocean");
            OceanSettings ocean = doc.Ocean;
            bool oceanSettingsChanged = false;

            bool oceanEnabled = ocean.Enabled;
            if (ImGui.Checkbox("Enabled##oceanEnabled", ref oceanEnabled))
            {
                ocean.Enabled = oceanEnabled;
                oceanSettingsChanged = true;
            }
            Undo.TrackItem(_frameBeginState);
            ImGui.SameLine();
            ImGui.TextDisabled("Visual ocean with optional water physics.");

            if (ocean.Enabled && ImGui.TreeNodeEx(
                    "Ocean settings##oceanSettings",
                    ImGuiTreeNodeFlags.DefaultOpen))
            {
                float waterLevel = ocean.WaterLevel;
                if (ImGui.DragFloat("Water level##oceanWaterLevel", ref waterLevel, 0.1f, -10000.0f, 10000.0f, "%.2f"))
                {
                    ocean.WaterLevel = waterLevel;
                    oceanSettingsChanged = true;
                }
                Undo.TrackItem(_frameBeginState);

                float oceanSize = ocean.OceanSize;
                if (ImGui.DragFloat("Ocean size##oceanSize", ref oceanSize, 10.0f, 64.0f, 100000.0f, "%.0f"))
                {
                    ocean.OceanSize = MathF.Max(64.0f, oceanSize);
                    oceanSettingsChanged = true;
                }
                Undo.TrackItem(_frameBeginState);

                int gridResolution = ocean.GridResolution;
                if (ImGui.SliderInt("Grid resolution##oceanGridResolution", ref gridResolution, 32, 256))
                {
                    ocean.GridResolution = Math.Clamp(gridResolution, 32, 256);
                    oceanSettingsChanged = true;
            }
            Undo.TrackItem(_frameBeginState);

            ImGui.SeparatorText("Physics");
            bool oceanPhysicsEnabled = ocean.PhysicsEnabled;
            if (ImGui.Checkbox("Enable water physics##oceanPhysicsEnabled", ref oceanPhysicsEnabled))
            {
                ocean.PhysicsEnabled = oceanPhysicsEnabled;
                oceanSettingsChanged = true;
            }
            Undo.TrackItem(_frameBeginState);
            ImGui.SameLine();
            ImGui.TextDisabled("Dynamic bodies float by mass/volume; hold Space to float.");

            if (ocean.PhysicsEnabled)
            {
                float waterDensity = ocean.WaterDensity;
                if (ImGui.DragFloat("Water density##oceanWaterDensity", ref waterDensity, 10.0f, 100.0f, 3000.0f, "%.0f"))
                {
                    ocean.WaterDensity = Math.Clamp(waterDensity, 100.0f, 3000.0f);
                    oceanSettingsChanged = true;
                }
                Undo.TrackItem(_frameBeginState);

                ImGui.TextDisabled("Buoyancy is fixed by density, gravity and displaced volume.");

                float waterLinearDrag = ocean.WaterLinearDrag;
                if (ImGui.SliderFloat("Linear drag Cd##oceanWaterLinearDrag", ref waterLinearDrag, 0.0f, 5.0f, "%.2f"))
                {
                    ocean.WaterLinearDrag = Math.Clamp(waterLinearDrag, 0.0f, 5.0f);
                    oceanSettingsChanged = true;
                }
                Undo.TrackItem(_frameBeginState);

                float waterAngularDrag = ocean.WaterAngularDrag;
                if (ImGui.SliderFloat("Rotational drag Cd##oceanWaterAngularDrag", ref waterAngularDrag, 0.0f, 5.0f, "%.2f"))
                {
                    ocean.WaterAngularDrag = Math.Clamp(waterAngularDrag, 0.0f, 5.0f);
                    oceanSettingsChanged = true;
                }
                Undo.TrackItem(_frameBeginState);

                ImGui.SeparatorText("Player in water");
                float playerGravityScale = ocean.PlayerGravityScale;
                if (ImGui.SliderFloat("Gravity scale##oceanPlayerGravity", ref playerGravityScale, 0.0f, 2.0f, "%.2f"))
                {
                    ocean.PlayerGravityScale = Math.Clamp(playerGravityScale, 0.0f, 2.0f);
                    oceanSettingsChanged = true;
                }
                Undo.TrackItem(_frameBeginState);

                float playerSinkAcceleration = ocean.PlayerSinkAcceleration;
                if (ImGui.SliderFloat("Sink acceleration##oceanPlayerSink", ref playerSinkAcceleration, 0.0f, 50.0f, "%.1f"))
                {
                    ocean.PlayerSinkAcceleration = Math.Clamp(playerSinkAcceleration, 0.0f, 50.0f);
                    oceanSettingsChanged = true;
                }
                Undo.TrackItem(_frameBeginState);

                float playerSwimUpAcceleration = ocean.PlayerSwimUpAcceleration;
                if (ImGui.SliderFloat("Swim acceleration##oceanPlayerSwimAcceleration", ref playerSwimUpAcceleration, 0.0f, 100.0f, "%.1f"))
                {
                    ocean.PlayerSwimUpAcceleration = Math.Clamp(playerSwimUpAcceleration, 0.0f, 100.0f);
                    oceanSettingsChanged = true;
                }
                Undo.TrackItem(_frameBeginState);

                float playerSwimUpSpeed = ocean.PlayerSwimUpSpeed;
                if (ImGui.SliderFloat("Float speed##oceanPlayerSwimSpeed", ref playerSwimUpSpeed, 0.0f, 30.0f, "%.1f"))
                {
                    ocean.PlayerSwimUpSpeed = Math.Clamp(playerSwimUpSpeed, 0.0f, 30.0f);
                    oceanSettingsChanged = true;
                }
                Undo.TrackItem(_frameBeginState);

                float playerWaterMoveSpeed = ocean.PlayerWaterMoveSpeed;
                if (ImGui.SliderFloat("Water move speed##oceanPlayerMoveSpeed", ref playerWaterMoveSpeed, 0.0f, 20.0f, "%.1f"))
                {
                    ocean.PlayerWaterMoveSpeed = Math.Clamp(playerWaterMoveSpeed, 0.0f, 20.0f);
                    oceanSettingsChanged = true;
                }
                Undo.TrackItem(_frameBeginState);

                float playerWaterDrag = ocean.PlayerWaterDrag;
                if (ImGui.SliderFloat("Player water drag##oceanPlayerWaterDrag", ref playerWaterDrag, 0.0f, 30.0f, "%.2f"))
                {
                    ocean.PlayerWaterDrag = Math.Clamp(playerWaterDrag, 0.0f, 30.0f);
                    oceanSettingsChanged = true;
                }
                Undo.TrackItem(_frameBeginState);

                float playerSurfaceFloatStrength = ocean.PlayerSurfaceFloatStrength;
                if (ImGui.SliderFloat("Surface float strength##oceanPlayerFloatStrength", ref playerSurfaceFloatStrength, 0.0f, 20.0f, "%.1f"))
                {
                    ocean.PlayerSurfaceFloatStrength = Math.Clamp(playerSurfaceFloatStrength, 0.0f, 20.0f);
                    oceanSettingsChanged = true;
                }
                Undo.TrackItem(_frameBeginState);
            }

            ImGui.SeparatorText("Waves");
                float waveAmplitude = ocean.WaveAmplitude;
                if (ImGui.DragFloat("Amplitude##oceanWaveAmplitude", ref waveAmplitude, 0.05f, 0.0f, 100.0f, "%.2f"))
                {
                    ocean.WaveAmplitude = MathF.Max(0.0f, waveAmplitude);
                    oceanSettingsChanged = true;
                }
                Undo.TrackItem(_frameBeginState);

                float waveLength = ocean.WaveLength;
                if (ImGui.DragFloat("Wavelength##oceanWaveLength", ref waveLength, 0.5f, 0.5f, 10000.0f, "%.1f"))
                {
                    ocean.WaveLength = MathF.Max(0.5f, waveLength);
                    oceanSettingsChanged = true;
                }
                Undo.TrackItem(_frameBeginState);

                float waveSpeed = ocean.WaveSpeed;
                if (ImGui.DragFloat("Speed##oceanWaveSpeed", ref waveSpeed, 0.05f, -100.0f, 100.0f, "%.2f"))
                {
                    ocean.WaveSpeed = waveSpeed;
                    oceanSettingsChanged = true;
                }
                Undo.TrackItem(_frameBeginState);

                float waveChoppiness = ocean.WaveChoppiness;
                if (ImGui.SliderFloat("Choppiness##oceanWaveChoppiness", ref waveChoppiness, 0.0f, 2.0f, "%.2f"))
                {
                    ocean.WaveChoppiness = Math.Clamp(waveChoppiness, 0.0f, 2.0f);
                    oceanSettingsChanged = true;
                }
                Undo.TrackItem(_frameBeginState);

                Vector2 waveDirection = ocean.WaveDirection;
                if (ImGui.DragFloat2("Direction X/Z##oceanWaveDirection", ref waveDirection, 0.01f, -1.0f, 1.0f, "%.2f"))
                {
                    ocean.WaveDirection = waveDirection.LengthSquared() > 1e-8f
                        ? Vector2.Normalize(waveDirection)
                        : Vector2.UnitX;
                    oceanSettingsChanged = true;
                }
                Undo.TrackItem(_frameBeginState);

                float windSpeed = ocean.WindSpeed;
                if (ImGui.DragFloat("Wind speed##oceanWindSpeed", ref windSpeed, 0.1f, 0.1f, 200.0f, "%.1f"))
                {
                    ocean.WindSpeed = Math.Clamp(windSpeed, 0.1f, 200.0f);
                    oceanSettingsChanged = true;
                }
                Undo.TrackItem(_frameBeginState);

                float smallWaveLength = ocean.SmallWaveLength;
                if (ImGui.DragFloat("Small wave length##oceanSmallWaveLength", ref smallWaveLength, 0.01f, 0.05f, 20.0f, "%.2f"))
                {
                    ocean.SmallWaveLength = Math.Clamp(smallWaveLength, 0.05f, 20.0f);
                    oceanSettingsChanged = true;
                }
                Undo.TrackItem(_frameBeginState);

                int spectrumSeed = ocean.SpectrumSeed;
                if (ImGui.InputInt("Spectrum seed##oceanSpectrumSeed", ref spectrumSeed))
                {
                    ocean.SpectrumSeed = spectrumSeed;
                    oceanSettingsChanged = true;
                }
                Undo.TrackItem(_frameBeginState);

                string[] oceanDebugLabels = ["Final", "Height", "Slope", "Displacement"];
                int oceanDebugView = Math.Clamp(ocean.DebugView, 0, oceanDebugLabels.Length - 1);
                if (ImGui.Combo(
                        "Debug view##oceanDebugView",
                        ref oceanDebugView,
                        oceanDebugLabels,
                        oceanDebugLabels.Length))
                {
                    ocean.DebugView = oceanDebugView;
                    oceanSettingsChanged = true;
                }
                Undo.TrackItem(_frameBeginState);

                ImGui.SeparatorText("Surface");
                ImGui.TextDisabled("Normal detail: Textures/ocean_normal.png");
                bool normalMapEnabled = ocean.NormalMapEnabled;
                if (ImGui.Checkbox("Normal map##oceanNormalMapEnabled", ref normalMapEnabled))
                {
                    ocean.NormalMapEnabled = normalMapEnabled;
                    oceanSettingsChanged = true;
                }
                Undo.TrackItem(_frameBeginState);

                if (ocean.NormalMapEnabled)
                {
                    float normalMapStrength = ocean.NormalMapStrength;
                    if (ImGui.SliderFloat(
                            "Normal strength##oceanNormalMapStrength",
                            ref normalMapStrength,
                            0.0f,
                            1.0f,
                            "%.2f"))
                    {
                        ocean.NormalMapStrength = Math.Clamp(normalMapStrength, 0.0f, 1.0f);
                        oceanSettingsChanged = true;
                    }
                    Undo.TrackItem(_frameBeginState);

                    float normalMapScale = ocean.NormalMapScale;
                    if (ImGui.DragFloat(
                            "Normal scale##oceanNormalMapScale",
                            ref normalMapScale,
                            0.001f,
                            0.001f,
                            0.25f,
                            "%.3f"))
                    {
                        ocean.NormalMapScale = Math.Clamp(normalMapScale, 0.001f, 0.25f);
                        oceanSettingsChanged = true;
                    }
                    Undo.TrackItem(_frameBeginState);

                    float normalMapDistortion = ocean.NormalMapDistortion;
                    if (ImGui.SliderFloat(
                            "Normal distortion##oceanNormalMapDistortion",
                            ref normalMapDistortion,
                            0.0f,
                            2.0f,
                            "%.2f"))
                    {
                        ocean.NormalMapDistortion = Math.Clamp(normalMapDistortion, 0.0f, 2.0f);
                        oceanSettingsChanged = true;
                    }
                    Undo.TrackItem(_frameBeginState);
                }

                Vector3 shallowColor = ocean.ShallowColor;
                if (ImGui.ColorEdit3("Shallow color##oceanShallowColor", ref shallowColor, ImGuiColorEditFlags.Float))
                {
                    ocean.ShallowColor = shallowColor;
                    oceanSettingsChanged = true;
                }
                Undo.TrackItem(_frameBeginState);

                Vector3 deepColor = ocean.DeepColor;
                if (ImGui.ColorEdit3("Deep color##oceanDeepColor", ref deepColor, ImGuiColorEditFlags.Float))
                {
                    ocean.DeepColor = deepColor;
                    oceanSettingsChanged = true;
                }
                Undo.TrackItem(_frameBeginState);

                Vector3 foamColor = ocean.FoamColor;
                if (ImGui.ColorEdit3("Foam color##oceanFoamColor", ref foamColor, ImGuiColorEditFlags.Float))
                {
                    ocean.FoamColor = foamColor;
                    oceanSettingsChanged = true;
                }
                Undo.TrackItem(_frameBeginState);

                float reflectionStrength = ocean.ReflectionStrength;
                if (ImGui.SliderFloat("Reflection##oceanReflection", ref reflectionStrength, 0.0f, 2.0f, "%.2f"))
                {
                    ocean.ReflectionStrength = Math.Clamp(reflectionStrength, 0.0f, 2.0f);
                    oceanSettingsChanged = true;
                }
                Undo.TrackItem(_frameBeginState);

                float refractionStrength = ocean.RefractionStrength;
                if (ImGui.SliderFloat("Refraction##oceanRefraction", ref refractionStrength, 0.0f, 0.25f, "%.3f"))
                {
                    ocean.RefractionStrength = Math.Clamp(refractionStrength, 0.0f, 0.25f);
                    oceanSettingsChanged = true;
                }
                Undo.TrackItem(_frameBeginState);

                float absorptionDistance = ocean.AbsorptionDistance;
                if (ImGui.DragFloat("Absorption distance##oceanAbsorption", ref absorptionDistance, 1.0f, 0.1f, 10000.0f, "%.1f"))
                {
                    ocean.AbsorptionDistance = MathF.Max(0.1f, absorptionDistance);
                    oceanSettingsChanged = true;
                }
                Undo.TrackItem(_frameBeginState);

                float surfaceRoughness = ocean.SurfaceRoughness;
                if (ImGui.SliderFloat("Roughness##oceanRoughness", ref surfaceRoughness, 0.02f, 1.0f, "%.2f"))
                {
                    ocean.SurfaceRoughness = Math.Clamp(surfaceRoughness, 0.02f, 1.0f);
                    oceanSettingsChanged = true;
                }
                Undo.TrackItem(_frameBeginState);

                float foamStrength = ocean.FoamStrength;
                if (ImGui.SliderFloat("Foam strength##oceanFoamStrength", ref foamStrength, 0.0f, 2.0f, "%.2f"))
                {
                    ocean.FoamStrength = Math.Clamp(foamStrength, 0.0f, 2.0f);
                    oceanSettingsChanged = true;
                }
                Undo.TrackItem(_frameBeginState);

                float foamDepth = ocean.FoamDepth;
                if (ImGui.DragFloat("Foam depth##oceanFoamDepth", ref foamDepth, 0.1f, 0.0f, 100.0f, "%.2f"))
                {
                    ocean.FoamDepth = MathF.Max(0.0f, foamDepth);
                    oceanSettingsChanged = true;
                }
                Undo.TrackItem(_frameBeginState);

                ImGui.SeparatorText("Underwater");
                bool underwaterEnabled = ocean.UnderwaterEnabled;
                if (ImGui.Checkbox("Enable underwater##oceanUnderwaterEnabled", ref underwaterEnabled))
                {
                    ocean.UnderwaterEnabled = underwaterEnabled;
                    oceanSettingsChanged = true;
                }
                Undo.TrackItem(_frameBeginState);

                if (ocean.UnderwaterEnabled)
                {
                    Vector3 underwaterColor = ocean.UnderwaterColor;
                    if (ImGui.ColorEdit3("Color##oceanUnderwaterColor", ref underwaterColor, ImGuiColorEditFlags.Float))
                    {
                        ocean.UnderwaterColor = underwaterColor;
                        oceanSettingsChanged = true;
                    }
                    Undo.TrackItem(_frameBeginState);

                    float underwaterFog = ocean.UnderwaterFogDensity;
                    if (ImGui.SliderFloat("Fog density##oceanUnderwaterFog", ref underwaterFog, 0.0f, 0.5f, "%.3f"))
                    {
                        ocean.UnderwaterFogDensity = MathF.Max(0.0f, underwaterFog);
                        oceanSettingsChanged = true;
                    }
                    Undo.TrackItem(_frameBeginState);

                    float underwaterDistortion = ocean.UnderwaterDistortion;
                    if (ImGui.SliderFloat("Distortion##oceanUnderwaterDistortion", ref underwaterDistortion, 0.0f, 0.1f, "%.3f"))
                    {
                        ocean.UnderwaterDistortion = Math.Clamp(underwaterDistortion, 0.0f, 0.1f);
                        oceanSettingsChanged = true;
                    }
                    Undo.TrackItem(_frameBeginState);

                    float underwaterDarkening = ocean.UnderwaterDarkening;
                    if (ImGui.SliderFloat("Darkening##oceanUnderwaterDarkening", ref underwaterDarkening, 0.0f, 1.0f, "%.2f"))
                    {
                        ocean.UnderwaterDarkening = Math.Clamp(underwaterDarkening, 0.0f, 1.0f);
                        oceanSettingsChanged = true;
                    }
                    Undo.TrackItem(_frameBeginState);
                }

                ImGui.TreePop();
            }

            if (oceanSettingsChanged)
                viewport3D.RequestRender();
        }

        if (doc.PlayerSpawn != null && ImGui.CollapsingHeader("Player Spawn", ImGuiTreeNodeFlags.None))
        {
            var sp = doc.PlayerSpawn;
            
            Vector3 spPos = sp.Position;
            bool posChanged = ImGui.DragFloat3("Spawn Pos##spPos", ref spPos, 0.05f);
            Undo.TrackItem(_frameBeginState);
            if (posChanged)
            {
                spPos = ApplySnap(spPos, _snapGrid);
                sp.Position = spPos;
            }
            
            
            float yaw = sp.Yaw;
            bool yawChanged = ImGui.DragFloat("Spawn Yaw##spYaw", ref yaw, 0.5f);
            Undo.TrackItem(_frameBeginState);
            if (yawChanged)
            {
                yaw = ApplySnap(yaw, _snapAngle);
                sp.Yaw = yaw;
            }
            

            float pitch = sp.Pitch;
            bool pitchChanged = ImGui.DragFloat("Spawn Pitch##spPitch", ref pitch, 0.5f);
            Undo.TrackItem(_frameBeginState);
            if (pitchChanged)
            {
                pitch = ApplySnap(pitch, _snapAngle);
                sp.Pitch = pitch;
            }
            
        }

        MapObject? objectToDelete = null;
        MapObject? objectToDuplicate = null;

        // --- Scene Hierarchy Outliner ---
        ImGui.Spacing();
        ImGui.TextColored(new Vector4(0.82f, 0.87f, 0.94f, 1.0f), "Scene Hierarchy");
        ImGui.SameLine();
        ImGui.TextDisabled($"{doc.Objects.Count} objects  |  {_selectedObjects.Count} selected");
        ImGui.Separator();
        ImGui.SetNextItemWidth(-34);
        ImGui.InputTextWithHint("##HierarchyFilter", "Search objects, lights, models...", ref _hierarchyFilter, 128);
        ImGui.SameLine();
        if (ImGui.Button("X##ClearHierarchyFilter", new Vector2(26, 0)))
            _hierarchyFilter = "";

        float listHeight = ImGui.GetContentRegionAvail().Y * 0.5f - 10;
        if (listHeight < 150f) listHeight = 150f; // Ensure minimum height

        ImGui.BeginChild("HierarchyTree", new Vector2(0, listHeight), ImGuiChildFlags.Borders, ImGuiWindowFlags.HorizontalScrollbar);
        
        var rootObjects = doc.Objects.Where(o => string.IsNullOrEmpty(o.ParentId)).ToList();
        DrawHierarchyCategory("groups", "Groups", rootObjects.Where(o => GetHierarchyIconKind(o) == HierarchyIconKind.Group), doc,
            sceneService, assetService, history, viewport3D, viewportTop, viewportFront, viewportSide, _hierarchyFilter,
            ref objectToDelete, ref objectToDuplicate);
        DrawHierarchyCategory("geometry", "Geometry", rootObjects.Where(o => GetHierarchyIconKind(o) is HierarchyIconKind.Brush or HierarchyIconKind.Terrain or HierarchyIconKind.Box or HierarchyIconKind.Sphere or HierarchyIconKind.Capsule), doc,
            sceneService, assetService, history, viewport3D, viewportTop, viewportFront, viewportSide, _hierarchyFilter,
            ref objectToDelete, ref objectToDuplicate);
        DrawHierarchyCategory("models", "Models", rootObjects.Where(o => GetHierarchyIconKind(o) == HierarchyIconKind.Model), doc,
            sceneService, assetService, history, viewport3D, viewportTop, viewportFront, viewportSide, _hierarchyFilter,
            ref objectToDelete, ref objectToDuplicate);
        DrawHierarchyCategory("lights", "Lights", rootObjects.Where(o => o.IsLight), doc,
            sceneService, assetService, history, viewport3D, viewportTop, viewportFront, viewportSide, _hierarchyFilter,
            ref objectToDelete, ref objectToDuplicate);
        DrawHierarchyCategory("other", "Other", rootObjects.Where(o => GetHierarchyIconKind(o) == HierarchyIconKind.Other), doc,
            sceneService, assetService, history, viewport3D, viewportTop, viewportFront, viewportSide, _hierarchyFilter,
            ref objectToDelete, ref objectToDuplicate);
        
        if (ImGui.BeginPopupContextWindow("hierarchy_tree_context", ImGuiPopupFlags.MouseButtonRight | ImGuiPopupFlags.NoOpenOverItems))
        {
            if (_selectedObjects.Count > 0)
            {
                if (ImGui.MenuItem("Group Selected"))
                {
                    GroupSelected(sceneService, assetService, history);
                }
                if (_selectedObjects.Any(o => !string.IsNullOrEmpty(o.ParentId)))
                {
                    if (ImGui.MenuItem("Ungroup Selected"))
                    {
                        UngroupSelected(sceneService, assetService, history);
                    }
                }
            }
            ImGui.EndPopup();
        }

        // Empty space drop target to unparent / make root
        ImGui.Dummy(new Vector2(ImGui.GetContentRegionAvail().X, 50f));
        if (ImGui.BeginDragDropTarget())
        {
            var payload = ImGui.AcceptDragDropPayload("HIERARCHY_NODE");
            if (payload.NativePtr != null)
            {
                List<MapObject> draggedObjects = _draggedObjects.Count > 0
                    ? _draggedObjects
                    : _draggedObject != null ? [_draggedObject] : [];
                List<MapObject> objectsToUnparent = draggedObjects
                    .Where(draggedObject => !string.IsNullOrEmpty(draggedObject.ParentId))
                    .ToList();

                if (objectsToUnparent.Count > 0)
                {
                    var pre = doc.Serialize();
                    foreach (MapObject draggedObject in objectsToUnparent)
                    {
                        draggedObject.ParentId = null;
                        UpdateEntityParent(scene, draggedObject, null);
                    }
                    var post = doc.Serialize();
                    sceneService.MarkModified(post);
                    history.PushCommand(new SnapshotCommand(sceneService, assetService, pre, post));
                    sceneService.PopulateScene(assetService);
                }
                _draggedObject = null;
                _draggedObjects.Clear();
            }
            ImGui.EndDragDropTarget();
        }

        ImGui.EndChild();

        // --- Inspector / Properties Window ---
        ImGui.Spacing();
        ImGui.TextColored(new Vector4(0.82f, 0.87f, 0.94f, 1.0f), "Inspector");
        ImGui.SameLine();
        ImGui.TextDisabled(_selectedObjects.Count == 0 ? "No selection" : "Properties");
        ImGui.Separator();

        ImGui.BeginChild("InspectorSection", new Vector2(0, 0), ImGuiChildFlags.Borders);
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(8.0f, 6.0f));
        ImGui.PushStyleColor(ImGuiCol.Header, new Vector4(0.10f, 0.15f, 0.22f, 1.0f));
        ImGui.PushStyleColor(ImGuiCol.HeaderHovered, new Vector4(0.15f, 0.23f, 0.33f, 1.0f));
        ImGui.PushStyleColor(ImGuiCol.HeaderActive, new Vector4(0.18f, 0.29f, 0.40f, 1.0f));
        if (_selectedObjects.Count == 0)
        {
            ImGui.Spacing();
            ImGui.TextDisabled("Nothing selected");
            ImGui.TextWrapped("Select an object in the hierarchy or a viewport to inspect and edit its properties.");
            ImGui.Spacing();
            ImGui.TextDisabled("Tip: Ctrl-click adds objects to the selection.");
        }
        else if (_selectedObjects.Count > 1)
        {
            DrawInspectorMultiSelection(
                _selectedObjects.ToList(),
                doc,
                scene,
                sceneService,
                assetService,
                history);
        }
        else
        {
            var obj = _selectedObject;
            if (obj != null)
            {
                DrawInspectorObjectHeader(obj);

                if (ImGui.CollapsingHeader("Object", ImGuiTreeNodeFlags.DefaultOpen))
                {
                // ID
                string id = obj.Id;
                bool idChanged = ImGui.InputText("ID##inspectId", ref id, 64);
                Undo.TrackItem(_frameBeginState);
                if (idChanged)
                {
                    var entity = scene.Entities.FirstOrDefault(e => e.Id == obj.Id);
                    if (entity != null) entity.Id = id;
                    obj.Id = id;
                }
                if (ImGui.IsItemDeactivatedAfterEdit())
                {
                    string uniqueId = SceneNameManager.GetUniqueName(sceneService.Document, obj, obj.Id);
                    if (uniqueId != obj.Id)
                    {
                        var entity = scene.Entities.FirstOrDefault(e => e.Id == obj.Id);
                        if (entity != null) entity.Id = uniqueId;
                        obj.Id = uniqueId;
                    }
                }
                

                // Visible
                bool visible = obj.Visible;
                bool visChanged = ImGui.Checkbox("Visible##inspectVis", ref visible);
                if (visChanged) Undo.RecordState(_frameBeginState);
                if (visChanged)
                {
                    obj.Visible = visible;
                    var entity = scene.Entities.FirstOrDefault(e => e.Id == obj.Id);
                    if (entity != null) entity.Visible = visible;
                    UpdateEntitiesVisibilityRecursive(doc, scene, obj);
                    SyncLight(sceneService, obj);
                    Undo.ForceEnd(history, sceneService, assetService);
                }

                // Parent Selection
                string currentParentText = string.IsNullOrEmpty(obj.ParentId) ? "(None)" : obj.ParentId;
                if (ImGui.BeginCombo("Parent##inspectParent", currentParentText))
                {
                    if (ImGui.Selectable("(None)##parent_none", string.IsNullOrEmpty(obj.ParentId)))
                    {
                        Undo.TrackItem(_frameBeginState);
                        obj.ParentId = null;
                        var entity = scene.Entities.FirstOrDefault(e => e.Id == obj.Id);
                        if (entity != null)
                        {
                            entity.ParentId = "";
                        }
                        
                    }

                    foreach (var potentialParent in doc.Objects)
                    {
                        if (potentialParent.Id == obj.Id) continue;
                        if (IsDescendantOf(potentialParent, obj, doc)) continue;

                        bool isSelectedParent = obj.ParentId == potentialParent.Id;
                        if (ImGui.Selectable($"{potentialParent.Id}##parent_{potentialParent.Id}", isSelectedParent))
                        {
                            Undo.TrackItem(_frameBeginState);
                            obj.ParentId = potentialParent.Id;
                            
                            var entity = scene.Entities.FirstOrDefault(e => e.Id == obj.Id);
                            var parentEntity = scene.Entities.FirstOrDefault(e => e.Id == potentialParent.Id);
                            if (entity != null)
                            {
                                entity.ParentId = potentialParent.Id;
                                if (parentEntity != null)
                                {
                                    entity.InitialRelativePosition = entity.Transform.Position - parentEntity.Transform.Position;
                                    entity.InitialRelativeRotation = Quaternion.Inverse(parentEntity.Transform.Rotation) * entity.Transform.Rotation;
                                }
                            }
                            
                        }
                    }
                    ImGui.EndCombo();
                }

                }

                // Transform
                if (ImGui.CollapsingHeader("Transform", ImGuiTreeNodeFlags.DefaultOpen))
                {
                    bool isChild = !string.IsNullOrEmpty(obj.ParentId);
                    var entity = scene.Entities.FirstOrDefault(e => e.Id == obj.Id);
                    var parentEntity = isChild ? scene.Entities.FirstOrDefault(e => e.Id == obj.ParentId) : null;

                    Vector3 pos = entity != null ? entity.Transform.Position : (obj.Body != null ? obj.Body.Position : Vector3.Zero);
                    Quaternion rot = entity != null ? entity.Transform.Rotation : (obj.Body != null ? obj.Body.Rotation : Quaternion.Identity);
                    
                    Vector3 currentScale = Vector3.One;
                    if (!obj.IsModel && obj.Body != null && (obj.Body.Shape == MapShapeType.Box || obj.Body.Shape == MapShapeType.Trimesh) && obj.Body.HalfExtents.HasValue) currentScale = obj.Body.HalfExtents.Value * 2.0f;
                    else if (!obj.IsModel && obj.Body != null && obj.Body.Shape == MapShapeType.Sphere && obj.Body.Radius.HasValue) currentScale = new Vector3(obj.Body.Radius.Value * 2.0f);
                    else if (obj.IsModel) currentScale = obj.ModelScale;

                    if (isChild && parentEntity != null)
                    {
                        ImGui.TextColored(new Vector4(0.8f, 0.8f, 0.3f, 1.0f), "Local Space (Parented)");
                        pos = Vector3.Transform(pos - parentEntity.Transform.Position, Quaternion.Inverse(parentEntity.Transform.Rotation));
                        rot = Quaternion.Inverse(parentEntity.Transform.Rotation) * rot;
                        currentScale = currentScale / parentEntity.Transform.Scale;
                    }
                    else
                    {
                        ImGui.TextColored(new Vector4(0.3f, 0.8f, 0.3f, 1.0f), "Global Space");
                    }

                    Vector3 euler = QuaternionToEuler(rot);

                    bool tChanged = ImGui.DragFloat3("Location##inspectPos", ref pos, 0.1f);
                    Undo.TrackItem(_frameBeginState);

                    bool rChanged = ImGui.DragFloat3("Rotation##inspectRot", ref euler, 0.5f, -360f, 360f, "%.1f deg");
                    Undo.TrackItem(_frameBeginState);

                    bool sChanged = false;
                    if (obj.IsTerrain)
                    {
                        ImGui.TextDisabled("Scale is defined by the terrain asset.");
                    }
                    else
                    {
                        sChanged = ImGui.DragFloat3("Scale##inspectScale", ref currentScale, 0.05f);
                        Undo.TrackItem(_frameBeginState);
                    }

                    if (tChanged || rChanged || sChanged)
                    {
                        if (isChild && parentEntity != null)
                        {
                            if (tChanged) pos = parentEntity.Transform.Position + Vector3.Transform(pos, parentEntity.Transform.Rotation);
                            if (rChanged) rot = parentEntity.Transform.Rotation * EulerToQuaternion(euler);
                            if (sChanged) currentScale = currentScale * parentEntity.Transform.Scale;
                        }
                        else
                        {
                            if (rChanged) rot = EulerToQuaternion(euler);
                        }

                        if (entity != null)
                        {
                            if (tChanged) entity.Transform.Position = pos;
                            if (rChanged) entity.Transform.Rotation = rot;
                        }
                        if (obj.Body != null)
                        {
                            if (tChanged) obj.Body.Position = pos;
                            if (rChanged) obj.Body.Rotation = rot;
                        }

                        if (sChanged)
                        {
                            if (obj.IsModel) obj.ModelScale = currentScale;
                            else if (obj.Body != null && (obj.Body.Shape == MapShapeType.Box || obj.Body.Shape == MapShapeType.Trimesh) && obj.Body.HalfExtents.HasValue)
                            {
                                Vector3 oldExtents = obj.Body.HalfExtents.Value;
                                obj.Body.HalfExtents = Vector3.Max(new Vector3(0.05f), currentScale / 2.0f);
                                if (obj is Fuse.Scene.Model.Brush b) b.ScalePlanes(obj.Body.HalfExtents.Value / oldExtents);
                            }
                            else if (obj.Body != null && obj.Body.Shape == MapShapeType.Sphere && obj.Body.Radius.HasValue)
                            {
                                obj.Body.Radius = MathF.Max(0.05f, currentScale.X / 2.0f);
                            }
                        }

                        if (entity != null)
                        {
                            if (!obj.IsModel && obj.Body != null && (obj.Body.Shape == MapShapeType.Box || obj.Body.Shape == MapShapeType.Trimesh) && obj.Body.HalfExtents.HasValue)
                                entity.Transform.Scale = obj.Body.HalfExtents.Value * 2.0f;
                            else if (!obj.IsModel && obj.Body != null && obj.Body.Shape == MapShapeType.Sphere && obj.Body.Radius.HasValue)
                                entity.Transform.Scale = new Vector3(obj.Body.Radius.Value * 2.0f);
                            else
                                entity.Transform.Scale = currentScale;
                        }

                        if (sChanged && !obj.IsModel && !obj.IsTerrain)
                        {
                            assetService.InvalidateMesh(obj.Id);
                            if (entity != null) entity.Mesh = assetService.GetOrCreateMesh(obj);
                        }

                        if (obj.IsTerrain && (tChanged || rChanged))
                            sceneService.PopulateScene(assetService);
                    }


                }

                // Visuals & Material
                if (obj.IsTerrain && ImGui.CollapsingHeader("Terrain", ImGuiTreeNodeFlags.DefaultOpen))
                {
                    TerrainTileSetAsset? terrainSet = sceneService.TryLoadTerrainTileSet(obj, assetService);
                    TerrainAsset? terrain = terrainSet?.Primary.Asset;
                    if (terrainSet == null || terrain == null)
                    {
                        ImGui.TextColored(new Vector4(1.0f, 0.35f, 0.25f, 1.0f), "Terrain asset could not be loaded.");
                    }
                    else
                    {
                        terrainSet.GetBounds(out Vector3 terrainMin, out Vector3 terrainMax);
                        ImGui.TextUnformatted($"Asset: {obj.TerrainAssetPath}");
                        ImGui.TextUnformatted($"Resolution: {terrain.Width} x {terrain.Depth}");
                        ImGui.TextUnformatted($"Size: {(terrain.Width - 1) * terrain.CellSize:0.##} x {(terrain.Depth - 1) * terrain.CellSize:0.##}");
                        ImGui.TextUnformatted($"Height: {terrainMin.Y:0.##} .. {terrainMax.Y:0.##}");
                        if (terrainSet.Procedural != null)
                        {
                            ImGui.TextUnformatted($"Preview tiles: {terrainSet.Tiles.Count}");
                            DrawProceduralTerrainInspector(terrainSet.Procedural);
                        }
                        else
                        {
                            ImGui.TextUnformatted($"Neighbor tiles: {terrainSet.Tiles.Count}");
                            ImGui.TextDisabled(
                                "Existing: " + string.Join(
                                    ", ",
                                    terrainSet.Tiles.Select(tile => $"({tile.X}, {tile.Z})")));

                            ImGui.SeparatorText("Neighbor terrains");
                            if (ImGui.Checkbox(
                                    "Edit neighbors in 3D viewport##terrainNeighborEditMode",
                                    ref _terrainNeighborEditMode))
                                viewport3D.RequestRender();
                            if (ImGui.IsItemHovered())
                                ImGui.SetTooltip(
                                    "Empty cells are clickable to create a neighbor. Click an existing neighbor to delete it.");
                            ImGui.TextDisabled("Blue cells create; green cells delete. The origin cannot be deleted.");
                            if (!string.IsNullOrWhiteSpace(_terrainNeighborStatus))
                                ImGui.TextDisabled(_terrainNeighborStatus);
                            ImGui.TextDisabled("Create connected tiles in the same .terrain asset.");
                            ImGui.SetNextItemWidth(90.0f);
                            ImGui.InputInt("Source X##terrainNeighborSourceX", ref _terrainNeighborSourceX);
                            ImGui.SameLine();
                            ImGui.SetNextItemWidth(90.0f);
                            ImGui.InputInt("Source Z##terrainNeighborSourceZ", ref _terrainNeighborSourceZ);

                            bool neighborCreated = false;
                            if (ImGui.Button("-X##terrainNeighborMinusX"))
                                neighborCreated = sceneService.CreateTerrainNeighbor(obj, assetService, _terrainNeighborSourceX, _terrainNeighborSourceZ, -1, 0);
                            ImGui.SameLine();
                            if (ImGui.Button("+X##terrainNeighborPlusX"))
                                neighborCreated = sceneService.CreateTerrainNeighbor(obj, assetService, _terrainNeighborSourceX, _terrainNeighborSourceZ, 1, 0);
                            ImGui.SameLine();
                            if (ImGui.Button("-Z##terrainNeighborMinusZ"))
                                neighborCreated = sceneService.CreateTerrainNeighbor(obj, assetService, _terrainNeighborSourceX, _terrainNeighborSourceZ, 0, -1);
                            ImGui.SameLine();
                            if (ImGui.Button("+Z##terrainNeighborPlusZ"))
                                neighborCreated = sceneService.CreateTerrainNeighbor(obj, assetService, _terrainNeighborSourceX, _terrainNeighborSourceZ, 0, 1);

                            if (neighborCreated)
                            {
                                sceneService.PopulateScene(assetService);
                                sceneService.MarkModified(sceneService.Document.Serialize());
                                _terrainNeighborStatus = "Created neighbor from the manual coordinates.";
                                viewport3D.RequestRender();
                                viewportTop.RequestRender();
                                viewportFront.RequestRender();
                                viewportSide.RequestRender();
                            }
                            if (ImGui.IsItemHovered())
                                ImGui.SetTooltip("The source tile must exist and the target grid slot must be empty.");
                        }

                        int terrainChunkQuads = obj.TerrainChunkQuads;
                        if (ImGui.DragInt("Chunk quads##terrainChunkQuads", ref terrainChunkQuads, 1.0f, 1, 256))
                        {
                            obj.TerrainChunkQuads = Math.Clamp(terrainChunkQuads, 1, 256);
                            sceneService.PopulateScene(assetService);
                        }
                        Undo.TrackItem(_frameBeginState);

                        float terrainPixelError = obj.TerrainPixelError;
                        if (ImGui.DragFloat("Pixel error##terrainPixelError", ref terrainPixelError, 0.1f, 0.1f, 100.0f, "%.2f px"))
                        {
                            obj.TerrainPixelError = MathF.Max(0.1f, terrainPixelError);
                            sceneService.PopulateScene(assetService);
                        }
                        if (ImGui.IsItemHovered())
                            ImGui.SetTooltip("Maximum projected height error allowed before a lower terrain LOD is selected.");
                        Undo.TrackItem(_frameBeginState);

                        bool forceTerrainLod0 = sceneService.IsTerrainEditorForceLod0(obj.Id);
                        if (ImGui.Checkbox(
                                "Force LOD 0 in Blowtorch##terrainForceLod0",
                                ref forceTerrainLod0))
                        {
                            sceneService.SetTerrainEditorForceLod0(
                                obj.Id,
                                forceTerrainLod0);
                            sceneService.PopulateScene(assetService);
                            viewport3D.RequestRender();
                            viewportTop.RequestRender();
                            viewportFront.RequestRender();
                            viewportSide.RequestRender();
                        }
                        if (ImGui.IsItemHovered())
                            ImGui.SetTooltip(
                                "Editor-only preview override. Keeps every chunk of this terrain at render LOD 0, regardless of camera distance. It is not saved to the map and does not affect Fuse runtime.");

                        int terrainCollisionLod = obj.TerrainCollisionLod;
                        if (ImGui.DragInt(
                                "Collision LOD##terrainCollisionLod",
                                ref terrainCollisionLod,
                                1.0f,
                                0,
                                TerrainMeshGenerator.MaxLodLevels - 1))
                        {
                            obj.TerrainCollisionLod = Math.Clamp(
                                terrainCollisionLod,
                                0,
                                TerrainMeshGenerator.MaxLodLevels - 1);
                            sceneService.PopulateScene(assetService);
                        }
                        if (ImGui.IsItemHovered())
                            ImGui.SetTooltip("Square terrains use the native full-resolution heightfield. This value only affects the legacy fallback for rectangular terrain assets.");
                        Undo.TrackItem(_frameBeginState);

                        ImGui.SeparatorText("Sculpt brush");
                        DrawTerrainHeightmapBrushSelector(assetService);
                        DrawTerrainSculptInspectorControls(terrain);
                        ImGui.TextDisabled("Activate Terrain mode, then click-drag in a viewport.");
                    }
                }

                // Visuals & Material
                if (ImGui.CollapsingHeader("Visuals & Material", ImGuiTreeNodeFlags.DefaultOpen))
                {
                    string materialPath = obj.MaterialPath ?? "";
                    if (DrawMaterialPicker("Material##inspectMaterial", materialPath, assetService, out string selectedMaterial))
                    {
                        Undo.RecordState(_frameBeginState);
                        AssignMaterial(obj, selectedMaterial, sceneService, assetService);
                        Undo.ForceEnd(history, sceneService, assetService);
                    }
                    if (!string.IsNullOrWhiteSpace(obj.MaterialPath))
                    {
                        if (ImGui.Button("Open Material Graph"))
                            _materialEditor.Open(obj.MaterialPath);
                        ImGui.SameLine();
                    }
                    if (ImGui.Button("New Material"))
                        RequestNewMaterial([obj]);

                    DrawMaterialSlots(obj, sceneService, assetService, history);

                    if (ImGui.TreeNode("Legacy Texture Compatibility##inspectLegacyTexture"))
                    {
                        string texture = obj.Texture ?? "";
                        bool texChanged = ImGui.InputText("Texture##inspectTex", ref texture, 256);
                        Undo.TrackItem(_frameBeginState);
                        if (texChanged)
                        {
                            obj.Texture = texture;
                            var entity = scene.Entities.FirstOrDefault(e => e.Id == obj.Id);
                            if (entity != null)
                            {
                                entity.TexturePath = texture;
                                if (string.IsNullOrWhiteSpace(entity.MaterialPath) && !string.IsNullOrWhiteSpace(texture))
                                    entity.Material = assetService.AssetManager.GetLegacyMaterial(texture);
                            }
                            if (obj.IsTerrain)
                                sceneService.PopulateScene(assetService);
                        }
                        ImGui.TreePop();
                    }

                    if (!obj.IsModel)
                    {
                        Vector2 uvScale = obj.UvScale;
                        bool uvChanged = ImGui.DragFloat2("UV Scale##inspectUv", ref uvScale, 0.05f);
                        Undo.TrackItem(_frameBeginState);
                        if (uvChanged)
                        {
                            obj.UvScale = uvScale;
                            var entity = scene.Entities.FirstOrDefault(e => e.Id == obj.Id);
                            if (entity != null) entity.UvScale = uvScale;
                            if (obj.IsTerrain) sceneService.PopulateScene(assetService);
                        }
                        

                        Vector2 uvOffset = obj.UvOffset;
                        bool uvOffChanged = ImGui.DragFloat2("UV Offset##inspectUvOff", ref uvOffset, 0.01f);
                        Undo.TrackItem(_frameBeginState);
                        if (uvOffChanged)
                        {
                            obj.UvOffset = uvOffset;
                            var entity = scene.Entities.FirstOrDefault(e => e.Id == obj.Id);
                            if (entity != null) entity.UvOffset = uvOffset;
                            if (obj.IsTerrain) sceneService.PopulateScene(assetService);
                        }
                        

                        float uvRotDeg = obj.UvRotation * (180f / MathF.PI);
                        bool uvRotChanged = ImGui.DragFloat("UV Rotation##inspectUvRot", ref uvRotDeg, 0.5f, -360f, 360f, "%.1f deg");
                        Undo.TrackItem(_frameBeginState);
                        if (uvRotChanged)
                        {
                            float uvRot = uvRotDeg * (MathF.PI / 180f);
                            obj.UvRotation = uvRot;
                            var entity = scene.Entities.FirstOrDefault(e => e.Id == obj.Id);
                            if (entity != null) entity.UvRotation = uvRot;
                            if (obj.IsTerrain) sceneService.PopulateScene(assetService);
                        }
                        
                    }
                    else
                    {
                        ImGui.Text($"Model File: {obj.Model}");
                    }
                }

                if (!obj.IsLight && ImGui.CollapsingHeader("Geometry Nodes", ImGuiTreeNodeFlags.DefaultOpen))
                {
                    string graphPath = obj.GeometryGraphPath ?? "";
                    ImGui.TextDisabled("Optional procedural geometry asset");
                    ImGui.TextWrapped(string.IsNullOrWhiteSpace(graphPath) ? "(None - use the regular mesh/brush)" : graphPath);
                    if (ImGui.Button("Select Geometry Graph##inspectGeometryGraph"))
                    {
                        _showAssetBrowser = true;
                        _assetBrowser.OpenGeometryPicker(selectedPath =>
                        {
                            Undo.RecordState(_frameBeginState);
                            assetService.InvalidateMesh(obj.Id);
                            obj.GeometryGraphPath = selectedPath;
                            sceneService.PopulateScene(assetService);
                            Undo.ForceEnd(history, sceneService, assetService);
                        });
                    }
                    if (!string.IsNullOrWhiteSpace(graphPath))
                    {
                        ImGui.SameLine();
                        if (ImGui.Button("Open##inspectGeometryGraphOpen"))
                            _geometryEditor.Open(graphPath);
                        ImGui.SameLine();
                        if (ImGui.Button("Clear##inspectGeometryGraphClear"))
                        {
                            Undo.RecordState(_frameBeginState);
                            obj.GeometryGraphPath = null;
                            assetService.InvalidateMesh(obj.Id);
                            sceneService.PopulateScene(assetService);
                            Undo.ForceEnd(history, sceneService, assetService);
                        }
                    }
                }

                // Interaction
                if (ImGui.CollapsingHeader("Interaction", ImGuiTreeNodeFlags.None))
                {
                    string interactable = obj.Interactable ?? "";
                    bool interactChanged = ImGui.InputText("Interactable Type##inspectInteract", ref interactable, 128);
                    Undo.TrackItem(_frameBeginState);
                    if (interactChanged)
                    {
                        obj.Interactable = interactable;
                        var entity = scene.Entities.FirstOrDefault(e => e.Id == obj.Id);
                        if (entity != null) entity.InteractableType = interactable;
                    }
                    
                }

                // Behaviours
                if (ImGui.CollapsingHeader("Behaviours", ImGuiTreeNodeFlags.DefaultOpen))
                {
                    Undo.TrackItem(_frameBeginState);
                    
                    var entity = scene.Entities.FirstOrDefault(e => e.Id == obj.Id);
                    
                    for (int i = 0; i < obj.Behaviours.Count; i++)
                    {
                        var behaviour = obj.Behaviours[i];
                        if (ImGui.TreeNodeEx($"{behaviour.Type}##b_{i}", ImGuiTreeNodeFlags.DefaultOpen))
                        {
                            var t = Fuse.Behaviours.BehaviourSystem.GetBehaviourType(behaviour.Type);
                            if (t != null)
                            {
                                foreach (var prop in t.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
                                {
                                    if (System.Reflection.CustomAttributeExtensions.GetCustomAttribute<Fuse.Behaviours.ExportAttribute>(prop) != null)
                                    {
                                        var pName = prop.Name;
                                        if (prop.PropertyType == typeof(float))
                                        {
                                            float val = behaviour.Properties.TryGetPropertyValue(pName, out var v) && v != null ? (float)v : 0f;
                                            if (ImGui.DragFloat($"{pName}##{i}", ref val, 0.1f))
                                                behaviour.Properties[pName] = val;
                                        }
                                        else if (prop.PropertyType == typeof(int))
                                        {
                                            int val = behaviour.Properties.TryGetPropertyValue(pName, out var v) && v != null ? (int)v : 0;
                                            if (ImGui.DragInt($"{pName}##{i}", ref val))
                                                behaviour.Properties[pName] = val;
                                        }
                                        else if (prop.PropertyType == typeof(bool))
                                        {
                                            bool val = behaviour.Properties.TryGetPropertyValue(pName, out var v) && v != null ? (bool)v : false;
                                            if (ImGui.Checkbox($"{pName}##{i}", ref val))
                                                behaviour.Properties[pName] = val;
                                        }
                                        else if (prop.PropertyType == typeof(string))
                                        {
                                            string val = behaviour.Properties.TryGetPropertyValue(pName, out var v) && v != null
                                                ? v.GetValue<string?>() ?? ""
                                                : "";
                                            if (ImGui.InputText($"{pName}##{i}", ref val, 128))
                                                behaviour.Properties[pName] = val;
                                        }
                                    }
                                }
                            }
                            
                            if (ImGui.Button($"Remove##{i}"))
                            {
                                obj.Behaviours.RemoveAt(i);
                                i--;
                            }
                            ImGui.TreePop();
                        }
                    }

                    ImGui.Separator();
                    
                    var available = System.Linq.Enumerable.ToArray(Fuse.Behaviours.BehaviourSystem.GetAvailableBehaviours());
                    if (ImGui.BeginCombo("Add Behaviour", "Select..."))
                    {
                        foreach (var bType in available)
                        {
                            if (ImGui.Selectable(bType))
                            {
                                obj.Behaviours.Add(new Fuse.Behaviours.BehaviourData { Type = bType, Properties = new System.Text.Json.Nodes.JsonObject() });
                            }
                        }
                        ImGui.EndCombo();
                    }

                    if (entity != null)
                    {
                        entity.Behaviours.Clear();
                        foreach (var b in obj.Behaviours)
                            entity.Behaviours.Add(b.Clone());
                    }

                    
                }

                // Light Properties
                if (obj.IsLight)
                {
                    if (ImGui.CollapsingHeader("Light Properties", ImGuiTreeNodeFlags.DefaultOpen))
                    {
                        Undo.TrackItem(_frameBeginState);

                        string[] lightTypes = ["point", "spot", "directional"];
                        int lightTypeIdx = obj.LightType == "directional" ? 2 : (obj.LightType == "spot" ? 1 : 0);
                        if (ImGui.Combo("Type##lightType", ref lightTypeIdx, lightTypes, 3))
                            obj.LightType = lightTypes[lightTypeIdx];

                        Vector3 col = obj.LightColor;
                        if (ImGui.ColorEdit3("Color##lightColor", ref col, ImGuiColorEditFlags.Float))
                            obj.LightColor = col;

                        float intensity = obj.LightIntensity;
                        if (ImGui.DragFloat("Intensity##lightIntensity", ref intensity, 0.05f, 0.0f, 100.0f))
                            obj.LightIntensity = float.Max(0, intensity);

                        float radius = obj.LightRadius;
                        if (ImGui.DragFloat("Radius##lightRadius", ref radius, 0.1f, 0.1f, 500.0f))
                            obj.LightRadius = float.Max(0.1f, radius);

                        if (obj.LightType == "spot")
                        {
                            float innerDeg = float.RadiansToDegrees(obj.LightInnerCone);
                            if (ImGui.DragFloat("Inner Cone##lightInner", ref innerDeg, 0.5f, 0.0f, 90.0f))
                                obj.LightInnerCone = float.DegreesToRadians(innerDeg);

                            float outerDeg = float.RadiansToDegrees(obj.LightOuterCone);
                            if (ImGui.DragFloat("Outer Cone##lightOuter", ref outerDeg, 0.5f, 0.0f, 90.0f))
                                obj.LightOuterCone = float.DegreesToRadians(outerDeg);
                        }

                        if (obj.LightType == "spot" || obj.LightType == "point" || obj.LightType == "directional")
                        {
                            bool castShadows = obj.LightCastShadows;
                            if (ImGui.Checkbox("Cast Shadows##lightShadows", ref castShadows))
                                obj.LightCastShadows = castShadows;

                            if (obj.LightCastShadows)
                            {
                                float shadowBias = obj.LightShadowBias;
                                if (ImGui.DragFloat("Shadow Bias##lightShadowBias", ref shadowBias, 0.0001f, 0.0f, 0.1f, "%.5f"))
                                    obj.LightShadowBias = float.Max(0.0f, shadowBias);
                            }

                            bool isDynamic = obj.LightDynamic;
                            if (ImGui.Checkbox("Dynamic (Follow Parent)##lightDynamic", ref isDynamic))
                                obj.LightDynamic = isDynamic;
                        }

                        SyncLight(sceneService, obj);
                        
                    }
                }

                // Physics Body (skip for lights)
                if (obj.Body != null && !obj.IsLight)
                {
                    var body = obj.Body;
                    if (ImGui.CollapsingHeader("Physics Body", ImGuiTreeNodeFlags.DefaultOpen))
                    {
                        if (obj.IsModel)
                        {
                            string[] shapes = { "Trimesh", "Convex Hull", "No Collision" };
                            int shapeIdx = body.Shape == MapShapeType.Trimesh ? 0 : (body.Shape == MapShapeType.ConvexHull ? 1 : 2);
                            bool shapeChanged = ImGui.Combo("Collision Shape", ref shapeIdx, shapes, shapes.Length);
                            Undo.TrackItem(_frameBeginState);
                            if (shapeChanged)
                            {
                                body.Shape = shapeIdx == 0 ? MapShapeType.Trimesh : (shapeIdx == 1 ? MapShapeType.ConvexHull : MapShapeType.None);
                            }
                        }
                        else
                        {
                            bool isGroup = IsGroupObject(obj);
                            string[] shapes = { isGroup ? "Compound Group" : "Default", "No Collision" };
                            int shapeIdx = body.Shape == MapShapeType.None ? 1 : 0;
                            bool shapeChanged = ImGui.Combo("Collision", ref shapeIdx, shapes, shapes.Length);
                            Undo.TrackItem(_frameBeginState);
                            if (shapeChanged)
                            {
                                if (shapeIdx == 1) body.Shape = MapShapeType.None;
                                else 
                                {
                                    if (isGroup) body.Shape = MapShapeType.Compound;
                                    else if (obj is Fuse.Scene.Model.Brush) body.Shape = MapShapeType.Box;
                                    else if (obj.Mesh == "sphere") body.Shape = MapShapeType.Sphere;
                                    else if (obj.Mesh == "capsule") body.Shape = MapShapeType.Capsule;
                                    else body.Shape = MapShapeType.Box;
                                    if (isGroup && body.Mass <= 0.0f)
                                        body.Mass = GetGroupDefaultMass(obj, sceneService.Document);
                                }
                            }
                            if (isGroup && body.Shape == MapShapeType.Compound)
                                ImGui.TextDisabled("Children are solved as one compound body; child masses are ignored.");
                        }

                        Vector3 pos = body.Position;
                        bool posChanged = ImGui.DragFloat3("Position##inspectPos", ref pos, 0.05f, 0.0f, 0.0f, "%.3f");
                        Undo.TrackItem(_frameBeginState);
                        if (posChanged)
                        {
                            pos = ApplySnap(pos, _snapGrid);
                            Vector3 delta = pos - body.Position;
                            if (delta.LengthSquared() > 0.00001f)
                            {
                                var objectsToTransform = GetObjectsToTransform(sceneService.Document);
                                foreach (var o in objectsToTransform)
                                {
                                    if (o.Body != null)
                                    {
                                        o.Body.Position += delta;
                                        var entity = scene.Entities.FirstOrDefault(e => e.Id == o.Id);
                                        if (entity != null) entity.Transform.Position = o.Body.Position;
                                        SyncLight(sceneService, o);
                                    }
                                }
                            }
                        }
                        

                        Vector3 euler = QuaternionToEuler(body.Rotation);
                        bool rotChanged = ImGui.DragFloat3("Rotation (Euler)##inspectRot", ref euler, 0.5f, 0.0f, 0.0f, "%.3f");
                        Undo.TrackItem(_frameBeginState);
                        if (rotChanged)
                        {
                            euler = ApplySnap(euler, _snapAngle);
                            Quaternion newRot = Quaternion.Normalize(EulerToQuaternion(euler));
                            Quaternion deltaRot = newRot * Quaternion.Inverse(body.Rotation);
                            Vector3 pivot = body.Position;

                            var objectsToTransform = GetObjectsToTransform(sceneService.Document);
                            foreach (var o in objectsToTransform)
                            {
                                if (o.Body != null)
                                {
                                    if (o != obj)
                                    {
                                        Vector3 relativePos = o.Body.Position - pivot;
                                        Vector3 rotatedPos = Vector3.Transform(relativePos, deltaRot);
                                        o.Body.Position = pivot + rotatedPos;
                                    }
                                    o.Body.Rotation = Quaternion.Normalize(deltaRot * o.Body.Rotation);
                                    var entity = scene.Entities.FirstOrDefault(e => e.Id == o.Id);
                                    if (entity != null)
                                    {
                                        entity.Transform.Position = o.Body.Position;
                                        entity.Transform.Rotation = o.Body.Rotation;
                                    }
                                    SyncLight(sceneService, o);
                                }
                            }
                        }
                        

                        if (body.Shape != MapShapeType.None)
                        {
                            float mass = body.Mass;
                            bool massChanged = ImGui.DragFloat("Mass##inspectMass", ref mass, 0.1f, 0.0f, 100000.0f, "%.3f");
                            Undo.TrackItem(_frameBeginState);
                            if (massChanged) body.Mass = mass;

                            float buoyancyVolume = body.BuoyancyVolume ?? 0.0f;
                            bool buoyancyVolumeChanged = ImGui.DragFloat(
                                "Buoyancy volume (m³)##inspectBuoyancyVolume",
                                ref buoyancyVolume,
                                0.05f,
                                0.0f,
                                100000.0f,
                                "%.3f");
                            Undo.TrackItem(_frameBeginState);
                            if (buoyancyVolumeChanged)
                            {
                                body.BuoyancyVolume = buoyancyVolume > 0.0001f
                                    ? buoyancyVolume
                                    : null;
                            }
                            ImGui.SameLine();
                            ImGui.TextDisabled("0 = collider volume");

                            float authoredVolume = CalculateAuthoredBuoyancyVolume(body);
                            if (body.Mass > 0.0f && authoredVolume > 0.0001f)
                            {
                                float objectDensity = body.Mass / authoredVolume;
                                float waterDensity = MathF.Max(doc.Ocean.WaterDensity, 0.0001f);
                                float equilibriumFraction = objectDensity / waterDensity;
                                ImGui.TextDisabled(
                                    $"Volume {authoredVolume:F3} m³ | density {objectDensity:F1} kg/m³ | equilibrium {equilibriumFraction:P1}");
                                if (equilibriumFraction < 0.05f)
                                {
                                    ImGui.TextColored(
                                        new Vector4(1.0f, 0.65f, 0.2f, 1.0f),
                                        "Very low density: the physical waterline is shallow and stiff.");
                                }
                                else if (equilibriumFraction > 1.0f)
                                {
                                    ImGui.TextColored(
                                        new Vector4(0.45f, 0.7f, 1.0f, 1.0f),
                                        "Density exceeds the water density: this body will sink.");
                                }
                            }
                            

                            float friction = body.Friction;
                            bool fricChanged = ImGui.DragFloat("Friction##inspectFriction", ref friction, 0.05f, 0.0f, 10.0f, "%.2f");
                            Undo.TrackItem(_frameBeginState);
                            if (fricChanged) body.Friction = friction;
                            

                            float restitution = body.Restitution;
                            bool restChanged = ImGui.DragFloat("Restitution##inspectRestitution", ref restitution, 0.05f, 0.0f, 1.0f, "%.2f");
                            Undo.TrackItem(_frameBeginState);
                            if (restChanged) body.Restitution = restitution;
                            

                            bool isTrigger = body.IsTrigger;
                            bool trigChanged = ImGui.Checkbox("Is Trigger##inspectTrigger", ref isTrigger);
                            Undo.TrackItem(_frameBeginState);
                            if (trigChanged)
                            {
                                body.IsTrigger = isTrigger;
                                var entity = scene.Entities.FirstOrDefault(e => e.Id == obj.Id);
                                if (entity != null)
                                    ApplyTriggerPreviewMaterial(obj, entity, isTrigger, assetService);
                            }
                        }

                        switch (body.Shape)
                        {
                            case MapShapeType.Box or MapShapeType.Trimesh when body.HalfExtents.HasValue:
                                Vector3 he = body.HalfExtents.Value;
                                bool heChanged = ImGui.DragFloat3("Half Extents##inspectHe", ref he, 0.05f, 0.0f, 1000.0f, "%.3f");
                                Undo.TrackItem(_frameBeginState);
                                if (heChanged)
                                {
                                    Vector3 oldHalf = body.HalfExtents ?? Vector3.One;
                                    body.HalfExtents = ApplySnap(he, _snapGrid);
                                    var entity = scene.Entities.FirstOrDefault(e => e.Id == obj.Id);
                                    if (obj is Brush brush)
                                    {
                                        Vector3 scale = body.HalfExtents.Value / oldHalf;
                                        brush.ScalePlanes(scale);
                                        assetService.InvalidateMesh(brush.Id);
                                        if (entity != null)
                                        {
                                            entity.Mesh = assetService.GetOrCreateMesh(brush);
                                        }
                                    }
                                    if (entity != null && body.HalfExtents.HasValue)
                                    {
                                        if (obj is Brush)
                                            entity.Transform.Scale = Vector3.One;
                                        else if (!obj.IsModel)
                                            entity.Transform.Scale = body.HalfExtents.Value * 2.0f;
                                    }
                                }
                                
                                break;
                            case MapShapeType.Sphere when body.Radius.HasValue:
                                float rad = body.Radius.Value;
                                bool radChanged = ImGui.DragFloat("Radius##inspectRad", ref rad, 0.05f, 0.0f, 1000.0f, "%.3f");
                                Undo.TrackItem(_frameBeginState);
                                if (radChanged)
                                {
                                    body.Radius = ApplySnap(rad, _snapGrid);
                                    var entity = scene.Entities.FirstOrDefault(e => e.Id == obj.Id);
                                    if (entity != null && body.Radius.HasValue)
                                    {
                                        if (!obj.IsModel) entity.Transform.Scale = new Vector3(body.Radius.Value * 2.0f);
                                    }
                                }
                                
                                break;
                            case MapShapeType.Capsule when body.Radius.HasValue && body.Height.HasValue:
                                float capRad = body.Radius.Value;
                                bool capRadChanged = ImGui.DragFloat("Radius##inspectCapRad", ref capRad, 0.05f, 0.0f, 1000.0f, "%.3f");
                                Undo.TrackItem(_frameBeginState);
                                if (capRadChanged) body.Radius = ApplySnap(capRad, _snapGrid);
                                

                                float capH = body.Height.Value;
                                bool capHChanged = ImGui.DragFloat("Height##inspectCapH", ref capH, 0.05f, 0.0f, 1000.0f, "%.3f");
                                Undo.TrackItem(_frameBeginState);
                                if (capHChanged) body.Height = ApplySnap(capH, _snapGrid);

                                if (capRadChanged || capHChanged)
                                {
                                    var entity = scene.Entities.FirstOrDefault(e => e.Id == obj.Id);
                                    if (entity != null && body.Radius.HasValue && body.Height.HasValue)
                                    {
                                        entity.Transform.Scale = MeshGenerator.GetCapsuleRenderScale(
                                            body.Radius.Value,
                                            body.Height.Value);
                                    }
                                }
                                
                                break;
                        }
                    }
                }
            }
        }
        ImGui.PopStyleColor(3);
        ImGui.PopStyleVar();
        ImGui.EndChild();

        // Apply Deletion or Duplication
        if (objectToDelete != null)
        {
            DeleteObject(objectToDelete, sceneService, assetService, history);
        }
        else if (objectToDuplicate != null)
        {
            DuplicateObject(objectToDuplicate, sceneService, assetService, history);
        }

        DrawModelImportDialog(sceneService, assetService, history);

        ImGui.End();
    }

    private void DrawTerrainCreateDialog(
        EditorWindow window,
        EditorSceneService sceneService,
        EditorAssetService assetService,
        CommandHistory history)
    {
        if (!_showTerrainCreateDialog)
            return;

        // The heightmap picker is drawn as a separate editor window. Keep the
        // create dialog closed while it is open so its modal overlay cannot
        // block interaction with the Asset Browser.
        if (_terrainCreateWaitingForHeightmap)
            return;

        ImGui.OpenPopup("Create Terrain");
        bool open = true;
        if (ImGui.BeginPopupModal("Create Terrain", ref open, ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.TextUnformatted(_terrainSourceMode == 0
                ? "Create a local heightmap terrain."
                : "Create a streamed procedural world terrain.");
            ImGui.InputText("Name", ref _terrainName, 128);

            string[] sourceLabels = ["Flat / Heightmap", "Procedural world"];
            bool sourceChanged = ImGui.Combo(
                "Source",
                ref _terrainSourceMode,
                sourceLabels,
                sourceLabels.Length);

            int width = _terrainWidth;
            int depth = _terrainDepth;
            float cellSize = _terrainCellSize;
            float heightScale = _terrainHeightScale;
            int chunkQuads = _terrainChunkQuads;

            bool chunkChanged = ImGui.InputInt("Chunk quads", ref chunkQuads);
            _terrainChunkQuads = Math.Clamp(chunkQuads, 1, 256);

            if (_terrainSourceMode == 0)
            {
                ImGui.InputInt("Width samples", ref width);
                ImGui.InputInt("Depth samples", ref depth);
                ImGui.DragFloat("Cell size", ref cellSize, 0.05f, 0.01f, 100.0f, "%.2f");
                ImGui.DragFloat("Height scale", ref heightScale, 0.1f, 0.01f, 10000.0f, "%.2f");

                ImGui.AlignTextToFramePadding();
                ImGui.TextUnformatted("Heightmap");
                ImGui.SameLine();
                string heightmapLabel = string.IsNullOrWhiteSpace(_terrainHeightmapPath)
                    ? "(Flat terrain)"
                    : _terrainHeightmapPath;
                float clearButtonWidth = 62.0f;
                float pickerWidth = MathF.Max(
                    120.0f,
                    ImGui.GetContentRegionAvail().X - clearButtonWidth - ImGui.GetStyle().ItemSpacing.X);
                if (ImGui.Button(
                        $"{heightmapLabel}##terrainHeightmapPicker",
                        new Vector2(pickerWidth, 0.0f)))
                {
                    OpenTerrainHeightmapPicker();
                }
                ImGui.SameLine();
                if (ImGui.Button("Clear##terrainHeightmapClear", new Vector2(clearButtonWidth, 0.0f)))
                    _terrainHeightmapPath = "";
                ImGui.TextDisabled("Use a grayscale image. Its resolution becomes the terrain resolution.");
            }

            if (_terrainSourceMode == 1)
            {
                bool controlsChanged = DrawProceduralTerrainCreateControls();
                if (sourceChanged || controlsChanged || chunkChanged || _terrainProceduralPreviewDirty)
                {
                    sceneService.UpdateProceduralTerrainPreview(
                        BuildProceduralTerrainSettings(),
                        _terrainChunkQuads,
                        ProceduralTerrainPreviewMaterialPath,
                        Array.Empty<string>(),
                        "",
                        Vector2.One,
                        Vector2.Zero,
                        0.0f,
                        assetService);
                    _terrainProceduralPreviewDirty = false;
                    _terrainProceduralPreviewNeedsRender = true;
                }

                DrawProceduralTerrainGeneratorPreview(window, sceneService, assetService);
            }
            else if (sourceChanged)
            {
                sceneService.ClearProceduralTerrainPreview();
                _terrainProceduralPreviewDirty = false;
                _terrainProceduralPreviewNeedsRender = false;
            }

            _terrainWidth = Math.Clamp(width, 2, 4096);
            _terrainDepth = Math.Clamp(depth, 2, 4096);
            _terrainCellSize = Math.Clamp(cellSize, 0.01f, 100.0f);
            _terrainHeightScale = Math.Clamp(heightScale, 0.01f, 10000.0f);
            _terrainChunkQuads = Math.Clamp(chunkQuads, 1, 256);

            ImGui.Separator();
            if (ImGui.Button("Create", new Vector2(120, 0)))
            {
                try
                {
                    string safeName = SanitizeAssetName(_terrainName);
                    string relativePath = $"Terrains/{safeName}.terrain";
                    string fullPath = assetService.ResolveEditorAssetPath(relativePath);
                    int suffix = 1;
                    while (File.Exists(fullPath))
                    {
                        relativePath = $"Terrains/{safeName}_{suffix++}.terrain";
                        fullPath = assetService.ResolveEditorAssetPath(relativePath);
                    }

                    if (_terrainSourceMode == 1)
                    {
                        new ProceduralTerrainAsset(BuildProceduralTerrainSettings()).Save(fullPath);
                    }
                    else
                    {
                        TerrainAsset terrain = string.IsNullOrWhiteSpace(_terrainHeightmapPath)
                            ? TerrainAsset.CreateFlat(
                                _terrainWidth,
                                _terrainDepth,
                                _terrainCellSize,
                                _terrainHeightScale)
                            : TerrainAsset.FromHeightmap(
                                assetService.ResolveEditorAssetPath(_terrainHeightmapPath),
                                _terrainCellSize,
                                _terrainHeightScale);
                        TerrainTileSetAsset.FromSingle(terrain).Save(fullPath);
                    }

                    string objectId = $"terrain_{safeName}";
                    var obj = new MapObject
                    {
                        Id = objectId,
                        Visible = true,
                        TerrainAssetPath = relativePath,
                        TerrainChunkQuads = _terrainChunkQuads,
                        MaterialPath = DefaultMaterialPath,
                        Body = new MapBody
                        {
                            Shape = MapShapeType.Trimesh,
                            Position = Vector3.Zero,
                            Rotation = Quaternion.Identity,
                            Mass = 0.0f,
                            Friction = 0.5f,
                            Restitution = 0.0f
                        }
                    };

                    string pre = sceneService.Document.Serialize();
                    sceneService.Document.Objects.Add(obj);
                    SceneNameManager.EnsureAllUnique(sceneService.Document);
                    _selectedObject = obj;
                    _selectedObjects.Clear();
                    _selectedObjects.Add(obj);
                    sceneService.PopulateScene(assetService);
                    string post = sceneService.Document.Serialize();
                    sceneService.MarkModified(post);
                    history.PushCommand(new SnapshotCommand(sceneService, assetService, pre, post));

                    _showTerrainCreateDialog = false;
                    _terrainCreateWaitingForHeightmap = false;
                    sceneService.ClearProceduralTerrainPreview();
                    _terrainProceduralPreviewDirty = false;
                    _terrainProceduralPreviewNeedsRender = false;
                    ImGui.CloseCurrentPopup();
                }
                catch (Exception ex)
                {
                    Logger.Error($"Terrain creation failed: {ex.Message}");
                    ShowDocumentError($"Terrain creation failed: {ex.Message}");
                }
            }

            ImGui.SameLine();
            if (ImGui.Button("Cancel", new Vector2(120, 0)))
            {
                _showTerrainCreateDialog = false;
                _terrainCreateWaitingForHeightmap = false;
                sceneService.ClearProceduralTerrainPreview();
                _terrainProceduralPreviewDirty = false;
                _terrainProceduralPreviewNeedsRender = false;
                ImGui.CloseCurrentPopup();
            }

            ImGui.EndPopup();
        }

        if (!open)
        {
            _showTerrainCreateDialog = false;
            _terrainCreateWaitingForHeightmap = false;
            sceneService.ClearProceduralTerrainPreview();
            _terrainProceduralPreviewDirty = false;
            _terrainProceduralPreviewNeedsRender = false;
        }
    }

    private void DrawProceduralTerrainGeneratorPreview(
        EditorWindow window,
        EditorSceneService sceneService,
        EditorAssetService assetService)
    {
        _terrainGeneratorPreviewViewport ??= new EditorViewport(
            window.GL,
            CameraViewType.Perspective3D,
            assetService.ImageBasedLighting);

        EditorViewport viewport = _terrainGeneratorPreviewViewport;
        viewport.Camera.FarClipPlane = 100_000.0f;
        float previewTileSize = Math.Clamp(_terrainProceduralTileSize, 32.0f, 65_536.0f);
        _terrainGeneratorPreviewTarget = new Vector3(
            previewTileSize * 0.5f,
            400.0f,
            previewTileSize * 0.5f);
        viewport.Camera.MinDistance = Math.Clamp(previewTileSize * 0.02f, 2.0f, 512.0f);
        viewport.Camera.MaxDistance = 100_000.0f;

        // The modal is auto-sized by ImGui. Keep the render target large
        // enough to inspect the terrain while preventing the preview from
        // taking the entire editor window on smaller monitors.
        Vector2 available = ImGui.GetContentRegionAvail();
        float previewWidth = available.X > 32.0f
            ? MathF.Min(MathF.Max(available.X, 560.0f), 820.0f)
            : 720.0f;
        float previewHeight = Math.Clamp(previewWidth * 0.56f, 300.0f, 470.0f);
        Vector2 previewSize = new(previewWidth, previewHeight);

        if (!_terrainGeneratorPreviewCameraInitialized || _terrainProceduralPreviewNeedsRender)
        {
            float previewDistance = Math.Clamp(previewTileSize * 2.05f, 128.0f, 10_000.0f);
            viewport.Camera.Position = _terrainGeneratorPreviewTarget + new Vector3(
                0.0f,
                previewDistance * 0.58f,
                previewDistance);
            viewport.Camera.LookAt(_terrainGeneratorPreviewTarget);
            _terrainGeneratorPreviewCameraInitialized = true;
            _terrainProceduralPreviewNeedsRender = false;
        }

        // Check the image rectangle before drawing it so the current frame's
        // mouse input can update the camera before the framebuffer is rendered.
        Vector2 previewMin = ImGui.GetCursorScreenPos();
        Vector2 previewMax = previewMin + previewSize;
        Vector2 mouse = ImGui.GetIO().MousePos;
        bool hovered = mouse.X >= previewMin.X && mouse.X <= previewMax.X &&
                       mouse.Y >= previewMin.Y && mouse.Y <= previewMax.Y;
        if (hovered)
        {
            Vector2 delta = ImGui.GetIO().MouseDelta;
            if (ImGui.IsMouseDown(ImGuiMouseButton.Left) && delta.LengthSquared() > 0.0001f)
            {
                viewport.Camera.OrbitAround(
                    _terrainGeneratorPreviewTarget,
                    -delta.X * 0.35f,
                    -delta.Y * 0.25f);
            }

            float wheel = ImGui.GetIO().MouseWheel;
            if (float.IsFinite(wheel) && MathF.Abs(wheel) > 0.0001f)
            {
                viewport.Camera.OrbitAround(
                    _terrainGeneratorPreviewTarget,
                    0.0f,
                    0.0f,
                    MathF.Exp(-wheel * 0.12f));
            }
        }

        int targetWidth = Math.Max(8, ((int)MathF.Round(previewWidth) + 3) & ~3);
        int targetHeight = Math.Max(8, ((int)MathF.Round(previewHeight) + 3) & ~3);
        if (targetWidth != viewport.Width || targetHeight != viewport.Height)
            viewport.CreateFbo(targetWidth, targetHeight);

        window.Glfw.GetFramebufferSize(window.Handle, out int framebufferWidth, out int framebufferHeight);
        viewport.BeginRender();
        try
        {
            viewport.RenderScene(
                assetService,
                sceneService,
                SnapGrid,
                sceneOverride: sceneService.ProceduralPreviewScene,
                drawAtmosphere: false,
                drawGrid: false);
        }
        finally
        {
            viewport.EndRender(
                Math.Max(1, framebufferWidth),
                Math.Max(1, framebufferHeight));
        }

        ImGui.Image(
            (IntPtr)viewport.ColorTexture,
            previewSize,
            new Vector2(0.0f, 1.0f),
            new Vector2(1.0f, 0.0f));
        ImGui.TextDisabled("Preview: left-drag to orbit, mouse wheel to zoom. Uses Materials/GRASS.fmat.");
    }

    private bool DrawProceduralTerrainCreateControls()
    {
        bool changed = false;
        ImGui.SeparatorText("Procedural world");
        ImGui.TextDisabled("The .terrain stores this recipe; only tiles near the camera are generated.");

        if (ImGui.Button("Randomize seed##proceduralTerrainRandomSeed"))
        {
            _terrainProceduralSeed = Random.Shared.Next();
            changed = true;
        }
        ImGui.SameLine();
        if (ImGui.Button("Regenerate preview##proceduralTerrainRegenerate"))
            changed = true;
        changed |= ImGui.InputInt("Seed##proceduralTerrainSeed", ref _terrainProceduralSeed);
        changed |= ImGui.DragFloat(
            "World size (km)##proceduralTerrainWorldSize",
            ref _terrainProceduralWorldSizeKm,
            10.0f,
            1.0f,
            80_000.0f,
            "%.0f km");
        changed |= ImGui.DragFloat(
            "Tile size (m)##proceduralTerrainTileSize",
            ref _terrainProceduralTileSize,
            8.0f,
            32.0f,
            65_536.0f,
            "%.0f m");
        changed |= ImGui.InputInt("Tile resolution##proceduralTerrainResolution", ref _terrainProceduralResolution);

        ImGui.SeparatorText("Shape");
        changed |= ImGui.DragFloat("Minimum height##proceduralTerrainMinHeight", ref _terrainProceduralMinHeight, 1.0f, -100_000.0f, 100_000.0f, "%.1f");
        changed |= ImGui.DragFloat("Maximum height##proceduralTerrainMaxHeight", ref _terrainProceduralMaxHeight, 1.0f, -100_000.0f, 100_000.0f, "%.1f");
        changed |= ImGui.DragFloat("Sea level##proceduralTerrainSeaLevel", ref _terrainProceduralSeaLevel, 1.0f, -100_000.0f, 100_000.0f, "%.1f");
        changed |= ImGui.DragFloat("Base height##proceduralTerrainBaseHeight", ref _terrainProceduralBaseHeight, 1.0f, -100_000.0f, 100_000.0f, "%.1f");
        changed |= ImGui.DragFloat("Continental amplitude##proceduralTerrainContinentalAmplitude", ref _terrainProceduralContinentalAmplitude, 10.0f, 0.0f, 100_000.0f, "%.1f");
        changed |= ImGui.DragFloat("Continental scale##proceduralTerrainContinentalScale", ref _terrainProceduralContinentalScale, 0.000001f, 0.0000001f, 0.001f, "%.6f");
        changed |= ImGui.InputInt("Continental octaves##proceduralTerrainContinentalOctaves", ref _terrainProceduralContinentalOctaves);
        changed |= ImGui.DragFloat("Noise lacunarity##proceduralTerrainNoiseLacunarity", ref _terrainProceduralNoiseLacunarity, 0.01f, 1.01f, 4.0f, "%.2f");
        changed |= ImGui.DragFloat("Noise gain##proceduralTerrainNoiseGain", ref _terrainProceduralNoiseGain, 0.01f, 0.01f, 0.99f, "%.2f");
        changed |= ImGui.DragFloat("Mountain height##proceduralTerrainMountainHeight", ref _terrainProceduralMountainHeight, 10.0f, 0.0f, 20_000.0f, "%.1f");
        changed |= ImGui.DragFloat("Mountain scale##proceduralTerrainMountainScale", ref _terrainProceduralMountainScale, 0.000001f, 0.0000001f, 0.001f, "%.6f");
        changed |= ImGui.InputInt("Mountain octaves##proceduralTerrainMountainOctaves", ref _terrainProceduralMountainOctaves);
        changed |= ImGui.DragFloat("Mountain mask start##proceduralTerrainMountainMaskStart", ref _terrainProceduralMountainMaskStart, 0.01f, 0.0f, 1.0f, "%.2f");
        changed |= ImGui.DragFloat("Mountain mask end##proceduralTerrainMountainMaskEnd", ref _terrainProceduralMountainMaskEnd, 0.01f, 0.0f, 1.0f, "%.2f");
        changed |= ImGui.DragFloat("Valley depth##proceduralTerrainValleyDepth", ref _terrainProceduralValleyDepth, 5.0f, 0.0f, 10_000.0f, "%.1f");
        changed |= ImGui.DragFloat("Valley scale##proceduralTerrainValleyScale", ref _terrainProceduralValleyScale, 0.000001f, 0.0000001f, 0.001f, "%.6f");
        changed |= ImGui.InputInt("Valley octaves##proceduralTerrainValleyOctaves", ref _terrainProceduralValleyOctaves);
        changed |= ImGui.DragFloat("Detail height##proceduralTerrainDetailHeight", ref _terrainProceduralDetailHeight, 1.0f, 0.0f, 1000.0f, "%.1f");
        changed |= ImGui.DragFloat("Detail scale##proceduralTerrainDetailScale", ref _terrainProceduralDetailScale, 0.00001f, 0.000001f, 0.01f, "%.6f");
        changed |= ImGui.InputInt("Detail octaves##proceduralTerrainDetailOctaves", ref _terrainProceduralDetailOctaves);
        changed |= ImGui.DragFloat("Domain warp strength##proceduralTerrainWarpStrength", ref _terrainProceduralWarpStrength, 0.01f, 0.0f, 1.0f, "%.2f");
        changed |= ImGui.DragFloat("Domain warp scale##proceduralTerrainWarpScale", ref _terrainProceduralWarpScale, 0.000001f, 0.0000001f, 0.001f, "%.6f");
        changed |= ImGui.InputInt("Domain warp octaves##proceduralTerrainWarpOctaves", ref _terrainProceduralWarpOctaves);
        changed |= ImGui.DragFloat("Erosion approximation##proceduralTerrainErosion", ref _terrainProceduralErosion, 0.01f, 0.0f, 1.0f, "%.2f");
        changed |= ImGui.DragFloat("River depth##proceduralTerrainRiverDepth", ref _terrainProceduralRiverDepth, 1.0f, 0.0f, 5000.0f, "%.1f");
        changed |= ImGui.DragFloat("River scale##proceduralTerrainRiverScale", ref _terrainProceduralRiverScale, 0.000001f, 0.0000001f, 0.001f, "%.6f");
        changed |= ImGui.InputInt("River octaves##proceduralTerrainRiverOctaves", ref _terrainProceduralRiverOctaves);

        ImGui.SeparatorText("Streaming budget");
        changed |= ImGui.InputInt("Preview tile radius##proceduralTerrainPreviewRadius", ref _terrainProceduralPreviewRadius);
        changed |= ImGui.InputInt("Streaming tile radius##proceduralTerrainStreamingRadius", ref _terrainProceduralStreamingRadius);
        changed |= ImGui.InputInt("Collision tile radius##proceduralTerrainCollisionRadius", ref _terrainProceduralCollisionRadius);
        changed |= ImGui.InputInt("Maximum resident tiles##proceduralTerrainMaxResident", ref _terrainProceduralMaxResidentTiles);
        changed |= ImGui.InputInt("Generation tasks##proceduralTerrainGenerationTasks", ref _terrainProceduralMaxGenerationTasks);
        changed |= ImGui.InputInt("Tile uploads per frame##proceduralTerrainUploads", ref _terrainProceduralMaxUploadsPerFrame);
        changed |= ImGui.DragFloat("LOD pixel error##proceduralTerrainLodPixelError", ref _terrainProceduralLodPixelError, 0.25f, 0.1f, 100.0f, "%.2f px");
        ImGui.TextDisabled("Generation runs on workers; mesh and collision uploads are budgeted per frame.");

        _terrainProceduralWorldSizeKm = Math.Clamp(_terrainProceduralWorldSizeKm, 1.0f, 80_000.0f);
        _terrainProceduralTileSize = Math.Clamp(_terrainProceduralTileSize, 32.0f, 65_536.0f);
        _terrainProceduralResolution = Math.Clamp(_terrainProceduralResolution, 17, 513);
        _terrainProceduralMinHeight = Math.Clamp(_terrainProceduralMinHeight, -100_000.0f, 100_000.0f);
        _terrainProceduralMaxHeight = Math.Clamp(_terrainProceduralMaxHeight, _terrainProceduralMinHeight + 1.0f, 100_000.0f);
        _terrainProceduralSeaLevel = Math.Clamp(_terrainProceduralSeaLevel, _terrainProceduralMinHeight, _terrainProceduralMaxHeight);
        _terrainProceduralContinentalOctaves = Math.Clamp(_terrainProceduralContinentalOctaves, 1, 8);
        _terrainProceduralMountainOctaves = Math.Clamp(_terrainProceduralMountainOctaves, 1, 8);
        _terrainProceduralValleyOctaves = Math.Clamp(_terrainProceduralValleyOctaves, 1, 8);
        _terrainProceduralDetailOctaves = Math.Clamp(_terrainProceduralDetailOctaves, 1, 8);
        _terrainProceduralWarpOctaves = Math.Clamp(_terrainProceduralWarpOctaves, 1, 8);
        _terrainProceduralRiverOctaves = Math.Clamp(_terrainProceduralRiverOctaves, 1, 8);
        _terrainProceduralNoiseLacunarity = Math.Clamp(_terrainProceduralNoiseLacunarity, 1.01f, 4.0f);
        _terrainProceduralNoiseGain = Math.Clamp(_terrainProceduralNoiseGain, 0.01f, 0.99f);
        _terrainProceduralMountainMaskStart = Math.Clamp(_terrainProceduralMountainMaskStart, 0.0f, 0.999f);
        _terrainProceduralMountainMaskEnd = Math.Clamp(_terrainProceduralMountainMaskEnd, _terrainProceduralMountainMaskStart + 0.001f, 1.0f);
        _terrainProceduralPreviewRadius = Math.Clamp(_terrainProceduralPreviewRadius, 0, 4);
        _terrainProceduralStreamingRadius = Math.Clamp(_terrainProceduralStreamingRadius, 0, 8);
        _terrainProceduralCollisionRadius = Math.Clamp(_terrainProceduralCollisionRadius, 0, _terrainProceduralStreamingRadius);
        _terrainProceduralMaxResidentTiles = Math.Clamp(_terrainProceduralMaxResidentTiles, 1, 4096);
        _terrainProceduralMaxGenerationTasks = Math.Clamp(_terrainProceduralMaxGenerationTasks, 1, 16);
        _terrainProceduralMaxUploadsPerFrame = Math.Clamp(_terrainProceduralMaxUploadsPerFrame, 1, 8);
        _terrainProceduralLodPixelError = MathF.Max(0.1f, _terrainProceduralLodPixelError);
        return changed;
    }

    private ProceduralTerrainSettings BuildProceduralTerrainSettings()
    {
        var settings = new ProceduralTerrainSettings
        {
            Seed = _terrainProceduralSeed,
            WorldSizeMeters = _terrainProceduralWorldSizeKm * 1000.0,
            TileSizeMeters = _terrainProceduralTileSize,
            TileResolution = _terrainProceduralResolution,
            MinHeight = _terrainProceduralMinHeight,
            MaxHeight = _terrainProceduralMaxHeight,
            SeaLevel = _terrainProceduralSeaLevel,
            BaseHeight = _terrainProceduralBaseHeight,
            ContinentalAmplitude = _terrainProceduralContinentalAmplitude,
            ContinentalScale = _terrainProceduralContinentalScale,
            ContinentalOctaves = _terrainProceduralContinentalOctaves,
            NoiseLacunarity = _terrainProceduralNoiseLacunarity,
            NoiseGain = _terrainProceduralNoiseGain,
            MountainHeight = _terrainProceduralMountainHeight,
            MountainScale = _terrainProceduralMountainScale,
            MountainOctaves = _terrainProceduralMountainOctaves,
            MountainMaskStart = _terrainProceduralMountainMaskStart,
            MountainMaskEnd = _terrainProceduralMountainMaskEnd,
            ValleyDepth = _terrainProceduralValleyDepth,
            ValleyScale = _terrainProceduralValleyScale,
            ValleyOctaves = _terrainProceduralValleyOctaves,
            DetailHeight = _terrainProceduralDetailHeight,
            DetailScale = _terrainProceduralDetailScale,
            DetailOctaves = _terrainProceduralDetailOctaves,
            DomainWarpStrength = _terrainProceduralWarpStrength,
            DomainWarpScale = _terrainProceduralWarpScale,
            DomainWarpOctaves = _terrainProceduralWarpOctaves,
            ErosionStrength = _terrainProceduralErosion,
            RiverDepth = _terrainProceduralRiverDepth,
            RiverScale = _terrainProceduralRiverScale,
            RiverOctaves = _terrainProceduralRiverOctaves,
            PreviewTileRadius = _terrainProceduralPreviewRadius,
            StreamingTileRadius = _terrainProceduralStreamingRadius,
            CollisionTileRadius = _terrainProceduralCollisionRadius,
            MaxResidentTiles = _terrainProceduralMaxResidentTiles,
            MaxGenerationTasks = _terrainProceduralMaxGenerationTasks,
            MaxTileUploadsPerFrame = _terrainProceduralMaxUploadsPerFrame,
            LodPixelError = _terrainProceduralLodPixelError
        };
        settings.Validate();
        return settings;
    }

    private static void DrawProceduralTerrainInspector(ProceduralTerrainAsset procedural)
    {
        ProceduralTerrainSettings settings = procedural.Settings;
        ImGui.SeparatorText("Procedural world");
        ImGui.TextUnformatted($"Seed: {settings.Seed}");
        ImGui.TextUnformatted($"World size: {settings.WorldSizeMeters / 1000.0:0.##} km");
        ImGui.TextUnformatted($"Tile: {settings.TileSizeMeters:0.##} m ({settings.TileResolution} samples)");
        ImGui.TextUnformatted($"Preview radius: {settings.PreviewTileRadius} tile(s)");
        ImGui.TextUnformatted($"Streaming radius: {settings.StreamingTileRadius} tile(s)");
        ImGui.TextUnformatted($"Collision radius: {settings.CollisionTileRadius} tile(s)");
        ImGui.TextUnformatted($"Resident budget: {settings.MaxResidentTiles} tile(s)");
        ImGui.TextUnformatted($"Modified tiles: {procedural.ModifiedTileCount}");
        ImGui.TextDisabled(
            "This asset stores a recipe and sparse sculpt deltas. The editor " +
            "shows only the local preview; the game streams tiles around the player.");
    }

    private void OpenTerrainHeightmapPicker()
    {
        _terrainCreateWaitingForHeightmap = true;
        ImGui.CloseCurrentPopup();
        _showAssetBrowser = true;
        _assetBrowser.OpenTexturePicker(selectedPath =>
        {
            _terrainHeightmapPath = selectedPath;
            _terrainCreateWaitingForHeightmap = false;
        });
    }

    private void DrawTerrainSculptViewportToolbar(string title)
    {
        if (!string.Equals(title, "Camera 3D", StringComparison.Ordinal) ||
            _currentMode != EditorMode.TerrainSculpt ||
            _selectedObject?.IsTerrain != true)
            return;

        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(6.0f, 2.0f));
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(5.0f, 3.0f));
        if (ImGui.BeginChild(
                "TerrainSculptViewportToolbar",
                new Vector2(0.0f, 38.0f),
                ImGuiChildFlags.Borders,
                ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextDisabled("Terrain");
            ImGui.SameLine();

            ImGui.SetNextItemWidth(145.0f);
            DrawTerrainSculptToolCombo("Tool##terrainSculptToolViewport");
            ImGui.SameLine();

            ImGui.SetNextItemWidth(175.0f);
            ImGui.SliderFloat(
                "Radius##terrainBrushRadiusViewport",
                ref _terrainBrushRadius,
                0.1f,
                1000.0f,
                "Radius %.2f");
            ImGui.SameLine();

            ImGui.SetNextItemWidth(175.0f);
            ImGui.SliderFloat(
                "Strength##terrainBrushStrengthViewport",
                ref _terrainBrushStrength,
                0.01f,
                100.0f,
                "Strength %.2f");
        }

        ImGui.EndChild();
        ImGui.PopStyleVar(2);
    }

    private void DrawTerrainSculptToolCombo(string label)
    {
        int selectedTool = Math.Clamp(
            (int)_terrainSculptTool,
            0,
            TerrainSculptToolLabels.Length - 1);
        if (ImGui.Combo(label, ref selectedTool, TerrainSculptToolLabels, TerrainSculptToolLabels.Length))
        {
            _terrainSculptTool = (TerrainSculptTool)selectedTool;
        }
    }

    private void DrawTerrainSculptInspectorControls(TerrainAsset terrain)
    {
        DrawTerrainSculptToolCombo("Tool##terrainSculptToolInspector");

        ImGui.SliderFloat(
            "Radius##terrainBrushRadius",
            ref _terrainBrushRadius,
            0.1f,
            1000.0f,
            "%.2f");
        ImGui.SliderFloat(
            "Strength##terrainBrushStrength",
            ref _terrainBrushStrength,
            0.01f,
            100.0f,
            "%.2f");

        if (_terrainSculptTool == TerrainSculptTool.SetHeight)
        {
            float minimumHeight = terrain.HeightOffset;
            float maximumHeight = terrain.HeightOffset + MathF.Max(terrain.HeightScale, 0.001f);
            _terrainSetHeight = Math.Clamp(_terrainSetHeight, minimumHeight, maximumHeight);
            ImGui.SliderFloat(
                "Target height##terrainSetHeight",
                ref _terrainSetHeight,
                minimumHeight,
                maximumHeight,
                "%.2f");
        }
        else if (_terrainSculptTool == TerrainSculptTool.Noise)
        {
            ImGui.SliderFloat(
                "Noise scale##terrainNoiseScale",
                ref _terrainNoiseScale,
                0.01f,
                2.0f,
                "%.3f");
            ImGui.DragInt(
                "Noise seed##terrainNoiseSeed",
                ref _terrainNoiseSeed,
                1.0f,
                -100000,
                100000);
        }

        if (_terrainSculptTool is TerrainSculptTool.RaiseLower or
            TerrainSculptTool.Stamp or
            TerrainSculptTool.Noise)
        {
            ImGui.Checkbox("Lower terrain (Shift also lowers)", ref _terrainSculptLower);
        }
        else if (_terrainSculptTool == TerrainSculptTool.Smooth)
        {
            ImGui.TextDisabled("Smooth averages neighboring height samples.");
        }
        else
        {
            ImGui.TextDisabled("The brush moves the terrain toward the target height.");
        }
    }

    private void DrawTerrainHeightmapBrushSelector(EditorAssetService assetService)
    {
        IReadOnlyList<string> brushPaths = assetService.EnumerateTerrainHeightmapBrushes();
        if (brushPaths.Count == 0)
        {
            _terrainHeightmapBrushPath = "";
            _terrainHeightmapBrushLoadedPath = "";
            _terrainHeightmapBrush = null;
            ImGui.TextDisabled("No heightmap brushes found in Textures/Terrain/heightmap_brushes.");
            return;
        }

        if (!string.IsNullOrWhiteSpace(_terrainHeightmapBrushPath) &&
            !brushPaths.Any(path => path.Equals(
                _terrainHeightmapBrushPath,
                StringComparison.OrdinalIgnoreCase)))
        {
            _terrainHeightmapBrushPath = "";
            _terrainHeightmapBrushLoadedPath = "";
            _terrainHeightmapBrush = null;
        }

        string[] labels = new string[brushPaths.Count + 1];
        labels[0] = "Circular (default)";
        for (int i = 0; i < brushPaths.Count; i++)
            labels[i + 1] = Path.GetFileNameWithoutExtension(brushPaths[i]);

        int selectedIndex = 0;
        if (!string.IsNullOrWhiteSpace(_terrainHeightmapBrushPath))
        {
            for (int i = 0; i < brushPaths.Count; i++)
            {
                if (!brushPaths[i].Equals(
                        _terrainHeightmapBrushPath,
                        StringComparison.OrdinalIgnoreCase))
                    continue;

                selectedIndex = i + 1;
                break;
            }
        }

        if (ImGui.Combo(
                "Heightmap brush##terrainHeightmapBrush",
                ref selectedIndex,
                labels,
                labels.Length))
        {
            _terrainHeightmapBrushPath = selectedIndex == 0
                ? ""
                : brushPaths[selectedIndex - 1];
            _terrainHeightmapBrushLoadedPath = "";
            _terrainHeightmapBrush = null;
        }

        if (!string.Equals(
                _terrainHeightmapBrushLoadedPath,
                _terrainHeightmapBrushPath,
                StringComparison.OrdinalIgnoreCase))
        {
            _terrainHeightmapBrushLoadedPath = _terrainHeightmapBrushPath;
            _terrainHeightmapBrush = string.IsNullOrWhiteSpace(_terrainHeightmapBrushPath)
                ? null
                : assetService.LoadTerrainHeightmapBrush(_terrainHeightmapBrushPath);
        }

        if (_terrainHeightmapBrush == null)
        {
            ImGui.TextDisabled("Circular falloff is active.");
            return;
        }

        uint previewTexture = assetService.RequestTerrainHeightmapBrushPreview(_terrainHeightmapBrushPath);
        if (previewTexture != 0)
        {
            float maxSize = MathF.Min(128.0f, ImGui.GetContentRegionAvail().X);
            float aspect = _terrainHeightmapBrush.Width / (float)_terrainHeightmapBrush.Height;
            Vector2 previewSize = aspect >= 1.0f
                ? new Vector2(maxSize, maxSize / aspect)
                : new Vector2(maxSize * aspect, maxSize);
            ImGui.Image(
                (IntPtr)previewTexture,
                previewSize,
                new Vector2(0.0f, 1.0f),
                new Vector2(1.0f, 0.0f));
        }

        ImGui.TextDisabled($"{_terrainHeightmapBrush.Width} x {_terrainHeightmapBrush.Height} • white raises, black has no effect");
    }

    private void BeginTerrainSculpt(
        EditorSceneService sceneService,
        EditorAssetService assetService)
    {
        if (_terrainSculptActive || _selectedObject?.IsTerrain != true)
            return;

        try
        {
            string path = assetService.ResolveEditorAssetPath(_selectedObject.TerrainAssetPath!);
            TerrainTileSetAsset? terrain = sceneService.TryLoadTerrainTileSet(_selectedObject, assetService);
            if (terrain == null)
                throw new InvalidOperationException("The selected terrain asset could not be loaded.");

            _terrainSculptAssetPath = path;
            _terrainSculptAsset = terrain;
            _terrainSculptBefore = terrain.CaptureSnapshot();
            _terrainSculptActive = true;
        }
        catch (Exception ex)
        {
            Logger.Warn($"Could not begin terrain sculpt: {ex.Message}");
            _terrainSculptAssetPath = "";
            _terrainSculptAsset = null;
            _terrainSculptBefore = null;
            _terrainSculptActive = false;
        }
    }

    private void EndTerrainSculpt(
        EditorSceneService sceneService,
        EditorAssetService assetService,
        CommandHistory history)
    {
        if (!_terrainSculptActive)
            return;

        try
        {
            TerrainTileSetAsset? terrain = _terrainSculptAsset;
            if (terrain == null)
                throw new InvalidOperationException("The active terrain sculpt has no loaded terrain asset.");

            TerrainTileSetSnapshot after = terrain.CaptureSnapshot();
            if (_terrainSculptBefore != null &&
                !_terrainSculptBefore.ContentEquals(after))
            {
                if (!sceneService.SaveTerrainAsset(_terrainSculptAssetPath))
                    throw new IOException("The terrain asset could not be saved.");

                sceneService.MarkModified(sceneService.Document.Serialize());
                history.PushCommand(new TerrainSnapshotCommand(
                    sceneService,
                    assetService,
                    _terrainSculptAssetPath,
                    _terrainSculptBefore,
                    after));
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"Could not finish terrain sculpt: {ex.Message}");
        }
        finally
        {
            _terrainSculptActive = false;
            _terrainSculptAssetPath = "";
            _terrainSculptAsset = null;
            _terrainSculptBefore = null;
        }
    }

    private void AddNewObject(EditorSceneService sceneService, EditorAssetService assetService, CommandHistory history, MapShapeType shape)
    {
        var pre = sceneService.Document.Serialize();
        var doc = sceneService.Document;

        MapObject obj;
        obj = new MapObject
        {
            Id = $"new_{shape.ToString().ToLower()}",
            Visible = true,
            Mesh = shape == MapShapeType.Sphere ? "sphere" : "cube",
            MaterialPath = DefaultMaterialPath,
            Body = new MapBody
            {
                Shape = shape,
                Position = new Vector3(0, 1, 0),
                Rotation = Quaternion.Identity,
                Mass = 0,
                Friction = 0.5f,
                Restitution = 0.0f
            }
        };

        if (shape == MapShapeType.Box) obj.Body.HalfExtents = new Vector3(0.5f, 0.5f, 0.5f);
        else if (shape == MapShapeType.Sphere) obj.Body.Radius = 0.5f;
        else if (shape == MapShapeType.Capsule) { obj.Body.Radius = 0.5f; obj.Body.Height = 1.0f; obj.Mesh = "capsule"; }

        doc.Objects.Add(obj);
        SceneNameManager.EnsureAllUnique(doc);
        _selectedObject = obj; // Auto select new object
        _selectedObjects.Clear();
        _selectedObjects.Add(obj);

        var post = sceneService.Document.Serialize();
        sceneService.MarkModified(post);
        history.PushCommand(new SnapshotCommand(sceneService, assetService, pre, post));
        sceneService.PopulateScene(assetService);
    }

    private void AddNewLight(EditorSceneService sceneService, EditorAssetService assetService, CommandHistory history, string lightType)
    {
        var pre = sceneService.Document.Serialize();
        var doc = sceneService.Document;

        var obj = new MapObject
        {
            Id = $"new_{lightType}_light",
            Visible = true,
            LightType = lightType,
            LightColor = lightType == "spot" ? new Vector3(1, 0.9f, 0.7f) : new Vector3(1, 0.3f, 0.2f),
            LightIntensity = 2.0f,
            LightRadius = 15.0f,
            LightInnerCone = float.DegreesToRadians(15),
            LightOuterCone = float.DegreesToRadians(30),
            LightCastShadows = false,
            LightShadowBias = 0.00100f,
            Body = new MapBody
            {
                Shape = MapShapeType.None,
                Position = new Vector3(0, 2, 0),
                Rotation = Quaternion.Identity,
                Mass = 0,
                Friction = 0.5f,
                Restitution = 0.0f
            }
        };

        doc.Objects.Add(obj);
        SceneNameManager.EnsureAllUnique(doc);
        _selectedObject = obj;
        _selectedObjects.Clear();
        _selectedObjects.Add(obj);

        var post = sceneService.Document.Serialize();
        sceneService.MarkModified(post);
        history.PushCommand(new SnapshotCommand(sceneService, assetService, pre, post));
        sceneService.PopulateScene(assetService);
    }

    private static void SyncLight(EditorSceneService sceneService, MapObject obj)
    {
        if (!obj.IsLight || obj.Body == null) return;
        var light = sceneService.Scene.Lights.FirstOrDefault(l => l.Id == obj.Id);
        if (light == null) return;
        light.Type = obj.LightType == "directional" ? LightType.Directional : (obj.LightType == "spot" ? LightType.Spot : LightType.Point);
        light.Position = obj.Body.Position;
        light.Direction = Vector3.Transform(-Vector3.UnitY, obj.Body.Rotation);
        light.Enabled = obj.IsGloballyVisible(sceneService.Document);
        light.Color = obj.LightColor;
        light.Intensity = obj.LightIntensity;
        light.Radius = obj.LightRadius;
        light.InnerConeAngle = obj.LightInnerCone;
        light.OuterConeAngle = obj.LightOuterCone;
        light.CastShadows = obj.LightCastShadows;
        light.ShadowBias = obj.LightShadowBias;
        light.Dynamic = obj.LightDynamic;
    }

    private void CommitBrush(EditorSceneService sceneService, EditorAssetService assetService, CommandHistory history)
    {
        if (!_previewManager.HasPreview) return;

        var pre = sceneService.Document.Serialize();
        var brush = _previewManager.CreateBrush();
        brush.Texture = "Textures/dev_measurecrate01.bmp";
        brush.MaterialPath = DevBrushMaterialPath;

        sceneService.Document.Objects.Add(brush);
        SceneNameManager.EnsureAllUnique(sceneService.Document);
        _selectedObject = brush;
        _selectedObjects.Clear();
        _selectedObjects.Add(brush);
        _currentMode = EditorMode.Select;
        _previewManager.Reset();

        var post = sceneService.Document.Serialize();
        sceneService.MarkModified(post);
        history.PushCommand(new SnapshotCommand(sceneService, assetService, pre, post));
        sceneService.PopulateScene(assetService);
    }

    private void RefreshModelFileList(string fuseResPath)
    {
        _modelFiles.Clear();
        _selectedModelIndex = -1;
        _detectedTexturePath = null;

        string modelsDir = Path.Combine(fuseResPath, "Models");
        if (Directory.Exists(modelsDir))
        {
            var files = Directory.GetFiles(modelsDir, "*.*", SearchOption.AllDirectories)
                                 .Where(f => f.EndsWith(".obj", StringComparison.OrdinalIgnoreCase) || 
                                             f.EndsWith(".gltf", StringComparison.OrdinalIgnoreCase) || 
                                             f.EndsWith(".glb", StringComparison.OrdinalIgnoreCase));
            foreach (var f in files)
            {
                string relPath = Path.GetRelativePath(modelsDir, f).Replace('\\', '/');
                _modelFiles.Add(relPath);
            }
        }
    }

    private unsafe string? FindTextureInModel(string objFullPath, string fuseResPath)
    {
        try
        {
            if (!File.Exists(objFullPath)) return null;

            var api = Silk.NET.Assimp.Assimp.GetApi();
            var scene = api.ImportFile(objFullPath, (uint)Silk.NET.Assimp.PostProcessSteps.None);
            
            if (scene == null || scene->MRootNode == null) 
            {
                Logger.Error($"Failed to parse model for textures: {objFullPath}");
                return null;
            }

            Silk.NET.Assimp.TextureType[] texTypes = new[] {
                Silk.NET.Assimp.TextureType.BaseColor,
                Silk.NET.Assimp.TextureType.Diffuse,
                Silk.NET.Assimp.TextureType.Unknown
            };

            for (int i = 0; i < scene->MNumMaterials; i++)
            {
                var mat = scene->MMaterials[i];
                Silk.NET.Assimp.AssimpString path;

                foreach (var type in texTypes)
                {
                    if (api.GetMaterialTexture(mat, type, 0, &path, null, null, null, null, null, null) == Silk.NET.Assimp.Return.Success)
                    {
                        string texPath = path.AsString;
                        if (string.IsNullOrEmpty(texPath)) continue;

                        if (texPath.StartsWith("*"))
                        {
                            if (int.TryParse(texPath.Substring(1), out int texIndex) && texIndex >= 0 && texIndex < scene->MNumTextures)
                            {
                                var embeddedTex = scene->MTextures[texIndex];
                                if (embeddedTex->MHeight == 0) // Compressed (PNG/JPG)
                                {
                                    byte[] bytes = new byte[embeddedTex->MWidth];
                                    System.Runtime.InteropServices.Marshal.Copy((IntPtr)embeddedTex->PcData, bytes, 0, (int)embeddedTex->MWidth);
                                    
                                    string baseName = Path.GetFileNameWithoutExtension(objFullPath);
                                    string saveName = $"{baseName}_tex_{texIndex}.png";
                                    string targetTexturePath = Path.Combine(fuseResPath, "Textures", saveName);
                                    
                                    Directory.CreateDirectory(Path.Combine(fuseResPath, "Textures"));
                                    File.WriteAllBytes(targetTexturePath, bytes);
                                    
                                    api.ReleaseImport(scene);
                                    return $"Textures/{saveName}";
                                }
                            }
                        }
                        else
                        {
                            string nameOnly = Path.GetFileName(texPath);
                            string targetTexturePath = Path.Combine(fuseResPath, "Textures", nameOnly);

                            if (File.Exists(targetTexturePath))
                            {
                                api.ReleaseImport(scene);
                                return $"Textures/{nameOnly}";
                            }
                            
                            string relativeToModel = Path.Combine(Path.GetDirectoryName(objFullPath) ?? "", texPath);
                            if (File.Exists(relativeToModel))
                            {
                                Directory.CreateDirectory(Path.Combine(fuseResPath, "Textures"));
                                File.Copy(relativeToModel, targetTexturePath, true);
                                api.ReleaseImport(scene);
                                return $"Textures/{nameOnly}";
                            }

                            string absoluteNameOnly = Path.Combine(Path.GetDirectoryName(objFullPath) ?? "", nameOnly);
                            if (File.Exists(absoluteNameOnly))
                            {
                                Directory.CreateDirectory(Path.Combine(fuseResPath, "Textures"));
                                File.Copy(absoluteNameOnly, targetTexturePath, true);
                                api.ReleaseImport(scene);
                                return $"Textures/{nameOnly}";
                            }
                            
                            Logger.Warn($"Model import: Texture '{nameOnly}' defined in model but not found locally.");
                            api.ReleaseImport(scene);
                            return null;
                        }
                    }
                }
            }

            api.ReleaseImport(scene);
            return null;
        }
        catch (Exception ex)
        {
            Logger.Error($"Error parsing model for texture: {ex.Message}");
            return null;
        }
    }

    private void ImportSingleModel(string filename, string? texturePath, EditorSceneService sceneService, EditorAssetService assetService, CommandHistory history)
    {
        var doc = sceneService.Document;
        var pre = doc.Serialize();

        var obj = new MapObject
        {
            Id = Path.GetFileNameWithoutExtension(filename),
            Visible = true,
            Model = $"Models/{filename}",
            ModelScale = Vector3.One,
            MaterialPath = !string.IsNullOrEmpty(texturePath)
                ? EnsureMaterialForTexture(assetService, texturePath, Path.GetFileNameWithoutExtension(texturePath))
                : DefaultMaterialPath,
            Body = new MapBody
            {
                Shape = MapShapeType.Trimesh,
                Position = new Vector3(0, 1, 0),
                Rotation = Quaternion.Identity,
                Mass = 0,
                Friction = 0.5f,
                Restitution = 0.0f
            }
        };

        if (!string.IsNullOrEmpty(texturePath))
        {
            obj.Texture = texturePath;
        }

        doc.Objects.Add(obj);
        SceneNameManager.EnsureAllUnique(doc);
        
        _selectedObject = obj;
        _selectedObjects.Clear();
        _selectedObjects.Add(obj);

        var post = doc.Serialize();
        sceneService.MarkModified(post);
        history.PushCommand(new SnapshotCommand(sceneService, assetService, pre, post));
        sceneService.PopulateScene(assetService);
    }

    private unsafe void ImportSelectedModel(string filename, string? texturePath, EditorSceneService sceneService, EditorAssetService assetService, CommandHistory history)
    {
        string modelFullPath = Path.Combine(assetService.FuseResPath, "Models", filename);
        if (!File.Exists(modelFullPath)) return;

        try
        {
            var api = Silk.NET.Assimp.Assimp.GetApi();
            var scene = api.ImportFile(modelFullPath, (uint)Silk.NET.Assimp.PostProcessSteps.None);
            
            if (scene == null || scene->MRootNode == null || scene->MNumMeshes == 0) 
            {
                Logger.Error($"Failed to parse model for import: {modelFullPath}");
                if (scene != null) api.ReleaseImport(scene);
                ImportSingleModel(filename, texturePath, sceneService, assetService, history);
                return;
            }

            var doc = sceneService.Document;
            var pre = doc.Serialize();

            string baseName = Path.GetFileNameWithoutExtension(filename);
            var importedObjects = new System.Collections.Generic.List<MapObject>();

            Silk.NET.Assimp.TextureType[] texTypes = new[] {
                Silk.NET.Assimp.TextureType.BaseColor,
                Silk.NET.Assimp.TextureType.Diffuse,
                Silk.NET.Assimp.TextureType.Unknown
            };

            int nodeCounter = 0;
            void ProcessNode(Silk.NET.Assimp.Node* node, string? parentId, System.Numerics.Matrix4x4 parentGlobalMatrix)
            {
                string nodeName = node->MName.AsString;
                if (string.IsNullOrEmpty(nodeName)) nodeName = $"node_{nodeCounter}";
                nodeCounter++;

                // Assimp matrix is row-major but mathematically column-vector. System.Numerics is row-vector.
                // Transposing it converts it correctly so that Translation goes into M41, M42, M43 and Rotation axes are correct.
                var localMat = System.Numerics.Matrix4x4.Transpose(node->MTransformation);
                var globalMat = localMat * parentGlobalMatrix;
                
                System.Numerics.Matrix4x4.Decompose(globalMat, out System.Numerics.Vector3 scale, out System.Numerics.Quaternion rotation, out System.Numerics.Vector3 translation);
                
                // If Decompose fails (e.g. due to negative scale/determinant from GLTF right-handed flips),
                // it wipes the translation to 0,0,0. We MUST manually extract it!
                if (translation == System.Numerics.Vector3.Zero && (globalMat.M41 != 0 || globalMat.M42 != 0 || globalMat.M43 != 0))
                {
                    translation = new System.Numerics.Vector3(globalMat.M41, globalMat.M42, globalMat.M43);
                    scale = System.Numerics.Vector3.One;
                    rotation = System.Numerics.Quaternion.Identity;
                }

                bool hasMeshes = node->MNumMeshes > 0;
                bool hasChildren = node->MNumChildren > 0;
                
                string? currentObjId = parentId;

                // Create a node MapObject if it has meshes or children, or if it is the root
                if (hasChildren || hasMeshes || parentId == null)
                {
                    string id = $"{baseName}_{nodeName}";
                    int dupCount = 1;
                    string orig = id;
                    while (importedObjects.Any(o => o.Id == id) || doc.Objects.Any(o => o.Id == id))
                    {
                        id = $"{orig}_{dupCount++}";
                    }

                    var nodeObj = new MapObject
                    {
                        Id = id,
                        Visible = true,
                        ParentId = parentId,
                        ModelScale = scale,
                        Body = new MapBody
                        {
                            Shape = MapShapeType.None,
                            Position = translation,
                            Rotation = rotation
                        }
                    };
                    importedObjects.Add(nodeObj);
                    currentObjId = id;
                }

                // Create child MapObjects for each mesh referenced by this node
                for (int i = 0; i < node->MNumMeshes; i++)
                {
                    uint meshIndex = node->MMeshes[i];
                    var mesh = scene->MMeshes[meshIndex];
                    
                    string meshName = mesh->MName.AsString;
                    if (string.IsNullOrEmpty(meshName)) meshName = $"mesh_{meshIndex}";
                    
                    string mId = $"{currentObjId}_{meshName}";
                    int dupCount = 1;
                    string orig = mId;
                    while (importedObjects.Any(o => o.Id == mId) || doc.Objects.Any(o => o.Id == mId))
                    {
                        mId = $"{orig}_{dupCount++}";
                    }

                    string? meshTexturePath = null;
                    uint matIdx = mesh->MMaterialIndex;
                    if (matIdx < scene->MNumMaterials)
                    {
                        var material = scene->MMaterials[matIdx];
                        Silk.NET.Assimp.AssimpString path;
                        foreach (var type in texTypes)
                        {
                            if (api.GetMaterialTexture(material, type, 0, &path, null, null, null, null, null, null) == Silk.NET.Assimp.Return.Success)
                            {
                                string texPath = path.AsString;
                                if (string.IsNullOrEmpty(texPath)) continue;

                                if (texPath.StartsWith("*"))
                                {
                                    if (int.TryParse(texPath.Substring(1), out int texIndex) && texIndex >= 0 && texIndex < scene->MNumTextures)
                                    {
                                        var embeddedTex = scene->MTextures[texIndex];
                                        if (embeddedTex->MHeight == 0)
                                        {
                                            byte[] bytes = new byte[embeddedTex->MWidth];
                                            System.Runtime.InteropServices.Marshal.Copy((IntPtr)embeddedTex->PcData, bytes, 0, (int)embeddedTex->MWidth);
                                            string targetDir = Path.Combine(assetService.FuseResPath, "Textures", baseName);
                                            Directory.CreateDirectory(targetDir);
                                            string saveName = $"tex_{texIndex}.png";
                                            string targetTexturePath = Path.Combine(targetDir, saveName);
                                            File.WriteAllBytes(targetTexturePath, bytes);
                                            meshTexturePath = $"Textures/{baseName}/{saveName}";
                                            break;
                                        }
                                    }
                                }
                                else
                                {
                                    string nameOnly = Path.GetFileName(texPath);
                                    string targetDir = Path.Combine(assetService.FuseResPath, "Textures", baseName);
                                    string targetTexturePath = Path.Combine(targetDir, nameOnly);

                                    if (File.Exists(targetTexturePath))
                                    {
                                        meshTexturePath = $"Textures/{baseName}/{nameOnly}";
                                        break;
                                    }
                                    
                                    string relativeToModel = Path.Combine(Path.GetDirectoryName(modelFullPath) ?? "", texPath);
                                    if (File.Exists(relativeToModel))
                                    {
                                        Directory.CreateDirectory(targetDir);
                                        File.Copy(relativeToModel, targetTexturePath, true);
                                        meshTexturePath = $"Textures/{baseName}/{nameOnly}";
                                        break;
                                    }

                                    string absoluteNameOnly = Path.Combine(Path.GetDirectoryName(modelFullPath) ?? "", nameOnly);
                                    if (File.Exists(absoluteNameOnly))
                                    {
                                        Directory.CreateDirectory(targetDir);
                                        File.Copy(absoluteNameOnly, targetTexturePath, true);
                                        meshTexturePath = $"Textures/{baseName}/{nameOnly}";
                                        break;
                                    }
                                }
                            }
                        }
                    }

                    if (string.IsNullOrEmpty(meshTexturePath)) meshTexturePath = texturePath;

                    var meshObj = new MapObject
                    {
                        Id = mId,
                        Visible = true,
                        ParentId = currentObjId,
                        Model = $"Models/{filename}#{meshIndex}",
                        ModelScale = scale,
                        Texture = meshTexturePath,
                        MaterialPath = !string.IsNullOrEmpty(meshTexturePath)
                            ? EnsureMaterialForTexture(assetService, meshTexturePath, Path.GetFileNameWithoutExtension(meshTexturePath))
                            : DefaultMaterialPath,
                        Body = new MapBody
                        {
                            Shape = MapShapeType.Trimesh,
                            Position = translation,
                            Rotation = rotation,
                            Mass = 0,
                            Friction = 0.5f,
                            Restitution = 0.0f
                        }
                    };
                    importedObjects.Add(meshObj);
                }

                for (int i = 0; i < node->MNumChildren; i++)
                {
                    ProcessNode(node->MChildren[i], currentObjId, globalMat);
                }
            }

            // Start recursion from root node
            ProcessNode(scene->MRootNode, null, System.Numerics.Matrix4x4.Identity);

            api.ReleaseImport(scene);

            if (importedObjects.Count == 0)
            {
                ImportSingleModel(filename, texturePath, sceneService, assetService, history);
                return;
            }

            foreach (var obj in importedObjects)
            {
                doc.Objects.Add(obj);
            }
            SceneNameManager.EnsureAllUnique(doc);

            _selectedObjects.Clear();
            _selectedObjects.Add(importedObjects[0]); // Select root node
            _selectedObject = importedObjects[0];

            var post = doc.Serialize();
            sceneService.MarkModified(post);
            history.PushCommand(new SnapshotCommand(sceneService, assetService, pre, post));
            sceneService.PopulateScene(assetService);
        }
        catch (Exception ex)
        {
            Logger.Error($"Error importing model meshes: {ex.Message}");
            ImportSingleModel(filename, texturePath, sceneService, assetService, history);
        }
    }

    private void DrawModelImportDialog(EditorSceneService sceneService, EditorAssetService assetService, CommandHistory history)
    {
        if (!_showModelImportDialog) return;

        ImGui.OpenPopup("Import Model");
        
        bool open = true;
        if (ImGui.BeginPopupModal("Import Model", ref open, ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.Text("Select a model file from the Models directory:");
            ImGui.Separator();

            if (_modelFiles.Count == 0)
            {
                ImGui.TextColored(new Vector4(1, 0, 0, 1), "No models found in Models/ directory.");
            }
            else
            {
                string[] filesArray = _modelFiles.ToArray();
                if (ImGui.ListBox("##ModelsList", ref _selectedModelIndex, filesArray, filesArray.Length, 6))
                {
                    if (_selectedModelIndex >= 0 && _selectedModelIndex < _modelFiles.Count)
                    {
                        string selectedFile = _modelFiles[_selectedModelIndex];
                        string modelFullPath = Path.Combine(assetService.FuseResPath, "Models", selectedFile);
                        _detectedTexturePath = FindTextureInModel(modelFullPath, assetService.FuseResPath);
                    }
                }
            }

            ImGui.Separator();

            if (_selectedModelIndex >= 0 && _selectedModelIndex < _modelFiles.Count)
            {
                ImGui.Text($"Selected: {_modelFiles[_selectedModelIndex]}");
                if (!string.IsNullOrEmpty(_detectedTexturePath))
                {
                    ImGui.TextColored(new Vector4(0, 1, 0, 1), $"Texture found: {_detectedTexturePath}");
                }
                else
                {
                    ImGui.TextColored(new Vector4(1, 1, 0, 1), "No texture associated (or not found in res/Textures).");
                }
            }

            ImGui.Separator();

            ImGui.BeginDisabled(_selectedModelIndex < 0);
            if (ImGui.Button("Import", new Vector2(120, 0)))
            {
                string selectedFile = _modelFiles[_selectedModelIndex];
                ImportSelectedModel(selectedFile, _detectedTexturePath, sceneService, assetService, history);
                _showModelImportDialog = false;
            }
            ImGui.EndDisabled();

            ImGui.SameLine();
            if (ImGui.Button("Cancel", new Vector2(120, 0)))
            {
                _showModelImportDialog = false;
            }

            ImGui.EndPopup();
        }

        if (!open)
        {
            _showModelImportDialog = false;
        }
    }

    private void DrawJsonWindow(EditorSceneService sceneService)
    {
        ImGui.SetNextWindowSize(new Vector2(450, 500), ImGuiCond.FirstUseEver);

        if (!ImGui.Begin("Raw JSON", ImGuiWindowFlags.NoCollapse))
        {
            ImGui.End();
            return;
        }

        string json = sceneService.CaptureSnapshot();
        ImGui.InputTextMultiline("##json", ref json, (uint)json.Length,
            new Vector2(-1, -1), ImGuiInputTextFlags.ReadOnly);

        ImGui.End();
    }

    private void DrawDiagnosticsWindow(
        EditorSceneService sceneService,
        EditorAssetService assetService,
        CommandHistory history,
        EditorViewport viewport3D,
        EditorViewport viewportTop,
        EditorViewport viewportFront,
        EditorViewport viewportSide)
    {
        ImGui.SetNextWindowSize(new Vector2(430, 420), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("Diagnostics", ref _showDiagnostics))
        {
            ImGui.End();
            return;
        }

        float fps = ImGui.GetIO().Framerate;
        ImGui.TextUnformatted($"Editor: {fps:0.0} FPS ({(fps > 0 ? 1000.0f / fps : 0):0.00} ms)");
        ImGui.TextUnformatted($"Map: {(sceneService.IsDirty ? "modified" : "saved")}");
        ImGui.TextUnformatted($"Objects: {sceneService.Document.Objects.Count}");
        ImGui.TextUnformatted($"Render entities: {sceneService.Scene.Entities.Count}");
        ImGui.TextUnformatted($"Lights: {sceneService.Scene.Lights.Count}");
        ImGui.TextUnformatted($"Materials: {assetService.EnumerateMaterials().Count}");
        ImGui.TextUnformatted($"Textures: {assetService.EnumerateTextures().Count}");
        ImGui.TextUnformatted($"Undo: {(history.CanUndo ? "available" : "empty")} | Redo: {(history.CanRedo ? "available" : "empty")}");

        ImGui.SeparatorText("Viewport rendering");
        DrawViewportDiagnostics("3D", viewport3D);
        DrawViewportDiagnostics("Top", viewportTop);
        DrawViewportDiagnostics("Front", viewportFront);
        DrawViewportDiagnostics("Side", viewportSide);

        if (sceneService.ValidationWarnings.Count > 0)
        {
            ImGui.SeparatorText("Map validation");
            foreach (string warning in sceneService.ValidationWarnings)
                ImGui.BulletText(warning);
        }

        ImGui.End();

        static void DrawViewportDiagnostics(string name, EditorViewport viewport)
        {
            string state = viewport.IsVisibleInUi ? "visible" : "skipped";
            ImGui.TextUnformatted(
                $"{name}: {state}, {viewport.LastRenderMilliseconds:0.00} ms, " +
                $"drawn {viewport.LastVisibleEntityCount}, culled {viewport.LastCulledEntityCount}, debug {viewport.LastVisibleDebugCount}");
        }
    }

    private void DrawOpenDialog(EditorWindow window, EditorSceneService sceneService, EditorAssetService assetService, CommandHistory history)
    {
        if (!_showOpenDialog) return;

        ImGui.OpenPopup("Open Map");

        bool open = true;
        if (ImGui.BeginPopupModal("Open Map", ref open, ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.Text("Select a map file:");
            ImGui.Separator();

            if (_availableMaps.Length == 0)
            {
                ImGui.TextColored(new Vector4(1, 0, 0, 1), "No .bth maps found in res/Maps/.");
            }
            else
            {
                if (ImGui.ListBox("##MapsList", ref _selectedOpenMapIndex, _availableMaps.Select(Path.GetFileName).ToArray(), _availableMaps.Length, 6))
                {
                }
            }

            ImGui.Separator();

            ImGui.BeginDisabled(_selectedOpenMapIndex < 0);
            if (ImGui.Button("Open", new Vector2(120, 0)))
            {
                string selectedPath = _availableMaps[_selectedOpenMapIndex];
                RequestDocumentAction(PendingDocumentAction.Open, sceneService, selectedPath);
                _showOpenDialog = false;
            }
            ImGui.EndDisabled();

            ImGui.SameLine();
            if (ImGui.Button("Cancel", new Vector2(120, 0)))
            {
                _showOpenDialog = false;
            }

            ImGui.EndPopup();
        }

        if (!open)
        {
            _showOpenDialog = false;
        }
    }

    private void DrawSaveAsDialog(EditorWindow window, EditorSceneService sceneService, EditorAssetService assetService, CommandHistory history)
    {
        if (!_showSaveAsDialog) return;

        ImGui.OpenPopup("Save Map As");

        bool open = true;
        if (ImGui.BeginPopupModal("Save Map As", ref open, ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.Text("Enter map filename:");
            ImGui.InputText("##SaveName", ref _saveMapName, 128);

            ImGui.Separator();

            if (ImGui.Button("Save", new Vector2(120, 0)))
            {
                if (TryResolveMapSavePath(_saveMapName, out string fullPath, out string error))
                {
                    if (File.Exists(fullPath) &&
                        !fullPath.Equals(sceneService.MapPath, StringComparison.OrdinalIgnoreCase))
                    {
                        _pendingSavePath = fullPath;
                        _showOverwriteDialog = true;
                    }
                    else
                    {
                        CompleteSaveAs(fullPath, window, sceneService, assetService, history);
                    }
                }
                else
                {
                    ShowDocumentError(error);
                    if (_resumePendingActionAfterSave)
                        CancelPendingDocumentAction();
                }
                _showSaveAsDialog = false;
            }

            ImGui.SameLine();
            if (ImGui.Button("Cancel", new Vector2(120, 0)))
            {
                _showSaveAsDialog = false;
                if (_resumePendingActionAfterSave)
                    CancelPendingDocumentAction();
            }

            ImGui.EndPopup();
        }

        if (!open)
        {
            _showSaveAsDialog = false;
            if (_resumePendingActionAfterSave)
                CancelPendingDocumentAction();
        }
    }

    private void OpenMapDialog()
    {
        string mapsDir = Path.Combine(ResPath.Path, "Maps");
        _availableMaps = Directory.Exists(mapsDir)
            ? Directory.GetFiles(mapsDir, "*.bth").OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).ToArray()
            : [];
        _selectedOpenMapIndex = -1;
        _showOpenDialog = true;
    }

    private void RequestDocumentAction(
        PendingDocumentAction action,
        EditorSceneService sceneService,
        string openPath = "")
    {
        _pendingDocumentAction = action;
        _pendingOpenPath = openPath;
        _executePendingDocumentAction = false;
        if (sceneService.IsDirty)
            _showUnsavedChangesDialog = true;
        else
            _executePendingDocumentAction = true;
    }

    private void ExecutePendingDocumentAction(
        EditorWindow window,
        EditorSceneService sceneService,
        EditorAssetService assetService,
        CommandHistory history)
    {
        _executePendingDocumentAction = false;
        PendingDocumentAction action = _pendingDocumentAction;
        string openPath = _pendingOpenPath;
        _pendingDocumentAction = PendingDocumentAction.None;
        _pendingOpenPath = "";
        _resumePendingActionAfterSave = false;

        switch (action)
        {
            case PendingDocumentAction.New:
                assetService.ClearBrushMeshes();
                sceneService.SetDocument(new MapDocument
                {
                    PlayerSpawn = new MapPlayerSpawn
                    {
                        Position = Vector3.Zero,
                        Yaw = 0,
                        Pitch = 0
                    }
                });
                sceneService.SetMapPath("");
                sceneService.PopulateScene(assetService);
                history.Clear();
                Undo.Reset();
                _selectedObject = null;
                _selectedObjects.Clear();
                _newDocumentRequested = true;
                break;

            case PendingDocumentAction.Open:
                if (!sceneService.TryOpenMap(openPath, out string error))
                {
                    ShowDocumentError(error);
                    break;
                }
                assetService.ClearBrushMeshes();
                sceneService.PopulateScene(assetService);
                history.Clear();
                Undo.Reset();
                _selectedObject = null;
                _selectedObjects.Clear();
                break;

            case PendingDocumentAction.Exit:
                window.Close();
                break;
        }
    }

    private void DrawUnsavedChangesDialog(
        EditorWindow window,
        EditorSceneService sceneService,
        EditorAssetService assetService,
        CommandHistory history)
    {
        if (!_showUnsavedChangesDialog)
            return;

        ImGui.OpenPopup("Unsaved Changes");
        bool open = true;
        if (ImGui.BeginPopupModal("Unsaved Changes", ref open, ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.TextUnformatted("The current map contains unsaved changes.");
            ImGui.TextUnformatted("Save them before continuing?");
            ImGui.Separator();

            if (ImGui.Button("Save", new Vector2(110, 0)))
            {
                if (string.IsNullOrEmpty(sceneService.MapPath))
                {
                    _showUnsavedChangesDialog = false;
                    _showSaveAsDialog = true;
                    _saveMapName = "map.bth";
                    _resumePendingActionAfterSave = true;
                }
                else if (sceneService.SaveMap())
                {
                    _showUnsavedChangesDialog = false;
                    _executePendingDocumentAction = true;
                }
                else
                {
                    ShowDocumentError(sceneService.LastError);
                }
                ImGui.CloseCurrentPopup();
            }

            ImGui.SameLine();
            if (ImGui.Button("Discard", new Vector2(110, 0)))
            {
                _showUnsavedChangesDialog = false;
                _executePendingDocumentAction = true;
                ImGui.CloseCurrentPopup();
            }

            ImGui.SameLine();
            if (ImGui.Button("Cancel", new Vector2(110, 0)))
            {
                _showUnsavedChangesDialog = false;
                CancelPendingDocumentAction();
                ImGui.CloseCurrentPopup();
            }

            ImGui.EndPopup();
        }

        if (!open)
        {
            _showUnsavedChangesDialog = false;
            CancelPendingDocumentAction();
        }
    }

    private void DrawOverwriteDialog(
        EditorWindow window,
        EditorSceneService sceneService,
        EditorAssetService assetService,
        CommandHistory history)
    {
        if (!_showOverwriteDialog)
            return;

        ImGui.OpenPopup("Overwrite Map");
        bool open = true;
        if (ImGui.BeginPopupModal("Overwrite Map", ref open, ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.TextWrapped($"'{Path.GetFileName(_pendingSavePath)}' already exists. Replace it?");
            ImGui.Separator();
            if (ImGui.Button("Overwrite", new Vector2(120, 0)))
            {
                string path = _pendingSavePath;
                _pendingSavePath = "";
                _showOverwriteDialog = false;
                CompleteSaveAs(path, window, sceneService, assetService, history);
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if (ImGui.Button("Cancel", new Vector2(120, 0)))
            {
                _pendingSavePath = "";
                _showOverwriteDialog = false;
                if (_resumePendingActionAfterSave)
                    CancelPendingDocumentAction();
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndPopup();
        }

        if (!open)
        {
            _showOverwriteDialog = false;
            _pendingSavePath = "";
            if (_resumePendingActionAfterSave)
                CancelPendingDocumentAction();
        }
    }

    private void CompleteSaveAs(
        string fullPath,
        EditorWindow window,
        EditorSceneService sceneService,
        EditorAssetService assetService,
        CommandHistory history)
    {
        if (!sceneService.SaveMapAs(fullPath))
        {
            ShowDocumentError(sceneService.LastError);
            if (_resumePendingActionAfterSave)
                CancelPendingDocumentAction();
            return;
        }

        if (_resumePendingActionAfterSave)
            _executePendingDocumentAction = true;
        _resumePendingActionAfterSave = false;
    }

    private static bool TryResolveMapSavePath(string requestedName, out string fullPath, out string error)
    {
        fullPath = "";
        error = "";
        string name = requestedName.Trim();
        if (string.IsNullOrEmpty(name))
        {
            error = "Enter a map filename.";
            return false;
        }
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            !Path.GetFileName(name).Equals(name, StringComparison.Ordinal))
        {
            error = "Use a filename only; folders and invalid filename characters are not allowed.";
            return false;
        }
        if (!name.EndsWith(".bth", StringComparison.OrdinalIgnoreCase))
            name += ".bth";

        string mapsDirectory = Path.GetFullPath(Path.Combine(ResPath.Path, "Maps"));
        string candidate = Path.GetFullPath(Path.Combine(mapsDirectory, name));
        if (!Path.GetDirectoryName(candidate)!.Equals(mapsDirectory, StringComparison.OrdinalIgnoreCase))
        {
            error = "The map must be saved inside the Maps directory.";
            return false;
        }

        fullPath = candidate;
        return true;
    }

    private void CancelPendingDocumentAction()
    {
        _pendingDocumentAction = PendingDocumentAction.None;
        _pendingOpenPath = "";
        _executePendingDocumentAction = false;
        _resumePendingActionAfterSave = false;
    }

    private void ShowDocumentError(string message)
    {
        _documentError = string.IsNullOrWhiteSpace(message) ? "The operation could not be completed." : message;
    }

    private void DrawDocumentErrorDialog()
    {
        if (string.IsNullOrEmpty(_documentError))
            return;

        ImGui.OpenPopup("Document Error");
        bool open = true;
        if (ImGui.BeginPopupModal("Document Error", ref open, ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.PushTextWrapPos(520);
            ImGui.TextWrapped(_documentError);
            ImGui.PopTextWrapPos();
            ImGui.Separator();
            if (ImGui.Button("OK", new Vector2(100, 0)))
            {
                _documentError = "";
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndPopup();
        }
        if (!open)
            _documentError = "";
    }

    private void DrawHollowDialog(EditorSceneService sceneService, EditorAssetService assetService, CommandHistory history)
    {
        if (_showHollowDialog)
        {
            ImGui.OpenPopup("Make Hollow");
            _showHollowDialog = false;
        }

        bool open = true;
        if (ImGui.BeginPopupModal("Make Hollow", ref open, ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.Text("Enter wall thickness:");
            ImGui.InputFloat("##Thickness", ref _hollowThickness, 0.1f, 1.0f);

            if (ImGui.Button("Apply", new Vector2(120, 0)))
            {
                PerformCsgOperation(sceneService, assetService, history, "Hollow");
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if (ImGui.Button("Cancel", new Vector2(120, 0)))
            {
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndPopup();
        }
    }

    private void PerformCsgOperation(EditorSceneService sceneService, EditorAssetService assetService, CommandHistory history, string op)
    {
        var targetBrushes = _selectedObjects.OfType<Brush>().ToList();
        if (targetBrushes.Count == 0) return;
        if (targetBrushes.Any(brush => !brush.SupportsPlaneCsg))
        {
            ShowDocumentError("CSG funciona somente com brushes convexos. O brush selecionado foi convertido para malha editável e precisa ser simplificado ou usado fora do fluxo CSG.");
            return;
        }

        string pre = sceneService.Document.Serialize();
        bool changed = false;

        if (op == "Subtract" && targetBrushes.Count >= 2)
        {
            var tool = targetBrushes.Last();
            targetBrushes.RemoveAt(targetBrushes.Count - 1);
            
            foreach (var target in targetBrushes)
            {
                var resultBrushes = CSGOperations.Subtract(target, tool);
                sceneService.Document.Objects.Remove(target);
                sceneService.Document.Objects.AddRange(resultBrushes);
                assetService.InvalidateMesh(target.Id);
                _selectedObjects.Remove(target);
            }
            changed = true;
        }
        else if (op == "Intersect" && targetBrushes.Count >= 2)
        {
            var brushA = targetBrushes[0];
            for (int i = 1; i < targetBrushes.Count; i++)
            {
                var result = CSGOperations.Intersect(brushA, targetBrushes[i]);
                if (result != null)
                {
                    brushA = result;
                }
            }
            foreach (var b in targetBrushes)
            {
                sceneService.Document.Objects.Remove(b);
                assetService.InvalidateMesh(b.Id);
                _selectedObjects.Remove(b);
            }
            sceneService.Document.Objects.Add(brushA);
            changed = true;
        }
        else if (op == "Union" && targetBrushes.Count >= 2)
        {
            var tool = targetBrushes.Last();
            targetBrushes.RemoveAt(targetBrushes.Count - 1);
            
            foreach (var target in targetBrushes)
            {
                var resultBrushes = CSGOperations.Union(target, tool);
                sceneService.Document.Objects.Remove(target);
                sceneService.Document.Objects.Remove(tool);
                sceneService.Document.Objects.AddRange(resultBrushes);
                
                assetService.InvalidateMesh(target.Id);
                assetService.InvalidateMesh(tool.Id);
                
                _selectedObjects.Remove(target);
            }
            changed = true;
        }
        else if (op == "Hollow" && targetBrushes.Count >= 1)
        {
            foreach (var target in targetBrushes)
            {
                var resultBrushes = CSGOperations.Hollow(target, _hollowThickness);
                if (resultBrushes.Count > 0)
                {
                    sceneService.Document.Objects.Remove(target);
                    sceneService.Document.Objects.AddRange(resultBrushes);
                    assetService.InvalidateMesh(target.Id);
                    _selectedObjects.Remove(target);
                    changed = true;
                }
            }
        }

        if (changed)
        {
            sceneService.PopulateScene(assetService);
            string post = sceneService.Document.Serialize();
            sceneService.MarkModified(post);
            history.PushCommand(new SnapshotCommand(sceneService, assetService, pre, post));
        }
    }

    private void DrawViewportOverlays(
        EditorViewport viewport,
        Vector2 vpPos,
        Vector2 vpSize,
        EditorSceneService sceneService,
        EditorAssetService assetService)
    {
        if (vpSize.X < 8.0f || vpSize.Y < 8.0f)
            return;

        ImDrawListPtr drawList = ImGui.GetWindowDrawList();
        DrawWorldAxes(drawList, viewport, vpPos, vpSize);
        DrawSelectionHighlight(drawList, viewport, vpPos, vpSize, sceneService, assetService);
        DrawBrushComponentOverlay(drawList, viewport, vpPos, vpSize);
        DrawTerrainNeighborPreview(drawList, viewport, vpPos, vpSize, sceneService, assetService);
        DrawTerrainBrushPreview(drawList, viewport, vpPos, vpSize, sceneService, assetService);
        DrawOrientationWidget(drawList, viewport, vpPos, vpSize);
    }

    private void DrawTerrainNeighborPreview(
        ImDrawListPtr drawList,
        EditorViewport viewport,
        Vector2 vpPos,
        Vector2 vpSize,
        EditorSceneService sceneService,
        EditorAssetService assetService)
    {
        if (!_terrainNeighborEditMode ||
            _currentMode != EditorMode.Select ||
            viewport.Camera.ViewType != CameraViewType.Perspective3D ||
            _selectedObject?.IsTerrain != true ||
            _selectedObject.Body == null)
            return;

        TerrainTileSetAsset? terrainSet = sceneService.TryLoadTerrainTileSet(_selectedObject, assetService);
        if (terrainSet == null || terrainSet.Procedural != null)
            return;

        Vector3 terrainPosition = _selectedObject.Body.Position;
        Quaternion terrainRotation = _selectedObject.Body.Rotation;
        Vector2 mousePosition = ImGui.GetMousePos();

        foreach ((int tileX, int tileZ) in GetTerrainNeighborPreviewCoordinates(terrainSet))
        {
            if (!TryProjectTerrainNeighborCell(
                    terrainSet,
                    terrainPosition,
                    terrainRotation,
                    tileX,
                    tileZ,
                    viewport,
                    vpPos,
                    vpSize,
                    out Vector2 p00,
                    out Vector2 p10,
                    out Vector2 p11,
                    out Vector2 p01))
                continue;

            bool exists = terrainSet.TryGetTile(tileX, tileZ, out _);
            bool canCreate = !exists && TryFindTerrainNeighborSource(
                terrainSet,
                tileX,
                tileZ,
                out _,
                out _,
                out _,
                out _);
            bool hovered = IsPointInsideTerrainPreviewQuad(mousePosition, p00, p10, p11, p01);

            Vector4 fillColor;
            Vector4 outlineColor;
            if (exists)
            {
                fillColor = new Vector4(0.10f, 0.80f, 0.35f, 0.06f);
                outlineColor = tileX == 0 && tileZ == 0
                    ? new Vector4(1.00f, 0.76f, 0.18f, 0.95f)
                    : new Vector4(0.20f, 1.00f, 0.42f, 0.95f);
            }
            else if (canCreate)
            {
                fillColor = hovered
                    ? new Vector4(0.20f, 0.85f, 1.00f, 0.25f)
                    : new Vector4(0.20f, 0.65f, 1.00f, 0.10f);
                outlineColor = hovered
                    ? new Vector4(0.90f, 0.98f, 1.00f, 1.00f)
                    : new Vector4(0.28f, 0.82f, 1.00f, 0.85f);
            }
            else
            {
                fillColor = new Vector4(0.35f, 0.40f, 0.50f, 0.025f);
                outlineColor = new Vector4(0.48f, 0.53f, 0.62f, 0.42f);
            }

            if (!exists)
                AddTerrainPreviewQuadFilled(drawList, p00, p10, p11, p01, ImGui.GetColorU32(fillColor));

            uint outline = ImGui.GetColorU32(hovered && !exists
                ? new Vector4(0.95f, 0.98f, 1.00f, 1.00f)
                : outlineColor);
            float thickness = hovered ? 3.0f : (exists ? 2.0f : 1.5f);
            drawList.AddLine(p00, p10, outline, thickness);
            drawList.AddLine(p10, p11, outline, thickness);
            drawList.AddLine(p11, p01, outline, thickness);
            drawList.AddLine(p01, p00, outline, thickness);

            if (hovered)
            {
                string message = exists
                    ? (tileX == 0 && tileZ == 0
                        ? "Origin terrain"
                        : $"Click to delete neighbor ({tileX}, {tileZ})")
                    : canCreate
                        ? $"Click to create neighbor ({tileX}, {tileZ})"
                        : "Create a connected tile first";
                drawList.AddText(mousePosition + new Vector2(12.0f, 12.0f), outline, message);
            }
        }
    }

    private bool TryHandleTerrainNeighborClick(
        EditorViewport viewport,
        Vector2 vpPos,
        Vector2 vpSize,
        EditorSceneService sceneService,
        EditorAssetService assetService)
    {
        if (!_terrainNeighborEditMode ||
            _currentMode != EditorMode.Select ||
            viewport.Camera.ViewType != CameraViewType.Perspective3D ||
            _selectedObject?.IsTerrain != true ||
            _selectedObject.Body == null ||
            !ImGui.IsMouseHoveringRect(vpPos, vpPos + vpSize) ||
            !ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            return false;

        TerrainTileSetAsset? terrainSet = sceneService.TryLoadTerrainTileSet(_selectedObject, assetService);
        if (terrainSet == null || terrainSet.Procedural != null)
            return false;

        Vector3 terrainPosition = _selectedObject.Body.Position;
        Quaternion terrainRotation = _selectedObject.Body.Rotation;
        Vector2 mousePosition = ImGui.GetMousePos();
        foreach ((int tileX, int tileZ) in GetTerrainNeighborPreviewCoordinates(terrainSet))
        {
            if (!TryProjectTerrainNeighborCell(
                    terrainSet,
                    terrainPosition,
                    terrainRotation,
                    tileX,
                    tileZ,
                    viewport,
                    vpPos,
                    vpSize,
                    out Vector2 p00,
                    out Vector2 p10,
                    out Vector2 p11,
                    out Vector2 p01) ||
                !IsPointInsideTerrainPreviewQuad(mousePosition, p00, p10, p11, p01))
                continue;

            bool exists = terrainSet.TryGetTile(tileX, tileZ, out _);
            if (exists)
            {
                if (tileX == 0 && tileZ == 0)
                    return false;

                bool deleted = sceneService.DeleteTerrainNeighbor(
                    _selectedObject,
                    assetService,
                    tileX,
                    tileZ);
                if (!deleted)
                {
                    _terrainNeighborStatus = $"Could not delete neighbor ({tileX}, {tileZ}).";
                    return true;
                }

                _terrainNeighborStatus = $"Deleted neighbor ({tileX}, {tileZ}).";
            }
            else
            {
                if (!TryFindTerrainNeighborSource(
                        terrainSet,
                        tileX,
                        tileZ,
                        out int sourceX,
                        out int sourceZ,
                        out int offsetX,
                        out int offsetZ))
                {
                    _terrainNeighborStatus = "That preview needs a connected neighbor first.";
                    return true;
                }

                bool created = sceneService.CreateTerrainNeighbor(
                    _selectedObject,
                    assetService,
                    sourceX,
                    sourceZ,
                    offsetX,
                    offsetZ);
                if (!created)
                {
                    _terrainNeighborStatus = $"Could not create neighbor ({tileX}, {tileZ}).";
                    return true;
                }

                _terrainNeighborSourceX = sourceX;
                _terrainNeighborSourceZ = sourceZ;
                _terrainNeighborStatus = $"Created neighbor ({tileX}, {tileZ}).";
            }

            sceneService.PopulateScene(assetService);
            sceneService.MarkModified(sceneService.Document.Serialize());
            viewport.RequestRender();
            return true;
        }

        return false;
    }

    private static List<(int X, int Z)> GetTerrainNeighborPreviewCoordinates(TerrainTileSetAsset terrainSet)
    {
        var coordinates = new HashSet<(int X, int Z)>();
        foreach (TerrainTile tile in terrainSet.Tiles)
        {
            for (int z = tile.Z - 1; z <= tile.Z + 1; z++)
            {
                for (int x = tile.X - 1; x <= tile.X + 1; x++)
                    coordinates.Add((x, z));
            }
        }

        return coordinates
            .OrderBy(coordinate => coordinate.Z)
            .ThenBy(coordinate => coordinate.X)
            .ToList();
    }

    private static bool TryFindTerrainNeighborSource(
        TerrainTileSetAsset terrainSet,
        int targetX,
        int targetZ,
        out int sourceX,
        out int sourceZ,
        out int offsetX,
        out int offsetZ)
    {
        ReadOnlySpan<int> offsetsX = stackalloc int[] { -1, 1, 0, 0 };
        ReadOnlySpan<int> offsetsZ = stackalloc int[] { 0, 0, -1, 1 };
        for (int i = 0; i < offsetsX.Length; i++)
        {
            int candidateSourceX = targetX - offsetsX[i];
            int candidateSourceZ = targetZ - offsetsZ[i];
            if (!terrainSet.TryGetTile(candidateSourceX, candidateSourceZ, out _))
                continue;

            sourceX = candidateSourceX;
            sourceZ = candidateSourceZ;
            offsetX = offsetsX[i];
            offsetZ = offsetsZ[i];
            return true;
        }

        sourceX = 0;
        sourceZ = 0;
        offsetX = 0;
        offsetZ = 0;
        return false;
    }

    private static bool TryProjectTerrainNeighborCell(
        TerrainTileSetAsset terrainSet,
        Vector3 terrainPosition,
        Quaternion terrainRotation,
        int tileX,
        int tileZ,
        EditorViewport viewport,
        Vector2 vpPos,
        Vector2 vpSize,
        out Vector2 p00,
        out Vector2 p10,
        out Vector2 p11,
        out Vector2 p01)
    {
        p00 = Vector2.Zero;
        p10 = Vector2.Zero;
        p11 = Vector2.Zero;
        p01 = Vector2.Zero;
        float x0 = tileX * terrainSet.TileWorldWidth;
        float x1 = x0 + terrainSet.TileWorldWidth;
        float z0 = tileZ * terrainSet.TileWorldDepth;
        float z1 = z0 + terrainSet.TileWorldDepth;
        const float lift = 0.035f;

        float y00;
        float y10;
        float y11;
        float y01;
        if (terrainSet.TryGetTile(tileX, tileZ, out TerrainTile? tile))
        {
            y00 = tile.Asset.GetHeight(0, 0) + lift;
            y10 = tile.Asset.GetHeight(tile.Asset.Width - 1, 0) + lift;
            y11 = tile.Asset.GetHeight(tile.Asset.Width - 1, tile.Asset.Depth - 1) + lift;
            y01 = tile.Asset.GetHeight(0, tile.Asset.Depth - 1) + lift;
        }
        else
        {
            y00 = GetTerrainNeighborPreviewHeight(terrainSet, tileX, tileZ, 0, 0) + lift;
            y10 = GetTerrainNeighborPreviewHeight(terrainSet, tileX, tileZ, terrainSet.Width - 1, 0) + lift;
            y11 = GetTerrainNeighborPreviewHeight(terrainSet, tileX, tileZ, terrainSet.Width - 1, terrainSet.Depth - 1) + lift;
            y01 = GetTerrainNeighborPreviewHeight(terrainSet, tileX, tileZ, 0, terrainSet.Depth - 1) + lift;
        }

        Vector3 local00 = new(x0, y00, z0);
        Vector3 local10 = new(x1, y10, z0);
        Vector3 local11 = new(x1, y11, z1);
        Vector3 local01 = new(x0, y01, z1);
        return TryWorldToScreenStatic(
            terrainPosition + Vector3.Transform(local00, terrainRotation),
            viewport,
            vpPos,
            vpSize,
            out p00) &&
            TryWorldToScreenStatic(
                terrainPosition + Vector3.Transform(local10, terrainRotation),
                viewport,
                vpPos,
                vpSize,
                out p10) &&
            TryWorldToScreenStatic(
                terrainPosition + Vector3.Transform(local11, terrainRotation),
                viewport,
                vpPos,
                vpSize,
                out p11) &&
            TryWorldToScreenStatic(
                terrainPosition + Vector3.Transform(local01, terrainRotation),
                viewport,
                vpPos,
                vpSize,
                out p01);
    }

    private static float GetTerrainNeighborPreviewHeight(
        TerrainTileSetAsset terrainSet,
        int tileX,
        int tileZ,
        int cornerX,
        int cornerZ)
    {
        if (terrainSet.TryGetTile(tileX - 1, tileZ, out TerrainTile? left))
            return left.Asset.GetHeight(left.Asset.Width - 1, cornerZ);
        if (terrainSet.TryGetTile(tileX + 1, tileZ, out TerrainTile? right))
            return right.Asset.GetHeight(0, cornerZ);
        if (terrainSet.TryGetTile(tileX, tileZ - 1, out TerrainTile? back))
            return back.Asset.GetHeight(cornerX, back.Asset.Depth - 1);
        if (terrainSet.TryGetTile(tileX, tileZ + 1, out TerrainTile? front))
            return front.Asset.GetHeight(cornerX, 0);

        return terrainSet.Primary.Asset.GetHeight(cornerX, cornerZ);
    }

    private static bool IsPointInsideTerrainPreviewQuad(
        Vector2 point,
        Vector2 p00,
        Vector2 p10,
        Vector2 p11,
        Vector2 p01)
    {
        float c0 = Cross2D(p10 - p00, point - p00);
        float c1 = Cross2D(p11 - p10, point - p10);
        float c2 = Cross2D(p01 - p11, point - p11);
        float c3 = Cross2D(p00 - p01, point - p01);
        const float epsilon = 0.5f;
        bool hasPositive = c0 > epsilon || c1 > epsilon || c2 > epsilon || c3 > epsilon;
        bool hasNegative = c0 < -epsilon || c1 < -epsilon || c2 < -epsilon || c3 < -epsilon;
        return !(hasPositive && hasNegative);
    }

    private static float Cross2D(Vector2 left, Vector2 right) => left.X * right.Y - left.Y * right.X;

    private static void AddTerrainPreviewQuadFilled(
        ImDrawListPtr drawList,
        Vector2 p00,
        Vector2 p10,
        Vector2 p11,
        Vector2 p01,
        uint color)
    {
        Vector2[] points = [p00, p10, p11, p01];
        fixed (Vector2* pointsPtr = points)
            drawList.AddConvexPolyFilled(ref pointsPtr[0], 4, color);
    }

    private static bool TryWorldToScreenStatic(
        Vector3 worldPos,
        EditorViewport viewport,
        Vector2 vpPos,
        Vector2 vpSize,
        out Vector2 screenPos)
    {
        var view = viewport.Camera.ViewMatrix;
        var proj = viewport.Camera.ProjectionMatrix(vpSize.X / vpSize.Y);
        Vector4 clip = Vector4.Transform(new Vector4(worldPos, 1.0f), view * proj);
        if (clip.W <= 0.0001f || !float.IsFinite(clip.W))
        {
            screenPos = Vector2.Zero;
            return false;
        }

        Vector3 ndc = new Vector3(clip.X, clip.Y, clip.Z) / clip.W;
        if (!float.IsFinite(ndc.X) || !float.IsFinite(ndc.Y))
        {
            screenPos = Vector2.Zero;
            return false;
        }

        screenPos = new Vector2(
            vpPos.X + (ndc.X + 1.0f) * 0.5f * vpSize.X,
            vpPos.Y + (1.0f - ndc.Y) * 0.5f * vpSize.Y);
        return true;
    }

    private void DrawTerrainBrushPreview(
        ImDrawListPtr drawList,
        EditorViewport viewport,
        Vector2 vpPos,
        Vector2 vpSize,
        EditorSceneService sceneService,
        EditorAssetService assetService)
    {
        if (_currentMode != EditorMode.TerrainSculpt ||
            _selectedObject?.IsTerrain != true ||
            !ImGui.IsMouseHoveringRect(vpPos, vpPos + vpSize))
            return;

        TerrainTileSetAsset? terrainSet = sceneService.TryLoadTerrainTileSet(_selectedObject, assetService);
        if (terrainSet == null || _selectedObject.Body == null)
            return;

        EditorGizmo.GetMouseRay(
            ImGui.GetMousePos(),
            viewport.Camera.ViewMatrix,
            viewport.Camera.ProjectionMatrix(vpSize.X / vpSize.Y),
            vpPos,
            vpSize,
            out Vector3 rayOrigin,
            out Vector3 rayDirection);

        Vector3 terrainPosition = _selectedObject.Body.Position;
        Quaternion terrainRotation = _selectedObject.Body.Rotation;
        Quaternion inverseRotation = Quaternion.Inverse(terrainRotation);
        Vector3 localOrigin = Vector3.Transform(rayOrigin - terrainPosition, inverseRotation);
        Vector3 localDirection = Vector3.Normalize(Vector3.Transform(rayDirection, inverseRotation));
        if (!terrainSet.Raycast(
                localOrigin,
                localDirection,
                out _,
                out Vector3 localHit,
                out _))
            return;

        if (_terrainHeightmapBrush != null &&
            !string.IsNullOrWhiteSpace(_terrainHeightmapBrushPath))
        {
            uint previewTexture = assetService.RequestTerrainHeightmapBrushPreview(_terrainHeightmapBrushPath);
            if (previewTexture != 0)
            {
                DrawTerrainHeightmapBrushProjection(
                    drawList,
                    viewport,
                    vpPos,
                    vpSize,
                    terrainSet,
                    terrainPosition,
                    terrainRotation,
                    localHit,
                    previewTexture);
            }
        }

        const int segments = 48;
        Span<Vector2> points = stackalloc Vector2[segments];
        Span<bool> visible = stackalloc bool[segments];
        float radius = MathF.Max(0.01f, _terrainBrushRadius);
        for (int i = 0; i < segments; i++)
        {
            float angle = i / (float)segments * MathF.Tau;
            float localX = localHit.X + MathF.Cos(angle) * radius;
            float localZ = localHit.Z + MathF.Sin(angle) * radius;
            if (!TryGetTerrainSurfacePoint(
                    terrainSet,
                    localHit.Y,
                    localX,
                    localZ,
                    0.025f,
                    out Vector3 localPoint))
            {
                visible[i] = false;
                continue;
            }

            Vector3 worldPoint = terrainPosition + Vector3.Transform(localPoint, terrainRotation);
            visible[i] = TryWorldToScreen(worldPoint, viewport, vpPos, vpSize, out points[i]);
        }

        uint outline = ImGui.GetColorU32(new Vector4(0.02f, 0.02f, 0.02f, 0.95f));
        uint brushColor = ImGui.GetColorU32(_terrainSculptLower
            ? new Vector4(0.95f, 0.32f, 0.20f, 0.95f)
            : new Vector4(0.35f, 1.00f, 0.35f, 0.95f));
        for (int i = 0; i < segments; i++)
        {
            int next = (i + 1) % segments;
            if (!visible[i] || !visible[next])
                continue;

            drawList.AddLine(points[i], points[next], outline, 4.0f);
            drawList.AddLine(points[i], points[next], brushColor, 2.0f);
        }

        Vector3 localCenter = new(
            localHit.X,
            localHit.Y,
            localHit.Z);
        if (terrainSet.TryGetTileAt(localHit, out TerrainTile centerTile))
        {
            localCenter.Y = centerTile.Asset.GetInterpolatedHeight(
                localHit.X - terrainSet.GetTileOrigin(centerTile).X,
                localHit.Z - terrainSet.GetTileOrigin(centerTile).Z) + 0.035f;
        }
        Vector3 worldCenter = terrainPosition + Vector3.Transform(localCenter, terrainRotation);
        if (TryWorldToScreen(worldCenter, viewport, vpPos, vpSize, out Vector2 center))
        {
            drawList.AddCircleFilled(center, 3.0f, brushColor, 12);
            drawList.AddCircle(center, 6.0f, outline, 16, 1.5f);
        }
    }

    private void DrawTerrainHeightmapBrushProjection(
        ImDrawListPtr drawList,
        EditorViewport viewport,
        Vector2 vpPos,
        Vector2 vpSize,
        TerrainTileSetAsset terrainSet,
        Vector3 terrainPosition,
        Quaternion terrainRotation,
        Vector3 localHit,
        uint previewTexture)
    {
        const int cells = 20;
        float radius = MathF.Max(0.01f, _terrainBrushRadius);
        uint tint = ImGui.GetColorU32(new Vector4(1.0f, 1.0f, 1.0f, 0.72f));
        IntPtr textureId = (IntPtr)previewTexture;

        for (int z = 0; z < cells; z++)
        {
            float v0 = z / (float)cells;
            float v1 = (z + 1) / (float)cells;
            for (int x = 0; x < cells; x++)
            {
                float u0 = x / (float)cells;
                float u1 = (x + 1) / (float)cells;
                float centerU = (u0 + u1) * 0.5f;
                float centerV = (v0 + v1) * 0.5f;
                float circleX = centerU * 2.0f - 1.0f;
                float circleZ = centerV * 2.0f - 1.0f;
                if (circleX * circleX + circleZ * circleZ > 1.0f)
                    continue;

                Vector3 localP00 = Vector3.Zero;
                Vector3 localP10 = Vector3.Zero;
                Vector3 localP11 = Vector3.Zero;
                Vector3 localP01 = Vector3.Zero;
                bool hasSurface =
                    TryGetTerrainBrushSurfacePoint(terrainSet, localHit, radius, u0, v0, out localP00) &&
                    TryGetTerrainBrushSurfacePoint(terrainSet, localHit, radius, u1, v0, out localP10) &&
                    TryGetTerrainBrushSurfacePoint(terrainSet, localHit, radius, u1, v1, out localP11) &&
                    TryGetTerrainBrushSurfacePoint(terrainSet, localHit, radius, u0, v1, out localP01);
                if (!hasSurface)
                    continue;

                Vector2 p00 = Vector2.Zero;
                Vector2 p10 = Vector2.Zero;
                Vector2 p11 = Vector2.Zero;
                Vector2 p01 = Vector2.Zero;
                bool visible =
                    TryWorldToScreen(
                        terrainPosition + Vector3.Transform(localP00, terrainRotation),
                        viewport,
                        vpPos,
                        vpSize,
                        out p00) &&
                    TryWorldToScreen(
                        terrainPosition + Vector3.Transform(localP10, terrainRotation),
                        viewport,
                        vpPos,
                        vpSize,
                        out p10) &&
                    TryWorldToScreen(
                        terrainPosition + Vector3.Transform(localP11, terrainRotation),
                        viewport,
                        vpPos,
                        vpSize,
                        out p11) &&
                    TryWorldToScreen(
                        terrainPosition + Vector3.Transform(localP01, terrainRotation),
                        viewport,
                        vpPos,
                        vpSize,
                        out p01);
                if (!visible)
                    continue;

                // The texture is uploaded in top-to-bottom image order, while
                // OpenGL UVs are bottom-to-top. Reversing V keeps the sculpt
                // result and the projected preview oriented identically.
                drawList.AddImageQuad(
                    textureId,
                    p00,
                    p10,
                    p11,
                    p01,
                    new Vector2(u0, 1.0f - v0),
                    new Vector2(u1, 1.0f - v0),
                    new Vector2(u1, 1.0f - v1),
                    new Vector2(u0, 1.0f - v1),
                    tint);
            }
        }
    }

    private static bool TryGetTerrainSurfacePoint(
        TerrainTileSetAsset terrainSet,
        float referenceY,
        float localX,
        float localZ,
        float lift,
        out Vector3 surfacePoint)
    {
        if (!terrainSet.TryGetTileAt(new Vector3(localX, referenceY, localZ), out TerrainTile tile))
        {
            surfacePoint = default;
            return false;
        }

        Vector3 tileOrigin = terrainSet.GetTileOrigin(tile);
        surfacePoint = new Vector3(
            localX,
            tile.Asset.GetInterpolatedHeight(localX - tileOrigin.X, localZ - tileOrigin.Z) + lift,
            localZ);
        return true;
    }

    private static bool TryGetTerrainBrushSurfacePoint(
        TerrainTileSetAsset terrainSet,
        Vector3 localHit,
        float radius,
        float u,
        float v,
        out Vector3 surfacePoint)
    {
        float localX = localHit.X + (u * 2.0f - 1.0f) * radius;
        float localZ = localHit.Z + (v * 2.0f - 1.0f) * radius;
        if (!terrainSet.TryGetTileAt(new Vector3(localX, localHit.Y, localZ), out TerrainTile tile))
        {
            surfacePoint = default;
            return false;
        }

        Vector3 tileOrigin = terrainSet.GetTileOrigin(tile);
        surfacePoint = new Vector3(
            localX,
            tile.Asset.GetInterpolatedHeight(localX - tileOrigin.X, localZ - tileOrigin.Z) + 0.04f,
            localZ);
        return true;
    }

    private void DrawBrushComponentOverlay(ImDrawListPtr drawList, EditorViewport viewport, Vector2 vpPos, Vector2 vpSize)
    {
        Brush? brush = ActiveEditableBrush;
        if (!IsEditingBrushComponents || brush?.EditableMesh == null || brush.Body == null)
            return;

        EditableBrushMesh topology = brush.EditableMesh;
        uint edgeColor = ImGui.GetColorU32(new Vector4(0.34f, 0.78f, 1.0f, 0.52f));
        uint selectedColor = ImGui.GetColorU32(new Vector4(1.0f, 0.58f, 0.08f, 1.0f));
        uint vertexColor = ImGui.GetColorU32(new Vector4(0.72f, 0.88f, 1.0f, 0.95f));

        Vector3 ToWorld(Vector3 local) => brush.Body.Position + Vector3.Transform(local, brush.Body.Rotation);
        foreach (EditableBrushEdge edge in topology.GetEdges())
        {
            if (!TryWorldToScreen(ToWorld(topology.GetPosition(edge.A)), viewport, vpPos, vpSize, out Vector2 first) ||
                !TryWorldToScreen(ToWorld(topology.GetPosition(edge.B)), viewport, vpPos, vpSize, out Vector2 second))
                continue;
            bool selected = _selectedBrushEdges.Contains(edge) ||
                (_brushComponentMode == BrushComponentMode.Vertex && (_selectedBrushVertices.Contains(edge.A) || _selectedBrushVertices.Contains(edge.B)));
            drawList.AddLine(first, second, selected ? selectedColor : edgeColor, selected ? 3.0f : 1.2f);
        }

        if (_brushComponentMode == BrushComponentMode.Face)
        {
            foreach (EditableBrushFace face in topology.Faces)
            {
                if (!_selectedBrushFaces.Contains(face.Id))
                    continue;
                for (int index = 0; index < face.Vertices.Count; index++)
                {
                    Vector3 firstLocal = topology.GetPosition(face.Vertices[index]);
                    Vector3 secondLocal = topology.GetPosition(face.Vertices[(index + 1) % face.Vertices.Count]);
                    if (TryWorldToScreen(ToWorld(firstLocal), viewport, vpPos, vpSize, out Vector2 first) &&
                        TryWorldToScreen(ToWorld(secondLocal), viewport, vpPos, vpSize, out Vector2 second))
                    {
                        drawList.AddLine(first, second, selectedColor, 4.0f);
                    }
                }
            }
        }

        foreach (EditableBrushVertex vertex in topology.Vertices)
        {
            if (!TryWorldToScreen(ToWorld(vertex.Position), viewport, vpPos, vpSize, out Vector2 position))
                continue;
            bool selected = _selectedBrushVertices.Contains(vertex.Id) ||
                (_brushComponentMode == BrushComponentMode.Edge && _selectedBrushEdges.Any(edge => edge.Contains(vertex.Id))) ||
                (_brushComponentMode == BrushComponentMode.Face && topology.Faces.Any(face => _selectedBrushFaces.Contains(face.Id) && face.Vertices.Contains(vertex.Id)));
            drawList.AddCircleFilled(position, selected ? 5.2f : 3.6f, selected ? selectedColor : vertexColor, 12);
            drawList.AddCircle(position, selected ? 6.5f : 4.7f, ImGui.GetColorU32(new Vector4(0.02f, 0.03f, 0.05f, 0.95f)), 12, 1.0f);
        }
    }

    private void DrawWorldAxes(ImDrawListPtr drawList, EditorViewport viewport, Vector2 vpPos, Vector2 vpSize)
    {
        if (!TryWorldToScreen(Vector3.Zero, viewport, vpPos, vpSize, out Vector2 origin))
            return;

        float axisLength = viewport.Camera.IsOrthographic
            ? MathF.Max(viewport.Camera.OrthoSize * 0.16f, 1.0f)
            : float.Clamp(Vector3.Distance(viewport.Camera.Position, Vector3.Zero) * 0.12f, 1.0f, 8.0f);

        Vector3[] axes = [Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ];
        string[] labels = ["X", "Y", "Z"];
        Vector4[] colors =
        [
            new Vector4(0.95f, 0.20f, 0.20f, 0.95f),
            new Vector4(0.25f, 0.90f, 0.35f, 0.95f),
            new Vector4(0.25f, 0.55f, 1.00f, 0.95f)
        ];

        uint originColor = ImGui.GetColorU32(new Vector4(1.0f, 0.78f, 0.18f, 0.95f));
        drawList.AddCircleFilled(origin, 4.5f, originColor, 16);
        drawList.AddCircle(origin, 7.0f, ImGui.GetColorU32(new Vector4(0.05f, 0.05f, 0.05f, 0.85f)), 16, 1.5f);

        for (int i = 0; i < axes.Length; i++)
        {
            if (!TryWorldToScreen(axes[i] * axisLength, viewport, vpPos, vpSize, out Vector2 endpoint))
                continue;

            uint color = ImGui.GetColorU32(colors[i]);
            drawList.AddLine(origin, endpoint, ImGui.GetColorU32(new Vector4(0.02f, 0.02f, 0.02f, 0.9f)), 4.0f);
            drawList.AddLine(origin, endpoint, color, 2.0f);
            drawList.AddText(endpoint + new Vector2(4.0f, -8.0f), color, labels[i]);
        }

        drawList.AddText(origin + new Vector2(6.0f, 5.0f), originColor, "O");
    }

    private void DrawSelectionHighlight(
        ImDrawListPtr drawList,
        EditorViewport viewport,
        Vector2 vpPos,
        Vector2 vpSize,
        EditorSceneService sceneService,
        EditorAssetService assetService)
    {
        if (_currentMode != EditorMode.Select || _selectedObjects.Count == 0)
            return;

        if (GetSelectionAABB(sceneService, assetService, out Vector3 min, out Vector3 max))
        {
            Vector3[] corners =
            [
                new Vector3(min.X, min.Y, min.Z), new Vector3(max.X, min.Y, min.Z),
                new Vector3(min.X, max.Y, min.Z), new Vector3(max.X, max.Y, min.Z),
                new Vector3(min.X, min.Y, max.Z), new Vector3(max.X, min.Y, max.Z),
                new Vector3(min.X, max.Y, max.Z), new Vector3(max.X, max.Y, max.Z)
            ];

            Vector2 screenMin = new(float.MaxValue);
            Vector2 screenMax = new(float.MinValue);
            bool hasPoint = false;
            foreach (Vector3 corner in corners)
            {
                if (!TryWorldToScreen(corner, viewport, vpPos, vpSize, out Vector2 point))
                    continue;

                screenMin = Vector2.Min(screenMin, point);
                screenMax = Vector2.Max(screenMax, point);
                hasPoint = true;
            }

            if (hasPoint && screenMax.X - screenMin.X > 1.0f && screenMax.Y - screenMin.Y > 1.0f)
            {
                screenMin -= new Vector2(3.0f);
                screenMax += new Vector2(3.0f);
                uint outline = ImGui.GetColorU32(new Vector4(0.02f, 0.02f, 0.02f, 0.9f));
                uint selected = ImGui.GetColorU32(new Vector4(1.0f, 0.62f, 0.08f, 1.0f));
                drawList.AddRect(screenMin, screenMax, outline, 3.0f, ImDrawFlags.None, 4.0f);
                drawList.AddRect(screenMin, screenMax, selected, 3.0f, ImDrawFlags.None, 2.0f);

                // Corner brackets make the active selection readable over bright
                // materials without filling the selected object with a tint.
                float bracket = MathF.Min(14.0f, MathF.Min(screenMax.X - screenMin.X, screenMax.Y - screenMin.Y) * 0.25f);
                foreach ((Vector2 a, Vector2 b, Vector2 c, Vector2 d) in new[]
                {
                    (screenMin, screenMin + new Vector2(bracket, 0), screenMin, screenMin + new Vector2(0, bracket)),
                    (new Vector2(screenMax.X, screenMin.Y), new Vector2(screenMax.X - bracket, screenMin.Y), new Vector2(screenMax.X, screenMin.Y), new Vector2(screenMax.X, screenMin.Y + bracket)),
                    (new Vector2(screenMin.X, screenMax.Y), new Vector2(screenMin.X + bracket, screenMax.Y), new Vector2(screenMin.X, screenMax.Y), new Vector2(screenMin.X, screenMax.Y - bracket)),
                    (screenMax, new Vector2(screenMax.X - bracket, screenMax.Y), screenMax, new Vector2(screenMax.X, screenMax.Y - bracket))
                })
                {
                    drawList.AddLine(a, b, selected, 3.0f);
                    drawList.AddLine(c, d, selected, 3.0f);
                }
            }
        }

        if (_selectedObject?.Body != null &&
            TryWorldToScreen(_selectedObject.Body.Position, viewport, vpPos, vpSize, out Vector2 pivot))
        {
            uint pivotColor = ImGui.GetColorU32(new Vector4(1.0f, 0.78f, 0.18f, 1.0f));
            drawList.AddCircleFilled(pivot, 4.0f, pivotColor, 12);
            drawList.AddCircle(pivot, 8.0f, ImGui.GetColorU32(new Vector4(0.02f, 0.02f, 0.02f, 0.9f)), 16, 2.0f);
        }
    }

    private static void DrawOrientationWidget(
        ImDrawListPtr drawList,
        EditorViewport viewport,
        Vector2 vpPos,
        Vector2 vpSize)
    {
        if (vpSize.X < 120.0f || vpSize.Y < 90.0f)
            return;

        Vector2 center = vpPos + new Vector2(vpSize.X - 58.0f, 52.0f);
        drawList.AddCircleFilled(center, 34.0f, ImGui.GetColorU32(new Vector4(0.03f, 0.04f, 0.06f, 0.78f)), 24);
        drawList.AddCircle(center, 34.0f, ImGui.GetColorU32(new Vector4(0.55f, 0.60f, 0.70f, 0.75f)), 24, 1.0f);

        Vector3[] axes = [Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ];
        string[] labels = ["X", "Y", "Z"];
        Vector4[] colors =
        [
            new Vector4(0.95f, 0.20f, 0.20f, 1.0f),
            new Vector4(0.25f, 0.90f, 0.35f, 1.0f),
            new Vector4(0.25f, 0.55f, 1.00f, 1.0f)
        ];

        for (int i = 0; i < axes.Length; i++)
        {
            Vector3 axis = axes[i];
            Vector2 direction = new(
                Vector3.Dot(axis, viewport.Camera.Right),
                -Vector3.Dot(axis, viewport.Camera.Up));
            Vector2 endpoint = center + direction * 25.0f;
            float alpha = Vector3.Dot(axis, viewport.Camera.Front) < 0.0f ? 1.0f : 0.45f;
            Vector4 color = colors[i];
            color.W = alpha;
            uint axisColor = ImGui.GetColorU32(color);
            drawList.AddLine(center, endpoint, axisColor, alpha > 0.9f ? 2.5f : 1.5f);
            drawList.AddCircleFilled(endpoint, alpha > 0.9f ? 3.5f : 2.5f, axisColor, 12);
            drawList.AddText(endpoint + new Vector2(4.0f, -7.0f), axisColor, labels[i]);
        }

        drawList.AddCircleFilled(center, 3.0f, ImGui.GetColorU32(new Vector4(1.0f, 0.78f, 0.18f, 1.0f)), 12);
    }

    private bool TryWorldToScreen(
        Vector3 worldPos,
        EditorViewport viewport,
        Vector2 vpPos,
        Vector2 vpSize,
        out Vector2 screenPos)
    {
        var view = viewport.Camera.ViewMatrix;
        var proj = viewport.Camera.ProjectionMatrix(vpSize.X / vpSize.Y);
        Vector4 clip = Vector4.Transform(new Vector4(worldPos, 1.0f), view * proj);
        if (clip.W <= 0.0001f || !float.IsFinite(clip.W))
        {
            screenPos = Vector2.Zero;
            return false;
        }

        Vector3 ndc = new Vector3(clip.X, clip.Y, clip.Z) / clip.W;
        if (!float.IsFinite(ndc.X) || !float.IsFinite(ndc.Y))
        {
            screenPos = Vector2.Zero;
            return false;
        }

        screenPos = new Vector2(
            vpPos.X + (ndc.X + 1.0f) * 0.5f * vpSize.X,
            vpPos.Y + (1.0f - ndc.Y) * 0.5f * vpSize.Y);
        return true;
    }

    private Vector2 WorldToScreen(Vector3 worldPos, EditorViewport viewport, Vector2 vpPos, Vector2 vpSize)
    {
        var view = viewport.Camera.ViewMatrix;
        var proj = viewport.Camera.ProjectionMatrix(vpSize.X / vpSize.Y);
        Vector4 clip = Vector4.Transform(new Vector4(worldPos, 1.0f), view * proj);
        if (clip.W == 0.0f) return Vector2.Zero;
        Vector3 ndc = new Vector3(clip.X, clip.Y, clip.Z) / clip.W;
        float x = vpPos.X + (ndc.X + 1.0f) * 0.5f * vpSize.X;
        float y = vpPos.Y + (1.0f - ndc.Y) * 0.5f * vpSize.Y;
        return new Vector2(x, y);
    }

    private Vector3 ComputeHitPoint(CameraViewType viewType, Vector3 rayOrigin, Vector3 rayDir)
    {
        Vector3 hit = Vector3.Zero;
        if (viewType == CameraViewType.Top && MathF.Abs(rayDir.Y) > 0.001f)
            hit = rayOrigin + rayDir * (-rayOrigin.Y / rayDir.Y);
        else if (viewType == CameraViewType.Front && MathF.Abs(rayDir.Z) > 0.001f)
            hit = rayOrigin + rayDir * (-rayOrigin.Z / rayDir.Z);
        else if (viewType == CameraViewType.Side && MathF.Abs(rayDir.X) > 0.001f)
            hit = rayOrigin + rayDir * (-rayOrigin.X / rayDir.X);
        return hit;
    }

    private static float FindTopBrushSupportHeight(Vector3 topPoint, MapDocument document)
    {
        float supportHeight = 0.0f;
        bool foundSupport = false;
        Vector3 rayDirection = -Vector3.UnitY;

        foreach (Brush brush in document.Objects.OfType<Brush>())
        {
            MapBody? body = brush.Body;
            if (body == null || !brush.IsGloballyVisible(document))
                continue;

            if (!TryGetBrushWorldYBounds(brush, out _, out float maxY))
                continue;

            // Starting just above this brush is sufficient and avoids making
            // assumptions about the world's global height range.
            Vector3 rayOrigin = new(
                topPoint.X,
                MathF.Max(maxY + 1.0f, topPoint.Y + 1.0f),
                topPoint.Z);

            if (!TryIntersectBrushFromAbove(
                    brush,
                    rayOrigin,
                    rayDirection,
                    out float hitHeight))
                continue;

            if (!foundSupport || hitHeight > supportHeight)
            {
                supportHeight = hitHeight;
                foundSupport = true;
            }
        }

        return foundSupport ? supportHeight : 0.0f;
    }

    private static bool TryGetBrushWorldYBounds(
        Brush brush,
        out float minY,
        out float maxY)
    {
        minY = float.MaxValue;
        maxY = float.MinValue;
        MapBody? body = brush.Body;
        if (body == null)
            return false;

        if (brush.IsEditableMesh && brush.EditableMesh != null &&
            brush.EditableMesh.Vertices.Count > 0)
        {
            foreach (EditableBrushVertex vertex in brush.EditableMesh.Vertices)
            {
                float worldY = body.Position.Y +
                    Vector3.Transform(vertex.Position, body.Rotation).Y;
                minY = MathF.Min(minY, worldY);
                maxY = MathF.Max(maxY, worldY);
            }

            return minY <= maxY;
        }

        if (!body.HalfExtents.HasValue)
            return false;

        Vector3 halfExtents = body.HalfExtents.Value;
        for (int x = -1; x <= 1; x += 2)
        {
            for (int y = -1; y <= 1; y += 2)
            {
                for (int z = -1; z <= 1; z += 2)
                {
                    Vector3 localCorner = new(
                        halfExtents.X * x,
                        halfExtents.Y * y,
                        halfExtents.Z * z);
                    float worldY = body.Position.Y +
                        Vector3.Transform(localCorner, body.Rotation).Y;
                    minY = MathF.Min(minY, worldY);
                    maxY = MathF.Max(maxY, worldY);
                }
            }
        }

        return minY <= maxY;
    }

    private static bool TryIntersectBrushFromAbove(
        Brush brush,
        Vector3 rayOrigin,
        Vector3 rayDirection,
        out float worldY)
    {
        worldY = 0.0f;
        MapBody? body = brush.Body;
        if (body == null)
            return false;

        Matrix4x4 transform =
            Matrix4x4.CreateFromQuaternion(body.Rotation) *
            Matrix4x4.CreateTranslation(body.Position);
        if (!Matrix4x4.Invert(transform, out Matrix4x4 inverse))
            return false;

        Vector3 localOrigin = Vector3.Transform(rayOrigin, inverse);
        Vector3 localDirection = Vector3.TransformNormal(rayDirection, inverse);
        if (localDirection.LengthSquared() < 0.0000001f)
            return false;
        localDirection = Vector3.Normalize(localDirection);

        if (brush.IsEditableMesh && brush.EditableMesh != null &&
            brush.EditableMesh.TryRaycastFace(
                localOrigin,
                localDirection,
                out _,
                out Vector3 localHit,
                out _))
        {
            worldY = Vector3.Transform(localHit, transform).Y;
            return true;
        }

        if (!body.HalfExtents.HasValue)
            return false;

        bool supportedShape = body.Shape == MapShapeType.Box ||
            body.Shape == MapShapeType.Trimesh ||
            body.Shape == MapShapeType.ConvexHull;
        if (!supportedShape ||
            !TryRayAabbEntry(
                localOrigin,
                localDirection,
                -body.HalfExtents.Value,
                body.HalfExtents.Value,
                out float distance))
            return false;

        Vector3 localIntersection = localOrigin + localDirection * distance;
        worldY = Vector3.Transform(localIntersection, transform).Y;
        return true;
    }

    private static bool TryRayAabbEntry(
        Vector3 origin,
        Vector3 direction,
        Vector3 min,
        Vector3 max,
        out float distance)
    {
        const float epsilon = 0.000001f;
        float near = 0.0f;
        float far = float.MaxValue;

        bool TestAxis(float originAxis, float directionAxis, float minAxis, float maxAxis)
        {
            if (MathF.Abs(directionAxis) < epsilon)
                return originAxis >= minAxis - epsilon &&
                       originAxis <= maxAxis + epsilon;

            float first = (minAxis - originAxis) / directionAxis;
            float second = (maxAxis - originAxis) / directionAxis;
            if (first > second)
                (first, second) = (second, first);

            near = MathF.Max(near, first);
            far = MathF.Min(far, second);
            return near <= far;
        }

        if (!TestAxis(origin.X, direction.X, min.X, max.X) ||
            !TestAxis(origin.Y, direction.Y, min.Y, max.Y) ||
            !TestAxis(origin.Z, direction.Z, min.Z, max.Z) ||
            far < 0.0f)
        {
            distance = 0.0f;
            return false;
        }

        distance = MathF.Max(near, 0.0f);
        return true;
    }

    private void UpdateBoundsFromDrag(CameraViewType viewType, HandleType handle, Vector3 hitPoint, ref Vector3 min, ref Vector3 max)
    {
        int hAxis = 0;
        int vAxis = 0;
        bool hInverted = false;
        bool vInverted = false;

        if (viewType == CameraViewType.Top)
        {
            hAxis = 0; // X
            vAxis = 2; // Z
            vInverted = false;
        }
        else if (viewType == CameraViewType.Front)
        {
            hAxis = 0; // X
            vAxis = 1; // Y
            vInverted = true; // Top is Max Y
        }
        else if (viewType == CameraViewType.Side)
        {
            hAxis = 2; // Z
            vAxis = 1; // Y
            hInverted = true;
            vInverted = true; // Top is Max Y
        }

        bool dragLeft = handle == HandleType.Left || handle == HandleType.TopLeft || handle == HandleType.BottomLeft;
        bool dragRight = handle == HandleType.Right || handle == HandleType.TopRight || handle == HandleType.BottomRight;
        bool dragTop = handle == HandleType.Top || handle == HandleType.TopLeft || handle == HandleType.TopRight;
        bool dragBottom = handle == HandleType.Bottom || handle == HandleType.BottomLeft || handle == HandleType.BottomRight;

        if (dragLeft)
        {
            if (hInverted)
                SetComponent(ref max, hAxis, GetComp(hitPoint, hAxis));
            else
                SetComponent(ref min, hAxis, GetComp(hitPoint, hAxis));
        }
        if (dragRight)
        {
            if (hInverted)
                SetComponent(ref min, hAxis, GetComp(hitPoint, hAxis));
            else
                SetComponent(ref max, hAxis, GetComp(hitPoint, hAxis));
        }

        if (dragTop)
        {
            if (vInverted)
                SetComponent(ref max, vAxis, GetComp(hitPoint, vAxis));
            else
                SetComponent(ref min, vAxis, GetComp(hitPoint, vAxis));
        }
        if (dragBottom)
        {
            if (vInverted)
                SetComponent(ref min, vAxis, GetComp(hitPoint, vAxis));
            else
                SetComponent(ref max, vAxis, GetComp(hitPoint, vAxis));
        }

        Vector3 realMin = Vector3.Min(min, max);
        Vector3 realMax = Vector3.Max(min, max);
        min = realMin;
        max = realMax;
    }

    private float GetComp(Vector3 v, int axis) => axis switch
    {
        0 => v.X,
        1 => v.Y,
        2 => v.Z,
        _ => 0
    };

    private void SetComponent(ref Vector3 v, int axis, float val)
    {
        if (axis == 0) v.X = val;
        else if (axis == 1) v.Y = val;
        else if (axis == 2) v.Z = val;
    }

    private int GetSelectionBoundsCacheKey(EditorSceneService sceneService)
    {
        var hash = new HashCode();
        hash.Add(sceneService.Scene);
        hash.Add(sceneService.Revision);

        foreach (MapObject selObj in _selectedObjects.OrderBy(o => o.Id, StringComparer.OrdinalIgnoreCase))
        {
            hash.Add(selObj.Id, StringComparer.OrdinalIgnoreCase);
            hash.Add(selObj.Visible);
            hash.Add(selObj.IsModel);
            hash.Add(selObj.Model);
            hash.Add(selObj.IsTerrain);
            hash.Add(selObj.TerrainAssetPath);
            hash.Add(selObj.ModelScale);

            MapBody? body = selObj.Body;
            if (body == null)
            {
                hash.Add(false);
                continue;
            }

            hash.Add(true);
            hash.Add(body.Shape);
            hash.Add(body.Position);
            hash.Add(body.Rotation);
            hash.Add(body.HalfExtents);
            hash.Add(body.Radius);
            hash.Add(body.Height);
        }

        return hash.ToHashCode();
    }

    private bool GetSelectionAABB(
        EditorSceneService sceneService,
        EditorAssetService assetService,
        out Vector3 totalMin,
        out Vector3 totalMax)
    {
        int cacheKey = GetSelectionBoundsCacheKey(sceneService);
        if (_selectionBoundsCacheValid && _selectionBoundsCacheKey == cacheKey)
        {
            totalMin = _selectionBoundsCacheMin;
            totalMax = _selectionBoundsCacheMax;
            return _selectionBoundsCacheHasBounds;
        }

        totalMin = new Vector3(float.MaxValue);
        totalMax = new Vector3(float.MinValue);
        bool hasBounds = false;

        foreach (var selObj in _selectedObjects)
        {
            if (selObj.Body == null || !selObj.Visible) continue;
            var body = selObj.Body;
            var rotMatrix = Matrix4x4.CreateFromQuaternion(body.Rotation);

            if (selObj.IsTerrain && !string.IsNullOrWhiteSpace(selObj.TerrainAssetPath))
            {
                if (sceneService.TryGetTerrainRenderBounds(selObj.Id, out var terrainBounds))
                {
                    totalMin = Vector3.Min(totalMin, terrainBounds.GetBoundsMin());
                    totalMax = Vector3.Max(totalMax, terrainBounds.GetBoundsMax());
                    hasBounds = true;
                }

                // There is no visible selection box to draw when the terrain
                // chunks are not resident, so do not fall back to a costly
                // asset load here.
                continue;
            }

            if (body.Shape == MapShapeType.Trimesh && selObj.IsModel && selObj.Model != null)
            {
                string modelPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(assetService.FuseResPath, selObj.Model));
                var model = assetService.AssetManager.GetModel(modelPath);
                if (model != null && model.CollVertices.Length > 0)
                {
                    foreach (var v in model.CollVertices)
                    {
                        Vector3 scaledV = v * selObj.ModelScale;
                        Vector3 world = body.Position + Vector3.Transform(scaledV, rotMatrix);
                        totalMin = Vector3.Min(totalMin, world);
                        totalMax = Vector3.Max(totalMax, world);
                    }
                    hasBounds = true;
                    continue;
                }
            }

            Vector3 h = body.HalfExtents ?? Vector3.One;
            if (body.Shape == MapShapeType.Sphere || body.Shape == MapShapeType.Capsule)
            {
                float r = body.Radius ?? 0.5f;
                if (body.Shape == MapShapeType.Capsule)
                    r = MathF.Max(r, (body.Height ?? 1f) * 0.5f);
                h = new Vector3(r);
            }

            for (int i = 0; i < 8; i++)
            {
                Vector3 local = new(
                    (i & 1) == 0 ? -h.X : h.X,
                    (i & 2) == 0 ? -h.Y : h.Y,
                    (i & 4) == 0 ? -h.Z : h.Z);
                Vector3 world = body.Position + Vector3.Transform(local, rotMatrix);
                totalMin = Vector3.Min(totalMin, world);
                totalMax = Vector3.Max(totalMax, world);
            }
            hasBounds = true;
        }

        _selectionBoundsCacheKey = cacheKey;
        _selectionBoundsCacheMin = totalMin;
        _selectionBoundsCacheMax = totalMax;
        _selectionBoundsCacheHasBounds = hasBounds;
        _selectionBoundsCacheValid = true;
        return hasBounds;
    }

    private static bool DrawMaterialPicker(
        string label,
        string currentPath,
        EditorAssetService assetService,
        out string selectedPath)
    {
        selectedPath = currentPath;
        string preview = string.IsNullOrWhiteSpace(currentPath)
            ? "(Legacy / Default)"
            : Path.GetFileNameWithoutExtension(currentPath);
        bool changed = false;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.BeginCombo(label, preview))
        {
            if (ImGui.Selectable("(Legacy / Default)", string.IsNullOrWhiteSpace(currentPath)))
            {
                selectedPath = "";
                changed = true;
            }

            foreach (string material in assetService.EnumerateMaterials())
            {
                bool selected = material.Equals(currentPath, StringComparison.OrdinalIgnoreCase);
                if (ImGui.Selectable(material, selected))
                {
                    selectedPath = material;
                    changed = true;
                }
                if (selected)
                    ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }
        return changed;
    }

    private static void AssignMaterial(
        MapObject obj,
        string materialPath,
        EditorSceneService sceneService,
        EditorAssetService assetService)
    {
        obj.MaterialPath = string.IsNullOrWhiteSpace(materialPath) ? null : materialPath;
        if (obj.IsTerrain)
        {
            sceneService.PopulateScene(assetService);
            return;
        }

        var entity = sceneService.Scene.Entities.FirstOrDefault(candidate => candidate.Id == obj.Id);
        if (entity == null)
            return;

        entity.MaterialPath = obj.MaterialPath ?? "";
        entity.Material = !string.IsNullOrWhiteSpace(obj.MaterialPath)
            ? assetService.GetOrCreateMaterial(obj.MaterialPath)
            : (!string.IsNullOrWhiteSpace(entity.TexturePath)
                ? assetService.AssetManager.GetLegacyMaterial(entity.TexturePath)
                : null);
    }

    private static void ApplyTriggerPreviewMaterial(
        MapObject obj,
        Entity entity,
        bool isTrigger,
        EditorAssetService assetService)
    {
        entity.TexturePath = isTrigger ? "Textures/tools/toolstrigger.bmp" : (obj.Texture ?? "");
        entity.Materials.Clear();
        if (isTrigger)
        {
            entity.Material = assetService.AssetManager.GetLegacyMaterial(entity.TexturePath);
            return;
        }

        entity.Material = !string.IsNullOrWhiteSpace(obj.MaterialPath)
            ? assetService.GetOrCreateMaterial(obj.MaterialPath)
            : (!string.IsNullOrWhiteSpace(entity.TexturePath)
                ? assetService.AssetManager.GetLegacyMaterial(entity.TexturePath)
                : null);
        foreach (string slot in obj.MaterialSlots)
            entity.Materials.Add(assetService.GetOrCreateMaterial(slot));
    }

    private void DrawMaterialSlots(
        MapObject obj,
        EditorSceneService sceneService,
        EditorAssetService assetService,
        CommandHistory history)
    {
        if (!ImGui.TreeNode($"Material Slots ({obj.MaterialSlots.Count})##materialSlots_{obj.Id}"))
            return;

        bool slotsChanged = false;
        int removedSlot = -1;
        for (int i = 0; i < obj.MaterialSlots.Count; i++)
        {
            ImGui.PushID($"material_slot_{obj.Id}_{i}");
            string slotPath = obj.MaterialSlots[i];
            if (DrawMaterialPicker($"Slot {i}", slotPath, assetService, out string selectedSlot))
            {
                Undo.RecordState(_frameBeginState);
                obj.MaterialSlots[i] = selectedSlot;
                slotsChanged = true;
            }
            ImGui.SameLine();
            if (ImGui.SmallButton("X"))
            {
                Undo.RecordState(_frameBeginState);
                removedSlot = i;
                slotsChanged = true;
                ImGui.PopID();
                break;
            }
            ImGui.PopID();
        }

        if (removedSlot >= 0)
        {
            obj.MaterialSlots.RemoveAt(removedSlot);
            if (obj is Brush brush)
            {
                foreach (Face face in brush.Faces)
                {
                    if (face.MaterialSlot == removedSlot) face.MaterialSlot = 0;
                    else if (face.MaterialSlot > removedSlot) face.MaterialSlot--;
                }
            }
        }

        if (ImGui.Button("Add Material Slot"))
        {
            Undo.RecordState(_frameBeginState);
            obj.MaterialSlots.Add(obj.MaterialPath ?? assetService.EnumerateMaterials().FirstOrDefault() ?? "");
            slotsChanged = true;
        }

        bool faceAssignmentsChanged = false;
        if (obj is Brush selectedBrush && obj.MaterialSlots.Count > 0 &&
            ImGui.TreeNode($"Face Assignments##faceMaterials_{obj.Id}"))
        {
            for (int faceIndex = 0; faceIndex < selectedBrush.Faces.Count; faceIndex++)
            {
                Face face = selectedBrush.Faces[faceIndex];
                int slot = Math.Clamp(face.MaterialSlot, 0, obj.MaterialSlots.Count - 1);
                string preview = $"{slot}: {Path.GetFileNameWithoutExtension(obj.MaterialSlots[slot])}";
                ImGui.SetNextItemWidth(-1);
                if (ImGui.BeginCombo($"Face {faceIndex}##faceSlot_{obj.Id}_{faceIndex}", preview))
                {
                    for (int slotIndex = 0; slotIndex < obj.MaterialSlots.Count; slotIndex++)
                    {
                        if (ImGui.Selectable($"{slotIndex}: {obj.MaterialSlots[slotIndex]}", slotIndex == slot))
                        {
                            Undo.RecordState(_frameBeginState);
                            face.MaterialSlot = slotIndex;
                            faceAssignmentsChanged = true;
                        }
                    }
                    ImGui.EndCombo();
                }
            }
            ImGui.TreePop();
        }

        if (slotsChanged || faceAssignmentsChanged)
        {
            var entity = sceneService.Scene.Entities.FirstOrDefault(candidate => candidate.Id == obj.Id);
            if (entity != null)
            {
                entity.MaterialPaths = obj.MaterialSlots.ToList();
                entity.Materials.Clear();
                foreach (string slot in obj.MaterialSlots)
                    entity.Materials.Add(assetService.GetOrCreateMaterial(slot));

                if (faceAssignmentsChanged || removedSlot >= 0)
                {
                    assetService.InvalidateMesh(obj.Id);
                    entity.Mesh = assetService.GetOrCreateMesh(obj);
                }
            }
            Undo.ForceEnd(history, sceneService, assetService);
        }

        ImGui.TreePop();
    }

    private void RequestNewMaterial(IEnumerable<MapObject> targets)
    {
        _newMaterialTargets.Clear();
        _newMaterialTargets.AddRange(targets.Where(target => target != null).Distinct());
        _newMaterialName = "NewMaterial";
        _newMaterialTexture = "";
        _newMaterialPopupRequested = true;
    }

    private void DrawNewMaterialDialog(
        EditorSceneService sceneService,
        EditorAssetService assetService,
        CommandHistory history)
    {
        if (_newMaterialPopupRequested)
        {
            ImGui.OpenPopup("Create Material");
            _newMaterialPopupRequested = false;
        }

        if (!ImGui.BeginPopupModal("Create Material", ImGuiWindowFlags.AlwaysAutoResize))
            return;

        ImGui.InputText("Name", ref _newMaterialName, 128);
        ImGui.InputText("Initial Base Color Texture", ref _newMaterialTexture, 512);
        if (ImGui.BeginCombo("Browse Texture", string.IsNullOrWhiteSpace(_newMaterialTexture)
            ? "Optional..."
            : Path.GetFileName(_newMaterialTexture)))
        {
            if (ImGui.Selectable("(None)", string.IsNullOrWhiteSpace(_newMaterialTexture)))
                _newMaterialTexture = "";
            foreach (string texture in assetService.EnumerateTextures())
            {
                if (ImGui.Selectable(texture, texture.Equals(_newMaterialTexture, StringComparison.OrdinalIgnoreCase)))
                    _newMaterialTexture = texture;
            }
            ImGui.EndCombo();
        }

        bool validName = !string.IsNullOrWhiteSpace(_newMaterialName);
        if (!validName)
            ImGui.BeginDisabled();
        if (ImGui.Button("Create", new Vector2(120, 0)))
        {
            string safeName = SanitizeAssetName(_newMaterialName);
            string relativePath = $"Materials/{safeName}.fmat";
            string fullPath = Path.Combine(assetService.FuseResPath, relativePath);
            int suffix = 1;
            while (File.Exists(fullPath))
            {
                relativePath = $"Materials/{safeName}_{suffix++}.fmat";
                fullPath = Path.Combine(assetService.FuseResPath, relativePath);
            }

            MaterialAsset.CreateDefault(safeName, string.IsNullOrWhiteSpace(_newMaterialTexture) ? null : _newMaterialTexture)
                .Save(fullPath);
            assetService.GetOrCreateMaterial(relativePath);

            if (_newMaterialTargets.Count > 0)
            {
                Undo.RecordState(_frameBeginState);
                foreach (MapObject target in _newMaterialTargets)
                    AssignMaterial(target, relativePath, sceneService, assetService);
                Undo.ForceEnd(history, sceneService, assetService);
            }

            _materialEditor.Open(relativePath);
            ImGui.CloseCurrentPopup();
        }
        if (!validName)
            ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button("Cancel", new Vector2(120, 0)))
            ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
    }

    private void ConvertLegacyTexturesToMaterials(
        EditorSceneService sceneService,
        EditorAssetService assetService,
        CommandHistory history)
    {
        Undo.RecordState(_frameBeginState);
        string defaultMaterial = EnsureMaterialForTexture(assetService, "", "DefaultMaterial");

        foreach (MapObject obj in sceneService.Document.Objects)
        {
            if (obj.IsLight || (obj is not Brush && !obj.IsModel && string.IsNullOrWhiteSpace(obj.Mesh)))
                continue;

            if (string.IsNullOrWhiteSpace(obj.MaterialPath))
            {
                obj.MaterialPath = !string.IsNullOrWhiteSpace(obj.Texture)
                    ? EnsureMaterialForTexture(assetService, obj.Texture!, Path.GetFileNameWithoutExtension(obj.Texture))
                    : defaultMaterial;
            }

            if (obj is not Brush brush)
                continue;

            var faceTextures = brush.Faces
                .Select(face => face.Texture)
                .Where(texture => !string.IsNullOrWhiteSpace(texture) && File.Exists(MaterialRuntime.ResolveAssetPath(texture)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (faceTextures.Count == 0)
                continue;

            obj.MaterialSlots.Clear();
            foreach (string texture in faceTextures)
                obj.MaterialSlots.Add(EnsureMaterialForTexture(assetService, texture, Path.GetFileNameWithoutExtension(texture)));
            foreach (Face face in brush.Faces)
            {
                int index = faceTextures.FindIndex(texture => texture.Equals(face.Texture, StringComparison.OrdinalIgnoreCase));
                face.MaterialSlot = index >= 0 ? index : 0;
            }
        }

        sceneService.PopulateScene(assetService);
        Undo.ForceEnd(history, sceneService, assetService);
        Logger.Important("Legacy textures converted to material references. Legacy texture fields were preserved for compatibility.");
    }

    private static string EnsureMaterialForTexture(
        EditorAssetService assetService,
        string texturePath,
        string suggestedName)
    {
        string normalizedTexture = MaterialAsset.NormalizeAssetPath(texturePath);
        string hashSource = string.IsNullOrWhiteSpace(normalizedTexture) ? "default" : normalizedTexture.ToLowerInvariant();
        string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(hashSource)))[..8];
        string safeName = SanitizeAssetName(string.IsNullOrWhiteSpace(suggestedName) ? "Material" : suggestedName);
        string relativePath = $"Materials/{safeName}_{hash}.fmat";
        string fullPath = Path.Combine(assetService.FuseResPath, relativePath);
        if (!File.Exists(fullPath))
            MaterialAsset.CreateDefault(safeName, string.IsNullOrWhiteSpace(normalizedTexture) ? null : normalizedTexture).Save(fullPath);
        return relativePath;
    }

    private static string SanitizeAssetName(string name)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        string safe = new(name.Trim().Select(character => invalid.Contains(character) || char.IsWhiteSpace(character) ? '_' : character).ToArray());
        return string.IsNullOrWhiteSpace(safe) ? "Material" : safe;
    }

    private static float CalculateAuthoredBuoyancyVolume(MapBody body)
    {
        if (body.BuoyancyVolume is float explicitVolume &&
            explicitVolume > 0.0001f &&
            float.IsFinite(explicitVolume))
        {
            return explicitVolume;
        }

        return body.Shape switch
        {
            MapShapeType.Box when body.HalfExtents.HasValue =>
                8.0f * MathF.Abs(
                    body.HalfExtents.Value.X *
                    body.HalfExtents.Value.Y *
                    body.HalfExtents.Value.Z),
            MapShapeType.Sphere when body.Radius.HasValue =>
                4.0f / 3.0f * MathF.PI *
                MathF.Pow(MathF.Abs(body.Radius.Value), 3.0f),
            MapShapeType.Capsule when body.Radius.HasValue && body.Height.HasValue =>
                MathF.PI * MathF.Pow(MathF.Abs(body.Radius.Value), 2.0f) *
                MathF.Abs(body.Height.Value) +
                4.0f / 3.0f * MathF.PI *
                MathF.Pow(MathF.Abs(body.Radius.Value), 3.0f),
            _ => 0.0f
        };
    }

    public void DrawPreviewDebug(Fuse.Debug.DebugDrawer drawer, EditorAssetService assetService)
    {
        _previewManager.Draw3DPreview(drawer);

        if (_selectionBoundsCacheValid && _selectionBoundsCacheHasBounds)
        {
            Vector3 totalMin = _selectionBoundsCacheMin;
            Vector3 totalMax = _selectionBoundsCacheMax;
            Vector3 center = (totalMin + totalMax) * 0.5f;
            Vector3 halfExt = (totalMax - totalMin) * 0.5f;
            Vector3 color = new Vector3(0.2f, 1.0f, 0.2f);
            drawer.DrawBox(center, Quaternion.Identity, halfExt, color);
        }
    }
}
