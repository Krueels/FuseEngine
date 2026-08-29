using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using Fuse.Animation;
using Fuse.AssetManagement;
using Fuse.Core;
using Fuse.Physics;
using Fuse.Renderer;
using Fuse.Scene;
using Fuse.Scene.Model;
using JoltPhysicsSharp;

namespace Fuse.Enemy;

public sealed class SpiderEnemy : IEnemy, Debug.IGizmoDrawable
{
    public string Id { get; }
    public Entity Entity { get; private set; } = null!;
    public RigidBody Body { get; private set; } = new();
    public CharacterVirtual? Character => _surfaceMotor?.Character;
    public float Health { get; set; }
    public float MaxHealth { get; }
    public bool IsDead => Health <= 0f;
    public SpiderSurfaceMotor? SurfaceMotor => _surfaceMotor;

    private readonly AssetManager _assets;
    private Scene.SceneManager? _sceneManager;
    private PhysicsWorld? _physics;
    private bool _initialized;
    private bool _hasDied;
    private Animation.Animator? _animator;
    // SpiderPatrol owns the ground-safe route and exposes its target/velocity
    // to the leg solver and debug visualisation.
    private SpiderPatrol? _spiderPatrol;
    private SpiderSurfaceSolver? _surfaceSolver;
    private SpiderSurfaceMotor? _surfaceMotor;
    private SkinnedModel? _model;
    private ProceduralSpiderWalk? _proceduralWalk;

    private const float SurfaceProbeDistance = 2.5f;
    private const float SurfaceLeanStartDistance = 2.0f;
    private const float SurfaceLeanFullDistance = 0.55f;
    private const float MaxSurfaceLeanRadians = 72f * (MathF.PI / 180f);
    private const float SurfaceLeanResponse = 9f;

    // Leg bone indices (resolved at init)
    private readonly LegData[] _legs = new LegData[8];

    // Bone name prefixes for each leg
    private static readonly string[] LegNames = {
        "L.thigh.0",    // 0
        "L.thigh.1",    // 1
        "L.thigh.2",    // 2
        "L.thigh.3",    // 3
        "R.thigh.0",    // 4
        "R.thigh.1",    // 5
        "R.thigh.2",    // 6
        "R.thigh.3",    // 7
    };

    public EnemyPatrol? Patrol => null;
    public SpiderPatrol? NavigationPatrol => _spiderPatrol;

    public SpiderEnemy(string id, float maxHealth = 100f, AssetManager? assets = null)
    {
        Id = id;
        MaxHealth = maxHealth;
        Health = maxHealth;
        _assets = assets!;
    }

    public void Initialize(PhysicsWorld physics, Scene.SceneManager sceneManager, Vector3 spawnPos) =>
        Initialize(physics, sceneManager, spawnPos, null);

