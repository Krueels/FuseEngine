using System;
using System.IO;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Numerics;
using Silk.NET.OpenGL;
using Fuse.Scene.Model;
using Fuse.AssetManagement;
using Fuse.Renderer;
using Fuse.Core;
using Shader = Fuse.Renderer.Shader;
using Mesh = Fuse.Renderer.Mesh;
using Texture = Fuse.Renderer.Texture;
using Fuse.Renderer.Materials;
using Fuse.Scene.Geometry;
using Brush = Fuse.Scene.Model.Brush;

namespace Blowtorch;

public class EditorAssetService : IDisposable
{
    public const string DefaultSkyboxPath = "Textures/" + Bible.Skybox;
    private const int PreviewTextureMaxDimension = 256;

    private readonly GL _gl;
    private readonly AssetManager _assets;
    private Shader _shader = null!;
    private Shader _gridShader = null!;
    private Shader _shadowShader = null!;
    private Shader _pointShadowShader = null!;
    private Shader _skyboxShader = null!;
    private Mesh _skyboxMesh = null!;
    private uint _defaultTex;
    private Texture? _skyboxTexture;
    private string _skyboxPath = "";
    private ImageBasedLighting? _imageBasedLighting;
    private SkyboxSettings _skyboxSettings = new();
    private VolumetricCloudRenderer? _cloudRenderer;
    private VolumetricFogRenderer? _fogRenderer;
    private OceanRenderer? _oceanRenderer;
    private ProceduralGrassRenderer? _grassRenderer;
    private ulong _skyboxSettingsSignature;
    private ulong _proceduralIblSignature;
    private long _proceduralIblLastAttemptMilliseconds;
    private const long ProceduralIblRefreshIntervalMilliseconds = 500;
    private readonly Dictionary<string, uint> _texCache = [];
    private readonly Dictionary<string, TerrainHeightmapBrush?> _terrainHeightmapBrushCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, uint> _terrainHeightmapBrushPreviewTextures = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Mesh?> _meshCache = [];
    private readonly Dictionary<string, GeometryGraphAsset> _liveGeometryGraphs = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _brushMeshKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _geometryMeshKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _staircaseMeshKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _catalogLock = new();
    private readonly ConcurrentQueue<string> _pendingTextureInvalidations = new();
    private readonly ConcurrentQueue<string> _pendingTerrainHeightmapBrushInvalidations = new();
    private readonly ConcurrentQueue<string> _pendingMaterialReloads = new();
    private readonly ConcurrentQueue<string> _pendingGeometryReloads = new();
    private IReadOnlyList<string>? _materialCatalog;
    private IReadOnlyList<string>? _textureCatalog;
    private IReadOnlyList<string>? _geometryCatalog;
    private FileSystemWatcher? _materialWatcher;
    private FileSystemWatcher? _textureWatcher;
    private FileSystemWatcher? _geometryWatcher;
    private long _assetRevision;
    private string _fuseResPath = "";
    private IReadOnlyList<string>? _terrainHeightmapBrushCatalog;

    public EditorAssetService(GL gl)
    {
        _gl = gl;
        _assets = new AssetManager(gl);
    }

    public string FuseResPath => _fuseResPath;
    public Shader DefaultShader => _shader;
    public Shader GridShader => _gridShader;
    public Shader ShadowShader => _shadowShader;
    public Shader PointShadowShader => _pointShadowShader;
    public Shader SkyboxShader => _skyboxShader;
    public Mesh SkyboxMesh => _skyboxMesh;
    public uint DefaultTexture => _defaultTex;
    public AssetManager AssetManager => _assets;
    public ImageBasedLighting? ImageBasedLighting => _imageBasedLighting;
    public Texture? SkyboxTexture => _skyboxTexture;
    public string SkyboxPath => _skyboxPath;
    public SkyboxSettings SkyboxSettings => _skyboxSettings;
    public VolumetricCloudRenderer? CloudRenderer => _cloudRenderer;
    public VolumetricFogRenderer? FogRenderer => _fogRenderer;
    public OceanRenderer? OceanRenderer => _oceanRenderer;
    public ProceduralGrassRenderer? GrassRenderer => _grassRenderer;
    public bool IsProceduralSkybox => _skyboxSettings.Mode == SkyboxMode.Procedural;
    public ulong AssetRevision => unchecked((ulong)System.Threading.Interlocked.Read(ref _assetRevision));

    public MaterialRuntime? GetOrCreateMaterial(string? materialRelPath) =>
        _assets.TryGetMaterial(materialRelPath);

    public MaterialRuntime ReloadMaterial(string materialRelPath)
    {
        MaterialRuntime material = _assets.ReloadMaterial(materialRelPath);
        System.Threading.Interlocked.Increment(ref _assetRevision);
        return material;
    }

    public IReadOnlyList<string> EnumerateMaterials()
    {
        lock (_catalogLock)
        {
            if (_materialCatalog != null)
                return _materialCatalog;
        }

        string materialDirectory = Path.Combine(_fuseResPath, "Materials");
        if (!Directory.Exists(materialDirectory))
            return [];
        IReadOnlyList<string> catalog = Directory.EnumerateFiles(materialDirectory, "*.fmat", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(_fuseResPath, path).Replace('\\', '/'))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        lock (_catalogLock)
            _materialCatalog ??= catalog;
        return _materialCatalog;
    }

