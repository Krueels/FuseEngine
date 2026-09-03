using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace Fuse.Scene.Terrain;

/// <summary>
/// Version 3 .terrain asset. It stores a procedural recipe and sparse sample
/// overrides instead of a world-sized heightmap.
/// </summary>
public sealed class ProceduralTerrainAsset
{
    public const int FileVersion = 3;
    private const int MaxDeltaTiles = 4096;
    private const int MaxDeltasPerTile = 4_194_304;

    private readonly Dictionary<(long X, long Z), SortedDictionary<int, ushort>> _deltas = [];

    public ProceduralTerrainSettings Settings { get; }
    public int ModifiedTileCount => _deltas.Count;
    public IReadOnlyCollection<(long X, long Z)> ModifiedTileCoordinates => _deltas.Keys;

    public ProceduralTerrainAsset(ProceduralTerrainSettings settings)
    {
        Settings = settings?.Clone() ?? throw new ArgumentNullException(nameof(settings));
        Settings.Validate();
    }

    public TerrainAsset GenerateTile(
        long tileX,
        long tileZ,
        CancellationToken cancellationToken = default)
    {
        TerrainAsset terrain = ProceduralTerrainGenerator.GenerateTile(
            Settings,
            tileX,
            tileZ,
            cancellationToken);

        if (_deltas.TryGetValue((tileX, tileZ), out SortedDictionary<int, ushort>? deltas))
        {
            foreach (var pair in deltas)
            {
                if ((uint)pair.Key < (uint)terrain.Samples.Length)
                    terrain.Samples[pair.Key] = pair.Value;
            }
        }

        return terrain;
    }

    public float SampleHeight(double worldX, double worldZ) =>
        ProceduralTerrainGenerator.SampleHeight(Settings, worldX, worldZ);

    /// <summary>
    /// Returns whether a tile intersects the finite world rectangle. Tiles at
    /// the edge are allowed to straddle the boundary so their shared samples
    /// remain deterministic; the streamer simply never requests tiles wholly
    /// outside this rectangle.
    /// </summary>
    public bool IsTileWithinWorld(long tileX, long tileZ)
    {
        double halfWorld = Settings.WorldSizeMeters * 0.5;
        double tileSize = Settings.TileSizeMeters;
        return IntersectsWorld(tileX, tileSize, halfWorld) &&
               IntersectsWorld(tileZ, tileSize, halfWorld);
    }

    private static bool IntersectsWorld(long coordinate, double tileSize, double halfWorld)
    {
        double min = coordinate * tileSize;
        double max = min + tileSize;
        return max > -halfWorld && min < halfWorld;
    }

    /// <summary>
    /// Replaces the sparse override list for a generated tile. Values equal to
    /// the recipe output are omitted, keeping procedural files compact.
    /// </summary>
    public void UpdateDeltaFromTile(long tileX, long tileZ, TerrainAsset editedTile)
    {
        ArgumentNullException.ThrowIfNull(editedTile);
        TerrainAsset generated = ProceduralTerrainGenerator.GenerateTile(Settings, tileX, tileZ);
        if (generated.Samples.Length != editedTile.Samples.Length)
            throw new InvalidDataException("The edited tile does not match the procedural tile resolution.");

        SortedDictionary<int, ushort>? changed = null;
        for (int index = 0; index < editedTile.Samples.Length; index++)
        {
            ushort value = editedTile.Samples[index];
            if (value == generated.Samples[index])
                continue;

            changed ??= new SortedDictionary<int, ushort>();
            changed[index] = value;
        }

        if (changed == null || changed.Count == 0)
            _deltas.Remove((tileX, tileZ));
        else
            _deltas[(tileX, tileZ)] = changed;
    }

    public bool TryGetDeltaCount(long tileX, long tileZ, out int count)
    {
        if (_deltas.TryGetValue((tileX, tileZ), out SortedDictionary<int, ushort>? deltas))
        {
            count = deltas.Count;
            return true;
        }

        count = 0;
        return false;
    }

    public void Save(string path)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        writer.Write(TerrainAsset.FileMagic);
        writer.Write(FileVersion);
        Settings.Write(writer);
        writer.Write(_deltas.Count);
        foreach (var tile in _deltas)
        {
            writer.Write(tile.Key.X);
            writer.Write(tile.Key.Z);
            writer.Write(tile.Value.Count);
            foreach (var delta in tile.Value)
            {
                writer.Write(delta.Key);
                writer.Write(delta.Value);
            }
        }
    }

    public static ProceduralTerrainAsset Load(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);
        uint magic = reader.ReadUInt32();
        if (magic != TerrainAsset.FileMagic)
            throw new InvalidDataException("Invalid terrain asset magic.");

        int version = reader.ReadInt32();
        if (version != FileVersion)
            throw new InvalidDataException($"Unsupported procedural terrain version: {version}");
        return ReadPayload(reader);
    }

    internal static ProceduralTerrainAsset ReadPayload(BinaryReader reader)
    {
        var asset = new ProceduralTerrainAsset(ProceduralTerrainSettings.Read(reader));
        int tileCount = reader.ReadInt32();
        if (tileCount < 0 || tileCount > MaxDeltaTiles)
            throw new InvalidDataException("Invalid procedural terrain delta tile count.");

        for (int tileIndex = 0; tileIndex < tileCount; tileIndex++)
        {
            long tileX = reader.ReadInt64();
            long tileZ = reader.ReadInt64();
            int deltaCount = reader.ReadInt32();
            if (deltaCount < 1 || deltaCount > MaxDeltasPerTile)
                throw new InvalidDataException("Invalid procedural terrain delta count.");

            var deltas = new SortedDictionary<int, ushort>();
            for (int deltaIndex = 0; deltaIndex < deltaCount; deltaIndex++)
            {
                int sampleIndex = reader.ReadInt32();
                ushort value = reader.ReadUInt16();
                if (sampleIndex < 0 || sampleIndex >= asset.Settings.TileResolution * asset.Settings.TileResolution)
                    throw new InvalidDataException("A procedural terrain delta references an invalid sample.");
                deltas[sampleIndex] = value;
            }

            asset._deltas[(tileX, tileZ)] = deltas;
        }

        return asset;
    }
}
