using System.Numerics;
using JoltPhysicsSharp;
using Fuse.Core;

namespace Fuse.Physics;

public class PhysicsWorld : IDisposable
{
    private static readonly JoltFoundation s_jolt;
    private readonly PhysicsSystem _system;
    private readonly JobSystemThreadPool _jobSystem;
    private readonly Vector3 _gravity;

    // Keep filter references alive to prevent GC
    private readonly BroadPhaseLayerInterfaceTable _bpLayerInterface;
    private readonly ObjectLayerPairFilterTable _objectLayerFilter;
    private readonly ObjectVsBroadPhaseLayerFilterTable _objectVsBPFilter;

    static PhysicsWorld()
    {
        s_jolt = new JoltFoundation();
    }

    public PhysicsWorld(Vector3 gravity)
    {
        _gravity = gravity;

        _bpLayerInterface = new BroadPhaseLayerInterfaceTable(1, 1);
        _objectLayerFilter = new ObjectLayerPairFilterTable(1);
        _objectLayerFilter.EnableCollision(0, 0);
        _objectVsBPFilter = new ObjectVsBroadPhaseLayerFilterTable(
            _bpLayerInterface, 1, _objectLayerFilter, 1);

        var settings = new PhysicsSystemSettings
        {
            MaxBodies = 1024,
            NumBodyMutexes = 0,
            MaxBodyPairs = 1024,
            MaxContactConstraints = 1024,
            BroadPhaseLayerInterface = _bpLayerInterface,
            ObjectLayerPairFilter = _objectLayerFilter,
            ObjectVsBroadPhaseLayerFilter = _objectVsBPFilter,
        };

        _system = new PhysicsSystem(settings);
        _system.Gravity = gravity;

        var jobConfig = new JobSystemThreadPoolConfig { numThreads = 1 };
        _jobSystem = new JobSystemThreadPool(in jobConfig);
    }

    public void Dispose()
    {
        _jobSystem.Dispose();
        _system.Dispose();
    }

    public PhysicsSystem Native => _system;
    public BodyInterface BodyInterface => _system.BodyInterface;
    public BodyLockInterface BodyLockInterface => _system.BodyLockInterface;

    public void Step(float deltaTime)
    {
        _system.Update(deltaTime, 1, _jobSystem);
    }

    public void AddBody(BodyID id)
    {
        _system.BodyInterface.AddBody(id, Activation.Activate);
    }

    public void RemoveBody(BodyID id)
    {
        _system.BodyInterface.RemoveBody(id);
    }

    public void DestroyBody(BodyID id)
    {
        var bi = _system.BodyInterface;
        if (bi.IsAdded(id))
            bi.RemoveBody(id);
        bi.DestroyBody(id);
    }

    public BodyID CreateAndAddBody(BodyCreationSettings settings)
    {
        return _system.BodyInterface.CreateAndAddBody(settings, Activation.Activate);
    }

    public Vector3 GetBodyPosition(BodyID id)
    {
        return _system.BodyInterface.GetPosition(id);
    }

    public Quaternion GetBodyRotation(BodyID id)
    {
        return _system.BodyInterface.GetRotation(id);
    }

    public void SetBodyPosition(BodyID id, Vector3 pos)
    {
        _system.BodyInterface.SetPosition(id, pos, Activation.Activate);
    }

    public void SetBodyPositionAndRotation(BodyID id, Vector3 pos, Quaternion rot)
    {
        _system.BodyInterface.SetPositionAndRotation(id, pos, rot, Activation.Activate);
    }

    public NarrowPhaseQuery NarrowPhaseQuery => _system.NarrowPhaseQuery;
    public ObjectLayer ObjectLayer { get; set; }
    public Vector3 Gravity => _gravity;

    private sealed class JoltFoundation : IDisposable
    {
        public JoltFoundation()
        {
            Foundation.SetTraceHandler(msg =>
                Logger.Info($"[Jolt] {msg}"));
            Foundation.SetAssertFailureHandler((expr, msg, file, line) =>
            {
                Logger.Error($"[Jolt Assert] {expr} {msg} {file}:{line}");
                return false;
            });
            Foundation.Init(doublePrecision: false);
            Logger.Important("Jolt Physics Init");
        }

        public void Dispose()
        {
            Foundation.Shutdown();
            Logger.Info("Jolt Physics shutdown");
        }
    }
}
