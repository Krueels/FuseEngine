using System;
using System.Collections.Generic;
using System.Numerics;
using Fuse.Animation;
using Fuse.Core;
using Fuse.Debug;
using Fuse.Physics;
using Fuse.Renderer;
using JoltPhysicsSharp;
using AnimationSkeleton = Fuse.Animation.Skeleton;

namespace Fuse.Enemy;

public sealed class SpiderDeathBody : IDisposable
{
    private sealed class PartRuntime
    {
        public PartRuntime(
            SpiderRagdollPartDefinition definition,
            RigidBody body,
            Vector3 initialPosition,
            Quaternion initialRotation)
        {
            Definition = definition;
            Body = body;
            InitialPosition = initialPosition;
            InitialRotation = initialRotation;
        }

        public SpiderRagdollPartDefinition Definition { get; }
        public RigidBody Body { get; }
        public Vector3 InitialPosition { get; }
        public Quaternion InitialRotation { get; }
    }

    private readonly PhysicsWorld _physics;
    private readonly SpiderRagdollDefinition _definition;
    private Vector3 _modelScale = Vector3.One;

    private readonly Dictionary<string, PartRuntime> _parts =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly List<Constraint> _constraints = new();

    private GroupFilterTable? _selfCollisionFilter;
    private CollisionGroupID _collisionGroupId;
    private bool _disposed;

    private static uint s_nextCollisionGroupId = 1;

    public RigidBody Body { get; private set; } = new();

    public bool IsBuilt => Body.IsBuilt;

    public int PartCount => _parts.Count;

    public int ConstraintCount => _constraints.Count;

    public SpiderDeathBody(
        PhysicsWorld physics,
        Vector3 position,
        Quaternion rotation,
        SpiderRagdollDefinition definition)
    {
        _physics = physics;
        _definition = definition;

        BuildFallbackBody(position, rotation);
    }

    public SpiderDeathBody(
        PhysicsWorld physics,
        AnimationSkeleton skeleton,
        Vector3 modelOrigin,
        Quaternion modelRotation,
        Vector3 modelScale,
        SpiderRagdollDefinition definition,
        Vector3 inheritedVelocity = default)
    {
        _physics = physics;
        _definition = definition;
        _modelScale = SanitizeScale(modelScale);

        if (skeleton.Nodes.Length == 0 ||
            !definition.TryGetPart("Body", out _))
        {
            BuildFallbackBody(
                modelOrigin,
                modelRotation,
                inheritedVelocity);

            return;
        }

        skeleton.ComputeGlobalTransforms();

        _collisionGroupId = s_nextCollisionGroupId++;

        if (_collisionGroupId.Value == 0)
            _collisionGroupId = s_nextCollisionGroupId++;

        if (!definition.SelfCollisionEnabled)
        {
            uint subgroupCount = (uint)System.Math.Max(
                1,
                definition.Parts.Count);

            _selfCollisionFilter =
                new GroupFilterTable(subgroupCount);

            for (uint i = 0; i < subgroupCount; i++)
            {
                for (uint j = i; j < subgroupCount; j++)
                {
                    CollisionSubGroupID first = i;
                    CollisionSubGroupID second = j;

                    _selfCollisionFilter.DisableCollision(
                        first,
                        second);
                }
            }
        }

        for (int i = 0; i < definition.Parts.Count; i++)
        {
            SpiderRagdollPartDefinition part = definition.Parts[i];

            bool hasBone = skeleton.TryGetNodeIndex(
                part.BoneName,
                out int nodeIndex);

            if (!hasBone &&
                !string.Equals(
                    part.Id,
                    "Body",
                    StringComparison.OrdinalIgnoreCase))
            {
                Logger.Warn(
                    $"[SpiderDeathBody] Bone '{part.BoneName}' " +
                    $"for part '{part.Id}' was not found.");

                continue;
            }

            CalculateBonePose(
                skeleton,
                part,
                modelOrigin,
                modelRotation,
                modelScale,
                out Vector3 worldPosition,
                out Quaternion worldRotation);

            RigidBody? body = CreatePartBody(
                part,
                worldPosition,
                worldRotation,
                inheritedVelocity);

            if (body == null)
                continue;

            if (_selfCollisionFilter != null)
            {
                AssignCollisionGroup(
                    body,
                    i);
            }

            _parts.Add(
                part.Id,
                new PartRuntime(
                    part,
                    body,
                    worldPosition,
                    worldRotation));
        }

        if (!_parts.TryGetValue(
                "Body",
                out PartRuntime? rootPart))
        {
            Logger.Warn(
                "[SpiderDeathBody] Ragdoll root could not be created. " +
                "Using fallback body.");

            DisposePhysicsObjects();

            BuildFallbackBody(
                modelOrigin,
                modelRotation,
                inheritedVelocity);

            return;
        }

        Body = rootPart.Body;

        foreach (SpiderRagdollJointDefinition joint
                 in definition.Joints)
        {
            CreateJoint(joint);
        }
    }

