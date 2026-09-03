using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;

namespace Fuse.Scene.Terrain;

/// <summary>
/// One terrain heightmap inside a connected terrain tile set. X/Z are grid
/// coordinates, not world units. The tile at (0, 0) is the map object's
/// origin tile.
/// </summary>
public sealed class TerrainTile
{
    public int X { get; }
    public int Z { get; }
    public TerrainAsset Asset { get; }

    public TerrainTile(int x, int z, TerrainAsset asset)
    {
        Asset = asset ?? throw new ArgumentNullException(nameof(asset));
        X = x;
        Z = z;
    }
}

/// <summary>
/// Snapshot of all height samples in a tile set. It is used by the editor's
/// undo system without flattening a multi-tile terrain back to one tile.
/// </summary>
public sealed class TerrainTileSetSnapshot
{
    private readonly Dictionary<(int X, int Z), ushort[]> _samples;

    internal TerrainTileSetSnapshot(IEnumerable<TerrainTile> tiles)
    {
        _samples = tiles.ToDictionary(
            tile => (tile.X, tile.Z),
            tile => (ushort[])tile.Asset.Samples.Clone());
    }

    public bool ContentEquals(TerrainTileSetSnapshot other)
    {
        if (_samples.Count != other._samples.Count)
            return false;

        foreach (var pair in _samples)
        {
            if (!other._samples.TryGetValue(pair.Key, out ushort[]? samples) ||
                !pair.Value.AsSpan().SequenceEqual(samples))
                return false;
        }

        return true;
    }

    internal bool TryGetSamples(int x, int z, out ushort[] samples) =>
        _samples.TryGetValue((x, z), out samples!);

    internal IReadOnlyCollection<(int X, int Z)> Coordinates => _samples.Keys;
}

/// <summary>
/// Connected terrain tiles stored in one .terrain file.
///
/// Version 1 .terrain files are transparently exposed as a one-tile set.
/// Version 2 stores a tile count followed by each tile coordinate and its
/// heightmap, so the map file needs only one terrain_asset reference. Version
/// 3 stores a procedural recipe plus sparse sculpt deltas and materialises a
/// small preview window on demand.
/// </summary>
public sealed class TerrainTileSetAsset
{
    public const int CurrentVersion = 2;
    public const int ProceduralVersion = ProceduralTerrainAsset.FileVersion;
    private const int MaxTileCount = 4096;
    private const float SettingsEpsilon = 0.000001f;

    private readonly Dictionary<(int X, int Z), TerrainTile> _tiles;
    private readonly List<TerrainTile> _orderedTiles;

    public IReadOnlyList<TerrainTile> Tiles => _orderedTiles;
    public TerrainTile Primary => _tiles.TryGetValue((0, 0), out TerrainTile? origin)
        ? origin
        : _orderedTiles[0];
    public ProceduralTerrainAsset? Procedural { get; }
    public int Width => Primary.Asset.Width;
    public int Depth => Primary.Asset.Depth;
    public float CellSize => Primary.Asset.CellSize;
    public float HeightScale => Primary.Asset.HeightScale;
    public float HeightOffset => Primary.Asset.HeightOffset;
    public float TileWorldWidth => (Width - 1) * CellSize;
    public float TileWorldDepth => (Depth - 1) * CellSize;

    public TerrainTileSetAsset(IEnumerable<TerrainTile> tiles)
        : this(tiles, null)
    {
    }

    private TerrainTileSetAsset(
        IEnumerable<TerrainTile> tiles,
        ProceduralTerrainAsset? procedural)
    {
        if (tiles == null)
            throw new ArgumentNullException(nameof(tiles));

        Procedural = procedural;

        _tiles = new Dictionary<(int X, int Z), TerrainTile>();
        foreach (TerrainTile tile in tiles)
        {
            if (!_tiles.TryAdd((tile.X, tile.Z), tile))
                throw new InvalidDataException($"Duplicate terrain tile coordinate ({tile.X}, {tile.Z}).");
        }

        if (_tiles.Count == 0 || _tiles.Count > MaxTileCount)
            throw new InvalidDataException("The terrain tile count is outside the supported range.");

        // Primary normally resolves to (0, 0), but transient streamed sets
        // may contain only a tile at another global coordinate. Do not use
        // Primary here because the ordered list is built below.
        TerrainAsset reference = _tiles.TryGetValue((0, 0), out TerrainTile? origin)
            ? origin.Asset
            : _tiles.Values.First().Asset;
        foreach (TerrainTile tile in _tiles.Values)
        {
            TerrainAsset asset = tile.Asset;
            if (asset.Width != reference.Width ||
                asset.Depth != reference.Depth ||
                !NearlyEqual(asset.CellSize, reference.CellSize) ||
                !NearlyEqual(asset.HeightScale, reference.HeightScale) ||
                !NearlyEqual(asset.HeightOffset, reference.HeightOffset))
            {
                throw new InvalidDataException(
                    "All connected terrain tiles must use the same resolution and scale settings.");
            }
        }

        _orderedTiles = _tiles.Values
            .OrderBy(tile => tile.Z)
            .ThenBy(tile => tile.X)
            .ToList();
    }

