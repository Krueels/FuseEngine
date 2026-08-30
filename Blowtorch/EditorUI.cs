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

namespace Blowtorch;

public unsafe class EditorUI : IDisposable
{
    private const string DefaultMaterialPath = "Materials/Default.fmat";
    private const string DevBrushMaterialPath = "Materials/DevMeasureCrate.fmat";

    private bool _showOpenDialog;
    private string[] _availableMaps = [];
    private int _selectedOpenMapIndex = -1;
    private bool _newDocumentRequested;
    private bool _focusCameraRequested;
    private bool _showSaveAsDialog;
    private bool _showHollowDialog;
    private bool _showHitBoxes = true;
    private float _hollowThickness = 0.5f;
    private string _saveMapName = "map.bth";
    private bool _showDiagnostics;
    private string _hierarchyFilter = "";
    private string _documentError = "";
    private bool _showUnsavedChangesDialog;
    private bool _showOverwriteDialog;
    private string _pendingOpenPath = "";
    private string _pendingSavePath = "";
    private bool _resumePendingActionAfterSave;
    private bool _executePendingDocumentAction;
    private bool _initialMapStatusChecked;

    private enum PendingDocumentAction { None, New, Open, Exit }
    private PendingDocumentAction _pendingDocumentAction;
    private enum ViewportLayout { Quad, PerspectiveOnly }
    private ViewportLayout _viewportLayout = ViewportLayout.Quad;

    private bool _showMapWindow = true;
    private bool _showJsonWindow = false;
    private readonly MaterialEditorWindow _materialEditor = new();
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
    public enum EditorMode { Select, DrawBrush }
    public enum GizmoOperation { Translate, Rotate, Scale, Shear }
    
    private EditorMode _currentMode = EditorMode.Select;
    private GizmoOperation _gizmoOperation = GizmoOperation.Translate;
    private MapObject? _selectedObject;
    private MapObject? _draggedObject;
    private HashSet<MapObject> _selectedObjects = new();
    private HashSet<string> _lastSelectedObjectIds = new();
    private double _lastSelectionTime = 0.0;
    private bool _showModelImportDialog = false;
    private List<string> _modelFiles = new();
    private int _selectedModelIndex = -1;
    private string? _detectedTexturePath = null;
    private bool _wasUsingGizmo = false;
    private EditorViewport? _activeDraggingViewport;

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
        _currentMode == EditorMode.DrawBrush || _isDraggingHandle || EditorGizmo.IsUsing() || ImGui.IsAnyItemActive();

