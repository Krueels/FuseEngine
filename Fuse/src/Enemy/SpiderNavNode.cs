using System;
using System.Collections.Generic;
using System.Numerics;

namespace Fuse.Enemy;

/// <summary>
/// A navigable sample on a physical surface.
/// Position is the desired body-center position and SurfaceNormal points away
/// from the supporting surface.
/// </summary>
public sealed class SpiderNavNode
{
    private const float Epsilon = 0.0001f;
    private readonly List<SpiderNavEdge> _edges = new();

    public SpiderNavNode(
        int id,
        Vector3 position,
        Vector3 surfaceNormal,
        float clearance)
    {
        if (id < 0)
            throw new ArgumentOutOfRangeException(nameof(id));
        if (!IsFinite(position))
            throw new ArgumentException("Node position must contain finite values.", nameof(position));
        if (!IsFinite(surfaceNormal) || surfaceNormal.LengthSquared() <= Epsilon * Epsilon)
            throw new ArgumentException("Node surface normal must be a non-zero finite vector.", nameof(surfaceNormal));
        if (!float.IsFinite(clearance) || clearance < 0f)
            throw new ArgumentOutOfRangeException(nameof(clearance));

        Id = id;
        Position = position;
        SurfaceNormal = Vector3.Normalize(surfaceNormal);
        Clearance = clearance;
    }

    public int Id { get; }
    public Vector3 Position { get; }
    public Vector3 SurfaceNormal { get; }
    public float Clearance { get; }
    public IReadOnlyList<SpiderNavEdge> Edges => _edges;

    /// <summary>
    /// Adds an already validated outgoing connection. Collision validation is
    /// intentionally outside this class and belongs to the future graph bake.
    /// </summary>
    public void AddEdge(SpiderNavEdge edge)
    {
        if (edge.FromNodeId != Id)
        {
            throw new ArgumentException(
                $"Edge starts at node {edge.FromNodeId}, but belongs to node {Id}.",
                nameof(edge));
        }

        _edges.Add(edge);
    }

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) &&
        float.IsFinite(value.Y) &&
        float.IsFinite(value.Z);
}
