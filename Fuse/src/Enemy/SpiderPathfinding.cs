using System;
using System.Collections.Generic;
using System.Numerics;
using Fuse.Scene;
using JoltPhysicsSharp;

namespace Fuse.Enemy;

/// <summary>
/// Resolves world positions to reachable graph nodes and delegates the final
/// route search to SpiderAStar. It does not move the spider or generate nodes.
/// </summary>
public sealed class SpiderPathfinding
{
    private const float Epsilon = 0.0001f;
    private const float AlreadyAtNodeDistance = 0.10f;
    private const float SurfaceProbeOffset = 0.12f;
    private const float SurfaceDistanceTolerance = 0.50f;
    private const float MinimumSurfaceAlignment = 0.35f;
    private const int MaximumNormalsToProbe = 32;
    private const float NormalPenaltyWeight = 2.0f;
    private const float ClearancePenaltyWeight = 0.15f;

    private readonly SceneManager _scene;
    private readonly SpiderAStar _aStar;
    private readonly SpiderNavValidator _reachabilityValidator;
    private readonly float _spiderRadius;
    private readonly float _spiderHeight;
    private readonly BodyID? _excludedBody;
    private SpiderNavGraph? _indexedGraph;
    private SpiderNavSpatialIndex? _spatialIndex;
    private int _spatialIndexNodeCount = -1;

    public SpiderPathfinding(
        SceneManager scene,
        float spiderRadius,
        float spiderHeight,
        float maxNodeSearchDistance = 4f,
        BodyID? excludedBody = null)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ValidateBodyDimensions(spiderRadius, spiderHeight);
        if (!float.IsFinite(maxNodeSearchDistance) || maxNodeSearchDistance <= 0f)
            throw new ArgumentOutOfRangeException(nameof(maxNodeSearchDistance));

