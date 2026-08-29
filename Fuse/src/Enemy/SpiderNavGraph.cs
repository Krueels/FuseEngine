using System;
using System.Collections.Generic;

namespace Fuse.Enemy;

/// <summary>
/// In-memory navigation graph used by the first A* implementation.
/// Nodes can be filled by a future precomputed asset loader or graph builder;
/// pathfinding does not depend on how the graph was created.
/// </summary>
public sealed class SpiderNavGraph
{
    private readonly Dictionary<int, SpiderNavNode> _nodes = new();

    public int Count => _nodes.Count;
    public IEnumerable<SpiderNavNode> Nodes => _nodes.Values;

    public void AddNode(SpiderNavNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (!_nodes.TryAdd(node.Id, node))
            throw new ArgumentException($"A navigation node with ID {node.Id} already exists.", nameof(node));
    }

    public bool TryGetNode(int nodeId, out SpiderNavNode? node) =>
        _nodes.TryGetValue(nodeId, out node);

    public void AddEdge(SpiderNavEdge edge)
    {
        if (!_nodes.TryGetValue(edge.FromNodeId, out SpiderNavNode? fromNode))
        {
            throw new ArgumentException(
                $"Cannot add an edge from missing node {edge.FromNodeId}.",
                nameof(edge));
        }

        if (!_nodes.ContainsKey(edge.ToNodeId))
        {
            throw new ArgumentException(
                $"Cannot add an edge to missing node {edge.ToNodeId}.",
                nameof(edge));
        }

        fromNode.AddEdge(edge);
    }
}
