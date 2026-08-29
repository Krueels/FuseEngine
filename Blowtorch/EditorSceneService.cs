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
            entity.TexturePath = mapObj.Texture ?? "";
            if (mapObj.Body?.IsTrigger == true)
                entity.TexturePath = "Textures/tools/toolstrigger.bmp";
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

            if (!string.IsNullOrEmpty(mapObj.Texture))
            {
                assetService.GetOrCreateTexture(mapObj.Texture);
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
}
