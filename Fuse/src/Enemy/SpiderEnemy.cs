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

public sealed class SpiderEnemy : IEnemy
{
    public string Id { get; }
    public Entity Entity { get; private set; } = null!;
    public RigidBody Body { get; private set; } = new();
    public float Health { get; set; }
    public float MaxHealth { get; }
    public bool IsDead => Health <= 0f;

    private readonly AssetManager _assets;
    private Scene.SceneManager? _sceneManager;
    private bool _initialized;
    private bool _hasDied;
    private Animation.Animator? _animator;
    private EnemyPatrol? _patrol;
    private SkinnedModel? _model;
    private ProceduralSpiderWalk? _proceduralWalk;
    private float _wallLeanAngle;

    private const float WallProbeDistance = 2.5f;
    private const float WallLeanStartDistance = 2.0f;
    private const float WallLeanFullDistance = 0.55f;
    private const float MaxWallLeanRadians = 55f * (MathF.PI / 180f);
    private const float WallLeanResponse = 10f;

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

    public EnemyPatrol? Patrol => _patrol;

    public SpiderEnemy(string id, float maxHealth = 100f, AssetManager? assets = null)
    {
        Id = id;
        MaxHealth = maxHealth;
        Health = maxHealth;
        _assets = assets!;
    }

    public void Initialize(PhysicsWorld physics, Scene.SceneManager sceneManager, Vector3 spawnPos)
    {
        if (_initialized) return;

        _sceneManager = sceneManager;

        // Physics — spider is flatter and wider than humanoid
        Body.SetCapsule(0.6f, 0.8f)
            .SetPosition(spawnPos)
            .SetMass(30f)
            .SetFriction(0.5f)
            .SetRestitution(0.1f)
            .SetAllowedDOFs(AllowedDOFs.TranslationX | AllowedDOFs.TranslationY | AllowedDOFs.TranslationZ)
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

            _proceduralWalk = new ProceduralSpiderWalk(_sceneManager);
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
        _patrol = new EnemyPatrol(this, physics);
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

        _patrol?.Update(dt);

        float speed = _patrol?.CurrentSpeed ?? 0f;
        if (_proceduralWalk != null && Entity != null)
        {
            // The entity transform is synchronized from physics during rendering,
            // after enemies update. Read the body directly so IK uses this frame's
            // real position and rotation instead of the previous frame's values.
            Vector3 bodyPosition = Body.Position(physics) + Entity.ModelOffset;
            Quaternion bodyRotation = Body.Rotation(physics);
            Vector3 forward = Vector3.Transform(Vector3.UnitZ, bodyRotation);
            Vector3 totalModelScale = Entity.Transform.Scale * Entity.ModelScale;
            UpdateWallLean(dt, bodyPosition, forward);
            Quaternion modelWorldRotation = Quaternion.Concatenate(Entity.ModelRotation, bodyRotation);

            _proceduralWalk.Update(dt, speed, forward, bodyPosition, modelWorldRotation, totalModelScale);
        }
    }

    private void UpdateWallLean(float dt, Vector3 bodyPosition, Vector3 forward)
    {
        float targetLean = 0f;
        Vector3 probeStart = bodyPosition + Vector3.UnitY * 0.7f + forward * 0.75f;

        if (_sceneManager != null && _sceneManager.Raycast(probeStart, forward, WallProbeDistance, out var hit))
        {
            // Only vertical surfaces directly in front of the spider can cause a lean.
            bool isWall = MathF.Abs(hit.Normal.Y) < 0.25f;
            bool facesSpider = Vector3.Dot(-hit.Normal, forward) > 0.55f;
            if (isWall && facesSpider)
            {
                float closeness = System.Math.Clamp(
                    (WallLeanStartDistance - hit.Distance) / (WallLeanStartDistance - WallLeanFullDistance),
                    0f,
                    1f);
                targetLean = MaxWallLeanRadians * closeness;
            }
        }

        float blend = 1f - MathF.Exp(-WallLeanResponse * dt);
        _wallLeanAngle += (targetLean - _wallLeanAngle) * blend;

        // A negative local X rotation raises the front (+Z) of the model toward
        // the wall. This remains visual-only; physics continues to use its yaw.
        Entity.ModelRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, -_wallLeanAngle);
    }

    public void OnDeath(PhysicsWorld physics, Scene.SceneManager? sceneManager = null)
    {
        if (_hasDied) return;
        _hasDied = true;

        Logger.Info($"[SpiderEnemy] {Id} died!");
        Entity.Visible = false;

        if (Body.IsBuilt)
        {
            physics.DestroyBody(Body.Native);
            Body.Destroy();
        }

        sceneManager?.ActiveScene.Remove(Entity);
    }

    public void Dispose()
    {
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
