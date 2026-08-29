using System;
using System.Collections.Generic;
using System.Numerics;
using Fuse.Animation;
using Fuse.Core;
using Fuse.Physics;
using Fuse.Renderer;
using JoltPhysicsSharp;
using AnimationSkeleton = Fuse.Animation.Skeleton;

namespace Fuse.Enemy;

/// <summary>
/// Hitbox articulada da aranha viva.
///
/// Estas partes são corpos cinemáticos/sensores: acompanham o esqueleto, são
/// atingíveis por raycasts e não participam da locomoção, das transições de
/// superfície ou da simulação do ragdoll de morte.
/// </summary>
public sealed class SpiderDamageBody : IDisposable
{
    private sealed class PartRuntime
    {
        public PartRuntime(
            SpiderRagdollPartDefinition definition,
            RigidBody body)
        {
            Definition = definition;
            Body = body;
        }

        public SpiderRagdollPartDefinition Definition { get; }
        public RigidBody Body { get; }
    }

    private readonly PhysicsWorld _physics;
    private readonly SpiderRagdollDefinition _definition;
    private readonly Dictionary<string, PartRuntime> _parts =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<BodyID, string> _partByBodyId = new();
    private readonly List<BodyID> _bodyIds = new();
    private bool _disposed;

    public SpiderDamageBody(
        PhysicsWorld physics,
        AnimationSkeleton skeleton,
        Vector3 modelOrigin,
        Quaternion modelRotation,
        Vector3 modelScale,
        SpiderRagdollDefinition definition)
    {
        _physics = physics;
        _definition = definition;

        if (skeleton.Nodes.Length == 0)
            return;

        skeleton.ComputeGlobalTransforms();
        modelRotation = NormalizeOrIdentity(modelRotation);
        modelScale = SanitizeScale(modelScale);

        for (int i = 0; i < definition.Parts.Count; i++)
        {
            SpiderRagdollPartDefinition part = definition.Parts[i];

            if (!skeleton.TryGetNodeIndex(
                    part.BoneName,
                    out _))
            {
                Logger.Warn(
                    $"[SpiderDamageBody] Bone '{part.BoneName}' " +
                    $"for part '{part.Id}' was not found.");
                continue;
            }

            SpiderDeathBody.CalculateBonePose(
                skeleton,
                part,
                modelOrigin,
                modelRotation,
                modelScale,
                out Vector3 position,
                out Quaternion rotation);

            RigidBody? body = CreateSensorBody(
                part,
                position,
                rotation);

            if (body == null)
                continue;

            var runtime = new PartRuntime(part, body);
            _parts.Add(part.Id, runtime);
            _partByBodyId[body.Native] = part.Id;
            _bodyIds.Add(body.Native);
        }

        Logger.Info(
            $"[SpiderDamageBody] Live hit proxy created: " +
            $"parts={_parts.Count}");
    }

    public bool IsBuilt => !_disposed && _parts.Count > 0;

    public int PartCount => _parts.Count;

    /// <summary>
    /// BodyIDs que devem ser registrados como pertencentes a esta aranha e
    /// ignorados pelos raycasts de suporte da própria locomotion.
    /// </summary>
    public IReadOnlyList<BodyID> BodyIds => _bodyIds;

    public bool TryGetPart(
        BodyID bodyId,
        out string partId)
    {
        return _partByBodyId.TryGetValue(bodyId, out partId!);
    }

    /// <summary>
    /// Atualiza todas as cápsulas para a pose atual do Animator/procedural
    /// walk. Deve ser chamado depois de a pose viva ser calculada no frame.
    /// </summary>
    public void SyncFromSkeleton(
        AnimationSkeleton skeleton,
        Vector3 modelOrigin,
        Quaternion modelRotation,
        Vector3 modelScale)
    {
        if (!IsBuilt || skeleton.Nodes.Length == 0)
            return;

        skeleton.ComputeGlobalTransforms();
        modelRotation = NormalizeOrIdentity(modelRotation);
        modelScale = SanitizeScale(modelScale);

        foreach (PartRuntime part in _parts.Values)
        {
            if (!part.Body.IsBuilt)
                continue;

            SpiderDeathBody.CalculateBonePose(
                skeleton,
                part.Definition,
                modelOrigin,
                modelRotation,
                modelScale,
                out Vector3 position,
                out Quaternion rotation);

            if (!IsFinite(position) || !IsFinite(rotation))
                continue;

            _physics.BodyInterface.SetPositionAndRotation(
                part.Body.Native,
                position,
                rotation,
                Activation.Activate);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        foreach (PartRuntime part in _parts.Values)
        {
            if (!part.Body.IsBuilt)
                continue;

            _physics.DestroyBody(part.Body.Native);
            part.Body.Destroy();
        }

        _parts.Clear();
        _partByBodyId.Clear();
        _bodyIds.Clear();
    }

    private RigidBody? CreateSensorBody(
        SpiderRagdollPartDefinition part,
        Vector3 position,
        Quaternion rotation)
    {
        if (!IsFinite(position) || !IsFinite(rotation))
            return null;

        var body = new RigidBody();

        switch (part.ShapeType)
        {
            case SpiderRagdollShapeType.Capsule:
                body.SetCapsule(
                    MathF.Max(0.01f, part.Radius),
                    MathF.Max(0.02f, part.Height));
                break;

            case SpiderRagdollShapeType.Sphere:
                body.SetSphere(MathF.Max(0.01f, part.Radius));
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

        body.SetPosition(position)
            .SetRotation(rotation)
            .SetMass(0f)
            .SetKinematic(true)
            .SetTrigger(true)
            .SetFriction(0f)
            .SetRestitution(0f)
            .SetAllowedDOFs(AllowedDOFs.All)
            .Build(_physics);

        return body.IsBuilt ? body : null;
    }

    private static Vector3 SanitizeScale(Vector3 scale) =>
        new(
            MathF.Max(MathF.Abs(scale.X), 0.0001f),
            MathF.Max(MathF.Abs(scale.Y), 0.0001f),
            MathF.Max(MathF.Abs(scale.Z), 0.0001f));

    private static Quaternion NormalizeOrIdentity(Quaternion value)
    {
        if (IsFinite(value) && value.LengthSquared() > 0.000001f)
            return Quaternion.Normalize(value);

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
}
