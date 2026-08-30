using System;
using System.IO;
using System.Collections.Generic;
using System.Collections.Concurrent;
using Silk.NET.OpenGL;
using Fuse.Scene.Model;
using Fuse.AssetManagement;
using Fuse.Renderer;
using Fuse.Core;
using Shader = Fuse.Renderer.Shader;
using Mesh = Fuse.Renderer.Mesh;
using Texture = Fuse.Renderer.Texture;
using Fuse.Renderer.Materials;

namespace Blowtorch;

public class EditorAssetService : IDisposable
{
    public const string DefaultSkyboxPath = "Textures/" + Bible.Skybox;

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
    private readonly Dictionary<string, uint> _texCache = [];
    private readonly Dictionary<string, Mesh?> _meshCache = [];
    private readonly HashSet<string> _brushMeshKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _catalogLock = new();
    private readonly ConcurrentQueue<string> _pendingTextureInvalidations = new();
    private readonly ConcurrentQueue<string> _pendingMaterialReloads = new();
    private IReadOnlyList<string>? _materialCatalog;
    private IReadOnlyList<string>? _textureCatalog;
    private FileSystemWatcher? _materialWatcher;
    private FileSystemWatcher? _textureWatcher;
    private long _assetRevision;
    private string _fuseResPath = "";

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
        string[] extensions = [".png", ".jpg", ".jpeg", ".bmp", ".tga"];
        IReadOnlyList<string> catalog = Directory.EnumerateFiles(textureDirectory, "*.*", SearchOption.AllDirectories)
            .Where(path => extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            .Select(path => Path.GetRelativePath(_fuseResPath, path).Replace('\\', '/'))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        lock (_catalogLock)
            _textureCatalog ??= catalog;
        return _textureCatalog;
    }

    public IReadOnlyList<string> EnumerateSkyboxes() =>
        EnumerateTextures()
            .Where(path => path.StartsWith("Textures/Skybox/", StringComparison.OrdinalIgnoreCase))
            .ToArray();

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
        if (normalizedPath.Equals(_skyboxPath, StringComparison.OrdinalIgnoreCase) &&
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
        _skyboxTexture = texture;
        _skyboxPath = normalizedPath;
        System.Threading.Interlocked.Increment(ref _assetRevision);
        return true;
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
    }

    public Mesh? GetOrCreateMesh(MapObject mapObj)
    {
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
            var texture = new Texture(_gl, texPath);
            _texCache[textureRelPath] = texture.ID;
            return texture.ID;
        }
        
        _texCache[textureRelPath] = 0;
        Logger.Warn($"Texture not found: {texPath}");
        return 0;
    }

    public void InvalidateMesh(string key)
    {
        if (_meshCache.TryGetValue(key, out var mesh))
        {
            mesh?.Dispose();
            _meshCache.Remove(key);
            _brushMeshKeys.Remove(key);
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
    }

    public void Dispose()
    {
        _materialWatcher?.Dispose();
        _textureWatcher?.Dispose();
        foreach (var texId in _texCache.Values)
        {
            if (texId != 0) _gl.DeleteTexture(texId);
        }
        ClearBrushMeshes();
        _meshCache.Clear();
        if (_defaultTex != 0) _gl.DeleteTexture(_defaultTex);
        _imageBasedLighting?.Dispose();
        _assets.Clear();
    }

    private void StartAssetWatchers()
    {
        string materials = Path.Combine(_fuseResPath, "Materials");
        string textures = Path.Combine(_fuseResPath, "Textures");
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
                if (extension is not (".png" or ".jpg" or ".jpeg" or ".bmp" or ".tga") &&
                    !extension.Equals(".PNG", StringComparison.OrdinalIgnoreCase) &&
                    !extension.Equals(".JPG", StringComparison.OrdinalIgnoreCase) &&
                    !extension.Equals(".JPEG", StringComparison.OrdinalIgnoreCase) &&
                    !extension.Equals(".BMP", StringComparison.OrdinalIgnoreCase) &&
                    !extension.Equals(".TGA", StringComparison.OrdinalIgnoreCase))
                    return;

                lock (_catalogLock) _textureCatalog = null;
                _pendingTextureInvalidations.Enqueue(Path.GetRelativePath(_fuseResPath, args.FullPath));
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