        _scene = scene;
        _aStar = new SpiderAStar();
        _reachabilityValidator = new SpiderNavValidator(
            scene,
            maxNodeSearchDistance,
            excludedBody);
        _spiderRadius = spiderRadius;
        _spiderHeight = spiderHeight;
        _excludedBody = excludedBody;
        MaxNodeSearchDistance = maxNodeSearchDistance;
    }

    public float MaxNodeSearchDistance { get; }

    public bool TryFindPath(
        SpiderNavGraph graph,
        Vector3 startWorldPosition,
        Vector3 goalWorldPosition,
        float requiredClearance,
        out SpiderPath path)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ValidateWorldPosition(startWorldPosition, nameof(startWorldPosition));
        ValidateWorldPosition(goalWorldPosition, nameof(goalWorldPosition));
        ValidateClearance(requiredClearance);

        Vector3? inferredStartNormal = TryInferSurfaceNormal(
            graph,
            startWorldPosition,
            requiredClearance);
        return TryFindPath(
            graph,
            startWorldPosition,
            goalWorldPosition,
            requiredClearance,
            inferredStartNormal,
            out path);
    }

    /// <summary>
    /// Variant for callers that already know the spider's current surface
    /// normal. This is intended for later locomotion integration.
    /// </summary>
    public bool TryFindPath(
        SpiderNavGraph graph,
        Vector3 startWorldPosition,
        Vector3 goalWorldPosition,
        float requiredClearance,
        Vector3? preferredStartSurfaceNormal,
        out SpiderPath path)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ValidateWorldPosition(startWorldPosition, nameof(startWorldPosition));
        ValidateWorldPosition(goalWorldPosition, nameof(goalWorldPosition));
        ValidateClearance(requiredClearance);
        path = SpiderPath.Empty;

        if (!FindNearestNode(
                graph,
                startWorldPosition,
                requiredClearance,
                preferredStartSurfaceNormal,
                out SpiderNavNode? startNode) ||
            !FindNearestNode(
                graph,
                goalWorldPosition,
                requiredClearance,
                preferredSurfaceNormal: null,
                out SpiderNavNode? goalNode) ||
            startNode == null ||
            goalNode == null)
        {
            return false;
        }

        return _aStar.TryFindPath(
            graph,
            startNode.Id,
            goalNode.Id,
            requiredClearance,
            out path);
    }

    public bool FindNearestNode(
        SpiderNavGraph graph,
        Vector3 worldPosition,
        float requiredClearance,
        out SpiderNavNode? node)
    {
        return FindNearestNode(
            graph,
            worldPosition,
            requiredClearance,
            preferredSurfaceNormal: null,
            out node);
    }

    /// <summary>
    /// Finds the best reachable node, not merely the closest node. Candidates
    /// are filtered by clearance, scored by distance, optional normal
    /// continuity and available clearance, then physically tested in score
    /// order.
    /// </summary>
    public bool FindNearestNode(
        SpiderNavGraph graph,
        Vector3 worldPosition,
        float requiredClearance,
        Vector3? preferredSurfaceNormal,
        out SpiderNavNode? node)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ValidateWorldPosition(worldPosition, nameof(worldPosition));
        ValidateClearance(requiredClearance);
        node = null;

        EnsureSpatialIndex(graph);
        if (_spatialIndex == null)
            return false;

        Vector3? preferredNormal = preferredSurfaceNormal.HasValue
            ? NormalizeNormal(preferredSurfaceNormal.Value, nameof(preferredSurfaceNormal))
            : null;
        var candidates = new List<NodeCandidate>();

        foreach (SpiderNavNode candidate in _spatialIndex.Query(worldPosition, MaxNodeSearchDistance))
        {
            if (candidate.Clearance + Epsilon < requiredClearance ||
                candidate.Clearance + Epsilon < _spiderRadius)
            {
                continue;
            }

            float distance = Vector3.Distance(worldPosition, candidate.Position);
            float normalPenalty = 0f;
            if (preferredNormal.HasValue)
            {
                float agreement = System.Math.Clamp(
                    Vector3.Dot(preferredNormal.Value, candidate.SurfaceNormal),
                    -1f,
                    1f);
                normalPenalty = (1f - agreement) * MaxNodeSearchDistance * NormalPenaltyWeight;
            }

            float clearanceMargin = MathF.Max(0f, candidate.Clearance - requiredClearance);
            float clearancePenalty = ClearancePenaltyWeight / (0.25f + clearanceMargin);
            float score = distance + normalPenalty + clearancePenalty;
            candidates.Add(new NodeCandidate(candidate, distance, score));
        }

        candidates.Sort(static (left, right) => left.Score.CompareTo(right.Score));
        foreach (NodeCandidate candidate in candidates)
        {
            if (!CanReachNode(
                    worldPosition,
                    candidate.Node,
                    preferredNormal,
                    requiredClearance,
                    candidate.Distance))
            {
                continue;
            }

            node = candidate.Node;
            return true;
        }

        return false;
    }

    private bool CanReachNode(
        Vector3 worldPosition,
        SpiderNavNode candidate,
        Vector3? preferredNormal,
        float requiredClearance,
        float distance)
    {
        if (distance <= AlreadyAtNodeDistance)
            return true;

        Vector3 connectionNormal = preferredNormal ?? candidate.SurfaceNormal;
        int temporaryNodeId = int.MaxValue;
        if (candidate.Id == temporaryNodeId)
            temporaryNodeId = int.MaxValue - 1;

        var temporaryNode = new SpiderNavNode(
            temporaryNodeId,
            worldPosition,
            connectionNormal,
            MathF.Max(requiredClearance, _spiderRadius));

        return _reachabilityValidator.CanConnect(
            temporaryNode,
            candidate,
            _spiderRadius,
            _spiderHeight);
    }

    private Vector3? TryInferSurfaceNormal(
        SpiderNavGraph graph,
        Vector3 worldPosition,
        float requiredClearance)
    {
        EnsureSpatialIndex(graph);
        if (_spatialIndex == null)
            return null;

        float minimumClearance = MathF.Max(requiredClearance, _spiderRadius);
        float bestDistance = float.MaxValue;
        Vector3? bestNormal = null;
        var candidates = new List<SpiderNavNode>();

        foreach (SpiderNavNode candidate in _spatialIndex.Query(worldPosition, MaxNodeSearchDistance))
        {
            if (candidate.Clearance + Epsilon < minimumClearance)
                continue;

            candidates.Add(candidate);
        }

        candidates.Sort((left, right) =>
            Vector3.DistanceSquared(left.Position, worldPosition)
                .CompareTo(Vector3.DistanceSquared(right.Position, worldPosition)));

        var testedNormals = new List<Vector3>(8);
        foreach (SpiderNavNode candidate in candidates)
        {
            bool alreadyTested = false;
            foreach (Vector3 testedNormal in testedNormals)
            {
                if (Vector3.Dot(testedNormal, candidate.SurfaceNormal) >= 0.995f)
                {
                    alreadyTested = true;
                    break;
                }
            }

            if (alreadyTested)
                continue;

            if (testedNormals.Count >= MaximumNormalsToProbe)
                break;
            testedNormals.Add(candidate.SurfaceNormal);

            if (!HasSupportAtPosition(worldPosition, candidate.SurfaceNormal))
                continue;

            float distance = Vector3.DistanceSquared(worldPosition, candidate.Position);
            if (distance >= bestDistance)
                continue;

            bestDistance = distance;
            bestNormal = candidate.SurfaceNormal;
        }

        return bestNormal;
    }

    private bool HasSupportAtPosition(Vector3 position, Vector3 surfaceNormal)
    {
        float capsuleHalfExtent = _spiderHeight * 0.5f + _spiderRadius;
        Vector3 rayOrigin = position + surfaceNormal * SurfaceProbeOffset;
        float expectedDistance = capsuleHalfExtent + SurfaceProbeOffset;
        float maxDistance = expectedDistance + SurfaceDistanceTolerance;

        if (!_scene.Raycast(
                rayOrigin,
                -surfaceNormal,
                maxDistance,
                out SceneRaycastHit hit,
                _excludedBody,
                collideWithBackFaces: true))
        {
            return false;
        }

        return hit.Distance >= expectedDistance - SurfaceDistanceTolerance &&
               hit.Distance <= expectedDistance + SurfaceDistanceTolerance &&
               Vector3.Dot(hit.Normal, surfaceNormal) >= MinimumSurfaceAlignment;
    }

    private void EnsureSpatialIndex(SpiderNavGraph graph)
    {
        if (ReferenceEquals(_indexedGraph, graph) &&
            _spatialIndex != null &&
            _spatialIndexNodeCount == graph.Count)
            return;

        _spatialIndex = SpiderNavSpatialIndex.FromGraph(graph, MaxNodeSearchDistance);
        _indexedGraph = graph;
        _spatialIndexNodeCount = graph.Count;
    }

    private static Vector3 NormalizeNormal(Vector3 normal, string parameterName)
    {
        if (!float.IsFinite(normal.X) ||
            !float.IsFinite(normal.Y) ||
            !float.IsFinite(normal.Z) ||
            normal.LengthSquared() <= Epsilon * Epsilon)
        {
            throw new ArgumentException("Surface normal must be a non-zero finite vector.", parameterName);
        }

        return Vector3.Normalize(normal);
    }

    private static void ValidateBodyDimensions(float radius, float height)
    {
        if (!float.IsFinite(radius) || radius <= 0f)
            throw new ArgumentOutOfRangeException(nameof(radius));
        if (!float.IsFinite(height) || height <= 0f)
            throw new ArgumentOutOfRangeException(nameof(height));
    }

    private static void ValidateWorldPosition(Vector3 position, string parameterName)
    {
        if (!float.IsFinite(position.X) ||
            !float.IsFinite(position.Y) ||
            !float.IsFinite(position.Z))
        {
            throw new ArgumentException("World position must contain finite values.", parameterName);
        }
    }

    private static void ValidateClearance(float clearance)
    {
        if (!float.IsFinite(clearance) || clearance < 0f)
            throw new ArgumentOutOfRangeException(nameof(clearance));
    }

    private readonly struct NodeCandidate
    {
        public NodeCandidate(SpiderNavNode node, float distance, float score)
        {
            Node = node;
            Distance = distance;
            Score = score;
        }

        public SpiderNavNode Node { get; }
        public float Distance { get; }
        public float Score { get; }
    }
}
