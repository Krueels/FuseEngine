using System.Numerics;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Silk.NET.OpenGL;
using Fuse.Core;
using Fuse.Renderer.Materials;
using Fuse.Scene.Model;

namespace Fuse.AssetManagement;

public enum AssetPriority
{
    Critical = 0,
    High = 10,
    Normal = 20,
    Low = 30
}

public class AssetManager
{
    private interface IBackgroundAssetJob
    {
        void Execute(AssetManager manager);
    }

    private sealed class TextureDecodeJob : IBackgroundAssetJob
    {
        private readonly string _key;
        private readonly string _path;
        private readonly Renderer.Texture _target;
        private readonly AssetPriority _priority;
        private readonly int _maxDimension;

        public TextureDecodeJob(string key, string path, Renderer.Texture target, AssetPriority priority, int maxDimension)
        {
            _key = key;
            _path = path;
            _target = target;
            _priority = priority;
            _maxDimension = maxDimension;
        }

        public void Execute(AssetManager manager)
        {
            // This stage performs file I/O and image decoding only. It never touches GL.
            Renderer.TextureUploadData? data = Renderer.Texture.DecodeFile(_path, _maxDimension);
            manager.EnqueueGpuUpload(_priority, () =>
            {
                if (data != null)
                    _target.ApplyUpload(data, _path, keepCpuPixels: _maxDimension <= 0);
                else
                    _target.MarkFailed();
                manager.CompleteTextureWaiters(_key, _target);
            });
        }
    }

    private sealed class FilePreloadJob : IBackgroundAssetJob
    {
        private readonly string[] _paths;
        private readonly Action<AssetManager, string> _enqueueOnRenderThread;
        private readonly AssetPriority _priority;

        public FilePreloadJob(
            IEnumerable<string> paths,
            Action<AssetManager, string> enqueueOnRenderThread,
            AssetPriority priority)
        {
            _paths = paths.ToArray();
            _enqueueOnRenderThread = enqueueOnRenderThread;
            _priority = priority;
        }

        public void Execute(AssetManager manager)
        {
            foreach (string path in _paths)
            {
                string resolvedReference = ResolveAssetReferencePath(path);
                string resolvedFile = GetAssetFilePath(resolvedReference);
                if (!File.Exists(resolvedFile))
                {
                    Logger.Warn($"Asset preload skipped; file not found: {resolvedFile}");
                    continue;
                }

                // Use the canonical path in the render-thread callback as well.
                // Passing the original relative reference here made GetModel/GetMaterial
                // resolve it against bin/Debug instead of the res directory.
                manager.EnqueueGpuUpload(_priority, () => _enqueueOnRenderThread(manager, resolvedReference));
            }
        }
    }

    private sealed class ShaderPreloadJob : IBackgroundAssetJob
    {
        private readonly string _vertexPath;
        private readonly string _fragmentPath;
        private readonly AssetPriority _priority;

        public ShaderPreloadJob(string vertexPath, string fragmentPath, AssetPriority priority)
        {
            _vertexPath = vertexPath;
            _fragmentPath = fragmentPath;
            _priority = priority;
        }

        public void Execute(AssetManager manager)
        {
            string vertexFile = ResolveAssetPath(_vertexPath);
            string fragmentFile = ResolveAssetPath(_fragmentPath);
            if (!File.Exists(vertexFile) || !File.Exists(fragmentFile))
            {
                Logger.Warn($"Shader preload skipped; file not found: {vertexFile} / {fragmentFile}");
                return;
            }

            try
            {
                string vertexSource = Renderer.Shader.PreprocessIncludes(
                    File.ReadAllText(vertexFile), Path.GetDirectoryName(vertexFile)!);
                string fragmentSource = Renderer.Shader.PreprocessIncludes(
                    File.ReadAllText(fragmentFile), Path.GetDirectoryName(fragmentFile)!);

                manager.EnqueueGpuUpload(_priority, () => manager.LoadShaderFromSources(
                    vertexFile, fragmentFile, vertexSource, fragmentSource));
            }
            catch (Exception ex)
            {
                Logger.Warn($"Shader preload failed for '{_vertexPath}'/'{_fragmentPath}': {ex.Message}");
            }
        }
    }