    public void Dispose() => _materialEditor.Dispose();

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
            _activeDraggingViewport = null;
        }

        if (_focusCameraRequested && _selectedObject != null)
        {
            _focusCameraRequested = false;
            FocusCameraOnObject(_selectedObject, viewport3D, viewportTop, viewportFront, viewportSide);
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

        // Draw the graph before map input is processed so it can claim the
        // MaterialGraph context in the same frame when it has focus.
        _materialEditor.Draw(assetService, sceneService, inputService);

        DrawViewportWindow(window, viewport3D, viewportTop, viewportFront, viewportSide, sceneService, assetService, history, inputService);

        viewport3D.ShowHitboxes = _showHitBoxes;
        viewportTop.ShowHitboxes = _showHitBoxes;
        viewportFront.ShowHitboxes = _showHitBoxes;
        viewportSide.ShowHitboxes = _showHitBoxes;

        if (_showMapWindow)
            DrawMapWindow(sceneService, assetService, history, viewport3D, viewportTop, viewportFront, viewportSide);

        if (_showJsonWindow)
            DrawJsonWindow(sceneService);

        if (_showDiagnostics)
            DrawDiagnosticsWindow(sceneService, assetService, history, viewport3D, viewportTop, viewportFront, viewportSide);

        HandleKeyboardShortcuts(sceneService, assetService, history, inputService);
        Undo.EndFrame(history, sceneService, assetService);
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

    private void UpdateEntitiesVisibilityRecursive(MapDocument doc, Fuse.Renderer.Scene scene, MapObject obj)
    {
        var children = doc.Objects.Where(o => o.ParentId == obj.Id);
        foreach (var child in children)
        {
            var entity = scene.Entities.FirstOrDefault(e => e.Id == child.Id);
            if (entity != null)
            {
                entity.Visible = child.IsGloballyVisible(doc);
            }
            UpdateEntitiesVisibilityRecursive(doc, scene, child);
        }
    }

    private void GroupSelected(EditorSceneService sceneService, EditorAssetService assetService, CommandHistory history)
    {
        if (_selectedObjects.Count == 0) return;

        var pre = sceneService.Document.Serialize();
        var doc = sceneService.Document;

        // Calculate center position
        Vector3 sum = Vector3.Zero;
        int count = 0;
        foreach (var obj in _selectedObjects)
        {
            if (obj.Body != null)
            {
                sum += obj.Body.Position;
                count++;
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
                Rotation = Quaternion.Identity
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

    private void DrawObjectNode(MapObject obj, MapDocument doc, EditorSceneService sceneService, EditorAssetService assetService, CommandHistory history, EditorViewport viewport3D, EditorViewport viewportTop, EditorViewport viewportFront, EditorViewport viewportSide, string filter, ref MapObject? objectToDelete, ref MapObject? objectToDuplicate)
    {
        if (!HierarchyMatchesFilter(obj, doc, filter))
            return;
        var children = doc.Objects.Where(o => o.ParentId == obj.Id).ToList();
        bool isSelected = _selectedObjects.Contains(obj);
        bool hasChildren = children.Count > 0;

        ImGuiTreeNodeFlags flags = ImGuiTreeNodeFlags.FramePadding | (isSelected ? ImGuiTreeNodeFlags.Selected : 0);
        if (!hasChildren)
        {
            flags |= ImGuiTreeNodeFlags.Leaf;
        }
        else
        {
            flags |= ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.OpenOnDoubleClick;
        }

        bool isEmptyGroup = (obj.Body == null || obj.Body.Shape == MapShapeType.None) && string.IsNullOrEmpty(obj.Mesh) && string.IsNullOrEmpty(obj.Model);
        string typeEmoji = isEmptyGroup ? "📁" : (obj.IsModel ? "🗿" : (obj is Brush ? "📐" : (obj.Body?.Shape == MapShapeType.Sphere ? "🔮" : (obj.Body?.Shape == MapShapeType.Capsule ? "💊" : "📦"))));

        bool isGloballyVisible = obj.IsGloballyVisible(doc);

        string label = $"{typeEmoji} {obj.Id}";

        bool isOpen = ImGui.TreeNodeEx($"##node_{obj.Id}", flags, label);

        if (ImGui.BeginDragDropSource())
        {
            _draggedObject = obj;
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
                if (_draggedObject != null && _draggedObject != obj && !IsDescendantOf(obj, _draggedObject, doc))
                {
                    var pre = doc.Serialize();
                    
                    if (isReorderAbove || isReorderBelow)
                    {
                        // REORDER
                        _draggedObject.ParentId = obj.ParentId;
                        doc.Objects.Remove(_draggedObject);
                        int insertIndex = doc.Objects.IndexOf(obj);
                        if (isReorderBelow) insertIndex++;
                        if (insertIndex < 0) insertIndex = 0;
                        if (insertIndex > doc.Objects.Count) insertIndex = doc.Objects.Count;
                        doc.Objects.Insert(insertIndex, _draggedObject);
                        
                        var entity = sceneService.Scene.Entities.FirstOrDefault(e => e.Id == _draggedObject.Id);
                        var parentEntity = sceneService.Scene.Entities.FirstOrDefault(e => e.Id == obj.ParentId);
                        if (entity != null)
                        {
                            entity.ParentId = obj.ParentId ?? "";
                            if (parentEntity != null)
                            {
                                entity.InitialRelativePosition = entity.Transform.Position - parentEntity.Transform.Position;
                                entity.InitialRelativeRotation = Quaternion.Inverse(parentEntity.Transform.Rotation) * entity.Transform.Rotation;
                            }
                        }
                    }
                    else
                    {
                        // REPARENT
                        _draggedObject.ParentId = obj.Id;
                        
                        var entity = sceneService.Scene.Entities.FirstOrDefault(e => e.Id == _draggedObject.Id);
                        var parentEntity = sceneService.Scene.Entities.FirstOrDefault(e => e.Id == obj.Id);
                        if (entity != null)
                        {
                            entity.ParentId = obj.Id;
                            if (parentEntity != null)
                            {
                                entity.InitialRelativePosition = entity.Transform.Position - parentEntity.Transform.Position;
                                entity.InitialRelativeRotation = Quaternion.Inverse(parentEntity.Transform.Rotation) * entity.Transform.Rotation;
                            }
                        }
                    }

                    var post = doc.Serialize();
                    sceneService.MarkModified(post);
                    history.PushCommand(new SnapshotCommand(sceneService, assetService, pre, post));
                    sceneService.PopulateScene(assetService);
                }
                _draggedObject = null;
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
                _selectedObjects.Clear();
                _selectedObjects.Add(obj);
                _selectedObject = obj;
            }
            _lastSelectionTime = ImGui.GetTime();
        }

        if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
        {
            FocusCameraOnObject(obj, viewport3D, viewportTop, viewportFront, viewportSide);
        }

        if (ImGui.BeginPopupContextItem($"context_{obj.Id}"))
        {
            if (!_selectedObjects.Contains(obj))
            {
                _selectedObjects.Clear();
                _selectedObjects.Add(obj);
                _selectedObject = obj;
            }

            if (ImGui.MenuItem("🔍 Focus Camera"))
            {
                FocusCameraOnObject(obj, viewport3D, viewportTop, viewportFront, viewportSide);
            }
            if (ImGui.MenuItem("👁 Toggle Visibility"))
            {
                Undo.RecordState(_frameBeginState);
                obj.Visible = !obj.Visible;
                var entity = sceneService.Scene.Entities.FirstOrDefault(e => e.Id == obj.Id);
                if (entity != null) entity.Visible = obj.IsGloballyVisible(doc);
                UpdateEntitiesVisibilityRecursive(doc, sceneService.Scene, obj);
                Undo.ForceEnd(history, sceneService, assetService);
            }
            ImGui.Separator();
            if (ImGui.MenuItem("📦 Group Selected"))
            {
                GroupSelected(sceneService, assetService, history);
            }
            if (_selectedObjects.Any(o => !string.IsNullOrEmpty(o.ParentId)))
            {
                if (ImGui.MenuItem("📤 Ungroup Selected"))
                {
                    UngroupSelected(sceneService, assetService, history);
                }
            }
            ImGui.Separator();
            if (ImGui.MenuItem("📋 Duplicate"))
            {
                objectToDuplicate = obj;
            }
            if (ImGui.MenuItem("❌ Delete"))
            {
                objectToDelete = obj;
            }
            ImGui.EndPopup();
        }

        ImGui.SameLine();
        float rightAlignPos = ImGui.GetWindowWidth() - 35;
        ImGui.SetCursorPosX(rightAlignPos);

        string visIcon = obj.Visible ? "👁" : "◌";
        bool inheritedHidden = !isGloballyVisible && obj.Visible;

        if (inheritedHidden)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.5f, 0.5f, 0.5f, 0.5f));
            visIcon = "👁";
        }

        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0, 0, 0, 0));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0, 0, 0, 0));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.2f, 0.2f, 0.2f, 0.5f));

        if (ImGui.Button($"{visIcon}##visbtn_{obj.Id}", new Vector2(24, 20)))
        {
            Undo.RecordState(_frameBeginState);
            obj.Visible = !obj.Visible;
            var entity = sceneService.Scene.Entities.FirstOrDefault(e => e.Id == obj.Id);
            if (entity != null) entity.Visible = obj.IsGloballyVisible(doc);
            UpdateEntitiesVisibilityRecursive(doc, sceneService.Scene, obj);
            Undo.ForceEnd(history, sceneService, assetService);
        }
        ImGui.PopStyleColor(3);
        if (inheritedHidden)
        {
            ImGui.PopStyleColor(1);
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
            System.Diagnostics.Process.Start("Fuse.exe", mapFile);
        }
        catch (Exception ex)
        {
            ShowDocumentError($"Could not launch Fuse: {ex.Message}");
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

        if (ImGui.IsKeyPressed(ImGuiKey.Delete) && _selectedObjects.Count > 0)
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
            if (ImGui.BeginMenu("View"))
            {
                ImGui.MenuItem("Map Objects", "", ref _showMapWindow);
                ImGui.MenuItem("Raw JSON", "", ref _showJsonWindow);
                ImGui.MenuItem("Diagnostics", "", ref _showDiagnostics);
                ImGui.MenuItem("Show hitboxes", "", ref _showHitBoxes);
                ImGui.Separator();
                if (ImGui.MenuItem("Quad View", "", _viewportLayout == ViewportLayout.Quad))
                    _viewportLayout = ViewportLayout.Quad;
                if (ImGui.MenuItem("3D View Only", "", _viewportLayout == ViewportLayout.PerspectiveOnly))
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
            if (ImGui.RadioButton("Select (Esc)", _currentMode == EditorMode.Select)) _currentMode = EditorMode.Select;
            ImGui.SameLine();
            if (ImGui.RadioButton("Brush (B)", _currentMode == EditorMode.DrawBrush)) _currentMode = EditorMode.DrawBrush;
            ImGui.SameLine();
            ImGui.Text("|");
            ImGui.SameLine();
            if (ImGui.RadioButton("Translate (W)", _gizmoOperation == GizmoOperation.Translate)) _gizmoOperation = GizmoOperation.Translate;
            ImGui.SameLine();
            if (ImGui.RadioButton("Rotate (E)", _gizmoOperation == GizmoOperation.Rotate)) _gizmoOperation = GizmoOperation.Rotate;
            ImGui.SameLine();
            if (ImGui.RadioButton("Scale (R)", _gizmoOperation == GizmoOperation.Scale)) _gizmoOperation = GizmoOperation.Scale;
            ImGui.SameLine();
            if (ImGui.RadioButton("Shear (T)", _gizmoOperation == GizmoOperation.Shear)) _gizmoOperation = GizmoOperation.Shear;

            if (inputService.IsMapContext &&
                !ImGui.IsMouseDown(ImGuiMouseButton.Right) &&
                !ImGui.GetIO().WantTextInput)
            {
                if (ImGui.IsKeyPressed(ImGuiKey.Escape))
                {
                    if (_currentMode == EditorMode.DrawBrush)
                    {
                        _currentMode = EditorMode.Select;
                        _activeHandle = HandleType.None;
                        _previewManager.Reset();
                    }
                    else
                    {
                        _selectedObject = null;
                        _selectedObjects.Clear();
                    }
                }
                if (ImGui.IsKeyPressed(ImGuiKey.B)) _currentMode = EditorMode.DrawBrush;
                if (ImGui.IsKeyPressed(ImGuiKey.W)) _gizmoOperation = GizmoOperation.Translate;
                if (ImGui.IsKeyPressed(ImGuiKey.E)) _gizmoOperation = GizmoOperation.Rotate;
                if (ImGui.IsKeyPressed(ImGuiKey.R)) _gizmoOperation = GizmoOperation.Scale;
                if (ImGui.IsKeyPressed(ImGuiKey.T)) _gizmoOperation = GizmoOperation.Shear;
                
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

        bool isHovered = ImGui.IsItemHovered() && inputService.IsMapContext;

        // 2D Handle Detection & State Setup
        bool showHandles = false;
        Vector3 boxMin = Vector3.Zero;
        Vector3 boxMax = Vector3.Zero;
        bool isPreview = false;
        Span<Vector2> handlePositions = stackalloc Vector2[10];

        if (viewport.Camera.IsOrthographic)
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
                if (GetSelectionAABB(assetService, out Vector3 tMin, out Vector3 tMax))
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
        bool allowViewportInput = isHovered && !gizmoActive && !_isDraggingHandle;
        bool allowPicking = allowViewportInput && !EditorGizmo.IsHovered;

        // Hammer-style: suppress translation and scale gizmos when the entire selection is made of brushes
        bool allSelectedAreBrushes = _selectedObjects.Count > 0 && _selectedObjects.All(o => o is Brush);
        bool suppressGizmoForBrushes = allSelectedAreBrushes && _gizmoOperation != GizmoOperation.Rotate;

        if (_selectedObject != null && _selectedObject.Body != null && sceneService.Document.Objects.Contains(_selectedObject) && !_isDraggingHandle && !suppressGizmoForBrushes)
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
                            else
                                entity.Transform.Scale = obj.ModelScale;
                        }

                        SyncLight(sceneService, obj);
                    }
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
            if (_currentMode == EditorMode.Select)
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
                    
                    if (isMouseClicked && !_previewManager.HasPreview)
                    {
                        _selectedObjects.Clear();
                        _selectedObject = null;
                    }

                    _previewManager.HandleDrawingInput(viewport, hitPoint, isMouseClicked);
                }
            }
        }

        if (allowPicking && _currentMode == EditorMode.Select)
        {
            if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            {
                EditorGizmo.GetMouseRay(ImGui.GetIO().MousePos, viewport.Camera.ViewMatrix, viewport.Camera.ProjectionMatrix(vpSize.X / vpSize.Y), vpPos, vpSize, out Vector3 rayOrigin, out Vector3 rayDir);
                
                var hitObjects = PickObjects(rayOrigin, rayDir, sceneService, assetService);
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
            
            if (obj.Body.Shape == MapShapeType.Sphere && obj.Body.Radius.HasValue)
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

    private void FocusCameraOnObject(MapObject obj, EditorViewport vp3D, EditorViewport vpTop, EditorViewport vpFront, EditorViewport vpSide)
    {
        if (obj.Body == null) return;
        Vector3 pos = obj.Body.Position;
        vp3D.Camera.Position = pos;
        vpTop.Camera.Position = pos;
        vpFront.Camera.Position = pos;
        vpSide.Camera.Position = pos;
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

        // --- Snapping Controls ---
        if (ImGui.CollapsingHeader("Editor Settings & Snapping", ImGuiTreeNodeFlags.None))
        {
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
            if (ImGui.Button("Add Box")) AddNewObject(sceneService, assetService, history, MapShapeType.Box);
            ImGui.SameLine();
            if (ImGui.Button("Add Sphere")) AddNewObject(sceneService, assetService, history, MapShapeType.Sphere);
            ImGui.SameLine();
            if (ImGui.Button("Add Capsule")) AddNewObject(sceneService, assetService, history, MapShapeType.Capsule);
            ImGui.SameLine();
            if (ImGui.Button("Add Model"))
            {
                _showModelImportDialog = true;
                RefreshModelFileList(assetService.FuseResPath);
            }
            ImGui.SameLine();
            if (ImGui.Button("Add Pt Light")) AddNewLight(sceneService, assetService, history, "point");
            ImGui.SameLine();
            if (ImGui.Button("Add Spot Light")) AddNewLight(sceneService, assetService, history, "spot");
            ImGui.SameLine();
            if (ImGui.Button("Add Dir Light")) AddNewLight(sceneService, assetService, history, "directional");

            ImGui.Text($"Total Objects: {doc.Objects.Count}");
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
        ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1.0f), "Scene Hierarchy");
        ImGui.Separator();
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##HierarchyFilter", "Search objects, lights, models...", ref _hierarchyFilter, 128);

        float listHeight = ImGui.GetContentRegionAvail().Y * 0.5f - 10;
        if (listHeight < 150f) listHeight = 150f; // Ensure minimum height

        ImGui.BeginChild("HierarchyTree", new Vector2(0, listHeight), ImGuiChildFlags.Borders, ImGuiWindowFlags.HorizontalScrollbar);
        
        var rootObjects = doc.Objects.Where(o => string.IsNullOrEmpty(o.ParentId)).ToList();
        foreach (var obj in rootObjects)
        {
            DrawObjectNode(obj, doc, sceneService, assetService, history, viewport3D, viewportTop, viewportFront, viewportSide, _hierarchyFilter, ref objectToDelete, ref objectToDuplicate);
        }
        
        if (ImGui.BeginPopupContextWindow("hierarchy_tree_context", ImGuiPopupFlags.MouseButtonRight | ImGuiPopupFlags.NoOpenOverItems))
        {
            if (_selectedObjects.Count > 0)
            {
                if (ImGui.MenuItem("📦 Group Selected"))
                {
                    GroupSelected(sceneService, assetService, history);
                }
                if (_selectedObjects.Any(o => !string.IsNullOrEmpty(o.ParentId)))
                {
                    if (ImGui.MenuItem("📤 Ungroup Selected"))
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
                if (_draggedObject != null && !string.IsNullOrEmpty(_draggedObject.ParentId))
                {
                    var pre = doc.Serialize();
                    _draggedObject.ParentId = null;
                    var entity = scene.Entities.FirstOrDefault(e => e.Id == _draggedObject.Id);
                    if (entity != null)
                    {
                        entity.ParentId = "";
                    }
                    var post = doc.Serialize();
                    sceneService.MarkModified(post);
                    history.PushCommand(new SnapshotCommand(sceneService, assetService, pre, post));
                    sceneService.PopulateScene(assetService);
                }
                _draggedObject = null;
            }
            ImGui.EndDragDropTarget();
        }

        ImGui.EndChild();

        // --- Inspector / Properties Window ---
        ImGui.Spacing();
        ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1.0f), "Inspector");
        ImGui.Separator();

        ImGui.BeginChild("InspectorSection", new Vector2(0, 0), ImGuiChildFlags.Borders);
        if (_selectedObjects.Count == 0)
        {
            ImGui.TextDisabled("Select an object to inspect properties.");
        }
        else if (_selectedObjects.Count > 1)
        {
            ImGui.TextColored(new Vector4(0.3f, 0.8f, 0.8f, 1f), $"Multiple Selection ({_selectedObjects.Count} objects)");
            ImGui.Separator();

            bool allVisible = _selectedObjects.All(o => o.Visible);
            bool multiVis = allVisible;
            if (ImGui.Checkbox("Visible##multiVis", ref multiVis))
            {
                Undo.RecordState(_frameBeginState);
                foreach (var obj in _selectedObjects)
                {
                    obj.Visible = multiVis;
                    var entity = scene.Entities.FirstOrDefault(e => e.Id == obj.Id);
                    if (entity != null) entity.Visible = multiVis;
                }
                Undo.ForceEnd(history, sceneService, assetService);
            }

            string commonMaterial = _selectedObjects.Select(o => o.MaterialPath).Distinct().Count() == 1
                ? (_selectedObjects.First().MaterialPath ?? "")
                : "";
            if (DrawMaterialPicker("Material##multiMaterial", commonMaterial, assetService, out string multiMaterial))
            {
                Undo.RecordState(_frameBeginState);
                foreach (var obj in _selectedObjects)
                    AssignMaterial(obj, multiMaterial, sceneService, assetService);
                Undo.ForceEnd(history, sceneService, assetService);
            }
            ImGui.SameLine();
            if (ImGui.Button("New##multiMaterial"))
                RequestNewMaterial(_selectedObjects);

            if (ImGui.TreeNode("Legacy Texture Compatibility##multiLegacyTexture"))
            {
                string commonTex = _selectedObjects.Select(o => o.Texture).Distinct().Count() == 1 ? (_selectedObjects.First().Texture ?? "") : "";
                string multiTex = commonTex;
                if (ImGui.InputText("Texture##multiTex", ref multiTex, 256))
                {
                    Undo.TrackItem(_frameBeginState);
                    foreach (var obj in _selectedObjects)
                    {
                        obj.Texture = multiTex;
                        var entity = scene.Entities.FirstOrDefault(e => e.Id == obj.Id);
                        if (entity != null)
                        {
                            entity.TexturePath = multiTex;
                            if (string.IsNullOrWhiteSpace(entity.MaterialPath) && !string.IsNullOrWhiteSpace(multiTex))
                                entity.Material = assetService.AssetManager.GetLegacyMaterial(multiTex);
                        }
                    }
                }
                ImGui.TreePop();
            }

            if (_selectedObjects.All(o => !o.IsModel))
            {
                Vector2 commonUv = _selectedObjects.Select(o => o.UvScale).Distinct().Count() == 1 ? _selectedObjects.First().UvScale : Vector2.One;
                Vector2 multiUv = commonUv;
                if (ImGui.DragFloat2("UV Scale##multiUv", ref multiUv, 0.05f))
                {
                    Undo.TrackItem(_frameBeginState);
                    foreach (var obj in _selectedObjects)
                    {
                        obj.UvScale = multiUv;
                        var entity = scene.Entities.FirstOrDefault(e => e.Id == obj.Id);
                        if (entity != null) entity.UvScale = multiUv;
                    }
                    
                }

                Vector2 commonUvOff = _selectedObjects.Select(o => o.UvOffset).Distinct().Count() == 1 ? _selectedObjects.First().UvOffset : Vector2.Zero;
                Vector2 multiUvOff = commonUvOff;
                if (ImGui.DragFloat2("UV Offset##multiUvOff", ref multiUvOff, 0.01f))
                {
                    Undo.TrackItem(_frameBeginState);
                    foreach (var obj in _selectedObjects)
                    {
                        obj.UvOffset = multiUvOff;
                        var entity = scene.Entities.FirstOrDefault(e => e.Id == obj.Id);
                        if (entity != null) entity.UvOffset = multiUvOff;
                    }
                    
                }

                float commonUvRot = _selectedObjects.Select(o => o.UvRotation).Distinct().Count() == 1 ? _selectedObjects.First().UvRotation : 0f;
                float multiUvRotDeg = commonUvRot * (180f / MathF.PI);
                if (ImGui.DragFloat("UV Rotation##multiUvRot", ref multiUvRotDeg, 0.5f, -360f, 360f, "%.1f deg"))
                {
                    Undo.TrackItem(_frameBeginState);
                    float multiUvRot = multiUvRotDeg * (MathF.PI / 180f);
                    foreach (var obj in _selectedObjects)
                    {
                        obj.UvRotation = multiUvRot;
                        var entity = scene.Entities.FirstOrDefault(e => e.Id == obj.Id);
                        if (entity != null) entity.UvRotation = multiUvRot;
                    }
                    
                }
            }

            if (_selectedObjects.All(o => o.Body != null))
            {
                float commonMass = _selectedObjects.Select(o => o.Body!.Mass).Distinct().Count() == 1 ? _selectedObjects.First().Body!.Mass : 0f;
                float multiMass = commonMass;
                if (ImGui.DragFloat("Mass##multiMass", ref multiMass, 0.1f, 0.0f, 100000.0f, "%.3f"))
                {
                    Undo.TrackItem(_frameBeginState);
                    foreach (var obj in _selectedObjects)
                    {
                        obj.Body!.Mass = multiMass;
                    }
                    
                }

                // Is Trigger
                bool commonTrigger = _selectedObjects.Select(o => o.Body!.IsTrigger).Distinct().Count() == 1
                    ? _selectedObjects.First().Body!.IsTrigger : false;
                bool multiTrigger = commonTrigger;
                if (ImGui.Checkbox("Is Trigger##multiTrigger", ref multiTrigger))
                {
                    Undo.TrackItem(_frameBeginState);
                    foreach (var obj in _selectedObjects)
                    {
                        obj.Body!.IsTrigger = multiTrigger;
                        var entity = scene.Entities.FirstOrDefault(e => e.Id == obj.Id);
                        if (entity != null)
                            ApplyTriggerPreviewMaterial(obj, entity, multiTrigger, assetService);
                    }
                    
                }
            }
        }
        else
        {
            var obj = _selectedObject;
            if (obj != null)
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

                    bool sChanged = ImGui.DragFloat3("Scale##inspectScale", ref currentScale, 0.05f);
                    Undo.TrackItem(_frameBeginState);

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

                        if (sChanged && !obj.IsModel)
                        {
                            assetService.InvalidateMesh(obj.Id);
                            if (entity != null) entity.Mesh = assetService.GetOrCreateMesh(obj);
                        }
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
                        }
                        

                        Vector2 uvOffset = obj.UvOffset;
                        bool uvOffChanged = ImGui.DragFloat2("UV Offset##inspectUvOff", ref uvOffset, 0.01f);
                        Undo.TrackItem(_frameBeginState);
                        if (uvOffChanged)
                        {
                            obj.UvOffset = uvOffset;
                            var entity = scene.Entities.FirstOrDefault(e => e.Id == obj.Id);
                            if (entity != null) entity.UvOffset = uvOffset;
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
                        }
                        
                    }
                    else
                    {
                        ImGui.Text($"Model File: {obj.Model}");
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
                            string[] shapes = { "Default", "No Collision" };
                            int shapeIdx = body.Shape == MapShapeType.None ? 1 : 0;
                            bool shapeChanged = ImGui.Combo("Collision", ref shapeIdx, shapes, shapes.Length);
                            Undo.TrackItem(_frameBeginState);
                            if (shapeChanged)
                            {
                                if (shapeIdx == 1) body.Shape = MapShapeType.None;
                                else 
                                {
                                    if (obj is Fuse.Scene.Model.Brush) body.Shape = MapShapeType.Box;
                                    else if (obj.Mesh == "sphere") body.Shape = MapShapeType.Sphere;
                                    else body.Shape = MapShapeType.Box;
                                }
                            }
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
                                
                                break;
                        }
                    }
                }
            }
        }
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
        light.Enabled = obj.Visible;
        light.Color = obj.LightColor;
        light.Intensity = obj.LightIntensity;
        light.Radius = obj.LightRadius;
        light.InnerConeAngle = obj.LightInnerCone;
        light.OuterConeAngle = obj.LightOuterCone;
        light.CastShadows = obj.LightCastShadows;
        light.ShadowBias = obj.LightShadowBias;
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

    private bool GetSelectionAABB(EditorAssetService assetService, out Vector3 totalMin, out Vector3 totalMax)
    {
        totalMin = new Vector3(float.MaxValue);
        totalMax = new Vector3(float.MinValue);
        bool hasBounds = false;

        foreach (var selObj in _selectedObjects)
        {
            if (selObj.Body == null || !selObj.Visible) continue;
            var body = selObj.Body;
            var rotMatrix = Matrix4x4.CreateFromQuaternion(body.Rotation);

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

    public void DrawPreviewDebug(Fuse.Debug.DebugDrawer drawer, EditorAssetService assetService)
    {
        _previewManager.Draw3DPreview(drawer);

        if (GetSelectionAABB(assetService, out Vector3 totalMin, out Vector3 totalMax))
        {
            Vector3 center = (totalMin + totalMax) * 0.5f;
            Vector3 halfExt = (totalMax - totalMin) * 0.5f;
            Vector3 color = new Vector3(0.2f, 1.0f, 0.2f);
            drawer.DrawBox(center, Quaternion.Identity, halfExt, color);
        }
    }
}
