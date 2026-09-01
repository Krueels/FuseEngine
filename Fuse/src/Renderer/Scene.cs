using System.Numerics;
using System.Runtime.CompilerServices;
using Fuse.Math;
using Fuse.Physics;
using Fuse.Scene.Terrain;
using JoltPhysicsSharp;

namespace Fuse.Renderer;

public class Transform
{
    public Vector3 Position;
    public Quaternion Rotation = Quaternion.Identity;
    public Vector3 Scale = Vector3.One;

    public Matrix4x4 Matrix =>
        Matrix4x4.CreateScale(Scale) *
        Matrix4x4.CreateFromQuaternion(Rotation) *
        Matrix4x4.CreateTranslation(Position);
}

public enum ShadowCasterMobility
{
    Auto,
    Static,
    Dynamic
}

public enum ShadowCasterFilter
{
    Static,
    Dynamic
}

public class Entity
{
    public string Id { get; set; } = "";
    public string MeshKey { get; set; } = "";
    public string MaterialPath { get; set; } = "";
    public List<string> MaterialPaths { get; set; } = [];
    public Materials.MaterialRuntime? Material { get; set; }
    public List<Materials.MaterialRuntime?> Materials { get; set; } = [];
    public string TexturePath { get; set; } = "";
    public string InteractableType { get; set; } = "";
    public List<Behaviours.BehaviourData> Behaviours { get; set; } = new();
    public System.Numerics.Vector3 ModelScale { get; set; } = System.Numerics.Vector3.One;
    public Vector2 UvScale { get; set; } = Vector2.One;
    public Vector2 UvOffset { get; set; } = Vector2.Zero;
    public float UvRotation { get; set; } = 0f;
    public Mesh? Mesh { get; set; }
    public TerrainLodSet? TerrainLod { get; set; }
    /// <summary>
    /// Editor-only override used by Blowtorch to keep this terrain chunk at
    /// the highest render LOD. Runtime maps leave it disabled.
    /// </summary>
    public bool ForceTerrainLod0 { get; set; }
    public float TerrainPixelError { get; set; } = 5.0f;
    public string TerrainChunkGroupId { get; set; } = "";
    public int TerrainChunkX { get; set; } = -1;
    public int TerrainChunkZ { get; set; } = -1;
    /// <summary>True se esta entidade é dona única da Mesh (pode dar Dispose). False = mesh compartilhada/cacheada pelo AssetManager.</summary>
    public bool MeshOwnedByEntity { get; set; }
    public Texture? Texture { get; set; }
    public RigidBody? Body { get; set; }
    public Transform Transform { get; set; } = new();
    public bool Visible { get; set; } = true;
    public bool IsViewmodel { get; set; }
    public Animation.SkinnedModel? SkinnedModel { get; set; }
    public Animation.Animator? Animator { get; set; }
    public Vector3 ModelOffset { get; set; } = Vector3.Zero;
    /// <summary>Visual-only local rotation applied before the entity's world rotation.</summary>
    public Quaternion ModelRotation { get; set; } = Quaternion.Identity;
    public string ParentId { get; set; } = "";
    public Vector3 InitialRelativePosition { get; set; }
    public Quaternion InitialRelativeRotation { get; set; } = Quaternion.Identity;
    public System.Text.Json.Nodes.JsonObject? MapData { get; set; }
    public Light? AttachedLight { get; set; }

    public bool CastShadows { get; set; } = true;
    public ShadowCasterMobility ShadowMobility { get; set; } = ShadowCasterMobility.Auto;
    /// <summary>Extra local-space padding used mainly by animated meshes.</summary>
    public float ShadowBoundsPadding { get; set; } = 0.35f;

    // Emissive material
    public Vector3 EmissiveColor { get; set; } = Vector3.One;
    public float EmissiveStrength { get; set; } = 1.0f;

    public Matrix4x4 RenderMatrix => SkinnedModel == null
        ? Transform.Matrix
        : Matrix4x4.CreateScale(Transform.Scale * ModelScale) *
          Matrix4x4.CreateFromQuaternion(ModelRotation) *
          Matrix4x4.CreateFromQuaternion(Transform.Rotation) *
          Matrix4x4.CreateTranslation(Transform.Position + ModelOffset);

