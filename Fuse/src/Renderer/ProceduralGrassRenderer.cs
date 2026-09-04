using System.Numerics;
using System.Runtime.InteropServices;
using Fuse.Core;
using Fuse.Scene.Terrain;
using Silk.NET.OpenGL;

namespace Fuse.Renderer;

public readonly record struct ProceduralGrassDiagnostics(
    int TerrainLayers,
    int ResidentPatches,
    int VisiblePatches,
    int PendingPatches,
    int CandidateBlades,
    int Lod0Blades,
    int Lod1Blades,
    int Lod2Blades,
    int FrustumCulledPatches,
    int DistanceCulledPatches,
    int DensityCulledBlades,
    int OcclusionCulledPatches,
    int DrawCalls,
    long GpuBytes,
    double ComputeMilliseconds);

/// <summary>
/// GPU-driven procedural grass pass. Terrain tiles own CPU patch residency;
/// this renderer uploads immutable candidates, performs per-frame frustum,
/// density and distance LOD selection in compute, then submits three indirect
/// draws per terrain layer.
/// </summary>
public unsafe sealed class ProceduralGrassRenderer : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct CandidateGpu
    {
        public Vector4 OffsetHeight;
        public Vector4 NormalWidth;
        public Vector4 Parameters;
        public Vector4 Metadata;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PatchGpu
    {
        public Vector4 RelativeOriginVisible;
        public Vector4 RelativeCenterRadius;
    }

    private sealed class BladeMesh
    {
        public uint Vao;
        public uint Vbo;
        public uint Ebo;
        public uint IndexCount;
    }

    private sealed class LayerGpuState : IDisposable
    {
        private readonly GL _gl;

        public uint CandidateBuffer;
        public uint PatchBuffer;
        public readonly uint[] LodBuffers = new uint[3];
        public uint IndirectBuffer;
        public uint DrawIndirectBuffer;
        public int CandidateCapacity;
        public int PatchCapacity;
        public int LodCapacity;
        public int CandidateCount;
        public ulong UploadedRevision = ulong.MaxValue;
        public ProceduralGrassPatch[] Patches = [];
        public PatchGpu[] PatchDescriptors = [];
        public int VisiblePatchCount;
        public int FrustumCulledPatchCount;
        public int DistanceCulledPatchCount;
        public int VisibleCandidateCount;
        public int DensityCulledBladeCount;
        public int OcclusionCulledPatchCount;
        public int Lod0Count;
        public int Lod1Count;
        public int Lod2Count;
        public int[] PatchCandidateStarts = [];
        public byte[] PatchCullReasons = [];
        public float[] PatchDistances = [];

        public LayerGpuState(GL gl)
        {
            _gl = gl;
            CandidateBuffer = gl.GenBuffer();
            PatchBuffer = gl.GenBuffer();
            for (int i = 0; i < LodBuffers.Length; i++)
                LodBuffers[i] = gl.GenBuffer();
            IndirectBuffer = gl.GenBuffer();
            DrawIndirectBuffer = gl.GenBuffer();
        }

        public long EstimateGpuBytes() =>
            (long)CandidateCapacity * sizeof(CandidateGpu) +
            (long)PatchCapacity * sizeof(PatchGpu) +
            (long)LodCapacity * sizeof(Vector4) * 3 * 3 +
            2L * 3L * 5L * sizeof(uint);

        public void Dispose()
        {
            if (CandidateBuffer != 0) _gl.DeleteBuffer(CandidateBuffer);
            if (PatchBuffer != 0) _gl.DeleteBuffer(PatchBuffer);
            foreach (uint buffer in LodBuffers)
            {
                if (buffer != 0) _gl.DeleteBuffer(buffer);
            }
            if (IndirectBuffer != 0) _gl.DeleteBuffer(IndirectBuffer);
            if (DrawIndirectBuffer != 0) _gl.DeleteBuffer(DrawIndirectBuffer);
            CandidateBuffer = 0;
            PatchBuffer = 0;
            IndirectBuffer = 0;
            DrawIndirectBuffer = 0;
        }
    }

    private const uint CandidateBinding = 0;
    private const uint PatchBinding = 1;
    private const uint Lod0Binding = 2;
    private const uint Lod1Binding = 3;
    private const uint Lod2Binding = 4;
    private const uint IndirectBinding = 5;
    private const uint DrawInstanceBinding = 6;
    private const int HiZTextureUnit = 7;
    private const int IndirectCommandUIntCount = 5;
    private const int IndirectCommandSize = IndirectCommandUIntCount * sizeof(uint);

    private readonly GL _gl;
    private readonly Shader _shader;
    private readonly Shader _shadowShader;
    private readonly ComputeShader _cullShader;
    private readonly ComputeShader _hizCopyShader;
    private readonly ComputeShader _hizReduceShader;
    private readonly ComputeShader _occlusionShader;
    private readonly BladeMesh[] _lodMeshes;
    private readonly Dictionary<ProceduralTerrainLayer, LayerGpuState> _layers = [];
    private readonly uint[] _computeQueries = new uint[3];
    private readonly bool[] _computeQueryPending = new bool[3];
    private int _computeQueryWriteIndex;
    private double _lastGpuComputeMilliseconds;
    private uint _hizReadTexture;
    private uint _hizWriteTexture;
    private int _hizWidth;
    private int _hizHeight;
    private int _hizMipCount;
    private bool _hizHistoryValid;
    private bool _indirectDrawSupported = true;
    private bool _indirectVisiblePassValidated;
    private bool _indirectShadowPassValidated;
    private bool _reportedIndirectDrawFallback;
    private bool _disposed;

    public bool Enabled { get; set; } = true;
    public bool DebugLodColors { get; set; }
    public bool ReadbackDiagnostics { get; set; }
    public ProceduralGrassDiagnostics Diagnostics { get; private set; }

    public ProceduralGrassRenderer(GL gl)
    {
        _gl = gl;
        _shader = Shader.FromFile(
            gl,
            Bible.Shader(Bible.ProceduralGrassVert),
            Bible.Shader(Bible.ProceduralGrassFrag));
        _shadowShader = Shader.FromFile(
            gl,
            Bible.Shader(Bible.ProceduralGrassShadowVert),
            Bible.Shader(Bible.ProceduralGrassShadowFrag));
        _cullShader = ComputeShader.FromFile(
            gl,
            Bible.Shader(Bible.ProceduralGrassCullCompute));
        _hizCopyShader = ComputeShader.FromFile(
            gl,
            Bible.Shader(Bible.ProceduralGrassHiZCopyCompute));
        _hizReduceShader = ComputeShader.FromFile(
            gl,
            Bible.Shader(Bible.ProceduralGrassHiZReduceCompute));
        _occlusionShader = ComputeShader.FromFile(
            gl,
            Bible.Shader(Bible.ProceduralGrassOcclusionCompute));
        _lodMeshes =
        [
            CreateBladeMesh(4, 2),
            CreateBladeMesh(2, 1),
            CreateBladeMesh(1, 2)
        ];
        for (int i = 0; i < _computeQueries.Length; i++)
            _computeQueries[i] = _gl.GenQuery();
    }

    public void Render(
        Scene scene,
        Matrix4x4 view,
        Matrix4x4 projection,
        Vector3 cameraPosition,
        Vector3 lightDirection,
        Vector3 lightColor,
        float ambient,
        bool outputSrgb,
        float? simulationTimeSeconds = null,
        uint sceneDepthTexture = 0,
        int sceneDepthWidth = 0,
        int sceneDepthHeight = 0)
    {
        Prepare(
            scene,
            view,
            projection,
            cameraPosition,
            sceneDepthTexture,
            sceneDepthWidth,
            sceneDepthHeight);
        DrawPrepared(
            scene,
            view,
            projection,
            cameraPosition,
            lightDirection,
            lightColor,
            ambient,
            outputSrgb,
            simulationTimeSeconds);
    }

    /// <summary>
    /// Updates patch residency, uploads changed candidates and runs GPU culling.
    /// MasterRenderer invokes this before shadow maps so the same LOD0 instance
    /// buffer can feed both the shadow and visible passes.
    /// </summary>
    public void Prepare(
        Scene scene,
        Matrix4x4 view,
        Matrix4x4 projection,
        Vector3 cameraPosition,
        uint sceneDepthTexture = 0,
        int sceneDepthWidth = 0,
        int sceneDepthHeight = 0)
    {
        ArgumentNullException.ThrowIfNull(scene);
        if (_disposed || !Enabled || !_shader.IsValid || !_cullShader.IsValid)
        {
            Diagnostics = default;
            return;
        }

        RemoveUnusedLayers(scene.ProceduralTerrainLayers);
        var frustum = new ViewFrustum(view * projection);
        int residentPatches = 0;
        int visiblePatches = 0;
        int pendingPatches = 0;
        int candidateBlades = 0;
        int lod0 = 0;
        int lod1 = 0;
        int lod2 = 0;
        int frustumCulled = 0;
        int distanceCulled = 0;
        int densityCulled = 0;
        int occlusionCulled = 0;
        int drawCalls = 0;
        long gpuBytes = 0;
        ResolveGpuComputeTimer();
        int timerSlot = BeginGpuComputeTimer();
        bool hiZRequested = scene.ProceduralTerrainLayers.Any(static layer =>
            layer.Visible && layer.Asset.Settings.Grass.Enabled &&
            layer.Asset.Settings.Grass.HiZOcclusion);
        bool hiZReady = hiZRequested && _occlusionShader.IsValid &&
                        BuildHiZ(sceneDepthTexture, sceneDepthWidth, sceneDepthHeight);

        foreach (ProceduralTerrainLayer layer in scene.ProceduralTerrainLayers)
        {
            ProceduralGrassSettings settings = layer.Asset.Settings.Grass;
            if (!layer.Visible || !settings.Enabled || settings.Density <= 0.0001f)
                continue;

            Vector3 localCamera = Vector3.Transform(
                cameraPosition - layer.WorldPosition,
                Quaternion.Inverse(layer.WorldRotation));
            layer.GrassPatches.Update(localCamera.X, localCamera.Z);

            if (!_layers.TryGetValue(layer, out LayerGpuState? state))
            {
                state = new LayerGpuState(_gl);
                _layers.Add(layer, state);
            }

            if (state.UploadedRevision != layer.GrassPatches.Revision)
                UploadCandidates(layer, state);
            if (state.CandidateCount == 0 || state.Patches.Length == 0)
                continue;

            UpdatePatchDescriptors(layer, state, localCamera, cameraPosition, settings, frustum);
            if (settings.HiZOcclusion && hiZReady && state.VisiblePatchCount > 0)
                DispatchPatchOcclusion(state, view, projection, settings, sceneDepthWidth, sceneDepthHeight);
            DispatchCulling(state, settings, view, projection);

            if (state.VisiblePatchCount > 0 &&
                (ReadbackDiagnostics || !_indirectDrawSupported))
                ReadLodCounts(state);

            residentPatches += layer.GrassPatches.ResidentCount;
            visiblePatches += state.VisiblePatchCount;
            pendingPatches += layer.GrassPatches.PendingCount;
            candidateBlades += state.CandidateCount;
            lod0 += state.Lod0Count;
            lod1 += state.Lod1Count;
            lod2 += state.Lod2Count;
            frustumCulled += state.FrustumCulledPatchCount;
            distanceCulled += state.DistanceCulledPatchCount;
            densityCulled += state.DensityCulledBladeCount;
            occlusionCulled += state.OcclusionCulledPatchCount;
            drawCalls += 3;
            gpuBytes += state.EstimateGpuBytes();
        }
        EndGpuComputeTimer(timerSlot);

        Diagnostics = new ProceduralGrassDiagnostics(
            _layers.Count,
            residentPatches,
            visiblePatches,
            pendingPatches,
            candidateBlades,
            lod0,
            lod1,
            lod2,
            frustumCulled,
            distanceCulled,
            densityCulled,
            occlusionCulled,
            drawCalls,
            gpuBytes,
            _lastGpuComputeMilliseconds);
    }

    public void DrawPrepared(
        Scene scene,
        Matrix4x4 view,
        Matrix4x4 projection,
        Vector3 cameraPosition,
        Vector3 lightDirection,
        Vector3 lightColor,
        float ambient,
        bool outputSrgb,
        float? simulationTimeSeconds = null)
    {
        ArgumentNullException.ThrowIfNull(scene);
        if (_disposed || !Enabled || !_shader.IsValid)
            return;

        float time = simulationTimeSeconds ?? Environment.TickCount64 / 1000.0f;
        foreach (ProceduralTerrainLayer layer in scene.ProceduralTerrainLayers)
        {
            ProceduralGrassSettings settings = layer.Asset.Settings.Grass;
            if (!layer.Visible || !settings.Enabled ||
                !_layers.TryGetValue(layer, out LayerGpuState? state) ||
                state.CandidateCount == 0 || state.VisiblePatchCount == 0)
                continue;

            DrawLayer(
                state,
                settings,
                view,
                projection,
                cameraPosition,
                lightDirection,
                lightColor,
                ambient,
                outputSrgb,
                time);
        }
    }

    /// <summary>
    /// Adds only detailed LOD0 blades to the currently bound shadow map. Far
    /// LODs intentionally never cast shadows. The shared GLSL deformation file
    /// keeps visible blades and their shadow silhouettes synchronized.
    /// </summary>
    public void RenderNearShadows(
        Scene scene,
        Matrix4x4 lightSpaceMatrix,
        Vector3 cameraPosition,
        float simulationTimeSeconds)
    {
        ArgumentNullException.ThrowIfNull(scene);
        if (_disposed || !Enabled || !_shadowShader.IsValid)
            return;

        _gl.Enable(EnableCap.DepthTest);
        _gl.DepthMask(true);
        _gl.Disable(EnableCap.Blend);
        _gl.Disable(EnableCap.CullFace);

        foreach (ProceduralTerrainLayer layer in scene.ProceduralTerrainLayers)
        {
            ProceduralGrassSettings settings = layer.Asset.Settings.Grass;
            if (!layer.Visible || !settings.Enabled || !settings.CastNearShadows ||
                !_layers.TryGetValue(layer, out LayerGpuState? state) ||
                state.CandidateCount == 0 || state.VisiblePatchCount == 0)
                continue;

            _shadowShader.Use();
            _shadowShader.SetMat4("uLightSpaceMatrix", lightSpaceMatrix);
            _shadowShader.SetVec3("uCameraPosition", cameraPosition);
            _shadowShader.SetFloat("uTime", simulationTimeSeconds);
            _shadowShader.SetVec2("uWindDirection", settings.WindDirection);
            _shadowShader.SetFloat("uWindStrength", settings.WindStrength);
            _shadowShader.SetFloat("uWindSpeed", settings.WindSpeed);
            _shadowShader.SetFloat("uGustStrength", settings.GustStrength);
            _shadowShader.SetFloat("uGustScale", settings.GustScale);

            _gl.BindBufferBase(GLEnum.ShaderStorageBuffer, DrawInstanceBinding, state.LodBuffers[0]);
            _gl.BindVertexArray(_lodMeshes[0].Vao);
            if (_indirectDrawSupported)
            {
                _gl.BindBuffer(GLEnum.DrawIndirectBuffer, state.DrawIndirectBuffer);
                _gl.DrawElementsIndirect(PrimitiveType.Triangles, DrawElementsType.UnsignedInt, null);
                if (!_indirectShadowPassValidated)
                {
                    GLEnum error = _gl.GetError();
                    if (error != GLEnum.NoError)
                    {
                        DisableIndirectDrawing(error, "shadow pass");
                        ReadLodCounts(state);
                        DrawLodInstanced(state, 0);
                    }
                    else
                    {
                        _indirectShadowPassValidated = true;
                    }
                }
            }
            else
            {
                DrawLodInstanced(state, 0);
            }
        }

        _gl.BindVertexArray(0);
        _gl.BindBuffer(GLEnum.DrawIndirectBuffer, 0);
        _gl.Enable(EnableCap.CullFace);
        _gl.CullFace(GLEnum.Back);
    }

    private void UploadCandidates(ProceduralTerrainLayer layer, LayerGpuState state)
    {
        ProceduralGrassPatch[] patches = layer.GrassPatches.ResidentPatches
            .OrderBy(static patch => patch.Coordinate.TileX)
            .ThenBy(static patch => patch.Coordinate.TileZ)
            .ThenBy(static patch => patch.Coordinate.PatchX)
            .ThenBy(static patch => patch.Coordinate.PatchZ)
            .ToArray();
        int totalCandidates = 0;
        foreach (ProceduralGrassPatch patch in patches)
            totalCandidates += patch.Candidates.Count;

        var candidates = new CandidateGpu[totalCandidates];
        int destination = 0;
        for (int patchIndex = 0; patchIndex < patches.Length; patchIndex++)
        {
            ProceduralGrassPatch patch = patches[patchIndex];
            foreach (GrassBladeCandidate candidate in patch.Candidates)
            {
                Vector3 offset = Vector3.Transform(candidate.LocalOffset, layer.WorldRotation);
                Vector3 normal = Vector3.Normalize(Vector3.Transform(candidate.LocalNormal, layer.WorldRotation));
                candidates[destination++] = new CandidateGpu
                {
                    OffsetHeight = new Vector4(offset, candidate.Height),
                    NormalWidth = new Vector4(normal, candidate.Width),
                    Parameters = new Vector4(
                        candidate.Yaw,
                        candidate.WindPhase,
                        candidate.Random,
                        candidate.ProceduralDensity),
                    Metadata = new Vector4(patchIndex, candidate.Species, 0.0f, 0.0f)
                };
            }
        }

        EnsureBufferCapacity(
            state.CandidateBuffer,
            ref state.CandidateCapacity,
            System.Math.Max(totalCandidates, 1),
            sizeof(CandidateGpu));
        if (candidates.Length > 0)
        {
            _gl.BindBuffer(GLEnum.ShaderStorageBuffer, state.CandidateBuffer);
            fixed (CandidateGpu* pointer = candidates)
                _gl.BufferSubData(
                    GLEnum.ShaderStorageBuffer,
                    0,
                    (nuint)(candidates.Length * sizeof(CandidateGpu)),
                    pointer);
        }

        EnsureBufferCapacity(
            state.PatchBuffer,
            ref state.PatchCapacity,
            System.Math.Max(patches.Length, 1),
            sizeof(PatchGpu));
        EnsureLodCapacity(state, System.Math.Max(totalCandidates, 1));
        EnsureIndirectBuffer(state);
        _gl.BindBuffer(GLEnum.ShaderStorageBuffer, 0);

        state.CandidateCount = totalCandidates;
        state.Patches = patches;
        state.PatchCandidateStarts = new int[patches.Length];
        int candidateStart = 0;
        for (int index = 0; index < patches.Length; index++)
        {
            state.PatchCandidateStarts[index] = candidateStart;
            candidateStart += patches[index].Candidates.Count;
        }
        state.PatchDescriptors = new PatchGpu[patches.Length];
        state.PatchCullReasons = new byte[patches.Length];
        state.PatchDistances = new float[patches.Length];
        state.UploadedRevision = layer.GrassPatches.Revision;
    }

    private void UpdatePatchDescriptors(
        ProceduralTerrainLayer layer,
        LayerGpuState state,
        Vector3 localCamera,
        Vector3 cameraPosition,
        ProceduralGrassSettings settings,
        ViewFrustum frustum)
    {
        state.VisiblePatchCount = 0;
        state.FrustumCulledPatchCount = 0;
        state.DistanceCulledPatchCount = 0;
        state.VisibleCandidateCount = 0;
        state.DensityCulledBladeCount = 0;
        state.OcclusionCulledPatchCount = 0;
        state.Lod0Count = 0;
        state.Lod1Count = 0;
        state.Lod2Count = 0;
        float maximumSpeciesHeight = settings.Species.Count == 0
            ? 1.0f
            : settings.Species.Max(static species => species.HeightMultiplier);
        float maximumWindReach = CalculateMaximumWindReach(settings);
        float bladePadding = settings.BladeHeightMax * maximumSpeciesHeight +
                             1.0f + maximumWindReach;

        for (int index = 0; index < state.Patches.Length; index++)
        {
            ProceduralGrassPatch patch = state.Patches[index];
            double centerX = patch.LocalOriginX + patch.Width * 0.5;
            double centerZ = patch.LocalOriginZ + patch.Depth * 0.5;
            double dx = centerX - localCamera.X;
            double dz = centerZ - localCamera.Z;
            float horizontalDistance = (float)System.Math.Sqrt(dx * dx + dz * dz);
            state.PatchDistances[index] = horizontalDistance;
            float patchRadius = MathF.Sqrt(patch.Width * patch.Width + patch.Depth * patch.Depth) * 0.5f + bladePadding;
            bool visible = horizontalDistance <= settings.MaximumDistance + patchRadius;
            if (!visible)
            {
                state.DistanceCulledPatchCount++;
                state.PatchCullReasons[index] = 1;
            }
            else
            {
                float maximumBladeHeight = settings.BladeHeightMax * maximumSpeciesHeight;
                float centerHeight = (patch.MinimumHeight + patch.MaximumHeight + maximumBladeHeight) * 0.5f;
                Vector3 localCenter = new((float)centerX, centerHeight, (float)centerZ);
                Vector3 worldCenter = layer.WorldPosition + Vector3.Transform(localCenter, layer.WorldRotation);
                float heightExtent = (patch.MaximumHeight - patch.MinimumHeight + maximumBladeHeight) * 0.5f;
                float sphereRadius = MathF.Sqrt(
                    patch.Width * patch.Width * 0.25f +
                    patch.Depth * patch.Depth * 0.25f +
                    heightExtent * heightExtent) + 0.5f + maximumWindReach;
                visible = frustum.Intersects(new Fuse.Math.BoundingSphere(worldCenter, sphereRadius));
                if (!visible)
                {
                    state.FrustumCulledPatchCount++;
                    state.PatchCullReasons[index] = 2;
                }
            }

            Vector3 localRelativeOrigin = new(
                (float)(patch.LocalOriginX - localCamera.X),
                -localCamera.Y,
                (float)(patch.LocalOriginZ - localCamera.Z));
            Vector3 relativeOrigin = Vector3.Transform(localRelativeOrigin, layer.WorldRotation);
            float occlusionMaximumBladeHeight = settings.BladeHeightMax * maximumSpeciesHeight;
            float occlusionCenterHeight =
                (patch.MinimumHeight + patch.MaximumHeight + occlusionMaximumBladeHeight) * 0.5f;
            Vector3 occlusionLocalCenter = new(
                (float)centerX,
                occlusionCenterHeight,
                (float)centerZ);
            Vector3 occlusionWorldCenter = layer.WorldPosition +
                                  Vector3.Transform(occlusionLocalCenter, layer.WorldRotation);
            float occlusionHeightExtent =
                (patch.MaximumHeight - patch.MinimumHeight + occlusionMaximumBladeHeight) * 0.5f;
            float occlusionSphereRadius = MathF.Sqrt(
                patch.Width * patch.Width * 0.25f +
                patch.Depth * patch.Depth * 0.25f +
                occlusionHeightExtent * occlusionHeightExtent) + 0.5f + maximumWindReach;
            state.PatchDescriptors[index].RelativeOriginVisible =
                new Vector4(relativeOrigin, visible ? 1.0f : -1.0f);
            state.PatchDescriptors[index].RelativeCenterRadius =
                new Vector4(occlusionWorldCenter - cameraPosition, occlusionSphereRadius);
            if (visible)
            {
                state.VisiblePatchCount++;
                state.VisibleCandidateCount += patch.Candidates.Count;
                state.PatchCullReasons[index] = 0;
            }
        }

        _gl.BindBuffer(GLEnum.ShaderStorageBuffer, state.PatchBuffer);
        if (state.PatchDescriptors.Length > 0)
        {
            fixed (PatchGpu* pointer = state.PatchDescriptors)
                _gl.BufferSubData(
                    GLEnum.ShaderStorageBuffer,
                    0,
                    (nuint)(state.PatchDescriptors.Length * sizeof(PatchGpu)),
                    pointer);
        }
        _gl.BindBuffer(GLEnum.ShaderStorageBuffer, 0);
    }

    private void DispatchPatchOcclusion(
        LayerGpuState state,
        Matrix4x4 view,
        Matrix4x4 projection,
        ProceduralGrassSettings settings,
        int viewportWidth,
        int viewportHeight)
    {
        if (state.Patches.Length == 0 || _hizReadTexture == 0 ||
            viewportWidth <= 0 || viewportHeight <= 0 || _hizMipCount <= 0)
            return;

        _occlusionShader.Use();
        _occlusionShader.SetInt("uHiZ", HiZTextureUnit);
        _occlusionShader.SetMat4("uView", view);
        _occlusionShader.SetMat4("uProj", projection);
        _occlusionShader.SetVec2("uViewportSize", new Vector2(viewportWidth, viewportHeight));
        _occlusionShader.SetInt("uHiZMipCount", _hizMipCount);
        _occlusionShader.SetInt("uPatchCount", state.Patches.Length);
        _occlusionShader.SetFloat("uBias", settings.HiZOcclusionBias);
        _occlusionShader.BindStorageBuffer(PatchBinding, state.PatchBuffer);

        _gl.ActiveTexture((TextureUnit)((int)TextureUnit.Texture0 + HiZTextureUnit));
        _gl.BindTexture(TextureTarget.Texture2D, _hizReadTexture);
        _occlusionShader.Dispatch(
            (uint)((state.Patches.Length + 63) / 64),
            barrier: MemoryBarrierMask.ShaderStorageBarrierBit |
                     MemoryBarrierMask.TextureFetchBarrierBit);
        _gl.ActiveTexture(TextureUnit.Texture0);
    }

    private bool BuildHiZ(uint depthTexture, int width, int height)
    {
        if (depthTexture == 0 || width <= 0 || height <= 0 ||
            !_hizCopyShader.IsValid || !_hizReduceShader.IsValid)
            return false;

        EnsureHiZResources(width, height);
        bool readyForOcclusion = _hizHistoryValid;

        _hizCopyShader.Use();
        _hizCopyShader.SetInt("uSourceDepth", 0);
        _hizCopyShader.SetVec2("uDestinationSize", new Vector2(_hizWidth, _hizHeight));
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, depthTexture);
        _gl.BindImageTexture(
            0,
            _hizReadTexture,
            0,
            false,
            0,
            GLEnum.WriteOnly,
            GLEnum.R32f);
        _hizCopyShader.Dispatch(
            (uint)((_hizWidth + 7) / 8),
            (uint)((_hizHeight + 7) / 8),
            barrier: MemoryBarrierMask.ShaderImageAccessBarrierBit |
                     MemoryBarrierMask.TextureFetchBarrierBit);
        UnbindHiZImage();

        int sourceWidth = _hizWidth;
        int sourceHeight = _hizHeight;
        uint readTexture = _hizReadTexture;
        uint writeTexture = _hizWriteTexture;
        for (int level = 1; level < _hizMipCount; level++)
        {
            int destinationWidth = System.Math.Max(1, (sourceWidth + 1) / 2);
            int destinationHeight = System.Math.Max(1, (sourceHeight + 1) / 2);
            _hizReduceShader.Use();
            _hizReduceShader.SetInt("uHiZ", HiZTextureUnit);
            _hizReduceShader.SetInt("uSourceLevel", level - 1);
            _hizReduceShader.SetVec2("uSourceSize", new Vector2(sourceWidth, sourceHeight));
            _hizReduceShader.SetVec2(
                "uDestinationSize",
                new Vector2(destinationWidth, destinationHeight));
            _gl.ActiveTexture((TextureUnit)((int)TextureUnit.Texture0 + HiZTextureUnit));
            _gl.BindTexture(TextureTarget.Texture2D, readTexture);
            _gl.BindImageTexture(
                0,
                writeTexture,
                level,
                false,
                0,
                GLEnum.WriteOnly,
                GLEnum.R32f);
            _hizReduceShader.Dispatch(
                (uint)((destinationWidth + 7) / 8),
                (uint)((destinationHeight + 7) / 8),
                barrier: MemoryBarrierMask.ShaderImageAccessBarrierBit |
                         MemoryBarrierMask.TextureFetchBarrierBit);
            UnbindHiZImage();

            (readTexture, writeTexture) = (writeTexture, readTexture);
            sourceWidth = destinationWidth;
            sourceHeight = destinationHeight;
        }

        _hizReadTexture = readTexture;
        _hizWriteTexture = writeTexture;
        _hizHistoryValid = true;
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, 0);
        return readyForOcclusion;
    }

    private void EnsureHiZResources(int width, int height)
    {
        if (_hizReadTexture != 0 && _hizWidth == width && _hizHeight == height)
            return;

        DeleteHiZResources();
        _hizWidth = width;
        _hizHeight = height;
        _hizMipCount = 1;
        int mipWidth = width;
        int mipHeight = height;
        while (mipWidth > 1 || mipHeight > 1)
        {
            mipWidth = System.Math.Max(1, (mipWidth + 1) / 2);
            mipHeight = System.Math.Max(1, (mipHeight + 1) / 2);
            _hizMipCount++;
        }

        _hizReadTexture = CreateHiZTexture(width, height, _hizMipCount);
        _hizWriteTexture = CreateHiZTexture(width, height, _hizMipCount);
        _hizHistoryValid = false;
    }

    private uint CreateHiZTexture(int width, int height, int mipCount)
    {
        uint texture = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, texture);
        int mipWidth = width;
        int mipHeight = height;
        for (int level = 0; level < mipCount; level++)
        {
            _gl.TexImage2D(
                TextureTarget.Texture2D,
                level,
                (int)InternalFormat.R32f,
                (uint)mipWidth,
                (uint)mipHeight,
                0,
                PixelFormat.Red,
                PixelType.Float,
                null);
            mipWidth = System.Math.Max(1, (mipWidth + 1) / 2);
            mipHeight = System.Math.Max(1, (mipHeight + 1) / 2);
        }
        _gl.TexParameter(
            TextureTarget.Texture2D,
            TextureParameterName.TextureMinFilter,
            (int)TextureMinFilter.Nearest);
        _gl.TexParameter(
            TextureTarget.Texture2D,
            TextureParameterName.TextureMagFilter,
            (int)TextureMagFilter.Nearest);
        _gl.TexParameter(
            TextureTarget.Texture2D,
            TextureParameterName.TextureWrapS,
            (int)TextureWrapMode.ClampToEdge);
        _gl.TexParameter(
            TextureTarget.Texture2D,
            TextureParameterName.TextureWrapT,
            (int)TextureWrapMode.ClampToEdge);
        _gl.BindTexture(TextureTarget.Texture2D, 0);
        return texture;
    }

    private void UnbindHiZImage() =>
        _gl.BindImageTexture(0, 0, 0, false, 0, GLEnum.WriteOnly, GLEnum.R32f);

    private void DeleteHiZResources()
    {
        if (_hizReadTexture != 0)
            _gl.DeleteTexture(_hizReadTexture);
        if (_hizWriteTexture != 0)
            _gl.DeleteTexture(_hizWriteTexture);
        _hizReadTexture = 0;
        _hizWriteTexture = 0;
        _hizWidth = 0;
        _hizHeight = 0;
        _hizMipCount = 0;
        _hizHistoryValid = false;
    }

    private void DispatchCulling(
        LayerGpuState state,
        ProceduralGrassSettings settings,
        Matrix4x4 view,
        Matrix4x4 projection)
    {
        uint[] commands =
        [
            _lodMeshes[0].IndexCount, 0, 0, 0, 0,
            _lodMeshes[1].IndexCount, 0, 0, 0, 0,
            _lodMeshes[2].IndexCount, 0, 0, 0, 0
        ];
        _gl.BindBuffer(GLEnum.ShaderStorageBuffer, state.IndirectBuffer);
        fixed (uint* pointer = commands)
            _gl.BufferSubData(
                GLEnum.ShaderStorageBuffer,
                0,
                (nuint)(commands.Length * sizeof(uint)),
                pointer);

        _cullShader.Use();
        _cullShader.SetFloat("uDensity", settings.Density);
        _cullShader.SetFloat("uFarDensity", settings.FarDensity);
        _cullShader.SetFloat("uLod0Distance", settings.Lod0Distance);
        _cullShader.SetFloat("uLod1Distance", settings.Lod1Distance);
        _cullShader.SetFloat("uMaximumDistance", settings.MaximumDistance);
        _cullShader.SetMat4("uView", view);
        _cullShader.SetMat4("uProj", projection);
        _cullShader.SetFloat("uBladePadding", CalculateMaximumWindReach(settings));
        _cullShader.BindStorageBuffer(CandidateBinding, state.CandidateBuffer);
        _cullShader.BindStorageBuffer(PatchBinding, state.PatchBuffer);
        _cullShader.BindStorageBuffer(Lod0Binding, state.LodBuffers[0]);
        _cullShader.BindStorageBuffer(Lod1Binding, state.LodBuffers[1]);
        _cullShader.BindStorageBuffer(Lod2Binding, state.LodBuffers[2]);
        _cullShader.BindStorageBuffer(IndirectBinding, state.IndirectBuffer);

        // Candidates are stored patch-by-patch. Dispatch only contiguous runs
        // of CPU-visible patches so looking away from the terrain also avoids
        // walking every resident candidate in the compute shader. The shader
        // still performs per-blade frustum and distance tests for the edges.
        bool dispatched = false;
        int patchIndex = 0;
        while (patchIndex < state.Patches.Length)
        {
            if (state.PatchCandidateStarts.Length <= patchIndex ||
                state.PatchDescriptors[patchIndex].RelativeOriginVisible.W < 0.0f)
            {
                patchIndex++;
                continue;
            }

            int candidateStart = state.PatchCandidateStarts[patchIndex];
            int candidateEnd = candidateStart + state.Patches[patchIndex].Candidates.Count;
            int nextPatch = patchIndex + 1;
            while (nextPatch < state.Patches.Length &&
                   state.PatchCandidateStarts.Length > nextPatch &&
                   state.PatchDescriptors[nextPatch].RelativeOriginVisible.W >= 0.0f &&
                   state.PatchCandidateStarts[nextPatch] == candidateEnd)
            {
                candidateEnd += state.Patches[nextPatch].Candidates.Count;
                nextPatch++;
            }

            int candidateCount = candidateEnd - candidateStart;
            if (candidateCount > 0)
            {
                _cullShader.SetInt("uCandidateStart", candidateStart);
                _cullShader.SetInt("uCandidateCount", candidateCount);
                _cullShader.Dispatch(
                    (uint)((candidateCount + 127) / 128),
                    barrier: MemoryBarrierMask.ShaderStorageBarrierBit);
                dispatched = true;
            }

            patchIndex = nextPatch;
        }

        if (dispatched)
        {
            _gl.MemoryBarrier(
                MemoryBarrierMask.ShaderStorageBarrierBit |
                MemoryBarrierMask.CommandBarrierBit |
                MemoryBarrierMask.VertexAttribArrayBarrierBit |
                MemoryBarrierMask.BufferUpdateBarrierBit);
        }
        // Some Intel drivers do not reliably consume a buffer that is both an
        // SSBO write target and the active GL_DRAW_INDIRECT_BUFFER in the next
        // command. Keep the GPU-driven counters, but copy the tiny argument
        // block to a buffer used exclusively by the indirect draw.
        if (_indirectDrawSupported)
        {
            _gl.BindBuffer(GLEnum.CopyReadBuffer, state.IndirectBuffer);
            _gl.BindBuffer(GLEnum.CopyWriteBuffer, state.DrawIndirectBuffer);
            _gl.CopyBufferSubData(
                GLEnum.CopyReadBuffer,
                GLEnum.CopyWriteBuffer,
                0,
                0,
                (nuint)(3 * IndirectCommandSize));
            _gl.BindBuffer(GLEnum.CopyReadBuffer, 0);
            _gl.BindBuffer(GLEnum.CopyWriteBuffer, 0);
        }
    }

    private static float CalculateMaximumWindReach(ProceduralGrassSettings settings)
    {
        float maximumSpeciesHeight = settings.Species.Count == 0
            ? 1.0f
            : settings.Species.Max(static species => species.HeightMultiplier);
        float maximumBend =
            (settings.WindStrength * 1.12f + settings.GustStrength) * 1.18f;
        return maximumBend * settings.BladeHeightMax * maximumSpeciesHeight *
               (0.34f + maximumBend * 0.055f) + 0.25f;
    }

    private void ResolveGpuComputeTimer()
    {
        for (int index = 0; index < _computeQueries.Length; index++)
        {
            if (!_computeQueryPending[index])
                continue;
            _gl.GetQueryObject(
                _computeQueries[index],
                QueryObjectParameterName.ResultAvailable,
                out int available);
            if (available == 0)
                continue;

            _gl.GetQueryObject(
                _computeQueries[index],
                QueryObjectParameterName.Result,
                out ulong nanoseconds);
            _lastGpuComputeMilliseconds = nanoseconds / 1_000_000.0;
            _computeQueryPending[index] = false;
        }
    }

    private int BeginGpuComputeTimer()
    {
        int slot = _computeQueryWriteIndex;
        if (_computeQueryPending[slot])
            return -1;
        _gl.BeginQuery(QueryTarget.TimeElapsed, _computeQueries[slot]);
        return slot;
    }

    private void EndGpuComputeTimer(int slot)
    {
        if (slot < 0)
            return;
        _gl.EndQuery(QueryTarget.TimeElapsed);
        _computeQueryPending[slot] = true;
        _computeQueryWriteIndex = (slot + 1) % _computeQueries.Length;
    }

    private void DrawLayer(
        LayerGpuState state,
        ProceduralGrassSettings settings,
        Matrix4x4 view,
        Matrix4x4 projection,
        Vector3 cameraPosition,
        Vector3 lightDirection,
        Vector3 lightColor,
        float ambient,
        bool outputSrgb,
        float time)
    {
        _gl.Enable(EnableCap.DepthTest);
        _gl.DepthFunc(DepthFunction.Less);
        _gl.DepthMask(true);
        _gl.Disable(EnableCap.Blend);
        _gl.Disable(EnableCap.CullFace);

        _shader.Use();
        _shader.SetMat4("uView", view);
        _shader.SetMat4("uProj", projection);
        _shader.SetVec3("uCameraPosition", cameraPosition);
        _shader.SetVec3("uLightDirection", lightDirection);
        _shader.SetVec3("uLightColor", lightColor);
        _shader.SetFloat("uAmbient", ambient);
        _shader.SetFloat("uTime", time);
        _shader.SetVec2("uWindDirection", settings.WindDirection);
        _shader.SetFloat("uWindStrength", settings.WindStrength);
        _shader.SetFloat("uWindSpeed", settings.WindSpeed);
        _shader.SetFloat("uGustStrength", settings.GustStrength);
        _shader.SetFloat("uGustScale", settings.GustScale);
        _shader.SetVec3("uRootColor", settings.RootColor);
        _shader.SetVec3("uMidColor", settings.MidColor);
        _shader.SetVec3("uTipColor", settings.TipColor);
        _shader.SetInt("uSpeciesCount", settings.Species.Count);
        for (int index = 0; index < ProceduralGrassSettings.MaximumSpecies; index++)
        {
            Vector3 tint = index < settings.Species.Count
                ? settings.Species[index].ColorTint
                : Vector3.One;
            _shader.SetVec3($"uSpeciesTint[{index}]", tint);
        }
        _shader.SetFloat("uAmbientOcclusion", settings.AmbientOcclusion);
        _shader.SetFloat("uTranslucency", settings.Translucency);
        _shader.SetBool("uDebugLodColors", DebugLodColors);
        _shader.SetBool("uOutputSrgb", outputSrgb);

        if (_indirectDrawSupported)
            _gl.BindBuffer(GLEnum.DrawIndirectBuffer, state.DrawIndirectBuffer);
        for (int lod = 0; lod < 3; lod++)
        {
            _gl.BindBufferBase(GLEnum.ShaderStorageBuffer, DrawInstanceBinding, state.LodBuffers[lod]);
            _gl.BindVertexArray(_lodMeshes[lod].Vao);
            if (_indirectDrawSupported)
            {
                _gl.DrawElementsIndirect(
                    PrimitiveType.Triangles,
                    DrawElementsType.UnsignedInt,
                    (void*)(nuint)(lod * IndirectCommandSize));
                if (!_indirectVisiblePassValidated)
                {
                    GLEnum error = _gl.GetError();
                    if (error != GLEnum.NoError)
                    {
                        DisableIndirectDrawing(error, "visible pass");
                        ReadLodCounts(state);
                        DrawLodInstanced(state, lod);
                    }
                    else
                    {
                        _indirectVisiblePassValidated = true;
                    }
                }
            }
            else
            {
                DrawLodInstanced(state, lod);
            }
        }

        _gl.BindVertexArray(0);
        _gl.BindBuffer(GLEnum.DrawIndirectBuffer, 0);
        _gl.Enable(EnableCap.CullFace);
        _gl.CullFace(GLEnum.Back);
    }

    private void DisableIndirectDrawing(GLEnum error, string pass)
    {
        _indirectDrawSupported = false;
        if (_reportedIndirectDrawFallback)
            return;

        _reportedIndirectDrawFallback = true;
        Logger.Warn(
            $"[Grass] Falling back to instanced grass draws because the OpenGL indirect {pass} " +
            $"was rejected ({error}). GPU culling remains enabled.");
    }

    private void DrawLodInstanced(LayerGpuState state, int lod)
    {
        int instanceCount = lod switch
        {
            0 => state.Lod0Count,
            1 => state.Lod1Count,
            _ => state.Lod2Count
        };
        if (instanceCount <= 0)
            return;

        _gl.DrawElementsInstanced(
            PrimitiveType.Triangles,
            _lodMeshes[lod].IndexCount,
            DrawElementsType.UnsignedInt,
            null,
            (uint)instanceCount);
    }

    private void ReadLodCounts(LayerGpuState state)
    {
        uint[] commands = new uint[3 * IndirectCommandUIntCount];
        _gl.BindBuffer(GLEnum.DrawIndirectBuffer, state.IndirectBuffer);
        fixed (uint* pointer = commands)
            _gl.GetBufferSubData(
                GLEnum.DrawIndirectBuffer,
                0,
                (nuint)(commands.Length * sizeof(uint)),
                pointer);
        _gl.BindBuffer(GLEnum.DrawIndirectBuffer, 0);
        state.Lod0Count = checked((int)commands[1]);
        state.Lod1Count = checked((int)commands[6]);
        state.Lod2Count = checked((int)commands[11]);
        state.DensityCulledBladeCount = System.Math.Max(
            0,
            state.VisibleCandidateCount - state.Lod0Count - state.Lod1Count - state.Lod2Count);
        if (ReadbackDiagnostics)
            ReadPatchCullReasons(state);
    }

    private void ReadPatchCullReasons(LayerGpuState state)
    {
        if (state.PatchDescriptors.Length == 0)
            return;

        var descriptors = new PatchGpu[state.PatchDescriptors.Length];
        _gl.BindBuffer(GLEnum.ShaderStorageBuffer, state.PatchBuffer);
        fixed (PatchGpu* pointer = descriptors)
            _gl.GetBufferSubData(
                GLEnum.ShaderStorageBuffer,
                0,
                (nuint)(descriptors.Length * sizeof(PatchGpu)),
                pointer);
        _gl.BindBuffer(GLEnum.ShaderStorageBuffer, 0);

        int occluded = 0;
        int occludedCandidates = 0;
        for (int index = 0; index < descriptors.Length; index++)
        {
            if (descriptors[index].RelativeOriginVisible.W >= -1.5f)
                continue;
            state.PatchCullReasons[index] = 3;
            occluded++;
            occludedCandidates += state.Patches[index].Candidates.Count;
        }

        state.OcclusionCulledPatchCount = occluded;
        state.VisiblePatchCount = System.Math.Max(0, state.VisiblePatchCount - occluded);
        state.VisibleCandidateCount = System.Math.Max(0, state.VisibleCandidateCount - occludedCandidates);
        state.DensityCulledBladeCount = System.Math.Max(
            0,
            state.VisibleCandidateCount - state.Lod0Count - state.Lod1Count - state.Lod2Count);
    }

    public void DrawDebug(Fuse.Debug.DebugDrawer drawer)
    {
        ArgumentNullException.ThrowIfNull(drawer);
        if (_disposed || !Enabled)
            return;

        foreach ((ProceduralTerrainLayer layer, LayerGpuState state) in _layers)
        {
            ProceduralGrassSettings settings = layer.Asset.Settings.Grass;
            if (!layer.Visible || !settings.Enabled)
                continue;

            for (int index = 0; index < state.Patches.Length; index++)
            {
                ProceduralGrassPatch patch = state.Patches[index];
                byte reason = index < state.PatchCullReasons.Length
                    ? state.PatchCullReasons[index]
                    : (byte)0;
                float distance = index < state.PatchDistances.Length
                    ? state.PatchDistances[index]
                    : 0.0f;
                Vector3 color = reason switch
                {
                    1 => new Vector3(1.0f, 0.1f, 0.85f), // distance
                    2 => new Vector3(0.15f, 0.45f, 1.0f), // frustum
                    3 => new Vector3(0.55f, 0.2f, 1.0f), // Hi-Z
                    _ when distance <= settings.Lod0Distance => new Vector3(0.1f, 1.0f, 0.2f),
                    _ when distance <= settings.Lod1Distance => new Vector3(1.0f, 0.75f, 0.05f),
                    _ => new Vector3(1.0f, 0.12f, 0.08f)
                };

                float minimumHeight = patch.MinimumHeight;
                float maximumSpeciesHeight = settings.Species.Count == 0
                    ? 1.0f
                    : settings.Species.Max(static species => species.HeightMultiplier);
                float maximumHeight = patch.MaximumHeight +
                                      settings.BladeHeightMax * maximumSpeciesHeight;
                Vector3 localCenter = new(
                    (float)(patch.LocalOriginX + patch.Width * 0.5),
                    (minimumHeight + maximumHeight) * 0.5f,
                    (float)(patch.LocalOriginZ + patch.Depth * 0.5));
                Vector3 worldCenter = layer.WorldPosition +
                                      Vector3.Transform(localCenter, layer.WorldRotation);
                Vector3 halfExtents = new(
                    patch.Width * 0.5f,
                    MathF.Max(0.1f, (maximumHeight - minimumHeight) * 0.5f),
                    patch.Depth * 0.5f);
                drawer.DrawBox(worldCenter, layer.WorldRotation, halfExtents, color);
            }
        }
    }

    private void EnsureBufferCapacity(uint buffer, ref int capacity, int required, int stride)
    {
        if (required <= capacity)
            return;
        int replacement = NextCapacity(required);
        _gl.BindBuffer(GLEnum.ShaderStorageBuffer, buffer);
        _gl.BufferData(
            GLEnum.ShaderStorageBuffer,
            (nuint)(replacement * stride),
            null,
            GLEnum.DynamicDraw);
        capacity = replacement;
    }

    private void EnsureLodCapacity(LayerGpuState state, int required)
    {
        if (required <= state.LodCapacity)
            return;
        int replacement = NextCapacity(required);
        foreach (uint buffer in state.LodBuffers)
        {
            _gl.BindBuffer(GLEnum.ShaderStorageBuffer, buffer);
            _gl.BufferData(
                GLEnum.ShaderStorageBuffer,
                (nuint)(replacement * sizeof(Vector4) * 3),
                null,
                GLEnum.DynamicDraw);
        }
        state.LodCapacity = replacement;
    }

    private void EnsureIndirectBuffer(LayerGpuState state)
    {
        _gl.BindBuffer(GLEnum.ShaderStorageBuffer, state.IndirectBuffer);
        _gl.BufferData(
            GLEnum.ShaderStorageBuffer,
            (nuint)(3 * IndirectCommandSize),
            null,
            GLEnum.DynamicDraw);
        _gl.BindBuffer(GLEnum.DrawIndirectBuffer, state.DrawIndirectBuffer);
        _gl.BufferData(
            GLEnum.DrawIndirectBuffer,
            (nuint)(3 * IndirectCommandSize),
            null,
            GLEnum.DynamicDraw);
        _gl.BindBuffer(GLEnum.ShaderStorageBuffer, 0);
        _gl.BindBuffer(GLEnum.DrawIndirectBuffer, 0);
    }

    private static int NextCapacity(int required)
    {
        int capacity = 64;
        while (capacity < required && capacity < 1 << 24)
            capacity <<= 1;
        return System.Math.Max(capacity, required);
    }

    private BladeMesh CreateBladeMesh(int segments, int ribbons)
    {
        var vertices = new List<float>((segments + 1) * 5 * 2 * ribbons);
        var indices = new List<uint>(segments * 6 * ribbons);
        for (int ribbon = 0; ribbon < ribbons; ribbon++)
        {
            float ribbonAngle = ribbon * MathF.PI / ribbons;
            uint baseVertex = (uint)(vertices.Count / 5);
            for (int row = 0; row <= segments; row++)
            {
                float t = row / (float)segments;
                float halfWidth = 0.5f * (1.0f - t * t);
                AddVertex(vertices, -halfWidth, t, ribbonAngle, 0.0f, t);
                AddVertex(vertices, halfWidth, t, ribbonAngle, 1.0f, t);
            }
            for (int row = 0; row < segments; row++)
            {
                uint current = baseVertex + (uint)(row * 2);
                indices.Add(current);
                indices.Add(current + 1);
                indices.Add(current + 2);
                indices.Add(current + 1);
                indices.Add(current + 3);
                indices.Add(current + 2);
            }
        }

        var mesh = new BladeMesh
        {
            Vao = _gl.GenVertexArray(),
            Vbo = _gl.GenBuffer(),
            Ebo = _gl.GenBuffer(),
            IndexCount = (uint)indices.Count
        };
        _gl.BindVertexArray(mesh.Vao);
        _gl.BindBuffer(GLEnum.ArrayBuffer, mesh.Vbo);
        fixed (float* vertexPointer = vertices.ToArray())
            _gl.BufferData(
                GLEnum.ArrayBuffer,
                (nuint)(vertices.Count * sizeof(float)),
                vertexPointer,
                GLEnum.StaticDraw);
        _gl.BindBuffer(GLEnum.ElementArrayBuffer, mesh.Ebo);
        fixed (uint* indexPointer = indices.ToArray())
            _gl.BufferData(
                GLEnum.ElementArrayBuffer,
                (nuint)(indices.Count * sizeof(uint)),
                indexPointer,
                GLEnum.StaticDraw);
        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(0, 3, GLEnum.Float, false, 5 * sizeof(float), (void*)0);
        _gl.EnableVertexAttribArray(1);
        _gl.VertexAttribPointer(1, 2, GLEnum.Float, false, 5 * sizeof(float), (void*)(3 * sizeof(float)));
        _gl.BindVertexArray(0);
        return mesh;
    }

    private static void AddVertex(List<float> vertices, float x, float y, float ribbonAngle, float u, float v)
    {
        vertices.Add(x);
        vertices.Add(y);
        vertices.Add(ribbonAngle);
        vertices.Add(u);
        vertices.Add(v);
    }

    private void RemoveUnusedLayers(IReadOnlyList<ProceduralTerrainLayer> activeLayers)
    {
        foreach (ProceduralTerrainLayer layer in _layers.Keys.ToArray())
        {
            if (activeLayers.Contains(layer))
                continue;
            _layers[layer].Dispose();
            _layers.Remove(layer);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        foreach (LayerGpuState state in _layers.Values)
            state.Dispose();
        _layers.Clear();
        foreach (BladeMesh mesh in _lodMeshes)
        {
            if (mesh.Vao != 0) _gl.DeleteVertexArray(mesh.Vao);
            if (mesh.Vbo != 0) _gl.DeleteBuffer(mesh.Vbo);
            if (mesh.Ebo != 0) _gl.DeleteBuffer(mesh.Ebo);
        }
        foreach (uint query in _computeQueries)
        {
            if (query != 0)
                _gl.DeleteQuery(query);
        }
        _shader.Dispose();
        _shadowShader.Dispose();
        _cullShader.Dispose();
        _hizCopyShader.Dispose();
        _hizReduceShader.Dispose();
        _occlusionShader.Dispose();
        DeleteHiZResources();
    }
}
