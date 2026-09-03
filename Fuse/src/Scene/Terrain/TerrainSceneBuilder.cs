using System;
using System.Collections.Generic;
using System.Numerics;
using Fuse.AssetManagement;
using Fuse.Physics;
using Fuse.Renderer;
using Fuse.Scene.Model;

namespace Fuse.Scene.Terrain;

/// <summary>
/// Builds the render and collision representation of a terrain asset.
/// Runtime and Blowtorch both use this path so their chunk boundaries and
/// local-space conventions remain identical.
/// </summary>
public static class TerrainSceneBuilder
{
    public const int DefaultChunkQuads = 32;
    public const float DefaultPixelError = 5.0f;
    // Native square heightfields always use the complete heightmap. This value
    // remains only as a backwards-compatible serialized setting for terrains
    // that cannot be represented by the native square heightfield path.
    public const int DefaultCollisionLod = 0;

    public static int AddToScene(
        Fuse.Renderer.Scene scene,
        TerrainAsset terrain,
        string id,
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
        AssetManager assets,
        PhysicsWorld? physics = null,
        IList<RigidBody>? createdBodies = null,
        string parentId = "",
        float friction = 0.5f,
        float restitution = 0.0f,
        float pixelError = DefaultPixelError,
        int collisionLod = DefaultCollisionLod,
        bool forceLod0 = false)
    {
        return AddToScene(
            scene,
            TerrainTileSetAsset.FromSingle(terrain),
            id,
            worldPosition,
            worldRotation,
            visible,
            chunkQuads,
            materialPath,
            materialPaths,
            texturePath,
            uvScale,
            uvOffset,
            uvRotation,
            assets,
            physics,
            createdBodies,
            parentId,
            friction,
            restitution,
            pixelError,
            collisionLod,
            forceLod0);
    }

    public static int AddToScene(
        Fuse.Renderer.Scene scene,
        TerrainTileSetAsset terrainSet,
        string id,
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
        AssetManager assets,
        PhysicsWorld? physics = null,
        IList<RigidBody>? createdBodies = null,
        string parentId = "",
        float friction = 0.5f,
        float restitution = 0.0f,
        float pixelError = DefaultPixelError,
        int collisionLod = DefaultCollisionLod,
        bool forceLod0 = false)
    {
        if (terrainSet == null)
            throw new ArgumentNullException(nameof(terrainSet));

        chunkQuads = System.Math.Max(1, chunkQuads);
        materialPaths ??= Array.Empty<string>();
        pixelError = MathF.Max(0.1f, pixelError);
        collisionLod = System.Math.Max(0, collisionLod);
        int chunkCount = 0;

        foreach (TerrainTile tile in terrainSet.Tiles)
        {
            Vector3 tilePosition = worldPosition + Vector3.Transform(
                terrainSet.GetTileOrigin(tile),
                worldRotation);
            chunkCount += AddTileToScene(
                scene,
                tile,
                terrainSet,
                id,
                tilePosition,
                worldRotation,
                visible,
                chunkQuads,
                materialPath,
                materialPaths,
                texturePath,
                uvScale,
                uvOffset,
                uvRotation,
                assets,
                physics,
                createdBodies,
                parentId,
                friction,
                restitution,
                pixelError,
                collisionLod,
                forceLod0);
        }

        return chunkCount;
    }