    public void Initialize(
        PhysicsWorld physics,
        Scene.SceneManager sceneManager,
        Vector3 spawnPos,
        Vector3? spawnNormal)
    {
        if (_initialized) return;

        _sceneManager = sceneManager;
        _physics = physics;

        Vector3 initialNormal = NormalizeOrZero(spawnNormal ?? Vector3.Zero);
        Vector3 safeSpawn = spawnPos;
        const float initialClearance = 0.83f;
        if (initialNormal.LengthSquared() > 0.0001f)
        {
            // Spawn outside the selected collider. Starting exactly on a face
            // makes rays beginning at fraction zero return ambiguous normals.
            safeSpawn = spawnPos + initialNormal * initialClearance;
        }
        else if (TryFindInitialSurface(sceneManager, spawnPos, out SceneRaycastHit spawnHit))
        {
            initialNormal = NormalizeOrZero(spawnHit.Normal);
            safeSpawn = spawnHit.Position + initialNormal * initialClearance;
        }

        if (initialNormal.LengthSquared() <= 0.0001f)
            throw new InvalidOperationException($"Spider '{Id}' could not find a valid spawn surface.");

        Vector3 initialForward = BuildTangent(initialNormal, Vector3.Zero);
        if (initialForward.LengthSquared() <= 0.0001f)
            throw new InvalidOperationException($"Spider '{Id}' could not build a tangent spawn direction.");

        // This kinematic body remains the render/hit proxy. Physical movement
        // is solved by SpiderSurfaceMotor's CharacterVirtual and copied here.
        Body.SetCapsule(0.6f, 0.3f)
            .SetPosition(safeSpawn)
            .SetKinematic(true)
            .SetFriction(0.5f)
            .SetRestitution(0.1f)
            .SetAllowedDOFs(AllowedDOFs.All)
            .Build(physics);


        _model = _assets.GetSkinnedModel(Bible.Model(Bible.SpiderModel));

        if (_model != null)
        {
            // 1. Cria o Animator e vincula ao SkinnedModel para alocar os arrays de matrizes
            _animator = new Animation.Animator(_model.Skeleton);
            _model.Link(_animator);

            Entity = sceneManager.ActiveScene.Add(null, Id, Body);
            Entity.SkinnedModel = _model;
            Entity.Animator = _animator; // Permite que a GPU leia as matrizes atualizadas
            Entity.Visible = true;
            Entity.ModelOffset = new Vector3(0f, 0f, 0f);
            Entity.ModelScale = new Vector3(10f, 10f, 10f);

            LogBoneNames();
            ResolveLegBones();

            _surfaceSolver = new SpiderSurfaceSolver(_sceneManager);
            _proceduralWalk = new ProceduralSpiderWalk(_sceneManager, _surfaceSolver);
            _proceduralWalk.Initialize(_model.Skeleton, _legs);

            // 2. Conecta o buffer de matrizes do Animator ao ProceduralSpiderWalk
            _proceduralWalk.SetFinalBoneMatrices(_animator.FinalBoneMatrices);

            Logger.Info($"[SpiderEnemy] Initialized with {_legs.Length} legs resolved");
        }
        else
        {
            Logger.Warn($"[SpiderEnemy] Model not found: {Bible.Model(Bible.SpiderModel)}");
            var meshData = MeshGenerator.GenerateCapsule(0.6f, 0.8f, 12);
            var capsuleMesh = new Mesh(_assets.Gl, meshData.Vertices, meshData.Indices);
            Entity = sceneManager.ActiveScene.Add(capsuleMesh, Id, Body);
            Entity.MeshOwnedByEntity = true;
            Entity.Visible = true;
        }

        sceneManager.ActiveScene.RegisterBody(Entity);
        _surfaceSolver ??= new SpiderSurfaceSolver(sceneManager);
        _surfaceMotor = new SpiderSurfaceMotor(
            physics,
            Body,
            _surfaceSolver,
            safeSpawn,
            initialNormal,
            initialForward);
        _spiderPatrol = new SpiderPatrol(this, physics, _surfaceMotor);
        Debug.DebugDrawer.Register(this);
        _initialized = true;
    }

    private void LogBoneNames()
    {
        if (_model == null) return;

        Logger.Info($"[SpiderEnemy] === Skeleton Nodes ({_model.Skeleton.Nodes.Length}) ===");
        for (int i = 0; i < _model.Skeleton.Nodes.Length; i++)
        {
            var n = _model.Skeleton.Nodes[i];
            Logger.Info($"  [{i}] '{n.Name}' parent={n.Parent}");
        }

        Logger.Info($"[SpiderEnemy] === Bones ({_model.Skeleton.Bones.Length}) ===");
        for (int i = 0; i < _model.Skeleton.Bones.Length; i++)
        {
            var b = _model.Skeleton.Bones[i];
            Logger.Info($"  [{b.Index}] '{b.Name}' nodeIdx={b.NodeIndex}");
        }
    }

    private void ResolveLegBones()
    {
        if (_model == null) return;
        var skeleton = _model.Skeleton;

        for (int leg = 0; leg < 8; leg++)
        {
            _legs[leg] = new LegData();
            string thighName = LegNames[leg]; // Ex: "L.thigh.0"
            string suffix = thighName.Substring(thighName.LastIndexOf('.')); // Ex: ".0"
            string side = thighName.StartsWith("L.") ? "L." : "R.";

            // 1. Encontra a Thigh (Coxa)
            for (int n = 0; n < skeleton.Nodes.Length; n++)
            {
                if (skeleton.Nodes[n].Name == thighName)
                {
                    _legs[leg].ThighNodeIndex = n;
                    break;
                }
            }

            // 2. Encontra o nó Leg correspondente (ex: "L.Leg.0")
            string legSegmentName = side + "Leg" + suffix;
            int legNodeIdx = -1;
            for (int n = 0; n < skeleton.Nodes.Length; n++)
            {
                if (skeleton.Nodes[n].Name.Equals(legSegmentName, StringComparison.OrdinalIgnoreCase))
                {
                    legNodeIdx = n;
                    break;
                }
            }

            _legs[leg].SegmentNodeIndices[0] = legNodeIdx;

            // 3. A partir do nó Leg, faz a busca dos filhos reais: Leg -> Foot -> Toes
            if (legNodeIdx >= 0)
            {
                int footIdx = FindNonTwistChild(skeleton, legNodeIdx);
                _legs[leg].SegmentNodeIndices[1] = footIdx;

                if (footIdx >= 0)
                {
                    int toesIdx = FindNonTwistChild(skeleton, footIdx);
                    _legs[leg].SegmentNodeIndices[2] = toesIdx;
                }
            }

            Logger.Info($"[SpiderEnemy] Leg {leg} ({thighName}): thigh={_legs[leg].ThighNodeIndex}, " +
                $"segments=[{string.Join(",", _legs[leg].SegmentNodeIndices)}]");
        }
    }

