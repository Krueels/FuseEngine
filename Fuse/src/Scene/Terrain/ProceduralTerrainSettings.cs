using System.IO;

namespace Fuse.Scene.Terrain;

/// <summary>
/// Compact recipe used by a procedural terrain world. The values are kept in
/// world units (metres) and are deliberately independent from the render
/// resolution of a tile. This lets the same recipe generate a close tile and
/// a distant tile without storing a world-sized heightmap.
/// </summary>
public sealed class ProceduralTerrainSettings
{
    public const int CurrentVersion = 2;

    public long Seed { get; set; } = 1337;

    /// <summary>World width/depth in metres. The world is centred at zero.</summary>
    public double WorldSizeMeters { get; set; } = 80_000_000.0;

    /// <summary>Width/depth of one generated tile in metres.</summary>
    public double TileSizeMeters { get; set; } = 2048.0;

    /// <summary>Samples per side, including the duplicated tile border.</summary>
    public int TileResolution { get; set; } = 65;

    public float MinHeight { get; set; } = -512.0f;
    public float MaxHeight { get; set; } = 4096.0f;
    public float SeaLevel { get; set; } = 0.0f;

    // Macro terrain.
    public float BaseHeight { get; set; } = 32.0f;
    public float ContinentalAmplitude { get; set; } = 420.0f;
    public float ContinentalScale { get; set; } = 0.000004f;
    public int ContinentalOctaves { get; set; } = 5;
    public float NoiseLacunarity { get; set; } = 2.03f;
    public float NoiseGain { get; set; } = 0.5f;

    // Mountain and valley structure.
    public float MountainHeight { get; set; } = 1800.0f;
    public float MountainScale { get; set; } = 0.000028f;
    public int MountainOctaves { get; set; } = 5;
    public float MountainMaskStart { get; set; } = 0.48f;
    public float MountainMaskEnd { get; set; } = 0.76f;
    public float ValleyDepth { get; set; } = 180.0f;
    public float ValleyScale { get; set; } = 0.000075f;
    public int ValleyOctaves { get; set; } = 4;

    // Medium and high frequency detail.
    public float DetailHeight { get; set; } = 28.0f;
    public float DetailScale { get; set; } = 0.00035f;
    public int DetailOctaves { get; set; } = 4;

    // Domain warping is expressed as a fraction of one warp-noise period.
    public float DomainWarpStrength { get; set; } = 0.28f;
    public float DomainWarpScale { get; set; } = 0.000018f;
    public int DomainWarpOctaves { get; set; } = 3;

    // A cheap, deterministic erosion approximation. It attenuates small
    // detail in steep mountain areas; it is not a frame-time hydraulic sim.
    public float ErosionStrength { get; set; } = 0.32f;

    // Optional river carving. Zero keeps the feature disabled.
    public float RiverDepth { get; set; } = 0.0f;
    public float RiverScale { get; set; } = 0.000085f;
    public int RiverOctaves { get; set; } = 3;

    // Editor/runtime budgets.
    public int PreviewTileRadius { get; set; } = 1;
    public int StreamingTileRadius { get; set; } = 2;
    public int CollisionTileRadius { get; set; } = 1;
    public int MaxResidentTiles { get; set; } = 25;
    public int MaxGenerationTasks { get; set; } = 2;
    public int MaxTileUploadsPerFrame { get; set; } = 1;
    public float LodPixelError { get; set; } = 5.0f;

    public ProceduralTerrainSettings Clone() => (ProceduralTerrainSettings)MemberwiseClone();

    public void Validate()
    {
        WorldSizeMeters = System.Math.Clamp(WorldSizeMeters, 1.0, 80_000_000.0);
        TileSizeMeters = System.Math.Clamp(TileSizeMeters, 32.0, 65_536.0);
        TileResolution = System.Math.Clamp(TileResolution, 17, 513);
        int cells = TileResolution - 1;
        if ((cells & (cells - 1)) != 0)
            TileResolution = (1 << (int)System.Math.Round(System.Math.Log2(cells))) + 1;
        TileResolution = System.Math.Clamp(TileResolution, 17, 513);

        if (MaxHeight <= MinHeight + 1.0f)
            MaxHeight = MinHeight + 1.0f;
        SeaLevel = System.Math.Clamp(SeaLevel, MinHeight, MaxHeight);
        ContinentalScale = MathF.Max(0.00000001f, ContinentalScale);
        MountainScale = MathF.Max(0.00000001f, MountainScale);
        ValleyScale = MathF.Max(0.00000001f, ValleyScale);
        DetailScale = MathF.Max(0.00000001f, DetailScale);
        DomainWarpScale = MathF.Max(0.00000001f, DomainWarpScale);
        RiverScale = MathF.Max(0.00000001f, RiverScale);
        NoiseLacunarity = MathF.Max(1.01f, NoiseLacunarity);
        NoiseGain = System.Math.Clamp(NoiseGain, 0.01f, 0.99f);
        ContinentalOctaves = System.Math.Clamp(ContinentalOctaves, 1, 8);
        MountainOctaves = System.Math.Clamp(MountainOctaves, 1, 8);
        ValleyOctaves = System.Math.Clamp(ValleyOctaves, 1, 8);
        DetailOctaves = System.Math.Clamp(DetailOctaves, 1, 8);
        DomainWarpOctaves = System.Math.Clamp(DomainWarpOctaves, 1, 8);
        RiverOctaves = System.Math.Clamp(RiverOctaves, 1, 8);
        MountainMaskStart = System.Math.Clamp(MountainMaskStart, 0.0f, 0.999f);
        MountainMaskEnd = System.Math.Clamp(MountainMaskEnd, MountainMaskStart + 0.001f, 1.0f);
        DomainWarpStrength = System.Math.Clamp(DomainWarpStrength, 0.0f, 1.0f);
        ErosionStrength = System.Math.Clamp(ErosionStrength, 0.0f, 1.0f);
        RiverDepth = System.Math.Clamp(RiverDepth, 0.0f, MathF.Max(0.0f, MaxHeight - MinHeight));
        PreviewTileRadius = System.Math.Clamp(PreviewTileRadius, 0, 4);
        StreamingTileRadius = System.Math.Clamp(StreamingTileRadius, 0, 8);
        CollisionTileRadius = System.Math.Clamp(CollisionTileRadius, 0, StreamingTileRadius);
        MaxResidentTiles = System.Math.Clamp(MaxResidentTiles, 1, 4096);
        MaxGenerationTasks = System.Math.Clamp(MaxGenerationTasks, 1, 16);
        MaxTileUploadsPerFrame = System.Math.Clamp(MaxTileUploadsPerFrame, 1, 8);
        LodPixelError = MathF.Max(0.1f, LodPixelError);
    }

