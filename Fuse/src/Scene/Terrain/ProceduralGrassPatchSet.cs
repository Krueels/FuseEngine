using System.Collections.Concurrent;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;

namespace Fuse.Scene.Terrain;

public readonly record struct GrassPatchCoordinate(
    long TileX,
    long TileZ,
    int PatchX,
    int PatchZ);

public readonly record struct GrassBladeCandidate(
    Vector3 LocalOffset,
    Vector3 LocalNormal,
    float Height,
    float Width,
    float Yaw,
    float WindPhase,
    float Random,
    float ProceduralDensity,
    int Species);

public sealed class ProceduralGrassPatch
{
    public GrassPatchCoordinate Coordinate { get; }
    public double LocalOriginX { get; }
    public double LocalOriginZ { get; }
    public float Width { get; }
    public float Depth { get; }
    public float MinimumHeight { get; }
    public float MaximumHeight { get; }
    public IReadOnlyList<GrassBladeCandidate> Candidates { get; }

    internal ProceduralGrassPatch(
        GrassPatchCoordinate coordinate,
        double localOriginX,
        double localOriginZ,
        float width,
        float depth,
        GrassBladeCandidate[] candidates)
    {
        Coordinate = coordinate;
        LocalOriginX = localOriginX;
        LocalOriginZ = localOriginZ;
        Width = width;
        Depth = depth;
        Candidates = candidates;
        if (candidates.Length == 0)
        {
            MinimumHeight = 0.0f;
            MaximumHeight = 0.0f;
        }
        else
        {
            float minimum = float.MaxValue;
            float maximum = float.MinValue;
            foreach (GrassBladeCandidate candidate in candidates)
            {
                minimum = MathF.Min(minimum, candidate.LocalOffset.Y);
                maximum = MathF.Max(maximum, candidate.LocalOffset.Y);
            }
            MinimumHeight = minimum;
            MaximumHeight = maximum;
        }
    }
}

/// <summary>
/// CPU-only grass residency layer. It mirrors the already-resident procedural
/// terrain tiles, creates deterministic candidates on worker threads, and
/// exposes immutable patches for bounded render-thread GPU uploads.
/// </summary>
public sealed class ProceduralGrassPatchSet : IDisposable
{
    private readonly ProceduralTerrainLayer _layer;
    private readonly object _sync = new();
    private readonly Dictionary<GrassPatchCoordinate, CancellationTokenSource> _pending = [];
    private readonly ConcurrentQueue<ProceduralGrassPatch> _ready = new();
    private readonly Dictionary<GrassPatchCoordinate, ProceduralGrassPatch> _resident = [];
    private readonly HashSet<GrassPatchCoordinate> _desired = [];
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ProceduralGrassDensityMaskStore _densityMasks = new();
    private int _profileSignature;
    private bool _disposed;

    public IReadOnlyCollection<ProceduralGrassPatch> ResidentPatches => _resident.Values;
    public int ResidentCount => _resident.Count;
    public int PendingCount
    {
        get
        {
            lock (_sync)
                return _pending.Count;
        }
    }
    public ulong Revision { get; private set; }

    public ProceduralGrassPatchSet(ProceduralTerrainLayer layer)
    {
        _layer = layer ?? throw new ArgumentNullException(nameof(layer));
        _profileSignature = CalculateProfileSignature(layer.Asset.Settings.Grass);
    }

    public void Update(double localCameraX, double localCameraZ)
    {
        if (_disposed)
            return;

        ProceduralGrassSettings grass = _layer.Asset.Settings.Grass;
        grass.Validate();
        int signature = CalculateProfileSignature(grass);
        if (signature != _profileSignature)
        {
            Invalidate();
            _profileSignature = signature;
        }

        if (!grass.Enabled || !_layer.Visible || grass.Density <= 0.0001f)
        {
            ClearResidency();
            return;
        }

        BuildDesiredSet(localCameraX, localCameraZ, grass);
        CancelUndesiredTasks();
        EvictUndesiredPatches();
        DrainReady(grass.MaxPatchUploadsPerFrame);
        ScheduleMissingPatches(localCameraX, localCameraZ, grass);
    }

