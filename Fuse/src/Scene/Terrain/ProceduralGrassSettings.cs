using System.IO;
using System.Numerics;

namespace Fuse.Scene.Terrain;

public sealed class ProceduralGrassSpeciesSettings
{
    public string Name { get; set; } = "Meadow grass";
    public bool Enabled { get; set; } = true;
    public float Weight { get; set; } = 1.0f;
    public float HeightMultiplier { get; set; } = 1.0f;
    public float WidthMultiplier { get; set; } = 1.0f;
    public Vector3 ColorTint { get; set; } = Vector3.One;

    public ProceduralGrassSpeciesSettings Clone() =>
        (ProceduralGrassSpeciesSettings)MemberwiseClone();

    internal void Validate(int index)
    {
        Name = string.IsNullOrWhiteSpace(Name) ? $"Species {index + 1}" : Name.Trim();
        Weight = System.Math.Clamp(Weight, 0.001f, 100.0f);
        HeightMultiplier = System.Math.Clamp(HeightMultiplier, 0.1f, 4.0f);
        WidthMultiplier = System.Math.Clamp(WidthMultiplier, 0.1f, 4.0f);
        ColorTint = Vector3.Clamp(ColorTint, Vector3.Zero, new Vector3(4.0f));
    }

    internal void Write(BinaryWriter writer)
    {
        writer.Write(Name);
        writer.Write(Enabled);
        writer.Write(Weight);
        writer.Write(HeightMultiplier);
        writer.Write(WidthMultiplier);
        writer.Write(ColorTint.X);
        writer.Write(ColorTint.Y);
        writer.Write(ColorTint.Z);
    }

    internal static ProceduralGrassSpeciesSettings Read(BinaryReader reader) => new()
    {
        Name = reader.ReadString(),
        Enabled = reader.ReadBoolean(),
        Weight = reader.ReadSingle(),
        HeightMultiplier = reader.ReadSingle(),
        WidthMultiplier = reader.ReadSingle(),
        ColorTint = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle())
    };
}

/// <summary>
/// Compact, deterministic grass recipe stored with procedural terrain. No
/// blade positions are persisted; runtime patches recreate them from this
/// profile and the terrain tile coordinates.
/// </summary>
public sealed class ProceduralGrassSettings
{
    public const int MaximumSpecies = 4;

    public bool Enabled { get; set; }
    public long Seed { get; set; } = 7331;
    public float Density { get; set; } = 0.72f;
    public float PatchSizeMeters { get; set; } = 24.0f;
    public int CandidatesPerPatch { get; set; } = 576;
    public int MaxResidentPatches { get; set; } = 512;
    public int MaxPatchUploadsPerFrame { get; set; } = 8;

    public float BladeHeightMin { get; set; } = 0.55f;
    public float BladeHeightMax { get; set; } = 1.05f;
    public float BladeWidthMin { get; set; } = 0.035f;
    public float BladeWidthMax { get; set; } = 0.075f;
    public float ClumpStrength { get; set; } = 0.35f;
    public float ClumpScale { get; set; } = 0.08f;

    public float Lod0Distance { get; set; } = 65.0f;
    public float Lod1Distance { get; set; } = 170.0f;
    public float MaximumDistance { get; set; } = 360.0f;
    public float FarDensity { get; set; } = 0.18f;

    public float MinimumHeight { get; set; } = -256.0f;
    public float MaximumHeight { get; set; } = 4096.0f;
    public float MaximumSlopeDegrees { get; set; } = 42.0f;
    public float WaterClearance { get; set; } = 0.35f;
    public float BiomeNoiseScale { get; set; } = 0.004f;
    public float BiomeNoiseInfluence { get; set; } = 0.28f;

    public Vector2 WindDirection { get; set; } = Vector2.Normalize(new Vector2(0.8f, 0.35f));
    public float WindStrength { get; set; } = 0.55f;
    public float WindSpeed { get; set; } = 1.25f;
    public float GustStrength { get; set; } = 0.35f;
    public float GustScale { get; set; } = 0.025f;

    public Vector3 RootColor { get; set; } = new(0.055f, 0.16f, 0.025f);
    public Vector3 MidColor { get; set; } = new(0.14f, 0.36f, 0.075f);
    public Vector3 TipColor { get; set; } = new(0.30f, 0.55f, 0.12f);
    public float AmbientOcclusion { get; set; } = 0.55f;
    public float Translucency { get; set; } = 0.38f;
    public bool CastNearShadows { get; set; } = true;

    /// <summary>
    /// Optional sparse tiled R8 mask namespace. Empty means fully procedural
    /// density. The runtime never interprets this as one world-sized image.
    /// </summary>
    public string DensityMaskPath { get; set; } = "";
    public int DensityMaskResolution { get; set; } = 128;

    /// <summary>
    /// Optional patch-level hierarchical-Z occlusion. It uses the previous
    /// scene depth in the game renderer and the current viewport depth in the
    /// editor. It is intentionally off by default because it adds a small GPU
    /// pyramid pass and temporal visibility can lag by one frame.
    /// </summary>
    public bool HiZOcclusion { get; set; }
    public float HiZOcclusionBias { get; set; } = 0.0025f;

