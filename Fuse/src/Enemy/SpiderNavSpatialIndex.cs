using System;
using System.Collections.Generic;
using System.Numerics;

namespace Fuse.Enemy;

/// <summary>
/// Uniform spatial hash for immutable navigation node positions. It is shared
/// by graph generation and world-position queries so nearest-node searches do
/// not need to scan every node.
/// </summary>
public sealed class SpiderNavSpatialIndex
{
    private const float Epsilon = 0.0001f;
    private const float MinimumNormalAgreementForDuplicate = 0.55f;

    private readonly float _cellSize;
    private readonly float _minimumDistanceSquared;
    private readonly Dictionary<(int X, int Y, int Z), List<SpiderNavNode>> _cells = new();

    public SpiderNavSpatialIndex(float cellSize)
    {
        if (!float.IsFinite(cellSize) || cellSize <= 0f)
            throw new ArgumentOutOfRangeException(nameof(cellSize));

        _cellSize = cellSize;
        _minimumDistanceSquared = cellSize * cellSize;
    }

    public void Add(SpiderNavNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        var key = GetCell(node.Position, _cellSize);
        if (!_cells.TryGetValue(key, out List<SpiderNavNode>? nodes))
        {
            nodes = new List<SpiderNavNode>();
            _cells.Add(key, nodes);
        }

        nodes.Add(node);
    }

    public bool ContainsNearby(Vector3 position, Vector3 normal)
    {
        (int x, int y, int z) cell = GetCell(position, _cellSize);
        for (int x = -1; x <= 1; x++)
        {
            for (int y = -1; y <= 1; y++)
            {
                for (int z = -1; z <= 1; z++)
                {
                    if (!_cells.TryGetValue((cell.x + x, cell.y + y, cell.z + z), out List<SpiderNavNode>? nodes))
                        continue;

                    foreach (SpiderNavNode node in nodes)
                    {
                        if (Vector3.DistanceSquared(position, node.Position) <= _minimumDistanceSquared &&
                            Vector3.Dot(normal, node.SurfaceNormal) >= MinimumNormalAgreementForDuplicate)
                        {
                            return true;
                        }
                    }
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Enumerates nodes within maxDistance of a world position.
    /// </summary>
    public IEnumerable<SpiderNavNode> Query(Vector3 position, float maxDistance)
    {
        if (!float.IsFinite(maxDistance) || maxDistance < 0f)
            throw new ArgumentOutOfRangeException(nameof(maxDistance));

        float maxDistanceSquared = maxDistance * maxDistance;
        int cellRange = System.Math.Max(1, (int)MathF.Ceiling(maxDistance / _cellSize));
        (int x, int y, int z) cell = GetCell(position, _cellSize);

        for (int x = -cellRange; x <= cellRange; x++)
        {
            for (int y = -cellRange; y <= cellRange; y++)
            {
                for (int z = -cellRange; z <= cellRange; z++)
                {
                    if (!_cells.TryGetValue((cell.x + x, cell.y + y, cell.z + z), out List<SpiderNavNode>? nodes))
                        continue;

                    foreach (SpiderNavNode node in nodes)
                    {
                        if (Vector3.DistanceSquared(position, node.Position) <= maxDistanceSquared + Epsilon)
                            yield return node;
                    }
                }
            }
        }
    }

    public static SpiderNavSpatialIndex FromGraph(SpiderNavGraph graph, float cellSize)
    {
        ArgumentNullException.ThrowIfNull(graph);
        var index = new SpiderNavSpatialIndex(cellSize);
        foreach (SpiderNavNode node in graph.Nodes)
            index.Add(node);
        return index;
    }

    private static (int X, int Y, int Z) GetCell(Vector3 position, float cellSize) =>
        (
            (int)MathF.Floor(position.X / cellSize),
            (int)MathF.Floor(position.Y / cellSize),
            (int)MathF.Floor(position.Z / cellSize));
}
