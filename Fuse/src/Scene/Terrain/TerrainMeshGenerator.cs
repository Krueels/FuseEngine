using System.Collections.Generic;
using System.Numerics;
using Fuse.Renderer;
using Fuse.Scene.Model;

namespace Fuse.Scene.Terrain;

[Flags]
public enum TerrainEdgeFlags
{
    None = 0,
    Top = 1 << 0,
    Bottom = 1 << 1,
    Left = 1 << 2,
    Right = 1 << 3,
    All = Top | Bottom | Left | Right
}

public static class TerrainMeshGenerator
{
    public const int MaxLodLevels = 5;

    public static MeshData Generate(
        TerrainAsset terrain,
        int chunkX,
        int chunkZ,
        int chunkQuads)
    {
        return Generate(terrain, chunkX, chunkZ, chunkQuads, 0, TerrainEdgeFlags.None);
    }

    public static MeshData Generate(
        TerrainAsset terrain,
        int chunkX,
        int chunkZ,
        int chunkQuads,
        int lodLevel,
        TerrainEdgeFlags stitchEdges)
    {
        GetChunkInfo(terrain, chunkX, chunkZ, chunkQuads,
            out int startX, out int startZ, out int quadsX, out int quadsZ);

        int sampleStep = GetSampleStep(lodLevel);
        int stitchStep = checked(sampleStep * 2);

        int[] xSamples = BuildSampleAxis(quadsX, sampleStep);
        int[] zSamples = BuildSampleAxis(quadsZ, sampleStep);
        int verticesX = xSamples.Length;
        int verticesZ = zSamples.Length;

        var vertices = new List<Vertex>(verticesX * verticesZ);
        for (int z = 0; z < verticesZ; z++)
        {
            for (int x = 0; x < verticesX; x++)
            {
                int globalX = startX + xSamples[x];
                int globalZ = startZ + zSamples[z];
                float height = terrain.GetHeight(globalX, globalZ);

                if (z == 0 && (stitchEdges & TerrainEdgeFlags.Top) != 0)
                {
                    height = SampleSimplifiedHeight(
                        terrain,
                        startX,
                        startZ,
                        quadsX,
                        quadsZ,
                        xSamples[x],
                        0,
                        stitchStep);
                }
                else if (z == verticesZ - 1 && (stitchEdges & TerrainEdgeFlags.Bottom) != 0)
                {
                    height = SampleSimplifiedHeight(
                        terrain,
                        startX,
                        startZ,
                        quadsX,
                        quadsZ,
                        xSamples[x],
                        quadsZ,
                        stitchStep);
                }
                else if (x == 0 && (stitchEdges & TerrainEdgeFlags.Left) != 0)
                {
                    height = SampleSimplifiedHeight(
                        terrain,
                        startX,
                        startZ,
                        quadsX,
                        quadsZ,
                        0,
                        zSamples[z],
                        stitchStep);
                }
                else if (x == verticesX - 1 && (stitchEdges & TerrainEdgeFlags.Right) != 0)
                {
                    height = SampleSimplifiedHeight(
                        terrain,
                        startX,
                        startZ,
                        quadsX,
                        quadsZ,
                        quadsX,
                        zSamples[z],
                        stitchStep);
                }

                float left = terrain.GetHeight(globalX - 1, globalZ);
                float right = terrain.GetHeight(globalX + 1, globalZ);
                float down = terrain.GetHeight(globalX, globalZ - 1);
                float up = terrain.GetHeight(globalX, globalZ + 1);
                Vector3 normal = Vector3.Normalize(new Vector3(
                    (left - right) / (terrain.CellSize * 2f),
                    1f,
                    (down - up) / (terrain.CellSize * 2f)));

                float u = globalX / (float)(terrain.Width - 1);
                float v = globalZ / (float)(terrain.Depth - 1);
                vertices.Add(new Vertex
                {
                    Position = new Vector3(
                        xSamples[x] * terrain.CellSize,
                        height,
                        zSamples[z] * terrain.CellSize),
                    TexCoord = new Vector2(u, v),
                    Normal = normal
                });
            }
        }

        var indices = new List<uint>((verticesX - 1) * (verticesZ - 1) * 6);
        for (int z = 0; z < verticesZ - 1; z++)
        {
            for (int x = 0; x < verticesX - 1; x++)
            {
                uint a = (uint)(z * verticesX + x);
                uint b = a + 1;
                uint d = (uint)((z + 1) * verticesX + x);
                uint c = d + 1;

                indices.Add(b);
                indices.Add(a);
                indices.Add(c);
                indices.Add(d);
                indices.Add(c);
                indices.Add(a);
            }
        }

        return new MeshData(vertices.ToArray(), indices.ToArray());
    }

