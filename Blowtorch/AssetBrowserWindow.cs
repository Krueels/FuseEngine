using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using ImGuiNET;

namespace Blowtorch;

public sealed class AssetBrowserWindow
{
    private readonly List<EditorAssetEntry> _entries = [];
    private string _search = "";
    private int _filter;
    private string _currentFolder = "";
    private string _selectedPath = "";
    private ulong _catalogRevision = ulong.MaxValue;
    private string _status = "";
    private bool _refreshRequested = true;
    private Action<string>? _texturePickerCallback;
    private Action<string>? _geometryPickerCallback;
    private string _pendingDeletePath = "";
    private EditorAssetKind _pendingDeleteKind;
    private bool _showDeleteConfirmation;

    public bool IsOpen { get; set; }

    public void OpenTexturePicker(Action<string> onSelected)
    {
        _texturePickerCallback = onSelected;
        _geometryPickerCallback = null;
        _filter = 3;
        _search = "";
        _currentFolder = "";
        _status = "Select a texture.";
        IsOpen = true;
    }

    public void OpenGeometryPicker(Action<string> onSelected)
    {
        _geometryPickerCallback = onSelected;
        _texturePickerCallback = null;
        _filter = 5;
        _search = "";
        _currentFolder = "";
        _status = "Select a geometry graph.";
        IsOpen = true;
    }

    public void Draw(
        EditorAssetService assetService,
        EditorSceneService sceneService,
        MaterialEditorWindow materialEditor,
        GeometryGraphEditorWindow geometryEditor,
        Action<EditorAssetEntry>? activate)
    {
        if (!IsOpen)
            return;

        if (_refreshRequested || _catalogRevision != assetService.AssetRevision)
            Refresh(assetService);

        ImGui.SetNextWindowSize(new Vector2(980, 620), ImGuiCond.FirstUseEver);
        bool open = IsOpen;
        if (!ImGui.Begin("Asset Browser##AssetBrowser", ref open, ImGuiWindowFlags.MenuBar))
        {
            IsOpen = open;
            if (!open)
            {
                _texturePickerCallback = null;
                _geometryPickerCallback = null;
            }
            ImGui.End();
            return;
        }
        IsOpen = open;
        if (!open)
        {
            _texturePickerCallback = null;
            _geometryPickerCallback = null;
        }

        if (ImGui.BeginMenuBar())
        {
            if (ImGui.MenuItem("Refresh"))
                _refreshRequested = true;
            if (ImGui.MenuItem("Reimport Selected", "", false, FindSelected() != null))
                ReimportSelected(assetService, sceneService, activate);
            if (ImGui.MenuItem("Delete Selected...", "", false, FindSelected() != null))
                RequestDelete(FindSelected()!);
            ImGui.EndMenuBar();
        }

        ImGui.InputTextWithHint("##AssetSearch", "Search assets...", ref _search, 256);
        ImGui.SameLine();
        string[] filters = ["All", "Models", "Materials", "Textures", "Skyboxes", "Geometry Graphs"];
        ImGui.SetNextItemWidth(130);
        ImGui.Combo("##AssetType", ref _filter, filters, filters.Length);
        ImGui.SameLine();
        ImGui.TextDisabled($"{FilteredEntries().Count} assets");
        if (!string.IsNullOrEmpty(_status))
        {
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(0.95f, 0.75f, 0.25f, 1), _status);
        }
        ImGui.Separator();
        if (!string.IsNullOrEmpty(_currentFolder))
        {
            if (ImGui.Button("Up"))
            {
                int separator = _currentFolder.LastIndexOf('/');
                _currentFolder = separator >= 0 ? _currentFolder[..separator] : "";
                _selectedPath = "";
            }
            ImGui.SameLine();
            ImGui.TextDisabled($"Folder: /{_currentFolder}");
            ImGui.Separator();
        }