    private static int AddTileToScene(
        Fuse.Renderer.Scene scene,
        TerrainTile tile,
        TerrainTileSetAsset terrainSet,
        string terrainId,
        Vector3 worldPosition,
        Quaternion worldRotation,
        bool visible,
        int chunkQuads,
        string materialPath,
        IReadOnlyList<string> materialPaths,
        string texturePath,
        Vector2 uvScale,
        Vector2 uvOffset,
        float uvRotation,
        AssetManager assets,
        PhysicsWorld? physics,
        IList<RigidBody>? createdBodies,
        string parentId,
        float friction,
        float restitution,
        float pixelError,
        int collisionLod,
        bool forceLod0)
    {
        TerrainAsset terrain = tile.Asset;
        int chunksX = (terrain.Width - 2 + chunkQuads) / chunkQuads;
        int chunksZ = (terrain.Depth - 2 + chunkQuads) / chunkQuads;
        if (chunksX <= 0 || chunksZ <= 0)
            return 0;

        // A native heightfield represents the complete tile and avoids the
        // seam/diagonal errors caused by independent chunk meshes. A tile set
        // receives one complete body per tile, all using the same grid space.
        RigidBody? nativeTerrainBody = null;
        if (physics != null && visible && CanUseNativeHeightField(terrain))
        {
            string collisionId = GetTileId(terrainId, tile) + "_collision";
            nativeTerrainBody = CreateNativeHeightFieldBody(
                scene,
                terrain,
                collisionId,
                terrainId,
                tile,
                worldPosition,
                worldRotation,
                physics,
                createdBodies,
                friction,
                restitution);
        }

        int chunkCount = 0;
        for (int chunkZ = 0; chunkZ < chunksZ; chunkZ++)
        {
            for (int chunkX = 0; chunkX < chunksX; chunkX++)
            {
                int terrainChunkX = chunkX;
                int terrainChunkZ = chunkZ;
                int lodCount = TerrainMeshGenerator.GetLodCount(
                    terrain,
                    terrainChunkX,
                    terrainChunkZ,
                    chunkQuads);
                var lodMeshes = new Mesh[lodCount];
                var lodErrors = new float[lodCount];
                for (int lodLevel = 0; lodLevel < lodCount; lodLevel++)
                {
                    MeshData lodData = TerrainMeshGenerator.Generate(
                        terrain,
                        terrainChunkX,
                        terrainChunkZ,
                        chunkQuads,
                        lodLevel,
                        TerrainEdgeFlags.None);
                    lodMeshes[lodLevel] = new Mesh(
                        assets.Gl,
                        lodData.Vertices,
                        lodData.Indices,
                        null,
                        lodData.Parts);
                    lodErrors[lodLevel] = TerrainMeshGenerator.CalculateGeometricError(
                        terrain,
                        terrainChunkX,
                        terrainChunkZ,
                        chunkQuads,
                        lodLevel);
                }

                var localBounds = lodMeshes[0].LocalBounds;
                for (int lodLevel = 1; lodLevel < lodMeshes.Length; lodLevel++)
                    localBounds.Grow(lodMeshes[lodLevel].LocalBounds);

                void ApplyStitching(int lodLevel, TerrainEdgeFlags stitchEdges)
                {
                    MeshData stitchedData = TerrainMeshGenerator.Generate(
                        terrain,
                        terrainChunkX,
                        terrainChunkZ,
                        chunkQuads,
                        lodLevel,
                        stitchEdges);
                    lodMeshes[lodLevel].UpdateVertices(
                        stitchedData.Vertices,
                        stitchedData.Indices);
                }

                var terrainLod = new TerrainLodSet(
                    lodMeshes,
                    lodErrors,
                    localBounds,
                    ApplyStitching);

                int startX = terrainChunkX * chunkQuads;
                int startZ = terrainChunkZ * chunkQuads;
                Vector3 localOrigin = new(
                    startX * terrain.CellSize,
                    0f,
                    startZ * terrain.CellSize);
                Vector3 chunkPosition = worldPosition + Vector3.Transform(localOrigin, worldRotation);

                RigidBody? body = null;
                if (nativeTerrainBody == null && physics != null && visible)
                {
                    // Rectangular/invalid assets use a mesh fallback, but it
                    // must still use the full heightmap resolution. The native
                    // path above is used for the normal square terrain case.
                    MeshData collisionData = TerrainMeshGenerator.Generate(
                        terrain,
                        terrainChunkX,
                        terrainChunkZ,
                        chunkQuads,
                        0,
                        TerrainEdgeFlags.None);
                    Vector3[] vertices = new Vector3[collisionData.Vertices.Length];
                    for (int i = 0; i < vertices.Length; i++)
                        vertices[i] = collisionData.Vertices[i].Position;

                    body = new RigidBody()
                        .SetTrimesh(vertices, collisionData.Indices)
                        .SetPosition(chunkPosition)
                        .SetRotation(worldRotation)
                        .SetMass(0f)
                        .SetFriction(friction)
                        .SetRestitution(restitution);
                    body.Build(physics);

                    if (!body.IsBuilt)
                    {
                        body.Destroy();
                        body = null;
                    }
                    else
                    {
                        createdBodies?.Add(body);
                    }
                }

                string chunkId = $"{GetTileId(terrainId, tile)}_chunk_{terrainChunkX}_{terrainChunkZ}";
                Entity entity = scene.Add(terrainLod.CurrentMesh, chunkId, body);
                entity.TerrainLod = terrainLod;
                entity.ForceTerrainLod0 = forceLod0;
                entity.TerrainPixelError = pixelError;
                entity.TerrainChunkGroupId = terrainId;
                entity.TerrainTileX = tile.X;
                entity.TerrainTileZ = tile.Z;
                entity.TerrainLocalChunkX = terrainChunkX;
                entity.TerrainLocalChunkZ = terrainChunkZ;
                // LOD adjacency uses global chunk coordinates so a tile edge
                // is treated exactly like an ordinary chunk edge.
                entity.TerrainChunkX = checked(tile.X * chunksX + terrainChunkX);
                entity.TerrainChunkZ = checked(tile.Z * chunksZ + terrainChunkZ);
                entity.MeshOwnedByEntity = true;
                entity.MeshKey = chunkId;
                entity.ParentId = parentId;
                entity.Transform.Position = chunkPosition;
                entity.Transform.Rotation = worldRotation;
                entity.Transform.Scale = Vector3.One;
                entity.ModelScale = Vector3.One;
                entity.Visible = visible;
                entity.MaterialPath = materialPath;
                entity.MaterialPaths = new List<string>(materialPaths);
                entity.Material = assets.TryGetMaterial(materialPath);
                foreach (string slotPath in materialPaths)
                    entity.Materials.Add(assets.TryGetMaterial(slotPath));
                entity.TexturePath = texturePath;
                entity.UvScale = uvScale;
                entity.UvOffset = uvOffset;
                entity.UvRotation = uvRotation;

                bool hasMaterial = entity.Material != null ||
                    entity.Materials.Exists(material => material != null);
                if (!hasMaterial && !string.IsNullOrWhiteSpace(texturePath))
                    entity.Material = assets.GetLegacyMaterial(texturePath);

                chunkCount++;
            }
        }

        return chunkCount;
    }