    public Vector3 Position =>
        Body.IsBuilt
            ? Body.Position(_physics)
            : Vector3.Zero;

    public Quaternion Rotation =>
        Body.IsBuilt
            ? Body.Rotation(_physics)
            : Quaternion.Identity;

    public Vector3 LinearVelocity =>
        Body.IsBuilt
            ? Body.LinearVelocity(_physics)
            : Vector3.Zero;

    public void UpdateEntity(Entity entity)
    {
        if (!Body.IsBuilt)
            return;

        // EDITAR AQUI — manter a entidade visual acompanhando o tronco físico.
        entity.Transform.Position =
            Body.Position(_physics) - entity.ModelOffset;

        entity.Transform.Rotation =
            Body.Rotation(_physics);
    }

    public void SyncSkeleton(
        AnimationSkeleton skeleton,
        Entity entity,
        Matrix4x4[] finalBoneMatrices)
    {
        if (!Body.IsBuilt ||
            skeleton.Nodes.Length == 0 ||
            finalBoneMatrices.Length == 0)
        {
            return;
        }

        // Guardar a pose atual antes de substituir apenas os nós controlados
        // pelo ragdoll. Os demais ossos continuam na pose congelada da morte.
        skeleton.ComputeGlobalTransforms();

        int nodeCount = skeleton.Nodes.Length;
        var baseLocals = new Matrix4x4[nodeCount];
        var baseGlobals = new Matrix4x4[nodeCount];

        for (int i = 0; i < nodeCount; i++)
        {
            baseLocals[i] = skeleton.Nodes[i].Local;
            baseGlobals[i] = skeleton.Nodes[i].Global;
        }

        var physicalGlobals = new Dictionary<int, Matrix4x4>();

        foreach (PartRuntime part in _parts.Values)
        {
            if (!part.Body.IsBuilt ||
                !skeleton.TryGetNodeIndex(
                    part.Definition.BoneName,
                    out int nodeIndex))
            {
                continue;
            }

            Vector3 bodyWorldPosition =
                part.Body.Position(_physics);

            Quaternion bodyWorldRotation =
                part.Body.Rotation(_physics);

            CalculateBonePoseFromBody(
                part.Definition,
                bodyWorldPosition,
                bodyWorldRotation,
                _modelScale,
                out Vector3 worldPosition,
                out Quaternion worldRotation);

            if (!TryBuildModelGlobal(
                    worldPosition,
                    worldRotation,
                    baseGlobals[nodeIndex],
                    entity,
                    out Matrix4x4 modelGlobal))
            {
                continue;
            }

            physicalGlobals[nodeIndex] = modelGlobal;
        }

        if (physicalGlobals.Count == 0)
            return;

        var desiredGlobals = new Matrix4x4[nodeCount];
        var resolved = new bool[nodeCount];

        void ResolveDesiredGlobal(int nodeIndex)
        {
            if (resolved[nodeIndex])
                return;

            if (physicalGlobals.TryGetValue(
                    nodeIndex,
                    out Matrix4x4 physicalGlobal))
            {
                desiredGlobals[nodeIndex] = physicalGlobal;
                resolved[nodeIndex] = true;
                return;
            }

            int parentIndex = skeleton.Nodes[nodeIndex].Parent;

            if (parentIndex < 0)
            {
                desiredGlobals[nodeIndex] = baseGlobals[nodeIndex];
                resolved[nodeIndex] = true;
                return;
            }

            ResolveDesiredGlobal(parentIndex);

            desiredGlobals[nodeIndex] =
                desiredGlobals[parentIndex] *
                baseLocals[nodeIndex];

            resolved[nodeIndex] = true;
        }

        foreach (int nodeIndex in physicalGlobals.Keys)
        {
            int parentIndex = skeleton.Nodes[nodeIndex].Parent;

            // Um osso físico precisa sempre derivar seu espaço local a partir
            // da pose final do pai. Sem resolver o pai primeiro, a matriz
            // ainda pode estar no valor padrão (zero), deformando toda a
            // cadeia de ossos filha durante o skinning.
            if (parentIndex >= 0)
                ResolveDesiredGlobal(parentIndex);

            ResolveDesiredGlobal(nodeIndex);

            Matrix4x4 parentGlobal =
                parentIndex >= 0
                    ? desiredGlobals[parentIndex]
                    : Matrix4x4.Identity;

            Matrix4x4 local = desiredGlobals[nodeIndex];

            if (parentIndex >= 0)
            {
                if (!Matrix4x4.Invert(
                        parentGlobal,
                        out Matrix4x4 inverseParentGlobal))
                {
                    continue;
                }

                local = inverseParentGlobal * desiredGlobals[nodeIndex];
            }

            skeleton.Nodes[nodeIndex].Local = local;
        }

        // Animator.Update acontece antes do EnemySystem.Update. Recalcular aqui
        // garante que a pose física chega ao buffer usado pelo renderer no frame atual.
        skeleton.ComputeFinalBoneMatrices(finalBoneMatrices);
    }

