using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fuse.Core;
using Fuse.Physics;
using Fuse.Scene.Model;
using Fuse.Scene.Geometry;
using Fuse.Scene.Terrain;

namespace Fuse.Scene;

public record struct PlayerSpawn(Vector3 Position, float Yaw, float Pitch);

public static class MapSerializer
{
    private static Vector3 Vec3FromJson(JsonArray arr)
    {
        return new Vector3(
            (float)arr[0]!,
            (float)arr[1]!,
            (float)arr[2]!);
    }

    private static Quaternion QuatFromJson(JsonArray arr)
    {
        return new Quaternion(
            (float)arr[1]!,
            (float)arr[2]!,
            (float)arr[3]!,
            (float)arr[0]!);
    }

    private static JsonArray Vec3ToJson(Vector3 v) => new(v.X, v.Y, v.Z);
    private static JsonArray QuatToJson(Quaternion q) => new(q.W, q.X, q.Y, q.Z);

    private static string ResolveTerrainPath(string path, string? resPath)
    {
        string normalized = path.Trim().Replace('\\', '/');
        if (normalized.StartsWith("res/", StringComparison.OrdinalIgnoreCase))
            normalized = normalized[4..];

        if (Path.IsPathRooted(normalized))
            return Path.GetFullPath(normalized);

        string root = resPath ?? Fuse.ResPath.Path;
        return Path.GetFullPath(Path.Combine(
            root,
            normalized.Replace('/', Path.DirectorySeparatorChar)));
    }

    private static string ShapeTypeToString(RigidBody.ShapeType t) => t switch
    {
        RigidBody.ShapeType.Box => "box",
        RigidBody.ShapeType.Plane => "plane",
        RigidBody.ShapeType.Sphere => "sphere",
        RigidBody.ShapeType.Capsule => "capsule",
        RigidBody.ShapeType.Trimesh => "trimesh",
        _ => "none"
    };

    private static RigidBody.ShapeType ShapeTypeFromString(string s) => s switch
    {
        "box" => RigidBody.ShapeType.Box,
        "plane" => RigidBody.ShapeType.Plane,
        "sphere" => RigidBody.ShapeType.Sphere,
        "capsule" => RigidBody.ShapeType.Capsule,
        "trimesh" => RigidBody.ShapeType.Trimesh,
        _ => RigidBody.ShapeType.None
    };

    private static JsonObject SerializeBody(Renderer.Entity e, PhysicsWorld physics)
    {
        var bj = new JsonObject();
        if (e.Body == null || !e.Body.IsBuilt) return bj;

        var pos = e.Body.Position(physics);
        var rot = e.Body.Rotation(physics);

        bj["shape"] = ShapeTypeToString(e.Body.Type);
        bj["position"] = Vec3ToJson(pos);
        bj["rotation"] = QuatToJson(rot);
        bj["mass"] = e.Body.Mass;
        bj["friction"] = e.Body.Friction;
        bj["restitution"] = e.Body.Restitution;
        bj["is_trigger"] = e.Body.IsTrigger;

        switch (e.Body.Type)
        {
            case RigidBody.ShapeType.Box:
                bj["half_extents"] = Vec3ToJson(e.Body.BoxHalfExtents);
                break;
            case RigidBody.ShapeType.Sphere:
                bj["radius"] = e.Body.SphereRadius;
                break;
            case RigidBody.ShapeType.Capsule:
                bj["radius"] = e.Body.CapsuleRadius;
                bj["height"] = e.Body.CapsuleHeight;
                break;
            case RigidBody.ShapeType.Plane:
                bj["normal"] = Vec3ToJson(e.Body.PlaneNormal);
                bj["distance"] = e.Body.PlaneDistance;
                break;
        }

        return bj;
    }

