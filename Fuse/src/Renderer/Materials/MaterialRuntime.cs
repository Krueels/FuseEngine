using System.Numerics;
using Fuse.AssetManagement;
using Fuse.Core;

namespace Fuse.Renderer.Materials;

public sealed class MaterialRuntime : IDisposable
{
    public const int FirstTextureUnit = 7;
    public const int MaxTextureSlots = 8;

    private readonly List<(MaterialTextureSlot Slot, Texture? Texture)> _textures = [];
    private readonly IReadOnlyList<MaterialUniformSlot> _uniforms;
    private readonly AssetManager? _owner;
    private readonly string _shaderCacheKey;
    private bool _disposed;

    public string SourcePath { get; }
    public MaterialAsset Asset { get; private set; }
    public Shader StaticShader { get; }
    public Shader SkinnedShader { get; }
    public bool IsLegacy { get; }

    private MaterialRuntime(
        string sourcePath,
        MaterialAsset asset,
        Shader staticShader,
        Shader skinnedShader,
        IReadOnlyList<MaterialUniformSlot> uniforms,
        bool isLegacy,
        AssetManager? owner = null,
        string shaderCacheKey = "")
    {
        SourcePath = sourcePath;
        Asset = asset;
        StaticShader = staticShader;
        SkinnedShader = skinnedShader;
        _uniforms = uniforms;
        IsLegacy = isLegacy;
        _owner = owner;
        _shaderCacheKey = shaderCacheKey;
    }

    public static MaterialRuntime Load(AssetManager assets, string path)
    {
        string fullPath = ResolveAssetPath(path);
        MaterialAsset asset = MaterialAsset.Load(fullPath);
        return CreateInMemory(assets, asset, fullPath);
    }

    /// <summary>
    /// Compiles a material that has not been written to disk yet. Used by editor
    /// previews so graph changes can be inspected before saving the .fmat file.
    /// The caller owns the returned runtime and must dispose it.
    /// </summary>
    public static MaterialRuntime CreateInMemory(AssetManager assets, MaterialAsset asset, string sourcePath)
    {
        MaterialGraphCompilation compilation = MaterialGraphCompiler.Compile(asset, Bible.Shader(Bible.ShaderDefaultFrag));

        (Shader staticShader, Shader skinnedShader) = assets.AcquireMaterialShaders(compilation);

        var material = new MaterialRuntime(
            sourcePath,
            asset,
            staticShader,
            skinnedShader,
            compilation.Uniforms,
            false,
            assets,
            compilation.GraphHash);
        try
        {
            material.ResolveTextures(assets, compilation.Textures);
            return material;
        }
        catch
        {
            material.Dispose();
            throw;
        }
    }

    public static MaterialRuntime CreateLegacy(
        AssetManager assets,
        string texturePath,
        Shader staticShader,
        Shader skinnedShader)
    {
        MaterialAsset asset = MaterialAsset.CreateDefault(
            "Legacy_" + Path.GetFileNameWithoutExtension(texturePath),
            MaterialAsset.NormalizeAssetPath(texturePath));
        var material = new MaterialRuntime(
            "legacy://" + MaterialAsset.NormalizeAssetPath(texturePath),
            asset,
            staticShader,
            skinnedShader,
            [],
            true);
        string resolved = ResolveAssetPath(texturePath);
        Texture? texture = File.Exists(resolved) ? assets.GetTexture(resolved, TextureColorSpace.Srgb) : null;
        material._textures.Add((new MaterialTextureSlot("legacy", "uTexture", texturePath, 0, TextureColorSpace.Srgb), texture));
        return material;
    }

