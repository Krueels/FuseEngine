using System;
using System.Collections.Generic;
using System.Numerics;
using Fuse.Debug;

namespace Fuse.Enemy;

/// <summary>
/// Removes intermediate waypoints only when the spider can physically travel
/// directly between the remaining endpoints. It does not interpolate points
/// or alter surface positions.
/// </summary>
public sealed class SpiderPathSmoother : IGizmoDrawable
{
    private const float Epsilon = 0.0001f;
    private const float DegreesToRadians = MathF.PI / 180f;

    private static readonly Vector3 RawPathColor = new(1f, 0.40f, 0.10f);
    private static readonly Vector3 SmoothedPathColor = new(1f, 0.05f, 0.85f);
    private static readonly Vector3 RawNormalColor = new(1f, 0.65f, 0.15f);
    private static readonly Vector3 SmoothedNormalColor = new(0.35f, 1f, 0.85f);

    private readonly SpiderNavValidator _validator;
    private readonly float _spiderRadius;
    private readonly float _spiderHeight;

    public SpiderPathSmoother(
        SpiderNavValidator validator,
        float spiderRadius,
        float spiderHeight)
    {
        ArgumentNullException.ThrowIfNull(validator);
        ValidateBodyDimensions(spiderRadius, spiderHeight);

        _validator = validator;
        _spiderRadius = spiderRadius;
        _spiderHeight = spiderHeight;
        DebugDrawer.Register(this);
    }

    /// <summary>
    /// A shortcut is rejected when the endpoint normals, or any transition it
    /// would erase, changes by more than this angle. This keeps important
    /// floor/wall, wall/ceiling and corner waypoints in the route.
    /// </summary>
    public float MaxShortcutNormalAngle
    {
        get => _maxShortcutNormalAngle;
        set
        {
            if (!float.IsFinite(value) || value <= 0f || value > 180f)
                throw new ArgumentOutOfRangeException(nameof(value));
            _maxShortcutNormalAngle = value;
        }
    }

    public bool DebugEnabled { get; set; } = true;
    public bool ShowRawPath { get; set; } = true;
    public bool ShowSmoothedPath { get; set; } = true;
    public float DebugWaypointRadius { get; set; } = 0.085f;
    public float DebugNormalLength { get; set; } = 0.30f;

    public SpiderPath LastRawPath { get; private set; } = SpiderPath.Empty;
    public SpiderPath LastSmoothedPath { get; private set; } = SpiderPath.Empty;
    public int RawWaypointCount => LastRawPath.Count;
    public int SmoothedWaypointCount => LastSmoothedPath.Count;
    public int LastRemovedWaypointCount => System.Math.Max(0, RawWaypointCount - SmoothedWaypointCount);
    public float LastReductionRatio => RawWaypointCount == 0
        ? 0f
        : (RawWaypointCount - SmoothedWaypointCount) / (float)RawWaypointCount;

    private float _maxShortcutNormalAngle = 45f;

    /// <summary>
    /// Greedily keeps the current anchor and searches from the far end of the
    /// remaining route for the furthest valid direct connection. If none is
    /// valid, the next original waypoint becomes the new anchor.
    /// </summary>
    public SpiderPath Smooth(
        SpiderNavGraph graph,
        SpiderPath originalPath,
        float requiredClearance)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(originalPath);
        ValidateClearance(requiredClearance);

        LastRawPath = originalPath;
        LastSmoothedPath = originalPath;

        if (originalPath.Count <= 2)
            return originalPath;

        var nodes = new List<SpiderNavNode>(originalPath.Count);
        foreach (SpiderNavNode waypoint in originalPath.Waypoints)
        {
            if (!graph.TryGetNode(waypoint.Id, out SpiderNavNode? graphNode) || graphNode == null)
                return originalPath;

            nodes.Add(graphNode);
        }

        var smoothedNodes = new List<SpiderNavNode>(nodes.Count)
        {
            nodes[0]
        };

        int anchorIndex = 0;
        while (anchorIndex < nodes.Count - 1)
        {
            int selectedIndex = anchorIndex + 1;
            for (int candidateIndex = nodes.Count - 1;
                 candidateIndex > anchorIndex + 1;
                 candidateIndex--)
            {
                if (!CanShortcut(
                        nodes,
                        anchorIndex,
                        candidateIndex,
                        requiredClearance))
                {
                    continue;
                }

                selectedIndex = candidateIndex;
                break;
            }

            smoothedNodes.Add(nodes[selectedIndex]);
            anchorIndex = selectedIndex;
        }