    public void DrawDebug(DebugDrawer drawer)
    {
        foreach (PartRuntime part in _parts.Values)
        {
            if (!part.Body.IsBuilt)
                continue;

            Vector3 position = part.Body.Position(_physics);
            Quaternion rotation = part.Body.Rotation(_physics);

            Vector3 color =
                string.Equals(
                    part.Definition.Id,
                    "Body",
                    StringComparison.OrdinalIgnoreCase)
                    ? new Vector3(1f, 0.1f, 0.1f)
                    : new Vector3(1f, 0.55f, 0.05f);

            switch (part.Definition.ShapeType)
            {
                case SpiderRagdollShapeType.Capsule:
                    drawer.DrawCapsule(
                        position,
                        rotation,
                        MathF.Max(
                            0.01f,
                            part.Definition.Height * 0.5f),
                        MathF.Max(
                            0.01f,
                            part.Definition.Radius),
                        color);
                    break;

                case SpiderRagdollShapeType.Sphere:
                    drawer.DrawSphere(
                        position,
                        rotation,
                        MathF.Max(
                            0.01f,
                            part.Definition.Radius),
                        color);
                    break;

                case SpiderRagdollShapeType.Box:
                    drawer.DrawBox(
                        position,
                        rotation,
                        part.Definition.BoxHalfExtents,
                        color);
                    break;

                case SpiderRagdollShapeType.ConvexHull:
                    drawer.DrawSphere(
                        position,
                        rotation,
                        MathF.Max(
                            0.05f,
                            part.Definition.Radius),
                        color);
                    break;
            }
        }

        foreach (SpiderRagdollJointDefinition joint
                 in _definition.Joints)
        {
            if (!_parts.TryGetValue(
                    joint.ParentPartId,
                    out PartRuntime? parent))
            {
                continue;
            }

            if (!_parts.TryGetValue(
                    joint.ChildPartId,
                    out PartRuntime? child))
            {
                continue;
            }

            if (!parent.Body.IsBuilt || !child.Body.IsBuilt)
                continue;

            drawer.PushLine(
                parent.Body.Position(_physics),
                child.Body.Position(_physics),
            new Vector3(0.2f, 0.8f, 1f));
        }
    }

