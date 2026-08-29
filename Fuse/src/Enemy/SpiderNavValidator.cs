using System;
using System.Collections.Generic;
using System.Numerics;
using Fuse.Physics;
using Fuse.Scene;
using JoltPhysicsSharp;

namespace Fuse.Enemy;

/// <summary>
/// Validates and creates directed connections between already existing
/// navigation nodes. It does not generate nodes and it does not move a spider.
/// Node positions are interpreted as spider body-center positions, with the
/// corresponding supporting surface described by SurfaceNormal.
/// </summary>
public sealed class SpiderNavValidator
{
    private const float Epsilon = 0.0001f;
    private const float DegreesToRadians = MathF.PI / 180f;
    private const float SurfaceValidationOffset = 0.06f;
    private const float SupportProbeOffset = 0.12f;
    private const float SupportDistanceTolerance = 0.50f;
    private const float MinimumSupportAlignment = 0.35f;
    private const float CastStartContactTolerance = 0.02f;
    private const float TransitionClassificationDegrees = 5f;
    private const float MaximumCastSegmentDistance = 1.0f;
    private const float MaximumNormalStepDegrees = 22.5f;
    private const int MaximumValidationSegments = 16;

    private readonly SceneManager _scene;
    private readonly BodyID? _excludedBody;
    private readonly float _maxNormalChangeRadians;
    private readonly float _normalTransitionCost;

    public SpiderNavValidator(
        SceneManager scene,
        float maxConnectionDistance = 4f,
        BodyID? excludedBody = null,
        float maxNormalChangeDegrees = 120f,
        float normalTransitionCost = 0.5f)
    {
        ArgumentNullException.ThrowIfNull(scene);
        if (!float.IsFinite(maxConnectionDistance) || maxConnectionDistance <= 0f)
            throw new ArgumentOutOfRangeException(nameof(maxConnectionDistance));
        if (!float.IsFinite(maxNormalChangeDegrees) ||
            maxNormalChangeDegrees <= 0f ||
            maxNormalChangeDegrees > 180f)
        {
            throw new ArgumentOutOfRangeException(nameof(maxNormalChangeDegrees));
        }
        if (!float.IsFinite(normalTransitionCost) || normalTransitionCost < 0f)
            throw new ArgumentOutOfRangeException(nameof(normalTransitionCost));

        _scene = scene;
        _excludedBody = excludedBody;
        MaxConnectionDistance = maxConnectionDistance;
        _maxNormalChangeRadians = maxNormalChangeDegrees * DegreesToRadians;
        _normalTransitionCost = normalTransitionCost;
    }

    public float MaxConnectionDistance { get; }

