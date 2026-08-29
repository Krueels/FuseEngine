using System;
using System.Collections.Generic;
using System.Numerics;
using Fuse.Math;
using Fuse.Physics;
using Fuse.Scene;
using JoltPhysicsSharp;

namespace Fuse.Enemy;

/// <summary>
/// Settings for the one-time navigation bake over a world-space AABB.
/// SpiderHeight is the height of the cylindrical part of the capsule; the
/// spherical caps add spiderRadius to each end, matching CapsuleShape.
/// </summary>
public sealed class SpiderNavGenerationSettings
{
    public float Spacing { get; init; } = 1.5f;
    public float MaxConnectionDistance { get; init; } = 3f;
    public float SpiderRadius { get; init; } = 0.3f;
    public float SpiderHeight { get; init; } = 1.2f;
    public float MaxSurfaceSearchDistance { get; init; } = 8f;
    public BodyID? ExcludedBody { get; init; }
    public bool ConnectNodes { get; init; } = true;
}

/// <summary>
/// Builds a static spider navigation graph by sampling collision surfaces in
/// a requested AABB. This class performs no gameplay-time node generation.
/// </summary>
public sealed class SpiderNavGenerator
{
    private const float Epsilon = 0.0001f;
    private const float SurfaceNodeMargin = 0.08f;
    private const float ScanMargin = 0.05f;
    private const float RayAdvance = 0.025f;
    private const float PlacementPenetrationTolerance = 0.025f;
    private const int MaximumHitsPerRay = 64;

    private readonly SceneManager _scene;

    public SpiderNavGenerator(SceneManager scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        _scene = scene;
    }

    /// <summary>
    /// Convenience overload for the main generation parameters.
    /// </summary>
    public SpiderNavGraph Generate(
        AABB bounds,
        float spacing,
        float spiderRadius,
        float spiderHeight,
        float maxConnectionDistance,
        float maxSurfaceSearchDistance)
    {
        return Generate(
            bounds,
            new SpiderNavGenerationSettings
            {
                Spacing = spacing,
                SpiderRadius = spiderRadius,
                SpiderHeight = spiderHeight,
                MaxConnectionDistance = maxConnectionDistance,
                MaxSurfaceSearchDistance = maxSurfaceSearchDistance
            });
    }

    /// <summary>
    /// Scans the volume using three orthogonal scan bases and both directions
    /// of each basis. Each ray may be split into chunks so the configured
    /// MaxSurfaceSearchDistance remains the maximum individual query length.
    /// </summary>
    public SpiderNavGraph Generate(AABB bounds, SpiderNavGenerationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ValidateSettings(bounds, settings);

        float halfHeight = settings.SpiderHeight * 0.5f;
        float capsuleHalfExtent = halfHeight + settings.SpiderRadius;
        using var capsule = new CapsuleShape(halfHeight, settings.SpiderRadius);

        var graph = new SpiderNavGraph();
        float minimumNodeSeparation = MathF.Max(0.05f, settings.Spacing * 0.45f);
        var nodeIndex = new SpiderNavSpatialIndex(minimumNodeSeparation);
        int nextNodeId = 0;

        Vector3[] scanAxes =
        {
            Vector3.UnitX,
            Vector3.UnitY,
            Vector3.UnitZ
        };

        Vector3 boundsMin = bounds.GetBoundsMin();
        Vector3 boundsMax = bounds.GetBoundsMax();
        Vector3 extent = boundsMax - boundsMin;

        for (int i = 0; i < scanAxes.Length; i++)
        {
            Vector3 scanAxis = scanAxes[i];
            Vector3 tangentU = scanAxes[(i + 1) % scanAxes.Length];
            Vector3 tangentV = scanAxes[(i + 2) % scanAxes.Length];

            ScanPlaneLines(
                bounds,
                extent,
                scanAxis,
                tangentU,
                tangentV,
                settings,
                capsule,
                capsuleHalfExtent,
                graph,
                nodeIndex,
                ref nextNodeId);
        }

        if (settings.ConnectNodes && graph.Count > 1)
        {
            ConnectNodes(
                graph,
                settings.MaxConnectionDistance,
                settings.SpiderRadius,
                settings.SpiderHeight,
                settings.ExcludedBody);
        }

        return graph;
    }

