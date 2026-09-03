using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using Fuse.Behaviours;
using Fuse.Core;
using Fuse.Interaction;
using Fuse.Physics;
using Fuse.Renderer;
using Fuse.Scene.Model;
using JoltPhysicsSharp;

namespace Fuse.Scene;

public class SceneManager
{
    private readonly Renderer.Scene _scene;
    private readonly PhysicsWorld _physics;
    private readonly AssetManagement.AssetManager _assets;

    public string CurrentMapPath { get; private set; } = null!;
    public Renderer.Scene ActiveScene => _scene;
    public MasterRenderer Renderer => _renderer;
    public PhysicsWorld Physics => _physics;

    private readonly List<RigidBody> _bodies = [];
    private readonly List<IInteractable> _interactables = [];
    private readonly Dictionary<JoltPhysicsSharp.BodyID, GCHandle> _interactableHandles = [];
    private readonly List<IBehaviour> _behaviours = [];
    private TriggerSystem _triggerSystem = null!;
    private readonly MasterRenderer _renderer;

    public SceneManager(PhysicsWorld physics, AssetManagement.AssetManager assets, MasterRenderer renderer = null!)
    {
        _scene = new Renderer.Scene();
        _physics = physics;
        _assets = assets;
        _renderer = renderer;
    }

    public void InitTriggerSystem(Player.Player player)
    {
        _triggerSystem = new TriggerSystem(
            player.NativeCharacter,
            _behaviours,
            id => _scene.GetEntityByBody(id),
            player.GetBodyLockInterface()
        );
    }

    public PlayerSpawn? LoadMap(string fileName, Action<float, string>? onProgress = null)
    {
        string loadPath = $"{Fuse.ResPath.Path}/Maps/{fileName}";
        if (!File.Exists(loadPath))
        {
            Logger.Error($"Map not found: {loadPath}");
            return null;
        }

        onProgress?.Invoke(0f, "Clearing scene...");
        ClearCurrentMap();

        onProgress?.Invoke(0.05f, "Loading map data...");
        var loaded = MapSerializer.LoadFromFile(
            loadPath,
            _scene,
            _physics,
            _assets,
            out var spawn,
            out var skyboxPath,
            out var skyboxSettings,
            out var cloudSettings,
            out var fogSettings,
            out var oceanSettings,
            Fuse.ResPath.Path,
            onProgress);

        if (loaded != null)
        {
            _bodies.AddRange(loaded);
        }

        onProgress?.Invoke(0.82f, "Loading skybox...");
        ApplyMapSkybox(skyboxPath, skyboxSettings);
        _renderer?.SetVolumetricClouds(cloudSettings);
        _renderer?.SetVolumetricFog(fogSettings);
        _renderer?.SetOcean(oceanSettings);

        onProgress?.Invoke(0.85f, "Registering interactions...");
        RegisterInteractablesAndBehaviours();
        CurrentMapPath = loadPath;

        onProgress?.Invoke(1.0f, "Done!");
        Logger.Info($"Map loaded: {fileName}");

        _renderer?.ClearBillboardQueue();

        return spawn;
    }

    public PlayerSpawn? ReloadMap(Action<float, string>? onProgress = null)
    {
        if (string.IsNullOrEmpty(CurrentMapPath)) return null;

        onProgress?.Invoke(0f, "Clearing scene...");
        ClearCurrentMap();

        onProgress?.Invoke(0.05f, "Loading map data...");
        var loaded = MapSerializer.LoadFromFile(
            CurrentMapPath,
            _scene,
            _physics,
            _assets,
            out var spawn,
            out var skyboxPath,
            out var skyboxSettings,
            out var cloudSettings,
            out var fogSettings,
            out var oceanSettings,
            Fuse.ResPath.Path,
            onProgress);
            
        if (loaded != null)
        {
            _bodies.AddRange(loaded);
        }

        onProgress?.Invoke(0.82f, "Loading skybox...");
        ApplyMapSkybox(skyboxPath, skyboxSettings);
        _renderer?.SetVolumetricClouds(cloudSettings);
        _renderer?.SetVolumetricFog(fogSettings);
        _renderer?.SetOcean(oceanSettings);

        onProgress?.Invoke(0.85f, "Registering interactions...");
        RegisterInteractablesAndBehaviours();

        onProgress?.Invoke(1.0f, "Done!");

        _renderer?.ClearBillboardQueue();
        return spawn;
    }