    private static int FindNonTwistChild(Animation.Skeleton skeleton, int parentNodeIndex)
    {
        for (int n = 0; n < skeleton.Nodes.Length; n++)
        {
            if (skeleton.Nodes[n].Parent == parentNodeIndex)
            {
                string childName = skeleton.Nodes[n].Name;
                if (!childName.Contains("twist", StringComparison.OrdinalIgnoreCase))
                    return n;
            }
        }
        return -1;
    }

    public void TakeDamage(float damage, Vector3 hitPos, Vector3 hitDirection, PhysicsWorld physics)
    {
        if (IsDead) return;

        Health -= damage;
        Logger.Info($"[SpiderEnemy] {Id} took {damage} damage. Health: {Health}/{MaxHealth}");

        if (Body.IsBuilt)
        {
            Vector3 impulse = hitDirection * damage * 0.3f;
            physics.BodyInterface.AddImpulse(Body.Native, impulse);
        }
    }

    public void Update(float dt, PhysicsWorld physics)
    {
        if (IsDead || !_initialized) return;

        _spiderPatrol?.Update(dt);

        float speed = _spiderPatrol?.CurrentSpeed ?? 0f;
        if (_proceduralWalk != null && Entity != null)
        {
            Vector3 bodyPosition = Body.Position(physics) + Entity.ModelOffset;
            Quaternion bodyRotation = Body.Rotation(physics);
            Vector3 forward = Vector3.Transform(Vector3.UnitZ, bodyRotation);
            Vector3 totalModelScale = Entity.Transform.Scale * Entity.ModelScale;

            // The visual model uses the exact same surface anchor selected by
            // patrol. It must never perform a competing wall scan, otherwise
            // the body can adhere to one side while the model leans to another.
            UpdateSurfaceOrientation(dt, bodyRotation, _spiderPatrol?.SurfaceNormal ?? Vector3.Zero);
            Quaternion modelWorldRotation = Quaternion.Concatenate(Entity.ModelRotation, bodyRotation);

            Matrix4x4 modelMatrix = Matrix4x4.CreateScale(totalModelScale) *
                                     Matrix4x4.CreateFromQuaternion(modelWorldRotation) *
                                     Matrix4x4.CreateTranslation(bodyPosition);
            _proceduralWalk.Update(
                dt,
                speed,
                forward,
                _spiderPatrol?.CurrentVelocity ?? forward * speed,
                bodyPosition,
                modelWorldRotation,
                totalModelScale,
                modelMatrix,
                Body.Native,
                _spiderPatrol?.SurfaceContact ?? default);
        }
    }

    private void UpdateSurfaceOrientation(
        float dt,
        Quaternion bodyRotation,
        Vector3 surfaceNormal)
    {
        Quaternion targetRotation = Quaternion.Identity;

        surfaceNormal = NormalizeOrZero(surfaceNormal);
        if (surfaceNormal.LengthSquared() > 0.0001f)
        {
            Vector3 targetLocalUp = Vector3.Transform(
                surfaceNormal,
                Quaternion.Inverse(bodyRotation));
            targetRotation = RotationBetween(Vector3.UnitY, targetLocalUp);
        }

        float blend = 1f - MathF.Exp(-SurfaceLeanResponse * dt);
        Entity.ModelRotation = Quaternion.Normalize(Quaternion.Slerp(Entity.ModelRotation, targetRotation, blend));
    }

