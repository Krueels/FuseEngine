using System;
using System.IO;
using System.Numerics;
using StbImageSharp;

namespace Fuse.Scene.Terrain;

public sealed class TerrainAsset
{
    private const uint Magic = 0x314E5254;
    public const int CurrentVersion = 1;

    public int Width { get; }
    public int Depth { get; }
    public float CellSize { get; }
    public float HeightScale { get; }
    public float HeightOffset { get; }
    public ushort[] Samples { get; }

    public TerrainAsset(
        int width,
        int depth,
        float cellSize,
        float heightScale,
        float heightOffset,
        ushort[] samples)
    {
        if (width < 2)
            throw new ArgumentOutOfRangeException(nameof(width));

        if (depth < 2)
            throw new ArgumentOutOfRangeException(nameof(depth));

        if (cellSize <= 0f)
            throw new ArgumentOutOfRangeException(nameof(cellSize));

        if (samples.Length != width * depth)
            throw new ArgumentException("Height sample count does not match terrain dimensions.");

        Width = width;
        Depth = depth;
        CellSize = cellSize;
        HeightScale = heightScale;
        HeightOffset = heightOffset;
        Samples = (ushort[])samples.Clone();
    }

    public float GetHeight(int x, int z)
    {
        x = System.Math.Clamp(x, 0, Width - 1);
        z = System.Math.Clamp(z, 0, Depth - 1);

        ushort sample = Samples[z * Width + x];
        return HeightOffset + sample / 65535f * HeightScale;
    }

    public float GetInterpolatedHeight(float localX, float localZ)
    {
        float sampleX = System.Math.Clamp(localX / CellSize, 0f, Width - 1);
        float sampleZ = System.Math.Clamp(localZ / CellSize, 0f, Depth - 1);
        int x0 = (int)MathF.Floor(sampleX);
        int z0 = (int)MathF.Floor(sampleZ);
        int x1 = System.Math.Min(x0 + 1, Width - 1);
        int z1 = System.Math.Min(z0 + 1, Depth - 1);
        float tx = sampleX - x0;
        float tz = sampleZ - z0;

        float h00 = GetHeight(x0, z0);
        float h10 = GetHeight(x1, z0);
        float h01 = GetHeight(x0, z1);
        float h11 = GetHeight(x1, z1);
        float near = h00 + (h10 - h00) * tx;
        float far = h01 + (h11 - h01) * tx;
        return near + (far - near) * tz;
    }

    public void SetNormalizedHeight(int x, int z, float value)
    {
        x = System.Math.Clamp(x, 0, Width - 1);
        z = System.Math.Clamp(z, 0, Depth - 1);

        value = System.Math.Clamp(value, 0f, 1f);
        Samples[z * Width + x] = (ushort)MathF.Round(value * 65535f);
    }

    public void GetBounds(out Vector3 min, out Vector3 max)
    {
        float minHeight = float.MaxValue;
        float maxHeight = float.MinValue;
        foreach (ushort sample in Samples)
        {
            float height = HeightOffset + sample / 65535f * HeightScale;
            minHeight = MathF.Min(minHeight, height);
            maxHeight = MathF.Max(maxHeight, height);
        }

        min = new Vector3(0f, minHeight, 0f);
        max = new Vector3(
            (Width - 1) * CellSize,
            maxHeight,
            (Depth - 1) * CellSize);
    }

    public static TerrainAsset CreateFlat(
        int width,
        int depth,
        float cellSize,
        float heightScale,
        float heightOffset = 0f)
    {
        return new TerrainAsset(
            width,
            depth,
            cellSize,
            heightScale,
            heightOffset,
            new ushort[checked(width * depth)]);
    }

    public static TerrainAsset FromHeightmap(
        string path,
        float cellSize,
        float heightScale,
        float heightOffset = 0f)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("A heightmap path is required.", nameof(path));

        byte[] fileData = File.ReadAllBytes(path);
        ImageResult image = ImageResult.FromMemory(fileData, ColorComponents.Grey);
        if (image.Width < 2 || image.Height < 2)
            throw new InvalidDataException("A terrain heightmap must be at least 2 x 2 pixels.");

