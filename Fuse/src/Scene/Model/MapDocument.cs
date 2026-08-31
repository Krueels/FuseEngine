using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Fuse.Scene.Model;

public class MapDocument
{
    public int Version { get; set; } = 1;
    public string SkyboxPath { get; set; } = "";
    public List<MapObject> Objects { get; set; } = [];
    public MapPlayerSpawn? PlayerSpawn { get; set; }
    public List<string> ValidationWarnings { get; } = [];

    public static MapDocument? Load(string path)
    {
        return TryLoad(path, out MapDocument? document, out _) ? document : null;
    }

    public static MapDocument? Parse(string json)
    {
        return TryParse(json, out MapDocument? document, out _) ? document : null;
    }

    public static bool TryLoad(string path, out MapDocument? document, out string error)
    {
        document = null;
        error = "";

        try
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                error = "No map path was provided.";
                return false;
            }

            if (!File.Exists(path))
            {
                error = $"Map file was not found: {path}";
                return false;
            }

            return TryParse(File.ReadAllText(path), out document, out error);
        }
        catch (Exception ex)
        {
            error = $"Could not read map '{path}': {ex.Message}";
            return false;
        }
    }

    public static bool TryParse(string json, out MapDocument? document, out string error)
    {
        document = null;
        error = "";

        try
        {
            JsonNode? rootNode = JsonNode.Parse(json);
            if (rootNode is not JsonObject root)
                throw new InvalidDataException("The map root must be a JSON object.");

            var doc = new MapDocument
            {
                Version = root.TryGetPropertyValue("version", out var verNode) ? (int)verNode! : 1,
                SkyboxPath = root.TryGetPropertyValue("skybox", out var skyboxNode)
                    ? (string?)skyboxNode ?? ""
                    : ""
            };

            if (root.TryGetPropertyValue("player_spawn", out var spawnNode) && spawnNode != null)
            {
                var sj = spawnNode.AsObject();
                doc.PlayerSpawn = new MapPlayerSpawn
                {
                    Position = Vec3FromJson(sj["position"]!.AsArray()),
                    Yaw = (float)sj["yaw"]!,
                    Pitch = (float)sj["pitch"]!
                };
            }

            if (root.TryGetPropertyValue("objects", out var objectsNode) && objectsNode != null)
            {
                foreach (var objNode in objectsNode.AsArray())
                {
                    if (objNode == null) continue;
                    doc.Objects.Add(ParseObject(objNode.AsObject()));
                }
            }

            SceneNameManager.EnsureAllUnique(doc);
            doc.ValidationWarnings.AddRange(SceneNameManager.ValidateAndRepairHierarchy(doc));

            document = doc;
            return true;
        }
        catch (Exception ex)
        {
            error = $"Invalid map data: {ex.Message}";
            document = null;
            return false;
        }
    }

    public string Serialize()
    {
        var root = new JsonObject
        {
            ["version"] = Version,
            ["objects"] = new JsonArray()
        };

        if (!string.IsNullOrWhiteSpace(SkyboxPath))
            root["skybox"] = SkyboxPath.Replace('\\', '/');

        if (PlayerSpawn != null)
        {
            root["player_spawn"] = new JsonObject
            {
                ["position"] = Vec3ToJson(PlayerSpawn.Position),
                ["yaw"] = PlayerSpawn.Yaw,
                ["pitch"] = PlayerSpawn.Pitch
            };
        }

        var objects = (JsonArray)root["objects"]!;
        foreach (var obj in Objects)
            objects.Add(SerializeObject(obj));

        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    public static MapObject ParseObject(JsonObject obj)
    {
        bool isBrush = obj.TryGetPropertyValue("type", out var typeNode) && (string)typeNode! == "brush";
        
        MapObject mo = isBrush ? new Brush() : new MapObject();

        mo.Id = obj.TryGetPropertyValue("id", out var idNode) ? (string)idNode! : "unnamed";
        mo.Visible = obj.TryGetPropertyValue("visible", out var visNode) ? (bool)visNode! : true;
        mo.ParentId = obj.TryGetPropertyValue("parent", out var parentNode) ? (string)parentNode! : null;
        mo.Mesh = obj.TryGetPropertyValue("mesh", out var meshNode) ? (string)meshNode! : null;
        mo.Model = obj.TryGetPropertyValue("model", out var modelNode) ? (string)modelNode! : null;
        mo.GeometryGraphPath = obj.TryGetPropertyValue("geometry_graph", out var graphNode) ? (string?)graphNode : null;
        if (obj.TryGetPropertyValue("model_scale", out var scaleNode))
        {
            if (scaleNode is JsonArray arr && arr.Count >= 3)
                mo.ModelScale = new System.Numerics.Vector3((float)arr[0]!, (float)arr[1]!, (float)arr[2]!);
            else
                mo.ModelScale = new System.Numerics.Vector3((float)scaleNode!);
        }
        else mo.ModelScale = System.Numerics.Vector3.One;
        mo.UvScale = obj.TryGetPropertyValue("uv_scale", out var uvNode) ? Vec2FromJson(uvNode!.AsArray()) : Vector2.One;
        mo.UvOffset = obj.TryGetPropertyValue("uv_offset", out var uvOffNode) ? Vec2FromJson(uvOffNode!.AsArray()) : Vector2.Zero;
        mo.UvRotation = obj.TryGetPropertyValue("uv_rotation", out var uvRotNode) ? (float)uvRotNode! : 0f;
        mo.MaterialPath = obj.TryGetPropertyValue("material", out var materialNode) ? (string)materialNode! : null;
        if (obj.TryGetPropertyValue("material_slots", out var slotsNode) && slotsNode is JsonArray slotsArray)
        {
            foreach (JsonNode? slot in slotsArray)
            {
                if (slot != null)
                    mo.MaterialSlots.Add(slot.GetValue<string>());
            }
        }
        mo.Texture = obj.TryGetPropertyValue("texture", out var texNode) ? (string)texNode! : null;
        mo.Interactable = obj.TryGetPropertyValue("interactable", out var interactNode) ? (string)interactNode! : null;
        if (obj.TryGetPropertyValue("behaviours", out var bArr) && bArr is JsonArray behavioursArray)
        {
            foreach (var node in behavioursArray)
            {
                if (node is JsonObject bObj)
                {
                    var bType = bObj.TryGetPropertyValue("type", out var bt) ? (string)bt! : "";
                    var bProps = bObj.TryGetPropertyValue("properties", out var bp) ? bp as JsonObject : new JsonObject();
                    if (!string.IsNullOrEmpty(bType))
                    {
                        mo.Behaviours.Add(new Fuse.Behaviours.BehaviourData { Type = bType, Properties = bProps != null ? (JsonObject)JsonNode.Parse(bProps.ToJsonString())! : new JsonObject() });
                    }
                }
            }
        }

        mo.LightType = obj.TryGetPropertyValue("light_type", out var ltNode) ? (string)ltNode! : null;
        if (mo.IsLight)
        {
            mo.LightColor = obj.TryGetPropertyValue("light_color", out var lcNode) ? Vec3FromJson(lcNode!.AsArray()) : System.Numerics.Vector3.One;
            mo.LightIntensity = obj.TryGetPropertyValue("light_intensity", out var liNode) ? (float)liNode! : 1.0f;
            mo.LightRadius = obj.TryGetPropertyValue("light_radius", out var lrNode) ? (float)lrNode! : 10.0f;
            mo.LightInnerCone = obj.TryGetPropertyValue("light_inner_cone", out var licNode) ? (float)licNode! : float.DegreesToRadians(20);
            mo.LightOuterCone = obj.TryGetPropertyValue("light_outer_cone", out var locNode) ? (float)locNode! : float.DegreesToRadians(30);
            mo.LightCastShadows = obj.TryGetPropertyValue("light_cast_shadows", out var csNode) && (bool)csNode!;
            mo.LightShadowBias = obj.TryGetPropertyValue("light_shadow_bias", out var sbNode) ? (float)sbNode! : 0.00100f;
            mo.LightDynamic = obj.TryGetPropertyValue("light_dynamic", out var dynNode) && (bool)dynNode!;
        }

        if (obj.TryGetPropertyValue("body", out var bodyNode))
            mo.Body = ParseBody(bodyNode!.AsObject());

        if (isBrush && mo is Brush brush)
        {
            if (obj.TryGetPropertyValue("faces", out var facesNode))
            {
                foreach (var faceNode in facesNode!.AsArray())
                {
                    if (faceNode == null) continue;
                    brush.AddFace(ParseFace(faceNode.AsObject()));
                }
            }

            if (obj.TryGetPropertyValue("geometry_mode", out var geometryModeNode) &&
                string.Equals((string?)geometryModeNode, "editable_mesh", StringComparison.OrdinalIgnoreCase) &&
                obj.TryGetPropertyValue("editable_mesh", out var editableMeshNode) && editableMeshNode is JsonObject editableMeshObject)
            {
                brush.EditableMesh = ParseEditableBrushMesh(editableMeshObject);
                brush.GeometryMode = BrushGeometryMode.EditableMesh;
            }
        }

        return mo;
    }

    private static MapBody ParseBody(JsonObject bj)
    {
        var body = new MapBody
        {
            Shape = ShapeFromString(bj.TryGetPropertyValue("shape", out var sNode) ? (string)sNode! : "none"),
            Mass = bj.TryGetPropertyValue("mass", out var mNode) ? (float)mNode! : 0,
            Friction = bj.TryGetPropertyValue("friction", out var fNode) ? (float)fNode! : 0.5f,
            Restitution = bj.TryGetPropertyValue("restitution", out var rNode) ? (float)rNode! : 0.3f,
            IsTrigger = bj.TryGetPropertyValue("is_trigger", out var TrigNode) ? (bool)TrigNode! : false,
        };

        if (bj.TryGetPropertyValue("position", out var posNode))
            body.Position = Vec3FromJson(posNode!.AsArray());
        if (bj.TryGetPropertyValue("rotation", out var rotNode))
            body.Rotation = QuatFromJson(rotNode!.AsArray());

        switch (body.Shape)
        {
            case MapShapeType.Box:
            case MapShapeType.Trimesh:
            case MapShapeType.ConvexHull:
                if (bj.TryGetPropertyValue("half_extents", out var heNode))
                    body.HalfExtents = Vec3FromJson(heNode!.AsArray());
                break;
            case MapShapeType.Sphere:
                if (bj.TryGetPropertyValue("radius", out var radNode))
                    body.Radius = (float)radNode!;
                break;
            case MapShapeType.Capsule:
                if (bj.TryGetPropertyValue("radius", out var capRadNode))
                    body.Radius = (float)capRadNode!;
                if (bj.TryGetPropertyValue("height", out var hNode))
                    body.Height = (float)hNode!;
                break;
            case MapShapeType.Plane:
                if (bj.TryGetPropertyValue("normal", out var nNode))
                    body.Normal = Vec3FromJson(nNode!.AsArray());
                if (bj.TryGetPropertyValue("distance", out var dNode))
                    body.Distance = (float)dNode!;
                break;
        }

        return body;
    }

    public static JsonObject SerializeObject(MapObject obj)
    {
        var j = new JsonObject
        {
            ["id"] = obj.Id,
            ["visible"] = obj.Visible
        };

        if (!string.IsNullOrEmpty(obj.ParentId))
            j["parent"] = obj.ParentId;

        if (obj is Brush brush)
        {
            j["type"] = "brush";
            var facesArray = new JsonArray();
            foreach (var face in brush.Faces)
            {
                facesArray.Add(SerializeFace(face));
            }
            j["faces"] = facesArray;

            if (brush.IsEditableMesh)
            {
                j["geometry_mode"] = "editable_mesh";
                j["editable_mesh"] = SerializeEditableBrushMesh(brush.EditableMesh!);
            }
        }

        if (obj.IsModel)
        {
            j["model"] = obj.Model!;
            if (obj.ModelScale != System.Numerics.Vector3.One)
                j["model_scale"] = new JsonArray { obj.ModelScale.X, obj.ModelScale.Y, obj.ModelScale.Z };
        }
        else if (obj.Mesh != null)
        {
            j["mesh"] = obj.Mesh;
            if (obj.UvScale != Vector2.One)
                j["uv_scale"] = Vec2ToJson(obj.UvScale);
            if (obj.UvOffset != Vector2.Zero)
                j["uv_offset"] = Vec2ToJson(obj.UvOffset);
            if (obj.UvRotation != 0f)
                j["uv_rotation"] = obj.UvRotation;
        }

        if (!string.IsNullOrEmpty(obj.MaterialPath))
            j["material"] = obj.MaterialPath;
        if (obj.MaterialSlots.Count > 0)
        {
            var materialSlots = new JsonArray();
            foreach (string slot in obj.MaterialSlots)
                materialSlots.Add(slot);
            j["material_slots"] = materialSlots;
        }

        if (!string.IsNullOrWhiteSpace(obj.GeometryGraphPath))
            j["geometry_graph"] = obj.GeometryGraphPath.Replace('\\', '/');

        // Legacy compatibility. New objects normally use material/material_slots.
        if (!string.IsNullOrEmpty(obj.Texture))
            j["texture"] = obj.Texture;
        if (!string.IsNullOrEmpty(obj.Interactable))
            j["interactable"] = obj.Interactable;
        if (obj.Behaviours.Count > 0)
        {
            var arr = new JsonArray();
            foreach (var b in obj.Behaviours)
            {
                var bObj = new JsonObject();
                bObj["type"] = b.Type;
                bObj["properties"] = b.Properties != null ? JsonNode.Parse(b.Properties.ToJsonString()) : new JsonObject();
                arr.Add(bObj);
            }
            j["behaviours"] = arr;
        }

        if (obj.IsLight)
        {
            j["light_type"] = obj.LightType!;
            j["light_color"] = Vec3ToJson(obj.LightColor);
            j["light_intensity"] = obj.LightIntensity;
            j["light_radius"] = obj.LightRadius;
            j["light_inner_cone"] = obj.LightInnerCone;
            j["light_outer_cone"] = obj.LightOuterCone;
            j["light_cast_shadows"] = obj.LightCastShadows;
            j["light_shadow_bias"] = obj.LightShadowBias;
            j["light_dynamic"] = obj.LightDynamic;
        }

        if (obj.Body != null)
            j["body"] = SerializeBody(obj.Body);

        return j;
    }

    private static JsonObject SerializeBody(MapBody body)
    {
        var bj = new JsonObject
        {
            ["shape"] = ShapeToString(body.Shape),
            ["position"] = Vec3ToJson(body.Position),
            ["rotation"] = QuatToJson(body.Rotation),
            ["mass"] = body.Mass,
            ["friction"] = body.Friction,
            ["restitution"] = body.Restitution,
            ["is_trigger"] = body.IsTrigger,
        };

        switch (body.Shape)
        {
            case MapShapeType.Box:
            case MapShapeType.Trimesh:
            case MapShapeType.ConvexHull:
                if (body.HalfExtents.HasValue)
                    bj["half_extents"] = Vec3ToJson(body.HalfExtents.Value);
                break;
            case MapShapeType.Sphere:
                if (body.Radius.HasValue)
                    bj["radius"] = body.Radius.Value;
                break;
            case MapShapeType.Capsule:
                if (body.Radius.HasValue)
                    bj["radius"] = body.Radius.Value;
                if (body.Height.HasValue)
                    bj["height"] = body.Height.Value;
                break;
            case MapShapeType.Plane:
                if (body.Normal.HasValue)
                    bj["normal"] = Vec3ToJson(body.Normal.Value);
                if (body.Distance.HasValue)
                    bj["distance"] = body.Distance.Value;
                break;
        }

        return bj;
    }

    private static Face ParseFace(JsonObject fj)
    {
        var normal = fj.TryGetPropertyValue("normal", out var nNode) ? Vec3FromJson(nNode!.AsArray()) : Vector3.UnitY;
        var d = fj.TryGetPropertyValue("d", out var dNode) ? (float)dNode! : 0f;
        var face = new Face(new Plane(normal, d));

        if (fj.TryGetPropertyValue("texture", out var tNode)) face.Texture = (string)tNode!;
        if (fj.TryGetPropertyValue("material_slot", out var materialSlotNode)) face.MaterialSlot = (int)materialSlotNode!;
        if (fj.TryGetPropertyValue("u_axis", out var uaNode)) face.UAxis = Vec3FromJson(uaNode!.AsArray());
        if (fj.TryGetPropertyValue("v_axis", out var vaNode)) face.VAxis = Vec3FromJson(vaNode!.AsArray());
        if (fj.TryGetPropertyValue("u_scale", out var usNode)) face.UScale = (float)usNode!;
        if (fj.TryGetPropertyValue("v_scale", out var vsNode)) face.VScale = (float)vsNode!;
        if (fj.TryGetPropertyValue("u_offset", out var uoNode)) face.UOffset = (float)uoNode!;
        if (fj.TryGetPropertyValue("v_offset", out var voNode)) face.VOffset = (float)voNode!;
        if (fj.TryGetPropertyValue("rotation", out var rNode)) face.Rotation = (float)rNode!;

        return face;
    }

    private static JsonObject SerializeFace(Face face)
    {
        var result = new JsonObject
        {
            ["normal"] = Vec3ToJson(face.Plane.Normal),
            ["d"] = face.Plane.D,
            ["texture"] = face.Texture,
            ["u_axis"] = Vec3ToJson(face.UAxis),
            ["v_axis"] = Vec3ToJson(face.VAxis),
            ["u_scale"] = face.UScale,
            ["v_scale"] = face.VScale,
            ["u_offset"] = face.UOffset,
            ["v_offset"] = face.VOffset,
            ["rotation"] = face.Rotation
        };

        // Slot zero is the implicit/default material and does not need to make
        // legacy map files noisy when they are saved by the new editor.
        if (face.MaterialSlot != 0)
            result["material_slot"] = face.MaterialSlot;

        return result;
    }

    private static EditableBrushMesh ParseEditableBrushMesh(JsonObject source)
    {
        var mesh = new EditableBrushMesh
        {
            NextVertexId = source.TryGetPropertyValue("next_vertex_id", out var nextVertexNode) ? (int)nextVertexNode! : 1,
            NextFaceId = source.TryGetPropertyValue("next_face_id", out var nextFaceNode) ? (int)nextFaceNode! : 1
        };

        if (source.TryGetPropertyValue("vertices", out var verticesNode) && verticesNode is JsonArray vertices)
        {
            foreach (JsonNode? node in vertices)
            {
                if (node is not JsonObject vertex)
                    continue;
                if (!vertex.TryGetPropertyValue("id", out var idNode) || !vertex.TryGetPropertyValue("position", out var positionNode))
                    continue;
                mesh.Vertices.Add(new EditableBrushVertex
                {
                    Id = (int)idNode!,
                    Position = Vec3FromJson(positionNode!.AsArray())
                });
            }
        }

        if (source.TryGetPropertyValue("faces", out var facesNode) && facesNode is JsonArray faces)
        {
            foreach (JsonNode? node in faces)
            {
                if (node is not JsonObject face ||
                    !face.TryGetPropertyValue("id", out var idNode) ||
                    !face.TryGetPropertyValue("vertices", out var faceVerticesNode) ||
                    faceVerticesNode is not JsonArray faceVertices)
                    continue;

                var parsed = new EditableBrushFace
                {
                    Id = (int)idNode!,
                    Texture = face.TryGetPropertyValue("texture", out var textureNode) ? (string?)textureNode ?? "default" : "default",
                    MaterialSlot = face.TryGetPropertyValue("material_slot", out var slotNode) ? (int)slotNode! : 0,
                    UAxis = face.TryGetPropertyValue("u_axis", out var uAxisNode) ? Vec3FromJson(uAxisNode!.AsArray()) : Vector3.UnitX,
                    VAxis = face.TryGetPropertyValue("v_axis", out var vAxisNode) ? Vec3FromJson(vAxisNode!.AsArray()) : -Vector3.UnitZ,
                    UScale = face.TryGetPropertyValue("u_scale", out var uScaleNode) ? (float)uScaleNode! : 1.0f,
                    VScale = face.TryGetPropertyValue("v_scale", out var vScaleNode) ? (float)vScaleNode! : 1.0f,
                    UOffset = face.TryGetPropertyValue("u_offset", out var uOffsetNode) ? (float)uOffsetNode! : 0.0f,
                    VOffset = face.TryGetPropertyValue("v_offset", out var vOffsetNode) ? (float)vOffsetNode! : 0.0f,
                    Rotation = face.TryGetPropertyValue("rotation", out var rotationNode) ? (float)rotationNode! : 0.0f
                };
                foreach (JsonNode? vertexId in faceVertices)
                {
                    if (vertexId != null)
                        parsed.Vertices.Add(vertexId.GetValue<int>());
                }
                mesh.Faces.Add(parsed);
            }
        }

        return mesh;
    }

    private static JsonObject SerializeEditableBrushMesh(EditableBrushMesh mesh)
    {
        var result = new JsonObject
        {
            ["next_vertex_id"] = mesh.NextVertexId,
            ["next_face_id"] = mesh.NextFaceId
        };

        var vertices = new JsonArray();
        foreach (EditableBrushVertex vertex in mesh.Vertices)
        {
            vertices.Add(new JsonObject
            {
                ["id"] = vertex.Id,
                ["position"] = Vec3ToJson(vertex.Position)
            });
        }
        result["vertices"] = vertices;

        var faces = new JsonArray();
        foreach (EditableBrushFace face in mesh.Faces)
        {
            var faceVertices = new JsonArray();
            foreach (int vertexId in face.Vertices)
                faceVertices.Add(vertexId);

            faces.Add(new JsonObject
            {
                ["id"] = face.Id,
                ["vertices"] = faceVertices,
                ["texture"] = face.Texture,
                ["material_slot"] = face.MaterialSlot,
                ["u_axis"] = Vec3ToJson(face.UAxis),
                ["v_axis"] = Vec3ToJson(face.VAxis),
                ["u_scale"] = face.UScale,
                ["v_scale"] = face.VScale,
                ["u_offset"] = face.UOffset,
                ["v_offset"] = face.VOffset,
                ["rotation"] = face.Rotation
            });
        }
        result["faces"] = faces;
        return result;
    }

    private static Vector3 Vec3FromJson(JsonArray arr) => new(
        (float)arr[0]!, (float)arr[1]!, (float)arr[2]!);

    private static Vector2 Vec2FromJson(JsonArray arr) => new(
        (float)arr[0]!, (float)arr[1]!);

    private static Quaternion QuatFromJson(JsonArray arr) => new(
        (float)arr[1]!, (float)arr[2]!, (float)arr[3]!, (float)arr[0]!);

    private static JsonArray Vec3ToJson(Vector3 v) => new(v.X, v.Y, v.Z);
    private static JsonArray Vec2ToJson(Vector2 v) => new(v.X, v.Y);
    private static JsonArray QuatToJson(Quaternion q) => new(q.W, q.X, q.Y, q.Z);

    private static MapShapeType ShapeFromString(string s) => s switch
    {
        "box" => MapShapeType.Box,
        "sphere" => MapShapeType.Sphere,
        "capsule" => MapShapeType.Capsule,
        "plane" => MapShapeType.Plane,
        "trimesh" => MapShapeType.Trimesh,
        "convexhull" => MapShapeType.ConvexHull,
        _ => MapShapeType.None
    };

    private static string ShapeToString(MapShapeType t) => t switch
    {
        MapShapeType.Box => "box",
        MapShapeType.Sphere => "sphere",
        MapShapeType.Capsule => "capsule",
        MapShapeType.Plane => "plane",
        MapShapeType.Trimesh => "trimesh",
        MapShapeType.ConvexHull => "convexhull",
        _ => "none"
    };
}
