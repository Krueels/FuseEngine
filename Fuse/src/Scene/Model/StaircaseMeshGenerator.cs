using System.Numerics;
using Fuse.Renderer;

namespace Fuse.Scene.Model;

/// <summary>
/// One compact box used by the staircase compound collider.
/// </summary>
public readonly record struct StaircaseStep(Vector3 Center, Vector3 HalfExtents);

/// <summary>
/// Builds the staircase mesh and its collision layout from the same bounds.
/// The source object remains centered at the bounds' origin.
/// </summary>
public static class StaircaseMeshGenerator
{
    private const float MinimumExtent = 0.01f;

    public static int ResolveStepCount(Vector3 sourceHalfExtents, StaircaseSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Sanitize();

        if (settings.StepCount > 0)
            return settings.StepCount;

        float totalHeight = MathF.Max(MathF.Abs(sourceHalfExtents.Y) * 2.0f, MinimumExtent);
        int count = (int)MathF.Ceiling(totalHeight / settings.StepHeight);
        return System.Math.Clamp(count, 1, StaircaseSettings.MaximumStepCount);
    }

    public static StaircaseStep[] GenerateCollisionSteps(
        Vector3 sourceHalfExtents,
        StaircaseSettings settings)
    {
        Vector3 halfExtents = NormalizeHalfExtents(sourceHalfExtents);
        int stepCount = ResolveStepCount(halfExtents, settings);
        float totalHeight = halfExtents.Y * 2.0f;
        float stepHeight = totalHeight / stepCount;
        float stepDepth = halfExtents.Z * 2.0f / stepCount;
        bool ascendingPositiveZ = settings.Direction >= 0;

        var steps = new StaircaseStep[stepCount];
        for (int index = 0; index < stepCount; index++)
        {
            float top = -halfExtents.Y + stepHeight * (index + 1);
            float centerY = (-halfExtents.Y + top) * 0.5f;
            float stepHalfY = (top + halfExtents.Y) * 0.5f;

            float z0;
            float z1;
            if (ascendingPositiveZ)
            {
                z0 = -halfExtents.Z + stepDepth * index;
                z1 = z0 + stepDepth;
            }
            else
            {
                z1 = halfExtents.Z - stepDepth * index;
                z0 = z1 - stepDepth;
            }

            steps[index] = new StaircaseStep(
                new Vector3(0.0f, centerY, (z0 + z1) * 0.5f),
                new Vector3(halfExtents.X, stepHalfY, stepDepth * 0.5f));
        }

        return steps;
    }

    public static MeshData Generate(
        Vector3 sourceHalfExtents,
        StaircaseSettings settings)
    {
        Vector3 halfExtents = NormalizeHalfExtents(sourceHalfExtents);
        int stepCount = ResolveStepCount(halfExtents, settings);
        float totalHeight = halfExtents.Y * 2.0f;
        float stepHeight = totalHeight / stepCount;
        float stepDepth = halfExtents.Z * 2.0f / stepCount;
        bool ascendingPositiveZ = settings.Direction >= 0;

        var vertices = new List<Vertex>(stepCount * 24 + 8);
        var indices = new List<uint>(stepCount * 36 + 12);
        var lineIndices = new List<uint>(stepCount * 48 + 16);

        for (int index = 0; index < stepCount; index++)
        {
            float previousTop = -halfExtents.Y + stepHeight * index;
            float top = -halfExtents.Y + stepHeight * (index + 1);

            float z0;
            float z1;
            if (ascendingPositiveZ)
            {
                z0 = -halfExtents.Z + stepDepth * index;
                z1 = z0 + stepDepth;
            }
            else
            {
                z1 = halfExtents.Z - stepDepth * index;
                z0 = z1 - stepDepth;
            }

            AddTopQuad(vertices, indices, lineIndices, halfExtents.X, top, z0, z1);
            AddSideQuad(vertices, indices, lineIndices, -halfExtents.X, -halfExtents.Y, top, z0, z1, false);
            AddSideQuad(vertices, indices, lineIndices, halfExtents.X, -halfExtents.Y, top, z0, z1, true);

            if (index == 0)
            {
                if (ascendingPositiveZ)
                    AddVerticalQuad(vertices, indices, lineIndices, halfExtents.X, -halfExtents.Z, -halfExtents.Y, top, false);
                else
                    AddVerticalQuad(vertices, indices, lineIndices, halfExtents.X, halfExtents.Z, -halfExtents.Y, top, true);
            }
            else if (ascendingPositiveZ)
            {
                AddVerticalQuad(vertices, indices, lineIndices, halfExtents.X, z0, previousTop, top, false);
            }
            else
            {
                AddVerticalQuad(vertices, indices, lineIndices, halfExtents.X, z1, previousTop, top, true);
            }

            if (index == stepCount - 1)
            {
                if (ascendingPositiveZ)
                    AddVerticalQuad(vertices, indices, lineIndices, halfExtents.X, halfExtents.Z, -halfExtents.Y, top, true);
                else
                    AddVerticalQuad(vertices, indices, lineIndices, halfExtents.X, -halfExtents.Z, -halfExtents.Y, top, false);
            }
        }

        AddBottomQuad(
            vertices,
            indices,
            lineIndices,
            halfExtents.X,
            -halfExtents.Y,
            -halfExtents.Z,
            halfExtents.Z);

        return new MeshData(
            vertices.ToArray(),
            indices.ToArray(),
            lineIndices.ToArray(),
            [new MeshPart(0, (uint)indices.Count, 0)]);
    }

