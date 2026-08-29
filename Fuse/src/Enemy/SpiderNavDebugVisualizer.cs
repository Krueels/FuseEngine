using System;
using System.Numerics;
using Fuse.Debug;

namespace Fuse.Enemy;

/// <summary>
/// Optional debug visualization for a baked spider navigation graph.
/// It uses the engine's existing DebugDrawer and creates no rendering path.
/// Keep an instance alive while the graph should remain visible.
/// </summary>
public sealed class SpiderNavDebugVisualizer : IGizmoDrawable
{
    private static readonly Vector3 GraphNodeColor = new(0.15f, 0.9f, 0.3f);
    private static readonly Vector3 IsolatedNodeColor = new(0.9f, 0.25f, 0.15f);
    private static readonly Vector3 GraphNormalColor = new(1f, 0.85f, 0.15f);
    private static readonly Vector3 TransitionEdgeColor = new(1f, 0.55f, 0.10f);
    private static readonly Vector3 RegularEdgeColor = new(0.10f, 0.65f, 1f);
    private static readonly Vector3 PathLineColor = new(1f, 0.05f, 1f);
    private static readonly Vector3 PathNormalColor = new(1f, 0.85f, 0.05f);
    private static readonly Vector3 StartColor = new(0.05f, 1f, 0.15f);
    private static readonly Vector3 GoalColor = new(1f, 0.1f, 0.05f);
    private static readonly Vector3 StartAndGoalColor = new(1f, 0.05f, 0.8f);

    private SpiderPath? _path;

    public SpiderNavDebugVisualizer(SpiderNavGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        Graph = graph;
        DebugDrawer.Register(this);
    }

    public SpiderNavGraph Graph { get; }
    public bool Enabled { get; set; } = true;
    public float NodeRadius { get; set; } = 0.08f;
    public float NormalLength { get; set; } = 0.30f;
    public SpiderPath? Path => _path;

    /// <summary>
    /// Sets the path to overlay on the graph. Passing null clears the overlay.
    /// </summary>
    public void SetPath(SpiderPath? path)
    {
        _path = path;
    }

    public void OnDrawGizmos(DebugDrawer drawer)
    {
        if (!Enabled)
            return;

        int? startNodeId = _path is { IsEmpty: false }
            ? _path.Waypoints[0].Id
            : null;
        int? goalNodeId = _path is { IsEmpty: false }
            ? _path.Waypoints[^1].Id
            : null;

        foreach (SpiderNavNode node in Graph.Nodes)
        {
            bool isStart = startNodeId == node.Id;
            bool isGoal = goalNodeId == node.Id;
            Vector3 nodeColor = isStart && isGoal
                ? StartAndGoalColor
                : isStart
                    ? StartColor
                    : isGoal
                        ? GoalColor
                        : node.Edges.Count > 0
                            ? GraphNodeColor
                            : IsolatedNodeColor;
            float radius = isStart || isGoal ? NodeRadius * 1.8f : NodeRadius;
            drawer.DrawSphere(node.Position, Quaternion.Identity, radius, nodeColor);
            drawer.PushLine(
                node.Position,
                node.Position + node.SurfaceNormal * NormalLength,
                GraphNormalColor);

            foreach (SpiderNavEdge edge in node.Edges)
            {
                if (!Graph.TryGetNode(edge.ToNodeId, out SpiderNavNode? target) || target == null)
                    continue;

                Vector3 edgeColor = edge.IsSurfaceTransition
                    ? TransitionEdgeColor
                    : RegularEdgeColor;
                drawer.PushLine(node.Position, target.Position, edgeColor);
            }
        }

        if (_path is not { IsEmpty: false })
            return;

        for (int i = 0; i < _path.Waypoints.Count; i++)
        {
            SpiderNavNode waypoint = _path.Waypoints[i];
            drawer.DrawSphere(
                waypoint.Position,
                Quaternion.Identity,
                NodeRadius * 1.35f,
                i == 0
                    ? StartColor
                    : i == _path.Waypoints.Count - 1
                        ? GoalColor
                        : PathLineColor);
            drawer.PushLine(
                waypoint.Position,
                waypoint.Position + waypoint.SurfaceNormal * (NormalLength * 1.5f),
                PathNormalColor);

            if (i == 0)
                continue;

            drawer.PushLine(
                _path.Waypoints[i - 1].Position,
                waypoint.Position,
                PathLineColor);
        }
    }
}
