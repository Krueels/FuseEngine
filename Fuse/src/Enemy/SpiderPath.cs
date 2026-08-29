using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Fuse.Enemy;

/// <summary>
/// Ordered result of a navigation query. Every node is also a waypoint and
/// therefore contains both Position and SurfaceNormal for future locomotion.
/// </summary>
public sealed class SpiderPath
{
    private readonly IReadOnlyList<SpiderNavNode> _waypoints;

    public SpiderPath(IEnumerable<SpiderNavNode> waypoints, float totalCost)
    {
        ArgumentNullException.ThrowIfNull(waypoints);
        if (!float.IsFinite(totalCost) || totalCost < 0f)
            throw new ArgumentOutOfRangeException(nameof(totalCost));

        var copy = new List<SpiderNavNode>();
        foreach (SpiderNavNode waypoint in waypoints)
        {
            ArgumentNullException.ThrowIfNull(waypoint);
            copy.Add(waypoint);
        }

        _waypoints = new ReadOnlyCollection<SpiderNavNode>(copy);
        TotalCost = totalCost;
    }

    public static SpiderPath Empty { get; } = new(Array.Empty<SpiderNavNode>(), 0f);

    public IReadOnlyList<SpiderNavNode> Waypoints => _waypoints;
    public float TotalCost { get; }
    public int Count => _waypoints.Count;
    public bool IsEmpty => _waypoints.Count == 0;
}