    /// <summary>
    /// A small palette sampled deterministically per blade. Species share the
    /// same GPU draw buffers; they do not multiply entities or draw calls.
    /// </summary>
    public List<ProceduralGrassSpeciesSettings> Species { get; set; } =
    [
        new(),
        new()
        {
            Name = "Fine grass",
            Weight = 0.32f,
            HeightMultiplier = 1.18f,
            WidthMultiplier = 0.72f,
            ColorTint = new Vector3(0.82f, 1.08f, 0.72f)
        },
        new()
        {
            Name = "Dry grass",
            Weight = 0.16f,
            HeightMultiplier = 0.82f,
            WidthMultiplier = 1.22f,
            ColorTint = new Vector3(1.24f, 1.02f, 0.62f)
        }
    ];

    public ProceduralGrassSettings Clone()
    {
        var clone = (ProceduralGrassSettings)MemberwiseClone();
        clone.Species = Species.Select(static species => species.Clone()).ToList();
        return clone;
    }

    public void Validate()
    {
        Density = System.Math.Clamp(Density, 0.0f, 1.0f);
        PatchSizeMeters = System.Math.Clamp(PatchSizeMeters, 4.0f, 128.0f);
        CandidatesPerPatch = System.Math.Clamp(CandidatesPerPatch, 16, 4096);
        MaxResidentPatches = System.Math.Clamp(MaxResidentPatches, 1, 8192);
        MaxPatchUploadsPerFrame = System.Math.Clamp(MaxPatchUploadsPerFrame, 1, 128);

        BladeHeightMin = System.Math.Clamp(BladeHeightMin, 0.02f, 8.0f);
        BladeHeightMax = System.Math.Clamp(BladeHeightMax, BladeHeightMin, 12.0f);
        BladeWidthMin = System.Math.Clamp(BladeWidthMin, 0.002f, 1.0f);
        BladeWidthMax = System.Math.Clamp(BladeWidthMax, BladeWidthMin, 2.0f);
        ClumpStrength = System.Math.Clamp(ClumpStrength, 0.0f, 1.0f);
        ClumpScale = System.Math.Clamp(ClumpScale, 0.00001f, 4.0f);

        Lod0Distance = System.Math.Clamp(Lod0Distance, 1.0f, 10_000.0f);
        Lod1Distance = System.Math.Clamp(Lod1Distance, Lod0Distance + 1.0f, 20_000.0f);
        MaximumDistance = System.Math.Clamp(MaximumDistance, Lod1Distance + 1.0f, 40_000.0f);
        FarDensity = System.Math.Clamp(FarDensity, 0.01f, 1.0f);

        if (MaximumHeight < MinimumHeight)
            (MinimumHeight, MaximumHeight) = (MaximumHeight, MinimumHeight);
        MaximumSlopeDegrees = System.Math.Clamp(MaximumSlopeDegrees, 0.0f, 89.9f);
        WaterClearance = System.Math.Clamp(WaterClearance, 0.0f, 100.0f);
        BiomeNoiseScale = System.Math.Clamp(BiomeNoiseScale, 0.0000001f, 10.0f);
        BiomeNoiseInfluence = System.Math.Clamp(BiomeNoiseInfluence, 0.0f, 1.0f);

        if (WindDirection.LengthSquared() < 0.000001f)
            WindDirection = Vector2.UnitX;
        else
            WindDirection = Vector2.Normalize(WindDirection);
        WindStrength = System.Math.Clamp(WindStrength, 0.0f, 4.0f);
        WindSpeed = System.Math.Clamp(WindSpeed, 0.0f, 20.0f);
        GustStrength = System.Math.Clamp(GustStrength, 0.0f, 4.0f);
        GustScale = System.Math.Clamp(GustScale, 0.00001f, 4.0f);

        RootColor = Vector3.Clamp(RootColor, Vector3.Zero, new Vector3(8.0f));
        MidColor = Vector3.Clamp(MidColor, Vector3.Zero, new Vector3(8.0f));
        TipColor = Vector3.Clamp(TipColor, Vector3.Zero, new Vector3(8.0f));
        AmbientOcclusion = System.Math.Clamp(AmbientOcclusion, 0.0f, 1.0f);
        Translucency = System.Math.Clamp(Translucency, 0.0f, 4.0f);
        DensityMaskPath ??= "";
        DensityMaskResolution = System.Math.Clamp(DensityMaskResolution, 16, 1024);
        HiZOcclusionBias = System.Math.Clamp(HiZOcclusionBias, 0.00001f, 0.1f);

        Species ??= [];
        if (Species.Count == 0)
            Species.Add(new ProceduralGrassSpeciesSettings());
        if (Species.Count > MaximumSpecies)
            Species.RemoveRange(MaximumSpecies, Species.Count - MaximumSpecies);
        for (int index = 0; index < Species.Count; index++)
        {
            Species[index] ??= new ProceduralGrassSpeciesSettings();
            Species[index].Validate(index);
        }
        if (!Species.Any(static species => species.Enabled))
            Species[0].Enabled = true;
    }