    public IReadOnlyList<string> EnumerateTextures()
    {
        lock (_catalogLock)
        {
            if (_textureCatalog != null)
                return _textureCatalog;
        }

        string textureDirectory = Path.Combine(_fuseResPath, "Textures");
        if (!Directory.Exists(textureDirectory))
            return [];
        string[] extensions = [".png", ".jpg", ".jpeg", ".bmp", ".tga", ".dds"];
        IReadOnlyList<string> catalog = Directory.EnumerateFiles(textureDirectory, "*.*", SearchOption.AllDirectories)
            .Where(path => extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            .Select(path => Path.GetRelativePath(_fuseResPath, path).Replace('\\', '/'))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        lock (_catalogLock)
            _textureCatalog ??= catalog;
        return _textureCatalog;
    }

    public IReadOnlyList<string> EnumerateTerrainHeightmapBrushes()
    {
        lock (_catalogLock)
        {
            if (_terrainHeightmapBrushCatalog != null)
                return _terrainHeightmapBrushCatalog;
        }

        string brushDirectory = Path.Combine(
            _fuseResPath,
            "Textures",
            "Terrain",
            "heightmap_brushes");
        if (!Directory.Exists(brushDirectory))
            return [];

        string[] extensions = [".png", ".jpg", ".jpeg", ".bmp", ".tga", ".exr", ".hdr"];
        IReadOnlyList<string> catalog = Directory.EnumerateFiles(
                brushDirectory,
                "*.*",
                SearchOption.AllDirectories)
            .Where(path => extensions.Contains(
                Path.GetExtension(path),
                StringComparer.OrdinalIgnoreCase))
            .Select(path => Path.GetRelativePath(_fuseResPath, path).Replace('\\', '/'))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        lock (_catalogLock)
            _terrainHeightmapBrushCatalog ??= catalog;
        return _terrainHeightmapBrushCatalog;
    }

    public TerrainHeightmapBrush? LoadTerrainHeightmapBrush(string relativePath)
    {
        string fullPath = Path.GetFullPath(ResolveEditorAssetPath(relativePath));
        if (_terrainHeightmapBrushCache.TryGetValue(fullPath, out TerrainHeightmapBrush? cached))
            return cached;

        if (!File.Exists(fullPath))
        {
            _terrainHeightmapBrushCache[fullPath] = null;
            return null;
        }

        try
        {
            TerrainHeightmapBrush brush = TerrainHeightmapBrush.Load(fullPath);
            _terrainHeightmapBrushCache[fullPath] = brush;
            return brush;
        }
        catch (Exception ex)
        {
            _terrainHeightmapBrushCache[fullPath] = null;
            Logger.Warn($"Terrain heightmap brush could not be loaded: {fullPath}: {ex.Message}");
            return null;
        }
    }

    public uint RequestTerrainHeightmapBrushPreview(string relativePath)
    {
        string fullPath = Path.GetFullPath(ResolveEditorAssetPath(relativePath));
        if (_terrainHeightmapBrushPreviewTextures.TryGetValue(fullPath, out uint cachedTexture))
            return cachedTexture;

        TerrainHeightmapBrush? brush = LoadTerrainHeightmapBrush(relativePath);
        if (brush == null)
            return 0;

        uint texture = 0;
        try
        {
            byte[] pixels = brush.CreatePreviewPixels();
            texture = _gl.GenTexture();
            _gl.BindTexture(TextureTarget.Texture2D, texture);
            _gl.TexImage2D(
                TextureTarget.Texture2D,
                0,
                (int)InternalFormat.Rgba,
                (uint)brush.Width,
                (uint)brush.Height,
                0,
                PixelFormat.Rgba,
                PixelType.UnsignedByte,
                pixels);
            _gl.TexParameter(
                TextureTarget.Texture2D,
                TextureParameterName.TextureWrapS,
                (int)TextureWrapMode.ClampToEdge);
            _gl.TexParameter(
                TextureTarget.Texture2D,
                TextureParameterName.TextureWrapT,
                (int)TextureWrapMode.ClampToEdge);
            _gl.TexParameter(
                TextureTarget.Texture2D,
                TextureParameterName.TextureMinFilter,
                (int)TextureMinFilter.Linear);
            _gl.TexParameter(
                TextureTarget.Texture2D,
                TextureParameterName.TextureMagFilter,
                (int)TextureMagFilter.Linear);

            _terrainHeightmapBrushPreviewTextures[fullPath] = texture;
            return texture;
        }
        catch (Exception ex)
        {
            if (texture != 0)
                _gl.DeleteTexture(texture);
            Logger.Warn($"Terrain heightmap brush preview failed: {fullPath}: {ex.Message}");
            return 0;
        }
    }

    public IReadOnlyList<string> EnumerateSkyboxes() =>
        EnumerateTextures()
            .Where(path => path.StartsWith("Textures/Skybox/", StringComparison.OrdinalIgnoreCase))
            .ToArray();

    public IReadOnlyList<string> EnumerateModels()
    {
        string[] directories =
        [
            Path.Combine(_fuseResPath, "Models"),
            Path.Combine(_fuseResPath, "skinned_models")
        ];
        string[] extensions = [".obj", ".fbx", ".gltf", ".glb"];
        return directories
            .Where(Directory.Exists)
            .SelectMany(directory => Directory.EnumerateFiles(directory, "*.*", SearchOption.AllDirectories))
            .Where(path => extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            .Select(path => Path.GetRelativePath(_fuseResPath, path).Replace('\\', '/'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public IReadOnlyList<string> EnumerateTerrainAssets()
    {
        if (!Directory.Exists(_fuseResPath))
            return [];

        string[] extensions = [".terrain", ".trn", ".fterrain"];
        return Directory.EnumerateFiles(_fuseResPath, "*.*", SearchOption.AllDirectories)
            .Where(path => extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            .Select(path => Path.GetRelativePath(_fuseResPath, path).Replace('\\', '/'))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public IReadOnlyList<string> EnumerateGeometryGraphs()
    {
        lock (_catalogLock)
        {
            if (_geometryCatalog != null)
                return _geometryCatalog;
        }

        string geometryDirectory = Path.Combine(_fuseResPath, "Geometry");
        if (!Directory.Exists(geometryDirectory))
            return [];
        IReadOnlyList<string> catalog = Directory.EnumerateFiles(geometryDirectory, "*.fgeo", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(_fuseResPath, path).Replace('\\', '/'))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        lock (_catalogLock)
            _geometryCatalog ??= catalog;
        return _geometryCatalog;
    }

    public string ResolveEditorAssetPath(string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
            return Path.GetFullPath(relativePath);

        string normalized = relativePath.Replace('\\', '/');
        if (normalized.StartsWith("res/", StringComparison.OrdinalIgnoreCase))
            normalized = normalized[4..];
        return Path.GetFullPath(Path.Combine(_fuseResPath, normalized.Replace('/', Path.DirectorySeparatorChar)));
    }

    public bool GetAssetStatus(
        EditorAssetKind kind,
        string relativePath,
        out string error,
        bool validateContents = false)
    {
        error = "";
        string fullPath = ResolveEditorAssetPath(relativePath);
        if (!File.Exists(fullPath))
        {
            error = "File not found.";
            return false;
        }

        // Catalog refreshes must remain cheap. Material and geometry parsing is
        // deferred to the details pane so opening Asset Browser never blocks on
        // every graph asset.
        if (kind != EditorAssetKind.Material && kind != EditorAssetKind.GeometryGraph || !validateContents)
            return true;

        try
        {
            if (kind == EditorAssetKind.GeometryGraph)
            {
                GeometryGraphAsset graph = GeometryGraphAsset.Load(fullPath);
                if (graph.Graph.FindOutput() == null)
                {
                    error = "Geometry graph has no output node.";
                    return false;
                }
                return true;
            }

            MaterialAsset material = MaterialAsset.Load(fullPath);
            foreach (MaterialGraphNode node in material.Graph.Nodes)
            {
                if (node.Type is not ("Texture2D" or "ScalarTexture" or "PackedMetallicRoughness"))
                    continue;
                string texturePath = MaterialAsset.GetString(node.Properties, "path", "");
                if (!string.IsNullOrWhiteSpace(texturePath) &&
                    !File.Exists(MaterialRuntime.ResolveAssetPath(texturePath)))
                {
                    error = $"Missing texture: {texturePath}";
                    return false;
                }
            }
            return true;
        }
        catch (Exception ex)
        {
            error = $"Invalid material: {ex.Message}";
            return false;
        }
    }

    public Vector3 GetMaterialThumbnailColor(string materialRelPath)
    {
        try
        {
            MaterialAsset material = MaterialAsset.Load(ResolveEditorAssetPath(materialRelPath));
            MaterialGraphNode? output = material.Graph.FindOutput();
            return output == null
                ? Vector3.One
                : MaterialAsset.GetVector3(output.Properties, "base_color", Vector3.One);
        }
        catch
        {
            return new Vector3(0.35f, 0.08f, 0.08f);
        }
    }

    public void RefreshCatalogs()
    {
        lock (_catalogLock)
        {
            _materialCatalog = null;
            _textureCatalog = null;
            _terrainHeightmapBrushCatalog = null;
            _geometryCatalog = null;
        }
        System.Threading.Interlocked.Increment(ref _assetRevision);
    }

    /// <summary>
    /// Sends an editor asset to the operating system recycle bin instead of
    /// permanently deleting it. The path is validated against Fuse/res so a
    /// malformed catalog entry cannot delete an unrelated file.
    /// </summary>
    public bool SendAssetToRecycleBin(EditorAssetKind kind, string relativePath, out string error)
    {
        error = "";
        string fullPath = ResolveEditorAssetPath(relativePath);
        string resourceRoot = Path.GetFullPath(_fuseResPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        string normalizedFullPath = Path.GetFullPath(fullPath);

        if (!normalizedFullPath.StartsWith(resourceRoot, StringComparison.OrdinalIgnoreCase))
        {
            error = "The asset is outside Fuse/res and cannot be deleted from the editor.";
            return false;
        }

        if (!File.Exists(normalizedFullPath))
        {
            error = $"Asset not found: {relativePath}";
            RefreshCatalogs();
            return false;
        }

        try
        {
            // Remove only the low-resolution browser preview. Runtime/full-
            // resolution textures remain valid for the current scene.
            if (kind is EditorAssetKind.Texture or EditorAssetKind.Skybox)
            {
                InvalidateTexture(relativePath);
                _assets.RemoveTextureCacheEntry(
                    normalizedFullPath,
                    TextureColorSpace.Srgb,
                    PreviewTextureMaxDimension);
            }

            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                normalizedFullPath,
                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);

            RefreshCatalogs();
            Logger.Info($"Asset moved to recycle bin: {normalizedFullPath}");
            return true;
        }
        catch (Exception ex)
        {
            error = $"Could not move asset to the recycle bin: {ex.Message}";
            Logger.Warn($"Asset deletion failed for '{normalizedFullPath}': {ex.Message}");
            return false;
        }
    }

    public bool ReimportAsset(EditorAssetKind kind, string relativePath, out string error)
    {
        error = "";
        try
        {
            string fullPath = ResolveEditorAssetPath(relativePath);
            if (!File.Exists(fullPath))
            {
                error = $"Asset not found: {relativePath}";
                return false;
            }

            switch (kind)
            {
                case EditorAssetKind.Material:
                    _assets.ReloadMaterial(fullPath);
                    break;
                case EditorAssetKind.Texture:
                case EditorAssetKind.Skybox:
                    InvalidateTexture(relativePath);
                    _assets.ReloadTexture(fullPath, TextureColorSpace.Srgb);
                    if (kind == EditorAssetKind.Skybox && !SetSkyboxTexture(relativePath))
                    {
                        error = $"Could not reload skybox: {relativePath}";
                        return false;
                    }
                    break;
                case EditorAssetKind.Model:
                    string modelKey = Path.GetFullPath(fullPath);
                    if (_meshCache.Remove(modelKey, out Mesh? mesh))
                        mesh?.Dispose();
                    _assets.ReloadModel(modelKey);
                    break;
                case EditorAssetKind.GeometryGraph:
                    GeometryGraphCache.Invalidate(fullPath);
                    InvalidateGeneratedGeometryMeshes();
                    break;
            }

            RefreshCatalogs();
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            Logger.Warn($"Asset reimport failed for '{relativePath}': {ex.Message}");
            return false;
        }
    }

    public void Initialize(string baseDirectory)
    {
        //_fuseResPath = Path.GetFullPath(Path.Combine(baseDirectory, @"..\..\..\..\Fuse\res"));
        _fuseResPath = Fuse.ResPath.Path;

        _shader = _assets.GetShader(
            Path.Combine(_fuseResPath, "Shaders", "default.vert"),
            Path.Combine(_fuseResPath, "Shaders", "default.frag"));

        _gridShader = _assets.GetShader(
            Path.Combine(_fuseResPath, "Shaders", "grid.vert"),
            Path.Combine(_fuseResPath, "Shaders", "grid.frag"));

        _shadowShader = _assets.GetShader(
            Path.Combine(_fuseResPath, "Shaders", "shadow.vert"),
            Path.Combine(_fuseResPath, "Shaders", "shadow.frag"));

        _pointShadowShader = _assets.GetShader(
            Path.Combine(_fuseResPath, "Shaders", "point_shadow.vert"),
            Path.Combine(_fuseResPath, "Shaders", "point_shadow.frag"));

        _skyboxShader = _assets.GetShader(
            Path.Combine(_fuseResPath, "Shaders", "skybox.vert"),
            Path.Combine(_fuseResPath, "Shaders", "skybox.frag"));
        _skyboxMesh = _assets.GetMesh("cube")!;

        _shader.BindUniformBlock("LightingBlock", LightingBuffer.BindingPoint);
        SetSkyboxTexture(null);

        try
        {
            _cloudRenderer = new VolumetricCloudRenderer(_gl);
        }
        catch (Exception ex)
        {
            _cloudRenderer = null;
            Logger.Warn($"Blowtorch volumetric clouds disabled: {ex.Message}");
        }

        try
        {
            _fogRenderer = new VolumetricFogRenderer(
                _gl,
                _cloudRenderer?.BaseNoiseTexture ?? 0);
        }
        catch (Exception ex)
        {
            _fogRenderer = null;
            Logger.Warn($"Blowtorch volumetric fog disabled: {ex.Message}");
        }

        try
        {
            _oceanRenderer = new OceanRenderer(_gl);
        }
        catch (Exception ex)
        {
            _oceanRenderer = null;
            Logger.Warn($"Blowtorch ocean rendering disabled: {ex.Message}");
        }

        try
        {
            _grassRenderer = new ProceduralGrassRenderer(_gl);
        }
        catch (Exception ex)
        {
            _grassRenderer = null;
            Logger.Warn($"Blowtorch procedural grass disabled: {ex.Message}");
        }

        string crateTexPath = Path.Combine(_fuseResPath, "Textures", "dev_measurecrate01.bmp");
        if (File.Exists(crateTexPath))
        {
            var crateTex = new Texture(_gl, crateTexPath);
            _defaultTex = crateTex.ID;
        }

        StartAssetWatchers();
    }

    public bool SetSkyboxTexture(string? texturePath)
    {
        string normalizedPath = NormalizeSkyboxPath(texturePath);
        if (!IsProceduralSkybox &&
            normalizedPath.Equals(_skyboxPath, StringComparison.OrdinalIgnoreCase) &&
            _skyboxTexture?.ID != 0)
            return true;

        string fullPath = Path.GetFullPath(Path.Combine(_fuseResPath, normalizedPath));
        if (!File.Exists(fullPath))
        {
            Logger.Warn($"Skybox texture not found: {fullPath}");
            return false;
        }

        Texture texture;
        try
        {
            texture = _assets.GetTexture(fullPath, TextureColorSpace.Srgb);
        }
        catch (Exception ex)
        {
            Logger.Warn($"Blowtorch skybox load failed: {ex.Message}");
            return false;
        }

        if (texture.ID == 0)
            return false;

        _gl.BindTexture(TextureTarget.Texture2D, texture.ID);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);

        ImageBasedLighting? replacement = null;
        try
        {
            replacement = new ImageBasedLighting(_gl, texture);
        }
        catch (Exception ex)
        {
            Logger.Warn($"Blowtorch IBL disabled for '{normalizedPath}': {ex.Message}");
        }

        _imageBasedLighting?.Dispose();
        _imageBasedLighting = replacement;
        _fogRenderer?.InvalidateHistory();
        _skyboxTexture = texture;
        _skyboxPath = normalizedPath;
        _skyboxSettings = new SkyboxSettings();
        _skyboxSettingsSignature = ProceduralSky.ComputeSettingsSignature(_skyboxSettings);
        _proceduralIblSignature = 0;
        _proceduralIblLastAttemptMilliseconds = 0;
        System.Threading.Interlocked.Increment(ref _assetRevision);
        return true;
    }

    public bool SetProceduralSkybox(SkyboxSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        SkyboxSettings next = settings.Clone();
        next.Mode = SkyboxMode.Procedural;
        ulong signature = ProceduralSky.ComputeSettingsSignature(next);
        bool alreadyProcedural = IsProceduralSkybox;
        if (alreadyProcedural && signature == _skyboxSettingsSignature)
            return true;

        if (!alreadyProcedural)
        {
            _imageBasedLighting?.Dispose();
            _imageBasedLighting = null;
            _proceduralIblSignature = 0;
            _proceduralIblLastAttemptMilliseconds = 0;
        }

        _skyboxSettings = next;
        _skyboxSettingsSignature = signature;
        _skyboxPath = "";
        _fogRenderer?.InvalidateHistory();
        System.Threading.Interlocked.Increment(ref _assetRevision);
        return true;
    }

    /// <summary>
    /// Keeps the visible procedural sky immediate while refreshing its
    /// precomputed IBL at a bounded rate. This is called once per visible
    /// viewport render, so the timestamp/signature guard is important in Quad
    /// View and while a setting is being dragged.
    /// </summary>
    public void UpdateProceduralSkybox(IReadOnlyList<Light> lights)
    {
        if (!IsProceduralSkybox)
            return;

        ProceduralSky.ResolveSun(
            lights,
            out Vector3 sunDirection,
            out Vector3 directionalLightColor);
        ulong signature = ProceduralSky.ComputeIblSignature(
            _skyboxSettings,
            sunDirection,
            directionalLightColor);
        long now = Environment.TickCount64;
        if (_imageBasedLighting != null && _proceduralIblSignature == signature)
            return;
        if (now - _proceduralIblLastAttemptMilliseconds < ProceduralIblRefreshIntervalMilliseconds)
            return;

        ImageBasedLighting? replacement = null;
        try
        {
            replacement = ImageBasedLighting.CreateProcedural(
                _gl,
                _skyboxSettings,
                sunDirection,
                directionalLightColor);
        }
        catch (Exception ex)
        {
            Logger.Warn($"Blowtorch procedural sky IBL disabled: {ex.Message}");
        }

        _proceduralIblLastAttemptMilliseconds = now;
        _proceduralIblSignature = signature;
        if (replacement == null)
            return;

        _imageBasedLighting?.Dispose();
        _imageBasedLighting = replacement;
        System.Threading.Interlocked.Increment(ref _assetRevision);
    }

    public void UpdateFileChanges(EditorSceneService? sceneService = null)
    {
        bool refreshedMaterial = false;
        var processedMaterials = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (_pendingMaterialReloads.TryDequeue(out string? materialPath))
        {
            if (!processedMaterials.Add(materialPath) || !File.Exists(materialPath))
                continue;
            try
            {
                _assets.ReloadMaterial(materialPath);
                refreshedMaterial = true;
            }
            catch (Exception ex)
            {
                Logger.Warn($"Material hot reload failed for '{materialPath}': {ex.Message}");
            }
        }
        if (refreshedMaterial && sceneService != null)
            sceneService.RefreshMaterials(this);

        while (_pendingTextureInvalidations.TryDequeue(out string? relativePath))
        {
            string normalized = relativePath.Replace('\\', '/');
            string? key = _texCache.Keys.FirstOrDefault(candidate =>
                NormalizeTexturePath(candidate).Equals(normalized, StringComparison.OrdinalIgnoreCase));
            if (key == null || !_texCache.Remove(key, out uint textureId))
                continue;
            if (textureId != 0)
                _gl.DeleteTexture(textureId);
        }

        while (_pendingTerrainHeightmapBrushInvalidations.TryDequeue(out string? brushPath))
            InvalidateTerrainHeightmapBrush(brushPath);

        bool geometryUsedByScene = false;
        var processedGeometry = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (_pendingGeometryReloads.TryDequeue(out string? geometryPath))
        {
            string fullPath = Path.GetFullPath(geometryPath);
            if (!processedGeometry.Add(fullPath))
                continue;

            GeometryGraphCache.Invalidate(fullPath);
            _liveGeometryGraphs.Remove(fullPath);
            if (sceneService != null && sceneService.Document.Objects.Any(obj =>
                    !string.IsNullOrWhiteSpace(obj.GeometryGraphPath) &&
                    PathsEqual(ResolveEditorAssetPath(obj.GeometryGraphPath!), fullPath)))
            {
                geometryUsedByScene = true;
            }
        }

        if (processedGeometry.Count > 0)
        {
            // FileSystemWatcher callbacks run on a worker thread. Dispose and
            // recreate OpenGL meshes only from the editor/render thread.
            InvalidateGeneratedGeometryMeshes();
            if (geometryUsedByScene && sceneService != null)
                sceneService.PopulateScene(this);
        }
    }

    public void InvalidateGeneratedGeometryMeshes()
    {
        foreach (string key in _geometryMeshKeys.ToArray())
        {
            if (_meshCache.Remove(key, out Mesh? generated))
                generated?.Dispose();
        }
        _geometryMeshKeys.Clear();
    }

    public void SetLiveGeometryGraph(string graphPath, GeometryGraphAsset graph, EditorSceneService sceneService)
    {
        string fullPath = Path.GetFullPath(graphPath);
        _liveGeometryGraphs[fullPath] = graph;
        GeometryGraphCache.Invalidate(fullPath);
        InvalidateGeneratedGeometryMeshes();

        if (sceneService.Document.Objects.Any(obj =>
                !string.IsNullOrWhiteSpace(obj.GeometryGraphPath) &&
                PathsEqual(ResolveEditorAssetPath(obj.GeometryGraphPath!), fullPath)))
        {
            sceneService.PopulateScene(this);
            System.Threading.Interlocked.Increment(ref _assetRevision);
        }
    }

    public void ClearLiveGeometryGraph(string graphPath, EditorSceneService? sceneService = null)
    {
        bool removed = _liveGeometryGraphs.Remove(Path.GetFullPath(graphPath));
        GeometryGraphCache.Invalidate(graphPath);
        if (removed && sceneService != null)
        {
            InvalidateGeneratedGeometryMeshes();
            sceneService.PopulateScene(this);
            System.Threading.Interlocked.Increment(ref _assetRevision);
        }
    }

    public Mesh? GetOrCreateMesh(MapObject mapObj)
    {
        if (mapObj.IsStaircase)
        {
            string cacheKey = $"staircase:{mapObj.Id}";
            if (_meshCache.TryGetValue(cacheKey, out Mesh? staircaseMesh))
                return staircaseMesh;

            Vector3 sourceHalfExtents = mapObj.Body?.HalfExtents ?? new Vector3(0.5f);
            MeshData meshData = StaircaseMeshGenerator.Generate(
                sourceHalfExtents,
                mapObj.Staircase!);
            staircaseMesh = new Mesh(
                _gl,
                meshData.Vertices,
                meshData.Indices,
                meshData.LineIndices,
                meshData.Parts);
            _meshCache[cacheKey] = staircaseMesh;
            _staircaseMeshKeys.Add(cacheKey);
            return staircaseMesh;
        }

        if (!string.IsNullOrWhiteSpace(mapObj.GeometryGraphPath))
        {
            string graphPath = ResolveEditorAssetPath(mapObj.GeometryGraphPath);
            string cacheKey = $"geometry:{mapObj.Id}";
            if (_meshCache.TryGetValue(cacheKey, out Mesh? generatedMesh))
                return generatedMesh;

            MeshData? inputMesh = mapObj is Brush graphBrush ? MeshGenerator.Generate(graphBrush) : null;
            bool evaluated = _liveGeometryGraphs.TryGetValue(graphPath, out GeometryGraphAsset? liveGraph)
                ? GeometryGraphEvaluator.TryEvaluate(liveGraph, new GeometryEvaluationContext { InputMesh = inputMesh }, out GeometryEvaluationResult? result, out string error)
                : GeometryGraphCache.TryEvaluateFile(graphPath, inputMesh, out result, out error);
            if (evaluated && result != null)
            {
                if (!string.IsNullOrWhiteSpace(result.MaterialPath))
                    mapObj.MaterialPath = result.MaterialPath;
                generatedMesh = new Mesh(_gl, result.Mesh.Vertices, result.Mesh.Indices, result.Mesh.LineIndices, result.Mesh.Parts);
                _meshCache[cacheKey] = generatedMesh;
                _geometryMeshKeys.Add(cacheKey);
                return generatedMesh;
            }

            Logger.Warn($"Geometry graph '{mapObj.GeometryGraphPath}' could not be evaluated for '{mapObj.Id}': {error}");
        }

        if (mapObj is Brush brush)
        {
            if (!_meshCache.TryGetValue(brush.Id, out var mesh))
            {
                var meshData = MeshGenerator.Generate(brush);
                mesh = new Mesh(_gl, meshData.Vertices, meshData.Indices, meshData.LineIndices, meshData.Parts);
                _meshCache[brush.Id] = mesh;
                _brushMeshKeys.Add(brush.Id);
            }
            return mesh;
        }
        else if (mapObj.IsModel && mapObj.Model != null)
        {
            string modelPath = Path.GetFullPath(Path.Combine(_fuseResPath, mapObj.Model));
            if (!_meshCache.TryGetValue(modelPath, out var mesh))
            {
                var model = _assets.GetModel(modelPath);
                mesh = model?.Mesh;
                _meshCache[modelPath] = mesh;
            }
            return mesh;
        }
        else if (mapObj.Mesh != null)
        {
            if (!_meshCache.TryGetValue(mapObj.Mesh, out var mesh))
            {
                mesh = _assets.GetMesh(mapObj.Mesh);
                _meshCache[mapObj.Mesh] = mesh;
            }
            return mesh;
        }
        return null;
    }

    public uint GetOrCreateTexture(string textureRelPath)
    {
        if (string.IsNullOrEmpty(textureRelPath))
            return 0;

        if (_texCache.TryGetValue(textureRelPath, out var cachedTex))
            return cachedTex;

        string rel = textureRelPath;
        if (rel.StartsWith("res/") || rel.StartsWith("res\\"))
            rel = rel[4..];
            
        string texPath = Path.GetFullPath(Path.Combine(_fuseResPath, rel));
        if (File.Exists(texPath))
        {
            try
            {
                var texture = new Texture(_gl, texPath);
                _texCache[textureRelPath] = texture.ID;
                return texture.ID;
            }
            catch (Exception ex)
            {
                _texCache[textureRelPath] = 0;
                Logger.Warn($"Texture import failed: {texPath}: {ex.Message}");
                return 0;
            }
        }
        
        _texCache[textureRelPath] = 0;
        Logger.Warn($"Texture not found: {texPath}");
        return 0;
    }

    public uint RequestTexturePreview(string textureRelPath)
    {
        if (string.IsNullOrWhiteSpace(textureRelPath))
            return 0;

        string fullPath = ResolveEditorAssetPath(textureRelPath);
        if (!File.Exists(fullPath))
            return 0;

        try
        {
            return _assets.RequestTexture(
                fullPath,
                TextureColorSpace.Srgb,
                AssetPriority.Low,
                PreviewTextureMaxDimension).ID;
        }
        catch (Exception ex)
        {
            Logger.Warn($"Texture preview request failed: {fullPath}: {ex.Message}");
            return 0;
        }
    }

    private void InvalidateTexture(string textureRelPath)
    {
        string normalized = NormalizeTexturePath(textureRelPath);
        foreach (string key in _texCache.Keys
                     .Where(candidate => NormalizeTexturePath(candidate)
                         .Equals(normalized, StringComparison.OrdinalIgnoreCase))
                     .ToArray())
        {
            if (_texCache.Remove(key, out uint textureId) && textureId != 0)
                _gl.DeleteTexture(textureId);
        }
    }

    public void InvalidateMesh(string key)
    {
        string[] keys = _meshCache.Keys
            .Where(candidate => candidate.Equals(key, StringComparison.OrdinalIgnoreCase) ||
                                candidate.Equals($"geometry:{key}", StringComparison.OrdinalIgnoreCase) ||
                                candidate.Equals($"staircase:{key}", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        foreach (string cacheKey in keys)
        {
            if (_meshCache.Remove(cacheKey, out Mesh? mesh))
                mesh?.Dispose();
            _brushMeshKeys.Remove(cacheKey);
            _geometryMeshKeys.Remove(cacheKey);
            _staircaseMeshKeys.Remove(cacheKey);
        }
    }

    public void ClearBrushMeshes()
    {
        foreach (string key in _brushMeshKeys.ToArray())
        {
            if (_meshCache.Remove(key, out Mesh? mesh))
            {
                mesh?.Dispose();
            }
        }
        _brushMeshKeys.Clear();
        foreach (string key in _geometryMeshKeys.ToArray())
        {
            if (_meshCache.Remove(key, out Mesh? mesh))
                mesh?.Dispose();
        }
        _geometryMeshKeys.Clear();
        foreach (string key in _staircaseMeshKeys.ToArray())
        {
            if (_meshCache.Remove(key, out Mesh? mesh))
                mesh?.Dispose();
        }
        _staircaseMeshKeys.Clear();
    }

    public void Dispose()
    {
        _materialWatcher?.Dispose();
        _textureWatcher?.Dispose();
        _geometryWatcher?.Dispose();
        foreach (var texId in _texCache.Values)
        {
            if (texId != 0) _gl.DeleteTexture(texId);
        }
        foreach (uint textureId in _terrainHeightmapBrushPreviewTextures.Values)
        {
            if (textureId != 0)
                _gl.DeleteTexture(textureId);
        }
        _terrainHeightmapBrushPreviewTextures.Clear();
        _terrainHeightmapBrushCache.Clear();
        ClearBrushMeshes();
        _meshCache.Clear();
        if (_defaultTex != 0) _gl.DeleteTexture(_defaultTex);
        _fogRenderer?.Dispose();
        _cloudRenderer?.Dispose();
        _oceanRenderer?.Dispose();
        _grassRenderer?.Dispose();
        _imageBasedLighting?.Dispose();
        _assets.Clear();
    }

    private void StartAssetWatchers()
    {
        string materials = Path.Combine(_fuseResPath, "Materials");
        string textures = Path.Combine(_fuseResPath, "Textures");
        string geometry = Path.Combine(_fuseResPath, "Geometry");
        if (Directory.Exists(materials))
        {
            _materialWatcher = CreateWatcher(materials, "*.fmat", (_, args) =>
            {
                lock (_catalogLock) _materialCatalog = null;
                _pendingMaterialReloads.Enqueue(args.FullPath);
                System.Threading.Interlocked.Increment(ref _assetRevision);
            });
        }
        if (Directory.Exists(textures))
        {
            _textureWatcher = CreateWatcher(textures, "*.*", (_, args) =>
            {
                string extension = Path.GetExtension(args.FullPath);
                if (extension is not (".png" or ".jpg" or ".jpeg" or ".bmp" or ".tga" or ".dds") &&
                    !extension.Equals(".exr", StringComparison.OrdinalIgnoreCase) &&
                    !extension.Equals(".hdr", StringComparison.OrdinalIgnoreCase) &&
                    !extension.Equals(".PNG", StringComparison.OrdinalIgnoreCase) &&
                    !extension.Equals(".JPG", StringComparison.OrdinalIgnoreCase) &&
                    !extension.Equals(".JPEG", StringComparison.OrdinalIgnoreCase) &&
                    !extension.Equals(".BMP", StringComparison.OrdinalIgnoreCase) &&
                    !extension.Equals(".TGA", StringComparison.OrdinalIgnoreCase) &&
                    !extension.Equals(".DDS", StringComparison.OrdinalIgnoreCase))
                    return;

                string relativePath = Path.GetRelativePath(_fuseResPath, args.FullPath);
                lock (_catalogLock)
                {
                    _textureCatalog = null;
                    _terrainHeightmapBrushCatalog = null;
                }
                _pendingTextureInvalidations.Enqueue(relativePath);
                _pendingTerrainHeightmapBrushInvalidations.Enqueue(relativePath);
                System.Threading.Interlocked.Increment(ref _assetRevision);
            });
        }
        if (Directory.Exists(geometry))
        {
            _geometryWatcher = CreateWatcher(geometry, "*.fgeo", (_, args) =>
            {
                lock (_catalogLock) _geometryCatalog = null;
                _pendingGeometryReloads.Enqueue(args.FullPath);
                System.Threading.Interlocked.Increment(ref _assetRevision);
            });
        }
    }

    private static FileSystemWatcher CreateWatcher(
        string directory,
        string filter,
        FileSystemEventHandler handler)
    {
        var watcher = new FileSystemWatcher(directory, filter)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime,
            EnableRaisingEvents = true
        };
        watcher.Created += handler;
        watcher.Changed += handler;
        watcher.Deleted += handler;
        watcher.Renamed += (_, args) => handler(watcher, args);
        return watcher;
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    private void InvalidateTerrainHeightmapBrush(string relativePath)
    {
        string fullPath = Path.GetFullPath(ResolveEditorAssetPath(relativePath));
        _terrainHeightmapBrushCache.Remove(fullPath);
        if (_terrainHeightmapBrushPreviewTextures.Remove(fullPath, out uint textureId) && textureId != 0)
            _gl.DeleteTexture(textureId);
    }

    private string NormalizeTexturePath(string path)
    {
        string relative = path.Replace('\\', '/');
        if (relative.StartsWith("res/", StringComparison.OrdinalIgnoreCase))
            relative = relative[4..];
        if (Path.IsPathRooted(relative))
            relative = Path.GetRelativePath(_fuseResPath, relative).Replace('\\', '/');
        return relative;
    }

    private static string NormalizeSkyboxPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return DefaultSkyboxPath;

        string normalized = path.Replace('\\', '/').TrimStart('/');
        if (normalized.StartsWith("res/", StringComparison.OrdinalIgnoreCase))
            normalized = normalized[4..];
        if (normalized.StartsWith("Textures/", StringComparison.OrdinalIgnoreCase))
            return normalized;
        return $"Textures/{normalized}";
    }
}