        var samples = new ushort[checked(image.Width * image.Height)];
        for (int z = 0; z < image.Height; z++)
        {
            // Image rows start at the top; terrain Z starts at its near edge.
            int sourceZ = image.Height - 1 - z;
            for (int x = 0; x < image.Width; x++)
                samples[z * image.Width + x] = (ushort)(image.Data[sourceZ * image.Width + x] * 257);
        }

        return new TerrainAsset(
            image.Width,
            image.Height,
            cellSize,
            heightScale,
            heightOffset,
            samples);
    }

    public bool Raycast(
        Vector3 origin,
        Vector3 direction,
        out float distance,
        out Vector3 hitPosition)
    {
        distance = float.MaxValue;
        hitPosition = default;

        if (direction.LengthSquared() < 0.0000001f)
            return false;

        direction = Vector3.Normalize(direction);
        if (!TryIntersectBounds(origin, direction, out float entry, out float exit))
            return false;

        Vector3 entryPoint = origin + direction * entry;
        float horizontalLengthSquared = direction.X * direction.X + direction.Z * direction.Z;
        if (horizontalLengthSquared < 0.0000001f)
        {
            int verticalX = System.Math.Clamp(
                (int)MathF.Floor(entryPoint.X / CellSize),
                0,
                Width - 2);
            int verticalZ = System.Math.Clamp(
                (int)MathF.Floor(entryPoint.Z / CellSize),
                0,
                Depth - 2);
            return TestCell(verticalX, verticalZ, origin, direction, ref distance, ref hitPosition);
        }

        float cellX = entryPoint.X / CellSize;
        float cellZ = entryPoint.Z / CellSize;
        int x = System.Math.Clamp((int)MathF.Floor(cellX), 0, Width - 2);
        int z = System.Math.Clamp((int)MathF.Floor(cellZ), 0, Depth - 2);
        int stepX = direction.X >= 0f ? 1 : -1;
        int stepZ = direction.Z >= 0f ? 1 : -1;
        if (direction.X < 0f && MathF.Abs(cellX - MathF.Round(cellX)) < 0.00001f)
            x--;
        if (direction.Z < 0f && MathF.Abs(cellZ - MathF.Round(cellZ)) < 0.00001f)
            z--;
        float deltaX = MathF.Abs(direction.X) > 0.0000001f
            ? CellSize / MathF.Abs(direction.X)
            : float.MaxValue;
        float deltaZ = MathF.Abs(direction.Z) > 0.0000001f
            ? CellSize / MathF.Abs(direction.Z)
            : float.MaxValue;
        float nextX = float.MaxValue;
        float nextZ = float.MaxValue;
        if (MathF.Abs(direction.X) > 0.0000001f)
        {
            float nextBoundaryX = (direction.X >= 0f ? (x + 1) : x) * CellSize;
            nextX = entry + (nextBoundaryX - entryPoint.X) / direction.X;
        }
        if (MathF.Abs(direction.Z) > 0.0000001f)
        {
            float nextBoundaryZ = (direction.Z >= 0f ? (z + 1) : z) * CellSize;
            nextZ = entry + (nextBoundaryZ - entryPoint.Z) / direction.Z;
        }

        while (x >= 0 && x < Width - 1 &&
               z >= 0 && z < Depth - 1)
        {
            if (TestCell(x, z, origin, direction, ref distance, ref hitPosition))
                return true;

            float nextBoundary = MathF.Min(nextX, nextZ);
            if (nextBoundary > exit + 0.0001f)
                break;

            if (nextX < nextZ)
            {
                x += stepX;
                nextX += deltaX;
            }
            else
            {
                z += stepZ;
                nextZ += deltaZ;
            }
        }

        return false;
    }

    private bool TestCell(
        int x,
        int z,
        Vector3 origin,
        Vector3 direction,
        ref float closestDistance,
        ref Vector3 closestPosition)
    {
        float x0 = x * CellSize;
        float x1 = (x + 1) * CellSize;
        float z0 = z * CellSize;
        float z1 = (z + 1) * CellSize;

        Vector3 a = new(x0, GetHeight(x, z), z0);
        Vector3 b = new(x1, GetHeight(x + 1, z), z0);
        Vector3 c = new(x1, GetHeight(x + 1, z + 1), z1);
        Vector3 d = new(x0, GetHeight(x, z + 1), z1);
        bool found = false;

        if (RayTriangle(origin, direction, a, b, c, out float firstDistance) &&
            firstDistance < closestDistance)
        {
            closestDistance = firstDistance;
            closestPosition = origin + direction * firstDistance;
            found = true;
        }

        if (RayTriangle(origin, direction, c, d, a, out float secondDistance) &&
            secondDistance < closestDistance)
        {
            closestDistance = secondDistance;
            closestPosition = origin + direction * secondDistance;
            found = true;
        }

        return found;
    }

    private bool TryIntersectBounds(
        Vector3 origin,
        Vector3 direction,
        out float entry,
        out float exit)
    {
        GetBounds(out Vector3 min, out Vector3 max);
        float currentEntry = 0f;
        float currentExit = float.MaxValue;

        bool TestAxis(float originComponent, float directionComponent, float minComponent, float maxComponent)
        {
            if (MathF.Abs(directionComponent) < 0.0000001f)
                return originComponent >= minComponent && originComponent <= maxComponent;

            float inverse = 1f / directionComponent;
            float first = (minComponent - originComponent) * inverse;
            float second = (maxComponent - originComponent) * inverse;
            if (first > second)
                (first, second) = (second, first);
            currentEntry = MathF.Max(currentEntry, first);
            currentExit = MathF.Min(currentExit, second);
            return currentEntry <= currentExit;
        }

        bool intersects = TestAxis(origin.X, direction.X, min.X, max.X) &&
               TestAxis(origin.Y, direction.Y, min.Y, max.Y) &&
               TestAxis(origin.Z, direction.Z, min.Z, max.Z) &&
               currentExit >= 0f;
        entry = currentEntry;
        exit = currentExit;
        return intersects;
    }

    public bool Sculpt(
        Vector3 localCenter,
        float radius,
        float strength,
        bool lower = false)
    {
        return Sculpt(localCenter, radius, strength, lower, null, 0, 0);
    }

    public bool Sculpt(
        Vector3 localCenter,
        float radius,
        float strength,
        bool lower,
        float[]? brushSamples,
        int brushWidth,
        int brushHeight)
    {
        return ApplyRaiseLower(
            localCenter,
            radius,
            strength,
            lower,
            brushSamples,
            brushWidth,
            brushHeight);
    }

    public bool ApplyBrush(
        TerrainSculptTool tool,
        Vector3 localCenter,
        float radius,
        float strength,
        bool lower = false,
        float targetHeight = 0.0f,
        float noiseScale = 0.25f,
        int noiseSeed = 0,
        float[]? brushSamples = null,
        int brushWidth = 0,
        int brushHeight = 0)
    {
        return tool switch
        {
            TerrainSculptTool.RaiseLower => ApplyRaiseLower(
                localCenter,
                radius,
                strength,
                lower,
                brushSamples,
                brushWidth,
                brushHeight),
            TerrainSculptTool.SetHeight => ApplySetHeight(
                localCenter,
                radius,
                strength,
                targetHeight,
                brushSamples,
                brushWidth,
                brushHeight),
            TerrainSculptTool.Smooth => ApplySmooth(
                localCenter,
                radius,
                strength,
                brushSamples,
                brushWidth,
                brushHeight),
            TerrainSculptTool.Stamp => ApplyStamp(
                localCenter,
                radius,
                strength,
                lower,
                brushSamples,
                brushWidth,
                brushHeight),
            TerrainSculptTool.Noise => ApplyNoise(
                localCenter,
                radius,
                strength,
                lower,
                noiseScale,
                noiseSeed,
                brushSamples,
                brushWidth,
                brushHeight),
            _ => false
        };
    }

    private bool ApplyRaiseLower(
        Vector3 localCenter,
        float radius,
        float strength,
        bool lower,
        float[]? brushSamples,
        int brushWidth,
        int brushHeight)
    {
        if (radius <= 0f || MathF.Abs(strength) <= 0.000001f || HeightScale <= 0.000001f)
            return false;

        bool hasHeightmapBrush = brushSamples != null &&
            brushWidth >= 2 &&
            brushHeight >= 2 &&
            brushSamples.Length >= checked(brushWidth * brushHeight);
        float radiusSquared = radius * radius;
        int minX = System.Math.Max(0, (int)MathF.Floor((localCenter.X - radius) / CellSize));
        int maxX = System.Math.Min(Width - 1, (int)MathF.Ceiling((localCenter.X + radius) / CellSize));
        int minZ = System.Math.Max(0, (int)MathF.Floor((localCenter.Z - radius) / CellSize));
        int maxZ = System.Math.Min(Depth - 1, (int)MathF.Ceiling((localCenter.Z + radius) / CellSize));
        float normalizedStrength = strength / HeightScale * (lower ? -1f : 1f);
        bool changed = false;

        for (int z = minZ; z <= maxZ; z++)
        {
            for (int x = minX; x <= maxX; x++)
            {
                float dx = x * CellSize - localCenter.X;
                float dz = z * CellSize - localCenter.Z;
                float distanceSquared = dx * dx + dz * dz;
                if (distanceSquared > radiusSquared)
                    continue;

                float distance01 = MathF.Sqrt(distanceSquared) / radius;
                float falloff = 1f - distance01;
                falloff *= falloff * (3f - 2f * falloff);

                if (hasHeightmapBrush)
                {
                    float brushU = (dx + radius) / (2.0f * radius);
                    float brushV = (dz + radius) / (2.0f * radius);
                    falloff *= SampleBrush(
                        brushSamples!,
                        brushWidth,
                        brushHeight,
                        brushU,
                        brushV);
                }

                if (falloff <= 0.000001f)
                    continue;

                float oldValue = Samples[z * Width + x] / 65535f;
                float newValue = System.Math.Clamp(oldValue + normalizedStrength * falloff, 0f, 1f);
                if (MathF.Abs(newValue - oldValue) <= 0.0000001f)
                    continue;

                SetNormalizedHeight(x, z, newValue);
                changed = true;
            }
        }

        return changed;
    }

    private bool ApplySetHeight(
        Vector3 localCenter,
        float radius,
        float strength,
        float targetHeight,
        float[]? brushSamples,
        int brushWidth,
        int brushHeight)
    {
        if (radius <= 0f || MathF.Abs(strength) <= 0.000001f || HeightScale <= 0.000001f)
            return false;

        bool hasHeightmapBrush = IsValidHeightmapBrush(
            brushSamples,
            brushWidth,
            brushHeight);
        GetBrushSampleBounds(
            localCenter,
            radius,
            out int minX,
            out int maxX,
            out int minZ,
            out int maxZ);

        float targetNormalized = System.Math.Clamp(
            (targetHeight - HeightOffset) / HeightScale,
            0.0f,
            1.0f);
        float blendStrength = System.Math.Clamp(strength, 0.0f, 1.0f);
        bool changed = false;

        for (int z = minZ; z <= maxZ; z++)
        {
            for (int x = minX; x <= maxX; x++)
            {
                if (!TryGetBrushInfluence(
                        x,
                        z,
                        localCenter,
                        radius,
                        hasHeightmapBrush,
                        brushSamples,
                        brushWidth,
                        brushHeight,
                        out float influence))
                    continue;

                float oldValue = Samples[z * Width + x] / 65535f;
                float newValue = oldValue +
                    (targetNormalized - oldValue) * blendStrength * influence;
                newValue = System.Math.Clamp(newValue, 0.0f, 1.0f);
                if (MathF.Abs(newValue - oldValue) <= 0.0000001f)
                    continue;

                SetNormalizedHeight(x, z, newValue);
                changed = true;
            }
        }

        return changed;
    }

    private bool ApplySmooth(
        Vector3 localCenter,
        float radius,
        float strength,
        float[]? brushSamples,
        int brushWidth,
        int brushHeight)
    {
        if (radius <= 0f || MathF.Abs(strength) <= 0.000001f)
            return false;

        bool hasHeightmapBrush = IsValidHeightmapBrush(
            brushSamples,
            brushWidth,
            brushHeight);
        GetBrushSampleBounds(
            localCenter,
            radius,
            out int minX,
            out int maxX,
            out int minZ,
            out int maxZ);

        // Keep the kernel local and predictable. The brush radius controls
        // the affected area, while repeated strokes/strength control how much
        // smoothing is applied. This avoids a large brush turning one frame
        // into an O(radius-squared) blur over the entire heightmap.
        const int kernelRadius = 1;
        int sourceMinX = System.Math.Max(0, minX - kernelRadius);
        int sourceMaxX = System.Math.Min(Width - 1, maxX + kernelRadius);
        int sourceMinZ = System.Math.Max(0, minZ - kernelRadius);
        int sourceMaxZ = System.Math.Min(Depth - 1, maxZ + kernelRadius);
        int sourceWidth = sourceMaxX - sourceMinX + 1;
        int sourceDepth = sourceMaxZ - sourceMinZ + 1;
        ushort[] sourceSamples = new ushort[checked(sourceWidth * sourceDepth)];
        for (int z = sourceMinZ; z <= sourceMaxZ; z++)
        {
            Array.Copy(
                Samples,
                z * Width + sourceMinX,
                sourceSamples,
                (z - sourceMinZ) * sourceWidth,
                sourceWidth);
        }

        float blendStrength = System.Math.Clamp(strength, 0.0f, 1.0f);
        int kernelRadiusSquared = kernelRadius * kernelRadius;
        bool changed = false;
        for (int z = minZ; z <= maxZ; z++)
        {
            for (int x = minX; x <= maxX; x++)
            {
                if (!TryGetBrushInfluence(
                        x,
                        z,
                        localCenter,
                        radius,
                        hasHeightmapBrush,
                        brushSamples,
                        brushWidth,
                        brushHeight,
                        out float influence))
                    continue;

                long sum = 0;
                int count = 0;
                int sourceX = x - sourceMinX;
                int sourceZ = z - sourceMinZ;
                for (int offsetZ = -kernelRadius; offsetZ <= kernelRadius; offsetZ++)
                {
                    for (int offsetX = -kernelRadius; offsetX <= kernelRadius; offsetX++)
                    {
                        if (offsetX * offsetX + offsetZ * offsetZ > kernelRadiusSquared)
                            continue;

                        int neighborX = sourceX + offsetX;
                        int neighborZ = sourceZ + offsetZ;
                        if (neighborX < 0 ||
                            neighborX >= sourceWidth ||
                            neighborZ < 0 ||
                            neighborZ >= sourceDepth)
                            continue;

                        sum += sourceSamples[
                            neighborZ * sourceWidth + neighborX];
                        count++;
                    }
                }

                float oldValue = Samples[z * Width + x] / 65535f;
                float average = count > 0
                    ? sum / (count * 65535f)
                    : oldValue;
                float newValue = oldValue +
                    (average - oldValue) * blendStrength * influence;
                if (MathF.Abs(newValue - oldValue) <= 0.0000001f)
                    continue;

                SetNormalizedHeight(x, z, newValue);
                changed = true;
            }
        }

        return changed;
    }

    private bool ApplyStamp(
        Vector3 localCenter,
        float radius,
        float strength,
        bool lower,
        float[]? brushSamples,
        int brushWidth,
        int brushHeight)
    {
        if (radius <= 0f || MathF.Abs(strength) <= 0.000001f || HeightScale <= 0.000001f)
            return false;

        bool hasHeightmapBrush = IsValidHeightmapBrush(
            brushSamples,
            brushWidth,
            brushHeight);
        GetBrushSampleBounds(
            localCenter,
            radius,
            out int minX,
            out int maxX,
            out int minZ,
            out int maxZ);
        float normalizedStrength = strength / HeightScale * (lower ? -1f : 1f);
        bool changed = false;

        for (int z = minZ; z <= maxZ; z++)
        {
            for (int x = minX; x <= maxX; x++)
            {
                if (!TryGetBrushInfluence(
                        x,
                        z,
                        localCenter,
                        radius,
                        hasHeightmapBrush,
                        brushSamples,
                        brushWidth,
                        brushHeight,
                        out float influence))
                    continue;

                float oldValue = Samples[z * Width + x] / 65535f;
                float newValue = System.Math.Clamp(
                    oldValue + normalizedStrength * influence,
                    0f,
                    1f);
                if (MathF.Abs(newValue - oldValue) <= 0.0000001f)
                    continue;

                SetNormalizedHeight(x, z, newValue);
                changed = true;
            }
        }

        return changed;
    }

    private bool ApplyNoise(
        Vector3 localCenter,
        float radius,
        float strength,
        bool lower,
        float noiseScale,
        int noiseSeed,
        float[]? brushSamples,
        int brushWidth,
        int brushHeight)
    {
        if (radius <= 0f || MathF.Abs(strength) <= 0.000001f || HeightScale <= 0.000001f)
            return false;

        bool hasHeightmapBrush = IsValidHeightmapBrush(
            brushSamples,
            brushWidth,
            brushHeight);
        GetBrushSampleBounds(
            localCenter,
            radius,
            out int minX,
            out int maxX,
            out int minZ,
            out int maxZ);
        float normalizedStrength = strength / HeightScale * (lower ? -1f : 1f);
        float frequency = MathF.Max(noiseScale, 0.0001f);
        bool changed = false;

        for (int z = minZ; z <= maxZ; z++)
        {
            for (int x = minX; x <= maxX; x++)
            {
                if (!TryGetBrushInfluence(
                        x,
                        z,
                        localCenter,
                        radius,
                        hasHeightmapBrush,
                        brushSamples,
                        brushWidth,
                        brushHeight,
                        out float influence))
                    continue;

                float noise = SampleFractalNoise(
                    x * CellSize * frequency,
                    z * CellSize * frequency,
                    noiseSeed);
                float oldValue = Samples[z * Width + x] / 65535f;
                float newValue = System.Math.Clamp(
                    oldValue + normalizedStrength * noise * influence,
                    0f,
                    1f);
                if (MathF.Abs(newValue - oldValue) <= 0.0000001f)
                    continue;

                SetNormalizedHeight(x, z, newValue);
                changed = true;
            }
        }

        return changed;
    }

    private void GetBrushSampleBounds(
        Vector3 localCenter,
        float radius,
        out int minX,
        out int maxX,
        out int minZ,
        out int maxZ)
    {
        minX = System.Math.Max(
            0,
            (int)MathF.Floor((localCenter.X - radius) / CellSize));
        maxX = System.Math.Min(
            Width - 1,
            (int)MathF.Ceiling((localCenter.X + radius) / CellSize));
        minZ = System.Math.Max(
            0,
            (int)MathF.Floor((localCenter.Z - radius) / CellSize));
        maxZ = System.Math.Min(
            Depth - 1,
            (int)MathF.Ceiling((localCenter.Z + radius) / CellSize));
    }

    private bool TryGetBrushInfluence(
        int x,
        int z,
        Vector3 localCenter,
        float radius,
        bool hasHeightmapBrush,
        float[]? brushSamples,
        int brushWidth,
        int brushHeight,
        out float influence)
    {
        float dx = x * CellSize - localCenter.X;
        float dz = z * CellSize - localCenter.Z;
        float distanceSquared = dx * dx + dz * dz;
        float radiusSquared = radius * radius;
        if (distanceSquared > radiusSquared)
        {
            influence = 0.0f;
            return false;
        }

        float distance01 = MathF.Sqrt(distanceSquared) / radius;
        float falloff = 1f - distance01;
        falloff *= falloff * (3f - 2f * falloff);
        if (hasHeightmapBrush)
        {
            float brushU = (dx + radius) / (2.0f * radius);
            float brushV = (dz + radius) / (2.0f * radius);
            falloff *= SampleBrush(
                brushSamples!,
                brushWidth,
                brushHeight,
                brushU,
                brushV);
        }

        influence = falloff;
        return influence > 0.000001f;
    }

    private static bool IsValidHeightmapBrush(
        float[]? brushSamples,
        int brushWidth,
        int brushHeight)
    {
        return brushSamples != null &&
            brushWidth >= 2 &&
            brushHeight >= 2 &&
            brushSamples.Length >= checked(brushWidth * brushHeight);
    }

    private static float SampleBrush(
        float[] samples,
        int width,
        int height,
        float u,
        float v)
    {
        u = System.Math.Clamp(u, 0.0f, 1.0f);
        v = System.Math.Clamp(v, 0.0f, 1.0f);
        float x = u * (width - 1);
        float y = v * (height - 1);
        int x0 = System.Math.Clamp((int)MathF.Floor(x), 0, width - 1);
        int y0 = System.Math.Clamp((int)MathF.Floor(y), 0, height - 1);
        int x1 = System.Math.Min(x0 + 1, width - 1);
        int y1 = System.Math.Min(y0 + 1, height - 1);
        float tx = x - x0;
        float ty = y - y0;

        float top = samples[y0 * width + x0] +
            (samples[y0 * width + x1] - samples[y0 * width + x0]) * tx;
        float bottom = samples[y1 * width + x0] +
            (samples[y1 * width + x1] - samples[y1 * width + x0]) * tx;
        return top + (bottom - top) * ty;
    }

    private static float SampleFractalNoise(float x, float z, int seed)
    {
        float value = 0.0f;
        float amplitude = 0.5f;
        float amplitudeSum = 0.0f;
        float frequency = 1.0f;
        for (int octave = 0; octave < 4; octave++)
        {
            value += SampleValueNoise(x * frequency, z * frequency, seed + octave * 1013) * amplitude;
            amplitudeSum += amplitude;
            amplitude *= 0.5f;
            frequency *= 2.0f;
        }

        return amplitudeSum > 0.0f ? value / amplitudeSum : 0.0f;
    }

    private static float SampleValueNoise(float x, float z, int seed)
    {
        int x0 = (int)MathF.Floor(x);
        int z0 = (int)MathF.Floor(z);
        int x1 = x0 + 1;
        int z1 = z0 + 1;
        float tx = SmoothNoiseFraction(x - x0);
        float tz = SmoothNoiseFraction(z - z0);
        float top = Lerp(
            HashNoise(x0, z0, seed),
            HashNoise(x1, z0, seed),
            tx);
        float bottom = Lerp(
            HashNoise(x0, z1, seed),
            HashNoise(x1, z1, seed),
            tx);
        return Lerp(top, bottom, tz);
    }

    private static float SmoothNoiseFraction(float value)
    {
        value = System.Math.Clamp(value, 0.0f, 1.0f);
        return value * value * (3.0f - 2.0f * value);
    }

    private static float HashNoise(int x, int z, int seed)
    {
        unchecked
        {
            uint hash = (uint)x * 374761393u;
            hash += (uint)z * 668265263u;
            hash += (uint)seed * 1442695041u;
            hash = (hash ^ (hash >> 13)) * 1274126177u;
            hash ^= hash >> 16;
            return (hash & 0x00FFFFFFu) / 8388607.5f - 1.0f;
        }
    }

    private static float Lerp(float from, float to, float amount) =>
        from + (to - from) * amount;

    private static bool RayTriangle(
        Vector3 origin,
        Vector3 direction,
        Vector3 a,
        Vector3 b,
        Vector3 c,
        out float distance)
    {
        const float epsilon = 0.000001f;
        Vector3 edge1 = b - a;
        Vector3 edge2 = c - a;
        Vector3 cross = Vector3.Cross(direction, edge2);
        float determinant = Vector3.Dot(edge1, cross);
        distance = 0f;

        if (MathF.Abs(determinant) < epsilon)
            return false;

        float inverseDeterminant = 1f / determinant;
        Vector3 offset = origin - a;
        float u = Vector3.Dot(offset, cross) * inverseDeterminant;
        if (u < 0f || u > 1f)
            return false;

        Vector3 secondCross = Vector3.Cross(offset, edge1);
        float v = Vector3.Dot(direction, secondCross) * inverseDeterminant;
        if (v < 0f || u + v > 1f)
            return false;

        distance = Vector3.Dot(edge2, secondCross) * inverseDeterminant;
        return distance > 0.0001f;
    }

    public void Save(string path)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);

        writer.Write(Magic);
        writer.Write(CurrentVersion);
        writer.Write(Width);
        writer.Write(Depth);
        writer.Write(CellSize);
        writer.Write(HeightScale);
        writer.Write(HeightOffset);

        foreach (ushort sample in Samples)
            writer.Write(sample);
    }

    public static TerrainAsset Load(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);

        uint magic = reader.ReadUInt32();
        if (magic != Magic)
            throw new InvalidDataException("Invalid terrain asset magic.");

        int version = reader.ReadInt32();
        if (version != CurrentVersion)
            throw new InvalidDataException($"Unsupported terrain version: {version}");

        int width = reader.ReadInt32();
        int depth = reader.ReadInt32();
        float cellSize = reader.ReadSingle();
        float heightScale = reader.ReadSingle();
        float heightOffset = reader.ReadSingle();

        int sampleCount = checked(width * depth);
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
}
