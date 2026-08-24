using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using Fuse.Behaviours;
using Fuse.Core;
using Fuse.Interaction;
using Fuse.Physics;
using Fuse.Renderer;
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
            loadPath, _scene, _physics, _assets, out var spawn, Fuse.ResPath.Path, onProgress);

        if (loaded != null)
        {
            _bodies.AddRange(loaded);
        }

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
            CurrentMapPath, _scene, _physics, _assets, out var spawn, Fuse.ResPath.Path, onProgress);
            
        if (loaded != null)
        {
            _bodies.AddRange(loaded);
        }

        onProgress?.Invoke(0.85f, "Registering interactions...");
        RegisterInteractablesAndBehaviours();

        onProgress?.Invoke(1.0f, "Done!");

        _renderer?.ClearBillboardQueue();
        return spawn;
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
                                        else if (prop.PropertyType == typeof(string)) prop.SetValue(behaviour, (string)valNode);
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

    public void Dispose()
    {
        ClearCurrentMap();
    }
}

