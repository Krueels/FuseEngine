using System.Collections.Generic;

namespace Fuse.Scene.Terrain;

public readonly record struct TerrainPatch(
    long X,
    long Z,
    int Level,
    double MinX,
    double MinZ,
    double Size,
    float GeometricError);

/// <summary>
/// CPU-side quadtree selector used by terrain streaming/preview tools. The
/// selector is intentionally independent from OpenGL and can therefore be
/// run on a worker thread or reused by a renderer with a different mesh
/// backend.
/// </summary>
public static class TerrainQuadTree
{
    public static IReadOnlyList<TerrainPatch> SelectVisiblePatches(
        double worldSizeMeters,
        double cameraX,
        double cameraZ,
        float viewportHeight,
        float fieldOfViewDegrees,
        float pixelError,
        int maxLevel = 18,
        int maxPatches = 4096)
    {
        worldSizeMeters = System.Math.Clamp(worldSizeMeters, 1.0, 80_000_000.0);
        viewportHeight = MathF.Max(viewportHeight, 1.0f);
        pixelError = MathF.Max(pixelError, 0.1f);
        maxLevel = System.Math.Clamp(maxLevel, 0, 30);
        maxPatches = System.Math.Clamp(maxPatches, 1, 65_536);

        double halfWorld = worldSizeMeters * 0.5;
        var result = new List<TerrainPatch>(System.Math.Min(maxPatches, 256));
        double pixelScale = viewportHeight /
            (2.0 * System.Math.Tan(System.Math.Clamp(fieldOfViewDegrees, 1.0f, 170.0f) * System.Math.PI / 360.0));

        Visit(
            minX: -halfWorld,
            minZ: -halfWorld,
            size: worldSizeMeters,
            level: 0,
            patchX: 0,
            patchZ: 0);
        return result;

        void Visit(double minX, double minZ, double size, int level, long patchX, long patchZ)
        {
            if (result.Count >= maxPatches)
                return;

            double closestX = System.Math.Clamp(cameraX, minX, minX + size);
            double closestZ = System.Math.Clamp(cameraZ, minZ, minZ + size);
            double dx = cameraX - closestX;
            double dz = cameraZ - closestZ;
            double distance = System.Math.Max(System.Math.Sqrt(dx * dx + dz * dz), 1.0);
            float geometricError = (float)(size * 0.02);
            double projectedError = geometricError * pixelScale / distance;

            // A patch is split only when its geometric error is visible. The
            // camera-neighbour padding avoids a coarse ring directly beside a
            // refined patch, and the max-patch guard bounds work on weak PCs.
            if (level < maxLevel &&
                (projectedError > pixelError || distance < size * 1.25) &&
                result.Count + 4 <= maxPatches)
            {
                double childSize = size * 0.5;
                Visit(minX, minZ, childSize, level + 1, patchX * 2, patchZ * 2);
                Visit(minX + childSize, minZ, childSize, level + 1, patchX * 2 + 1, patchZ * 2);
                Visit(minX, minZ + childSize, childSize, level + 1, patchX * 2, patchZ * 2 + 1);
                Visit(minX + childSize, minZ + childSize, childSize, level + 1, patchX * 2 + 1, patchZ * 2 + 1);
                return;
            }

            result.Add(new TerrainPatch(
                patchX,
                patchZ,
                level,
                minX,
                minZ,
                size,
                geometricError));
        }
    }
}
