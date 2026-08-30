using System;
using System.IO;
using System.Linq;
using System.Numerics;
using Fuse.Scene.Model;
using Fuse.Renderer;
using Fuse.Core;

namespace Blowtorch;

public class EditorSceneService
{
    private MapDocument _doc = null!;
    private Scene _scene = null!;
    private string _mapPath = "";
    private string _cachedSnapshot = "";
    private ulong _cachedSnapshotRevision = ulong.MaxValue;
    private ulong _revision;
    private bool _isDirty;

    public MapDocument Document => _doc;
    public Scene Scene => _scene;
    public string MapPath => _mapPath;
    public ulong Revision => _revision;
    public bool IsDirty => _isDirty;
    public string LastError { get; private set; } = "";
    public IReadOnlyList<string> ValidationWarnings => _doc?.ValidationWarnings ?? [];

    public void LoadMap(string fuseResPath)
    {
        string defaultPath = Path.Combine(fuseResPath, "Maps", "default.bth");
        if (!TryOpenMap(defaultPath, out string error))
        {
            _doc = CreateEmptyDocument();
            _scene = new Scene();
            _mapPath = "";
            _revision++;
            _isDirty = false;
            InvalidateSnapshot();
            LastError = error;
            Logger.Error(error);
        }
    }

    public bool TryOpenMap(string path, out string error)
    {
        if (!MapDocument.TryLoad(path, out MapDocument? document, out error) || document == null)
        {
            LastError = error;
            return false;
        }

        _doc = document;
        _scene = new Scene();
        _mapPath = Path.GetFullPath(path);
        _revision++;
        _isDirty = false;
        LastError = "";
        InvalidateSnapshot();
        _cachedSnapshot = _doc.Serialize();
        _cachedSnapshotRevision = _revision;
        foreach (string warning in _doc.ValidationWarnings)
            Logger.Warn($"Map validation: {warning}");
        Logger.Important($"CURRENT MAP LOADED: {_mapPath}");
        return true;
    }

    public void SetDocument(MapDocument doc, bool markClean = false)
    {
        _doc = doc ?? throw new ArgumentNullException(nameof(doc));
        SceneNameManager.EnsureAllUnique(_doc);
        _doc.ValidationWarnings.AddRange(SceneNameManager.ValidateAndRepairHierarchy(_doc));
        _revision++;
        _isDirty = !markClean;
        LastError = "";
        InvalidateSnapshot();
        if (markClean)
        {
            _cachedSnapshot = _doc.Serialize();
            _cachedSnapshotRevision = _revision;
        }
    }

    public void SetMapPath(string path)
    {
        _mapPath = string.IsNullOrWhiteSpace(path) ? "" : Path.GetFullPath(path);
    }

    public string CaptureSnapshot()
    {
        if (_cachedSnapshotRevision != _revision)
        {
            _cachedSnapshot = _doc.Serialize();
            _cachedSnapshotRevision = _revision;
        }
        return _cachedSnapshot;
    }

    public void MarkModified(string? knownSnapshot = null)
    {
        _revision++;
        _isDirty = true;
        if (knownSnapshot == null)
        {
            InvalidateSnapshot();
        }
        else
        {
            _cachedSnapshot = knownSnapshot;
            _cachedSnapshotRevision = _revision;
        }
    }