    internal void Write(BinaryWriter writer)
    {
        Validate();
        writer.Write(Enabled);
        writer.Write(Seed);
        writer.Write(Density);
        writer.Write(PatchSizeMeters);
        writer.Write(CandidatesPerPatch);
        writer.Write(MaxResidentPatches);
        writer.Write(MaxPatchUploadsPerFrame);
        writer.Write(BladeHeightMin);
        writer.Write(BladeHeightMax);
        writer.Write(BladeWidthMin);
        writer.Write(BladeWidthMax);
        writer.Write(ClumpStrength);
        writer.Write(ClumpScale);
        writer.Write(Lod0Distance);
        writer.Write(Lod1Distance);
        writer.Write(MaximumDistance);
        writer.Write(FarDensity);
        writer.Write(MinimumHeight);
        writer.Write(MaximumHeight);
        writer.Write(MaximumSlopeDegrees);
        writer.Write(WaterClearance);
        writer.Write(BiomeNoiseScale);
        writer.Write(BiomeNoiseInfluence);
        writer.Write(WindDirection.X);
        writer.Write(WindDirection.Y);
        writer.Write(WindStrength);
        writer.Write(WindSpeed);
        writer.Write(GustStrength);
        writer.Write(GustScale);
        WriteVector3(writer, RootColor);
        WriteVector3(writer, MidColor);
        WriteVector3(writer, TipColor);
        writer.Write(AmbientOcclusion);
        writer.Write(Translucency);
        writer.Write(CastNearShadows);
        writer.Write(DensityMaskPath);
        writer.Write(DensityMaskResolution);
        writer.Write(HiZOcclusion);
        writer.Write(HiZOcclusionBias);
        writer.Write(Species.Count);
        foreach (ProceduralGrassSpeciesSettings species in Species)
            species.Write(writer);
    }

    internal static ProceduralGrassSettings Read(
        BinaryReader reader,
        bool hasSpecies,
        bool hasHiZOcclusion)
    {
        var settings = new ProceduralGrassSettings
        {
            Enabled = reader.ReadBoolean(),
            Seed = reader.ReadInt64(),
            Density = reader.ReadSingle(),
            PatchSizeMeters = reader.ReadSingle(),
            CandidatesPerPatch = reader.ReadInt32(),
            MaxResidentPatches = reader.ReadInt32(),
            MaxPatchUploadsPerFrame = reader.ReadInt32(),
            BladeHeightMin = reader.ReadSingle(),
            BladeHeightMax = reader.ReadSingle(),
            BladeWidthMin = reader.ReadSingle(),
            BladeWidthMax = reader.ReadSingle(),
            ClumpStrength = reader.ReadSingle(),
            ClumpScale = reader.ReadSingle(),
            Lod0Distance = reader.ReadSingle(),
            Lod1Distance = reader.ReadSingle(),
            MaximumDistance = reader.ReadSingle(),
            FarDensity = reader.ReadSingle(),
            MinimumHeight = reader.ReadSingle(),
            MaximumHeight = reader.ReadSingle(),
            MaximumSlopeDegrees = reader.ReadSingle(),
            WaterClearance = reader.ReadSingle(),
            BiomeNoiseScale = reader.ReadSingle(),
            BiomeNoiseInfluence = reader.ReadSingle(),
            WindDirection = new Vector2(reader.ReadSingle(), reader.ReadSingle()),
            WindStrength = reader.ReadSingle(),
            WindSpeed = reader.ReadSingle(),
            GustStrength = reader.ReadSingle(),
            GustScale = reader.ReadSingle(),
            RootColor = ReadVector3(reader),
            MidColor = ReadVector3(reader),
            TipColor = ReadVector3(reader),
            AmbientOcclusion = reader.ReadSingle(),
            Translucency = reader.ReadSingle(),
            CastNearShadows = reader.ReadBoolean(),
            DensityMaskPath = reader.ReadString(),
            DensityMaskResolution = reader.ReadInt32()
        };
        if (hasHiZOcclusion)
        {
            settings.HiZOcclusion = reader.ReadBoolean();
            settings.HiZOcclusionBias = reader.ReadSingle();
        }
        if (hasSpecies)
        {
            int speciesCount = System.Math.Clamp(reader.ReadInt32(), 0, MaximumSpecies);
            settings.Species = new List<ProceduralGrassSpeciesSettings>(speciesCount);
            for (int index = 0; index < speciesCount; index++)
                settings.Species.Add(ProceduralGrassSpeciesSettings.Read(reader));
        }
        settings.Validate();
        return settings;
    }

    private static void WriteVector3(BinaryWriter writer, Vector3 value)
    {
        writer.Write(value.X);
        writer.Write(value.Y);
        writer.Write(value.Z);
    }

    private static Vector3 ReadVector3(BinaryReader reader) =>
        new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
}