    /// <summary>
    /// Checks whether the body can travel from A to B. This is directional:
    /// callers that need a reversible connection must validate both directions.
    /// </summary>
    public bool CanConnect(
        SpiderNavNode a,
        SpiderNavNode b,
        float spiderRadius,
        float spiderHeight)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        return TryBuildEdge(a, b, spiderRadius, spiderHeight, out _);
    }

    /// <summary>
    /// Validates a connection and inserts it into the graph when valid.
    /// </summary>
    public bool TryConnect(
        SpiderNavGraph graph,
        SpiderNavNode a,
        SpiderNavNode b,
        float spiderRadius,
        float spiderHeight,
        out SpiderNavEdge edge)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        edge = default;

        // Use the graph's instances as the source of truth. This prevents a
        // caller from validating one node instance and inserting an edge into
        // a graph containing a different node with the same ID.
        if (!graph.TryGetNode(a.Id, out SpiderNavNode? graphA) ||
            !graph.TryGetNode(b.Id, out SpiderNavNode? graphB) ||
            graphA == null ||
            graphB == null)
        {
            return false;
        }

        if (!TryBuildEdge(graphA, graphB, spiderRadius, spiderHeight, out edge))
            return false;

        graph.AddEdge(edge);
        return true;
    }

    private bool TryBuildEdge(
        SpiderNavNode a,
        SpiderNavNode b,
        float spiderRadius,
        float spiderHeight,
        out SpiderNavEdge edge)
    {
        edge = default;
        ValidateBodyDimensions(spiderRadius, spiderHeight);

        if (a.Id == b.Id)
            return false;

        float distance = Vector3.Distance(a.Position, b.Position);
        if (!float.IsFinite(distance) ||
            distance <= Epsilon ||
            distance > MaxConnectionDistance + Epsilon)
        {
            return false;
        }

        if (a.Clearance + Epsilon < spiderRadius ||
            b.Clearance + Epsilon < spiderRadius)
        {
            return false;
        }

        float normalDot = System.Math.Clamp(Vector3.Dot(a.SurfaceNormal, b.SurfaceNormal), -1f, 1f);
        float normalChange = MathF.Acos(normalDot);
        if (normalChange > _maxNormalChangeRadians + Epsilon)
            return false;

        float halfHeight = spiderHeight * 0.5f;
        using var capsule = new CapsuleShape(halfHeight, spiderRadius);

        int segmentCount = GetValidationSegmentCount(distance, normalChange);
        if (!HasSupportAlongConnection(a, b, halfHeight + spiderRadius, segmentCount))
            return false;
        if (!HasCapsuleClearanceAlongConnection(capsule, a, b, segmentCount))
            return false;

        bool isTransition = normalChange > TransitionClassificationDegrees * DegreesToRadians;
        float cost = distance;
        if (isTransition)
            cost += normalChange / MathF.PI * _normalTransitionCost;

        edge = new SpiderNavEdge(
            a.Id,
            b.Id,
            cost,
            MathF.Min(a.Clearance, b.Clearance),
            isTransition);
        return true;
    }

    private bool HasSupportAlongConnection(
        SpiderNavNode a,
        SpiderNavNode b,
        float capsuleHalfExtent,
        int segmentCount)
    {
        for (int i = 0; i <= segmentCount; i++)
        {
            float t = i / (float)segmentCount;
            Vector3 position = Vector3.Lerp(a.Position, b.Position, t);
            Vector3 normal = InterpolateNormal(a.SurfaceNormal, b.SurfaceNormal, t);

            Vector3 rayOrigin = position + normal * SupportProbeOffset;
            float expectedDistance = capsuleHalfExtent + SupportProbeOffset;
            float maxDistance = expectedDistance + SupportDistanceTolerance;

            if (!_scene.Raycast(
                    rayOrigin,
                    -normal,
                    maxDistance,
                    out SceneRaycastHit hit,
                    _excludedBody,
                    collideWithBackFaces: true))
            {
                return false;
            }

            if (hit.Distance < expectedDistance - SupportDistanceTolerance ||
                hit.Distance > expectedDistance + SupportDistanceTolerance ||
                Vector3.Dot(hit.Normal, normal) < MinimumSupportAlignment)
            {
                return false;
            }
        }

        return true;
    }

    private bool HasCapsuleClearanceAlongConnection(
        CapsuleShape capsule,
        SpiderNavNode a,
        SpiderNavNode b,
        int segmentCount)
    {
        var shapeCastSettings = new ShapeCastSettings
        {
            BackFaceModeTriangles = BackFaceMode.CollideWithBackFaces,
            BackFaceModeConvex = BackFaceMode.CollideWithBackFaces,
            CollisionTolerance = 0.01f,
            PenetrationTolerance = 0.01f,
            UseShrunkenShapeAndConvexRadius = false
        };

        for (int i = 0; i < segmentCount; i++)
        {
            float t0 = i / (float)segmentCount;
            float t1 = (i + 1) / (float)segmentCount;
            Vector3 normal0 = InterpolateNormal(a.SurfaceNormal, b.SurfaceNormal, t0);
            Vector3 normal1 = InterpolateNormal(a.SurfaceNormal, b.SurfaceNormal, t1);
            Vector3 position0 = Vector3.Lerp(a.Position, b.Position, t0) + normal0 * SurfaceValidationOffset;
            Vector3 position1 = Vector3.Lerp(a.Position, b.Position, t1) + normal1 * SurfaceValidationOffset;
            Vector3 castDirection = position1 - position0;

            if (castDirection.LengthSquared() <= Epsilon * Epsilon)
                continue;

            Vector3 intendedForward = ProjectOnPlane(castDirection, normal0);
            Matrix4x4 transform = CreateSurfaceTransform(position0, normal0, intendedForward);
            Vector3 baseOffset = Vector3.Zero;
            var hits = new List<ShapeCastResult>(4);

            using BroadPhaseLayerFilter broadPhaseFilter = new DefaultBroadPhaseLayerFilter();
            using ObjectLayerFilter objectLayerFilter = new DefaultObjectLayerFilter();
            using BodyFilter bodyFilter = _excludedBody.HasValue
                ? new EnemyBodyFilter(_excludedBody.Value)
                : new DefaultBodyFilter();
            using ShapeFilter shapeFilter = new DefaultShapeFilter();

            bool hasCollision = _scene.Physics.NarrowPhaseQuery.CastShape(
                capsule,
                ref transform,
                ref castDirection,
                shapeCastSettings,
                ref baseOffset,
                CollisionCollectorType.AllHit,
                hits,
                broadPhaseFilter,
                objectLayerFilter,
                bodyFilter,
                shapeFilter);

            if (!hasCollision)
                continue;

            foreach (ShapeCastResult hit in hits)
            {
                // A capsule starts in contact with its supporting surface.
                // Contacts at the initial fraction are expected; a hit later
                // along the segment means that the body enters an obstacle.
                if (hit.Fraction > CastStartContactTolerance)
                    return false;
            }
        }

        return true;
    }

    private int GetValidationSegmentCount(float distance, float normalChange)
    {
        int distanceSegments = System.Math.Max(1, (int)MathF.Ceiling(distance / MaximumCastSegmentDistance));
        int normalSegments = System.Math.Max(1, (int)MathF.Ceiling(normalChange / (MaximumNormalStepDegrees * DegreesToRadians)));
        return System.Math.Min(MaximumValidationSegments, System.Math.Max(distanceSegments, normalSegments));
    }

    private static Vector3 InterpolateNormal(Vector3 from, Vector3 to, float t)
    {
        Vector3 blended = Vector3.Lerp(from, to, t);
        return NormalizeOrFallback(blended, from);
    }

    private static Matrix4x4 CreateSurfaceTransform(
        Vector3 position,
        Vector3 surfaceNormal,
        Vector3 desiredForward)
    {
        surfaceNormal = NormalizeOrFallback(surfaceNormal, Vector3.UnitY);
        Vector3 forward = ProjectOnPlane(desiredForward, surfaceNormal);
        if (forward.LengthSquared() <= Epsilon * Epsilon)
        {
            Vector3 reference = MathF.Abs(Vector3.Dot(surfaceNormal, Vector3.UnitY)) < 0.95f
                ? Vector3.UnitY
                : Vector3.UnitX;
            forward = ProjectOnPlane(reference, surfaceNormal);
        }

        forward = NormalizeOrFallback(forward, Vector3.UnitZ);
        Vector3 right = NormalizeOrFallback(Vector3.Cross(surfaceNormal, forward), Vector3.UnitX);
        forward = NormalizeOrFallback(Vector3.Cross(right, surfaceNormal), forward);

        Matrix4x4 transform = new(
            right.X, right.Y, right.Z, 0f,
            surfaceNormal.X, surfaceNormal.Y, surfaceNormal.Z, 0f,
            forward.X, forward.Y, forward.Z, 0f,
            0f, 0f, 0f, 1f);
        transform.Translation = position;
        return transform;
    }

    private static Vector3 ProjectOnPlane(Vector3 value, Vector3 normal) =>
        value - normal * Vector3.Dot(value, normal);

    private static Vector3 NormalizeOrFallback(Vector3 value, Vector3 fallback)
    {
        if (value.LengthSquared() > Epsilon * Epsilon)
            return Vector3.Normalize(value);
        if (fallback.LengthSquared() > Epsilon * Epsilon)
            return Vector3.Normalize(fallback);
        return Vector3.UnitZ;
    }

    private static void ValidateBodyDimensions(float spiderRadius, float spiderHeight)
    {
        if (!float.IsFinite(spiderRadius) || spiderRadius <= 0f)
            throw new ArgumentOutOfRangeException(nameof(spiderRadius));
        if (!float.IsFinite(spiderHeight) || spiderHeight <= 0f)
            throw new ArgumentOutOfRangeException(nameof(spiderHeight));
    }
}
