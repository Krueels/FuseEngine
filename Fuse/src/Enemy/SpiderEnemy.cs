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
    private enum DeathState
    {
        Alive,
        Ragdoll,
        Cleanup
    }
    
    public string Id { get; }
    public Entity Entity { get; private set; } = null!;
    public RigidBody Body { get; private set; } = new();
    public CharacterVirtual? Character => _surfaceMotor?.Character;
    public float Health { get; set; }
    public float MaxHealth { get; }

    public bool IsDead => Health <= 0f;

    public bool CanBeRemoved => _deathState == DeathState.Cleanup;

    public bool IsDeathRagdollActive => _deathState == DeathState.Ragdoll;

    // Mostra as cápsulas e pivôs do ragdoll sobre a pose viva para permitir
    // validar tamanho e alinhamento antes de testar a morte.
    public bool DebugRagdollPreviewEnabled { get; set; } = true;

    public SpiderRagdollDefinition DeathRagdollDefinition { get; } = new()
    {
        Name = "SpiderDeathRagdoll"
    };


    public SpiderSurfaceMotor? SurfaceMotor => _surfaceMotor;

    public SpiderDamageBody? DamageBody => _damageBody;

    private readonly AssetManager _assets;
    private Scene.SceneManager? _sceneManager;
    private PhysicsWorld? _physics;
    private bool _initialized;
    private bool _hasDied;

    private DeathState _deathState = DeathState.Alive;
    private float _deathRagdollElapsed;

    private SpiderDeathBody? _deathBody;
    private SpiderDamageBody? _damageBody;

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
    private Vector3 _visualSurfaceUp;

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

            // EDITAR AQUI — construir as partes físicas do ragdoll depois de resolver os ossos.
            BuildDeathRagdollDefinition();

            _surfaceSolver = new SpiderSurfaceSolver(_sceneManager);

            // Hitboxes articuladas seguem a pose viva, mas são sensores
            // separados da cápsula usada pelo CharacterVirtual.
            _damageBody = new SpiderDamageBody(
                physics,
                _model.Skeleton,
                Body.Position(physics) + Entity.ModelOffset,
                Body.Rotation(physics),
                Entity.Transform.Scale * Entity.ModelScale,
                DeathRagdollDefinition);

            // Os probes de chão/parede nunca devem enxergar as próprias
            // hitboxes como uma superfície navegável.
            _surfaceSolver.SetIgnoredBodies(_damageBody.BodyIds);

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

    // EDITAR AQUI — gerar a definição física do ragdoll com base nos ossos encontrados.
    private void BuildDeathRagdollDefinition()
    {
        if (_model == null)
            return;

        Fuse.Animation.Skeleton skeleton = _model.Skeleton;

        if (skeleton.Nodes.Length == 0)
            return;

        DeathRagdollDefinition.Parts.Clear();
        DeathRagdollDefinition.Joints.Clear();

        // A definição física parte da pose de repouso real do arquivo. Assim,
        // tamanho, centro e orientação das cápsulas acompanham o modelo,
        // inclusive quando Entity.ModelScale é diferente de 1.
        skeleton.ComputeGlobalTransforms();

        Vector3 totalModelScale =
            Entity.Transform.Scale * Entity.ModelScale;

        totalModelScale = new Vector3(
            MathF.Max(MathF.Abs(totalModelScale.X), 0.0001f),
            MathF.Max(MathF.Abs(totalModelScale.Y), 0.0001f),
            MathF.Max(MathF.Abs(totalModelScale.Z), 0.0001f));

        int rootNodeIndex = -1;

        for (int i = 0; i < skeleton.Nodes.Length; i++)
        {
            if (skeleton.Nodes[i].Parent < 0)
            {
                rootNodeIndex = i;
                break;
            }
        }

        if (rootNodeIndex < 0)
            return;

        bool IsAncestorOf(int ancestor, int nodeIndex)
        {
            int current = nodeIndex;

            while ((uint)current < (uint)skeleton.Nodes.Length)
            {
                if (current == ancestor)
                    return true;

                current = skeleton.Nodes[current].Parent;
            }

            return false;
        }

        // O primeiro nó do FBX costuma ser apenas um contêiner de importação.
        // O ancestral comum mais próximo das coxas representa melhor o torso.
        var validThighNodes = new List<int>();

        for (int i = 0; i < _legs.Length; i++)
        {
            int thighNode = _legs[i].ThighNodeIndex;

            if ((uint)thighNode < (uint)skeleton.Nodes.Length)
                validThighNodes.Add(thighNode);
        }

        int bodyNodeIndex = rootNodeIndex;

        if (validThighNodes.Count > 0)
        {
            int candidate = validThighNodes[0];

            while ((uint)candidate < (uint)skeleton.Nodes.Length)
            {
                bool commonToAll = true;

                for (int i = 1; i < validThighNodes.Count; i++)
                {
                    if (!IsAncestorOf(candidate, validThighNodes[i]))
                    {
                        commonToAll = false;
                        break;
                    }
                }

                if (commonToAll)
                {
                    bodyNodeIndex = candidate;
                    break;
                }

                candidate = skeleton.Nodes[candidate].Parent;
            }
        }

        var nodeToPart = new Dictionary<int, string>();
        var ragdollParentByPartId = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase);

        int FindNodeByName(string name)
        {
            for (int i = 0; i < skeleton.Nodes.Length; i++)
            {
                if (string.Equals(
                        skeleton.Nodes[i].Name,
                        name,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }

            return -1;
        }

        void AddUniqueNode(List<int> chain, int nodeIndex)
        {
            if ((uint)nodeIndex >= (uint)skeleton.Nodes.Length ||
                chain.Contains(nodeIndex))
            {
                return;
            }

            chain.Add(nodeIndex);
        }

        bool TryGetNodePose(
            int nodeIndex,
            out Vector3 position,
            out Quaternion rotation)
        {
            position = Vector3.Zero;
            rotation = Quaternion.Identity;

            if ((uint)nodeIndex >= (uint)skeleton.Nodes.Length)
                return false;

            Matrix4x4 standard = Matrix4x4.Transpose(
                skeleton.Nodes[nodeIndex].Global);

            return Matrix4x4.Decompose(
                       standard,
                       out _,
                       out rotation,
                       out position) &&
                   float.IsFinite(position.X) &&
                   float.IsFinite(position.Y) &&
                   float.IsFinite(position.Z) &&
                   float.IsFinite(rotation.X) &&
                   float.IsFinite(rotation.Y) &&
                   float.IsFinite(rotation.Z) &&
                   float.IsFinite(rotation.W);
        }

        float GetWorldLength(Vector3 modelVector) =>
            (modelVector * totalModelScale).Length();

        void AddBodyPart()
        {
            float radius = MathF.Max(
                0.05f,
                DeathRagdollDefinition.RootRadius);

            if (TryGetNodePose(
                    bodyNodeIndex,
                    out Vector3 bodyPosition,
                    out _))
            {
                float hipExtent = 0f;

                foreach (int thighNode in validThighNodes)
                {
                    if (TryGetNodePose(
                            thighNode,
                            out Vector3 thighPosition,
                            out _))
                    {
                        hipExtent = MathF.Max(
                            hipExtent,
                            GetWorldLength(thighPosition - bodyPosition));
                    }
                }

                if (hipExtent > 0.01f)
                {
                    radius = System.Math.Clamp(
                        hipExtent * 0.72f,
                        0.35f,
                        1.1f);
                }
            }

            nodeToPart.Add(bodyNodeIndex, "Body");

            DeathRagdollDefinition.Parts.Add(
                new SpiderRagdollPartDefinition
                {
                    Id = "Body",
                    BoneName = skeleton.Nodes[bodyNodeIndex].Name,
                    ShapeType = SpiderRagdollShapeType.Capsule,
                    Radius = radius,
                    Height = MathF.Max(0.08f, radius * 0.45f),
                    Mass = MathF.Max(
                        0.01f,
                        DeathRagdollDefinition.RootMass),
                    LocalOffset = Vector3.Zero,
                    LocalRotation = Quaternion.Identity,
                    CollidesWithWorld = true,
                    CollidesWithOtherParts = false
                });
        }

        void AddSegmentPart(
            string id,
            int nodeIndex,
            int endNodeIndex,
            float radiusRatio,
            float minimumRadius,
            float maximumRadius,
            float mass)
        {
            if ((uint)nodeIndex >= (uint)skeleton.Nodes.Length ||
                (uint)endNodeIndex >= (uint)skeleton.Nodes.Length)
                return;

            if (!nodeToPart.TryAdd(nodeIndex, id))
                return;

            if (!TryGetNodePose(
                    nodeIndex,
                    out Vector3 startPosition,
                    out Quaternion boneRotation) ||
                !TryGetNodePose(
                    endNodeIndex,
                    out Vector3 endPosition,
                    out _))
            {
                nodeToPart.Remove(nodeIndex);
                return;
            }

            Vector3 modelSegment = endPosition - startPosition;
            float segmentLength = GetWorldLength(modelSegment);

            if (!float.IsFinite(segmentLength) || segmentLength <= 0.001f)
            {
                nodeToPart.Remove(nodeIndex);
                return;
            }

            Quaternion inverseBoneRotation =
                Quaternion.Inverse(Quaternion.Normalize(boneRotation));

            Vector3 localSegment = Vector3.Transform(
                modelSegment,
                inverseBoneRotation);

            Vector3 localDirection = NormalizeOrZero(localSegment);

            if (localDirection.LengthSquared() <= 0.0001f)
            {
                nodeToPart.Remove(nodeIndex);
                return;
            }

            float radius = System.Math.Clamp(
                segmentLength * radiusRatio,
                minimumRadius,
                maximumRadius);

            // Jolt recebe a distância entre os centros das duas semiesferas,
            // portanto o comprimento total é Height + 2 * Radius.
            float capsuleHeight = MathF.Max(
                0.02f,
                segmentLength - radius * 2f);

            DeathRagdollDefinition.Parts.Add(
                new SpiderRagdollPartDefinition
                {
                    Id = id,
                    BoneName = skeleton.Nodes[nodeIndex].Name,
                    ShapeType = SpiderRagdollShapeType.Capsule,
                    Radius = radius,
                    Height = capsuleHeight,
                    Mass = mass,
                    LocalOffset = localSegment * 0.5f,
                    LocalRotation = RotationBetween(
                        Vector3.UnitY,
                        localDirection),
                    CollidesWithWorld = true,
                    CollidesWithOtherParts = false
                });
        }

        AddBodyPart();

        void AddChainSegment(
            string id,
            int startNodeIndex,
            int endNodeIndex,
            float radiusRatio,
            float minimumRadius,
            float maximumRadius,
            float mass,
            ref string previousPartId)
        {
            int partsBefore = DeathRagdollDefinition.Parts.Count;

            AddSegmentPart(
                id,
                startNodeIndex,
                endNodeIndex,
                radiusRatio,
                minimumRadius,
                maximumRadius,
                mass);

            // AddSegmentPart pode rejeitar nós inválidos ou segmentos de
            // comprimento zero. Só criamos a junta se a cápsula realmente
            // tiver sido adicionada.
            if (DeathRagdollDefinition.Parts.Count <= partsBefore ||
                !nodeToPart.TryGetValue(
                    startNodeIndex,
                    out string? actualPartId))
            {
                return;
            }

            ragdollParentByPartId[actualPartId] = previousPartId;
            previousPartId = actualPartId;
        }

        int tailNodeIndex = FindNodeByName("tail");
        int tailEndNodeIndex = FindNodeByName("tail_end");

        if ((uint)tailNodeIndex < (uint)skeleton.Nodes.Length &&
            (uint)tailEndNodeIndex < (uint)skeleton.Nodes.Length)
        {
            string previousPartId = "Body";

            AddChainSegment(
                "Tail.tail",
                tailNodeIndex,
                tailEndNodeIndex,
                0.40f,
                0.20f,
                0.55f,
                0.16f,
                ref previousPartId);

            Logger.Info(
                $"[SpiderEnemy] Ragdoll tail: " +
                $"{skeleton.Nodes[tailNodeIndex].Name} -> " +
                skeleton.Nodes[tailEndNodeIndex].Name);
        }

        for (int leg = 0; leg < _legs.Length; leg++)
        {
            LegData data = _legs[leg];
            string thighName = LegNames[leg];
            int separator = thighName.LastIndexOf('.');
            string suffix = separator >= 0
                ? thighName[separator..]
                : string.Empty;
            string side = thighName.StartsWith(
                "L.",
                StringComparison.OrdinalIgnoreCase)
                ? "L."
                : "R.";

            // A cadeia de locomoção usa apenas thigh/Leg/foot/toes. O
            // esqueleto, porém, também possui ossos intermediários de twist.
            // Eles precisam ser nós físicos explícitos mesmo quando são
            // irmãos na hierarquia importada, pois representam uma dobra
            // visível da pata.
            var chain = new List<int>(6);
            AddUniqueNode(chain, data.ThighNodeIndex);
            AddUniqueNode(
                chain,
                FindNodeByName($"{side}thigh.twist{suffix}"));
            AddUniqueNode(
                chain,
                data.SegmentNodeIndices[0]);
            AddUniqueNode(
                chain,
                FindNodeByName($"{side}Leg.twist{suffix}"));
            AddUniqueNode(
                chain,
                data.SegmentNodeIndices[1]);
            AddUniqueNode(
                chain,
                data.SegmentNodeIndices[2]);

            if (chain.Count < 2)
                continue;

            Logger.Info(
                $"[SpiderEnemy] Ragdoll leg {leg}: " +
                string.Join(
                    " -> ",
                    chain.ConvertAll(node => skeleton.Nodes[node].Name)));

            string previousPartId = "Body";
            int segmentCount = chain.Count - 1;

            for (int segment = 0; segment < segmentCount; segment++)
            {
                float t = segmentCount > 1
                    ? segment / (float)(segmentCount - 1)
                    : 0f;

                float radiusRatio = 0.09f + (0.065f - 0.09f) * t;
                float minimumRadius = 0.07f + (0.045f - 0.07f) * t;
                float maximumRadius = 0.22f + (0.15f - 0.22f) * t;
                float mass = MathF.Max(
                    0.025f,
                    0.36f / segmentCount);

                int startNodeIndex = chain[segment];
                int endNodeIndex = chain[segment + 1];
                string boneName = skeleton.Nodes[startNodeIndex].Name;

                AddChainSegment(
                    $"Leg{leg}.Segment{segment}.{boneName}",
                    startNodeIndex,
                    endNodeIndex,
                    radiusRatio,
                    minimumRadius,
                    maximumRadius,
                    mass,
                    ref previousPartId);
            }
        }

        foreach (KeyValuePair<string, string> entry
                 in ragdollParentByPartId)
        {
            if (string.Equals(
                    entry.Key,
                    entry.Value,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            DeathRagdollDefinition.Joints.Add(
                new SpiderRagdollJointDefinition
                {
                    Id = $"{entry.Value}->{entry.Key}",
                    ParentPartId = entry.Value,
                    ChildPartId = entry.Key,
                    ParentAnchor = Vector3.Zero,
                    ChildAnchor = Vector3.Zero,
                    TwistMinRadians = -0.75f,
                    TwistMaxRadians = 0.75f,
                    SwingLimitRadians = 0.9f,
                    DisableCollision = true
                });
        }

        DeathRagdollDefinition.Validate();

        Logger.Info(
            $"[SpiderEnemy] Death ragdoll definition: " +
            $"parts={DeathRagdollDefinition.Parts.Count}, " +
            $"joints={DeathRagdollDefinition.Joints.Count}");
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
        if (!_initialized) return;

        if (_deathState == DeathState.Ragdoll)
        {
            _deathBody?.UpdateEntity(Entity);

            // EDITAR AQUI — copiar as poses físicas para o esqueleto neste frame.
            if (_deathBody != null &&
                _model != null &&
                _animator != null)
            {
                _deathBody.SyncSkeleton(
                    _model.Skeleton,
                    Entity,
                    _animator.FinalBoneMatrices);
            }
            
            _deathRagdollElapsed += MathF.Max(0f, dt);

            float lifeTime = MathF.Max(
                0.1f,
                DeathRagdollDefinition.LifetimeSeconds);

            if (_deathRagdollElapsed >= lifeTime)
                _deathState = DeathState.Cleanup;
            return;
        }

        if (_deathState == DeathState.Cleanup || IsDead)
            return;

        _spiderPatrol?.Update(dt);

        float speed = _spiderPatrol?.CurrentSpeed ?? 0f;
        if (_proceduralWalk != null && Entity != null)
        {
            Vector3 bodyPosition = Body.Position(physics) + Entity.ModelOffset;
            Quaternion bodyRotation = Body.Rotation(physics);
            Vector3 forward = Vector3.Transform(Vector3.UnitZ, bodyRotation);
            Vector3 movementForward = _surfaceMotor?.MovementDirection ?? forward;
            Vector3 totalModelScale = Entity.Transform.Scale * Entity.ModelScale;

            // Only the visual model is adjusted here. The physical body,
            // motor and leg solver keep their existing responsibilities.
            UpdateSurfaceOrientation(
                dt,
                bodyRotation,
                bodyPosition,
                movementForward,
                _spiderPatrol?.CurrentVelocity ?? Vector3.Zero,
                _spiderPatrol?.SurfaceNormal ?? Vector3.Zero);
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

        if (_damageBody != null &&
            _model != null &&
            Body.IsBuilt)
        {
            Vector3 proxyOrigin =
                Body.Position(physics) + Entity.ModelOffset;

            Quaternion proxyRotation =
                Quaternion.Concatenate(
                    Entity.ModelRotation,
                    Body.Rotation(physics));

            _damageBody.SyncFromSkeleton(
                _model.Skeleton,
                proxyOrigin,
                proxyRotation,
                Entity.Transform.Scale * Entity.ModelScale);
        }
    }

    private void UpdateSurfaceOrientation(
        float dt,
        Quaternion bodyRotation,
        Vector3 bodyPosition,
        Vector3 bodyForward,
        Vector3 bodyVelocity,
        Vector3 surfaceNormal)
    {
        surfaceNormal = NormalizeOrZero(surfaceNormal);
        if (surfaceNormal.LengthSquared() <= 0.0001f)
        {
            _visualSurfaceUp = Vector3.Zero;
            float resetBlend = 1f - MathF.Exp(-SurfaceLeanResponse * dt);
            Entity.ModelRotation = Quaternion.Normalize(
                Quaternion.Slerp(Entity.ModelRotation, Quaternion.Identity, resetBlend));
            return;
        }

        bodyForward = NormalizeOrZero(bodyForward);
        Vector3 surfaceForward = NormalizeOrZero(ProjectOnPlane(bodyForward, surfaceNormal));
        if (surfaceForward.LengthSquared() <= 0.0001f)
            surfaceForward = NormalizeOrZero(ProjectOnPlane(bodyVelocity, surfaceNormal));
        if (surfaceForward.LengthSquared() <= 0.0001f)
            surfaceForward = BuildTangent(surfaceNormal, bodyForward);

        Vector3 targetWorldUp = surfaceNormal;
        if (TryFindNearbyObstacle(
                bodyPosition,
                surfaceForward,
                surfaceNormal,
                out SceneRaycastHit obstacle,
                out Vector3 probeDirection))
        {
            Vector3 awayFromObstacle = NormalizeOrZero(ProjectOnPlane(obstacle.Normal, surfaceNormal));
            if (awayFromObstacle.LengthSquared() <= 0.0001f)
                awayFromObstacle = NormalizeOrZero(ProjectOnPlane(-probeDirection, surfaceNormal));

            if (awayFromObstacle.LengthSquared() > 0.0001f)
            {
                float range = MathF.Max(0.01f, SurfaceLeanStartDistance - SurfaceLeanFullDistance);
                float proximity = System.Math.Clamp(
                    (SurfaceLeanStartDistance - obstacle.Distance) / range,
                    0f,
                    1f);
                proximity = proximity * proximity * (3f - 2f * proximity);

                float angle = MaxSurfaceLeanRadians * proximity;
                targetWorldUp = NormalizeOrZero(
                    surfaceNormal * MathF.Cos(angle) + awayFromObstacle * MathF.Sin(angle));
            }
        }

        // The physical motor already smooths the real surface normal, but the
        // visual lean also receives nearby-obstacle raycast results. At a
        // corner those rays can alternate between the two faces by one frame.
        // Keep a filtered visual up vector so that this presentation layer does
        // not turn a temporary probe difference into a visible rotation flip.
        targetWorldUp = NormalizeOrZero(targetWorldUp);
        if (targetWorldUp.LengthSquared() <= 0.0001f)
            targetWorldUp = surfaceNormal;

        if (_visualSurfaceUp.LengthSquared() <= 0.0001f)
        {
            _visualSurfaceUp = surfaceNormal;
        }
        else if (Vector3.Dot(_visualSurfaceUp, targetWorldUp) < -0.95f)
        {
            // Opposite normals cannot be interpolated through a useful
            // direction. The locomotion motor must already have validated this
            // transition, so reset the visual anchor to the new valid frame.
            _visualSurfaceUp = targetWorldUp;
        }
        else
        {
            float visualBlend = 1f - MathF.Exp(-SurfaceLeanResponse * dt);
            _visualSurfaceUp = NormalizeOrZero(Vector3.Lerp(
                _visualSurfaceUp,
                targetWorldUp,
                visualBlend));
        }

        targetWorldUp = _visualSurfaceUp;

        // Re-anchor visual forward to locomotion every frame. Reusing the
        // previous visual rotation here accumulates yaw while the body leans.
        Vector3 targetWorldForward = NormalizeOrZero(ProjectOnPlane(surfaceForward, targetWorldUp));
        if (targetWorldForward.LengthSquared() <= 0.0001f)
            targetWorldForward = NormalizeOrZero(ProjectOnPlane(bodyForward, targetWorldUp));
        if (targetWorldForward.LengthSquared() <= 0.0001f)
            targetWorldForward = BuildTangent(targetWorldUp, surfaceForward);

        Quaternion inverseBodyRotation = Quaternion.Inverse(bodyRotation);
        Vector3 targetLocalUp = Vector3.Transform(targetWorldUp, inverseBodyRotation);
        Vector3 targetLocalForward = Vector3.Transform(targetWorldForward, inverseBodyRotation);
        Quaternion targetRotation = BuildSurfaceRotation(targetLocalUp, targetLocalForward);

        float blend = 1f - MathF.Exp(-SurfaceLeanResponse * dt);
        Entity.ModelRotation = Quaternion.Normalize(Quaternion.Slerp(Entity.ModelRotation, targetRotation, blend));
    }

    private bool TryFindNearbyObstacle(
        Vector3 origin,
        Vector3 surfaceForward,
        Vector3 surfaceNormal,
        out SceneRaycastHit bestHit,
        out Vector3 bestDirection)
    {
        bestHit = default;
        bestDirection = Vector3.Zero;

        if (_sceneManager == null)
            return false;

        Vector3 surfaceRight = NormalizeOrZero(Vector3.Cross(surfaceForward, surfaceNormal));
        if (surfaceRight.LengthSquared() <= 0.0001f)
            return false;

        float bestDistance = float.MaxValue;
        TryProbeObstacle(origin, surfaceForward, ref bestHit, ref bestDirection, ref bestDistance);
        TryProbeObstacle(origin, -surfaceForward, ref bestHit, ref bestDirection, ref bestDistance);
        TryProbeObstacle(origin, surfaceRight, ref bestHit, ref bestDirection, ref bestDistance);
        TryProbeObstacle(origin, -surfaceRight, ref bestHit, ref bestDirection, ref bestDistance);

        return bestDirection.LengthSquared() > 0.0001f;
    }

    private void TryProbeObstacle(
        Vector3 origin,
        Vector3 direction,
        ref SceneRaycastHit bestHit,
        ref Vector3 bestDirection,
        ref float bestDistance)
    {
        if (_sceneManager == null ||
            !_sceneManager.Raycast(
                origin,
                direction,
                SurfaceProbeDistance,
                out SceneRaycastHit hit,
                Body.Native,
                collideWithBackFaces: true))
        {
            return;
        }

        Vector3 normal = NormalizeOrZero(hit.Normal);
        if (normal.LengthSquared() <= 0.0001f ||
            !float.IsFinite(hit.Distance) ||
            hit.Distance < 0f ||
            Vector3.Dot(normal, -direction) < 0.08f ||
            hit.Distance >= bestDistance)
        {
            return;
        }

        bestHit = hit;
        bestDirection = direction;
        bestDistance = hit.Distance;
    }

    private static Quaternion BuildSurfaceRotation(Vector3 up, Vector3 forward)
    {
        up = NormalizeOrZero(up);
        if (up.LengthSquared() <= 0.0001f)
            return Quaternion.Identity;

        forward = NormalizeOrZero(ProjectOnPlane(forward, up));
        if (forward.LengthSquared() <= 0.0001f)
            forward = BuildTangent(up, Vector3.Zero);
        if (forward.LengthSquared() <= 0.0001f)
            return Quaternion.Identity;

        Vector3 right = NormalizeOrZero(Vector3.Cross(forward, up));
        if (right.LengthSquared() <= 0.0001f)
            return Quaternion.Identity;

        forward = NormalizeOrZero(Vector3.Cross(up, right));
        if (forward.LengthSquared() <= 0.0001f)
            return Quaternion.Identity;

        // Keep the same right-handed convention used by the existing
        // surface motor: local +Y maps to up and local +Z maps to forward.
        Vector3 matrixRight = -right;
        Matrix4x4 rotation = new(
            matrixRight.X, matrixRight.Y, matrixRight.Z, 0f,
            up.X, up.Y, up.Z, 0f,
            forward.X, forward.Y, forward.Z, 0f,
            0f, 0f, 0f, 1f);
        return Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(rotation));
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

    public void OnDeath(
    PhysicsWorld physics,
    Scene.SceneManager? sceneManager = null)
    {
        if (_hasDied)
            return;

        _hasDied = true;

        // inicia o estado persistente de morte.
        _deathState = DeathState.Ragdoll;
        _deathRagdollElapsed = 0f;

        _ = sceneManager;

        Logger.Info($"[SpiderEnemy] {Id} died!");

        // manter o modelo visível.
        Entity.Visible = true;

        // impedir que o animator continue alterando o esqueleto.
        if (_animator != null)
            _animator.Playing = false;

        // Remove a inclinação visual da aranha viva.
        Entity.ModelRotation = Quaternion.Identity;

        Vector3 deathPosition = Entity.Transform.Position;
        Quaternion deathRotation = Entity.Transform.Rotation;
        Vector3 deathVelocity = Vector3.Zero;

        // capturar a pose antes de destruir o corpo vivo.
        if (Body.IsBuilt)
        {
            deathPosition = Body.Position(physics);
            deathRotation = Body.Rotation(physics);
            deathVelocity = Body.LinearVelocity(physics);
        }

        // parar CharacterVirtual e locomotion.
        _surfaceMotor?.Dispose();
        _surfaceMotor = null;

        // As hitboxes cinemáticas só existem durante a vida. Removê-las antes
        // de criar o ragdoll evita corpos duplicados no mesmo lugar.
        _damageBody?.Dispose();
        _damageBody = null;
        _surfaceSolver?.SetIgnoredBodies(null);

        // EDITAR AQUI — criar o ragdoll articulado usando a pose atual dos ossos.
        if (_model != null &&
            DeathRagdollDefinition.Parts.Count > 0)
        {
            Vector3 modelOrigin =
                deathPosition + Entity.ModelOffset;

            Vector3 modelScale =
                Entity.Transform.Scale * Entity.ModelScale;

            _deathBody = new SpiderDeathBody(
                physics,
                _model.Skeleton,
                modelOrigin,
                deathRotation,
                modelScale,
                DeathRagdollDefinition,
                deathVelocity);
        }
        else
        {
            _deathBody = new SpiderDeathBody(
                physics,
                deathPosition,
                deathRotation,
                DeathRagdollDefinition);
        }

        // O corpo antigo era apenas o proxy cinemático da locomotion.
        if (Body.IsBuilt)
        {
            physics.DestroyBody(Body.Native);
            Body.Destroy();
        }

        // IMPORTANTE:
        // Não remover Entity da cena neste ponto.
        // O EnemySystem fará isso quando CanBeRemoved for true.
    }

    public void OnDrawGizmos(Debug.DebugDrawer drawer)
    {
        if (_physics == null)
            return;

        // desenhar o corpo físico da morte.
        if (_deathState == DeathState.Ragdoll &&
            _deathBody != null &&
            _deathBody.IsBuilt)
        {
            // desenhar todas as partes e conexões do ragdoll.
            _deathBody.DrawDebug(drawer);

            return;
        }

        if (IsDead || !Body.IsBuilt)
            return;

        if (DebugRagdollPreviewEnabled &&
            _model != null &&
            DeathRagdollDefinition.Parts.Count > 0)
        {
            SpiderDeathBody.DrawDebugPreview(
                drawer,
                _model.Skeleton,
                Entity,
                Body.Position(_physics) + Entity.ModelOffset,
                Body.Rotation(_physics),
                Entity.Transform.Scale * Entity.ModelScale,
                DeathRagdollDefinition);

            return;
        }

        Vector3 pos = Body.Position(_physics);
        Quaternion rot = Body.Rotation(_physics);

        drawer.DrawCapsule(
            pos,
            rot,
            0.3f,
            0.6f,
            new Vector3(1f, 0.5f, 0f));
    }

    public void Dispose()
    {
        _surfaceMotor?.Dispose();
        _surfaceMotor = null;

        _damageBody?.Dispose();
        _damageBody = null;

        // destruir o corpo dinâmico da morte.
        _deathBody?.Dispose();
        _deathBody = null;

        if (Entity != null &&
            Entity.MeshOwnedByEntity &&
            Entity.Mesh != null)
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
