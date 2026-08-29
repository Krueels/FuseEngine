using System;

namespace Fuse.Enemy;

/// <summary>
/// Directed connection between two nodes of the spider navigation graph.
/// The graph builder is responsible for creating an edge only after the
/// spider body has been validated along the whole connection.
/// </summary>
public readonly struct SpiderNavEdge
{
    public SpiderNavEdge(
        int fromNodeId,
        int toNodeId,
        float cost,
        float minimumClearance,
        bool isSurfaceTransition = false)
    {
        if (fromNodeId < 0)
            throw new ArgumentOutOfRangeException(nameof(fromNodeId));
        if (toNodeId < 0)
            throw new ArgumentOutOfRangeException(nameof(toNodeId));
        if (!float.IsFinite(cost) || cost < 0f)
            throw new ArgumentOutOfRangeException(nameof(cost));
        if (!float.IsFinite(minimumClearance) || minimumClearance < 0f)
            throw new ArgumentOutOfRangeException(nameof(minimumClearance));

        FromNodeId = fromNodeId;
        ToNodeId = toNodeId;
        Cost = cost;
        MinimumClearance = minimumClearance;
        IsSurfaceTransition = isSurfaceTransition;
    }

    public int FromNodeId { get; }
    public int ToNodeId { get; }
    public float Cost { get; }
    public float MinimumClearance { get; }
    public bool IsSurfaceTransition { get; }

    public bool SupportsClearance(float requiredClearance) =>
        MinimumClearance >= requiredClearance;
}