    private static Vector3 NormalizeHalfExtents(Vector3 sourceHalfExtents) => new(
        MathF.Max(MathF.Abs(sourceHalfExtents.X), MinimumExtent),
        MathF.Max(MathF.Abs(sourceHalfExtents.Y), MinimumExtent),
        MathF.Max(MathF.Abs(sourceHalfExtents.Z), MinimumExtent));

    private static void AddTopQuad(
        List<Vertex> vertices,
        List<uint> indices,
        List<uint> lineIndices,
        float halfWidth,
        float y,
        float z0,
        float z1)
    {
        float depth = MathF.Abs(z1 - z0);
        if (z1 >= z0)
        {
            AddQuad(
                vertices, indices, lineIndices,
                new Vector3(-halfWidth, y, z0),
                new Vector3(-halfWidth, y, z1),
                new Vector3(halfWidth, y, z1),
                new Vector3(halfWidth, y, z0),
                Vector3.UnitY,
                halfWidth * 2.0f,
                depth);
        }
        else
        {
            AddQuad(
                vertices, indices, lineIndices,
                new Vector3(-halfWidth, y, z0),
                new Vector3(halfWidth, y, z0),
                new Vector3(halfWidth, y, z1),
                new Vector3(-halfWidth, y, z1),
                Vector3.UnitY,
                halfWidth * 2.0f,
                depth);
        }
    }

    private static void AddSideQuad(
        List<Vertex> vertices,
        List<uint> indices,
        List<uint> lineIndices,
        float x,
        float bottom,
        float top,
        float z0,
        float z1,
        bool positiveX)
    {
        Vector3 normal = positiveX ? Vector3.UnitX : -Vector3.UnitX;
        float width = MathF.Abs(z1 - z0);
        if (positiveX == (z1 >= z0))
        {
            AddQuad(
                vertices, indices, lineIndices,
                new Vector3(x, bottom, z0),
                new Vector3(x, top, z0),
                new Vector3(x, top, z1),
                new Vector3(x, bottom, z1),
                normal,
                width,
                top - bottom);
        }
        else
        {
            AddQuad(
                vertices, indices, lineIndices,
                new Vector3(x, bottom, z0),
                new Vector3(x, bottom, z1),
                new Vector3(x, top, z1),
                new Vector3(x, top, z0),
                normal,
                width,
                top - bottom);
        }
    }

    private static void AddVerticalQuad(
        List<Vertex> vertices,
        List<uint> indices,
        List<uint> lineIndices,
        float halfWidth,
        float z,
        float bottom,
        float top,
        bool positiveZ)
    {
        Vector3 normal = positiveZ ? Vector3.UnitZ : -Vector3.UnitZ;
        if (positiveZ)
        {
            AddQuad(
                vertices, indices, lineIndices,
                new Vector3(-halfWidth, bottom, z),
                new Vector3(halfWidth, bottom, z),
                new Vector3(halfWidth, top, z),
                new Vector3(-halfWidth, top, z),
                normal,
                halfWidth * 2.0f,
                top - bottom);
        }
        else
        {
            AddQuad(
                vertices, indices, lineIndices,
                new Vector3(-halfWidth, bottom, z),
                new Vector3(-halfWidth, top, z),
                new Vector3(halfWidth, top, z),
                new Vector3(halfWidth, bottom, z),
                normal,
                halfWidth * 2.0f,
                top - bottom);
        }
    }

    private static void AddBottomQuad(
        List<Vertex> vertices,
        List<uint> indices,
        List<uint> lineIndices,
        float halfWidth,
        float y,
        float z0,
        float z1)
    {
        AddQuad(
            vertices, indices, lineIndices,
            new Vector3(-halfWidth, y, z0),
            new Vector3(halfWidth, y, z0),
            new Vector3(halfWidth, y, z1),
            new Vector3(-halfWidth, y, z1),
            -Vector3.UnitY,
            halfWidth * 2.0f,
            MathF.Abs(z1 - z0));
    }

    private static void AddQuad(
        List<Vertex> vertices,
        List<uint> indices,
        List<uint> lineIndices,
        Vector3 p0,
        Vector3 p1,
        Vector3 p2,
        Vector3 p3,
        Vector3 normal,
        float uScale,
        float vScale)
    {
        uint start = (uint)vertices.Count;
        Vector2 uv1 = new(uScale, 0.0f);
        Vector2 uv2 = new(uScale, vScale);
        Vector2 uv3 = new(0.0f, vScale);

        vertices.Add(new Vertex { Position = p0, TexCoord = Vector2.Zero, Normal = normal });
        vertices.Add(new Vertex { Position = p1, TexCoord = uv1, Normal = normal });
        vertices.Add(new Vertex { Position = p2, TexCoord = uv2, Normal = normal });
        vertices.Add(new Vertex { Position = p3, TexCoord = uv3, Normal = normal });

        indices.Add(start);
        indices.Add(start + 1);
        indices.Add(start + 2);
        indices.Add(start);
        indices.Add(start + 2);
        indices.Add(start + 3);

        lineIndices.Add(start);
        lineIndices.Add(start + 1);
        lineIndices.Add(start + 1);
        lineIndices.Add(start + 2);
        lineIndices.Add(start + 2);
        lineIndices.Add(start + 3);
        lineIndices.Add(start + 3);
        lineIndices.Add(start);
    }
}