    private void ScanPlaneLines(
        AABB bounds,
        Vector3 extent,
        Vector3 scanAxis,
        Vector3 tangentU,
        Vector3 tangentV,
        SpiderNavGenerationSettings settings,
        CapsuleShape capsule,
        float capsuleHalfExtent,
        SpiderNavGraph graph,
        SpiderNavSpatialIndex nodeIndex,
        ref int nextNodeId)
    {
        Vector3 boundsMin = bounds.GetBoundsMin();
        float scanLength = Vector3.Dot(extent, scanAxis);
        float uLength = Vector3.Dot(extent, tangentU);
        float vLength = Vector3.Dot(extent, tangentV);
        int uSteps = (int)MathF.Ceiling(uLength / settings.Spacing);
        int vSteps = (int)MathF.Ceiling(vLength / settings.Spacing);

        for (int uIndex = 0; uIndex <= uSteps; uIndex++)
        {
            float u = MathF.Min(uIndex * settings.Spacing, uLength);
            for (int vIndex = 0; vIndex <= vSteps; vIndex++)
            {
                float v = MathF.Min(vIndex * settings.Spacing, vLength);
                Vector3 lineBase = boundsMin + tangentU * u + tangentV * v;
                float lineDistance = scanLength + ScanMargin * 2f;

                CollectRayHits(
                    lineBase - scanAxis * ScanMargin,
                    scanAxis,
                    lineDistance,
                    bounds,
                    settings,
                    capsule,
                    capsuleHalfExtent,
                    graph,
                    nodeIndex,
                    ref nextNodeId);

                CollectRayHits(
                    lineBase + scanAxis * (scanLength + ScanMargin),
                    -scanAxis,
                    lineDistance,
                    bounds,
                    settings,
                    capsule,
                    capsuleHalfExtent,
                    graph,
                    nodeIndex,
                    ref nextNodeId);
            }
        }
    }

    private void CollectRayHits(
        Vector3 rayOrigin,
        Vector3 rayDirection,
        float totalDistance,
        AABB bounds,
        SpiderNavGenerationSettings settings,
        CapsuleShape capsule,
        float capsuleHalfExtent,
        SpiderNavGraph graph,
        SpiderNavSpatialIndex nodeIndex,
        ref int nextNodeId)
    {
        rayDirection = NormalizeOrFallback(rayDirection, Vector3.UnitY);
        float remainingDistance = totalDistance;
        Vector3 currentOrigin = rayOrigin;
        int hitCount = 0;

        while (remainingDistance > Epsilon && hitCount < MaximumHitsPerRay)
        {
            float queryDistance = MathF.Min(remainingDistance, settings.MaxSurfaceSearchDistance);
            if (!_scene.Raycast(
                    currentOrigin,
                    rayDirection,
                    queryDistance,
                    out SceneRaycastHit hit,
                    settings.ExcludedBody,
                    collideWithBackFaces: true))
            {
                currentOrigin += rayDirection * queryDistance;
                remainingDistance -= queryDistance;
                continue;
            }

            AddSurfaceCandidate(
                hit,
                rayDirection,
                bounds,
                settings,
                capsule,
                capsuleHalfExtent,
                graph,
                nodeIndex,
                ref nextNodeId);

            float advance = MathF.Max(RayAdvance, hit.Distance + RayAdvance);
            currentOrigin += rayDirection * advance;
            remainingDistance -= advance;
            hitCount++;
        }
    }

    private void AddSurfaceCandidate(
        in SceneRaycastHit hit,
        Vector3 rayDirection,
        AABB bounds,
        SpiderNavGenerationSettings settings,
        CapsuleShape capsule,
        float capsuleHalfExtent,
        SpiderNavGraph graph,
        SpiderNavSpatialIndex nodeIndex,
        ref int nextNodeId)
    {
        Vector3 normal = NormalizeOrFallback(hit.Normal, -rayDirection);
        Vector3 tangent = ProjectOnPlane(-rayDirection, normal);
        BuildTangentBasis(normal, tangent, out Vector3 forward, out _);

        Vector3 bodyPosition = hit.Position + normal * (capsuleHalfExtent + SurfaceNodeMargin);
        if (!bounds.ContainsPoint(bodyPosition))
            return;

        if (!IsSafeBodyPlacement(bodyPosition, normal, forward, capsule))
            return;

        float clearance = MeasureClearance(
            bodyPosition,
            normal,
            forward,
            settings.MaxSurfaceSearchDistance,
            capsuleHalfExtent,
            settings.ExcludedBody);
        if (clearance + Epsilon < settings.SpiderRadius)
            return;

        if (nodeIndex.ContainsNearby(bodyPosition, normal))
            return;

        var node = new SpiderNavNode(
            nextNodeId++,
            bodyPosition,
            normal,
            clearance);
        graph.AddNode(node);
        nodeIndex.Add(node);
    }