    private static string GetTileId(string terrainId, TerrainTile tile) =>
        $"{terrainId}_tile_{tile.X}_{tile.Z}";

    private static bool CanUseNativeHeightField(TerrainAsset terrain)
    {
        if (terrain.Width < 3 || terrain.Width != terrain.Depth || terrain.HeightScale <= 0.0f)
            return false;

        ulong expectedSampleCount = (ulong)terrain.Width * (ulong)terrain.Depth;
        return expectedSampleCount == (ulong)terrain.Samples.Length;
    }

    private static RigidBody? CreateNativeHeightFieldBody(
        Fuse.Renderer.Scene scene,
        TerrainAsset terrain,
        string collisionId,
        string terrainGroupId,
        TerrainTile tile,
        Vector3 worldPosition,
        Quaternion worldRotation,
        PhysicsWorld physics,
        IList<RigidBody>? createdBodies,
        float friction,
        float restitution)
    {
        int sampleCount = terrain.Width;
        float[] normalizedSamples = new float[terrain.Samples.Length];
        for (int i = 0; i < normalizedSamples.Length; i++)
            normalizedSamples[i] = terrain.Samples[i] / 65535.0f;

        RigidBody body = new RigidBody()
            .SetHeightField(
                normalizedSamples,
                new Vector3(0.0f, terrain.HeightOffset, 0.0f),
                new Vector3(terrain.CellSize, terrain.HeightScale, terrain.CellSize),
                (uint)sampleCount)
            .SetPosition(worldPosition)
            .SetRotation(worldRotation)
            .SetMass(0.0f)
            .SetFriction(friction)
            .SetRestitution(restitution);
        body.Build(physics);

        if (!body.IsBuilt)
        {
            body.Destroy();
            return null;
        }

        // Keep the physics body in the scene/body map without attaching it to
        // a parent transform a second time. It is already in world space.
        Entity collisionEntity = scene.Add(null, collisionId, body);
        collisionEntity.MeshKey = string.Empty;
        collisionEntity.ParentId = string.Empty;
        collisionEntity.Transform.Position = worldPosition;
        collisionEntity.Transform.Rotation = worldRotation;
        collisionEntity.Transform.Scale = Vector3.One;
        collisionEntity.ModelScale = Vector3.One;
        collisionEntity.Visible = false;
        collisionEntity.TerrainChunkGroupId = terrainGroupId;
        collisionEntity.TerrainTileX = tile.X;
        collisionEntity.TerrainTileZ = tile.Z;

        createdBodies?.Add(body);
        return body;
    }

