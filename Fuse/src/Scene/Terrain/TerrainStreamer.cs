using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;

namespace Fuse.Scene.Terrain;

public readonly record struct TerrainTileCoordinate(long X, long Z);

public sealed class TerrainStreamResult
{
    public TerrainTileCoordinate Coordinate { get; }
    public TerrainAsset Asset { get; }

    internal TerrainStreamResult(TerrainTileCoordinate coordinate, TerrainAsset asset)
    {
        Coordinate = coordinate;
        Asset = asset;
    }
}

/// <summary>
/// Background producer for procedural terrain tiles. It owns no OpenGL
/// resources: generation is CPU-only and the caller drains completed tiles on
/// the render thread before creating meshes or collision bodies.
/// </summary>
public sealed class TerrainStreamer : IDisposable
{
    private readonly ProceduralTerrainAsset _asset;
    private readonly object _sync = new();
    private readonly Dictionary<TerrainTileCoordinate, CancellationTokenSource> _pending = [];
    private readonly HashSet<TerrainTileCoordinate> _resident = [];
    private readonly HashSet<TerrainTileCoordinate> _desired = [];
    private readonly HashSet<TerrainTileCoordinate> _queued = [];
    private readonly Dictionary<TerrainTileCoordinate, TerrainAsset> _cache = [];
    private readonly LinkedList<TerrainTileCoordinate> _cacheLru = [];
    private readonly Dictionary<TerrainTileCoordinate, LinkedListNode<TerrainTileCoordinate>> _cacheNodes = [];
    private readonly ConcurrentQueue<TerrainStreamResult> _ready = new();
    private readonly CancellationTokenSource _shutdown = new();
    private bool _disposed;

    public ProceduralTerrainAsset Asset => _asset;
    public int StreamRadius { get; set; }
    public int MaxResidentTiles { get; set; }
    public int MaxGenerationTasks { get; set; }
    public int MaxCachedTiles { get; set; }
    public TerrainTileCoordinate CenterTile { get; private set; }
    public int PendingCount
    {
        get
        {
            lock (_sync)
                return _pending.Count;
        }
    }

    public int ResidentCount
    {
        get
        {
            lock (_sync)
                return _resident.Count;
        }
    }

    public int CachedCount
    {
        get
        {
            lock (_sync)
                return _cache.Count;
        }
    }

    public TerrainStreamer(ProceduralTerrainAsset asset)
    {
        _asset = asset ?? throw new ArgumentNullException(nameof(asset));
        asset.Settings.Validate();
        StreamRadius = asset.Settings.StreamingTileRadius;
        MaxResidentTiles = asset.Settings.MaxResidentTiles;
        MaxGenerationTasks = asset.Settings.MaxGenerationTasks;
        MaxCachedTiles = System.Math.Clamp(MaxResidentTiles * 2, 1, 8192);
    }

    public void MarkResident(long tileX, long tileZ)
    {
        lock (_sync)
        {
            if (!_disposed)
                _resident.Add(new TerrainTileCoordinate(tileX, tileZ));
        }
    }

    public bool IsResident(long tileX, long tileZ)
    {
        lock (_sync)
            return _resident.Contains(new TerrainTileCoordinate(tileX, tileZ));
    }

    public bool IsWithinRadius(TerrainTileCoordinate coordinate, int radius)
    {
        int effectiveRadius = System.Math.Max(0, radius);
        long dx = coordinate.X - CenterTile.X;
        long dz = coordinate.Z - CenterTile.Z;
        return System.Math.Abs(dx) <= effectiveRadius &&
               System.Math.Abs(dz) <= effectiveRadius;
    }

    public void Update(Vector3 localCameraPosition) =>
        Update((double)localCameraPosition.X, (double)localCameraPosition.Z);

    public void Update(double localCameraX, double localCameraZ)
    {
        lock (_sync)
        {
            if (_disposed)
                return;

            double tileSize = System.Math.Max(_asset.Settings.TileSizeMeters, 1.0);
            long centerX = ToTileCoordinate(localCameraX, tileSize);
            long centerZ = ToTileCoordinate(localCameraZ, tileSize);
            CenterTile = new TerrainTileCoordinate(centerX, centerZ);

            _desired.Clear();
            int radius = System.Math.Clamp(StreamRadius, 0, 8);
            var candidates = new List<(TerrainTileCoordinate Coordinate, long Distance)>();
            for (int z = -radius; z <= radius; z++)
            {
                for (int x = -radius; x <= radius; x++)
                {
                    var coordinate = new TerrainTileCoordinate(centerX + x, centerZ + z);
                    if (!_asset.IsTileWithinWorld(coordinate.X, coordinate.Z))
                        continue;
                    candidates.Add((coordinate, System.Math.Abs((long)x) + System.Math.Abs((long)z)));
                }
            }

            candidates.Sort((left, right) => left.Distance.CompareTo(right.Distance));
            int maximum = System.Math.Max(1, MaxResidentTiles);
            for (int i = 0; i < candidates.Count && _desired.Count < maximum; i++)
                _desired.Add(candidates[i].Coordinate);

            foreach (var pair in _pending)
            {
                if (!_desired.Contains(pair.Key))
                    pair.Value.Cancel();
            }

            int availableTasks = System.Math.Max(1, MaxGenerationTasks) - _pending.Count;
            if (availableTasks <= 0)
                return;

            for (int i = 0; i < candidates.Count && availableTasks > 0; i++)
            {
                TerrainTileCoordinate coordinate = candidates[i].Coordinate;
                if (_resident.Contains(coordinate) ||
                    _pending.ContainsKey(coordinate) ||
                    _queued.Contains(coordinate))
                    continue;

                if (_cache.TryGetValue(coordinate, out TerrainAsset? cached))
                {
                    _cache.Remove(coordinate);
                    RemoveCacheNode(coordinate);
                    _ready.Enqueue(new TerrainStreamResult(coordinate, cached));
                    _queued.Add(coordinate);
                }
                else
                {
                    ScheduleGeneration(coordinate);
                    availableTasks--;
                }
            }
        }
    }