    public void Invalidate()
    {
        lock (_sync)
        {
            foreach (CancellationTokenSource cancellation in _pending.Values)
                cancellation.Cancel();
            _pending.Clear();
        }

        while (_ready.TryDequeue(out _))
        {
        }
        if (_resident.Count > 0)
        {
            _resident.Clear();
            Revision++;
        }
        _desired.Clear();
    }

    public void InvalidateDensityMasks()
    {
        _densityMasks.Invalidate();
        Invalidate();
    }

    private void BuildDesiredSet(
        double localCameraX,
        double localCameraZ,
        ProceduralGrassSettings grass)
    {
        _desired.Clear();
        double patchSize = grass.PatchSizeMeters;
        double tileSize = _layer.Asset.Settings.TileSizeMeters;
        double radius = grass.MaximumDistance + patchSize * 0.75;
        double radiusSquared = radius * radius;
        var candidates = new List<(GrassPatchCoordinate Coordinate, double DistanceSquared)>();

        foreach ((TerrainTileCoordinate tileCoordinate, TerrainAsset tile) in _layer.LoadedAssets)
        {
            double tileOriginX = tileCoordinate.X * tileSize;
            double tileOriginZ = tileCoordinate.Z * tileSize;
            double tileWidth = (tile.Width - 1) * tile.CellSize;
            double tileDepth = (tile.Depth - 1) * tile.CellSize;

            double nearestX = System.Math.Clamp(localCameraX, tileOriginX, tileOriginX + tileWidth);
            double nearestZ = System.Math.Clamp(localCameraZ, tileOriginZ, tileOriginZ + tileDepth);
            double tileDx = nearestX - localCameraX;
            double tileDz = nearestZ - localCameraZ;
            if (tileDx * tileDx + tileDz * tileDz > radiusSquared)
                continue;

            int patchCountX = System.Math.Max(1, (int)System.Math.Ceiling(tileWidth / patchSize));
            int patchCountZ = System.Math.Max(1, (int)System.Math.Ceiling(tileDepth / patchSize));
            int minPatchX = System.Math.Clamp(
                (int)System.Math.Floor((localCameraX - radius - tileOriginX) / patchSize),
                0,
                patchCountX - 1);
            int maxPatchX = System.Math.Clamp(
                (int)System.Math.Floor((localCameraX + radius - tileOriginX) / patchSize),
                0,
                patchCountX - 1);
            int minPatchZ = System.Math.Clamp(
                (int)System.Math.Floor((localCameraZ - radius - tileOriginZ) / patchSize),
                0,
                patchCountZ - 1);
            int maxPatchZ = System.Math.Clamp(
                (int)System.Math.Floor((localCameraZ + radius - tileOriginZ) / patchSize),
                0,
                patchCountZ - 1);

            for (int patchZ = minPatchZ; patchZ <= maxPatchZ; patchZ++)
            {
                double originZ = tileOriginZ + patchZ * patchSize;
                double depth = System.Math.Min(patchSize, tileOriginZ + tileDepth - originZ);
                for (int patchX = minPatchX; patchX <= maxPatchX; patchX++)
                {
                    double originX = tileOriginX + patchX * patchSize;
                    double width = System.Math.Min(patchSize, tileOriginX + tileWidth - originX);
                    double centerX = originX + width * 0.5;
                    double centerZ = originZ + depth * 0.5;
                    double dx = centerX - localCameraX;
                    double dz = centerZ - localCameraZ;
                    double distanceSquared = dx * dx + dz * dz;
                    if (distanceSquared > radiusSquared)
                        continue;

                    candidates.Add((
                        new GrassPatchCoordinate(
                            tileCoordinate.X,
                            tileCoordinate.Z,
                            patchX,
                            patchZ),
                        distanceSquared));
                }
            }
        }

        candidates.Sort(static (left, right) =>
        {
            int distance = left.DistanceSquared.CompareTo(right.DistanceSquared);
            if (distance != 0) return distance;
            int tileX = left.Coordinate.TileX.CompareTo(right.Coordinate.TileX);
            if (tileX != 0) return tileX;
            int tileZ = left.Coordinate.TileZ.CompareTo(right.Coordinate.TileZ);
            if (tileZ != 0) return tileZ;
            int patchX = left.Coordinate.PatchX.CompareTo(right.Coordinate.PatchX);
            return patchX != 0
                ? patchX
                : left.Coordinate.PatchZ.CompareTo(right.Coordinate.PatchZ);
        });

        int maximum = System.Math.Min(grass.MaxResidentPatches, candidates.Count);
        for (int i = 0; i < maximum; i++)
            _desired.Add(candidates[i].Coordinate);
    }