    public void PopulateScene(EditorAssetService assetService)
    {
        _scene = new Scene();

        foreach (var mapObj in _doc.Objects)
        {
            if (mapObj.IsLight)
            {
                var pos = mapObj.Body?.Position ?? System.Numerics.Vector3.Zero;
                var rot = mapObj.Body?.Rotation ?? System.Numerics.Quaternion.Identity;
                var dir = System.Numerics.Vector3.Transform(-System.Numerics.Vector3.UnitY, rot);

                var light = new Light
                {
                    Id = mapObj.Id,
                    Type = mapObj.LightType == "directional" ? LightType.Directional : (mapObj.LightType == "spot" ? LightType.Spot : LightType.Point),
                    Position = pos,
                    Direction = dir,
                    Color = mapObj.LightColor,
                    Intensity = mapObj.LightIntensity,
                    Radius = mapObj.LightRadius,
                    InnerConeAngle = mapObj.LightInnerCone,
                    OuterConeAngle = mapObj.LightOuterCone,
                    CastShadows = mapObj.LightCastShadows,
                    ShadowBias = mapObj.LightShadowBias,
                    Dynamic = mapObj.LightDynamic,
                    Enabled = mapObj.IsGloballyVisible(_doc),
                };
                _scene.AddLight(light);
                continue;
            }

            var mesh = assetService.GetOrCreateMesh(mapObj);
            if (mesh == null) continue;

            var entity = _scene.Add(mesh, mapObj.Id);
            entity.ParentId = mapObj.ParentId ?? "";
            entity.MapData = MapDocument.SerializeObject(mapObj);
            entity.MeshKey = mapObj.Mesh ?? mapObj.Model ?? "";
            entity.MaterialPath = mapObj.MaterialPath ?? "";
            entity.MaterialPaths = mapObj.MaterialSlots.ToList();
            entity.Material = assetService.GetOrCreateMaterial(mapObj.MaterialPath);
            foreach (string slot in mapObj.MaterialSlots)
                entity.Materials.Add(assetService.GetOrCreateMaterial(slot));
            bool isTrigger = mapObj.Body?.IsTrigger == true;
            entity.TexturePath = isTrigger ? "Textures/tools/toolstrigger.bmp" : (mapObj.Texture ?? "");
            entity.Visible = mapObj.IsGloballyVisible(_doc);
            entity.ModelScale = mapObj.ModelScale;
            entity.UvScale = mapObj.UvScale;
            entity.UvOffset = mapObj.UvOffset;
            entity.UvRotation = mapObj.UvRotation;

            if (mapObj is Brush)
            {
                entity.Transform.Scale = System.Numerics.Vector3.One;
                if (mapObj.Body != null)
                {
                    entity.Transform.Position = mapObj.Body.Position;
                    entity.Transform.Rotation = mapObj.Body.Rotation;
                }
            }
            else if (mapObj.Body != null)
            {
                entity.Transform.Position = mapObj.Body.Position;
                entity.Transform.Rotation = mapObj.Body.Rotation;
                
                if (!mapObj.IsModel && mapObj.Body.Shape == MapShapeType.Box && mapObj.Body.HalfExtents.HasValue)
                {
                    entity.Transform.Scale = mapObj.Body.HalfExtents.Value * 2.0f;
                }
                else if (!mapObj.IsModel && mapObj.Body.Shape == MapShapeType.Sphere && mapObj.Body.Radius.HasValue)
                {
                    entity.Transform.Scale = new System.Numerics.Vector3(mapObj.Body.Radius.Value * 2.0f);
                }
                else
                {
                    entity.Transform.Scale = mapObj.ModelScale;
                }
            }
            else
            {
                entity.Transform.Scale = mapObj.ModelScale;
            }

            if (isTrigger)
            {
                assetService.GetOrCreateTexture(entity.TexturePath);
                entity.Material = assetService.AssetManager.GetLegacyMaterial(entity.TexturePath);
                entity.Materials.Clear();
            }
            else if (!string.IsNullOrEmpty(mapObj.Texture))
            {
                assetService.GetOrCreateTexture(mapObj.Texture);
                if (entity.Material == null)
                    entity.Material = assetService.AssetManager.GetLegacyMaterial(mapObj.Texture);
            }
        }

        var entitiesById = _scene.Entities.ToDictionary(entity => entity.Id, StringComparer.OrdinalIgnoreCase);
        foreach (Entity entity in _scene.Entities)
        {
            if (string.IsNullOrEmpty(entity.ParentId) ||
                !entitiesById.TryGetValue(entity.ParentId, out Entity? parent))
                continue;

            Vector3 globalOffset = entity.Transform.Position - parent.Transform.Position;
            Quaternion inverseParentRotation = Quaternion.Inverse(parent.Transform.Rotation);
            entity.InitialRelativePosition = Vector3.Transform(globalOffset, inverseParentRotation);
            entity.InitialRelativeRotation = Quaternion.Normalize(inverseParentRotation * entity.Transform.Rotation);
        }
    }

    public bool SaveMap()
    {
        LastError = "";
        if (string.IsNullOrEmpty(_mapPath) || _doc == null)
        {
            LastError = "Choose a map filename before saving.";
            return false;
        }

        string? temporaryPath = null;
        try
        {
            string? directory = Path.GetDirectoryName(_mapPath);
            if (string.IsNullOrEmpty(directory))
                throw new InvalidOperationException("The selected map path has no parent directory.");

            Directory.CreateDirectory(directory);
            string json = _doc.Serialize();
            temporaryPath = Path.Combine(directory, $".{Path.GetFileName(_mapPath)}.{Guid.NewGuid():N}.tmp");
            File.WriteAllText(temporaryPath, json);
            File.Move(temporaryPath, _mapPath, true);
            temporaryPath = null;

            _cachedSnapshot = json;
            _cachedSnapshotRevision = _revision;
            _isDirty = false;
            Logger.Info($"Map saved to {_mapPath}");
            return true;
        }
        catch (Exception ex)
        {
            LastError = $"Could not save map: {ex.Message}";
            Logger.Error(LastError);
            return false;
        }
        finally
        {
            if (temporaryPath != null && File.Exists(temporaryPath))
            {
                try { File.Delete(temporaryPath); }
                catch { }
            }
        }
    }

    public bool SaveMapAs(string path)
    {
        string previousPath = _mapPath;
        try
        {
            _mapPath = Path.GetFullPath(path);
        }
        catch (Exception ex)
        {
            LastError = $"Invalid map path: {ex.Message}";
            return false;
        }

        if (SaveMap())
            return true;

        _mapPath = previousPath;
        return false;
    }

    public void RefreshMaterials(EditorAssetService assetService)
    {
        foreach (var entity in _scene.Entities)
        {
            entity.Material = !string.IsNullOrWhiteSpace(entity.MaterialPath)
                ? assetService.GetOrCreateMaterial(entity.MaterialPath)
                : (!string.IsNullOrWhiteSpace(entity.TexturePath)
                    ? assetService.AssetManager.GetLegacyMaterial(entity.TexturePath)
                    : null);
            entity.Materials.Clear();
            foreach (string slot in entity.MaterialPaths)
                entity.Materials.Add(assetService.GetOrCreateMaterial(slot));
        }
    }

    private void InvalidateSnapshot()
    {
        _cachedSnapshot = "";
        _cachedSnapshotRevision = ulong.MaxValue;
    }

    private static MapDocument CreateEmptyDocument() => new()
    {
        PlayerSpawn = new MapPlayerSpawn
        {
            Position = Vector3.Zero,
            Yaw = 0,
            Pitch = 0
        }
    };
}