        Vector2 available = ImGui.GetContentRegionAvail();
        float detailsWidth = Math.Clamp(available.X * 0.28f, 230, 320);
        ImGui.BeginChild("AssetTiles", new Vector2(MathF.Max(300, available.X - detailsWidth - 8), available.Y), ImGuiChildFlags.Borders);
        DrawTiles(assetService, materialEditor, geometryEditor, activate);
        ImGui.EndChild();
        ImGui.SameLine();
        ImGui.BeginChild("AssetDetails", new Vector2(detailsWidth, available.Y), ImGuiChildFlags.Borders);
        DrawDetails(assetService, materialEditor, geometryEditor, activate);
        ImGui.EndChild();

        ImGui.End();
        DrawDeleteConfirmation(assetService, sceneService, materialEditor);
    }

    private void DrawTiles(EditorAssetService assetService, MaterialEditorWindow materialEditor, GeometryGraphEditorWindow geometryEditor, Action<EditorAssetEntry>? activate)
    {
        const float tileWidth = 142;
        const float tileHeight = 142;
        float width = ImGui.GetContentRegionAvail().X;
        int columns = Math.Max(1, (int)(width / tileWidth));
        int column = 0;

        foreach (string folder in VisibleFolders())
        {
            if (column > 0)
                ImGui.SameLine();

            ImGui.PushID($"folder:{folder}");
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.14f, 0.18f, 0.24f, 1));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.22f, 0.32f, 0.44f, 1));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.28f, 0.40f, 0.54f, 1));
            bool clicked = ImGui.Button("##FolderTile", new Vector2(tileWidth - 8, tileHeight));
            ImGui.PopStyleColor(3);
            Vector2 tileMin = ImGui.GetItemRectMin();
            Vector2 tileMax = ImGui.GetItemRectMax();
            ImDrawListPtr draw = ImGui.GetWindowDrawList();
            draw.AddRectFilled(tileMin + new Vector2(35, 28), tileMin + new Vector2(103, 78),
                ImGui.GetColorU32(ImGuiCol.Header), 5);
            draw.AddRectFilled(tileMin + new Vector2(42, 22), tileMin + new Vector2(72, 32),
                ImGui.GetColorU32(ImGuiCol.Header), 3);
            draw.AddText(tileMin + new Vector2(8, 94), ImGui.GetColorU32(ImGuiCol.Text), folder);
            draw.AddText(tileMin + new Vector2(8, 113), ImGui.GetColorU32(ImGuiCol.TextDisabled), "Folder");

            // A folder tile is opened with one click, matching the rest of
            // the browser and avoiding an invisible double-click requirement.
            if (clicked)
            {
                _currentFolder = CombineFolderPath(_currentFolder, folder);
                _selectedPath = "";
                _status = $"Opened folder: {_currentFolder}";
            }
            ImGui.PopID();

            column++;
            if (column >= columns)
                column = 0;
        }

        foreach (EditorAssetEntry entry in FilteredEntries())
        {
            if (column > 0)
                ImGui.SameLine();

            ImGui.PushID(entry.RelativePath);
            Vector4 tileColor = entry.Broken
                ? new Vector4(0.26f, 0.08f, 0.08f, 1)
                : entry.RelativePath.Equals(_selectedPath, StringComparison.OrdinalIgnoreCase)
                    ? new Vector4(0.12f, 0.28f, 0.45f, 1)
                    : new Vector4(0.12f, 0.14f, 0.18f, 1);
            ImGui.PushStyleColor(ImGuiCol.Button, tileColor);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.20f, 0.36f, 0.52f, 1));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.24f, 0.42f, 0.60f, 1));
            bool clicked = ImGui.Button("##AssetTile", new Vector2(tileWidth - 8, tileHeight));
            ImGui.PopStyleColor(3);

            Vector2 tileMin = ImGui.GetItemRectMin();
            Vector2 tileMax = ImGui.GetItemRectMax();
            bool tileVisible = ImGui.IsRectVisible(tileMin, tileMax);
            if (!tileVisible)
            {
                ImGui.PopID();
                column++;
                if (column >= columns)
                    column = 0;
                continue;
            }

            DrawThumbnail(assetService, entry, tileMin + new Vector2(10, 8), new Vector2(tileWidth - 28, 82));
            ImGui.GetWindowDrawList().AddText(tileMin + new Vector2(8, 94),
                ImGui.GetColorU32(entry.Broken ? ImGuiCol.TextDisabled : ImGuiCol.Text),
                entry.Broken ? "[!] BROKEN" : entry.DisplayName);
            ImGui.GetWindowDrawList().AddText(tileMin + new Vector2(8, 113),
                ImGui.GetColorU32(ImGuiCol.TextDisabled), KindLabel(entry.Kind));

            if (clicked)
                _selectedPath = entry.RelativePath;
            if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                Activate(entry, materialEditor, geometryEditor, activate);

            if (ImGui.BeginDragDropSource())
            {
                AssetDragDrop.Publish(entry);
                ImGui.Text($"{KindLabel(entry.Kind)}: {entry.DisplayName}");
                ImGui.SetDragDropPayload("BLOWTORCH_ASSET", IntPtr.Zero, 0);
                ImGui.EndDragDropSource();
            }

            if (ImGui.BeginPopupContextItem("AssetContextMenu"))
            {
                ImGui.TextDisabled(entry.RelativePath);
                ImGui.Separator();
                if (ImGui.MenuItem("Delete asset..."))
                    RequestDelete(entry);
                ImGui.EndPopup();
            }
            ImGui.PopID();

            column++;
            if (column >= columns)
                column = 0;
        }

        if (_entries.Count == 0)
            ImGui.TextDisabled("No assets found in Fuse/res.");
        else if (FilteredEntries().Count == 0)
            ImGui.TextDisabled(VisibleFolders().Count > 0
                ? "Open a folder or change the current filter."
                : "No asset matches the current folder or filter.");
    }

    private void DrawThumbnail(EditorAssetService assetService, EditorAssetEntry entry, Vector2 min, Vector2 size)
    {
        ImDrawListPtr draw = ImGui.GetWindowDrawList();
        Vector2 max = min + size;
        draw.AddRectFilled(min, max, ImGui.GetColorU32(ImGuiCol.FrameBg), 4);

        if (entry.Kind is EditorAssetKind.Texture or EditorAssetKind.Skybox)
        {
            uint texture = assetService.RequestTexturePreview(entry.RelativePath);
            if (texture != 0)
                draw.AddImage((IntPtr)texture, min, max, new Vector2(0, 1), new Vector2(1, 0));
            else
            {
                entry.Broken = true;
                entry.Error = "Texture could not be decoded.";
                DrawBroken(draw, min, max);
            }
            return;
        }

        if (entry.Kind == EditorAssetKind.Material)
        {
            Vector3 color = assetService.GetMaterialThumbnailColor(entry.RelativePath);
            draw.AddRectFilled(min + new Vector2(14, 10), max - new Vector2(14, 10),
                ImGui.ColorConvertFloat4ToU32(new Vector4(color, 1)), 4);
            draw.AddCircleFilled((min + max) * 0.5f, MathF.Min(size.X, size.Y) * 0.23f,
                ImGui.ColorConvertFloat4ToU32(new Vector4(0.8f, 0.86f, 0.95f, 0.45f)));
            return;
        }

        // A lightweight drawn model thumbnail keeps the browser responsive
        // even when a project contains hundreds of large imported models.
        Vector2 center = (min + max) * 0.5f;
        float s = MathF.Min(size.X, size.Y) * 0.28f;
        Vector2 top = center + new Vector2(0, -s);
        Vector2 left = center + new Vector2(-s, -s * 0.45f);
        Vector2 right = center + new Vector2(s, -s * 0.45f);
        Vector2 bottom = center + new Vector2(0, s * 0.55f);
        uint modelColor = ImGui.GetColorU32(ImGuiCol.Text);
        draw.AddLine(top, left, modelColor, 2); draw.AddLine(top, right, modelColor, 2);
        draw.AddLine(left, bottom, modelColor, 2); draw.AddLine(right, bottom, modelColor, 2);
        draw.AddLine(left, right, modelColor, 2);
        draw.AddText(center - new Vector2(12, 7), ImGui.GetColorU32(ImGuiCol.TextDisabled), "3D");
    }

    private void DrawDetails(EditorAssetService assetService, MaterialEditorWindow materialEditor, GeometryGraphEditorWindow geometryEditor, Action<EditorAssetEntry>? activate)
    {
        EditorAssetEntry? entry = FindSelected();
        if (entry == null)
        {
            ImGui.TextDisabled("Select an asset to inspect it.");
            return;
        }

        ImGui.TextColored(new Vector4(0.75f, 0.86f, 1, 1), entry.DisplayName);
        ImGui.TextDisabled(KindLabel(entry.Kind));
        ImGui.Separator();
        ImGui.TextWrapped(entry.RelativePath);
        ImGui.TextDisabled(entry.FullPath);
        ImGui.Spacing();

        assetService.GetAssetStatus(
            entry.Kind,
            entry.RelativePath,
            out string currentError,
            validateContents: entry.Kind is EditorAssetKind.Material or EditorAssetKind.GeometryGraph);
        if (!string.IsNullOrEmpty(currentError))
        {
            entry.Broken = true;
            entry.Error = currentError;
        }

        if (entry.Broken)
            ImGui.TextColored(new Vector4(1, 0.28f, 0.25f, 1), $"[!] {entry.Error}");
        else
            ImGui.TextColored(new Vector4(0.35f, 0.9f, 0.5f, 1), "Ready");

        if (entry.Kind is EditorAssetKind.Texture or EditorAssetKind.Skybox)
        {
            uint texture = assetService.RequestTexturePreview(entry.RelativePath);
            if (texture != 0)
            {
                ImGui.Spacing();
                ImGui.Image((IntPtr)texture, new Vector2(MathF.Min(240, ImGui.GetContentRegionAvail().X), 180),
                    new Vector2(0, 1), new Vector2(1, 0));
            }
        }

        ImGui.Spacing();
        string activateLabel = _texturePickerCallback != null ? "Use Selected Texture"
            : _geometryPickerCallback != null ? "Use Selected Graph" : "Open / Apply";
        if (ImGui.Button(activateLabel, new Vector2(-1, 0)))
            Activate(entry, materialEditor, geometryEditor, activate);
        if (ImGui.Button("Reimport", new Vector2(-1, 0)))
            ReimportSelected(assetService, null, activate);
        ImGui.TextDisabled("Drag this asset to a viewport or to the Material Graph.");
    }

    private void Activate(EditorAssetEntry entry, MaterialEditorWindow materialEditor, GeometryGraphEditorWindow geometryEditor, Action<EditorAssetEntry>? activate)
    {
        _selectedPath = entry.RelativePath;
        if (_texturePickerCallback != null &&
            entry.Kind is EditorAssetKind.Texture or EditorAssetKind.Skybox)
        {
            Action<string> callback = _texturePickerCallback;
            _texturePickerCallback = null;
            callback(entry.RelativePath);
            _status = $"Selected {entry.DisplayName}.";
            IsOpen = false;
            return;
        }
        if (_geometryPickerCallback != null && entry.Kind == EditorAssetKind.GeometryGraph)
        {
            Action<string> callback = _geometryPickerCallback;
            _geometryPickerCallback = null;
            callback(entry.RelativePath);
            _status = $"Selected {entry.DisplayName}.";
            IsOpen = false;
            return;
        }
        if (entry.Kind == EditorAssetKind.Material)
            materialEditor.Open(entry.RelativePath);
        if (entry.Kind == EditorAssetKind.GeometryGraph)
            geometryEditor.Open(entry.RelativePath);
        activate?.Invoke(entry);
    }

    private void ReimportSelected(EditorAssetService assetService, EditorSceneService? sceneService, Action<EditorAssetEntry>? activate)
    {
        EditorAssetEntry? entry = FindSelected();
        if (entry == null)
            return;

        if (assetService.ReimportAsset(entry.Kind, entry.RelativePath, out string error))
        {
            _status = $"Reimported {entry.DisplayName}.";
            _refreshRequested = true;
            if (sceneService != null)
            {
                if (entry.Kind is EditorAssetKind.Model or EditorAssetKind.GeometryGraph)
                    sceneService.PopulateScene(assetService);
                else
                    sceneService.RefreshMaterials(assetService);
            }
        }
        else
        {
            _status = error;
            _refreshRequested = true;
        }
    }

    private void Refresh(EditorAssetService assetService)
    {
        _entries.Clear();
        AddEntries(assetService.EnumerateModels(), EditorAssetKind.Model, assetService);
        AddEntries(assetService.EnumerateMaterials(), EditorAssetKind.Material, assetService);
        AddEntries(assetService.EnumerateGeometryGraphs(), EditorAssetKind.GeometryGraph, assetService);
        HashSet<string> skyboxes = assetService.EnumerateSkyboxes().ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string texture in assetService.EnumerateTextures())
            AddEntry(assetService, texture, skyboxes.Contains(texture) ? EditorAssetKind.Skybox : EditorAssetKind.Texture);
        _catalogRevision = assetService.AssetRevision;
        _refreshRequested = false;
    }

    private void AddEntries(IEnumerable<string> paths, EditorAssetKind kind, EditorAssetService assetService)
    {
        foreach (string path in paths)
            AddEntry(assetService, path, kind);
    }

    private void AddEntry(EditorAssetService assetService, string relativePath, EditorAssetKind kind)
    {
        string normalized = relativePath.Replace('\\', '/');
        assetService.GetAssetStatus(kind, normalized, out string error);
        _entries.Add(new EditorAssetEntry
        {
            RelativePath = normalized,
            FullPath = assetService.ResolveEditorAssetPath(normalized),
            Kind = kind,
            Broken = !string.IsNullOrEmpty(error),
            Error = error
        });
    }

    private EditorAssetEntry? FindSelected() => _entries.FirstOrDefault(entry =>
        entry.RelativePath.Equals(_selectedPath, StringComparison.OrdinalIgnoreCase));

    private List<EditorAssetEntry> FilteredEntries()
    {
        string filter = _search.Trim();
        EditorAssetKind? kind = _filter switch
        {
            1 => EditorAssetKind.Model,
            2 => EditorAssetKind.Material,
            3 => EditorAssetKind.Texture,
            4 => EditorAssetKind.Skybox,
            5 => EditorAssetKind.GeometryGraph,
            _ => null
        };
        return _entries.Where(entry =>
            (!kind.HasValue || entry.Kind == kind.Value) &&
            DirectoryPath(entry.RelativePath).Equals(_currentFolder, StringComparison.OrdinalIgnoreCase) &&
            (string.IsNullOrEmpty(filter) || entry.RelativePath.Contains(filter, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    private List<string> VisibleFolders()
    {
        EditorAssetKind? kind = _filter switch
        {
            1 => EditorAssetKind.Model,
            2 => EditorAssetKind.Material,
            3 => EditorAssetKind.Texture,
            4 => EditorAssetKind.Skybox,
            5 => EditorAssetKind.GeometryGraph,
            _ => null
        };
        string prefix = string.IsNullOrEmpty(_currentFolder) ? "" : _currentFolder.TrimEnd('/') + "/";
        return _entries
            .Where(entry => !kind.HasValue || entry.Kind == kind.Value)
            .Select(entry => entry.RelativePath.Replace('\\', '/'))
            .Where(path => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(path => path[prefix.Length..])
            .Where(rest => rest.Contains('/'))
            .Select(rest => rest[..rest.IndexOf('/')])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(folder => folder, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string DirectoryPath(string relativePath)
    {
        string normalized = relativePath.Replace('\\', '/');
        int separator = normalized.LastIndexOf('/');
        return separator >= 0 ? normalized[..separator] : "";
    }

    private static string CombineFolderPath(string parent, string child) =>
        string.IsNullOrEmpty(parent) ? child : $"{parent.TrimEnd('/')}/{child}";

    private void RequestDelete(EditorAssetEntry entry)
    {
        _pendingDeletePath = entry.RelativePath;
        _pendingDeleteKind = entry.Kind;
        _showDeleteConfirmation = true;
    }

    private void DrawDeleteConfirmation(
        EditorAssetService assetService,
        EditorSceneService sceneService,
        MaterialEditorWindow materialEditor)
    {
        if (!_showDeleteConfirmation)
            return;

        ImGui.OpenPopup("Dangerous Asset Deletion##AssetBrowser");
        bool popupOpen = true;
        if (ImGui.BeginPopupModal("Dangerous Asset Deletion##AssetBrowser", ref popupOpen,
                ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.TextColored(new Vector4(1.0f, 0.28f, 0.22f, 1.0f), "WARNING: dangerous operation");
            ImGui.Spacing();
            ImGui.TextWrapped("This will remove the asset from the project and send it to the Windows Recycle Bin. " +
                              "References to it may stop working until it is restored.");
            ImGui.Spacing();
            ImGui.TextDisabled(_pendingDeletePath);
            ImGui.Spacing();
            if (ImGui.Button("Send to Recycle Bin", new Vector2(190, 0)))
            {
                string deletedPath = _pendingDeletePath;
                if (assetService.SendAssetToRecycleBin(_pendingDeleteKind, deletedPath, out string error))
                {
                    _selectedPath = "";
                    _status = $"Moved {Path.GetFileName(deletedPath)} to the Recycle Bin.";
                    _refreshRequested = true;
                    if (_pendingDeleteKind == EditorAssetKind.Material)
                        materialEditor.HandleDeletedMaterial(deletedPath);
                    if (_pendingDeleteKind is EditorAssetKind.Model or EditorAssetKind.GeometryGraph)
                        sceneService.PopulateScene(assetService);
                    else
                        sceneService.RefreshMaterials(assetService);
                }
                else
                {
                    _status = error;
                }

                _showDeleteConfirmation = false;
                _pendingDeletePath = "";
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if (ImGui.Button("Cancel", new Vector2(100, 0)))
            {
                _showDeleteConfirmation = false;
                _pendingDeletePath = "";
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndPopup();
        }

        if (!popupOpen)
        {
            _showDeleteConfirmation = false;
            _pendingDeletePath = "";
        }
    }

    private static string KindLabel(EditorAssetKind kind) => kind switch
    {
        EditorAssetKind.Model => "Model",
        EditorAssetKind.Material => "Material",
        EditorAssetKind.Skybox => "Skybox",
        EditorAssetKind.GeometryGraph => "Geometry Graph",
        _ => "Texture"
    };

    private static void DrawBroken(ImDrawListPtr draw, Vector2 min, Vector2 max)
    {
        uint red = ImGui.GetColorU32(ImGuiCol.TextDisabled);
        draw.AddLine(min, max, red, 2);
        draw.AddLine(new Vector2(max.X, min.Y), new Vector2(min.X, max.Y), red, 2);
        draw.AddText((min + max) * 0.5f - new Vector2(18, 7), red, "MISSING");
    }
}
