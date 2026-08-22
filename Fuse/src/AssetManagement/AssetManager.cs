using System.Numerics;
using Silk.NET.OpenGL;
using Fuse.Core;

namespace Fuse.AssetManagement;

public class AssetManager
{
    private readonly GL _gl;
    private readonly Dictionary<string, Renderer.Texture> _textures = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Renderer.Shader> _shaders = [];
    private readonly Dictionary<string, Renderer.Mesh> _meshes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Renderer.LoadedModel> _models = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _loadedCleanPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Animation.SkinnedModel> _skinnedModels = new(StringComparer.OrdinalIgnoreCase);

    public AssetManager(GL gl)
    {
        _gl = gl;
    }

    public GL Gl => _gl;

    public Renderer.Texture GetTexture(string path)
    {
        if (_textures.TryGetValue(path, out var tex))
            return tex;
        tex = new Renderer.Texture(_gl, path);
        _textures[path] = tex;
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

    private static string ShaderKey(string v, string f) => $"{v}|{f}";
}
