using System.Collections.Generic;
using System.Numerics;

namespace Fuse.Scene.Terrain;

/// <summary>
/// Runtime placement and budgets for one procedural terrain object. The
/// streamer produces CPU tiles; Scene consumes them to create render meshes
/// and physics bodies on the owning OpenGL thread.
/// </summary>
public sealed class ProceduralTerrainLayer : IDisposable
{
    internal readonly Dictionary<TerrainTileCoordinate, string> LoadedGroups = [];
    internal readonly Dictionary<TerrainTileCoordinate, TerrainAsset> LoadedAssets = [];
    internal readonly HashSet<TerrainTileCoordinate> CollisionTiles = [];

    public string Id { get; }
    public ProceduralTerrainAsset Asset { get; }
    public TerrainStreamer Streamer { get; }
    public Vector3 WorldPosition { get; }
    public Quaternion WorldRotation { get; }
    public bool Visible { get; }
    public int ChunkQuads { get; }
    public string MaterialPath { get; }
    public IReadOnlyList<string> MaterialPaths { get; }
    public string TexturePath { get; }
    public Vector2 UvScale { get; }
    public Vector2 UvOffset { get; }
    public float UvRotation { get; }
    public string ParentId { get; }
    public float Friction { get; }
    public float Restitution { get; }
    public float PixelError { get; }
    public int CollisionLod { get; }
    public int CollisionTileRadius { get; }
    public int MaxTileUploadsPerFrame { get; }

    public ProceduralTerrainLayer(
        string id,
        ProceduralTerrainAsset asset,
        Vector3 worldPosition,
        Quaternion worldRotation,
        bool visible,
        int chunkQuads,
        string materialPath,
        IReadOnlyList<string>? materialPaths,
        string texturePath,
        Vector2 uvScale,
        Vector2 uvOffset,
        float uvRotation,
        string parentId,
        float friction,
        float restitution,
        float pixelError,
        int collisionLod)
    {
        Id = string.IsNullOrWhiteSpace(id) ? "procedural_terrain" : id;
        Asset = asset ?? throw new ArgumentNullException(nameof(asset));
        Asset.Settings.Validate();
        Streamer = new TerrainStreamer(Asset);
        WorldPosition = worldPosition;
        WorldRotation = Quaternion.Normalize(worldRotation);
        Visible = visible;
        ChunkQuads = System.Math.Max(1, chunkQuads);
        MaterialPath = materialPath ?? "";
        MaterialPaths = materialPaths ?? Array.Empty<string>();
        TexturePath = texturePath ?? "";
        UvScale = uvScale;
        UvOffset = uvOffset;
        UvRotation = uvRotation;
        ParentId = parentId ?? "";
        Friction = friction;
        Restitution = restitution;
        PixelError = MathF.Max(0.1f, pixelError);
        CollisionLod = System.Math.Max(0, collisionLod);
        CollisionTileRadius = System.Math.Clamp(
            Asset.Settings.CollisionTileRadius,
            0,
            Asset.Settings.StreamingTileRadius);
        MaxTileUploadsPerFrame = Asset.Settings.MaxTileUploadsPerFrame;
    }

    internal void MarkInitialTile(TerrainTile tile, bool hasCollision)
    {
        var coordinate = new TerrainTileCoordinate(tile.X, tile.Z);
        Streamer.MarkResident(tile.X, tile.Z);
        LoadedGroups[coordinate] = Id;
        LoadedAssets[coordinate] = tile.Asset;
        if (hasCollision)
            CollisionTiles.Add(coordinate);
    }

    public void Dispose() => Streamer.Dispose();
}
