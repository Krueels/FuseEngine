using System;
using System.Collections.Generic;
using System.Numerics;

namespace Fuse.Enemy;

/// <summary>
/// A* over an already-built spider navigation graph.
/// This class has no physics or locomotion dependency: an edge is considered
/// traversable because the graph provider already validated it.
/// </summary>
public sealed class SpiderAStar
{
    private const float Epsilon = 0.0001f;

    /// <summary>
    /// Finds a path between two graph nodes, including both endpoints in the
    /// returned waypoint list.
    /// </summary>
    public bool TryFindPath(
        SpiderNavGraph graph,
        int startNodeId,
        int goalNodeId,
        float requiredClearance,
        out SpiderPath path)
    {
        ArgumentNullException.ThrowIfNull(graph);
        if (!float.IsFinite(requiredClearance) || requiredClearance < 0f)
            throw new ArgumentOutOfRangeException(nameof(requiredClearance));

        path = SpiderPath.Empty;

        if (!graph.TryGetNode(startNodeId, out SpiderNavNode? start) ||
            !graph.TryGetNode(goalNodeId, out SpiderNavNode? goal) ||
            start == null ||
            goal == null ||
            !IsNodeUsable(start, requiredClearance) ||
            !IsNodeUsable(goal, requiredClearance))
        {
            return false;
        }

        if (startNodeId == goalNodeId)
        {
            path = new SpiderPath(new[] { start }, 0f);
            return true;
        }

        var open = new PriorityQueue<OpenEntry, float>();
        var gScore = new Dictionary<int, float>
        {
            [startNodeId] = 0f
        };
        var cameFrom = new Dictionary<int, int>();

        open.Enqueue(
            new OpenEntry(startNodeId, 0f),
            Heuristic(start, goal));

        while (open.TryDequeue(out OpenEntry current, out _))
        {
            if (!gScore.TryGetValue(current.NodeId, out float knownScore) ||
                current.GScore > knownScore + Epsilon)
            {
                // A better route to this node was queued after this entry.
                continue;
            }

            if (current.NodeId == goalNodeId)
            {
                path = BuildPath(graph, startNodeId, goalNodeId, cameFrom, knownScore);
                return !path.IsEmpty;
            }

            if (!graph.TryGetNode(current.NodeId, out SpiderNavNode? currentNode) || currentNode == null)
                continue;

            foreach (SpiderNavEdge edge in currentNode.Edges)
            {
                if (edge.FromNodeId != currentNode.Id ||
                    !edge.SupportsClearance(requiredClearance) ||
                    !graph.TryGetNode(edge.ToNodeId, out SpiderNavNode? neighbor) ||
                    neighbor == null ||
                    !IsNodeUsable(neighbor, requiredClearance))
                {
                    continue;
                }

                // Navigation costs should normally be at least the physical
                // distance. Clamping here keeps the Euclidean heuristic
                // admissible even for a manually authored edge with cost 0.
                float physicalDistance = Vector3.Distance(currentNode.Position, neighbor.Position);
                float edgeCost = MathF.Max(edge.Cost, physicalDistance);
                float tentativeGScore = knownScore + edgeCost;

                if (gScore.TryGetValue(neighbor.Id, out float previousGScore) &&
                    tentativeGScore >= previousGScore - Epsilon)
                {
                    continue;
                }

                cameFrom[neighbor.Id] = currentNode.Id;
                gScore[neighbor.Id] = tentativeGScore;
                float priority = tentativeGScore + Heuristic(neighbor, goal);
                open.Enqueue(new OpenEntry(neighbor.Id, tentativeGScore), priority);
            }
        }

        return false;
    }

    private static SpiderPath BuildPath(
        SpiderNavGraph graph,
        int startNodeId,
        int goalNodeId,
        Dictionary<int, int> cameFrom,
        float totalCost)
    {
        var nodeIds = new List<int> { goalNodeId };
        int currentNodeId = goalNodeId;

        while (currentNodeId != startNodeId)
        {
            if (!cameFrom.TryGetValue(currentNodeId, out currentNodeId))
                return SpiderPath.Empty;

            nodeIds.Add(currentNodeId);
        }

        nodeIds.Reverse();
        var nodes = new List<SpiderNavNode>(nodeIds.Count);
        foreach (int nodeId in nodeIds)
        {
            if (!graph.TryGetNode(nodeId, out SpiderNavNode? node) || node == null)
                return SpiderPath.Empty;

            nodes.Add(node);
        }

        return new SpiderPath(nodes, totalCost);
    }

    private static bool IsNodeUsable(SpiderNavNode node, float requiredClearance) =>
        node.Clearance + Epsilon >= requiredClearance;

    private static float Heuristic(SpiderNavNode from, SpiderNavNode goal) =>
        Vector3.Distance(from.Position, goal.Position);

    private readonly struct OpenEntry
    {
        public OpenEntry(int nodeId, float gScore)
        {
            NodeId = nodeId;
            GScore = gScore;
        }

        public int NodeId { get; }
        public float GScore { get; }
    }
}