    public static int GetLodCount(
        TerrainAsset terrain,
        int chunkX,
        int chunkZ,
        int chunkQuads)
    {
        GetChunkInfo(terrain, chunkX, chunkZ, chunkQuads,
            out _, out _, out int quadsX, out int quadsZ);

        int smallestDimension = System.Math.Min(quadsX, quadsZ);
        int count = 1;
        while (count < MaxLodLevels && smallestDimension >= 4)
        {
            count++;
            smallestDimension = (smallestDimension + 1) / 2;
        }

        return count;
    }

    public static float CalculateGeometricError(
        TerrainAsset terrain,
        int chunkX,
        int chunkZ,
        int chunkQuads,
        int lodLevel)
    {
        GetChunkInfo(terrain, chunkX, chunkZ, chunkQuads,
            out int startX, out int startZ, out int quadsX, out int quadsZ);
        if (lodLevel <= 0)
            return 0f;

        int sampleStep = GetSampleStep(lodLevel);

        float maximumError = 0f;
        for (int z = 0; z <= quadsZ; z++)
        {
            for (int x = 0; x <= quadsX; x++)
            {
                float original = terrain.GetHeight(startX + x, startZ + z);
                float simplified = SampleSimplifiedHeight(
                    terrain,
                    startX,
                    startZ,
                    quadsX,
                    quadsZ,
                    x,
                    z,
                    sampleStep);
                maximumError = MathF.Max(maximumError, MathF.Abs(original - simplified));
            }
        }

        return maximumError;
    }

    private static float SampleSimplifiedHeight(
        TerrainAsset terrain,
        int startX,
        int startZ,
        int quadsX,
        int quadsZ,
        int localX,
        int localZ,
        int sampleStep)
    {
        int x0 = localX / sampleStep * sampleStep;
        int z0 = localZ / sampleStep * sampleStep;
        int x1 = System.Math.Min(x0 + sampleStep, quadsX);
        int z1 = System.Math.Min(z0 + sampleStep, quadsZ);
        float tx = x1 == x0 ? 0f : (localX - x0) / (float)(x1 - x0);
        float tz = z1 == z0 ? 0f : (localZ - z0) / (float)(z1 - z0);

        float h00 = terrain.GetHeight(startX + x0, startZ + z0);
        float h10 = terrain.GetHeight(startX + x1, startZ + z0);
        float h01 = terrain.GetHeight(startX + x0, startZ + z1);
        float h11 = terrain.GetHeight(startX + x1, startZ + z1);
        float near = h00 + (h10 - h00) * tx;
        float far = h01 + (h11 - h01) * tx;
        return near + (far - near) * tz;
    }

    private static int GetSampleStep(int lodLevel)
    {
        int sampleStep = 1;
        for (int i = 0; i < lodLevel; i++)
            sampleStep = checked(sampleStep * 2);
        return sampleStep;
    }

    private static int[] BuildSampleAxis(int quads, int sampleStep)
    {
        var samples = new List<int>(quads / sampleStep + 2);
        for (int value = 0; value < quads; value += sampleStep)
            samples.Add(value);
        if (samples.Count == 0 || samples[^1] != quads)
            samples.Add(quads);
        return samples.ToArray();
    }

    private static void GetChunkInfo(
        TerrainAsset terrain,
        int chunkX,
        int chunkZ,
        int chunkQuads,
        out int startX,
        out int startZ,
        out int quadsX,
        out int quadsZ)
    {
        if (chunkQuads < 1)
            throw new ArgumentOutOfRangeException(nameof(chunkQuads));

        startX = chunkX * chunkQuads;
        startZ = chunkZ * chunkQuads;
        if (startX < 0 || startZ < 0 ||
            startX >= terrain.Width - 1 ||
            startZ >= terrain.Depth - 1)
        {
            throw new ArgumentOutOfRangeException(
                "Chunk coordinates are outside the terrain.");
        }

        quadsX = System.Math.Min(chunkQuads, terrain.Width - 1 - startX);
        quadsZ = System.Math.Min(chunkQuads, terrain.Depth - 1 - startZ);
    }

}
