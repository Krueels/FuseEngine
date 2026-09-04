using System;
using System.IO;
using System.Linq;
using System.Numerics;
using Fuse.Math;
using Fuse.Scene.Model;
using Fuse.Renderer;
using Fuse.Core;
using Fuse.Scene.Terrain;
using Brush = Fuse.Scene.Model.Brush;

namespace Blowtorch;

public class EditorSceneService
{
    private const string ProceduralPreviewGroupId = "__blowtorch_procedural_preview__";
    private MapDocument _doc = null!;
    private Scene _scene = null!;
    // The terrain generator preview must not become part of the open map. It
    // owns a separate transient scene so the modal can render it to its own
    // framebuffer without changing the main editor viewports.
    private readonly Scene _proceduralPreviewScene = new();
    private string _mapPath = "";
    private string _cachedSnapshot = "";
    private ulong _cachedSnapshotRevision = ulong.MaxValue;
    private ulong _revision;
    private bool _isDirty;
    private readonly Dictionary<string, (TerrainTileSetAsset Asset, DateTime LastWriteUtc, long Length)> _terrainCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _terrainEditorForceLod0 = new(StringComparer.OrdinalIgnoreCase);
    private readonly ProceduralGrassDensityMaskStore _grassDensityMasks = new();
    private bool _proceduralPreviewActive;

    public MapDocument Document => _doc;
    public Scene Scene => _scene;
    public Scene ProceduralPreviewScene => _proceduralPreviewScene;
    public string MapPath => _mapPath;
    public ulong Revision => _revision;
    public bool IsDirty => _isDirty;
    public string LastError { get; private set; } = "";
    public IReadOnlyList<string> ValidationWarnings => _doc?.ValidationWarnings ?? [];
    public bool RequiresContinuousProceduralGrassRender =>
        _scene != null && _scene.ProceduralTerrainLayers.Any(static layer =>
            layer.Visible && layer.Asset.Settings.Grass.Enabled);