    public static string SerializeScene(Renderer.Scene scene, PhysicsWorld physics,
        PlayerSpawn? playerSpawn = null)
    {
        var j = new JsonObject
        {
            ["version"] = 1,
            ["objects"] = new JsonArray()
        };

        if (playerSpawn.HasValue)
        {
            var ps = playerSpawn.Value;
            j["player_spawn"] = new JsonObject
            {
                ["position"] = Vec3ToJson(ps.Position),
                ["yaw"] = ps.Yaw,
                ["pitch"] = ps.Pitch
            };
        }

        var objects = (JsonArray)j["objects"]!;
        foreach (var e in scene.Entities)
        {
            var obj = new JsonObject
            {
                ["id"] = e.Id,
                ["visible"] = e.Visible
            };

            if (!string.IsNullOrEmpty(e.ParentId))
                obj["parent"] = e.ParentId;

            if (e.MapData != null && e.MapData.TryGetPropertyValue("type", out var typeNode) && (string)typeNode! == "brush")
            {
                obj["type"] = "brush";
                if (e.MapData.TryGetPropertyValue("faces", out var facesNode))
                {
                    obj["faces"] = System.Text.Json.Nodes.JsonNode.Parse(facesNode!.ToJsonString());
                }
            }

            if (e.MeshKey.Contains('/') || e.MeshKey.Contains('\\'))
            {
                obj["model"] = e.MeshKey;
                if (e.ModelScale != Vector3.One)
                    obj["model_scale"] = new JsonArray { e.ModelScale.X, e.ModelScale.Y, e.ModelScale.Z };
            }
            else
            {
                obj["mesh"] = e.MeshKey;
                if (e.UvScale != Vector2.One)
                    obj["uv_scale"] = new JsonArray(e.UvScale.X, e.UvScale.Y);
            }

            if (!string.IsNullOrEmpty(e.TexturePath))
                obj["texture"] = e.TexturePath;
            if (!string.IsNullOrEmpty(e.MaterialPath))
                obj["material"] = e.MaterialPath;
            if (e.MaterialPaths.Count > 0)
            {
                var slots = new JsonArray();
                foreach (string materialPath in e.MaterialPaths)
                    slots.Add(materialPath);
                obj["material_slots"] = slots;
            }

            if (e.MapData != null && e.MapData.TryGetPropertyValue("geometry_graph", out var graphNode) && graphNode != null)
                obj["geometry_graph"] = (string?)graphNode;

            if (!string.IsNullOrEmpty(e.InteractableType))
                obj["interactable"] = e.InteractableType;

            if (e.Behaviours.Count > 0)
            {
                var arr = new JsonArray();
                foreach (var b in e.Behaviours)
                {
                    var bObj = new JsonObject();
                    bObj["type"] = b.Type;
                    bObj["properties"] = b.Properties != null ? JsonNode.Parse(b.Properties.ToJsonString()) : new JsonObject();
                    arr.Add(bObj);
                }
                obj["behaviours"] = arr;
            }

            if (e.Body != null && e.Body.IsBuilt)
                obj["body"] = SerializeBody(e, physics);

            if (e.AttachedLight != null)
            {
                obj["light_type"] = e.AttachedLight.Type == Renderer.LightType.Directional ? "directional" : (e.AttachedLight.Type == Renderer.LightType.Point ? "point" : "spot");
                obj["light_color"] = new JsonArray(e.AttachedLight.Color.X, e.AttachedLight.Color.Y, e.AttachedLight.Color.Z);
                obj["light_intensity"] = e.AttachedLight.Intensity;
                obj["light_radius"] = e.AttachedLight.Radius;
                obj["light_inner_cone"] = e.AttachedLight.InnerConeAngle;
                obj["light_outer_cone"] = e.AttachedLight.OuterConeAngle;
                obj["light_cast_shadows"] = e.AttachedLight.CastShadows;
                obj["light_shadow_bias"] = e.AttachedLight.ShadowBias;
                obj["light_dynamic"] = e.AttachedLight.Dynamic;
            }

            objects.Add(obj);
        }

        var lightsArray = new JsonArray();
        foreach (var l in scene.Lights)
        {
            if (scene.Entities.Any(e => e.AttachedLight == l)) continue; // Don't save attached lights to the array

            var lj = new JsonObject
            {
                ["type"] = l.Type == Renderer.LightType.Directional ? "directional" : (l.Type == Renderer.LightType.Point ? "point" : "spot"),
                ["position"] = Vec3ToJson(l.Position),
                ["color"] = Vec3ToJson(l.Color),
                ["radius"] = l.Radius,
                ["cast_shadows"] = l.CastShadows,
                ["shadow_bias"] = l.ShadowBias,
                ["intensity"] = l.Intensity,
                ["enabled"] = l.Enabled,
            };
            if (l.Type == Renderer.LightType.Spot || l.Type == Renderer.LightType.Directional)
            {
                lj["direction"] = Vec3ToJson(l.Direction);
                lj["inner_cone"] = l.InnerConeAngle;
                lj["outer_cone"] = l.OuterConeAngle;
            }
            lightsArray.Add(lj);
        }
        if (lightsArray.Count > 0)
            j["lights"] = lightsArray;

        return j.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    public static List<RigidBody>? DeserializeScene(string json,
        Renderer.Scene scene, PhysicsWorld physics,
        AssetManagement.AssetManager assets,
        out PlayerSpawn? playerSpawn,
        out string? skyboxPath,
        out SkyboxSettings skyboxSettings,
        string? resPath = null,
        Action<float, string>? onProgress = null)
    {
        playerSpawn = null;
        skyboxPath = null;
        skyboxSettings = new SkyboxSettings();
        scene.Clear();
        var createdBodies = new List<RigidBody>();

        JsonNode? rootNode;
        try
        {
            rootNode = JsonNode.Parse(json);
        }
        catch (JsonException ex)
        {
            Logger.Error($"Map parse error: {ex.Message}");
            return null;
        }

        if (rootNode == null)
        {
            Logger.Error("Map parse error: empty JSON");
            return null;
        }

        var root = rootNode.AsObject();

        int version = root.TryGetPropertyValue("version", out var verNode)
            ? (int)verNode! : 0;
        if (version != 1)
        {
            Logger.Error($"Unknown map version: {version}");
            return null;
        }

        if (root.TryGetPropertyValue("skybox", out var skyboxNode) && skyboxNode != null)
        {
            if (skyboxNode is JsonObject skyboxObject)
                skyboxSettings = SkyboxSettings.FromJson(skyboxObject);
            else
                skyboxPath = (string?)skyboxNode;
        }
        if (root.TryGetPropertyValue("skybox_settings", out var skyboxSettingsNode) &&
            skyboxSettingsNode is JsonObject skyboxSettingsObject)
        {
            skyboxSettings = SkyboxSettings.FromJson(skyboxSettingsObject);
        }

        if (root.TryGetPropertyValue("player_spawn", out var spawnNode))
        {
            var sj = spawnNode!.AsObject();
            playerSpawn = new PlayerSpawn(
                Vec3FromJson(sj["position"]!.AsArray()),
                (float)sj["yaw"]!,
                (float)sj["pitch"]!);
        }

        if (root.TryGetPropertyValue("lights", out var lightsNode))
        {
            foreach (var lightNode in lightsNode!.AsArray())
            {
                if (lightNode == null) continue;
                var lj = lightNode.AsObject();
                var l = new Renderer.Light();
                l.Position = Vec3FromJson(lj["position"]!.AsArray());
                l.Color = Vec3FromJson(lj["color"]!.AsArray());
                l.Radius = (float)lj["radius"]!;
                l.CastShadows = lj.TryGetPropertyValue("cast_shadows", out var csNode) && (bool)csNode!;
                l.ShadowBias = lj.TryGetPropertyValue("shadow_bias", out var sbNode) ? (float)sbNode! : 0.005f;
                l.Intensity = (float)lj["intensity"]!;
                l.Enabled = lj.TryGetPropertyValue("enabled", out var en) ? (bool)en! : true;
                l.Type = (string)lj["type"]! == "directional" ? Renderer.LightType.Directional : ((string)lj["type"]! == "spot" ? Renderer.LightType.Spot : Renderer.LightType.Point);
                if (l.Type == Renderer.LightType.Spot || l.Type == Renderer.LightType.Directional)
                {
                    l.Direction = Vec3FromJson(lj["direction"]!.AsArray());
                    l.InnerConeAngle = (float)lj["inner_cone"]!;
                    l.OuterConeAngle = (float)lj["outer_cone"]!;
                }
                scene.AddLight(l);
            }
        }

        var parentMap = new Dictionary<string, string>();
        var visibleMap = new Dictionary<string, bool>();
        var objects = root["objects"]!.AsArray();
        foreach (var objNode in objects)
        {
            if (objNode == null) continue;
            var obj = objNode.AsObject();
            string id = obj.TryGetPropertyValue("id", out var idNode) ? (string)idNode! : "unnamed";
            bool visible = obj.TryGetPropertyValue("visible", out var visNode) ? (bool)visNode! : true;
            string? parent = obj.TryGetPropertyValue("parent", out var pNode) ? (string)pNode! : null;
            visibleMap[id] = visible;
            if (parent != null) parentMap[id] = parent;
        }

        bool IsGloballyVisible(string id)
        {
            if (!visibleMap.TryGetValue(id, out bool vis) || !vis) return false;
            if (parentMap.TryGetValue(id, out string? parentId) && !string.IsNullOrEmpty(parentId))
            {
                return IsGloballyVisible(parentId);
            }
            return true;
        }

        int totalEntities = objects.Count;
        int processedEntities = 0;
        foreach (var objNode in objects)
        {
            if (objNode == null) continue;
            var obj = objNode.AsObject();

            string id = obj.TryGetPropertyValue("id", out var idNode)
                ? (string)idNode! : "unnamed";

            bool isModel = obj.TryGetPropertyValue("model", out var modelNode);
            bool isBrush = obj.TryGetPropertyValue("type", out var typeNode) && string.Equals((string?)typeNode, "brush", StringComparison.OrdinalIgnoreCase);
            bool isTerrain = obj.TryGetPropertyValue("type", out var terrainTypeNode) && string.Equals((string?)terrainTypeNode, "terrain", StringComparison.OrdinalIgnoreCase);
            string terrainAssetPath = obj.TryGetPropertyValue("terrain_asset", out var terrainAssetNode) ? (string?)terrainAssetNode ?? "" : "";
            isTerrain |= !string.IsNullOrWhiteSpace(terrainAssetPath);

            string terrainMaterialPath = obj.TryGetPropertyValue("material", out var terrainMaterialNode)
                ? (string?)terrainMaterialNode ?? "" : "";
            string terrainTexturePath = obj.TryGetPropertyValue("texture", out var terrainTextureNode)
                ? (string?)terrainTextureNode ?? "" : "";

            if (isTerrain)
            {
                if (string.IsNullOrWhiteSpace(terrainAssetPath))
                {
                    Logger.Warn($"Map load: terrain '{id}' has no terrain_asset path.");
                    processedEntities++;
                    onProgress?.Invoke((float)processedEntities / totalEntities, $"Skipping {id}...");
                    continue;
                }

                string fullTerrainPath = ResolveTerrainPath(terrainAssetPath, resPath);
                if (!File.Exists(fullTerrainPath))
                {
                    Logger.Warn($"Map load: terrain asset not found '{fullTerrainPath}' for '{id}'.");
                    processedEntities++;
                    onProgress?.Invoke((float)processedEntities / totalEntities, $"Skipping {id}...");
                    continue;
                }

                try
                {
                    TerrainAsset terrain = TerrainAsset.Load(fullTerrainPath);
                    Vector3 terrainPosition = Vector3.Zero;
                    Quaternion terrainRotation = Quaternion.Identity;
                    float terrainFriction = 0.5f;
                    float terrainRestitution = 0.0f;

                    if (obj.TryGetPropertyValue("body", out var terrainBodyNode) &&
                        terrainBodyNode is JsonObject terrainBody)
                    {
                        if (terrainBody.TryGetPropertyValue("position", out var terrainPositionNode))
                            terrainPosition = Vec3FromJson(terrainPositionNode!.AsArray());
                        if (terrainBody.TryGetPropertyValue("rotation", out var terrainRotationNode))
                            terrainRotation = QuatFromJson(terrainRotationNode!.AsArray());
                        if (terrainBody.TryGetPropertyValue("friction", out var terrainFrictionNode))
                            terrainFriction = (float)terrainFrictionNode!;
                        if (terrainBody.TryGetPropertyValue("restitution", out var terrainRestitutionNode))
                            terrainRestitution = (float)terrainRestitutionNode!;
                    }

                    int terrainChunkQuads = obj.TryGetPropertyValue("terrain_chunk_quads", out var terrainChunkNode)
                        ? System.Math.Max(1, (int)terrainChunkNode!)
                        : TerrainSceneBuilder.DefaultChunkQuads;
                    Vector2 terrainUvScale = Vector2.One;
                    Vector2 terrainUvOffset = Vector2.Zero;
                    float terrainUvRotation = 0f;
                    if (obj.TryGetPropertyValue("uv_scale", out var terrainUvNode) &&
                        terrainUvNode is JsonArray terrainUvArray &&
                        terrainUvArray.Count >= 2)
                    {
                        terrainUvScale = new Vector2(
                            (float)terrainUvArray[0]!,
                            (float)terrainUvArray[1]!);
                    }
                    if (obj.TryGetPropertyValue("uv_offset", out var terrainUvOffsetNode) &&
                        terrainUvOffsetNode is JsonArray terrainUvOffsetArray &&
                        terrainUvOffsetArray.Count >= 2)
                    {
                        terrainUvOffset = new Vector2(
                            (float)terrainUvOffsetArray[0]!,
                            (float)terrainUvOffsetArray[1]!);
                    }
                    if (obj.TryGetPropertyValue("uv_rotation", out var terrainUvRotationNode))
                        terrainUvRotation = (float)terrainUvRotationNode!;
                    var terrainMaterialPaths = new List<string>();
                    if (obj.TryGetPropertyValue("material_slots", out var terrainMaterialSlotsNode) &&
                        terrainMaterialSlotsNode is JsonArray terrainMaterialSlots)
                    {
                        foreach (JsonNode? slotNode in terrainMaterialSlots)
                        {
                            if (slotNode != null)
                                terrainMaterialPaths.Add(slotNode.GetValue<string>());
                        }
                    }
                    bool terrainVisible = IsGloballyVisible(id);

                    int chunkCount = TerrainSceneBuilder.AddToScene(
                        scene,
                        terrain,
                        id,
                        terrainPosition,
                        terrainRotation,
                        terrainVisible,
                        terrainChunkQuads,
                        terrainMaterialPath,
                        terrainMaterialPaths,
                        terrainTexturePath,
                        terrainUvScale,
                        terrainUvOffset,
                        terrainUvRotation,
                        assets,
                        physics,
                        createdBodies,
                        "",
                        terrainFriction,
                        terrainRestitution,
                        obj.TryGetPropertyValue("terrain_pixel_error", out var terrainPixelErrorNode)
                            ? MathF.Max(0.1f, (float)terrainPixelErrorNode!)
                            : TerrainSceneBuilder.DefaultPixelError,
                        obj.TryGetPropertyValue("terrain_collision_lod", out var terrainCollisionLodNode)
                            ? System.Math.Max(0, (int)terrainCollisionLodNode!)
                            : TerrainSceneBuilder.DefaultCollisionLod);

                    if (chunkCount == 0)
                        Logger.Warn($"Map load: terrain '{id}' generated no chunks.");
                }
                catch (Exception ex)
                {
                    Logger.Error($"Map load: terrain '{terrainAssetPath}' failed for '{id}': {ex.Message}");
                }

                processedEntities++;
                onProgress?.Invoke((float)processedEntities / totalEntities, $"Processing {id}...");
                continue;
            }

            string meshKey = isModel
                ? (string)modelNode!
                : (obj.TryGetPropertyValue("mesh", out var meshNode)
                    ? (string)meshNode! : (isBrush ? id : ""));
            string geometryGraphPath = obj.TryGetPropertyValue("geometry_graph", out var graphPathNode)
                ? (string?)graphPathNode ?? "" : "";

            Vector3 modelScale = Vector3.One;
            if (obj.TryGetPropertyValue("model_scale", out var scaleNode))
            {
                if (scaleNode is JsonArray arr && arr.Count >= 3)
                    modelScale = new Vector3((float)arr[0]!, (float)arr[1]!, (float)arr[2]!);
                else
                    modelScale = new Vector3((float)scaleNode!);
            }

            Vector2 uvScale = Vector2.One;
            if (obj.TryGetPropertyValue("uv_scale", out var uvNode))
            {
                var arr = uvNode!.AsArray();
                uvScale = new Vector2((float)arr[0]!, (float)arr[1]!);
            }

            Vector2 uvOffset = Vector2.Zero;
            if (obj.TryGetPropertyValue("uv_offset", out var uvOffNode))
            {
                var arr = uvOffNode!.AsArray();
                uvOffset = new Vector2((float)arr[0]!, (float)arr[1]!);
            }

            float uvRotation = 0f;
            if (obj.TryGetPropertyValue("uv_rotation", out var uvRotNode))
                uvRotation = (float)uvRotNode!;

            string texturePath = obj.TryGetPropertyValue("texture", out var texNode)
                ? (string)texNode! : "";
            string materialPath = obj.TryGetPropertyValue("material", out var materialNode)
                ? (string)materialNode! : "";
            var materialPaths = new List<string>();
            if (obj.TryGetPropertyValue("material_slots", out var materialSlotsNode) && materialSlotsNode is JsonArray materialSlotsArray)
            {
                foreach (JsonNode? slotNode in materialSlotsArray)
                {
                    if (slotNode != null)
                        materialPaths.Add(slotNode.GetValue<string>());
                }
            }

            Renderer.Mesh? mesh = null;
            MeshData? generatedGeometry = null;
            System.Numerics.Vector3[]? brushCollVerts = null;
            uint[]? brushCollIndices = null;
            Brush? loadedBrush = null;
            string modelPath = meshKey;
            if (resPath != null && isModel && !Path.IsPathRooted(meshKey))
                modelPath = Path.GetFullPath(Path.Combine(resPath, meshKey));

            if (isBrush)
            {
                loadedBrush = (Brush)MapDocument.ParseObject(obj);
                var meshData = MeshGenerator.Generate(loadedBrush);
                generatedGeometry = meshData;
            }
            else if (isModel)
            {
                var model = assets.GetModel(modelPath);
                if (model != null) mesh = model.Mesh;
            }
            else
            {
                mesh = assets.GetMesh(meshKey);
            }

            if (!string.IsNullOrWhiteSpace(geometryGraphPath))
            {
                string graphPath = geometryGraphPath.Replace('\\', '/');
                if (graphPath.StartsWith("res/", StringComparison.OrdinalIgnoreCase))
                    graphPath = graphPath[4..];
                string fullGraphPath = resPath == null || Path.IsPathRooted(graphPath)
                    ? Path.GetFullPath(graphPath)
                    : Path.GetFullPath(Path.Combine(resPath, graphPath));
                if (GeometryGraphCache.TryEvaluateFile(fullGraphPath, generatedGeometry, out GeometryEvaluationResult? evaluated, out string graphError) && evaluated != null)
                {
                    generatedGeometry = evaluated.Mesh;
                    mesh = new Renderer.Mesh(assets.Gl, generatedGeometry.Vertices, generatedGeometry.Indices,
                        generatedGeometry.LineIndices, generatedGeometry.Parts);
                    if (!string.IsNullOrWhiteSpace(evaluated.MaterialPath))
                        materialPath = evaluated.MaterialPath;
                }
                else
                {
                    Logger.Warn($"Map load: geometry graph '{geometryGraphPath}' failed for '{id}': {graphError}");
                }
            }

            // Brushes are generated on the CPU above. Create their GPU mesh
            // when no Geometry Nodes result replaced it. This also preserves
            // the original brush as a fallback if graph evaluation fails.
            if (mesh == null && generatedGeometry != null)
            {
                mesh = new Renderer.Mesh(assets.Gl, generatedGeometry.Vertices, generatedGeometry.Indices,
                    generatedGeometry.LineIndices, generatedGeometry.Parts);
            }

            if (generatedGeometry != null)
            {
                brushCollVerts = new System.Numerics.Vector3[generatedGeometry.Vertices.Length];
                for (int i = 0; i < generatedGeometry.Vertices.Length; i++) brushCollVerts[i] = generatedGeometry.Vertices[i].Position;
                brushCollIndices = generatedGeometry.Indices;
            }

            // Check for inline light properties
            string? lightType = obj.TryGetPropertyValue("light_type", out var ltNode) ? (string)ltNode! : null;
            Renderer.Light? attachedLight = null;
            if (lightType != null)
            {
                var lightPos = Vector3.Zero;
                var lightRot = Quaternion.Identity;
                if (obj.TryGetPropertyValue("body", out var bodyNodeLight))
                {
                    var bj = bodyNodeLight!.AsObject();
                    if (bj.TryGetPropertyValue("position", out var pn))
                        lightPos = Vec3FromJson(pn!.AsArray());
                    if (bj.TryGetPropertyValue("rotation", out var rn))
                        lightRot = QuatFromJson(rn!.AsArray());
                }
                var lightDir = Vector3.Transform(-Vector3.UnitY, lightRot);
                var lightCol = obj.TryGetPropertyValue("light_color", out var lcNode) ? Vec3FromJson(lcNode!.AsArray()) : Vector3.One;
                float lightIntensity = obj.TryGetPropertyValue("light_intensity", out var liNode) ? (float)liNode! : 1.0f;
                float lightRadius = obj.TryGetPropertyValue("light_radius", out var lrNode) ? (float)lrNode! : 10.0f;
                float lightInner = obj.TryGetPropertyValue("light_inner_cone", out var licNode) ? (float)licNode! : float.DegreesToRadians(20);
                float lightOuter = obj.TryGetPropertyValue("light_outer_cone", out var locNode) ? (float)locNode! : float.DegreesToRadians(30);
                bool lightCastShadows = obj.TryGetPropertyValue("light_cast_shadows", out var lcsNode) && (bool)lcsNode!;
                float lightShadowBias = obj.TryGetPropertyValue("light_shadow_bias", out var lsbNode) ? (float)lsbNode! : 0.00100f;

                var light = new Renderer.Light
                {
                    Type = lightType == "directional" ? Renderer.LightType.Directional : (lightType == "spot" ? Renderer.LightType.Spot : Renderer.LightType.Point),
                    Position = lightPos,
                    Direction = lightDir,
                    Color = lightCol,
                    Intensity = lightIntensity,
                    Radius = lightRadius,
                    InnerConeAngle = lightInner,
                    OuterConeAngle = lightOuter,
                    CastShadows = lightCastShadows,
                    ShadowBias = lightShadowBias,
                    Dynamic = obj.TryGetPropertyValue("light_dynamic", out var dynNode) && (bool)dynNode!,
                    Enabled = IsGloballyVisible(id),
                };
                scene.AddLight(light);
                attachedLight = light;
            }

            if (mesh == null && !string.IsNullOrEmpty(meshKey))
            {
                Logger.Warn($"Map load: unknown mesh '{meshKey}' for '{id}'");
                continue;
            }

            var entity = scene.Add(mesh, id);
            entity.MeshOwnedByEntity = isBrush || generatedGeometry != null && !string.IsNullOrWhiteSpace(geometryGraphPath);
            entity.MapData = obj;
            entity.MeshKey = meshKey;
            entity.MaterialPath = materialPath;
            entity.MaterialPaths = materialPaths;
            entity.TexturePath = texturePath;
            entity.ModelScale = modelScale;
            entity.InteractableType = obj.TryGetPropertyValue("interactable", out var it) ? (string)it! : "";
            if (obj.TryGetPropertyValue("behaviours", out var bArr) && bArr is JsonArray behavioursArray)
            {
                foreach (var node in behavioursArray)
                {
                    if (node is JsonObject bObj)
                    {
                        var bType = bObj.TryGetPropertyValue("type", out var bt2) ? (string)bt2! : "";
                        var bProps = bObj.TryGetPropertyValue("properties", out var bp) ? bp as JsonObject : new JsonObject();
                        if (!string.IsNullOrEmpty(bType))
                        {
                            entity.Behaviours.Add(new Behaviours.BehaviourData { Type = bType, Properties = bProps != null ? (JsonObject)JsonNode.Parse(bProps.ToJsonString())! : new JsonObject() });
                        }
                    }
                }
            }
            entity.UvScale = uvScale;
            entity.UvOffset = uvOffset;
            entity.UvRotation = uvRotation;
            entity.Visible = IsGloballyVisible(id);
            entity.ParentId = obj.TryGetPropertyValue("parent", out var pIdNode) ? (string)pIdNode! : "";
            entity.AttachedLight = attachedLight;

            if (!string.IsNullOrWhiteSpace(materialPath))
                entity.Material = assets.TryGetMaterial(materialPath);
            foreach (string slotPath in materialPaths)
                entity.Materials.Add(assets.TryGetMaterial(slotPath));

            bool hasMaterial = entity.Material != null || entity.Materials.Any(material => material != null);

            // A material owns its texture graph. Some old maps still contain a
            // legacy texture field pointing to a file that was moved into a
            // subfolder; do not load or report that fallback when the material
            // was resolved successfully.
            if (!string.IsNullOrEmpty(texturePath) && !hasMaterial)
            {
                string texPath = texturePath;
                if (texPath.StartsWith("res/"))
                {
                    texPath = texPath.Substring(4);
                }
                if (resPath != null && !Path.IsPathRooted(texPath))
                {
                    texPath = Path.GetFullPath(Path.Combine(resPath, texPath));
                }
                
                if (File.Exists(texPath))
                {
                    try
                    {
                        entity.Texture = assets.GetTexture(texPath, Fuse.Renderer.TextureColorSpace.Srgb);
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn($"Map load: failed to load texture '{texturePath}' for '{id}' - {ex.Message}");
                    }
                }
                else
                {
                    Logger.Warn($"Map load: texture file not found '{texPath}' for '{id}' - using fallback");
                    entity.Texture = null;
                }

                // Auto-detect emissive texture (emi_ prefix)
                if (texPath.Contains("emi_", StringComparison.OrdinalIgnoreCase))
                {
                    entity.EmissiveColor = Vector3.One;
                    entity.EmissiveStrength = 2.0f;
                }

                if (entity.Material == null)
                    entity.Material = assets.GetLegacyMaterial(texturePath);
            }

            if (obj.TryGetPropertyValue("interactable", out var interactableNode))
                entity.InteractableType = (string)interactableNode!;

            if (obj.TryGetPropertyValue("behaviours", out var bArrGrp) && bArrGrp is JsonArray behavioursArrayGrp)
            {
                foreach (var node in behavioursArrayGrp)
                {
                    if (node is JsonObject bObj)
                    {
                        var bType = bObj.TryGetPropertyValue("type", out var bt2) ? (string)bt2! : "";
                        var bProps = bObj.TryGetPropertyValue("properties", out var bp) ? bp as JsonObject : new JsonObject();
                        if (!string.IsNullOrEmpty(bType))
                        {
                            entity.Behaviours.Add(new Behaviours.BehaviourData { Type = bType, Properties = bProps != null ? (JsonObject)JsonNode.Parse(bProps.ToJsonString())! : new JsonObject() });
                        }
                    }
                }
            }

            if (entity.Visible && obj.TryGetPropertyValue("body", out var bodyNode))
            {
                var bj = bodyNode!.AsObject();
                bool isTrimesh = bj.TryGetPropertyValue("shape", out var shapeNode)
                    && (string)shapeNode! == "trimesh";

                bool isConvexHull = bj.TryGetPropertyValue("shape", out var shapeNode2)
                    && (string)shapeNode2! == "convexhull";

                var body = new RigidBody();
                ConfigureBodyFromJson(body, bj);

                if (body.IsTrigger)
                    entity.Visible = false;

                if (entity.Behaviours.Count > 0)
                    body.SetKinematic(true);

                if (isTrimesh || isConvexHull)
                {
                    if (generatedGeometry != null && brushCollVerts != null)
                    {
                        if (isConvexHull || body.Mass > 0.0f)
                            body.SetConvexHull(brushCollVerts);
                        else
                            body.SetTrimesh(brushCollVerts, brushCollIndices ?? []);
                    }
                    else if (isBrush && brushCollVerts != null)
                    {
                        // Plane brushes stay on their legacy convex hull path.
                        // An editable brush may be concave after face operations,
                        // so its static collider must use the same triangles that
                        // were generated for rendering.
                        if (loadedBrush?.IsEditableMesh == true && brushCollIndices is { Length: > 0 })
                        {
                            if (body.Mass > 0.0f)
                            {
                                Logger.Warn($"Editable brush '{id}' requested dynamic trimesh collision; using a convex hull instead.");
                                body.SetConvexHull(brushCollVerts);
                            }
                            else
                            {
                                body.SetTrimesh(brushCollVerts, brushCollIndices);
                            }
                        }
                        else
                        {
                            body.SetConvexHull(brushCollVerts);
                        }
                    }
                    else
                    {
                        var model = assets.GetModel(modelPath);
                        if (model != null && model.CollVertices.Length > 0)
                        {
                            if (isConvexHull)
                                body.SetConvexHull(model.CollVertices, modelScale);
                            else
                                body.SetTrimesh(model.CollVertices, model.CollIndices, modelScale);
                        }
                        else
                        {
                            body.SetBox(new Vector3(0.5f));
                        }
                    }
                }

                body.Build(physics);
                entity.Body = body;
                entity.Transform.Position = body.Position(physics);
                entity.Transform.Rotation = body.Rotation(physics);
                scene.RegisterBody(entity);
                
                if (isBrush)
                    entity.Transform.Scale = Vector3.One;
                else if (!isModel && body.Type == RigidBody.ShapeType.Box)
                    entity.Transform.Scale = body.BoxHalfExtents * 2.0f;
                else if (!isModel && body.Type == RigidBody.ShapeType.Sphere)
                    entity.Transform.Scale = new Vector3(body.SphereRadius * 2.0f);
                else
                    entity.Transform.Scale = modelScale;
                    
                createdBodies.Add(body);
            }
            else
            {
                entity.Transform.Scale = isBrush ? Vector3.One : modelScale;
            }

            processedEntities++;
            onProgress?.Invoke((float)processedEntities / totalEntities, $"Processing {id}...");
        }

        // Compute initial relative transforms for children
        foreach (var entity in scene.Entities)
        {
            if (!string.IsNullOrEmpty(entity.ParentId))
            {
                var parent = scene.Entities.FirstOrDefault(e => e.Id == entity.ParentId);
                if (parent != null)
                {
                    var globalOffset = entity.Transform.Position - parent.Transform.Position;
                    entity.InitialRelativePosition = Vector3.Transform(globalOffset, Quaternion.Inverse(parent.Transform.Rotation));
                    entity.InitialRelativeRotation = Quaternion.Inverse(parent.Transform.Rotation) * entity.Transform.Rotation;
                }
            }
            Logger.Info($"[DebugMapSerializer] Entity {entity.Id} - TransformPos: {entity.Transform.Position}, InitialRelPos: {entity.InitialRelativePosition}");
        }

        Logger.Info($"Map loaded ({scene.Entities.Count} entities)");
        return createdBodies;
    }

    private static void ConfigureBodyFromJson(RigidBody body, JsonObject bj)
    {
        var shape = bj.TryGetPropertyValue("shape", out var shapeToken)
            ? ShapeTypeFromString((string)shapeToken!)
            : RigidBody.ShapeType.None;

        switch (shape)
        {
            case RigidBody.ShapeType.Box:
                body.SetBox(Vec3FromJson(bj["half_extents"]!.AsArray()));
                break;
            case RigidBody.ShapeType.Sphere:
                body.SetSphere((float)bj["radius"]!);
                break;
            case RigidBody.ShapeType.Capsule:
                body.SetCapsule((float)bj["radius"]!, (float)bj["height"]!);
                break;
            case RigidBody.ShapeType.Plane:
                body.SetPlane(
                    Vec3FromJson(bj["normal"]!.AsArray()),
                    (float)bj["distance"]!);
                break;
        }

        if (bj.TryGetPropertyValue("position", out var posToken))
            body.SetPosition(Vec3FromJson(posToken!.AsArray()));
        if (bj.TryGetPropertyValue("rotation", out var rotToken))
            body.SetRotation(QuatFromJson(rotToken!.AsArray()));
        if (bj.TryGetPropertyValue("mass", out var massToken))
            body.SetMass((float)massToken!);
        if (bj.TryGetPropertyValue("friction", out var frictionToken))
            body.SetFriction((float)frictionToken!);
        if (bj.TryGetPropertyValue("restitution", out var restToken))
            body.SetRestitution((float)restToken!);
        if (bj.TryGetPropertyValue("is_trigger", out var triggerToken))
            body.SetTrigger((bool)triggerToken!);
    }

    public static bool SaveToFile(Renderer.Scene scene, PhysicsWorld physics, string filepath,
        PlayerSpawn? playerSpawn = null)
    {
        string json = SerializeScene(scene, physics, playerSpawn);
        try
        {
            File.WriteAllText(filepath, json);
            Logger.Info($"Map saved: {filepath} ({scene.Entities.Count} entities)");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error($"Failed to save map: {filepath} - {ex.Message}");
            return false;
        }
    }

    public static List<RigidBody>? LoadFromFile(string filepath,
        Renderer.Scene scene, PhysicsWorld physics,
        AssetManagement.AssetManager assets,
        out PlayerSpawn? playerSpawn,
        out string? skyboxPath,
        out SkyboxSettings skyboxSettings,
        string? resPath = null,
        Action<float, string>? onProgress = null)
    {
        playerSpawn = null;
        skyboxPath = null;
        skyboxSettings = new SkyboxSettings();
        if (!File.Exists(filepath))
        {
            Logger.Error($"Failed to load map: {filepath}");
            return null;
        }

        string json = File.ReadAllText(filepath);
        return DeserializeScene(
            json,
            scene,
            physics,
            assets,
            out playerSpawn,
            out skyboxPath,
            out skyboxSettings,
            resPath,
            onProgress);
    }
}