    public bool IsDynamicShadowCaster => ShadowMobility switch
    {
        ShadowCasterMobility.Static => false,
        ShadowCasterMobility.Dynamic => true,
        _ => SkinnedModel != null || (Body?.IsDynamic ?? false)
    };

    public AABB GetLocalRenderBounds()
    {
        var bounds = new AABB();
        if (SkinnedModel != null)
        {
            foreach (var submesh in SkinnedModel.Submeshes)
            {
                if (!SkinnedModel.HiddenSubmeshes.Contains(submesh.Name))
                    bounds.Grow(submesh.Mesh.LocalBounds);
            }
            return bounds;
        }

        return TerrainLod?.LocalBounds ?? Mesh?.LocalBounds ?? bounds;
    }

    public AABB GetWorldRenderBounds()
    {
        AABB local = GetLocalRenderBounds();
        if (SkinnedModel != null && ShadowBoundsPadding > 0.0f)
            local = local.Inflated(ShadowBoundsPadding);
        return local.Transformed(RenderMatrix);
    }

    public Fuse.Math.BoundingSphere GetWorldBoundingSphere() =>
        Fuse.Math.BoundingSphere.FromAABB(GetWorldRenderBounds());

    public Materials.MaterialRuntime? ResolveMaterial(int slot)
    {
        if (slot >= 0 && slot < Materials.Count && Materials[slot] != null)
            return Materials[slot];
        return Material;
    }
}

public class Scene
{
    private readonly List<Entity> _entities = [];
    private readonly List<Light> _lights = [];
    private readonly Dictionary<BodyID, Entity> _bodyEntityMap = [];
    private readonly List<Entity> _staticShadowCasters = [];
    private readonly List<Entity> _dynamicShadowCasters = [];

    public ulong StaticShadowRevision { get; private set; }
    public ulong DynamicShadowRevision { get; private set; }
    public IReadOnlyList<Entity> StaticShadowCasters => _staticShadowCasters;
    public IReadOnlyList<Entity> DynamicShadowCasters => _dynamicShadowCasters;

    public IReadOnlyList<Light> Lights => _lights;

    public Light AddLight(Light light)
    {
        _lights.Add(light);
        return light;
    }

    public void RemoveLight(Light light) => _lights.Remove(light);

    public Entity Add(Mesh? mesh, string id, RigidBody? body = null)
    {
        var entity = new Entity
        {
            Id = id,
            MeshKey = id,
            Mesh = mesh,
            Body = body,
        };
        _entities.Add(entity);
        if (body != null)
            _bodyEntityMap[body.Native] = entity;
        return entity;
    }

    public void Clear()
    {
        // Dispose APENAS meshes que a entidade possui (brushes/cápsulas).
        // NUNCA dispose de meshes compartilhadas (cache do AssetManager: "cube", modelos OBJ, etc)
        // — o skybox usa GetMesh("cube") todo frame e o próximo mapa reutiliza o cache.
        foreach (var entity in _entities)
        {
            if (entity.TerrainLod != null)
                entity.TerrainLod.Dispose();
            else if (entity.MeshOwnedByEntity)
                entity.Mesh?.Dispose();
            entity.Mesh = null;
            entity.TerrainLod = null;
            entity.MeshOwnedByEntity = false;
            entity.Body = null;
            entity.SkinnedModel = null;
            entity.Material = null;
            entity.Materials.Clear();
            entity.Animator = null;
            entity.AttachedLight = null;
        }
        
        _bodyEntityMap.Clear();
        _entities.Clear();
        _lights.Clear();
        _staticShadowCasters.Clear();
        _dynamicShadowCasters.Clear();
        StaticShadowRevision = 0;
        DynamicShadowRevision = 0;
    }

    public IReadOnlyList<Entity> Entities => _entities;