    public static void DrawDebugPreview(
        DebugDrawer drawer,
        AnimationSkeleton skeleton,
        Entity entity,
        Vector3 modelOrigin,
        Quaternion modelRotation,
        Vector3 modelScale,
        SpiderRagdollDefinition definition)
    {
        if (skeleton.Nodes.Length == 0 || definition.Parts.Count == 0)
            return;

        skeleton.ComputeGlobalTransforms();

        Vector3 safeScale = SanitizeScale(modelScale);
        var poses = new Dictionary<
            string,
            (Vector3 Position, Quaternion Rotation)>(
                StringComparer.OrdinalIgnoreCase);

        foreach (SpiderRagdollPartDefinition part in definition.Parts)
        {
            if (!skeleton.TryGetNodeIndex(
                    part.BoneName,
                    out int nodeIndex))
            {
                continue;
            }

            CalculateBonePose(
                skeleton,
                part,
                modelOrigin,
                modelRotation,
                safeScale,
                out Vector3 position,
                out Quaternion rotation);

            poses[part.Id] = (position, rotation);

            Vector3 color =
                string.Equals(
                    part.Id,
                    "Body",
                    StringComparison.OrdinalIgnoreCase)
                    ? new Vector3(0.2f, 1f, 0.2f)
                    : new Vector3(0.1f, 0.85f, 1f);

            switch (part.ShapeType)
            {
                case SpiderRagdollShapeType.Capsule:
                    drawer.DrawCapsule(
                        position,
                        rotation,
                        MathF.Max(0.01f, part.Height * 0.5f),
                        MathF.Max(0.01f, part.Radius),
                        color);
                    break;

                case SpiderRagdollShapeType.Sphere:
                    drawer.DrawSphere(
                        position,
                        rotation,
                        MathF.Max(0.01f, part.Radius),
                        color);
                    break;

                case SpiderRagdollShapeType.Box:
                    drawer.DrawBox(
                        position,
                        rotation,
                        part.BoxHalfExtents,
                        color);
                    break;

                case SpiderRagdollShapeType.ConvexHull:
                    drawer.DrawSphere(
                        position,
                        rotation,
                        MathF.Max(0.05f, part.Radius),
                        color);
                    break;
            }
        }

        foreach (SpiderRagdollJointDefinition joint in definition.Joints)
        {
            if (!poses.TryGetValue(
                    joint.ParentPartId,
                    out (Vector3 Position, Quaternion Rotation) parent) ||
                !poses.TryGetValue(
                    joint.ChildPartId,
                    out (Vector3 Position, Quaternion Rotation) child))
            {
                continue;
            }

            Vector3 anchor;

            if (joint.ParentAnchor.LengthSquared() > 0.000001f ||
                joint.ChildAnchor.LengthSquared() > 0.000001f)
            {
                Vector3 parentAnchor =
                    parent.Position +
                    Vector3.Transform(
                        joint.ParentAnchor,
                        parent.Rotation);

                Vector3 childAnchor =
                    child.Position +
                    Vector3.Transform(
                        joint.ChildAnchor,
                        child.Rotation);

                anchor = (parentAnchor + childAnchor) * 0.5f;
            }
            else
            {
                SpiderRagdollPartDefinition? childDefinition =
                    definition.Parts.Find(part =>
                        string.Equals(
                            part.Id,
                            joint.ChildPartId,
                            StringComparison.OrdinalIgnoreCase));

                if (childDefinition == null)
                    continue;

                if (!skeleton.TryGetNodeIndex(
                        childDefinition.BoneName,
                        out _))
                {
                    continue;
                }

                CalculateBonePose(
                    skeleton,
                    childDefinition,
                    modelOrigin,
                    modelRotation,
                    safeScale,
                    out anchor,
                    out _);
            }

            drawer.DrawSphere(
                anchor,
                Quaternion.Identity,
                0.045f,
                new Vector3(1f, 1f, 0.1f));

            drawer.PushLine(
                parent.Position,
                child.Position,
                new Vector3(0.8f, 0.3f, 1f));
        }
    }