    private bool IsSafeBodyPlacement(
        Vector3 bodyPosition,
        Vector3 surfaceNormal,
        Vector3 desiredForward,
        CapsuleShape capsule)
    {
        Matrix4x4 transform = CreateSurfaceTransform(bodyPosition, surfaceNormal, desiredForward);
        Vector3 scale = Vector3.One;
        Vector3 baseOffset = Vector3.Zero;
        var results = new List<CollideShapeResult>(4);
        var settings = new CollideShapeSettings
        {
            BackFaceMode = BackFaceMode.CollideWithBackFaces,
            MaxSeparationDistance = 0.01f
        };

        using BroadPhaseLayerFilter broadPhaseFilter = new DefaultBroadPhaseLayerFilter();
        using ObjectLayerFilter objectLayerFilter = new DefaultObjectLayerFilter();
        using BodyFilter bodyFilter = new DefaultBodyFilter();
        using ShapeFilter shapeFilter = new DefaultShapeFilter();

        bool hasCollision = _scene.Physics.NarrowPhaseQuery.CollideShape(
            capsule,
            ref scale,
            ref transform,
            settings,
            ref baseOffset,
            CollisionCollectorType.AllHit,
            results,
            broadPhaseFilter,
            objectLayerFilter,
            bodyFilter,
            shapeFilter);
        if (!hasCollision)
            return true;

        foreach (CollideShapeResult result in results)
        {
            if (result.PenetrationDepth > PlacementPenetrationTolerance)
                return false;
        }

        return true;
    }

    private float MeasureClearance(
        Vector3 bodyPosition,
        Vector3 surfaceNormal,
        Vector3 desiredForward,
        float maxSearchDistance,
        float capsuleHalfExtent,
        BodyID? excludedBody)
    {
        BuildTangentBasis(surfaceNormal, desiredForward, out Vector3 forward, out Vector3 right);
        Vector3 normal = NormalizeOrFallback(surfaceNormal, Vector3.UnitY);
        Vector3[] directions =
        {
            normal,
            forward,
            -forward,
            right,
            -right,
            NormalizeOrFallback(normal * 0.65f + forward * 0.76f, normal),
            NormalizeOrFallback(normal * 0.65f - forward * 0.76f, normal),
            NormalizeOrFallback(normal * 0.65f + right * 0.76f, normal),
            NormalizeOrFallback(normal * 0.65f - right * 0.76f, normal)
        };

        float nearestObstacle = maxSearchDistance;
        foreach (Vector3 direction in directions)
        {
            if (_scene.Raycast(
                    bodyPosition,
                    direction,
                    maxSearchDistance,
                    out SceneRaycastHit hit,
                    excludedBody,
                    collideWithBackFaces: true))
            {
                nearestObstacle = MathF.Min(nearestObstacle, hit.Distance);
            }
        }

        return MathF.Max(0f, nearestObstacle - capsuleHalfExtent);
    }