    public static TerrainTileSetAsset FromSingle(TerrainAsset terrain)
    {
        if (terrain == null)
            throw new ArgumentNullException(nameof(terrain));
        return FromSingleAt(0, 0, terrain);
    }

    /// <summary>
    /// Creates a transient one-tile set at a global tile coordinate. This is
    /// used by the procedural streamer so the normal terrain LOD adjacency
    /// code can see neighboring streamed tiles in one coordinate space.
    /// </summary>
    public static TerrainTileSetAsset FromSingleAt(int x, int z, TerrainAsset terrain)
    {
        if (terrain == null)
            throw new ArgumentNullException(nameof(terrain));
        return new TerrainTileSetAsset([new TerrainTile(x, z, terrain)]);
    }

    /// <summary>
    /// Materializes only the small editor/runtime preview window of a
    /// procedural world. The complete world remains represented by the
    /// recipe, not by these preview tiles.
    /// </summary>
    public static TerrainTileSetAsset FromProcedural(ProceduralTerrainAsset procedural)
    {
        ArgumentNullException.ThrowIfNull(procedural);

        int radius = System.Math.Clamp(procedural.Settings.PreviewTileRadius, 0, 4);
        var coordinates = new HashSet<(int X, int Z)>();
        for (int z = -radius; z <= radius; z++)
        {
            for (int x = -radius; x <= radius; x++)
            {
                if (!procedural.IsTileWithinWorld(x, z))
                    continue;

                coordinates.Add((x, z));
            }
        }

        // Sculpt/neighbor edits may target a tile outside the small preview
        // radius. Keep those tiles materialized after reopening the asset so
        // the editor can display and edit the persisted result.
        foreach ((long X, long Z) coordinate in procedural.ModifiedTileCoordinates)
        {
            if (coordinate.X < int.MinValue || coordinate.X > int.MaxValue ||
                coordinate.Z < int.MinValue || coordinate.Z > int.MaxValue ||
                !procedural.IsTileWithinWorld(coordinate.X, coordinate.Z))
                continue;

            coordinates.Add(((int)coordinate.X, (int)coordinate.Z));
        }

        coordinates.Add((0, 0));
        var tiles = new List<TerrainTile>(coordinates.Count);
        foreach ((int X, int Z) coordinate in coordinates.OrderBy(value => value.Z).ThenBy(value => value.X))
        {
            tiles.Add(new TerrainTile(
                coordinate.X,
                coordinate.Z,
                procedural.GenerateTile(coordinate.X, coordinate.Z)));
        }

        return new TerrainTileSetAsset(tiles, procedural);
    }

    public bool TryGetTile(int x, int z, out TerrainTile tile) =>
        _tiles.TryGetValue((x, z), out tile!);

    public Vector3 GetTileOrigin(TerrainTile tile) =>
        new(tile.X * TileWorldWidth, 0f, tile.Z * TileWorldDepth);

    public Vector3 GetTileOrigin(int x, int z) =>
        new(x * TileWorldWidth, 0f, z * TileWorldDepth);

    public bool TryGetTileAt(Vector3 localPosition, out TerrainTile tile)
    {
        const float epsilon = 0.0001f;
        foreach (TerrainTile candidate in _orderedTiles)
        {
            Vector3 origin = GetTileOrigin(candidate);
            float maxX = origin.X + TileWorldWidth;
            float maxZ = origin.Z + TileWorldDepth;
            if (localPosition.X >= origin.X - epsilon &&
                localPosition.X <= maxX + epsilon &&
                localPosition.Z >= origin.Z - epsilon &&
                localPosition.Z <= maxZ + epsilon)
            {
                tile = candidate;
                return true;
            }
        }

        tile = null!;
        return false;
    }