    private sealed class MapPreloadJob : IBackgroundAssetJob
    {
        private readonly string _mapPath;
        private readonly AssetPriority _priority;

        public MapPreloadJob(string mapPath, AssetPriority priority)
        {
            _mapPath = mapPath;
            _priority = priority;
        }

        public void Execute(AssetManager manager)
        {
            string resolvedMapPath = ResolveMapPath(_mapPath);
            if (!File.Exists(resolvedMapPath))
            {
                Logger.Warn($"Map preload skipped; file not found: {resolvedMapPath}");
                return;
            }

            try
            {
                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(resolvedMapPath));
                var assetReferences = new List<string>();
                CollectAssetReferences(document.RootElement, assetReferences);

                foreach (string reference in assetReferences.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    string extension = Path.GetExtension(reference).ToLowerInvariant();
                    if (IsTextureExtension(extension))
                    {
                        string resolvedTexture = ResolveAssetPath(reference);
                        if (File.Exists(resolvedTexture))
                        {
                            manager.EnqueueGpuUpload(_priority, () =>
                                manager.RequestTexture(resolvedTexture, Renderer.TextureColorSpace.Srgb, _priority));
                        }
                    }
                    else if (extension is ".obj" or ".fbx" or ".gltf" or ".glb")
                    {
                        manager.QueueModelPreload(reference, _priority);
                    }
                    else if (extension == ".fmat")
                    {
                        manager.QueueMaterialPreload(reference, _priority);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"Map asset scan failed for '{resolvedMapPath}': {ex.Message}");
            }
        }
    }

    private sealed class CachedMaterialShaders
    {
        public required Renderer.Shader StaticShader { get; init; }
        public required Renderer.Shader SkinnedShader { get; init; }
        public required string GeneratedSource { get; init; }
        public int References { get; set; }
    }

    private readonly GL _gl;
    private readonly Dictionary<string, Renderer.Texture> _textures = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Renderer.Shader> _shaders = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Renderer.Mesh> _meshes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Renderer.LoadedModel> _models = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _loadedCleanPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _missingModels = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Animation.SkinnedModel> _skinnedModels = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, MaterialRuntime> _materials = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, MaterialRuntime> _legacyMaterials = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CachedMaterialShaders> _materialShaders = new(StringComparer.Ordinal);

    private readonly object _streamingGate = new();
    private readonly object _textureGate = new();
    private readonly object _skinnedPreloadGate = new();
    private readonly PriorityQueue<IBackgroundAssetJob, int> _backgroundQueue = new();
    private readonly PriorityQueue<Action, int> _gpuUploadQueue = new();
    private readonly Dictionary<string, List<TaskCompletionSource<Renderer.Texture>>> _textureWaiters = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _pendingSkinnedPreloads = new(StringComparer.OrdinalIgnoreCase);
    private readonly AutoResetEvent _streamingWake = new(false);
    private readonly CancellationTokenSource _streamingCancellation = new();
    private readonly Task _streamingWorker;
    private bool _streamingStopped;

    public AssetManager(GL gl)
    {
        _gl = gl;
        _streamingWorker = Task.Run(StreamingWorkerLoop);
    }

    public GL Gl => _gl;

    public Renderer.Texture GetTexture(string path, Renderer.TextureColorSpace colorSpace = Renderer.TextureColorSpace.Linear)
    {
        string filePath = ResolveAssetPath(path);
        string key = $"{filePath.Replace('\\', '/')}|{colorSpace}|0";
        if (_textures.TryGetValue(key, out var tex))
            return tex;
        tex = new Renderer.Texture(_gl, filePath, colorSpace);
        _textures[key] = tex;
        return tex;
    }