    private void ApplyMapSkybox(string? configuredPath, SkyboxSettings settings)
    {
        if (settings.Mode == SkyboxMode.Procedural)
        {
            _renderer?.SetProceduralSkybox(settings);
            return;
        }

        string skyboxPath = ResolveMapSkyboxPath(configuredPath);
        if (!File.Exists(skyboxPath))
        {
            Logger.Warn($"Map skybox not found: {skyboxPath}. Falling back to the default skybox.");
            skyboxPath = Bible.Tex(Bible.Skybox);
        }

        Texture texture = _assets.GetTexture(skyboxPath, TextureColorSpace.Srgb);
        if (texture.ID == 0)
        {
            Logger.Error($"Failed to load map skybox: {skyboxPath}");
            return;
        }

        _renderer?.SetSkyboxTexture(texture);
    }

    private static string ResolveMapSkyboxPath(string? configuredPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
            return Bible.Tex(Bible.Skybox);

        string normalized = configuredPath.Replace('\\', '/').TrimStart('/');
        if (normalized.StartsWith("res/", StringComparison.OrdinalIgnoreCase))
            normalized = normalized[4..];
        if (normalized.StartsWith("Textures/", StringComparison.OrdinalIgnoreCase))
            return Path.GetFullPath(Path.Combine(Fuse.ResPath.Path, normalized));
        return Path.GetFullPath(Path.Combine(Fuse.ResPath.Path, "Textures", normalized));
    }

private void ClearCurrentMap()
{
    _interactables.Clear();
    _behaviours.Clear();

    // 1. Zerar UserData ANTES de tudo: callbacks nativos (contato/raycast) nunca
    //    devem ler ponteiros de GChandles que serão liberados
    foreach (var kv in _interactableHandles)
        _physics.BodyInterface.SetUserData(kv.Key, 0);

    // 2. Liberar GCHandles
    foreach (var handle in _interactableHandles.Values)
        handle.Free();
    _interactableHandles.Clear();

    // 3. Destruir corpos E resetar os wrappers RigidBody.
    //    Sem b.Destroy() o flag IsBuilt fica true apontando pra BodyID morto —
    //    e a Jolt recicla IDs, então referências stale agem em corpos alheios → crash nativo.
    foreach (var b in _bodies)
    {
        if (b.IsBuilt)
        {
            _physics.DestroyBody(b.Native);
            b.Destroy();
        }
    }
    _bodies.Clear();
        
    _scene.Clear();
}

