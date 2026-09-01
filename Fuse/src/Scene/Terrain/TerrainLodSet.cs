using Fuse.Math;
using Fuse.Renderer;
using Fuse.Scene.Model;

namespace Fuse.Scene.Terrain;

public sealed class TerrainLodSet : IDisposable
{
    private bool _disposed;
    private readonly Action<int, TerrainEdgeFlags>? _applyStitching;

    public Mesh[] Meshes { get; }
    public float[] GeometricErrors { get; }
    public AABB LocalBounds { get; private set; }
    public int CurrentLevel { get; private set; }
    public TerrainEdgeFlags CurrentStitchEdges { get; private set; }

    public Mesh CurrentMesh => Meshes[CurrentLevel];

    public TerrainLodSet(
        Mesh[] meshes,
        float[] geometricErrors,
        AABB localBounds,
        Action<int, TerrainEdgeFlags>? applyStitching = null)
    {
        if (meshes.Length == 0)
            throw new ArgumentException("A terrain LOD set must contain at least one mesh.", nameof(meshes));
        if (meshes.Length != geometricErrors.Length)
            throw new ArgumentException("Terrain LOD meshes and errors must have the same length.");

        Meshes = meshes;
        GeometricErrors = geometricErrors;
        LocalBounds = localBounds;
        CurrentLevel = 0;
        CurrentStitchEdges = TerrainEdgeFlags.None;
        _applyStitching = applyStitching;
    }

    public bool TrySetLevel(int level)
    {
        return TrySetState(level, CurrentStitchEdges);
    }

    public bool TrySetState(int level, TerrainEdgeFlags stitchEdges)
    {
        int clamped = System.Math.Clamp(level, 0, Meshes.Length - 1);
        if (clamped == CurrentLevel && stitchEdges == CurrentStitchEdges)
            return false;

        CurrentLevel = clamped;
        CurrentStitchEdges = stitchEdges;
        _applyStitching?.Invoke(CurrentLevel, CurrentStitchEdges);
        return true;
    }

    /// <summary>
    /// Rebuilds the CPU geometry for every render LOD while keeping the
    /// existing GPU meshes and scene entities alive. This is used while
    /// sculpting a terrain so a brush stroke does not recreate the scene.
    /// </summary>
    public void RefreshGeometry(Func<int, TerrainEdgeFlags, MeshData> generate)
    {
        if (_disposed)
            return;

        if (generate == null)
            throw new ArgumentNullException(nameof(generate));

        AABB bounds = new();
        for (int lodLevel = 0; lodLevel < Meshes.Length; lodLevel++)
        {
            TerrainEdgeFlags stitchEdges = lodLevel == CurrentLevel
                ? CurrentStitchEdges
                : TerrainEdgeFlags.None;
            MeshData data = generate(lodLevel, stitchEdges);
            Meshes[lodLevel].UpdateVertices(data.Vertices, data.Indices);
            bounds.Grow(Meshes[lodLevel].LocalBounds);
        }

        LocalBounds = bounds;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        foreach (Mesh mesh in Meshes)
            mesh.Dispose();
    }
}