    public void LoadMap(string fuseResPath)
    {
        string defaultPath = Path.Combine(fuseResPath, "Maps", "default.bth");
        if (!TryOpenMap(defaultPath, out string error))
        {
            _doc = CreateEmptyDocument();
            _scene = new Scene();
            _proceduralPreviewScene.Clear();
            _mapPath = "";
            _terrainEditorForceLod0.Clear();
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
        _proceduralPreviewScene.Clear();
        _terrainCache.Clear();
        _terrainEditorForceLod0.Clear();
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
        _proceduralPreviewScene.Clear();
        _terrainCache.Clear();
        _terrainEditorForceLod0.Clear();
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
        _scene?.Clear();
        _scene = new Scene();
        ClearProceduralTerrainPreview();

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

            if (mapObj.IsTerrain)
            {
                TerrainTileSetAsset? terrainSet = TryLoadTerrainTileSet(mapObj, assetService);
                if (terrainSet == null)
                    continue;

                Vector3 terrainPosition = mapObj.Body?.Position ?? Vector3.Zero;
                Quaternion terrainRotation = mapObj.Body?.Rotation ?? Quaternion.Identity;
                float terrainFriction = mapObj.Body?.Friction ?? 0.5f;
                float terrainRestitution = mapObj.Body?.Restitution ?? 0.0f;

                // Keep a document-level entity for hierarchy/selection. The
                // renderable chunks are children identified by generated IDs.
                Entity rootEntity = _scene.Add(null, mapObj.Id);
                rootEntity.ParentId = mapObj.ParentId ?? "";
                rootEntity.MapData = MapDocument.SerializeObject(mapObj);
                rootEntity.Visible = mapObj.IsGloballyVisible(_doc);
                rootEntity.Transform.Position = terrainPosition;
                rootEntity.Transform.Rotation = terrainRotation;

                TerrainSceneBuilder.AddToScene(
                    _scene,
                    terrainSet,
                    mapObj.Id,
                    terrainPosition,
                    terrainRotation,
                    rootEntity.Visible,
                    mapObj.TerrainChunkQuads,
                    mapObj.MaterialPath ?? "",
                    mapObj.MaterialSlots,
                    mapObj.Texture ?? "",
                    mapObj.UvScale,
                    mapObj.UvOffset,
                    mapObj.UvRotation,
                    assetService.AssetManager,
                    null,
                    null,
                    mapObj.ParentId ?? "",
                    terrainFriction,
                    terrainRestitution,
                    mapObj.TerrainPixelError,
                    mapObj.TerrainCollisionLod,
                    IsTerrainEditorForceLod0(mapObj.Id));

                if (terrainSet.Procedural != null && rootEntity.Visible)
                {
                    var proceduralLayer = new ProceduralTerrainLayer(
                        mapObj.Id,
                        terrainSet.Procedural,
                        terrainPosition,
                        terrainRotation,
                        rootEntity.Visible,
                        mapObj.TerrainChunkQuads,
                        mapObj.MaterialPath ?? "",
                        mapObj.MaterialSlots,
                        mapObj.Texture ?? "",
                        mapObj.UvScale,
                        mapObj.UvOffset,
                        mapObj.UvRotation,
                        mapObj.ParentId ?? "",
                        terrainFriction,
                        terrainRestitution,
                        mapObj.TerrainPixelError,
                        mapObj.TerrainCollisionLod);
                    foreach (TerrainTile tile in terrainSet.Tiles)
                        proceduralLayer.MarkInitialTile(tile, false);
                    _scene.RegisterProceduralTerrain(proceduralLayer);
                }
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

            if (mapObj.IsStaircase)
            {
                // The staircase generator already emits vertices in the
                // object's authored bounds. Keep the render scale neutral so
                // the mesh and its compound collision share one size.
                entity.Transform.Scale = System.Numerics.Vector3.One;
                if (mapObj.Body != null)
                {
                    entity.Transform.Position = mapObj.Body.Position;
                    entity.Transform.Rotation = mapObj.Body.Rotation;
                }
            }
            else if (mapObj is Brush)
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
                    entity.Transform.Scale = MeshGenerator.GetSphereRenderScale(mapObj.Body.Radius.Value);
                }
                else if (!mapObj.IsModel && mapObj.Body.Shape == MapShapeType.Capsule &&
                         mapObj.Body.Radius.HasValue && mapObj.Body.Height.HasValue)
                {
                    entity.Transform.Scale = MeshGenerator.GetCapsuleRenderScale(
                        mapObj.Body.Radius.Value,
                        mapObj.Body.Height.Value);
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

    /// <summary>
    /// Replaces the transient procedural preview in the editor scene. The
    /// preview is intentionally not a MapObject and is never serialized; it
    /// uses the same generator and terrain chunk builder as the runtime.
    /// </summary>
    public int UpdateProceduralTerrainPreview(
        ProceduralTerrainSettings settings,
        int chunkQuads,
        string materialPath,
        IReadOnlyList<string>? materialPaths,
        string texturePath,
        Vector2 uvScale,
        Vector2 uvOffset,
        float uvRotation,
        EditorAssetService assetService)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(assetService);

        ClearProceduralTerrainPreview();
        var procedural = new ProceduralTerrainAsset(settings);
        TerrainTileSetAsset preview = TerrainTileSetAsset.FromProcedural(procedural);
        int chunkCount = TerrainSceneBuilder.AddToScene(
            _proceduralPreviewScene,
            preview,
            ProceduralPreviewGroupId,
            Vector3.Zero,
            Quaternion.Identity,
            true,
            chunkQuads,
            materialPath ?? "",
            materialPaths,
            texturePath ?? "",
            uvScale,
            uvOffset,
            uvRotation,
            assetService.AssetManager,
            null,
            null,
            "",
            0.5f,
            0.0f,
            settings.LodPixelError,
            TerrainSceneBuilder.DefaultCollisionLod,
            false);
        if (chunkCount > 0)
        {
            var previewLayer = new ProceduralTerrainLayer(
                ProceduralPreviewGroupId,
                procedural,
                Vector3.Zero,
                Quaternion.Identity,
                true,
                chunkQuads,
                materialPath ?? "",
                materialPaths,
                texturePath ?? "",
                uvScale,
                uvOffset,
                uvRotation,
                "",
                0.5f,
                0.0f,
                settings.LodPixelError,
                TerrainSceneBuilder.DefaultCollisionLod);
            foreach (TerrainTile tile in preview.Tiles)
                previewLayer.MarkInitialTile(tile, false);
            _proceduralPreviewScene.RegisterProceduralTerrain(previewLayer);
        }
        _proceduralPreviewActive = chunkCount > 0;
        return chunkCount;
    }

    public void ClearProceduralTerrainPreview()
    {
        _proceduralPreviewScene.Clear();
        _proceduralPreviewActive = false;
    }

    public TerrainTileSetAsset? TryLoadTerrainTileSet(MapObject mapObj, EditorAssetService assetService)
    {
        if (!mapObj.IsTerrain)
            return null;

        try
        {
            string path = assetService.ResolveEditorAssetPath(mapObj.TerrainAssetPath!);
            if (!File.Exists(path))
            {
                Logger.Warn($"Terrain asset not found: {path}");
                return null;
            }

            FileInfo fileInfo = new(path);
            DateTime lastWriteUtc = fileInfo.LastWriteTimeUtc;
            long length = fileInfo.Length;
            if (_terrainCache.TryGetValue(path, out var cached) &&
                cached.LastWriteUtc == lastWriteUtc &&
                cached.Length == length)
            {
                return cached.Asset;
            }

            TerrainTileSetAsset loaded = TerrainTileSetAsset.Load(path);
            _terrainCache[path] = (loaded, lastWriteUtc, length);
            return loaded;
        }
        catch (Exception ex)
        {
            Logger.Warn($"Terrain asset could not be loaded for '{mapObj.Id}': {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Gets the world-space bounds of the terrain chunks already present in
    /// the editor scene. Selection overlays must use these resident render
    /// chunks instead of loading/regenerating the terrain asset again.
    /// </summary>
    public bool TryGetTerrainRenderBounds(string terrainId, out AABB bounds)
    {
        bounds = new AABB();
        if (string.IsNullOrWhiteSpace(terrainId))
            return false;

        bool found = false;
        foreach (Entity entity in _scene.Entities)
        {
            if (entity.TerrainLod == null ||
                !string.Equals(entity.TerrainChunkGroupId, terrainId, StringComparison.OrdinalIgnoreCase) ||
                !entity.Visible)
                continue;

            AABB worldBounds = entity.GetWorldRenderBounds();
            if (!worldBounds.IsValid)
                continue;

            bounds.Grow(worldBounds);
            found = true;
        }

        return found;
    }

    public TerrainAsset? TryLoadTerrainAsset(MapObject mapObj, EditorAssetService assetService)
    {
        return TryLoadTerrainTileSet(mapObj, assetService)?.Primary.Asset;
    }

    public bool CreateTerrainNeighbor(
        MapObject mapObj,
        EditorAssetService assetService,
        int sourceX,
        int sourceZ,
        int offsetX,
        int offsetZ)
    {
        TerrainTileSetAsset? terrainSet = TryLoadTerrainTileSet(mapObj, assetService);
        if (terrainSet == null ||
            !terrainSet.TryCreateNeighbor(
                sourceX,
                sourceZ,
                offsetX,
                offsetZ,
                out _))
            return false;

        string path = assetService.ResolveEditorAssetPath(mapObj.TerrainAssetPath!);
        if (SaveTerrainAsset(path))
            return true;

        terrainSet.TryRemoveTile(
            sourceX + offsetX,
            sourceZ + offsetZ);
        return false;
    }

    public bool DeleteTerrainNeighbor(
        MapObject mapObj,
        EditorAssetService assetService,
        int tileX,
        int tileZ)
    {
        TerrainTileSetAsset? terrainSet = TryLoadTerrainTileSet(mapObj, assetService);
        if (terrainSet == null ||
            !terrainSet.TryRemoveTile(tileX, tileZ, out TerrainTile? removed) ||
            removed == null)
            return false;

        string path = assetService.ResolveEditorAssetPath(mapObj.TerrainAssetPath!);
        if (SaveTerrainAsset(path))
            return true;

        terrainSet.TryRestoreTile(removed);
        return false;
    }

    public bool IsTerrainEditorForceLod0(string terrainId) =>
        !string.IsNullOrWhiteSpace(terrainId) &&
        _terrainEditorForceLod0.Contains(terrainId);

    public bool SetTerrainEditorForceLod0(string terrainId, bool enabled)
    {
        if (string.IsNullOrWhiteSpace(terrainId))
            return false;

        return enabled
            ? _terrainEditorForceLod0.Add(terrainId)
            : _terrainEditorForceLod0.Remove(terrainId);
    }

    public bool SculptTerrainAtRay(
        MapObject mapObj,
        EditorAssetService assetService,
        Vector3 rayOrigin,
        Vector3 rayDirection,
        float radius,
        float strength,
        bool lower,
        TerrainHeightmapBrush? heightmapBrush = null)
    {
        return ApplyTerrainToolAtRay(
            mapObj,
            assetService,
            rayOrigin,
            rayDirection,
            TerrainSculptTool.RaiseLower,
            radius,
            strength,
            lower,
            0.0f,
            0.25f,
            0,
            heightmapBrush);
    }

    public bool ApplyTerrainToolAtRay(
        MapObject mapObj,
        EditorAssetService assetService,
        Vector3 rayOrigin,
        Vector3 rayDirection,
        TerrainSculptTool tool,
        float radius,
        float strength,
        bool lower,
        float targetHeight,
        float noiseScale,
        int noiseSeed,
        TerrainHeightmapBrush? heightmapBrush = null)
    {
        TerrainTileSetAsset? terrainSet = TryLoadTerrainTileSet(mapObj, assetService);
        if (terrainSet == null)
            return false;

        Vector3 terrainPosition = mapObj.Body?.Position ?? Vector3.Zero;
        Quaternion terrainRotation = mapObj.Body?.Rotation ?? Quaternion.Identity;
        Quaternion inverseRotation = Quaternion.Inverse(terrainRotation);
        Vector3 localOrigin = Vector3.Transform(rayOrigin - terrainPosition, inverseRotation);
        Vector3 localDirection = Vector3.Normalize(Vector3.Transform(rayDirection, inverseRotation));

        if (!terrainSet.Raycast(
                localOrigin,
                localDirection,
                out _,
                out Vector3 localHit,
                out _))
            return false;

        bool changed = false;
        foreach (TerrainTile tile in terrainSet.GetTilesIntersectingCircle(localHit, radius))
        {
            Vector3 tileLocalHit = localHit - terrainSet.GetTileOrigin(tile);
            changed |= tile.Asset.ApplyBrush(
                tool,
                tileLocalHit,
                radius,
                strength,
                lower,
                targetHeight,
                noiseScale,
                noiseSeed,
                heightmapBrush?.Samples,
                heightmapBrush?.Width ?? 0,
                heightmapBrush?.Height ?? 0);
        }

        if (!changed)
            return false;

        TerrainSceneBuilder.RefreshTerrainGeometry(
            _scene,
            terrainSet,
            mapObj.Id,
            mapObj.TerrainChunkQuads,
            localHit,
            radius);
        return true;
    }

    public bool PaintProceduralGrassAtRay(
        MapObject mapObj,
        EditorAssetService assetService,
        Vector3 rayOrigin,
        Vector3 rayDirection,
        float radius,
        float strength,
        bool erase)
    {
        TerrainTileSetAsset? terrainSet = TryLoadTerrainTileSet(mapObj, assetService);
        ProceduralTerrainAsset? procedural = terrainSet?.Procedural;
        if (terrainSet == null || procedural == null)
            return false;

        ProceduralGrassSettings grass = procedural.Settings.Grass;
        if (string.IsNullOrWhiteSpace(grass.DensityMaskPath))
        {
            string terrainName = Path.GetFileNameWithoutExtension(mapObj.TerrainAssetPath) ?? "terrain";
            string objectName = new(mapObj.Id
                .Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character)
                .ToArray());
            grass.DensityMaskPath = Path.Combine(
                    "Terrains",
                    "GrassMasks",
                    $"{terrainName}_{objectName}")
                .Replace('\\', '/');
            grass.Validate();
            string terrainPath = assetService.ResolveEditorAssetPath(mapObj.TerrainAssetPath!);
            if (!SaveTerrainAsset(terrainPath))
                return false;
        }

        Vector3 terrainPosition = mapObj.Body?.Position ?? Vector3.Zero;
        Quaternion terrainRotation = mapObj.Body?.Rotation ?? Quaternion.Identity;
        Quaternion inverseRotation = Quaternion.Inverse(terrainRotation);
        Vector3 localOrigin = Vector3.Transform(rayOrigin - terrainPosition, inverseRotation);
        Vector3 localDirection = Vector3.Normalize(Vector3.Transform(rayDirection, inverseRotation));
        if (!terrainSet.Raycast(localOrigin, localDirection, out _, out Vector3 localHit, out _))
            return false;

        bool changed = false;
        foreach (TerrainTile tile in terrainSet.GetTilesIntersectingCircle(localHit, radius))
        {
            Vector3 tileOrigin = terrainSet.GetTileOrigin(tile);
            Vector3 tileHit = localHit - tileOrigin;
            float tileWidth = (tile.Asset.Width - 1) * tile.Asset.CellSize;
            float tileDepth = (tile.Asset.Depth - 1) * tile.Asset.CellSize;
            changed |= _grassDensityMasks.PaintAndSave(
                grass,
                new TerrainTileCoordinate(tile.X, tile.Z),
                tileHit.X,
                tileHit.Z,
                tileWidth,
                tileDepth,
                radius,
                strength,
                erase);
        }

        if (!changed)
            return false;

        foreach (ProceduralTerrainLayer layer in _scene.ProceduralTerrainLayers)
        {
            if (layer.Id.Equals(mapObj.Id, StringComparison.OrdinalIgnoreCase))
                layer.GrassPatches.InvalidateDensityMasks();
        }
        _revision++;
        return true;
    }

    public bool SaveTerrainAsset(string path)
    {
        string fullPath = Path.GetFullPath(path);
        if (!_terrainCache.TryGetValue(fullPath, out var cached))
            return false;

        try
        {
            cached.Asset.Save(fullPath);
            FileInfo fileInfo = new(fullPath);
            _terrainCache[fullPath] = (cached.Asset, fileInfo.LastWriteTimeUtc, fileInfo.Length);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warn($"Terrain asset could not be saved: {ex.Message}");
            return false;
        }
    }

    public void InvalidateTerrainAsset(string path)
    {
        _terrainCache.Remove(Path.GetFullPath(path));
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
