using System.Numerics;
using Silk.NET.OpenGL;
using Fuse.Core;
using Fuse.Renderer.Materials;

namespace Fuse.AssetManagement;

public class AssetManager
{
    private sealed class CachedMaterialShaders
    {
        public required Renderer.Shader StaticShader { get; init; }
        public required Renderer.Shader SkinnedShader { get; init; }
        public int References { get; set; }
    }

    private readonly GL _gl;
    private readonly Dictionary<string, Renderer.Texture> _textures = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Renderer.Shader> _shaders = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Renderer.Mesh> _meshes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Renderer.LoadedModel> _models = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _loadedCleanPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Animation.SkinnedModel> _skinnedModels = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, MaterialRuntime> _materials = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, MaterialRuntime> _legacyMaterials = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CachedMaterialShaders> _materialShaders = new(StringComparer.Ordinal);

    public AssetManager(GL gl)
    {
        _gl = gl;
    }

    public GL Gl => _gl;

    public Renderer.Texture GetTexture(string path)
    {
        string key = Path.GetFullPath(path).Replace('\\', '/');
        if (_textures.TryGetValue(key, out var tex))
            return tex;
        tex = new Renderer.Texture(_gl, key);
        _textures[key] = tex;
        return tex;
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

        mesh = key switch
        {
            "cube" => Renderer.Mesh.CreateCube(_gl),
            "ground" => Renderer.Mesh.CreateGround(_gl, 1.0f, 1.0f),
            _ => null
        };

        if (mesh != null)
            _meshes[key] = mesh;
        return mesh;
    }

    public Renderer.LoadedModel? GetModel(string path)
    {
        if (_models.TryGetValue(path, out var model))
            return model;

        int hashIdx = path.IndexOf('#');
        if (hashIdx != -1)
        {
            string cleanPath = path.Substring(0, hashIdx);

            if (_loadedCleanPaths.Contains(cleanPath))
            {
                return null;
            }

            _loadedCleanPaths.Add(cleanPath);

            var submeshes = Renderer.ModelLoader.LoadAllSubmeshes(_gl, cleanPath);
            for (int i = 0; i < submeshes.Length; i++)
            {
                if (submeshes[i] != null)
                {
                    _models[$"{cleanPath}#{i}"] = submeshes[i]!;
                }
            }

            if (_models.TryGetValue(path, out var found))
                return found;

            return null;
        }

        var loaded = Renderer.ModelLoader.Load(_gl, path);
        _models[path] = loaded!;
        return loaded;
    }

    public Animation.SkinnedModel? GetSkinnedModel(string path)
    {
        if (_skinnedModels.TryGetValue(path, out var cached))
            return cached;

        var loaded = Renderer.SkinnedModelLoader.Load(_gl, path, texPath => GetTexture(texPath));
        if (loaded == null)
            return null;

        _skinnedModels[path] = loaded;
        return loaded;
    }

    public void Clear()
    {
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
        _skinnedModels.Clear();
        _loadedCleanPaths.Clear();
    }

    private static string ShaderKey(string v, string f) =>
        $"{Path.GetFullPath(v).Replace('\\', '/')}|{Path.GetFullPath(f).Replace('\\', '/')}";
}
