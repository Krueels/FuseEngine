using System;
using System.IO;
using System.Collections.Generic;
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
    private readonly GL _gl;
    private readonly AssetManager _assets;
    private Shader _shader = null!;
    private Shader _gridShader = null!;
    private Shader _shadowShader = null!;
    private Shader _pointShadowShader = null!;
    private uint _defaultTex;
    private ImageBasedLighting? _imageBasedLighting;
    private readonly Dictionary<string, uint> _texCache = [];
    private readonly Dictionary<string, Mesh?> _meshCache = [];
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
    public uint DefaultTexture => _defaultTex;
    public AssetManager AssetManager => _assets;
    public ImageBasedLighting? ImageBasedLighting => _imageBasedLighting;

    public MaterialRuntime? GetOrCreateMaterial(string? materialRelPath) =>
        _assets.TryGetMaterial(materialRelPath);

    public MaterialRuntime ReloadMaterial(string materialRelPath) =>
        _assets.ReloadMaterial(materialRelPath);

    public IReadOnlyList<string> EnumerateMaterials()
    {
        string materialDirectory = Path.Combine(_fuseResPath, "Materials");
        if (!Directory.Exists(materialDirectory))
            return [];
        return Directory.EnumerateFiles(materialDirectory, "*.fmat", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(_fuseResPath, path).Replace('\\', '/'))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public IReadOnlyList<string> EnumerateTextures()
    {
        string textureDirectory = Path.Combine(_fuseResPath, "Textures");
        if (!Directory.Exists(textureDirectory))
            return [];
        string[] extensions = [".png", ".jpg", ".jpeg", ".bmp", ".tga"];
        return Directory.EnumerateFiles(textureDirectory, "*.*", SearchOption.AllDirectories)
            .Where(path => extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            .Select(path => Path.GetRelativePath(_fuseResPath, path).Replace('\\', '/'))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
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

        _shader.BindUniformBlock("LightingBlock", LightingBuffer.BindingPoint);

        string skyboxPath = Path.Combine(_fuseResPath, "Textures", "skybox_1.png");
        if (File.Exists(skyboxPath))
        {
            try
            {
                _imageBasedLighting = new ImageBasedLighting(_gl,
                    _assets.GetTexture(skyboxPath, TextureColorSpace.Srgb));
            }
            catch (Exception ex)
            {
                Logger.Warn($"Blowtorch IBL disabled: {ex.Message}");
            }
        }

        string crateTexPath = Path.Combine(_fuseResPath, "Textures", "dev_measurecrate01.bmp");
        if (File.Exists(crateTexPath))
        {
            var crateTex = new Texture(_gl, crateTexPath);
            _defaultTex = crateTex.ID;
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
        }
    }

    public void ClearBrushMeshes()
    {
        var keysToRemove = new List<string>();
        foreach (var pair in _meshCache)
        {
            if (pair.Key.StartsWith("brush_"))
            {
                pair.Value?.Dispose();
                keysToRemove.Add(pair.Key);
            }
        }
        foreach (var key in keysToRemove)
        {
            _meshCache.Remove(key);
        }
    }

    public void Dispose()
    {
        foreach (var texId in _texCache.Values)
        {
            if (texId != 0) _gl.DeleteTexture(texId);
        }
        if (_defaultTex != 0) _gl.DeleteTexture(_defaultTex);
        _imageBasedLighting?.Dispose();
        _assets.Clear();
    }
}