    public void PrepareShadowCasters()
    {
        _staticShadowCasters.Clear();
        _dynamicShadowCasters.Clear();

        ulong hash = 1469598103934665603UL;
        ulong dynamicHash = 1469598103934665603UL;
        for (int i = 0; i < _entities.Count; i++)
        {
            Entity entity = _entities[i];
            if (!entity.Visible || !entity.CastShadows || entity.IsViewmodel)
                continue;
            if (entity.Mesh == null && entity.SkinnedModel == null)
                continue;

            if (entity.IsDynamicShadowCaster)
            {
                _dynamicShadowCasters.Add(entity);
                MixHash(ref dynamicHash, (uint)RuntimeHelpers.GetHashCode(entity));
                object dynamicGeometry = (object?)entity.SkinnedModel ?? (object?)entity.Mesh ?? entity;
                MixHash(ref dynamicHash, (uint)RuntimeHelpers.GetHashCode(dynamicGeometry));
                MixHash(ref dynamicHash, entity.RenderMatrix);
                MixHash(ref dynamicHash, (uint)BitConverter.SingleToInt32Bits(entity.ShadowBoundsPadding));
                MixHash(ref dynamicHash, entity.Material == null ? 0u : (uint)RuntimeHelpers.GetHashCode(entity.Material));
                for (int materialIndex = 0; materialIndex < entity.Materials.Count; materialIndex++)
                {
                    Materials.MaterialRuntime? material = entity.Materials[materialIndex];
                    MixHash(ref dynamicHash, material == null ? 0u : (uint)RuntimeHelpers.GetHashCode(material));
                }
                continue;
            }

            _staticShadowCasters.Add(entity);
            MixHash(ref hash, (uint)RuntimeHelpers.GetHashCode(entity));
            object geometry = (object?)entity.SkinnedModel ?? (object?)entity.Mesh ?? entity;
            MixHash(ref hash, (uint)RuntimeHelpers.GetHashCode(geometry));
            MixHash(ref hash, entity.RenderMatrix);
            MixHash(ref hash, (uint)BitConverter.SingleToInt32Bits(entity.ShadowBoundsPadding));
            MixHash(ref hash, entity.Material == null ? 0u : (uint)RuntimeHelpers.GetHashCode(entity.Material));
            for (int materialIndex = 0; materialIndex < entity.Materials.Count; materialIndex++)
            {
                Materials.MaterialRuntime? material = entity.Materials[materialIndex];
                MixHash(ref hash, material == null ? 0u : (uint)RuntimeHelpers.GetHashCode(material));
            }
        }

        MixHash(ref hash, (uint)_staticShadowCasters.Count);
        MixHash(ref dynamicHash, (uint)_dynamicShadowCasters.Count);
        StaticShadowRevision = hash;
        DynamicShadowRevision = dynamicHash;
    }

    public IReadOnlyList<Entity> GetShadowCasters(ShadowCasterFilter filter) =>
        filter == ShadowCasterFilter.Static ? _staticShadowCasters : _dynamicShadowCasters;

    public void RenderShadowCasters(Shader shader, Matrix4x4 cullMatrix, ShadowCasterFilter filter)
    {
        var frustum = new ViewFrustum(cullMatrix);
        IReadOnlyList<Entity> casters = GetShadowCasters(filter);

        for (int i = 0; i < casters.Count; i++)
        {
            Entity entity = casters[i];
            if (entity.Mesh == null || entity.SkinnedModel != null)
                continue;
            if (!frustum.Intersects(entity.GetWorldRenderBounds()))
                continue;

            shader.SetMat4("uModel", entity.RenderMatrix);
            shader.SetVec2("uUvScale", entity.UvScale);
            shader.SetVec2("uUvOffset", entity.UvOffset);
            shader.SetFloat("uUvRotation", entity.UvRotation);
            foreach (MeshPart part in entity.Mesh.Parts)
            {
                Materials.MaterialRuntime? material = entity.ResolveMaterial(part.MaterialSlot);
                if (material?.Asset.CastShadows == false)
                    continue;
                if (material != null)
                    material.BindShadow(shader);
                else
                    shader.SetBool("uShadowAlphaMask", false);
                entity.Mesh.DrawPart(part);
            }
        }
    }

    public void RegisterBody(Entity entity)
    {
        if (entity.Body != null && entity.Body.IsBuilt)
            _bodyEntityMap[entity.Body.Native] = entity;
    }

    public Entity? GetEntityByBody(BodyID bodyId)
    {
        _bodyEntityMap.TryGetValue(bodyId, out var entity);
        return entity;
    }

