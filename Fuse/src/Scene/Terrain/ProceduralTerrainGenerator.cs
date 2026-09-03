using System.IO;
using System.Numerics;
using System.Threading;

namespace Fuse.Scene.Terrain;

/// <summary>
/// Deterministic CPU terrain generator. Every sample depends only on the
/// recipe, seed and global metre coordinate, so neighbouring tiles share
/// exactly the same border samples.
/// </summary>
public static class ProceduralTerrainGenerator
{
    private const double InvTwoTo53 = 1.0 / 9007199254740992.0;

    public static TerrainAsset GenerateTile(
        ProceduralTerrainSettings settings,
        long tileX,
        long tileZ,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();

        int resolution = settings.TileResolution;
        double cellSize = settings.TileSizeMeters / (resolution - 1);
        float heightScale = MathF.Max(settings.MaxHeight - settings.MinHeight, 1.0f);
        var samples = new ushort[checked(resolution * resolution)];

        for (int z = 0; z < resolution; z++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            double worldZ = tileZ * settings.TileSizeMeters + z * cellSize;
            for (int x = 0; x < resolution; x++)
            {
                double worldX = tileX * settings.TileSizeMeters + x * cellSize;
                float height = SampleHeight(settings, worldX, worldZ);
                float normalized = System.Math.Clamp(
                    (height - settings.MinHeight) / heightScale,
                    0.0f,
                    1.0f);
                samples[z * resolution + x] = (ushort)MathF.Round(normalized * ushort.MaxValue);
            }
        }

        return new TerrainAsset(
            resolution,
            resolution,
            (float)cellSize,
            heightScale,
            settings.MinHeight,
            samples);
    }

    public static float SampleHeight(
        ProceduralTerrainSettings settings,
        double worldX,
        double worldZ)
    {
        ArgumentNullException.ThrowIfNull(settings);

        // Keep the noise domain in double precision. Converting an 80,000 km
        // coordinate to float before hashing would make distant tiles repeat
        // or lose their small-scale variation.
        double warpPeriod = 1.0 / System.Math.Max(settings.DomainWarpScale, 0.00000001f);
        double warpX = Fbm(settings, worldX * settings.DomainWarpScale + 13.7, worldZ * settings.DomainWarpScale - 41.2, settings.DomainWarpOctaves);
        double warpZ = Fbm(settings, worldX * settings.DomainWarpScale - 73.1, worldZ * settings.DomainWarpScale + 29.4, settings.DomainWarpOctaves);
        double warpedX = worldX + warpX * settings.DomainWarpStrength * warpPeriod;
        double warpedZ = worldZ + warpZ * settings.DomainWarpStrength * warpPeriod;

        float continental = ToUnit(Fbm(
            settings,
            warpedX * settings.ContinentalScale,
            warpedZ * settings.ContinentalScale,
            settings.ContinentalOctaves));
        float land = SmoothStep(0.28f, 0.62f, continental);
        float mountainMask = SmoothStep(
            settings.MountainMaskStart,
            settings.MountainMaskEnd,
            continental);

        float height = settings.BaseHeight +
            (continental - 0.5f) * settings.ContinentalAmplitude;

        float mountainNoise = ToUnit(Fbm(
            settings,
            warpedX * settings.MountainScale + 101.0,
            warpedZ * settings.MountainScale - 37.0,
            settings.MountainOctaves));
        float ridged = 1.0f - MathF.Abs(mountainNoise * 2.0f - 1.0f);
        ridged *= ridged;
        height += ridged * settings.MountainHeight * mountainMask;

        float valleyNoise = ToUnit(Fbm(
            settings,
            warpedX * settings.ValleyScale - 19.0,
            warpedZ * settings.ValleyScale + 67.0,
            settings.ValleyOctaves));
        float valleyShape = SmoothStep(0.18f, 0.82f, valleyNoise);
        height -= valleyShape * settings.ValleyDepth * (1.0f - mountainMask * 0.75f) * land;

        float detail = (float)Fbm(
            settings,
            warpedX * settings.DetailScale + 211.0,
            warpedZ * settings.DetailScale - 97.0,
            settings.DetailOctaves);
        float erosion = settings.ErosionStrength * mountainMask;
        height += detail * settings.DetailHeight * (1.0f - erosion * 0.7f);

        if (settings.RiverDepth > 0.0f)
        {
            float riverNoise = MathF.Abs((float)Fbm(
                settings,
                warpedX * settings.RiverScale + 31.0,
                warpedZ * settings.RiverScale + 17.0,
                settings.RiverOctaves));
            float river = 1.0f - SmoothStep(0.015f, 0.12f, riverNoise);
            height -= river * settings.RiverDepth * land * (1.0f - mountainMask * 0.5f);
        }

        return System.Math.Clamp(height, settings.MinHeight, settings.MaxHeight);
    }