    private void CancelUndesiredTasks()
    {
        lock (_sync)
        {
            foreach ((GrassPatchCoordinate coordinate, CancellationTokenSource cancellation) in _pending)
            {
                if (!_desired.Contains(coordinate) || !TileIsResident(coordinate))
                    cancellation.Cancel();
            }
        }
    }

    private void EvictUndesiredPatches()
    {
        bool changed = false;
        foreach (GrassPatchCoordinate coordinate in _resident.Keys.ToArray())
        {
            if (_desired.Contains(coordinate) && TileIsResident(coordinate))
                continue;
            _resident.Remove(coordinate);
            changed = true;
        }
        if (changed)
            Revision++;
    }

    private void DrainReady(int maximum)
    {
        bool changed = false;
        for (int i = 0; i < maximum && _ready.TryDequeue(out ProceduralGrassPatch? patch); i++)
        {
            if (!_desired.Contains(patch.Coordinate) || !TileIsResident(patch.Coordinate))
                continue;
            _resident[patch.Coordinate] = patch;
            changed = true;
        }
        if (changed)
            Revision++;
    }

    private void ScheduleMissingPatches(
        double localCameraX,
        double localCameraZ,
        ProceduralGrassSettings grass)
    {
        int maxTasks = System.Math.Max(1, _layer.Asset.Settings.MaxGenerationTasks);
        int available;
        lock (_sync)
            available = maxTasks - _pending.Count;
        if (available <= 0)
            return;

        double patchSize = grass.PatchSizeMeters;
        double tileSize = _layer.Asset.Settings.TileSizeMeters;
        var missing = new List<(GrassPatchCoordinate Coordinate, double DistanceSquared)>();
        foreach (GrassPatchCoordinate coordinate in _desired)
        {
            if (_resident.ContainsKey(coordinate))
                continue;
            lock (_sync)
            {
                if (_pending.ContainsKey(coordinate))
                    continue;
            }

            double originX = coordinate.TileX * tileSize + coordinate.PatchX * patchSize;
            double originZ = coordinate.TileZ * tileSize + coordinate.PatchZ * patchSize;
            double dx = originX + patchSize * 0.5 - localCameraX;
            double dz = originZ + patchSize * 0.5 - localCameraZ;
            missing.Add((coordinate, dx * dx + dz * dz));
        }
        missing.Sort(static (left, right) => left.DistanceSquared.CompareTo(right.DistanceSquared));

        for (int index = 0; index < missing.Count && available > 0; index++)
        {
            GrassPatchCoordinate coordinate = missing[index].Coordinate;
            var tileCoordinate = new TerrainTileCoordinate(coordinate.TileX, coordinate.TileZ);
            if (!_layer.LoadedAssets.TryGetValue(tileCoordinate, out TerrainAsset? tile))
                continue;

            ScheduleGeneration(coordinate, tile, grass.Clone());
            available--;
        }
    }