    public int UpdateTerrainLod(
        Vector3 cameraPosition,
        float viewportHeight,
        float fieldOfViewDegrees,
        bool orthographic = false,
        float orthographicSize = 10f,
        float defaultPixelError = 5f)
    {
        viewportHeight = MathF.Max(viewportHeight, 1f);
        defaultPixelError = MathF.Max(defaultPixelError, 0.1f);
        float perspectiveScale = orthographic
            ? viewportHeight / MathF.Max(orthographicSize, 0.001f)
            : viewportHeight / (2f * MathF.Tan(float.DegreesToRadians(
                float.Clamp(fieldOfViewDegrees, 1f, 170f)) * 0.5f));

        var terrainEntities = new List<Entity>();
        var desiredLevels = new Dictionary<Entity, int>();
        var terrainGroups = new Dictionary<
            string,
            Dictionary<(int X, int Z), Entity>>(StringComparer.OrdinalIgnoreCase);

        foreach (Entity entity in _entities)
        {
            TerrainLodSet? lod = entity.TerrainLod;
            if (lod == null || lod.Meshes.Length <= 1)
                continue;

            Fuse.Math.BoundingSphere worldSphere = entity.GetWorldBoundingSphere();
            float distance = orthographic
                ? 1f
                : MathF.Max(
                    Vector3.Distance(cameraPosition, worldSphere.Center) - worldSphere.Radius,
                    0.01f);
            float pixelsPerWorldUnit = orthographic
                ? perspectiveScale
                : perspectiveScale / distance;
            float pixelError = entity.TerrainPixelError > 0f
                ? entity.TerrainPixelError
                : defaultPixelError;

            int desiredLevel = 0;
            if (!entity.ForceTerrainLod0)
            {
                for (int level = lod.Meshes.Length - 1; level >= 0; level--)
                {
                    float projectedError = lod.GeometricErrors[level] * pixelsPerWorldUnit;
                    if (projectedError <= pixelError)
                    {
                        desiredLevel = level;
                        break;
                    }
                }
            }

            terrainEntities.Add(entity);
            desiredLevels[entity] = desiredLevel;

            if (!string.IsNullOrWhiteSpace(entity.TerrainChunkGroupId) &&
                entity.TerrainChunkX >= 0 && entity.TerrainChunkZ >= 0)
            {
                if (!terrainGroups.TryGetValue(entity.TerrainChunkGroupId, out var group))
                {
                    group = new Dictionary<(int X, int Z), Entity>();
                    terrainGroups.Add(entity.TerrainChunkGroupId, group);
                }

                group[(entity.TerrainChunkX, entity.TerrainChunkZ)] = entity;
            }
        }

        Entity? FindNeighbor(Entity entity, int offsetX, int offsetZ)
        {
            if (!terrainGroups.TryGetValue(entity.TerrainChunkGroupId, out var group))
                return null;

            group.TryGetValue(
                (entity.TerrainChunkX + offsetX, entity.TerrainChunkZ + offsetZ),
                out Entity? neighbor);
            return neighbor;
        }

        // Keep adjacent chunks within one LOD level. This lets the finer edge
        // be stitched to the coarser edge without needing a vertical skirt.
        bool adjusted;
        do
        {
            adjusted = false;
            foreach (Entity entity in terrainEntities)
            {
                TerrainLodSet lod = entity.TerrainLod!;
                int currentLevel = desiredLevels[entity];
                Entity?[] neighbors =
                [
                    FindNeighbor(entity, 0, -1),
                    FindNeighbor(entity, 0, 1),
                    FindNeighbor(entity, -1, 0),
                    FindNeighbor(entity, 1, 0)
                ];

                foreach (Entity? neighbor in neighbors)
                {
                    if (neighbor?.TerrainLod == null || !desiredLevels.TryGetValue(neighbor, out int neighborLevel))
                        continue;

                    if (currentLevel < neighborLevel - 1)
                    {
                        int refinedNeighbor = System.Math.Min(
                            currentLevel + 1,
                            neighbor.TerrainLod.Meshes.Length - 1);
                        if (refinedNeighbor < neighborLevel)
                        {
                            desiredLevels[neighbor] = refinedNeighbor;
                            adjusted = true;
                        }
                    }
                    else if (neighborLevel < currentLevel - 1)
                    {
                        int refinedCurrent = System.Math.Min(
                            neighborLevel + 1,
                            lod.Meshes.Length - 1);
                        if (refinedCurrent < currentLevel)
                        {
                            desiredLevels[entity] = refinedCurrent;
                            currentLevel = refinedCurrent;
                            adjusted = true;
                        }
                    }
                }
            }
        }
        while (adjusted);

        TerrainEdgeFlags GetStitchEdges(Entity entity)
        {
            TerrainEdgeFlags edges = TerrainEdgeFlags.None;
            int level = desiredLevels[entity];

            Entity? top = FindNeighbor(entity, 0, -1);
            if (top != null && desiredLevels.TryGetValue(top, out int topLevel) && topLevel == level + 1)
                edges |= TerrainEdgeFlags.Top;

            Entity? bottom = FindNeighbor(entity, 0, 1);
            if (bottom != null && desiredLevels.TryGetValue(bottom, out int bottomLevel) && bottomLevel == level + 1)
                edges |= TerrainEdgeFlags.Bottom;

            Entity? left = FindNeighbor(entity, -1, 0);
            if (left != null && desiredLevels.TryGetValue(left, out int leftLevel) && leftLevel == level + 1)
                edges |= TerrainEdgeFlags.Left;

            Entity? right = FindNeighbor(entity, 1, 0);
            if (right != null && desiredLevels.TryGetValue(right, out int rightLevel) && rightLevel == level + 1)
                edges |= TerrainEdgeFlags.Right;

            return edges;
        }

        int changed = 0;
        foreach (Entity entity in terrainEntities)
        {
            TerrainLodSet lod = entity.TerrainLod!;
            int desiredLevel = desiredLevels[entity];
            TerrainEdgeFlags stitchEdges = GetStitchEdges(entity);
            if (lod.TrySetState(desiredLevel, stitchEdges))
            {
                entity.Mesh = lod.CurrentMesh;
                changed++;
            }
        }

        return changed;
    }