    public bool TryCreateNeighbor(
        int sourceX,
        int sourceZ,
        int offsetX,
        int offsetZ,
        out TerrainTile? created)
    {
        created = null;
        if ((offsetX == 0 && offsetZ == 0) ||
            System.Math.Abs(offsetX) + System.Math.Abs(offsetZ) != 1 ||
            !TryGetTile(sourceX, sourceZ, out TerrainTile source))
            return false;

        int targetX = sourceX + offsetX;
        int targetZ = sourceZ + offsetZ;
        if (_tiles.ContainsKey((targetX, targetZ)))
            return false;

        TerrainAsset asset = CreateNeighborAsset(source.Asset, offsetX, offsetZ);
        created = new TerrainTile(targetX, targetZ, asset);
        _tiles.Add((targetX, targetZ), created);
        _orderedTiles.Add(created);
        _orderedTiles.Sort((left, right) =>
        {
            int zComparison = left.Z.CompareTo(right.Z);
            return zComparison != 0 ? zComparison : left.X.CompareTo(right.X);
        });
        return true;
    }

    public bool TryRemoveTile(int x, int z) => TryRemoveTile(x, z, out _);

    public bool TryRemoveTile(int x, int z, out TerrainTile? removed)
    {
        removed = null;
        if ((x == 0 && z == 0) ||
            !_tiles.Remove((x, z), out TerrainTile? tile))
            return false;

        _orderedTiles.Remove(tile);
        removed = tile;
        return true;
    }

    /// <summary>
    /// Restores a tile removed by the editor when an operation needs to be
    /// rolled back after a failed save.
    /// </summary>
    public bool TryRestoreTile(TerrainTile tile)
    {
        if (tile == null || !_tiles.TryAdd((tile.X, tile.Z), tile))
            return false;

        _orderedTiles.Add(tile);
        _orderedTiles.Sort(CompareTiles);
        return true;
    }

    private static int CompareTiles(TerrainTile left, TerrainTile right)
    {
        int zComparison = left.Z.CompareTo(right.Z);
        return zComparison != 0 ? zComparison : left.X.CompareTo(right.X);
    }

    private static TerrainAsset CreateNeighborAsset(
        TerrainAsset source,
        int offsetX,
        int offsetZ)
    {
        var samples = new ushort[checked(source.Width * source.Depth)];

        // Start the new tile by extruding the source's matching border. This
        // gives Create Neighbor the same useful initial seam behavior as an
        // editor tile that was created with matching terrain settings, while
        // still leaving the new tile fully editable.
        for (int z = 0; z < source.Depth; z++)
        {
            for (int x = 0; x < source.Width; x++)
            {
                int sourceX = offsetX > 0
                    ? source.Width - 1
                    : offsetX < 0
                        ? 0
                        : x;
                int sourceZ = offsetZ > 0
                    ? source.Depth - 1
                    : offsetZ < 0
                        ? 0
                        : z;
                samples[z * source.Width + x] = source.Samples[sourceZ * source.Width + sourceX];
            }
        }

        return new TerrainAsset(
            source.Width,
            source.Depth,
            source.CellSize,
            source.HeightScale,
            source.HeightOffset,
            samples);
    }

    public IEnumerable<TerrainTile> GetTilesIntersectingCircle(Vector3 localCenter, float radius)
    {
        radius = MathF.Max(0f, radius);
        float radiusSquared = radius * radius;
        foreach (TerrainTile tile in _orderedTiles)
        {
            Vector3 origin = GetTileOrigin(tile);
            float closestX = System.Math.Clamp(localCenter.X, origin.X, origin.X + TileWorldWidth);
            float closestZ = System.Math.Clamp(localCenter.Z, origin.Z, origin.Z + TileWorldDepth);
            float dx = localCenter.X - closestX;
            float dz = localCenter.Z - closestZ;
            if (dx * dx + dz * dz <= radiusSquared)
                yield return tile;
        }
    }

    public bool Raycast(
        Vector3 origin,
        Vector3 direction,
        out float distance,
        out Vector3 hitPosition,
        out TerrainTile? hitTile)
    {
        distance = float.MaxValue;
        hitPosition = default;
        hitTile = null;
        bool found = false;

        foreach (TerrainTile tile in _orderedTiles)
        {
            Vector3 tileOrigin = GetTileOrigin(tile);
            if (!tile.Asset.Raycast(
                    origin - tileOrigin,
                    direction,
                    out float tileDistance,
                    out Vector3 tileHit))
                continue;

            if (tileDistance >= distance)
                continue;

            distance = tileDistance;
            hitPosition = tileHit + tileOrigin;
            hitTile = tile;
            found = true;
        }

        return found;
    }