    public IReadOnlyList<TerrainStreamResult> DrainReady(int maximum)
    {
        maximum = System.Math.Max(0, maximum);
        var completed = new List<TerrainStreamResult>(maximum);
        while (completed.Count < maximum && _ready.TryDequeue(out TerrainStreamResult? result))
        {
            lock (_sync)
            {
                _queued.Remove(result.Coordinate);
                if (_disposed)
                    continue;

                if (!_desired.Contains(result.Coordinate))
                {
                    AddToCache(result.Coordinate, result.Asset);
                    continue;
                }

                AddToCache(result.Coordinate, result.Asset);
                _resident.Add(result.Coordinate);
            }
            completed.Add(result);
        }

        return completed;
    }

    /// <summary>
    /// Returns resident tiles that are no longer inside the active budget and
    /// removes them from the streamer's resident set. The scene owns removal
    /// of the corresponding GPU meshes and physics bodies.
    /// </summary>
    public IReadOnlyList<TerrainTileCoordinate> ConsumeEvictions()
    {
        lock (_sync)
        {
            var evicted = new List<TerrainTileCoordinate>();
            foreach (TerrainTileCoordinate coordinate in _resident)
            {
                if (_desired.Contains(coordinate))
                    continue;
                evicted.Add(coordinate);
            }

            foreach (TerrainTileCoordinate coordinate in evicted)
                _resident.Remove(coordinate);
            return evicted;
        }
    }

    private void ScheduleGeneration(TerrainTileCoordinate coordinate)
    {
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        _pending[coordinate] = cancellation;

        _ = Task.Run(
                () => _asset.GenerateTile(coordinate.X, coordinate.Z, cancellation.Token),
                cancellation.Token)
            .ContinueWith(
                task =>
                {
                    lock (_sync)
                    {
                        _pending.Remove(coordinate);

                        if (!_disposed && task.Status == TaskStatus.RanToCompletion && task.Result != null)
                        {
                            _ready.Enqueue(new TerrainStreamResult(coordinate, task.Result));
                            _queued.Add(coordinate);
                        }
                    }

                    cancellation.Dispose();
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
    }

    private static long ToTileCoordinate(double position, double tileSize)
    {
        double coordinate = System.Math.Floor(position / tileSize);
        if (coordinate <= long.MinValue)
            return long.MinValue;
        if (coordinate >= long.MaxValue)
            return long.MaxValue;
        return (long)coordinate;
    }

    private void AddToCache(TerrainTileCoordinate coordinate, TerrainAsset asset)
    {
        if (_cacheNodes.TryGetValue(coordinate, out LinkedListNode<TerrainTileCoordinate>? existing))
        {
            _cache[coordinate] = asset;
            _cacheLru.Remove(existing);
            _cacheNodes[coordinate] = _cacheLru.AddLast(coordinate);
        }
        else
        {
            _cache[coordinate] = asset;
            _cacheNodes[coordinate] = _cacheLru.AddLast(coordinate);
        }

        int capacity = System.Math.Clamp(MaxCachedTiles, 1, 8192);
        while (_cache.Count > capacity && _cacheLru.First != null)
        {
            TerrainTileCoordinate oldest = _cacheLru.First.Value;
            _cacheLru.RemoveFirst();
            _cacheNodes.Remove(oldest);
            _cache.Remove(oldest);
        }
    }

    private void RemoveCacheNode(TerrainTileCoordinate coordinate)
    {
        if (!_cacheNodes.Remove(coordinate, out LinkedListNode<TerrainTileCoordinate>? node))
            return;
        _cacheLru.Remove(node);
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
                return;
            _disposed = true;
            _shutdown.Cancel();
            foreach (CancellationTokenSource cancellation in _pending.Values)
                cancellation.Cancel();
            _pending.Clear();
            _resident.Clear();
            _desired.Clear();
            _queued.Clear();
            _cache.Clear();
            _cacheLru.Clear();
            _cacheNodes.Clear();
        }

        _shutdown.Dispose();
        while (_ready.TryDequeue(out _))
        {
        }
    }
}