    public void UpdateTransforms(PhysicsWorld world)
    {
        // 1. Update all physics-driven parent positions
        foreach (var e in _entities)
        {
            if (e.Body != null && e.Body.IsBuilt)
            {
                e.Transform.Position = e.Body.Position(world);
                e.Transform.Rotation = e.Body.Rotation(world);
            }
        }

        // 2. Resolve world transforms for parent-child hierarchies
        var worldPositions = new Dictionary<string, Vector3>();
        var worldRotations = new Dictionary<string, Quaternion>();

        bool HasPhysicsAncestor(Entity entity)
        {
            string pId = entity.ParentId;
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (!string.IsNullOrEmpty(pId))
            {
                if (!visited.Add(pId))
                    return false;
                var parent = _entities.FirstOrDefault(p => p.Id == pId);
                if (parent == null) break;
                if (parent.Body != null && parent.Body.IsBuilt) return true;
                pId = parent.ParentId;
            }
            return false;
        }

        var resolvingPositions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var resolvingRotations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        Vector3 GetWorldPosition(Entity e)
        {
            if (worldPositions.TryGetValue(e.Id, out var pos)) return pos;
            if (!resolvingPositions.Add(e.Id))
            {
                worldPositions[e.Id] = e.Transform.Position;
                return e.Transform.Position;
            }
            if (string.IsNullOrEmpty(e.ParentId))
            {
                worldPositions[e.Id] = e.Transform.Position;
                resolvingPositions.Remove(e.Id);
                return e.Transform.Position;
            }
            var parent = _entities.FirstOrDefault(p => p.Id == e.ParentId);
            if (parent == null)
            {
                worldPositions[e.Id] = e.Transform.Position;
                resolvingPositions.Remove(e.Id);
                return e.Transform.Position;
            }

            if (e.Body != null && e.Body.IsBuilt && HasPhysicsAncestor(e))
            {
                worldPositions[e.Id] = e.Transform.Position;
                resolvingPositions.Remove(e.Id);
                return e.Transform.Position;
            }

            Vector3 wPos = GetWorldPosition(parent) + Vector3.Transform(e.InitialRelativePosition, GetWorldRotation(parent));
            worldPositions[e.Id] = wPos;
            resolvingPositions.Remove(e.Id);
            return wPos;
        }

        Quaternion GetWorldRotation(Entity e)
        {
            if (worldRotations.TryGetValue(e.Id, out var rot)) return rot;
            if (!resolvingRotations.Add(e.Id))
            {
                worldRotations[e.Id] = e.Transform.Rotation;
                return e.Transform.Rotation;
            }
            if (string.IsNullOrEmpty(e.ParentId))
            {
                worldRotations[e.Id] = e.Transform.Rotation;
                resolvingRotations.Remove(e.Id);
                return e.Transform.Rotation;
            }
            var parent = _entities.FirstOrDefault(p => p.Id == e.ParentId);
            if (parent == null)
            {
                worldRotations[e.Id] = e.Transform.Rotation;
                resolvingRotations.Remove(e.Id);
                return e.Transform.Rotation;
            }

            if (e.Body != null && e.Body.IsBuilt && HasPhysicsAncestor(e))
            {
                worldRotations[e.Id] = e.Transform.Rotation;
                resolvingRotations.Remove(e.Id);
                return e.Transform.Rotation;
            }

            Quaternion wRot = GetWorldRotation(parent) * e.InitialRelativeRotation;
            worldRotations[e.Id] = wRot;
            resolvingRotations.Remove(e.Id);
            return wRot;
        }

        foreach (var e in _entities)
        {
            if (!string.IsNullOrEmpty(e.ParentId))
            {
                bool conflict = e.Body != null && e.Body.IsBuilt && HasPhysicsAncestor(e);
                if (!conflict)
                {
                    e.Transform.Position = GetWorldPosition(e);
                    e.Transform.Rotation = GetWorldRotation(e);
                    if (e.Body != null && e.Body.IsBuilt)
                    {
                        world.SetBodyPositionAndRotation(e.Body.Native, e.Transform.Position, e.Transform.Rotation);
                    }
                }
            }

            if (e.AttachedLight != null && e.AttachedLight.Dynamic)
            {
                e.AttachedLight.Position = e.Transform.Position;
                if (e.AttachedLight.Type == LightType.Spot)
                    e.AttachedLight.Direction = Vector3.Transform(-Vector3.UnitY, e.Transform.Rotation);
            }
        }
    }

