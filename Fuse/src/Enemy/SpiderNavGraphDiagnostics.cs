using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Fuse.Enemy;

/// <summary>
/// Structural information about a navigation graph. Connected components are
/// intentionally calculated as weak components: a directed edge connects its
/// two nodes for topology diagnostics, regardless of travel direction.
/// </summary>
public sealed class SpiderNavGraphDiagnostics
{
    private SpiderNavGraphDiagnostics(
        int nodeCount,
        int edgeCount,
        int invalidEdgeCount,
        int isolatedNodeCount,
        int deadEndNodeCount,
        int connectedComponentCount,
        int largestComponentSize,
        IReadOnlyList<int> isolatedNodeIds,
        IReadOnlyList<int> componentSizes)
    {
        NodeCount = nodeCount;
        EdgeCount = edgeCount;
        InvalidEdgeCount = invalidEdgeCount;
        IsolatedNodeCount = isolatedNodeCount;
        DeadEndNodeCount = deadEndNodeCount;
        ConnectedComponentCount = connectedComponentCount;
        LargestComponentSize = largestComponentSize;
        IsolatedNodeIds = isolatedNodeIds;
        ComponentSizes = componentSizes;
    }

    public int NodeCount { get; }
    public int EdgeCount { get; }
    public int InvalidEdgeCount { get; }
    public int IsolatedNodeCount { get; }
    public int DeadEndNodeCount { get; }
    public int ConnectedComponentCount { get; }
    public int LargestComponentSize { get; }
    public bool HasDisconnectedComponents => ConnectedComponentCount > 1;
    public IReadOnlyList<int> IsolatedNodeIds { get; }
    public IReadOnlyList<int> ComponentSizes { get; }

    public static SpiderNavGraphDiagnostics Analyze(SpiderNavGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);

        var nodes = new List<SpiderNavNode>();
        var neighbors = new Dictionary<int, HashSet<int>>();
        var incoming = new HashSet<int>();
        int edgeCount = 0;
        int invalidEdgeCount = 0;

        foreach (SpiderNavNode node in graph.Nodes)
        {
            nodes.Add(node);
            neighbors[node.Id] = new HashSet<int>();
        }

        foreach (SpiderNavNode node in nodes)
        {
            foreach (SpiderNavEdge edge in node.Edges)
            {
                edgeCount++;
                if (!graph.TryGetNode(edge.ToNodeId, out SpiderNavNode? target) || target == null)
                {
                    invalidEdgeCount++;
                    continue;
                }

                neighbors[node.Id].Add(target.Id);
                neighbors[target.Id].Add(node.Id);
                incoming.Add(target.Id);
            }
        }

        var isolatedNodeIds = new List<int>();
        int deadEndNodeCount = 0;
        foreach (SpiderNavNode node in nodes)
        {
            if (node.Edges.Count == 0)
                deadEndNodeCount++;

            if (node.Edges.Count == 0 && !incoming.Contains(node.Id))
                isolatedNodeIds.Add(node.Id);
        }

        var visited = new HashSet<int>();
        var componentSizes = new List<int>();
        foreach (SpiderNavNode node in nodes)
        {
            if (!visited.Add(node.Id))
                continue;

            int componentSize = 0;
            var pending = new Stack<int>();
            pending.Push(node.Id);

            while (pending.Count > 0)
            {
                int currentId = pending.Pop();
                componentSize++;

                foreach (int neighborId in neighbors[currentId])
                {
                    if (visited.Add(neighborId))
                        pending.Push(neighborId);
                }
            }

            componentSizes.Add(componentSize);
        }

        componentSizes.Sort((left, right) => right.CompareTo(left));
        int largestComponentSize = componentSizes.Count > 0 ? componentSizes[0] : 0;

        return new SpiderNavGraphDiagnostics(
            nodes.Count,
            edgeCount,
            invalidEdgeCount,
            isolatedNodeIds.Count,
            deadEndNodeCount,
            componentSizes.Count,
            largestComponentSize,
            new ReadOnlyCollection<int>(isolatedNodeIds),
            new ReadOnlyCollection<int>(componentSizes));
    }

    public override string ToString() =>
        $"nodes={NodeCount}, edges={EdgeCount}, isolated={IsolatedNodeCount}, " +
        $"deadEnds={DeadEndNodeCount}, components={ConnectedComponentCount}, " +
        $"largestComponent={LargestComponentSize}, invalidEdges={InvalidEdgeCount}";
}
