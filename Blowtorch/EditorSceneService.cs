using System.IO;
using Fuse.Scene.Model;
using Fuse.Renderer;
using Fuse.Core;

namespace Blowtorch;

public class EditorSceneService
{
    private MapDocument _doc = null!;
    private Scene _scene = null!;
    private string _mapPath = "";

    public MapDocument Document => _doc;
    public Scene Scene => _scene;
    public string MapPath => _mapPath;

    public void LoadMap(string fuseResPath)
    {
        _mapPath = Path.Combine(fuseResPath, "Maps", "default.bth");
        _doc = MapDocument.Load(_mapPath) ?? new MapDocument();
        _scene = new Scene();
        Logger.Important($"CURRENT MAP LOADED: {_mapPath}");
    }

    public void SetDocument(MapDocument doc)
    {
        _doc = doc;
    }

    public void SetMapPath(string path)
    {
        _mapPath = path;
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
                    Enabled = mapObj.Visible,
                };
                _scene.AddLight(light);
                continue;
            }

            var mesh = assetService.GetOrCreateMesh(mapObj);
            if (mesh == null) continue;

            var entity = _scene.Add(mesh, mapObj.Id);
            entity.MeshKey = mapObj.Mesh ?? mapObj.Model ?? "";
            entity.MaterialPath = mapObj.MaterialPath ?? "";
            entity.MaterialPaths = mapObj.MaterialSlots.ToList();
            entity.Material = assetService.GetOrCreateMaterial(mapObj.MaterialPath);
            foreach (string slot in mapObj.MaterialSlots)
                entity.Materials.Add(assetService.GetOrCreateMaterial(slot));
            bool isTrigger = mapObj.Body?.IsTrigger == true;
            entity.TexturePath = isTrigger ? "Textures/tools/toolstrigger.bmp" : (mapObj.Texture ?? "");
            entity.Visible = mapObj.Visible;
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
    }

    public void SaveMap()
    {
        if (string.IsNullOrEmpty(_mapPath) || _doc == null) return;
        string json = _doc.Serialize();
        File.WriteAllText(_mapPath, json);
        Logger.Info($"Map saved to {_mapPath}");
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
}