    public int Render(
        Shader legacyShader,
        Texture defaultTexture,
        Matrix4x4? cullMatrix = null,
        Action<Shader>? prepareShader = null)
    {
        ViewFrustum? frustum = cullMatrix.HasValue ? new ViewFrustum(cullMatrix.Value) : null;
        Shader? activeShader = null;
        bool cullEnabled = true;
        bool blendEnabled = false;
        int objectsDrawn = 0;

        // 3. Render all entities
        foreach (var e in _entities)
        {
            if (!e.Visible || e.Mesh == null) continue;
            if (e.SkinnedModel != null) continue; // rendered by the skinned passes

            if (frustum.HasValue && !frustum.Value.Intersects(e.GetWorldRenderBounds()))
                continue;

            IReadOnlyList<MeshPart> parts = e.Mesh.Parts;
            bool objectDrawn = false;
            for (int partIndex = 0; partIndex < parts.Count; partIndex++)
            {
                MeshPart part = parts[partIndex];
                Materials.MaterialRuntime? material = e.ResolveMaterial(part.MaterialSlot);
                Shader shader = material?.StaticShader ?? legacyShader;
                if (!ReferenceEquals(activeShader, shader))
                {
                    shader.Use();
                    prepareShader?.Invoke(shader);
                    activeShader = shader;
                }

                bool wantsCull = material?.Asset.TwoSided != true;
                if (wantsCull != cullEnabled)
                {
                    if (wantsCull) shader.Gl.Enable(Silk.NET.OpenGL.EnableCap.CullFace);
                    else shader.Gl.Disable(Silk.NET.OpenGL.EnableCap.CullFace);
                    cullEnabled = wantsCull;
                }

                bool wantsBlend = material?.Asset.AlphaMode == Materials.MaterialAlphaMode.Blend;
                if (wantsBlend != blendEnabled)
                {
                    if (wantsBlend)
                    {
                        shader.Gl.Enable(Silk.NET.OpenGL.EnableCap.Blend);
                        shader.Gl.BlendFunc(
                            Silk.NET.OpenGL.BlendingFactor.SrcAlpha,
                            Silk.NET.OpenGL.BlendingFactor.OneMinusSrcAlpha);
                        shader.Gl.DepthMask(false);
                    }
                    else
                    {
                        shader.Gl.Disable(Silk.NET.OpenGL.EnableCap.Blend);
                        shader.Gl.DepthMask(true);
                    }
                    blendEnabled = wantsBlend;
                }

                shader.SetMat4("uModel", e.Transform.Matrix);
                shader.SetVec2("uUvScale", e.UvScale);
                shader.SetVec2("uUvOffset", e.UvOffset);
                shader.SetFloat("uUvRotation", e.UvRotation);

                Texture? tex = e.Texture ?? defaultTexture;
                if (material != null)
                {
                    material.Bind(shader);
                    shader.SetBool("uIsEmissive", false);
                }
                else if (tex != null)
                {
                    shader.SetBool("uUseTexture", true);
                    shader.SetInt("uMaterialAlphaMode", 0);
                    shader.SetBool("uMaterialReceiveShadows", true);
                    tex.Bind(0);
                }
                else
                {
                    shader.SetBool("uUseTexture", false);
                }

                // Legacy emissive convention remains available to version-1 maps.
                bool isEmissive = (material == null || material.IsLegacy) && tex != null &&
                    !string.IsNullOrEmpty(e.TexturePath) &&
                    e.TexturePath.Contains("emi_", StringComparison.OrdinalIgnoreCase);

                shader.SetBool("uIsEmissive", isEmissive);
                if (isEmissive)
                {
                    Vector3 dominantColor = tex!.GetDominantColor();
                    shader.SetVec3("uEmissiveColor", dominantColor);
                    shader.SetFloat("uEmissiveStrength", e.EmissiveStrength);
                }

                e.Mesh.DrawPart(part);
                objectDrawn = true;
            }

            if (objectDrawn)
                objectsDrawn++;
        }

        if (!cullEnabled)
            legacyShader.Gl.Enable(Silk.NET.OpenGL.EnableCap.CullFace);
        if (blendEnabled)
        {
            legacyShader.Gl.Disable(Silk.NET.OpenGL.EnableCap.Blend);
            legacyShader.Gl.DepthMask(true);
        }

        return objectsDrawn;
    }