    /// <summary>
    /// Requests a texture without blocking the caller on file I/O or image decoding.
    /// A valid 1x1 placeholder is returned immediately and updated in place after
    /// <see cref="PumpGpuUploads"/> runs on the render thread.
    /// </summary>
    public Renderer.Texture RequestTexture(
        string path,
        Renderer.TextureColorSpace colorSpace = Renderer.TextureColorSpace.Linear,
        AssetPriority priority = AssetPriority.Normal,
        int maxDimension = 0)
    {
        string filePath = ResolveAssetPath(path);
        string key = $"{filePath.Replace('\\', '/') }|{colorSpace}|{System.Math.Max(0, maxDimension)}";

        lock (_textureGate)
        {
            if (_textures.TryGetValue(key, out Renderer.Texture? existing))
                return existing;

            Renderer.Texture placeholder = Renderer.Texture.CreatePlaceholder(_gl, colorSpace);
            _textures[key] = placeholder;
            QueueBackground(new TextureDecodeJob(
                key, filePath, placeholder, priority, System.Math.Max(0, maxDimension)), priority);
            return placeholder;
        }
    }

    public Task<Renderer.Texture> LoadTextureAsync(
        string path,
        Renderer.TextureColorSpace colorSpace = Renderer.TextureColorSpace.Linear,
        AssetPriority priority = AssetPriority.Normal,
        int maxDimension = 0)
    {
        Renderer.Texture texture = RequestTexture(path, colorSpace, priority, maxDimension);
        if (texture.IsReady || texture.IsFailed)
            return Task.FromResult(texture);

        string key = $"{ResolveAssetPath(path).Replace('\\', '/') }|{colorSpace}|{System.Math.Max(0, maxDimension)}";
        var completion = new TaskCompletionSource<Renderer.Texture>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_textureGate)
        {
            if (texture.IsReady || texture.IsFailed)
                completion.TrySetResult(texture);
            else
            {
                if (!_textureWaiters.TryGetValue(key, out var waiters))
                    _textureWaiters[key] = waiters = [];
                waiters.Add(completion);
            }
        }
        return completion.Task;
    }

    /// <summary>
    /// Executes a bounded amount of GPU work. Call this once per frame and from
    /// loading-screen progress callbacks. All GL resource creation stays here.
    /// </summary>
    public int PumpGpuUploads(int maxUploads = 4)
    {
        int processed = 0;
        while (processed < System.Math.Max(1, maxUploads))
        {
            Action? upload = null;
            lock (_streamingGate)
            {
                if (_gpuUploadQueue.Count > 0)
                    upload = _gpuUploadQueue.Dequeue();
            }

            if (upload == null)
                break;

            try
            {
                upload();
            }
            catch (Exception ex)
            {
                Logger.Error($"Asset GPU upload failed: {ex.Message}");
            }
            processed++;
        }
        return processed;
    }

    public int PendingBackgroundLoads
    {
        get { lock (_streamingGate) return _backgroundQueue.Count; }
    }

    public int PendingGpuUploads
    {
        get { lock (_streamingGate) return _gpuUploadQueue.Count; }
    }

    public void QueueTexturePreload(
        string path,
        Renderer.TextureColorSpace colorSpace = Renderer.TextureColorSpace.Srgb,
        AssetPriority priority = AssetPriority.High)
    {
        RequestTexture(path, colorSpace, priority);
    }

    public void QueueModelPreload(string path, AssetPriority priority = AssetPriority.Normal)
    {
        QueueBackground(new FilePreloadJob(
            [path],
            static (manager, assetPath) => manager.GetModel(assetPath),
            priority), priority);
    }

    /// <summary>
    /// Queues an animated/skinned model using the same loader that the
    /// viewmodel uses. QueueModelPreload is only for static LoadedModel assets
    /// and does not populate the skinned-model cache.
    /// </summary>
    public void QueueSkinnedModelPreload(string path, AssetPriority priority = AssetPriority.Normal)
    {
        string resolvedPath = ResolveAssetPath(path);
        if (string.IsNullOrWhiteSpace(resolvedPath) || !File.Exists(resolvedPath))
            return;

        lock (_skinnedPreloadGate)
        {
            if (_skinnedModels.ContainsKey(resolvedPath) || !_pendingSkinnedPreloads.Add(resolvedPath))
                return;
        }

        QueueBackground(new FilePreloadJob(
            [resolvedPath],
            static (manager, assetPath) => manager.LoadSkinnedModelFromPreload(assetPath),
            priority), priority);
    }

    public void QueueShaderPreload(string vertexPath, string fragmentPath, AssetPriority priority = AssetPriority.Normal)
    {
        QueueBackground(new ShaderPreloadJob(vertexPath, fragmentPath, priority), priority);
    }

    public void QueueMaterialPreload(string path, AssetPriority priority = AssetPriority.Normal)
    {
        QueueBackground(new FilePreloadJob(
            [path],
            static (manager, assetPath) => manager.GetMaterial(assetPath),
            priority), priority);
    }

    /// <summary>
    /// Scans a map on the worker thread and schedules its textures, models and
    /// materials. The actual GL work is still performed by PumpGpuUploads.
    /// </summary>
    public void QueueMapPreload(string mapPath, AssetPriority priority = AssetPriority.High)
    {
        QueueBackground(new MapPreloadJob(mapPath, priority), priority);
    }

    public Renderer.Shader GetShader(string vertPath, string fragPath)
    {
        string key = ShaderKey(vertPath, fragPath);
        if (_shaders.TryGetValue(key, out var shader))
            return shader;
        shader = Renderer.Shader.FromFile(_gl, vertPath, fragPath);
        _shaders[key] = shader;
        return shader;
    }

    /// <summary>
    /// Reloads every file-backed shader currently known by the asset manager and
    /// every generated material shader. Programs are replaced in place, so
    /// entities and cached materials do not keep references to disposed programs.
    /// </summary>
    public int ReloadAllShaders()
    {
        int reloaded = 0;

        foreach (Renderer.Shader shader in _shaders.Values.Distinct())
        {
            if (shader.Reload())
                reloaded++;
        }

        string staticVertexPath = Bible.Shader(Bible.ShaderDefaultVert);
        string skinnedVertexPath = Bible.Shader(Bible.ShaderSkinnedVert);
        string staticVertex = Renderer.Shader.PreprocessIncludes(
            File.ReadAllText(staticVertexPath), Path.GetDirectoryName(staticVertexPath)!);
        string skinnedVertex = Renderer.Shader.PreprocessIncludes(
            File.ReadAllText(skinnedVertexPath), Path.GetDirectoryName(skinnedVertexPath)!);

        string fragmentPath = Bible.Shader(Bible.ShaderDefaultFrag);
        string fragmentTemplate = Renderer.Shader.PreprocessIncludes(
            File.ReadAllText(fragmentPath), Path.GetDirectoryName(fragmentPath)!);

        foreach (CachedMaterialShaders cached in _materialShaders.Values)
        {
            string fragmentSource = MaterialGraphCompiler.BuildFragmentSource(
                fragmentTemplate, cached.GeneratedSource, fragmentPath);
            if (cached.StaticShader.ReloadSources(staticVertex, fragmentSource))
                reloaded++;
            if (cached.SkinnedShader.ReloadSources(skinnedVertex, fragmentSource))
                reloaded++;
        }

        return reloaded;
    }

    public MaterialRuntime GetMaterial(string path)
    {
        string fullPath = MaterialRuntime.ResolveAssetPath(path);
        if (_materials.TryGetValue(fullPath, out MaterialRuntime? material))
            return material;

        material = MaterialRuntime.Load(this, fullPath);
        _materials[fullPath] = material;
        Logger.Asset($"Material loaded: {fullPath}");
        return material;
    }

    public MaterialRuntime? TryGetMaterial(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        string fullPath = MaterialRuntime.ResolveAssetPath(path);
        if (!File.Exists(fullPath))
        {
            Logger.Warn($"Material file not found: {fullPath}");
            return null;
        }

        try
        {
            return GetMaterial(fullPath);
        }
        catch (Exception ex)
        {
            Logger.Error($"Failed to load material '{path}': {ex.Message}");
            return null;
        }
    }

    public MaterialRuntime ReloadMaterial(string path)
    {
        string fullPath = MaterialRuntime.ResolveAssetPath(path);
        MaterialRuntime replacement = MaterialRuntime.Load(this, fullPath);
        if (_materials.TryGetValue(fullPath, out MaterialRuntime? previous))
            previous.Dispose();
        _materials[fullPath] = replacement;
        Logger.Asset($"Material reloaded: {fullPath}");
        return replacement;
    }

    public MaterialRuntime GetLegacyMaterial(string texturePath)
    {
        string normalized = MaterialAsset.NormalizeAssetPath(texturePath);
        if (_legacyMaterials.TryGetValue(normalized, out MaterialRuntime? material))
            return material;

        Renderer.Shader staticShader = GetShader(Bible.Shader(Bible.ShaderDefaultVert), Bible.Shader(Bible.ShaderDefaultFrag));
        Renderer.Shader skinnedShader = GetShader(Bible.Shader(Bible.ShaderSkinnedVert), Bible.Shader(Bible.ShaderDefaultFrag));
        material = MaterialRuntime.CreateLegacy(this, normalized, staticShader, skinnedShader);
        _legacyMaterials[normalized] = material;
        return material;
    }

    internal (Renderer.Shader StaticShader, Renderer.Shader SkinnedShader) AcquireMaterialShaders(
        MaterialGraphCompilation compilation)
    {
        if (_materialShaders.TryGetValue(compilation.GraphHash, out CachedMaterialShaders? cached))
        {
            cached.References++;
            return (cached.StaticShader, cached.SkinnedShader);
        }

        string staticVertexPath = Bible.Shader(Bible.ShaderDefaultVert);
        string skinnedVertexPath = Bible.Shader(Bible.ShaderSkinnedVert);
        string staticVertex = Renderer.Shader.PreprocessIncludes(
            File.ReadAllText(staticVertexPath), Path.GetDirectoryName(staticVertexPath)!);
        string skinnedVertex = Renderer.Shader.PreprocessIncludes(
            File.ReadAllText(skinnedVertexPath), Path.GetDirectoryName(skinnedVertexPath)!);

        var staticShader = new Renderer.Shader(_gl, staticVertex, compilation.FragmentSource);
        var skinnedShader = new Renderer.Shader(_gl, skinnedVertex, compilation.FragmentSource);
        if (!staticShader.IsValid || !skinnedShader.IsValid)
        {
            staticShader.Dispose();
            skinnedShader.Dispose();
            throw new InvalidDataException("The material graph generated an invalid shader. Check the shader compiler log.");
        }

        staticShader.BindUniformBlock("LightingBlock", Renderer.LightingBuffer.BindingPoint);
        skinnedShader.BindUniformBlock("LightingBlock", Renderer.LightingBuffer.BindingPoint);
        cached = new CachedMaterialShaders
        {
            StaticShader = staticShader,
            SkinnedShader = skinnedShader,
            GeneratedSource = compilation.GeneratedSource,
            References = 1
        };
        _materialShaders[compilation.GraphHash] = cached;
        return (staticShader, skinnedShader);
    }

    internal void ReleaseMaterialShaders(string graphHash)
    {
        if (!_materialShaders.TryGetValue(graphHash, out CachedMaterialShaders? cached))
            return;
        cached.References--;
        if (cached.References > 0)
            return;

        cached.StaticShader.Dispose();
        cached.SkinnedShader.Dispose();
        _materialShaders.Remove(graphHash);
    }

    public Renderer.Mesh? GetMesh(string key)
    {
        if (_meshes.TryGetValue(key, out var mesh))
            return mesh;

        mesh = key.ToLowerInvariant() switch
        {
            "cube" => Renderer.Mesh.CreateCube(_gl),
            "ground" => Renderer.Mesh.CreateGround(_gl, 1.0f, 1.0f),
            // Primitive sphere/capsule objects are stored as normalized
            // meshes. Their authored dimensions are applied by the entity's
            // transform, while this cache keeps one GPU mesh per primitive.
            "sphere" => CreateGeneratedMesh(MeshGenerator.GenerateSphere(0.5f, 24, 16)),
            "capsule" => CreateGeneratedMesh(MeshGenerator.GenerateCapsule(0.5f, 1.0f, 16)),
            _ => null
        };

        if (mesh != null)
            _meshes[key] = mesh;
        return mesh;
    }

    private Renderer.Mesh CreateGeneratedMesh(MeshData data) =>
        new(_gl, data.Vertices, data.Indices, data.LineIndices, data.Parts);

    public Renderer.LoadedModel? GetModel(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        int hashIdx = path.IndexOf('#');
        string cleanPath = ResolveAssetPath(hashIdx >= 0 ? path[..hashIdx] : path);
        string key = hashIdx >= 0 ? cleanPath + path[hashIdx..] : cleanPath;

        if (_models.TryGetValue(key, out var model))
            return model;
        if (_missingModels.Contains(key))
            return null;

        if (hashIdx != -1)
        {
            if (!_loadedCleanPaths.Contains(cleanPath))
            {
                try
                {
                    var submeshes = Renderer.ModelLoader.LoadAllSubmeshes(_gl, cleanPath);
                    for (int i = 0; i < submeshes.Length; i++)
                    {
                        if (submeshes[i] != null)
                            _models[$"{cleanPath}#{i}"] = submeshes[i]!;
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error($"Failed to load model submeshes '{cleanPath}': {ex.Message}");
                }
                finally
                {
                    _loadedCleanPaths.Add(cleanPath);
                }
            }

            if (_models.TryGetValue(key, out var found))
                return found;

            _missingModels.Add(key);
            return null;
        }

        try
        {
            var loaded = Renderer.ModelLoader.Load(_gl, cleanPath);
            if (loaded == null)
            {
                _missingModels.Add(key);
                return null;
            }
            _models[key] = loaded;
            return loaded;
        }
        catch (Exception ex)
        {
            _missingModels.Add(key);
            Logger.Error($"Failed to load model '{cleanPath}': {ex.Message}");
            return null;
        }
    }

    public Animation.SkinnedModel? GetSkinnedModel(string path)
    {
        string resolvedPath = ResolveAssetPath(path);
        if (_skinnedModels.TryGetValue(resolvedPath, out var cached))
            return cached;

        var loaded = Renderer.SkinnedModelLoader.Load(_gl, resolvedPath,
            texPath => GetTexture(texPath, Renderer.TextureColorSpace.Srgb));
        if (loaded == null)
            return null;

        _skinnedModels[resolvedPath] = loaded;
        lock (_skinnedPreloadGate)
            _pendingSkinnedPreloads.Remove(resolvedPath);
        return loaded;
    }

    public Renderer.Texture ReloadTexture(
        string path,
        Renderer.TextureColorSpace colorSpace = Renderer.TextureColorSpace.Linear)
    {
        string filePath = ResolveAssetPath(path);
        string prefix = filePath.Replace('\\', '/') + "|";
        foreach (string key in _textures.Keys
                     .Where(candidate => candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                     .ToArray())
        {
            if (_textures.Remove(key, out Renderer.Texture? texture))
                texture.Dispose();
        }
        return GetTexture(filePath, colorSpace);
    }

    /// <summary>
    /// Removes one exact texture variant from the in-memory cache. This is
    /// primarily used by editor previews after an asset is sent to the
    /// recycle bin; full-resolution runtime textures are left untouched by
    /// callers unless they explicitly request that variant.
    /// </summary>
    public bool RemoveTextureCacheEntry(
        string path,
        Renderer.TextureColorSpace colorSpace,
        int maxDimension = 0)
    {
        string filePath = ResolveAssetPath(path);
        string key = $"{filePath.Replace('\\', '/')}|{colorSpace}|{System.Math.Max(0, maxDimension)}";
        lock (_textureGate)
        {
            if (!_textures.Remove(key, out Renderer.Texture? texture))
                return false;

            texture.Dispose();
            return true;
        }
    }

    public Renderer.LoadedModel? ReloadModel(string path)
    {
        string cleanPath = ResolveAssetPath(path);
        foreach (string key in _models.Keys
                     .Where(candidate => candidate.Equals(cleanPath, StringComparison.OrdinalIgnoreCase) ||
                                         candidate.StartsWith(cleanPath + "#", StringComparison.OrdinalIgnoreCase))
                     .ToArray())
        {
            _models.Remove(key);
        }
        _missingModels.RemoveWhere(candidate => candidate.Equals(cleanPath, StringComparison.OrdinalIgnoreCase) ||
                                                candidate.StartsWith(cleanPath + "#", StringComparison.OrdinalIgnoreCase));
        _loadedCleanPaths.Remove(cleanPath);
        return GetModel(cleanPath);
    }

    /// <summary>
    /// Returns a skinned model only when it is already resident. Unlike
    /// GetSkinnedModel, this method never imports a file or touches OpenGL.
    /// </summary>
    public bool TryGetLoadedSkinnedModel(string path, out Animation.SkinnedModel? model)
    {
        string resolvedPath = ResolveAssetPath(path);
        return _skinnedModels.TryGetValue(resolvedPath, out model);
    }

    /// <summary>
    /// Checks the material cache without loading, compiling or resolving any
    /// texture dependencies.
    /// </summary>
    public bool TryGetLoadedMaterial(string? path, out MaterialRuntime? material)
    {
        material = null;
        if (string.IsNullOrWhiteSpace(path))
            return false;

        string fullPath = MaterialRuntime.ResolveAssetPath(path);
        return _materials.TryGetValue(fullPath, out material);
    }

    public void Clear()
    {
        StopStreaming();

        foreach (var material in _materials.Values) material.Dispose();
        foreach (var material in _legacyMaterials.Values) material.Dispose();
        _materials.Clear();
        _legacyMaterials.Clear();
        foreach (CachedMaterialShaders cached in _materialShaders.Values)
        {
            cached.StaticShader.Dispose();
            cached.SkinnedShader.Dispose();
        }
        _materialShaders.Clear();

        foreach (var t in _textures.Values) t.Dispose();
        foreach (var s in _shaders.Values) s.Dispose();
        foreach (var m in _meshes.Values) m.Dispose();
        foreach (var m in _models.Values)
        {
            if (m != null && m.Mesh != null)
                m.Mesh.Dispose();
        }
        foreach (var sm in _skinnedModels.Values) sm.Dispose();
        _textures.Clear();
        _shaders.Clear();
        _meshes.Clear();
        _models.Clear();
        _missingModels.Clear();
        _skinnedModels.Clear();
        _loadedCleanPaths.Clear();
        lock (_skinnedPreloadGate)
            _pendingSkinnedPreloads.Clear();
    }

    private void LoadSkinnedModelFromPreload(string path)
    {
        try
        {
            GetSkinnedModel(path);
        }
        finally
        {
            lock (_skinnedPreloadGate)
                _pendingSkinnedPreloads.Remove(ResolveAssetPath(path));
        }
    }

    private void QueueBackground(IBackgroundAssetJob job, AssetPriority priority)
    {
        lock (_streamingGate)
        {
            if (_streamingStopped)
                return;
            _backgroundQueue.Enqueue(job, (int)priority);
        }
        _streamingWake.Set();
    }

    private void EnqueueGpuUpload(AssetPriority priority, Action upload)
    {
        lock (_streamingGate)
        {
            if (_streamingStopped)
                return;
            _gpuUploadQueue.Enqueue(upload, (int)priority);
        }
    }

    private Renderer.Shader LoadShaderFromSources(
        string vertexPath,
        string fragmentPath,
        string vertexSource,
        string fragmentSource)
    {
        string key = ShaderKey(vertexPath, fragmentPath);
        if (_shaders.TryGetValue(key, out Renderer.Shader? existing))
            return existing;

        Renderer.Shader shader = Renderer.Shader.FromSources(
            _gl, vertexPath, fragmentPath, vertexSource, fragmentSource);
        _shaders[key] = shader;
        return shader;
    }

    private void StreamingWorkerLoop()
    {
        while (!_streamingCancellation.IsCancellationRequested)
        {
            IBackgroundAssetJob? job = null;
            lock (_streamingGate)
            {
                if (_backgroundQueue.Count > 0)
                    job = _backgroundQueue.Dequeue();
            }

            if (job == null)
            {
                _streamingWake.WaitOne(25);
                continue;
            }

            try
            {
                job.Execute(this);
            }
            catch (Exception ex)
            {
                Logger.Error($"Asset background load failed: {ex.Message}");
            }
        }
    }

    private void CompleteTextureWaiters(string key, Renderer.Texture texture)
    {
        List<TaskCompletionSource<Renderer.Texture>>? waiters = null;
        lock (_textureGate)
        {
            if (_textureWaiters.TryGetValue(key, out waiters))
                _textureWaiters.Remove(key);
        }

        if (waiters == null)
            return;
        foreach (TaskCompletionSource<Renderer.Texture> waiter in waiters)
            waiter.TrySetResult(texture);
    }

    private void StopStreaming()
    {
        lock (_streamingGate)
            _streamingStopped = true;
        _streamingCancellation.Cancel();
        _streamingWake.Set();
        try { _streamingWorker.Wait(1000); }
        catch (AggregateException) { }

        lock (_streamingGate)
        {
            _backgroundQueue.Clear();
            _gpuUploadQueue.Clear();
        }

        lock (_textureGate)
        {
            foreach (List<TaskCompletionSource<Renderer.Texture>> waiters in _textureWaiters.Values)
                foreach (TaskCompletionSource<Renderer.Texture> waiter in waiters)
                    waiter.TrySetCanceled();
            _textureWaiters.Clear();
        }

        _streamingWake.Dispose();
        _streamingCancellation.Dispose();
    }

    private static void CollectAssetReferences(JsonElement element, List<string> references)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String)
                {
                    string? value = property.Value.GetString();
                    if (!string.IsNullOrWhiteSpace(value) && IsPreloadableAsset(value))
                        references.Add(value);
                }
                else
                {
                    CollectAssetReferences(property.Value, references);
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement child in element.EnumerateArray())
                CollectAssetReferences(child, references);
        }
    }

    private static bool IsPreloadableAsset(string path)
    {
        string extension = Path.GetExtension(path).ToLowerInvariant();
        return IsTextureExtension(extension) || extension is ".obj" or ".fbx" or ".gltf" or ".glb" or ".fmat";
    }

    private static bool IsTextureExtension(string extension)
        => extension is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".tga" or ".dds";

    private static string ResolveAssetPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;

        string normalized = path.Trim().Replace('\\', '/');
        if (Path.IsPathRooted(normalized))
            return Path.GetFullPath(normalized);

        if (normalized.StartsWith("res/", StringComparison.OrdinalIgnoreCase))
            normalized = normalized[4..];

        return Path.GetFullPath(Path.Combine(
            ResPath.Path,
            normalized.Replace('/', Path.DirectorySeparatorChar)));
    }

    private static string ResolveAssetReferencePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;

        int hashIdx = path.IndexOf('#');
        if (hashIdx < 0)
            return ResolveAssetPath(path);

        string cleanPath = ResolveAssetPath(path[..hashIdx]);
        return cleanPath + path[hashIdx..];
    }

    private static string GetAssetFilePath(string resolvedReference)
    {
        int hashIdx = resolvedReference.IndexOf('#');
        return hashIdx >= 0 ? resolvedReference[..hashIdx] : resolvedReference;
    }

    private static string ResolveMapPath(string path)
    {
        string directPath = ResolveAssetPath(path);
        if (File.Exists(directPath))
            return directPath;
        return Path.GetFullPath(Path.Combine(ResPath.Path, "Maps", path));
    }

    private static string ShaderKey(string v, string f) =>
        $"{Path.GetFullPath(v).Replace('\\', '/')}|{Path.GetFullPath(f).Replace('\\', '/')}";
}