        if (smoothedNodes.Count == originalPath.Count)
        {
            LastSmoothedPath = originalPath;
            return originalPath;
        }

        float totalCost = CalculateCost(smoothedNodes);
        LastSmoothedPath = new SpiderPath(smoothedNodes, totalCost);
        return LastSmoothedPath;
    }

    public void OnDrawGizmos(DebugDrawer drawer)
    {
        if (!DebugEnabled)
            return;

        if (ShowRawPath)
            DrawPath(drawer, LastRawPath, RawPathColor, RawNormalColor, 1f);
        if (ShowSmoothedPath)
            DrawPath(drawer, LastSmoothedPath, SmoothedPathColor, SmoothedNormalColor, 1.35f);
    }

    private bool CanShortcut(
        IReadOnlyList<SpiderNavNode> nodes,
        int fromIndex,
        int toIndex,
        float requiredClearance)
    {
        SpiderNavNode from = nodes[fromIndex];
        SpiderNavNode to = nodes[toIndex];
        if (!HasRequiredClearance(from, requiredClearance) ||
            !HasRequiredClearance(to, requiredClearance))
        {
            return false;
        }

        float distance = Vector3.Distance(from.Position, to.Position);
        if (!float.IsFinite(distance) ||
            distance <= Epsilon ||
            distance > _validator.MaxConnectionDistance + Epsilon)
        {
            return false;
        }

        if (ContainsImportantTransition(nodes, fromIndex, toIndex))
            return false;

        // CanConnect performs the directional support samples and capsule
        // casts, so a successful shortcut cannot simply cut through geometry.
        return _validator.CanConnect(from, to, _spiderRadius, _spiderHeight);
    }

    private bool ContainsImportantTransition(
        IReadOnlyList<SpiderNavNode> nodes,
        int fromIndex,
        int toIndex)
    {
        float maximumAngleRadians = _maxShortcutNormalAngle * DegreesToRadians;
        for (int i = fromIndex + 1; i <= toIndex; i++)
        {
            float angle = NormalAngle(nodes[i - 1].SurfaceNormal, nodes[i].SurfaceNormal);
            if (angle > maximumAngleRadians + Epsilon)
                return true;
        }

        return NormalAngle(nodes[fromIndex].SurfaceNormal, nodes[toIndex].SurfaceNormal) >
               maximumAngleRadians + Epsilon;
    }

    private static bool HasRequiredClearance(SpiderNavNode node, float requiredClearance) =>
        node.Clearance + Epsilon >= requiredClearance;

    private static float CalculateCost(IReadOnlyList<SpiderNavNode> nodes)
    {
        float totalCost = 0f;
        for (int i = 1; i < nodes.Count; i++)
            totalCost += Vector3.Distance(nodes[i - 1].Position, nodes[i].Position);
        return totalCost;
    }

    private void DrawPath(
        DebugDrawer drawer,
        SpiderPath path,
        Vector3 lineColor,
        Vector3 normalColor,
        float radiusScale)
    {
        if (path.IsEmpty)
            return;

        for (int i = 0; i < path.Count; i++)
        {
            SpiderNavNode waypoint = path.Waypoints[i];
            float radius = DebugWaypointRadius * radiusScale;
            drawer.DrawSphere(
                waypoint.Position,
                Quaternion.Identity,
                radius,
                lineColor);
            drawer.PushLine(
                waypoint.Position,
                waypoint.Position + waypoint.SurfaceNormal * DebugNormalLength,
                normalColor);

            if (i > 0)
            {
                drawer.PushLine(
                    path.Waypoints[i - 1].Position,
                    waypoint.Position,
                    lineColor);
            }
        }
    }

    private static float NormalAngle(Vector3 first, Vector3 second)
    {
        float dot = System.Math.Clamp(Vector3.Dot(first, second), -1f, 1f);
        return MathF.Acos(dot);
    }

    private static void ValidateBodyDimensions(float radius, float height)
    {
        if (!float.IsFinite(radius) || radius <= 0f)
            throw new ArgumentOutOfRangeException(nameof(radius));
        if (!float.IsFinite(height) || height <= 0f)
            throw new ArgumentOutOfRangeException(nameof(height));
    }

    private static void ValidateClearance(float clearance)
    {
        if (!float.IsFinite(clearance) || clearance < 0f)
            throw new ArgumentOutOfRangeException(nameof(clearance));
    }
}
