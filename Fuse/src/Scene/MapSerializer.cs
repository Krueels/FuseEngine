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
        RigidBody.ShapeType.ConvexHull => "convexhull",
        RigidBody.ShapeType.Compound => "compound",
        _ => "none"
    };

    private static RigidBody.ShapeType ShapeTypeFromString(string s) => s switch
    {
        "box" => RigidBody.ShapeType.Box,
        "plane" => RigidBody.ShapeType.Plane,
        "sphere" => RigidBody.ShapeType.Sphere,
        "capsule" => RigidBody.ShapeType.Capsule,
        "trimesh" => RigidBody.ShapeType.Trimesh,
        "convexhull" => RigidBody.ShapeType.ConvexHull,
        "compound" => RigidBody.ShapeType.Compound,
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
        if (e.Body.BuoyancyVolumeOverride > 0.0001f &&
            float.IsFinite(e.Body.BuoyancyVolumeOverride))
        {
            bj["buoyancy_volume"] = e.Body.BuoyancyVolumeOverride;
        }

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
            case RigidBody.ShapeType.ConvexHull:
            case RigidBody.ShapeType.Trimesh:
                // The vertex buffer is owned by the model/brush entry and is
                // rebuilt when the map is loaded. The shape marker is enough
                // here; the loader resolves the actual geometry again.
                break;
            case RigidBody.ShapeType.Compound:
                // A procedural staircase is authored from source bounds and
                // rebuilt as one box per step when the map is loaded. Keep
                // those bounds when serializing a running scene.
                if (e.MapData != null &&
                    e.MapData.TryGetPropertyValue("body", out var sourceBodyNode) &&
                    sourceBodyNode is JsonObject sourceBody &&
                    sourceBody.TryGetPropertyValue("half_extents", out var sourceHalfExtentsNode) &&
                    sourceHalfExtentsNode != null)
                {
                    bj["half_extents"] = JsonNode.Parse(sourceHalfExtentsNode.ToJsonString());
                }
                break;
            case RigidBody.ShapeType.Plane:
                bj["normal"] = Vec3ToJson(e.Body.PlaneNormal);
                bj["distance"] = e.Body.PlaneDistance;
                break;
        }

        return bj;
    }

    public static string SerializeScene(Renderer.Scene scene, PhysicsWorld physics,
        PlayerSpawn? playerSpawn = null,
        OceanSettings? oceanSettings = null,
        VolumetricFogSettings? fogSettings = null)
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

        if (oceanSettings != null)
            j["ocean"] = oceanSettings.ToJson();
        if (fogSettings != null)
            j["volumetric_fog"] = fogSettings.ToJson();

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

            if (e.MapData != null &&
                e.MapData.TryGetPropertyValue("staircase", out var staircaseNode) &&
                staircaseNode is JsonObject)
            {
                obj["staircase"] = JsonNode.Parse(staircaseNode.ToJsonString());
            }

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
            {
                obj["body"] = SerializeBody(e, physics);
            }
            else if (e.MapData != null &&
                     e.MapData.TryGetPropertyValue("body", out var preservedBodyNode) &&
                     preservedBodyNode is JsonObject preservedBody)
            {
                // Compound groups deliberately remove child RigidBody
                // wrappers from the runtime scene. Keep their authored body
                // definitions when a running scene is serialized, otherwise
                // reloading that file would lose the compound children.
                var bodyCopy = (JsonObject)JsonNode.Parse(preservedBody.ToJsonString())!;
                bodyCopy["position"] = Vec3ToJson(e.Transform.Position);
                bodyCopy["rotation"] = QuatToJson(e.Transform.Rotation);
                obj["body"] = bodyCopy;
            }

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
        out VolumetricCloudSettings cloudSettings,
        out VolumetricFogSettings fogSettings,
        out OceanSettings oceanSettings,
        string? resPath = null,
        Action<float, string>? onProgress = null)
    {
        playerSpawn = null;
        skyboxPath = null;
        skyboxSettings = new SkyboxSettings();
        cloudSettings = new VolumetricCloudSettings();
        fogSettings = new VolumetricFogSettings();
        oceanSettings = new OceanSettings();
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
        if (root.TryGetPropertyValue("volumetric_clouds", out var cloudsNode) &&
            cloudsNode is JsonObject cloudsObject)
        {
            cloudSettings = VolumetricCloudSettings.FromJson(cloudsObject);
        }
        if (root.TryGetPropertyValue("volumetric_fog", out var fogNode) &&
            fogNode is JsonObject fogObject)
        {
            fogSettings = VolumetricFogSettings.FromJson(fogObject);
        }
        if (root.TryGetPropertyValue("ocean", out var oceanNode) &&
            oceanNode is JsonObject oceanObject)
        {
            oceanSettings = OceanSettings.FromJson(oceanObject);
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
        var objectNodesById = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        foreach (var objNode in objects)
        {
            if (objNode == null) continue;
            var obj = objNode.AsObject();
            string id = obj.TryGetPropertyValue("id", out var idNode) ? (string)idNode! : "unnamed";
            objectNodesById[id] = obj;
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

        bool HasVisualPayload(JsonObject obj)
        {
            if (obj.TryGetPropertyValue("model", out var modelToken) &&
                !string.IsNullOrWhiteSpace((string?)modelToken))
            {
                return true;
            }

            if (obj.TryGetPropertyValue("mesh", out var meshToken) &&
                !string.IsNullOrWhiteSpace((string?)meshToken))
            {
                return true;
            }

            if (obj.TryGetPropertyValue("geometry_graph", out var graphToken) &&
                !string.IsNullOrWhiteSpace((string?)graphToken))
            {
                return true;
            }

            if (obj.TryGetPropertyValue("terrain_asset", out var terrainToken) &&
                !string.IsNullOrWhiteSpace((string?)terrainToken))
            {
                return true;
            }

            if (obj.TryGetPropertyValue("staircase", out var staircaseToken) &&
                staircaseToken is JsonObject)
            {
                return true;
            }

            if (obj.TryGetPropertyValue("type", out var typeToken) &&
                string.Equals((string?)typeToken, "brush", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return obj.TryGetPropertyValue("light_type", out var lightToken) &&
                   !string.IsNullOrWhiteSpace((string?)lightToken);
        }

        bool IsDescendantOf(string potentialDescendant, string potentialAncestor)
        {
            string current = potentialDescendant;
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (parentMap.TryGetValue(current, out string? parentId) &&
                   !string.IsNullOrEmpty(parentId))
            {
                if (!visited.Add(current))
                    return false;
                if (parentId.Equals(potentialAncestor, StringComparison.OrdinalIgnoreCase))
                    return true;
                current = parentId;
            }
            return false;
        }

        bool IsCompoundCandidate(JsonObject obj, string id)
        {
            if (HasVisualPayload(obj) ||
                !obj.TryGetPropertyValue("body", out var bodyToken) ||
                bodyToken is not JsonObject bodyObject)
            {
                return false;
            }

            string shape = bodyObject.TryGetPropertyValue("shape", out var shapeToken)
                ? (string?)shapeToken ?? "none"
                : "none";
            if (shape.Equals("none", StringComparison.OrdinalIgnoreCase))
                return false;

            return objectNodesById.Keys.Any(childId =>
                !childId.Equals(id, StringComparison.OrdinalIgnoreCase) &&
                IsDescendantOf(childId, id));
        }

        // A group is an invisible hierarchy node with a collider authored on
        // it. Its descendants must be held back until the group body can be
        // assembled, otherwise Jolt creates one independent dynamic body per
        // child and the group itself has no physical effect.
        var physicalGroupIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach ((string id, JsonObject obj) in objectNodesById)
        {
            if (IsCompoundCandidate(obj, id))
                physicalGroupIds.Add(id);
        }

        string? FindPhysicalGroupOwner(string id)
        {
            string current = id;
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (parentMap.TryGetValue(current, out string? parentId) &&
                   !string.IsNullOrEmpty(parentId))
            {
                if (!visited.Add(current))
                    return null;
                if (physicalGroupIds.Contains(parentId))
                    return parentId;
                current = parentId;
            }
            return null;
        }

        var pendingChildBodies = new Dictionary<string, RigidBody>(StringComparer.OrdinalIgnoreCase);
        var pendingGroupBodies = new Dictionary<string, RigidBody>(StringComparer.OrdinalIgnoreCase);

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
            bool isStaircase = obj.TryGetPropertyValue("staircase", out var staircaseNode) &&
                               staircaseNode is JsonObject;
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
                    TerrainTileSetAsset terrain = TerrainTileSetAsset.Load(fullTerrainPath);
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

                    bool isProceduralTerrain = terrain.Procedural != null;
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
                        isProceduralTerrain ? null : physics,
                        isProceduralTerrain ? null : createdBodies,
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

                    if (terrain.Procedural != null && terrainVisible)
                    {
                        var proceduralLayer = new ProceduralTerrainLayer(
                            id,
                            terrain.Procedural,
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
                            parentId: "",
                            friction: terrainFriction,
                            restitution: terrainRestitution,
                            pixelError: obj.TryGetPropertyValue("terrain_pixel_error", out var proceduralPixelErrorNode)
                                ? MathF.Max(0.1f, (float)proceduralPixelErrorNode!)
                                : TerrainSceneBuilder.DefaultPixelError,
                            collisionLod: obj.TryGetPropertyValue("terrain_collision_lod", out var proceduralCollisionLodNode)
                                ? System.Math.Max(0, (int)proceduralCollisionLodNode!)
                                : TerrainSceneBuilder.DefaultCollisionLod);

                        foreach (TerrainTile tile in terrain.Tiles)
                        {
                            var coordinate = new TerrainTileCoordinate(tile.X, tile.Z);
                            bool hasCollision = proceduralLayer.Streamer.IsWithinRadius(
                                coordinate,
                                proceduralLayer.CollisionTileRadius);
                            proceduralLayer.MarkInitialTile(tile, false);
                            if (hasCollision)
                            {
                                if (TerrainSceneBuilder.AddCollisionToScene(
                                    scene,
                                    tile,
                                    id,
                                    terrainPosition,
                                    terrainRotation,
                                    physics,
                                    createdBodies,
                                    terrainFriction,
                                    terrainRestitution))
                                {
                                    proceduralLayer.CollisionTiles.Add(coordinate);
                                }
                            }
                        }
                        scene.RegisterProceduralTerrain(proceduralLayer);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error($"Map load: terrain '{terrainAssetPath}' failed for '{id}': {ex.Message}");
                }

                processedEntities++;
                onProgress?.Invoke((float)processedEntities / totalEntities, $"Processing {id}...");
                continue;
            }

            StaircaseSettings? staircaseSettings = isStaircase
                ? StaircaseSettings.FromJson((JsonObject)staircaseNode!)
                : null;
            Vector3 staircaseHalfExtents = new(0.5f);
            if (isStaircase &&
                obj.TryGetPropertyValue("body", out var staircaseBodyNode) &&
                staircaseBodyNode is JsonObject staircaseBodyObject &&
                staircaseBodyObject.TryGetPropertyValue("half_extents", out var staircaseHalfExtentsNode) &&
                staircaseHalfExtentsNode is JsonArray staircaseHalfExtentsArray)
            {
                staircaseHalfExtents = Vec3FromJson(staircaseHalfExtentsArray);
            }

            string meshKey = isModel
                ? (string)modelNode!
                : (obj.TryGetPropertyValue("mesh", out var meshNode)
                    ? (string)meshNode! : (isBrush ? id : (isStaircase ? "staircase" : "")));
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

            if (isStaircase)
            {
                generatedGeometry = StaircaseMeshGenerator.Generate(
                    staircaseHalfExtents,
                    staircaseSettings!);
            }
            else if (isBrush)
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
            entity.MeshOwnedByEntity = isBrush || isStaircase ||
                                       generatedGeometry != null && !string.IsNullOrWhiteSpace(geometryGraphPath);
            entity.MapData = obj;
            entity.MeshKey = isStaircase ? "staircase" : meshKey;
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

                if (isStaircase && staircaseSettings != null)
                {
                    var staircaseChildren = StaircaseMeshGenerator
                        .GenerateCollisionSteps(staircaseHalfExtents, staircaseSettings)
                        .Select(step => new RigidBody.CompoundChild(
                            RigidBody.ShapeType.Box,
                            step.Center,
                            Quaternion.Identity,
                            step.HalfExtents,
                            0.0f,
                            0.0f,
                            0.0f,
                            null,
                            null,
                            Vector3.One))
                        .ToArray();
                    body.SetCompound(staircaseChildren);
                }

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

                // Keep the authored pose on entities whose body will become a
                // child shape of a compound. Scene.UpdateTransforms will later
                // derive their world pose from the single group body.
                entity.Transform.Position = body.GetPosition();
                entity.Transform.Rotation = body.GetRotation();

                if (isBrush || isStaircase)
                    entity.Transform.Scale = Vector3.One;
                else if (!isModel && body.Type == RigidBody.ShapeType.Box)
                    entity.Transform.Scale = body.BoxHalfExtents * 2.0f;
                else if (!isModel && body.Type == RigidBody.ShapeType.Sphere)
                    entity.Transform.Scale = MeshGenerator.GetSphereRenderScale(body.SphereRadius);
                else if (!isModel && body.Type == RigidBody.ShapeType.Capsule)
                    entity.Transform.Scale = MeshGenerator.GetCapsuleRenderScale(
                        body.CapsuleRadius,
                        body.CapsuleHeight);
                else
                    entity.Transform.Scale = modelScale;

                bool isPhysicalGroup = physicalGroupIds.Contains(id);
                string? physicalGroupOwner = isPhysicalGroup
                    ? null
                    : FindPhysicalGroupOwner(id);

                if (isPhysicalGroup)
                {
                    pendingGroupBodies[id] = body;
                }
                else if (physicalGroupOwner != null)
                {
                    if (body.Type != RigidBody.ShapeType.None && !body.IsTrigger)
                    {
                        pendingChildBodies[id] = body;
                    }
                    else if (body.IsTrigger)
                    {
                        Logger.Warn($"Map load: trigger child '{id}' is not included in physical group '{physicalGroupOwner}'.");
                    }
                }
                else
                {
                    body.Build(physics);
                    entity.Body = body;
                    entity.Transform.Position = body.Position(physics);
                    entity.Transform.Rotation = body.Rotation(physics);
                    scene.RegisterBody(entity);
                    createdBodies.Add(body);
                }
            }
            else
            {
                entity.Transform.Scale = isBrush ? Vector3.One : modelScale;
            }

            processedEntities++;
            onProgress?.Invoke((float)processedEntities / totalEntities, $"Processing {id}...");
        }

        // Build each physical group as one Jolt body. Child render entities
        // remain in the scene for hierarchy transforms, but deliberately do
        // not receive their own RigidBody, so collision, mass and impulses are
        // solved for the complete compound shape.
        foreach ((string groupId, RigidBody groupBody) in pendingGroupBodies)
        {
            Renderer.Entity? groupEntity = scene.Entities.FirstOrDefault(entity =>
                entity.Id.Equals(groupId, StringComparison.OrdinalIgnoreCase));
            if (groupEntity == null)
                continue;

            var children = new List<RigidBody.CompoundChild>();
            foreach ((string childId, RigidBody childBody) in pendingChildBodies)
            {
                if (!string.Equals(
                        FindPhysicalGroupOwner(childId),
                        groupId,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                children.Add(childBody.CreateCompoundChild(
                    groupBody.GetPosition(),
                    groupBody.GetRotation()));
            }

            if (children.Count == 0)
            {
                // Keep a useful fallback for a malformed/empty compound
                // group. An explicitly authored primitive can still behave as
                // a normal body; an empty "compound" remains disabled.
                if (groupBody.Type == RigidBody.ShapeType.Compound)
                {
                    Logger.Warn($"Map load: physical group '{groupId}' has no valid child colliders.");
                    continue;
                }

                groupBody.Build(physics);
            }
            else
            {
                groupBody.SetCompound(children);
                groupBody.Build(physics);
            }

            if (!groupBody.IsBuilt)
            {
                Logger.Warn($"Map load: physical group '{groupId}' could not build its compound collider.");
                continue;
            }

            groupEntity.Body = groupBody;
            groupEntity.Transform.Position = groupBody.Position(physics);
            groupEntity.Transform.Rotation = groupBody.Rotation(physics);
            scene.RegisterBody(groupEntity);
            createdBodies.Add(groupBody);
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
            case RigidBody.ShapeType.Compound:
                // The child list is assembled from the hierarchy after all
                // objects have been parsed. Mark the body as compound now so
                // an explicit "compound" map value is not lost.
                body.SetCompound([]);
                break;
        }

        if (bj.TryGetPropertyValue("position", out var posToken))
            body.SetPosition(Vec3FromJson(posToken!.AsArray()));
        if (bj.TryGetPropertyValue("rotation", out var rotToken))
            body.SetRotation(QuatFromJson(rotToken!.AsArray()));
        if (bj.TryGetPropertyValue("mass", out var massToken))
            body.SetMass((float)massToken!);
        if (bj.TryGetPropertyValue("buoyancy_volume", out var buoyancyVolumeToken))
            body.SetBuoyancyVolumeOverride((float)buoyancyVolumeToken!);
        if (bj.TryGetPropertyValue("friction", out var frictionToken))
            body.SetFriction((float)frictionToken!);
        if (bj.TryGetPropertyValue("restitution", out var restToken))
            body.SetRestitution((float)restToken!);
        if (bj.TryGetPropertyValue("is_trigger", out var triggerToken))
            body.SetTrigger((bool)triggerToken!);
    }

    public static bool SaveToFile(Renderer.Scene scene, PhysicsWorld physics, string filepath,
        PlayerSpawn? playerSpawn = null,
        OceanSettings? oceanSettings = null,
        VolumetricFogSettings? fogSettings = null)
    {
        string json = SerializeScene(scene, physics, playerSpawn, oceanSettings, fogSettings);
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
        out VolumetricCloudSettings cloudSettings,
        out VolumetricFogSettings fogSettings,
        out OceanSettings oceanSettings,
        string? resPath = null,
        Action<float, string>? onProgress = null)
    {
        playerSpawn = null;
        skyboxPath = null;
        skyboxSettings = new SkyboxSettings();
        cloudSettings = new VolumetricCloudSettings();
        fogSettings = new VolumetricFogSettings();
        oceanSettings = new OceanSettings();
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
            out cloudSettings,
            out fogSettings,
            out oceanSettings,
            resPath,
            onProgress);
    }
}