    internal void Write(BinaryWriter writer)
    {
        Validate();
        writer.Write(CurrentVersion);
        writer.Write(Seed);
        writer.Write(WorldSizeMeters);
        writer.Write(TileSizeMeters);
        writer.Write(TileResolution);
        writer.Write(MinHeight);
        writer.Write(MaxHeight);
        writer.Write(SeaLevel);
        writer.Write(BaseHeight);
        writer.Write(ContinentalAmplitude);
        writer.Write(ContinentalScale);
        writer.Write(MountainHeight);
        writer.Write(MountainScale);
        writer.Write(MountainMaskStart);
        writer.Write(MountainMaskEnd);
        writer.Write(ValleyDepth);
        writer.Write(ValleyScale);
        writer.Write(DetailHeight);
        writer.Write(DetailScale);
        writer.Write(DetailOctaves);
        writer.Write(DomainWarpStrength);
        writer.Write(DomainWarpScale);
        writer.Write(ErosionStrength);
        writer.Write(RiverDepth);
        writer.Write(RiverScale);
        writer.Write(PreviewTileRadius);
        writer.Write(StreamingTileRadius);
        writer.Write(CollisionTileRadius);
        writer.Write(MaxResidentTiles);
        writer.Write(MaxGenerationTasks);
        writer.Write(MaxTileUploadsPerFrame);
        writer.Write(LodPixelError);
        writer.Write(ContinentalOctaves);
        writer.Write(MountainOctaves);
        writer.Write(ValleyOctaves);
        writer.Write(DomainWarpOctaves);
        writer.Write(RiverOctaves);
        writer.Write(NoiseLacunarity);
        writer.Write(NoiseGain);
    }

    internal static ProceduralTerrainSettings Read(BinaryReader reader)
    {
        int version = reader.ReadInt32();
        if (version < 1 || version > CurrentVersion)
            throw new InvalidDataException($"Unsupported procedural terrain settings version: {version}");

        var settings = new ProceduralTerrainSettings
        {
            Seed = reader.ReadInt64(),
            WorldSizeMeters = reader.ReadDouble(),
            TileSizeMeters = reader.ReadDouble(),
            TileResolution = reader.ReadInt32(),
            MinHeight = reader.ReadSingle(),
            MaxHeight = reader.ReadSingle(),
            SeaLevel = reader.ReadSingle(),
            BaseHeight = reader.ReadSingle(),
            ContinentalAmplitude = reader.ReadSingle(),
            ContinentalScale = reader.ReadSingle(),
            MountainHeight = reader.ReadSingle(),
            MountainScale = reader.ReadSingle(),
            MountainMaskStart = reader.ReadSingle(),
            MountainMaskEnd = reader.ReadSingle(),
            ValleyDepth = reader.ReadSingle(),
            ValleyScale = reader.ReadSingle(),
            DetailHeight = reader.ReadSingle(),
            DetailScale = reader.ReadSingle(),
            DetailOctaves = reader.ReadInt32(),
            DomainWarpStrength = reader.ReadSingle(),
            DomainWarpScale = reader.ReadSingle(),
            ErosionStrength = reader.ReadSingle(),
            RiverDepth = reader.ReadSingle(),
            RiverScale = reader.ReadSingle(),
            PreviewTileRadius = reader.ReadInt32(),
            StreamingTileRadius = reader.ReadInt32(),
            CollisionTileRadius = reader.ReadInt32(),
            MaxResidentTiles = reader.ReadInt32(),
            MaxGenerationTasks = reader.ReadInt32(),
            MaxTileUploadsPerFrame = reader.ReadInt32(),
            LodPixelError = reader.ReadSingle()
        };
        if (version >= 2)
        {
            settings.ContinentalOctaves = reader.ReadInt32();
            settings.MountainOctaves = reader.ReadInt32();
            settings.ValleyOctaves = reader.ReadInt32();
            settings.DomainWarpOctaves = reader.ReadInt32();
            settings.RiverOctaves = reader.ReadInt32();
            settings.NoiseLacunarity = reader.ReadSingle();
            settings.NoiseGain = reader.ReadSingle();
        }
        settings.Validate();
        return settings;
    }
}