    private void ConnectNodes(
        SpiderNavGraph graph,
        float maxConnectionDistance,
        float spiderRadius,
        float spiderHeight,
        BodyID? excludedBody)
    {
        var validator = new SpiderNavValidator(
            _scene,
            maxConnectionDistance,
            excludedBody);
        var nodes = new List<SpiderNavNode>();
        foreach (SpiderNavNode node in graph.Nodes)
            nodes.Add(node);

        var buckets = new Dictionary<(int X, int Y, int Z), List<SpiderNavNode>>();
        foreach (SpiderNavNode node in nodes)
        {
            var key = GetCell(node.Position, maxConnectionDistance);
            if (!buckets.TryGetValue(key, out List<SpiderNavNode>? bucket))
            {
                bucket = new List<SpiderNavNode>();
                buckets.Add(key, bucket);
            }

            bucket.Add(node);
        }

        foreach (SpiderNavNode node in nodes)
        {
            (int x, int y, int z) cell = GetCell(node.Position, maxConnectionDistance);
            for (int x = -1; x <= 1; x++)
            {
                for (int y = -1; y <= 1; y++)
                {
                    for (int z = -1; z <= 1; z++)
                    {
                        if (!buckets.TryGetValue((cell.x + x, cell.y + y, cell.z + z), out List<SpiderNavNode>? bucket))
                            continue;

                        foreach (SpiderNavNode other in bucket)
                        {
                            if (other.Id <= node.Id ||
                                Vector3.DistanceSquared(node.Position, other.Position) >
                                maxConnectionDistance * maxConnectionDistance)
                            {
                                continue;
                            }

                            // Edges are directional because a future locomotion
                            // model may permit one transition but not its reverse.
                            validator.TryConnect(
                                graph,
                                node,
                                other,
                                spiderRadius,
                                spiderHeight,
                                out _);
                            validator.TryConnect(
                                graph,
                                other,
                                node,
                                spiderRadius,
                                spiderHeight,
                                out _);
                        }
                    }
                }
            }
        }
    }

    private static (int X, int Y, int Z) GetCell(Vector3 position, float cellSize) =>
        (
            (int)MathF.Floor(position.X / cellSize),
            (int)MathF.Floor(position.Y / cellSize),
            (int)MathF.Floor(position.Z / cellSize));

    private static void ValidateSettings(AABB bounds, SpiderNavGenerationSettings settings)
    {
        Vector3 min = bounds.GetBoundsMin();
        Vector3 max = bounds.GetBoundsMax();
        if (!IsFinite(min) || !IsFinite(max) ||
            max.X <= min.X || max.Y <= min.Y || max.Z <= min.Z)
        {
            throw new ArgumentException("Navigation bounds must be finite and have positive volume.", nameof(bounds));
        }

        if (!float.IsFinite(settings.Spacing) || settings.Spacing <= 0f)
            throw new ArgumentOutOfRangeException(nameof(settings.Spacing));
        if (!float.IsFinite(settings.MaxConnectionDistance) || settings.MaxConnectionDistance <= 0f)
            throw new ArgumentOutOfRangeException(nameof(settings.MaxConnectionDistance));
        if (!float.IsFinite(settings.SpiderRadius) || settings.SpiderRadius <= 0f)
            throw new ArgumentOutOfRangeException(nameof(settings.SpiderRadius));
        if (!float.IsFinite(settings.SpiderHeight) || settings.SpiderHeight <= 0f)
            throw new ArgumentOutOfRangeException(nameof(settings.SpiderHeight));
        if (!float.IsFinite(settings.MaxSurfaceSearchDistance) || settings.MaxSurfaceSearchDistance <= 0f)
            throw new ArgumentOutOfRangeException(nameof(settings.MaxSurfaceSearchDistance));
    }

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) &&
        float.IsFinite(value.Y) &&
        float.IsFinite(value.Z);

    private static void BuildTangentBasis(
        Vector3 normal,
        Vector3 desiredForward,
        out Vector3 forward,
        out Vector3 right)
    {
        normal = NormalizeOrFallback(normal, Vector3.UnitY);
        forward = ProjectOnPlane(desiredForward, normal);
        if (forward.LengthSquared() <= Epsilon * Epsilon)
        {
            Vector3 reference = MathF.Abs(Vector3.Dot(normal, Vector3.UnitY)) < 0.95f
                ? Vector3.UnitY
                : Vector3.UnitX;
            forward = ProjectOnPlane(reference, normal);
        }

        forward = NormalizeOrFallback(forward, Vector3.UnitZ);
        right = NormalizeOrFallback(Vector3.Cross(normal, forward), Vector3.UnitX);
        forward = NormalizeOrFallback(Vector3.Cross(right, normal), forward);
    }

    private static Matrix4x4 CreateSurfaceTransform(
        Vector3 position,
        Vector3 surfaceNormal,
        Vector3 desiredForward)
    {
        BuildTangentBasis(surfaceNormal, desiredForward, out Vector3 forward, out Vector3 right);
        Vector3 normal = NormalizeOrFallback(surfaceNormal, Vector3.UnitY);
        Matrix4x4 transform = new(
            right.X, right.Y, right.Z, 0f,
            normal.X, normal.Y, normal.Z, 0f,
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

}