    public void ApplyImpulse(Vector3 impulse)
    {
        if (!Body.IsBuilt)
            return;

        _physics.BodyInterface.AddImpulse(
            Body.Native,
            impulse);
    }

    public void ApplyAngularImpulse(Vector3 impulse)
    {
        if (!Body.IsBuilt)
            return;

        _physics.BodyInterface.AddAngularImpulse(
            Body.Native,
            impulse);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        DisposePhysicsObjects();
    }

    private void BuildFallbackBody(
        Vector3 position,
        Quaternion rotation,
        Vector3 inheritedVelocity = default)
    {
        var definition = new SpiderRagdollPartDefinition
        {
            Id = "Body",
            BoneName = string.Empty,
            ShapeType = SpiderRagdollShapeType.Capsule,
            Radius = MathF.Max(
                0.05f,
                _definition.RootRadius),
            Height = MathF.Max(
                0.05f,
                _definition.RootHeight),
            Mass = MathF.Max(
                0.01f,
                _definition.RootMass),
            Friction = 0.5f,
            Restitution = 0.1f,
            CollidesWithWorld = true,
            CollidesWithOtherParts = false
        };

        RigidBody? body = CreatePartBody(
            definition,
            position,
            rotation,
            inheritedVelocity);

        if (body == null)
            return;

        _parts["Body"] = new PartRuntime(
            definition,
            body,
            position,
            rotation);

        Body = body;
    }

    private RigidBody? CreatePartBody(
        SpiderRagdollPartDefinition part,
        Vector3 position,
        Quaternion rotation,
        Vector3 inheritedVelocity)
    {
        var body = new RigidBody();

        switch (part.ShapeType)
        {
            case SpiderRagdollShapeType.Capsule:
                body.SetCapsule(
                    MathF.Max(0.01f, part.Radius),
                    MathF.Max(0.02f, part.Height));
                break;

            case SpiderRagdollShapeType.Sphere:
                body.SetSphere(
                    MathF.Max(0.01f, part.Radius));
                break;

            case SpiderRagdollShapeType.Box:
                body.SetBox(part.BoxHalfExtents);
                break;

            case SpiderRagdollShapeType.ConvexHull:
            {
                Vector3[]? vertices = part.ConvexHullVertices;

                if (vertices == null || vertices.Length < 4)
                    return null;

                body.SetConvexHull(vertices);
                break;
            }

            default:
                return null;
        }

        float mass =
            float.IsFinite(part.Mass) && part.Mass > 0f
                ? part.Mass
                : MathF.Max(
                    0.01f,
                    _definition.DefaultDensity);

        body.SetPosition(position)
            .SetRotation(rotation)
            .SetMass(mass)
            .SetKinematic(false)
            .SetFriction(System.Math.Clamp(part.Friction, 0f, 1f))
            .SetRestitution(System.Math.Clamp(part.Restitution, 0f, 1f))
            .SetAllowedDOFs(AllowedDOFs.All);

        body.Build(_physics);

        if (!body.IsBuilt)
            return null;

        if (IsFinite(inheritedVelocity))
        {
            _physics.BodyInterface.SetLinearVelocity(
                body.Native,
                inheritedVelocity);
        }

        return body;
    }

    private void AssignCollisionGroup(
        RigidBody body,
        int subgroupIndex)
    {
        GroupFilterTable? filter = _selfCollisionFilter;

        if (filter == null)
            return;

        BodyID bodyId = body.Native;
        CollisionSubGroupID subgroup = (uint)subgroupIndex;

        var collisionGroup = new CollisionGroup(
            filter,
            _collisionGroupId,
            subgroup);

        _physics.BodyInterface.SetCollisionGroup(
            in bodyId,
            in collisionGroup);
    }