    private void ScheduleGeneration(
        GrassPatchCoordinate coordinate,
        TerrainAsset tile,
        ProceduralGrassSettings grass)
    {
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        lock (_sync)
        {
            if (_disposed || _pending.ContainsKey(coordinate))
            {
                cancellation.Dispose();
                return;
            }
            _pending[coordinate] = cancellation;
        }

        double tileSize = _layer.Asset.Settings.TileSizeMeters;
        float seaLevel = _layer.Asset.Settings.SeaLevel;
        _ = Task.Run(
                () => GeneratePatch(coordinate, tile, tileSize, seaLevel, grass, cancellation.Token),
                cancellation.Token)
            .ContinueWith(
                task =>
                {
                    lock (_sync)
                        _pending.Remove(coordinate);
                    if (!_disposed && task.Status == TaskStatus.RanToCompletion && task.Result != null)
                        _ready.Enqueue(task.Result);
                    cancellation.Dispose();
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
    }

    private ProceduralGrassPatch GeneratePatch(
        GrassPatchCoordinate coordinate,
        TerrainAsset tile,
        double tileSize,
        float seaLevel,
        ProceduralGrassSettings grass,
        CancellationToken cancellationToken)
    {
        double tileOriginX = coordinate.TileX * tileSize;
        double tileOriginZ = coordinate.TileZ * tileSize;
        double tileWidth = (tile.Width - 1) * tile.CellSize;
        double tileDepth = (tile.Depth - 1) * tile.CellSize;
        double originX = tileOriginX + coordinate.PatchX * grass.PatchSizeMeters;
        double originZ = tileOriginZ + coordinate.PatchZ * grass.PatchSizeMeters;
        float width = (float)System.Math.Max(0.001, System.Math.Min(
            grass.PatchSizeMeters,
            tileOriginX + tileWidth - originX));
        float depth = (float)System.Math.Max(0.001, System.Math.Min(
            grass.PatchSizeMeters,
            tileOriginZ + tileDepth - originZ));

        float areaRatio = width * depth / (grass.PatchSizeMeters * grass.PatchSizeMeters);
        int candidateCount = System.Math.Max(1, (int)MathF.Round(grass.CandidatesPerPatch * areaRatio));
        int gridSide = System.Math.Max(1, (int)MathF.Ceiling(MathF.Sqrt(candidateCount)));
        float minimumHeight = MathF.Max(grass.MinimumHeight, seaLevel + grass.WaterClearance);
        float sampleDistance = MathF.Max(tile.CellSize, 0.25f);
        float minimumUp = MathF.Cos(float.DegreesToRadians(grass.MaximumSlopeDegrees));
        var candidates = new List<GrassBladeCandidate>(candidateCount);

        for (int index = 0; index < candidateCount; index++)
        {
            if ((index & 63) == 0)
                cancellationToken.ThrowIfCancellationRequested();

            int cellX = index % gridSide;
            int cellZ = index / gridSide;
            float jitterX = Hash01(grass.Seed, coordinate, index, 0xA511E9B3UL);
            float jitterZ = Hash01(grass.Seed, coordinate, index, 0x63D83595UL);
            float localPatchX = (cellX + jitterX) / gridSide * width;
            float localPatchZ = (cellZ + jitterZ) / gridSide * depth;
            float localTileX = (float)(originX - tileOriginX) + localPatchX;
            float localTileZ = (float)(originZ - tileOriginZ) + localPatchZ;
            float height = tile.GetInterpolatedHeight(localTileX, localTileZ);
            if (height < minimumHeight || height > grass.MaximumHeight)
                continue;

            float left = tile.GetInterpolatedHeight(localTileX - sampleDistance, localTileZ);
            float right = tile.GetInterpolatedHeight(localTileX + sampleDistance, localTileZ);
            float back = tile.GetInterpolatedHeight(localTileX, localTileZ - sampleDistance);
            float front = tile.GetInterpolatedHeight(localTileX, localTileZ + sampleDistance);
            Vector3 normal = Vector3.Normalize(new Vector3(left - right, sampleDistance * 2.0f, back - front));
            if (normal.Y < minimumUp)
                continue;

            double worldX = originX + localPatchX;
            double worldZ = originZ + localPatchZ;
            float biome = ValueNoise(worldX * grass.BiomeNoiseScale, worldZ * grass.BiomeNoiseScale, grass.Seed);
            float proceduralDensity = 1.0f - grass.BiomeNoiseInfluence +
                                      grass.BiomeNoiseInfluence * biome;
            proceduralDensity *= _densityMasks.Sample(
                grass,
                new TerrainTileCoordinate(coordinate.TileX, coordinate.TileZ),
                localTileX / (float)tileWidth,
                localTileZ / (float)tileDepth);
            float slopeFade = SmoothStep(minimumUp, System.Math.Min(1.0f, minimumUp + 0.18f), normal.Y);
            proceduralDensity *= slopeFade;

            float random = Hash01(grass.Seed, coordinate, index, 0xC2B2AE3D27D4EB4FUL);
            float heightRandom = Hash01(grass.Seed, coordinate, index, 0x9E3779B97F4A7C15UL);
            float widthRandom = Hash01(grass.Seed, coordinate, index, 0xD1B54A32D192ED03UL);
            float clump = ValueNoise(worldX * grass.ClumpScale, worldZ * grass.ClumpScale, grass.Seed ^ 0x5DEECE66DL);
            float yaw = Hash01(grass.Seed, coordinate, index, 0x94D049BB133111EBUL) * MathF.Tau;
            yaw += (clump - 0.5f) * grass.ClumpStrength * MathF.PI;
            int speciesIndex = SelectSpecies(
                grass.Species,
                Hash01(grass.Seed, coordinate, index, 0xA0761D6478BD642FUL));
            ProceduralGrassSpeciesSettings species = grass.Species[speciesIndex];

            candidates.Add(new GrassBladeCandidate(
                new Vector3(localPatchX, height, localPatchZ),
                normal,
                Lerp(grass.BladeHeightMin, grass.BladeHeightMax, heightRandom) * species.HeightMultiplier,
                Lerp(grass.BladeWidthMin, grass.BladeWidthMax, widthRandom) * species.WidthMultiplier,
                yaw,
                Hash01(grass.Seed, coordinate, index, 0xDB4F0B9175AE2165UL) * MathF.Tau,
                random,
                System.Math.Clamp(proceduralDensity, 0.0f, 1.0f),
                speciesIndex));
        }

        return new ProceduralGrassPatch(
            coordinate,
            originX,
            originZ,
            width,
            depth,
            candidates.ToArray());
    }

    private bool TileIsResident(GrassPatchCoordinate coordinate) =>
        _layer.LoadedAssets.ContainsKey(new TerrainTileCoordinate(coordinate.TileX, coordinate.TileZ));

    private void ClearResidency()
    {
        _desired.Clear();
        lock (_sync)
        {
            foreach (CancellationTokenSource cancellation in _pending.Values)
                cancellation.Cancel();
        }
        if (_resident.Count == 0)
            return;
        _resident.Clear();
        Revision++;
    }

    private static int CalculateProfileSignature(ProceduralGrassSettings grass)
    {
        var hash = new HashCode();
        hash.Add(grass.Enabled);
        hash.Add(grass.Seed);
        hash.Add(grass.PatchSizeMeters);
        hash.Add(grass.CandidatesPerPatch);
        hash.Add(grass.MinimumHeight);
        hash.Add(grass.MaximumHeight);
        hash.Add(grass.MaximumSlopeDegrees);
        hash.Add(grass.WaterClearance);
        hash.Add(grass.BiomeNoiseScale);
        hash.Add(grass.BiomeNoiseInfluence);
        hash.Add(grass.BladeHeightMin);
        hash.Add(grass.BladeHeightMax);
        hash.Add(grass.BladeWidthMin);
        hash.Add(grass.BladeWidthMax);
        hash.Add(grass.ClumpScale);
        hash.Add(grass.ClumpStrength);
        hash.Add(grass.DensityMaskPath, StringComparer.OrdinalIgnoreCase);
        hash.Add(grass.DensityMaskResolution);
        foreach (ProceduralGrassSpeciesSettings species in grass.Species)
        {
            hash.Add(species.Name, StringComparer.Ordinal);
            hash.Add(species.Enabled);
            hash.Add(species.Weight);
            hash.Add(species.HeightMultiplier);
            hash.Add(species.WidthMultiplier);
            hash.Add(species.ColorTint);
        }
        return hash.ToHashCode();
    }

    private static int SelectSpecies(
        IReadOnlyList<ProceduralGrassSpeciesSettings> species,
        float selection)
    {
        float totalWeight = 0.0f;
        for (int index = 0; index < species.Count; index++)
        {
            if (species[index].Enabled)
                totalWeight += species[index].Weight;
        }

        float target = selection * totalWeight;
        int fallback = 0;
        for (int index = 0; index < species.Count; index++)
        {
            if (!species[index].Enabled)
                continue;
            fallback = index;
            target -= species[index].Weight;
            if (target <= 0.0f)
                return index;
        }
        return fallback;
    }

    private static float Hash01(
        long seed,
        GrassPatchCoordinate coordinate,
        int index,
        ulong salt)
    {
        ulong value = unchecked((ulong)seed) ^ salt;
        value = Mix(value ^ unchecked((ulong)coordinate.TileX));
        value = Mix(value ^ unchecked((ulong)coordinate.TileZ));
        value = Mix(value ^ unchecked((uint)coordinate.PatchX));
        value = Mix(value ^ unchecked((uint)coordinate.PatchZ));
        value = Mix(value ^ unchecked((uint)index));
        return (value >> 40) * (1.0f / 16_777_216.0f);
    }

    private static float ValueNoise(double x, double z, long seed)
    {
        long x0 = (long)System.Math.Floor(x);
        long z0 = (long)System.Math.Floor(z);
        float tx = (float)(x - x0);
        float tz = (float)(z - z0);
        tx = tx * tx * (3.0f - 2.0f * tx);
        tz = tz * tz * (3.0f - 2.0f * tz);
        float h00 = HashCell(x0, z0, seed);
        float h10 = HashCell(x0 + 1, z0, seed);
        float h01 = HashCell(x0, z0 + 1, seed);
        float h11 = HashCell(x0 + 1, z0 + 1, seed);
        return Lerp(Lerp(h00, h10, tx), Lerp(h01, h11, tx), tz);
    }

    private static float HashCell(long x, long z, long seed)
    {
        ulong value = Mix(unchecked((ulong)seed) ^ unchecked((ulong)x));
        value = Mix(value ^ unchecked((ulong)z));
        return (value >> 40) * (1.0f / 16_777_216.0f);
    }

    private static ulong Mix(ulong value)
    {
        value ^= value >> 30;
        value *= 0xBF58476D1CE4E5B9UL;
        value ^= value >> 27;
        value *= 0x94D049BB133111EBUL;
        value ^= value >> 31;
        return value;
    }

    private static float SmoothStep(float edge0, float edge1, float value)
    {
        float t = System.Math.Clamp((value - edge0) / MathF.Max(edge1 - edge0, 0.000001f), 0.0f, 1.0f);
        return t * t * (3.0f - 2.0f * t);
    }

    private static float Lerp(float from, float to, float amount) => from + (to - from) * amount;

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _shutdown.Cancel();
        Invalidate();
        _shutdown.Dispose();
    }
}