    private void RegisterInteractablesAndBehaviours()
    {
        foreach (var entity in _scene.Entities)
        {
            if (entity.Body != null)
                _scene.RegisterBody(entity);
        }

        foreach (var entity in _scene.Entities)
        {
            if (entity.Body != null && entity.Body.IsBuilt && !string.IsNullOrEmpty(entity.InteractableType))
            {
                var interactable = InteractionSystem.CreateInteractable(entity.InteractableType);
                if (interactable != null)
                {
                    interactable.Entity = entity;
                    interactable.World = _physics;
                    _interactables.Add(interactable);
                    var gcHandle = GCHandle.Alloc(interactable);
                    _interactableHandles[entity.Body.Native] = gcHandle;
                    _physics.BodyInterface.SetUserData(entity.Body.Native, (ulong)GCHandle.ToIntPtr(gcHandle));
                }
            }
        }

        foreach (var entity in _scene.Entities)
        {
            if (entity.Body != null && entity.Body.IsBuilt && entity.Behaviours.Count > 0)
            {
                foreach (var bData in entity.Behaviours)
                {
                    var behaviour = BehaviourSystem.Create(bData.Type);
                    if (behaviour != null)
                    {
                        var t = behaviour.GetType();
                        foreach (var prop in t.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
                        {
                            if (prop.GetCustomAttribute<ExportAttribute>() != null)
                            {
                                if (bData.Properties.TryGetPropertyValue(prop.Name, out var valNode) && valNode != null)
                                {
                                    try {
                                        if (prop.PropertyType == typeof(float)) prop.SetValue(behaviour, (float)valNode);
                                        else if (prop.PropertyType == typeof(int)) prop.SetValue(behaviour, (int)valNode);
                                        else if (prop.PropertyType == typeof(bool)) prop.SetValue(behaviour, (bool)valNode);
                                        else if (prop.PropertyType == typeof(string))
                                        {
                                            string? stringValue = (string?)valNode;
                                            if (stringValue != null)
                                                prop.SetValue(behaviour, stringValue);
                                        }
                                    } catch { }
                                }
                            }
                        }

                        behaviour.Entity = entity;
                        behaviour.World = _physics;
                        _behaviours.Add(behaviour);
                    }
                }
            }
        }
    }

    public void Update(float dt)
    {
        _scene.UpdateAnimators(dt);

        foreach (var interactable in _interactables)
            interactable.Update(dt);
            
        foreach (var behaviour in _behaviours)
            behaviour.Update(dt);
            
        if (_triggerSystem != null)
            _triggerSystem.Update(dt);
    }
    
    public bool CheckPendingResets()
    {
        foreach (var behaviour in _behaviours)
        {
            if (behaviour is TriggerReset reset && reset.PendingReset)
            {
                reset.PendingReset = false;
                return true;
            }
        }
        return false;
    }

    public void DrawDebug(Debug.DebugDrawer debugDrawer)
    {
        foreach (var b in _bodies)
        {
            if (!b.IsBuilt) continue;

            var pos = b.Position(_physics);
            var rot = b.Rotation(_physics);
            var color = b.Mass > 0 ? new Vector3(1, 1, 0) : new Vector3(1, 0, 0);

            switch (b.Type)
            {
                case RigidBody.ShapeType.Box:
                    debugDrawer.DrawBox(pos, rot, b.BoxHalfExtents, color);
                    break;
                case RigidBody.ShapeType.Sphere:
                    debugDrawer.DrawSphere(pos, rot, b.SphereRadius, color);
                    break;
                case RigidBody.ShapeType.Capsule:
                    debugDrawer.DrawCapsule(pos, rot, b.CapsuleHeight * 0.5f, b.CapsuleRadius, color);
                    break;
                case RigidBody.ShapeType.ConvexHull:
                case RigidBody.ShapeType.Trimesh:
                    if (b.TrimeshVertices != null && b.TrimeshIndices != null)
                        debugDrawer.DrawTrimesh(pos, rot, b.TrimeshVertices, b.TrimeshIndices, color, b.TrimeshScale);
                    break;
                case RigidBody.ShapeType.Compound:
                    foreach (RigidBody.CompoundChild child in b.CompoundChildren)
                    {
                        Vector3 childPosition = pos + Vector3.Transform(child.Position, rot);
                        Quaternion childRotation = Quaternion.Normalize(rot * child.Rotation);
                        switch (child.Type)
                        {
                            case RigidBody.ShapeType.Box:
                                debugDrawer.DrawBox(childPosition, childRotation, child.BoxHalfExtents, color);
                                break;
                            case RigidBody.ShapeType.Sphere:
                                debugDrawer.DrawSphere(childPosition, childRotation, child.SphereRadius, color);
                                break;
                            case RigidBody.ShapeType.Capsule:
                                debugDrawer.DrawCapsule(childPosition, childRotation,
                                    child.CapsuleHeight * 0.5f, child.CapsuleRadius, color);
                                break;
                            case RigidBody.ShapeType.ConvexHull:
                            case RigidBody.ShapeType.Trimesh:
                                if (child.Vertices != null && child.Indices != null)
                                {
                                    debugDrawer.DrawTrimesh(childPosition, childRotation,
                                        child.Vertices, child.Indices, color, child.Scale);
                                }
                                break;
                        }
                    }
                    break;
            }
        }
    }

    public RigidBody? GetRigidBody(BodyID id)
    {
        for (int i = 0; i < _bodies.Count; i++)
        {
            if (_bodies[i].Native == id)
                return _bodies[i];
        }
        return null;
    }

    /// <summary>
    /// Performs an exact raycast against the physics world and calculates the accurate surface normal
    /// for boxes, planes, spheres, and complex .OBJ trimesh/convex hull geometry.
    /// </summary>
    public bool Raycast(
        Vector3 origin,
        Vector3 direction,
        float maxDistance,
        out SceneRaycastHit hitResult,
        BodyID? excludedBody = null,
        bool collideWithBackFaces = false,
        IReadOnlySet<BodyID>? excludedBodies = null)
    {
        hitResult = default;
        Vector3 dirNormalized = Vector3.Normalize(direction);
        Vector3 dirScaled = dirNormalized * maxDistance;
        var ray = new Ray(in origin, in dirScaled);

        using var bpFilter = new Physics.DefaultBroadPhaseLayerFilter();
        using var olFilter = new Physics.DefaultObjectLayerFilter();
        // Procedural animation probes must never use their own collider as a
        // landing surface. Keeping this optional preserves the normal scene
        // raycast behaviour for every other caller.
        using BodyFilter bodyFilter = excludedBody.HasValue
            ? new Physics.EnemyBodyFilter(
                excludedBody.Value,
                excludedBodies)
            : excludedBodies != null && excludedBodies.Count > 0
                ? new Physics.EnemyBodyFilter(excludedBodies)
                : new Physics.DefaultBodyFilter();

        RayCastResult hit;
        if (collideWithBackFaces)
        {
            // Procedural surface queries must see both windings. Imported and
            // generated meshes can expose opposite cube faces with opposite
            // triangle winding; Jolt's default raycast ignores one of them.
            var settings = new RayCastSettings
            {
                BackFaceModeTriangles = BackFaceMode.CollideWithBackFaces,
                BackFaceModeConvex = BackFaceMode.CollideWithBackFaces,
                TreatConvexAsSolid = true
            };
            using var shapeFilter = new Physics.DefaultShapeFilter();
            var hits = new List<RayCastResult>(1);
            if (!_physics.NarrowPhaseQuery.CastRay(
                    ray,
                    settings,
                    CollisionCollectorType.ClosestHit,
                    hits,
                    bpFilter,
                    olFilter,
                    bodyFilter,
                    shapeFilter) || hits.Count == 0)
            {
                return false;
            }

            hit = hits[0];
        }
        else if (!_physics.NarrowPhaseQuery.CastRay(ray, out hit, bpFilter, olFilter, bodyFilter))
        {
            return false;
        }

        Vector3 hitPos = origin + dirNormalized * maxDistance * hit.Fraction;
        Vector3 hitNormal = -dirNormalized;
        var rigidBody = GetRigidBody(hit.BodyID);

        if (rigidBody != null)
        {
            Vector3 bodyPos = rigidBody.Position(_physics);
            Quaternion bodyRot = rigidBody.Rotation(_physics);

            switch (rigidBody.Type)
            {
                case RigidBody.ShapeType.Box:
                {
                    Vector3 localHit = Vector3.Transform(hitPos - bodyPos, Quaternion.Inverse(bodyRot));
                    Vector3 ext = rigidBody.BoxHalfExtents;
                    if (ext.X <= 0.0001f) ext.X = 0.0001f;
                    if (ext.Y <= 0.0001f) ext.Y = 0.0001f;
                    if (ext.Z <= 0.0001f) ext.Z = 0.0001f;

                    float rx = MathF.Abs(localHit.X) / ext.X;
                    float ry = MathF.Abs(localHit.Y) / ext.Y;
                    float rz = MathF.Abs(localHit.Z) / ext.Z;

                    Vector3 localNormal;
                    if (ry >= rx && ry >= rz)
                        localNormal = localHit.Y >= 0 ? Vector3.UnitY : -Vector3.UnitY;
                    else if (rx >= ry && rx >= rz)
                        localNormal = localHit.X >= 0 ? Vector3.UnitX : -Vector3.UnitX;
                    else
                        localNormal = localHit.Z >= 0 ? Vector3.UnitZ : -Vector3.UnitZ;

                    hitNormal = Vector3.Normalize(Vector3.Transform(localNormal, bodyRot));
                    break;
                }
                case RigidBody.ShapeType.Plane:
                {
                    hitNormal = rigidBody.PlaneNormal;
                    break;
                }
                case RigidBody.ShapeType.Sphere:
                {
                    hitNormal = Vector3.Normalize(hitPos - bodyPos);
                    break;
                }
                case RigidBody.ShapeType.HeightField:
                {
                    Vector3 localHit = Vector3.Transform(
                        hitPos - bodyPos,
                        Quaternion.Inverse(bodyRot));
                    Vector3 localNormal = rigidBody.GetHeightFieldSurfaceNormal(localHit);
                    hitNormal = Vector3.Normalize(Vector3.Transform(localNormal, bodyRot));
                    break;
                }
                case RigidBody.ShapeType.Trimesh:
                case RigidBody.ShapeType.ConvexHull:
                {
                    if (rigidBody.TrimeshVertices != null && rigidBody.TrimeshVertices.Length >= 3)
                    {
                        var invRot = Quaternion.Inverse(bodyRot);
                        Vector3 localOrigin = Vector3.Transform(origin - bodyPos, invRot);
                        Vector3 localDir = Vector3.Transform(dirNormalized, invRot);
                        Vector3 localHit = Vector3.Transform(hitPos - bodyPos, invRot);

                        Vector3 localNormal = FindClosestTriangleNormal(
                            rigidBody.TrimeshVertices,
                            rigidBody.TrimeshIndices,
                            rigidBody.TrimeshScale,
                            localOrigin,
                            localDir,
                            localHit);

                        hitNormal = Vector3.Normalize(Vector3.Transform(localNormal, bodyRot));
                    }
                    else
                    {
                        hitNormal = -dirNormalized;
                    }
                    break;
                }
                default:
                {
                    hitNormal = -dirNormalized;
                    break;
                }
            }

            if (Vector3.Dot(hitNormal, dirNormalized) > 0)
                hitNormal = -hitNormal;
        }

        hitResult = new SceneRaycastHit
        {
            HasHit    = true,
            Position  = hitPos,
            Normal    = hitNormal,
            Distance  = maxDistance * hit.Fraction,
            BodyID    = hit.BodyID,
            RigidBody = rigidBody
        };

        return true;
    }

    private static Vector3 FindClosestTriangleNormal(Vector3[] verts, uint[]? indices, Vector3 scale, Vector3 localRayOrigin, Vector3 localRayDir, Vector3 localHitPos)
    {
        Vector3 closestHitNormal = -localRayDir;
        float closestHitDistance = float.MaxValue;
        bool foundTriangleHit = false;
        Vector3 closestFallbackNormal = -localRayDir;
        float closestFallbackDistanceSq = float.MaxValue;

        int triCount = indices != null ? indices.Length / 3 : verts.Length / 3;

        for (int i = 0; i < triCount; i++)
        {
            uint rawI0 = indices != null ? indices[i * 3] : (uint)(i * 3);
            uint rawI1 = indices != null ? indices[i * 3 + 1] : (uint)(i * 3 + 1);
            uint rawI2 = indices != null ? indices[i * 3 + 2] : (uint)(i * 3 + 2);
            if (rawI0 >= (uint)verts.Length ||
                rawI1 >= (uint)verts.Length ||
                rawI2 >= (uint)verts.Length)
            {
                continue;
            }

            int i0 = (int)rawI0;
            int i1 = (int)rawI1;
            int i2 = (int)rawI2;
            Vector3 v0 = verts[i0] * scale;
            Vector3 v1 = verts[i1] * scale;
            Vector3 v2 = verts[i2] * scale;

            Vector3 edge1 = v1 - v0;
            Vector3 edge2 = v2 - v0;
            Vector3 triNormal = Vector3.Cross(edge1, edge2);
            float lenSq = triNormal.LengthSquared();
            if (lenSq < 1e-8f) continue;
            triNormal = Vector3.Normalize(triNormal);

            // Ray-Triangle intersection test (Möller–Trumbore)
            Vector3 h = Vector3.Cross(localRayDir, edge2);
            float a = Vector3.Dot(edge1, h);
            if (MathF.Abs(a) > 1e-7f)
            {
                float f = 1.0f / a;
                Vector3 s = localRayOrigin - v0;
                float u = f * Vector3.Dot(s, h);
                if (u >= -0.05f && u <= 1.05f)
                {
                    Vector3 q = Vector3.Cross(s, edge1);
                    float v = f * Vector3.Dot(localRayDir, q);
                    if (v >= -0.05f && (u + v) <= 1.05f)
                    {
                        float t = f * Vector3.Dot(edge2, q);
                        if (t > 0.001f && t < closestHitDistance)
                        {
                            closestHitDistance = t;
                            closestHitNormal = triNormal;
                            foundTriangleHit = true;
                        }
                    }
                }
            }

            Vector3 triCenter = (v0 + v1 + v2) / 3.0f;
            float dSq = Vector3.DistanceSquared(localHitPos, triCenter);
            if (dSq < closestFallbackDistanceSq)
            {
                closestFallbackDistanceSq = dSq;
                closestFallbackNormal = triNormal;
            }
        }

        return foundTriangleHit ? closestHitNormal : closestFallbackNormal;
    }

    public void Dispose()
    {
        ClearCurrentMap();
    }
}

public struct SceneRaycastHit
{
    public bool HasHit;
    public Vector3 Position;
    public Vector3 Normal;
    public float Distance;
    public BodyID BodyID;
    public RigidBody? RigidBody;
}