    private static void MixHash(ref ulong hash, uint value)
    {
        hash ^= value;
        hash *= 1099511628211UL;
    }

    private static void MixHash(ref ulong hash, Matrix4x4 matrix)
    {
        MixHash(ref hash, (uint)BitConverter.SingleToInt32Bits(matrix.M11));
        MixHash(ref hash, (uint)BitConverter.SingleToInt32Bits(matrix.M12));
        MixHash(ref hash, (uint)BitConverter.SingleToInt32Bits(matrix.M13));
        MixHash(ref hash, (uint)BitConverter.SingleToInt32Bits(matrix.M14));
        MixHash(ref hash, (uint)BitConverter.SingleToInt32Bits(matrix.M21));
        MixHash(ref hash, (uint)BitConverter.SingleToInt32Bits(matrix.M22));
        MixHash(ref hash, (uint)BitConverter.SingleToInt32Bits(matrix.M23));
        MixHash(ref hash, (uint)BitConverter.SingleToInt32Bits(matrix.M24));
        MixHash(ref hash, (uint)BitConverter.SingleToInt32Bits(matrix.M31));
        MixHash(ref hash, (uint)BitConverter.SingleToInt32Bits(matrix.M32));
        MixHash(ref hash, (uint)BitConverter.SingleToInt32Bits(matrix.M33));
        MixHash(ref hash, (uint)BitConverter.SingleToInt32Bits(matrix.M34));
        MixHash(ref hash, (uint)BitConverter.SingleToInt32Bits(matrix.M41));
        MixHash(ref hash, (uint)BitConverter.SingleToInt32Bits(matrix.M42));
        MixHash(ref hash, (uint)BitConverter.SingleToInt32Bits(matrix.M43));
        MixHash(ref hash, (uint)BitConverter.SingleToInt32Bits(matrix.M44));
    }

    public void UpdateAnimators(float dt)
    {
        foreach (var e in _entities)
        {
            if (e.Animator != null && e.Visible)
                e.Animator.Update(dt);
        }
    }

    public bool IsEntityTrigger(string id)
    {
        var entity = _entities.FirstOrDefault(e => e.Id == id);
        return entity?.Body?.IsTrigger ?? false;
    }

    public void Remove(Entity entity)
    {
        if (entity == null) return;

        if (entity.Body != null && entity.Body.IsBuilt)
            _bodyEntityMap.Remove(entity.Body.Native);

        _entities.Remove(entity);
    }
}