    /// <summary>
    /// Returns a stable unit normal for tools that need a procedural surface
    /// query without first materialising a complete height tile.
    /// </summary>
    public static Vector3 SampleNormal(
        ProceduralTerrainSettings settings,
        double worldX,
        double worldZ,
        double sampleDistance = 1.0)
    {
        double distance = System.Math.Max(sampleDistance, 0.01);
        float left = SampleHeight(settings, worldX - distance, worldZ);
        float right = SampleHeight(settings, worldX + distance, worldZ);
        float down = SampleHeight(settings, worldX, worldZ - distance);
        float up = SampleHeight(settings, worldX, worldZ + distance);
        return Vector3.Normalize(new Vector3(
            (left - right) / (float)(distance * 2.0),
            1.0f,
            (down - up) / (float)(distance * 2.0)));
    }

    private static double Fbm(
        ProceduralTerrainSettings settings,
        double x,
        double z,
        int octaves)
    {
        int count = System.Math.Clamp(octaves, 1, 8);
        double value = 0.0;
        double amplitude = 0.5;
        double frequency = 1.0;
        double normalization = 0.0;
        for (int octave = 0; octave < count; octave++)
        {
            value += ValueNoise(settings.Seed + octave * 1013L, x * frequency, z * frequency) * amplitude;
            normalization += amplitude;
            frequency *= MathF.Max(1.01f, settings.NoiseLacunarity);
            amplitude *= System.Math.Clamp(settings.NoiseGain, 0.01f, 0.99f);
        }

        return normalization > 0.0 ? value / normalization : 0.0;
    }

    private static double ValueNoise(long seed, double x, double z)
    {
        long x0 = (long)System.Math.Floor(x);
        long z0 = (long)System.Math.Floor(z);
        double tx = Fade(x - x0);
        double tz = Fade(z - z0);

        double n00 = HashToSigned(seed, x0, z0);
        double n10 = HashToSigned(seed, x0 + 1, z0);
        double n01 = HashToSigned(seed, x0, z0 + 1);
        double n11 = HashToSigned(seed, x0 + 1, z0 + 1);
        double near = Lerp(n00, n10, tx);
        double far = Lerp(n01, n11, tx);
        return Lerp(near, far, tz);
    }

    private static double HashToSigned(long seed, long x, long z)
    {
        unchecked
        {
            ulong value = (ulong)seed;
            value ^= (ulong)x * 0x9E3779B185EBCA87UL;
            value ^= (ulong)z * 0xC2B2AE3D27D4EB4FUL;
            value += 0x9E3779B97F4A7C15UL;
            value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
            value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
            value ^= value >> 31;
            return ((value >> 11) * InvTwoTo53) * 2.0 - 1.0;
        }
    }

    private static float ToUnit(double value) => System.Math.Clamp((float)(value * 0.5 + 0.5), 0.0f, 1.0f);

    private static double Fade(double value) => value * value * value * (value * (value * 6.0 - 15.0) + 10.0);

    private static float SmoothStep(float edge0, float edge1, float value)
    {
        if (edge1 <= edge0)
            return value >= edge1 ? 1.0f : 0.0f;
        float t = System.Math.Clamp((value - edge0) / (edge1 - edge0), 0.0f, 1.0f);
        return t * t * (3.0f - 2.0f * t);
    }

    private static double Lerp(double from, double to, double amount) => from + (to - from) * amount;
}