    public void Bind(Shader shader)
    {
        MaterialGraphNode? output = Asset.Graph.FindOutput();
        Vector3 baseColor = output == null
            ? Vector3.One
            : MaterialAsset.GetVector3(output.Properties, "base_color", Vector3.One);
        Vector3 emission = output == null
            ? Vector3.Zero
            : MaterialAsset.GetVector3(output.Properties, "emission", Vector3.Zero);
        float roughness = output == null ? 0.5f : MaterialAsset.GetFloat(output.Properties, "roughness", 0.5f);
        float metallic = output == null ? 0.0f : MaterialAsset.GetFloat(output.Properties, "metallic", 0.0f);
        float alpha = output == null ? 1.0f : MaterialAsset.GetFloat(output.Properties, "alpha", 1.0f);
        float ao = output == null ? 1.0f : MaterialAsset.GetFloat(output.Properties, "ao", 1.0f);

        shader.SetVec3("uMaterialBaseColor", baseColor);
        shader.SetVec3("uMaterialEmission", emission);
        shader.SetFloat("uMaterialRoughness", roughness);
        shader.SetFloat("uMaterialMetallic", metallic);
        shader.SetFloat("uMaterialAlpha", alpha);
        shader.SetFloat("uMaterialAO", ao);
        shader.SetInt("uMaterialAlphaMode", Asset.AlphaMode switch
        {
            MaterialAlphaMode.Mask => 1,
            MaterialAlphaMode.Blend => 2,
            _ => 0
        });
        shader.SetFloat("uMaterialAlphaCutoff", Asset.AlphaCutoff);
        shader.SetBool("uMaterialReceiveShadows", Asset.ReceiveShadows);

        foreach ((MaterialTextureSlot slot, Texture? texture) in _textures)
        {
            if (IsLegacy)
            {
                shader.SetBool("uUseTexture", texture != null);
                if (texture != null)
                    texture.Bind(0);
                continue;
            }

            int textureUnit = FirstTextureUnit + slot.Slot;
            shader.SetInt(slot.UniformName, textureUnit);
            (texture ?? _textures.FirstOrDefault(pair => pair.Texture != null).Texture)?.Bind((uint)textureUnit);
        }

        foreach (MaterialUniformSlot uniform in _uniforms)
        {
            MaterialGraphNode? node = Asset.Graph.FindNode(uniform.NodeId);
            if (node == null)
                continue;
            switch (uniform.Type)
            {
                case MaterialValueType.Float:
                    shader.SetFloat(uniform.UniformName, MaterialAsset.GetFloat(node.Properties, "value", 0.0f));
                    break;
                case MaterialValueType.Vector2:
                    shader.SetVec2(uniform.UniformName, Vector2.Zero);
                    break;
                case MaterialValueType.Vector3:
                    shader.SetVec3(uniform.UniformName, MaterialAsset.GetVector3(node.Properties, "value", Vector3.Zero));
                    break;
            }
        }
    }

    public void BindShadow(Shader shader)
    {
        bool alphaMask = !IsLegacy && Asset.AlphaMode == MaterialAlphaMode.Mask;
        shader.SetBool("uShadowAlphaMask", alphaMask);
        if (!alphaMask)
            return;

        MaterialGraphNode? output = Asset.Graph.FindOutput();
        float alpha = output == null ? 1.0f : MaterialAsset.GetFloat(output.Properties, "alpha", 1.0f);
        shader.SetFloat("uShadowAlpha", alpha);
        shader.SetFloat("uShadowAlphaCutoff", Asset.AlphaCutoff);

        MaterialGraphLink? alphaLink = output == null
            ? null
            : Asset.Graph.Links.LastOrDefault(link => link.ToNode == output.Id && link.ToSocket == "Alpha");
        Texture? alphaTexture = null;
        if (alphaLink != null)
        {
            MaterialGraphNode? source = Asset.Graph.FindNode(alphaLink.FromNode);
            if (source?.Type == "Texture2D" && alphaLink.FromSocket == "Alpha")
                alphaTexture = _textures.FirstOrDefault(pair => pair.Slot.NodeId == source.Id).Texture;
        }

        shader.SetBool("uShadowUseAlphaTexture", alphaTexture != null);
        shader.SetInt("uShadowAlphaTexture", 0);
        alphaTexture?.Bind(0);
    }

    public static string ResolveAssetPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "";
        string normalized = MaterialAsset.NormalizeAssetPath(path);
        return Path.IsPathRooted(normalized)
            ? Path.GetFullPath(normalized)
            : Path.GetFullPath(Path.Combine(ResPath.Path, normalized));
    }

    private void ResolveTextures(AssetManager assets, IReadOnlyList<MaterialTextureSlot> slots)
    {
        string fallbackPath = Path.Combine(ResPath.Path, "Textures", "white.png");
        Texture? fallback = File.Exists(fallbackPath) ? assets.GetTexture(fallbackPath, TextureColorSpace.Srgb) : null;
        foreach (MaterialTextureSlot slot in slots)
        {
            string fullPath = ResolveAssetPath(slot.AssetPath);
            Texture? texture = null;
            if (!string.IsNullOrWhiteSpace(fullPath) && File.Exists(fullPath))
                texture = assets.GetTexture(fullPath, slot.ColorSpace);
            else
                Logger.Warn($"Material '{Asset.Name}': texture not found '{slot.AssetPath}'.");
            _textures.Add((slot, texture ?? fallback));
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (!IsLegacy)
            _owner?.ReleaseMaterialShaders(_shaderCacheKey);
        _textures.Clear();
    }
}