    public void GetBounds(out Vector3 min, out Vector3 max)
    {
        min = new Vector3(float.MaxValue);
        max = new Vector3(float.MinValue);
        foreach (TerrainTile tile in _orderedTiles)
        {
            tile.Asset.GetBounds(out Vector3 tileMin, out Vector3 tileMax);
            Vector3 offset = GetTileOrigin(tile);
            min = Vector3.Min(min, tileMin + offset);
            max = Vector3.Max(max, tileMax + offset);
        }
    }

    public TerrainTileSetSnapshot CaptureSnapshot() =>
        new(_orderedTiles);

    public bool RestoreSnapshot(TerrainTileSetSnapshot snapshot)
    {
        if (snapshot == null || snapshot.Coordinates.Count != _tiles.Count)
            return false;

        foreach (TerrainTile tile in _orderedTiles)
        {
            if (!snapshot.TryGetSamples(tile.X, tile.Z, out ushort[] samples) ||
                samples.Length != tile.Asset.Samples.Length)
                return false;
        }

        foreach (TerrainTile tile in _orderedTiles)
        {
            snapshot.TryGetSamples(tile.X, tile.Z, out ushort[] samples);
            Array.Copy(samples, tile.Asset.Samples, samples.Length);
        }

        return true;
    }

    public void Save(string path)
    {
        if (Procedural != null)
        {
            // Sculpting still operates on the ordinary TerrainAsset returned
            // for a tile. Convert those changes back to sparse overrides before
            // writing the compact procedural file.
            foreach (TerrainTile tile in _orderedTiles)
                Procedural.UpdateDeltaFromTile(tile.X, tile.Z, tile.Asset);
            Procedural.Save(path);
            return;
        }

        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        writer.Write(TerrainAsset.FileMagic);
        writer.Write(CurrentVersion);
        writer.Write(_orderedTiles.Count);

        foreach (TerrainTile tile in _orderedTiles)
        {
            writer.Write(tile.X);
            writer.Write(tile.Z);
            WriteTerrain(writer, tile.Asset);
        }
    }

    public static TerrainTileSetAsset Load(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);

        uint magic = reader.ReadUInt32();
        if (magic != TerrainAsset.FileMagic)
            throw new InvalidDataException("Invalid terrain asset magic.");

        int version = reader.ReadInt32();
        if (version == TerrainAsset.CurrentVersion)
            return FromSingle(ReadTerrain(reader));

        if (version == ProceduralVersion)
            return FromProcedural(ProceduralTerrainAsset.ReadPayload(reader));

        if (version != CurrentVersion)
            throw new InvalidDataException($"Unsupported terrain version: {version}");

        int tileCount = reader.ReadInt32();
        if (tileCount < 1 || tileCount > MaxTileCount)
            throw new InvalidDataException("Invalid terrain tile count.");

        var tiles = new List<TerrainTile>(tileCount);
        for (int i = 0; i < tileCount; i++)
        {
            int x = reader.ReadInt32();
            int z = reader.ReadInt32();
            tiles.Add(new TerrainTile(x, z, ReadTerrain(reader)));
        }

        if (!tiles.Any(tile => tile.X == 0 && tile.Z == 0))
            throw new InvalidDataException("A saved terrain tile set must contain the origin tile (0, 0).");

        return new TerrainTileSetAsset(tiles);
    }

    private static void WriteTerrain(BinaryWriter writer, TerrainAsset terrain)
    {
        writer.Write(terrain.Width);
        writer.Write(terrain.Depth);
        writer.Write(terrain.CellSize);
        writer.Write(terrain.HeightScale);
        writer.Write(terrain.HeightOffset);
        foreach (ushort sample in terrain.Samples)
            writer.Write(sample);
    }

    private static TerrainAsset ReadTerrain(BinaryReader reader)
    {
        int width = reader.ReadInt32();
        int depth = reader.ReadInt32();
        float cellSize = reader.ReadSingle();
        float heightScale = reader.ReadSingle();
        float heightOffset = reader.ReadSingle();
        int sampleCount = checked(width * depth);
        if (sampleCount < 4)
            throw new InvalidDataException("Terrain dimensions are too small.");

        var samples = new ushort[sampleCount];
        for (int i = 0; i < samples.Length; i++)
            samples[i] = reader.ReadUInt16();

        return new TerrainAsset(
            width,
            depth,
            cellSize,
            heightScale,
            heightOffset,
            samples);
    }

    private static bool NearlyEqual(float left, float right) =>
        MathF.Abs(left - right) <= SettingsEpsilon;
}