    /// <summary>
    /// Adds only the native collision body for an already-rendered procedural
    /// tile. This lets the streamer keep visual tiles around the player while
    /// limiting Jolt bodies to the configured collision radius.
    /// </summary>
    public static bool AddCollisionToScene(
        Fuse.Renderer.Scene scene,
        TerrainTile tile,
        string terrainGroupId,
        Vector3 worldPosition,
        Quaternion worldRotation,
        PhysicsWorld physics,
        IList<RigidBody> createdBodies,
        float friction,
        float restitution)
    {
        if (scene == null || tile == null || string.IsNullOrWhiteSpace(terrainGroupId))
            return false;
        if (!CanUseNativeHeightField(tile.Asset))
            return false;

        string collisionId = $"{GetTileId(terrainGroupId, tile)}_collision";
        foreach (Entity entity in scene.Entities)
        {
            if (entity.Id.Equals(collisionId, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return CreateNativeHeightFieldBody(
                scene,
                tile.Asset,
                collisionId,
                terrainGroupId,
                tile,
                worldPosition + Vector3.Transform(
                    new Vector3(
                        tile.X * (tile.Asset.Width - 1) * tile.Asset.CellSize,
                        0.0f,
                        tile.Z * (tile.Asset.Depth - 1) * tile.Asset.CellSize),
                    worldRotation),
                worldRotation,
                physics,
                createdBodies,
                friction,
                restitution) != null;
    }

    public static int RemoveCollisionFromScene(
        Fuse.Renderer.Scene scene,
        string terrainGroupId,
        TerrainTileCoordinate coordinate,
        PhysicsWorld physics,
        IList<RigidBody> sceneBodies)
    {
        List<RigidBody> removedBodies = scene.RemoveEntities(entity =>
            entity.TerrainLod == null &&
            entity.TerrainChunkGroupId.Equals(terrainGroupId, StringComparison.OrdinalIgnoreCase) &&
            (long)entity.TerrainTileX == coordinate.X &&
            (long)entity.TerrainTileZ == coordinate.Z);

        foreach (RigidBody body in removedBodies)
        {
            if (body.IsBuilt)
            {
                physics.DestroyBody(body.Native);
                body.Destroy();
            }
            sceneBodies.Remove(body);
        }

        return removedBodies.Count;
    }

    /// <summary>
    /// Refreshes only chunks touched by a sculpt operation. Render meshes are
    /// updated in place, so the scene hierarchy, entities and materials stay
    /// alive while the brush is being dragged.
    /// </summary>
    public static int RefreshTerrainGeometry(
        Fuse.Renderer.Scene scene,
        TerrainAsset terrain,
        string terrainId,
        int chunkQuads,
        Vector3 localCenter,
        float radius)
    {
        return RefreshTerrainGeometry(
            scene,
            TerrainTileSetAsset.FromSingle(terrain),
            terrainId,
            chunkQuads,
            localCenter,
            radius);
    }

    public static int RefreshTerrainGeometry(
        Fuse.Renderer.Scene scene,
        TerrainTileSetAsset terrainSet,
        string terrainId,
        int chunkQuads,
        Vector3 localCenter,
        float radius)
    {
        chunkQuads = System.Math.Max(1, chunkQuads);
        int refreshed = 0;
        float samplePadding = MathF.Max(terrainSet.CellSize, 0.01f) * 2.0f;
        float influenceRadius = MathF.Max(0.0f, radius) + samplePadding;

        foreach (TerrainTile tile in terrainSet.Tiles)
        {
            TerrainAsset terrain = tile.Asset;
            int chunksX = (terrain.Width - 2 + chunkQuads) / chunkQuads;
            int chunksZ = (terrain.Depth - 2 + chunkQuads) / chunkQuads;
            if (chunksX <= 0 || chunksZ <= 0)
                continue;

            Vector3 tileOrigin = terrainSet.GetTileOrigin(tile);
            Vector3 tileCenter = localCenter - tileOrigin;
            float tileMaxX = terrainSet.TileWorldWidth;
            float tileMaxZ = terrainSet.TileWorldDepth;
            if (tileCenter.X < -influenceRadius ||
                tileCenter.X > tileMaxX + influenceRadius ||
                tileCenter.Z < -influenceRadius ||
                tileCenter.Z > tileMaxZ + influenceRadius)
                continue;

            float chunkWorldSize = MathF.Max(terrain.CellSize, 0.01f) * chunkQuads;
            int minChunkX = System.Math.Clamp(
                (int)MathF.Floor((tileCenter.X - influenceRadius) / chunkWorldSize),
                0,
                chunksX - 1);
            int maxChunkX = System.Math.Clamp(
                (int)MathF.Floor((tileCenter.X + influenceRadius) / chunkWorldSize),
                0,
                chunksX - 1);
            int minChunkZ = System.Math.Clamp(
                (int)MathF.Floor((tileCenter.Z - influenceRadius) / chunkWorldSize),
                0,
                chunksZ - 1);
            int maxChunkZ = System.Math.Clamp(
                (int)MathF.Floor((tileCenter.Z + influenceRadius) / chunkWorldSize),
                0,
                chunksZ - 1);

            foreach (Entity entity in scene.Entities)
            {
                if (entity.TerrainLod == null ||
                    !string.Equals(entity.TerrainChunkGroupId, terrainId, StringComparison.OrdinalIgnoreCase) ||
                    entity.TerrainTileX != tile.X ||
                    entity.TerrainTileZ != tile.Z)
                    continue;

                int terrainChunkX = entity.TerrainLocalChunkX >= 0
                    ? entity.TerrainLocalChunkX
                    : entity.TerrainChunkX;
                int terrainChunkZ = entity.TerrainLocalChunkZ >= 0
                    ? entity.TerrainLocalChunkZ
                    : entity.TerrainChunkZ;
                if (terrainChunkX < minChunkX || terrainChunkX > maxChunkX ||
                    terrainChunkZ < minChunkZ || terrainChunkZ > maxChunkZ)
                    continue;

                entity.TerrainLod.RefreshGeometry((lodLevel, stitchEdges) =>
                    TerrainMeshGenerator.Generate(
                        terrain,
                        terrainChunkX,
                        terrainChunkZ,
                        chunkQuads,
                        lodLevel,
                        stitchEdges));
                refreshed++;
            }
        }

        return refreshed;
    }
}