    private void CreateJoint(
        SpiderRagdollJointDefinition joint)
    {
        if (string.Equals(
                joint.ParentPartId,
                joint.ChildPartId,
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!_parts.TryGetValue(
                joint.ParentPartId,
                out PartRuntime? parent))
        {
            return;
        }

        if (!_parts.TryGetValue(
                joint.ChildPartId,
                out PartRuntime? child))
        {
            return;
        }

        if (!parent.Body.IsBuilt || !child.Body.IsBuilt)
            return;

        Vector3 parentAnchor =
            parent.InitialPosition +
            Vector3.Transform(
                joint.ParentAnchor,
                parent.InitialRotation);

        Vector3 childAnchor =
            child.InitialPosition +
            Vector3.Transform(
                joint.ChildAnchor,
                child.InitialRotation);

        bool hasParentAnchor =
            joint.ParentAnchor.LengthSquared() > 0.000001f;

        bool hasChildAnchor =
            joint.ChildAnchor.LengthSquared() > 0.000001f;

        Vector3 anchor;

        if (hasParentAnchor && hasChildAnchor)
        {
            anchor = (parentAnchor + childAnchor) * 0.5f;
        }
        else if (hasParentAnchor)
        {
            anchor = parentAnchor;
        }
        else
        {
            // As cápsulas das pernas são centralizadas entre dois ossos.
            // Recuperar a origem do osso filho fornece exatamente o pivô da
            // articulação, independentemente do centro/rotação da cápsula.
            CalculateBonePoseFromBody(
                child.Definition,
                child.InitialPosition,
                child.InitialRotation,
                _modelScale,
                out Vector3 childBonePosition,
                out _);

            anchor = childBonePosition;
        }

        Vector3 parentTwistAxis =
            NormalizeOrFallback(
                Vector3.Transform(
                    Vector3.UnitY,
                    parent.InitialRotation),
                Vector3.UnitY);

        Vector3 childTwistAxis =
            NormalizeOrFallback(
                Vector3.Transform(
                    Vector3.UnitY,
                    child.InitialRotation),
                Vector3.UnitY);

        Vector3 parentPlaneAxis =
            BuildPlaneAxis(
                parentTwistAxis,
                parent.InitialRotation);

        Vector3 childPlaneAxis =
            BuildPlaneAxis(
                childTwistAxis,
                child.InitialRotation);

        float swingLimit = ClampAngle(
            joint.SwingLimitRadians,
            0.01f,
            MathF.PI);

        float twistMin = ClampAngle(
            joint.TwistMinRadians,
            -MathF.PI,
            MathF.PI);

        float twistMax = ClampAngle(
            joint.TwistMaxRadians,
            -MathF.PI,
            MathF.PI);

        if (twistMin > twistMax)
        {
            float temporary = twistMin;
            twistMin = twistMax;
            twistMax = temporary;
        }

        BodyID parentId = parent.Body.Native;
        BodyID childId = child.Body.Native;

        _physics.BodyLockInterface.LockRead(
            in parentId,
            out BodyLockRead parentLock);

        try
        {
            if (!parentLock.Succeeded ||
                parentLock.Body == null)
            {
                return;
            }

            JoltPhysicsSharp.Body parentNative =
                parentLock.Body!;

            _physics.BodyLockInterface.LockRead(
                in childId,
                out BodyLockRead childLock);

            try
            {
                if (!childLock.Succeeded ||
                    childLock.Body == null)
                {
                    return;
                }

                JoltPhysicsSharp.Body childNative =
                    childLock.Body!;

                var settings =
                    new SwingTwistConstraintSettings
                    {
                        Space =
                            JoltPhysicsSharp.ConstraintSpace.WorldSpace,

                        Position1 = anchor,
                        Position2 = anchor,

                        TwistAxis1 = parentTwistAxis,
                        PlaneAxis1 = parentPlaneAxis,

                        TwistAxis2 = childTwistAxis,
                        PlaneAxis2 = childPlaneAxis,

                        SwingType =
                            JoltPhysicsSharp.SwingType.Cone,

                        NormalHalfConeAngle = swingLimit,
                        PlaneHalfConeAngle = swingLimit,

                        TwistMinAngle = twistMin,
                        TwistMaxAngle = twistMax,

                        MaxFrictionTorque = 0f,
                        Enabled = true,
                        DrawConstraintSize = 0.03f
                    };

                SwingTwistConstraint? constraint = null;

                try
                {
                    constraint =
                        new SwingTwistConstraint(
                            settings,
                            in parentNative,
                            in childNative);

                    _physics.Native.AddConstraint(
                        constraint);

                    _constraints.Add(constraint);
                }
                catch (Exception exception)
                {
                    constraint?.Dispose();

                    Logger.Warn(
                        $"[SpiderDeathBody] Could not create joint " +
                        $"'{joint.Id}': {exception.Message}");
                }
            }
            finally
            {
                _physics.BodyLockInterface.UnlockRead(
                    in childLock);
            }
        }
        finally
        {
            _physics.BodyLockInterface.UnlockRead(
                in parentLock);
        }
    }

    private static void CalculateBonePose(
        AnimationSkeleton skeleton,
        SpiderRagdollPartDefinition part,
        Vector3 modelOrigin,
        Quaternion modelRotation,
        Vector3 modelScale,
        out Vector3 worldPosition,
        out Quaternion worldRotation)
    {
        worldPosition = modelOrigin;
        worldRotation = NormalizeOrIdentity(modelRotation);

        if (skeleton.TryGetNodeIndex(
                part.BoneName,
                out int nodeIndex) &&
            (uint)nodeIndex < (uint)skeleton.Nodes.Length)
        {
            Matrix4x4 standardMatrix =
                Matrix4x4.Transpose(
                    skeleton.Nodes[nodeIndex].Global);

            if (Matrix4x4.Decompose(
                    standardMatrix,
                    out _,
                    out Quaternion boneRotation,
                    out Vector3 bonePosition) &&
                IsFinite(bonePosition) &&
                IsFinite(boneRotation))
            {
                worldPosition =
                    modelOrigin +
                    Vector3.Transform(
                        bonePosition * modelScale,
                        modelRotation);

                worldRotation =
                    NormalizeOrIdentity(
                        Quaternion.Concatenate(
                            boneRotation,
                            modelRotation));
            }
        }

        worldPosition +=
            Vector3.Transform(
                part.LocalOffset * modelScale,
                worldRotation);

        worldRotation =
            NormalizeOrIdentity(
                Quaternion.Concatenate(
                    part.LocalRotation,
                    worldRotation));
    }

    private static void CalculateBonePoseFromBody(
        SpiderRagdollPartDefinition part,
        Vector3 bodyWorldPosition,
        Quaternion bodyWorldRotation,
        Vector3 modelScale,
        out Vector3 boneWorldPosition,
        out Quaternion boneWorldRotation)
    {
        Quaternion inverseLocalRotation =
            Quaternion.Inverse(
                NormalizeOrIdentity(part.LocalRotation));

        boneWorldRotation =
            NormalizeOrIdentity(
                Quaternion.Concatenate(
                    inverseLocalRotation,
                    bodyWorldRotation));

        boneWorldPosition =
            bodyWorldPosition -
            Vector3.Transform(
                part.LocalOffset * modelScale,
                boneWorldRotation);
    }

    private static bool TryBuildModelGlobal(
        Vector3 worldPosition,
        Quaternion worldRotation,
        Matrix4x4 referenceModelGlobal,
        Entity entity,
        out Matrix4x4 modelGlobal)
    {
        modelGlobal = Matrix4x4.Identity;

        if (!IsFinite(worldPosition) ||
            !IsFinite(worldRotation))
        {
            return false;
        }

        // O corpo físico só tem translação e rotação. A malha, porém, pode
        // depender de escalas importadas pelo rig (inclusive nas patas).
        // Preservamos a escala global congelada da pose de morte para que o
        // skinning não reduza os segmentos a linhas durante a simulação.
        Matrix4x4 referenceStandard =
            Matrix4x4.Transpose(referenceModelGlobal);

        if (!Matrix4x4.Decompose(
                referenceStandard,
                out Vector3 skeletonScale,
                out _,
                out _) ||
            !IsFinite(skeletonScale))
        {
            return false;
        }

        Vector3 modelScale = SanitizeScale(
            entity.Transform.Scale * entity.ModelScale);

        Quaternion modelWorldRotation =
            NormalizeOrIdentity(
                Quaternion.Concatenate(
                    entity.ModelRotation,
                    entity.Transform.Rotation));

        Quaternion inverseModelWorldRotation =
            Quaternion.Inverse(modelWorldRotation);

        Vector3 modelPosition =
            Vector3.Transform(
                worldPosition -
                (entity.Transform.Position + entity.ModelOffset),
                inverseModelWorldRotation);

        modelPosition = new Vector3(
            modelPosition.X / modelScale.X,
            modelPosition.Y / modelScale.Y,
            modelPosition.Z / modelScale.Z);

        Quaternion localRotation =
            NormalizeOrIdentity(
                Quaternion.Concatenate(
                    worldRotation,
                    inverseModelWorldRotation));

        if (!IsFinite(modelPosition) ||
            !IsFinite(localRotation))
        {
            return false;
        }

        Matrix4x4 standardGlobal =
            Matrix4x4.CreateScale(skeletonScale) *
            Matrix4x4.CreateFromQuaternion(localRotation) *
            Matrix4x4.CreateTranslation(modelPosition);

        modelGlobal = Matrix4x4.Transpose(standardGlobal);
        return AnimationSkeleton.IsFinite(modelGlobal);
    }

    private static Vector3 SanitizeScale(Vector3 scale) =>
        new(
            MathF.Max(MathF.Abs(scale.X), 0.0001f),
            MathF.Max(MathF.Abs(scale.Y), 0.0001f),
            MathF.Max(MathF.Abs(scale.Z), 0.0001f));

    private static Vector3 BuildPlaneAxis(
        Vector3 twistAxis,
        Quaternion bodyRotation)
    {
        Vector3 candidate =
            Vector3.Transform(
                Vector3.UnitZ,
                bodyRotation);

        candidate -=
            twistAxis *
            Vector3.Dot(candidate, twistAxis);

        return NormalizeOrFallback(
            candidate,
            BuildPerpendicular(twistAxis));
    }

    private static Vector3 BuildPerpendicular(
        Vector3 normal)
    {
        Vector3 candidate =
            MathF.Abs(normal.X) < 0.7f
                ? Vector3.UnitX
                : MathF.Abs(normal.Y) < 0.7f
                    ? Vector3.UnitY
                    : Vector3.UnitZ;

        candidate -=
            normal *
            Vector3.Dot(candidate, normal);

        return NormalizeOrFallback(
            candidate,
            Vector3.UnitZ);
    }

    private static float ClampAngle(
        float value,
        float minimum,
        float maximum)
    {
        if (!float.IsFinite(value))
            return minimum;

        return System.Math.Clamp(
            value,
            minimum,
            maximum);
    }

    private static Vector3 NormalizeOrFallback(
        Vector3 value,
        Vector3 fallback)
    {
        if (IsFinite(value) &&
            value.LengthSquared() > 0.000001f)
        {
            return Vector3.Normalize(value);
        }

        return Vector3.Normalize(fallback);
    }

    private static Quaternion NormalizeOrIdentity(
        Quaternion value)
    {
        if (IsFinite(value) &&
            value.LengthSquared() > 0.000001f)
        {
            return Quaternion.Normalize(value);
        }

        return Quaternion.Identity;
    }

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) &&
        float.IsFinite(value.Y) &&
        float.IsFinite(value.Z);

    private static bool IsFinite(Quaternion value) =>
        float.IsFinite(value.X) &&
        float.IsFinite(value.Y) &&
        float.IsFinite(value.Z) &&
        float.IsFinite(value.W);

    private void DisposePhysicsObjects()
    {
        for (int i = _constraints.Count - 1; i >= 0; i--)
        {
            _physics.Native.RemoveConstraint(
                _constraints[i]);

            _constraints[i].Dispose();
        }

        _constraints.Clear();

        foreach (PartRuntime part in _parts.Values)
        {
            if (!part.Body.IsBuilt)
                continue;

            _physics.DestroyBody(
                part.Body.Native);

            part.Body.Destroy();
        }

        _parts.Clear();

        _selfCollisionFilter?.Dispose();
        _selfCollisionFilter = null;

        Body = new RigidBody();
    }
}