    private static Quaternion RotationBetween(Vector3 from, Vector3 to)
    {
        from = NormalizeOrZero(from);
        to = NormalizeOrZero(to);
        if (from.LengthSquared() <= 0.0001f || to.LengthSquared() <= 0.0001f)
            return Quaternion.Identity;

        float dot = System.Math.Clamp(Vector3.Dot(from, to), -1f, 1f);
        if (dot > 0.99999f)
            return Quaternion.Identity;
        if (dot < -0.99999f)
            return Quaternion.CreateFromAxisAngle(BuildTangent(from, Vector3.Zero), MathF.PI);
        return Quaternion.CreateFromAxisAngle(Vector3.Normalize(Vector3.Cross(from, to)), MathF.Acos(dot));
    }

    private static bool TryFindInitialSurface(
        Scene.SceneManager sceneManager,
        Vector3 position,
        out SceneRaycastHit bestHit)
    {
        Vector3[] probeDirections =
        {
            Vector3.UnitX,
            -Vector3.UnitX,
            Vector3.UnitY,
            -Vector3.UnitY,
            Vector3.UnitZ,
            -Vector3.UnitZ
        };

        bestHit = default;
        float bestDistance = float.MaxValue;
        bool found = false;
        foreach (Vector3 direction in probeDirections)
        {
            if (!sceneManager.Raycast(
                    position,
                    direction,
                    10f,
                    out SceneRaycastHit hit,
                    collideWithBackFaces: true))
            {
                continue;
            }

            Vector3 normal = NormalizeOrZero(hit.Normal);
            if (normal.LengthSquared() <= 0.0001f ||
                Vector3.Dot(normal, -direction) < 0.08f)
            {
                continue;
            }

            if (hit.Distance >= bestDistance)
                continue;

            bestDistance = hit.Distance;
            bestHit = hit;
            found = true;
        }

        return found;
    }

    private static Vector3 ProjectOnPlane(Vector3 value, Vector3 normal) =>
        value - normal * Vector3.Dot(value, normal);

    private static Vector3 BuildTangent(Vector3 normal, Vector3 desired)
    {
        normal = NormalizeOrZero(normal);
        if (normal.LengthSquared() <= 0.0001f)
            return Vector3.Zero;

        Vector3 tangent = NormalizeOrZero(ProjectOnPlane(desired, normal));
        if (tangent.LengthSquared() > 0.0001f)
            return tangent;

        Vector3 reference = Vector3.UnitX;
        float smallestAlignment = MathF.Abs(Vector3.Dot(normal, reference));
        Vector3[] candidates = { Vector3.UnitY, Vector3.UnitZ };
        foreach (Vector3 candidate in candidates)
        {
            float alignment = MathF.Abs(Vector3.Dot(normal, candidate));
            if (alignment >= smallestAlignment)
                continue;
            reference = candidate;
            smallestAlignment = alignment;
        }

        return NormalizeOrZero(Vector3.Cross(normal, reference));
    }

    private static Vector3 NormalizeOrZero(Vector3 value)
    {
        if (value.LengthSquared() > 0.0001f)
            return Vector3.Normalize(value);
        return Vector3.Zero;
    }

    public void OnDeath(PhysicsWorld physics, Scene.SceneManager? sceneManager = null)
    {
        if (_hasDied) return;
        _hasDied = true;

        Logger.Info($"[SpiderEnemy] {Id} died!");
        Entity.Visible = false;

        _surfaceMotor?.Dispose();

        if (Body.IsBuilt)
        {
            physics.DestroyBody(Body.Native);
            Body.Destroy();
        }

        sceneManager?.ActiveScene.Remove(Entity);
    }

    public void OnDrawGizmos(Debug.DebugDrawer drawer)
    {
        if (IsDead || !Body.IsBuilt || _physics == null) return;

        Vector3 pos = Body.Position(_physics);
        Quaternion rot = Body.Rotation(_physics);
        drawer.DrawCapsule(pos, rot, 0.3f, 0.6f, new Vector3(1, 0.5f, 0));
    }

    public void Dispose()
    {
        _surfaceMotor?.Dispose();

        if (Entity != null && Entity.MeshOwnedByEntity && Entity.Mesh != null)
        {
            Entity.Mesh.Dispose();
            Entity.Mesh = null;
        }
    }

    internal struct LegData
    {
        public int ThighNodeIndex;
        public int[] SegmentNodeIndices; // 3 segments: Leg, foot, toes

        public LegData()
        {
            ThighNodeIndex = -1;
            SegmentNodeIndices = new int[3];
            for (int i = 0; i < 3; i++)
                SegmentNodeIndices[i] = -1;
        }
    }
}
