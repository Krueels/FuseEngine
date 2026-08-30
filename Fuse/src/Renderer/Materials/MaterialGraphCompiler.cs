using System.Globalization;
using System.Numerics;
using System.Text;

namespace Fuse.Renderer.Materials;

public sealed record MaterialTextureSlot(string NodeId, string UniformName, string AssetPath, int Slot);
public sealed record MaterialUniformSlot(string NodeId, string UniformName, MaterialValueType Type);

public sealed class MaterialGraphCompilation
{
    public required string FragmentSource { get; init; }
    public required IReadOnlyList<MaterialTextureSlot> Textures { get; init; }
    public required IReadOnlyList<MaterialUniformSlot> Uniforms { get; init; }
    public required string GraphHash { get; init; }
}

public static class MaterialGraphCompiler
{
    private readonly record struct Expression(string Code, MaterialValueType Type);

    public static MaterialGraphCompilation Compile(MaterialAsset asset, string fragmentTemplatePath)
    {
        string fragmentSource = Shader.PreprocessIncludes(
            File.ReadAllText(fragmentTemplatePath),
            Path.GetDirectoryName(fragmentTemplatePath)!);

        MaterialGraphNode output = asset.Graph.FindOutput()
            ?? throw new InvalidDataException($"Material '{asset.Name}' has no PBROutput node.");

        var state = new CompilerState(asset.Graph);
        string generated = state.Generate(output);
        const string marker = "/*__FUSE_MATERIAL_GRAPH__*/";
        if (!fragmentSource.Contains(marker, StringComparison.Ordinal))
            throw new InvalidDataException($"Shader template '{fragmentTemplatePath}' does not contain {marker}.");

        fragmentSource = fragmentSource.Replace(marker, generated, StringComparison.Ordinal);
        int versionEnd = fragmentSource.IndexOf('\n');
        fragmentSource = versionEnd >= 0
            ? fragmentSource.Insert(versionEnd + 1, "#define FUSE_CUSTOM_MATERIAL 1\n")
            : "#define FUSE_CUSTOM_MATERIAL 1\n" + fragmentSource;

        string graphHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            Encoding.UTF8.GetBytes(generated)));

        return new MaterialGraphCompilation
        {
            FragmentSource = fragmentSource,
            Textures = state.Textures,
            Uniforms = state.Uniforms,
            GraphHash = graphHash
        };
    }

    private sealed class CompilerState
    {
        private readonly MaterialGraph _graph;
        private readonly Dictionary<(string Node, string Socket), Expression> _cache = [];
        private readonly HashSet<string> _visiting = [];
        private readonly Dictionary<string, MaterialTextureSlot> _textureByNode = [];
        private readonly Dictionary<string, MaterialUniformSlot> _uniformByNode = [];

        public List<MaterialTextureSlot> Textures { get; } = [];
        public List<MaterialUniformSlot> Uniforms { get; } = [];

        public CompilerState(MaterialGraph graph)
        {
            _graph = graph;
        }

        public string Generate(MaterialGraphNode output)
        {
            Expression baseColor = ResolveInput(output, "BaseColor", new Expression("uMaterialBaseColor", MaterialValueType.Vector3));
            Expression tangentNormal = ResolveInput(output, "Normal", new Expression("vec3(0.0, 0.0, 1.0)", MaterialValueType.Vector3));
            Expression roughness = ResolveInput(output, "Roughness", new Expression("uMaterialRoughness", MaterialValueType.Float));
            Expression metallic = ResolveInput(output, "Metallic", new Expression("uMaterialMetallic", MaterialValueType.Float));
            Expression emission = ResolveInput(output, "Emission", new Expression("uMaterialEmission", MaterialValueType.Vector3));
            Expression alpha = ResolveInput(output, "Alpha", new Expression("uMaterialAlpha", MaterialValueType.Float));
            bool hasNormalMap = _graph.Links.Any(link => link.ToNode == output.Id && link.ToSocket == "Normal");

            var source = new StringBuilder();
            source.AppendLine("uniform vec3 uMaterialBaseColor;");
            source.AppendLine("uniform float uMaterialRoughness;");
            source.AppendLine("uniform float uMaterialMetallic;");
            source.AppendLine("uniform vec3 uMaterialEmission;");
            source.AppendLine("uniform float uMaterialAlpha;");

            foreach (MaterialTextureSlot texture in Textures)
                source.AppendLine($"uniform sampler2D {texture.UniformName};");
            foreach (MaterialUniformSlot uniform in Uniforms)
            {
                string glslType = uniform.Type switch
                {
                    MaterialValueType.Float => "float",
                    MaterialValueType.Vector2 => "vec2",
                    _ => "vec3"
                };
                source.AppendLine($"uniform {glslType} {uniform.UniformName};");
            }

            source.AppendLine("MaterialSurface EvaluateMaterial(vec2 materialUv)");
            source.AppendLine("{");
            source.AppendLine("    MaterialSurface surface;");
            source.AppendLine($"    surface.baseColor = {AsType(baseColor, MaterialValueType.Vector3).Code};");
            source.AppendLine($"    surface.tangentNormal = normalize({AsType(tangentNormal, MaterialValueType.Vector3).Code});");
            source.AppendLine($"    surface.roughness = clamp({AsType(roughness, MaterialValueType.Float).Code}, 0.02, 1.0);");
            source.AppendLine($"    surface.metallic = clamp({AsType(metallic, MaterialValueType.Float).Code}, 0.0, 1.0);");
            source.AppendLine($"    surface.emission = {AsType(emission, MaterialValueType.Vector3).Code};");
            source.AppendLine($"    surface.alpha = clamp({AsType(alpha, MaterialValueType.Float).Code}, 0.0, 1.0);");
            source.AppendLine($"    surface.hasNormalMap = {(hasNormalMap ? "1.0" : "0.0")};");
            source.AppendLine("    surface.legacyLighting = 0.0;");
            source.AppendLine("    return surface;");
            source.AppendLine("}");
            return source.ToString();
        }

        private Expression ResolveInput(MaterialGraphNode node, string socket, Expression fallback)
        {
            MaterialGraphLink? link = _graph.Links.LastOrDefault(candidate =>
                candidate.ToNode == node.Id && candidate.ToSocket == socket);
            if (link == null)
                return fallback;

            MaterialGraphNode? source = _graph.FindNode(link.FromNode);
            return source == null ? fallback : ResolveOutput(source, link.FromSocket);
        }

        private Expression ResolveOutput(MaterialGraphNode node, string socket)
        {
            var key = (node.Id, socket);
            if (_cache.TryGetValue(key, out Expression cached))
                return cached;
            if (!_visiting.Add(node.Id))
                throw new InvalidDataException($"Material graph contains a cycle involving node '{node.Name}'.");

            Expression result = node.Type switch
            {
                "Texture2D" => ResolveTexture(node, socket),
                "Color" => ResolveUniform(node, MaterialValueType.Vector3, "uMatColor"),
                "Float" => ResolveUniform(node, MaterialValueType.Float, "uMatFloat"),
                "Vector3" => ResolveUniform(node, MaterialValueType.Vector3, "uMatVector"),
                "UV" => new Expression("materialUv", MaterialValueType.Vector2),
                "Multiply" => ResolveBinary(node, "*"),
                "Add" => ResolveBinary(node, "+"),
                "Lerp" => ResolveLerp(node),
                "NormalMap" => ResolveNormalMap(node),
                _ => throw new InvalidDataException($"Unsupported material node type '{node.Type}'.")
            };

            _visiting.Remove(node.Id);
            _cache[key] = result;
            return result;
        }

        private Expression ResolveTexture(MaterialGraphNode node, string socket)
        {
            if (!_textureByNode.TryGetValue(node.Id, out MaterialTextureSlot? texture))
            {
                int slot = Textures.Count;
                if (slot >= MaterialRuntime.MaxTextureSlots)
                    throw new InvalidDataException($"Material graph exceeds the limit of {MaterialRuntime.MaxTextureSlots} textures.");
                texture = new MaterialTextureSlot(
                    node.Id,
                    $"uMaterialTexture{slot}",
                    MaterialAsset.GetString(node.Properties, "path", ""),
                    slot);
                Textures.Add(texture);
                _textureByNode[node.Id] = texture;
            }

            Expression uv = ResolveInput(node, "UV", new Expression("materialUv", MaterialValueType.Vector2));
            string sample = $"texture({texture.UniformName}, {AsType(uv, MaterialValueType.Vector2).Code})";
            return socket == "Alpha"
                ? new Expression(sample + ".a", MaterialValueType.Float)
                : new Expression(sample + ".rgb", MaterialValueType.Vector3);
        }

        private Expression ResolveUniform(MaterialGraphNode node, MaterialValueType type, string prefix)
        {
            if (!_uniformByNode.TryGetValue(node.Id, out MaterialUniformSlot? uniform))
            {
                uniform = new MaterialUniformSlot(node.Id, $"{prefix}{Uniforms.Count}", type);
                Uniforms.Add(uniform);
                _uniformByNode[node.Id] = uniform;
            }
            return new Expression(uniform.UniformName, type);
        }

        private Expression ResolveBinary(MaterialGraphNode node, string operation)
        {
            Expression a = ResolveInput(node, "A", new Expression("vec3(0.0)", MaterialValueType.Vector3));
            Expression b = ResolveInput(node, "B", operation == "*"
                ? new Expression("vec3(1.0)", MaterialValueType.Vector3)
                : new Expression("vec3(0.0)", MaterialValueType.Vector3));
            MaterialValueType type = Widest(a.Type, b.Type);
            return new Expression($"({AsType(a, type).Code} {operation} {AsType(b, type).Code})", type);
        }

        private Expression ResolveLerp(MaterialGraphNode node)
        {
            Expression a = ResolveInput(node, "A", new Expression("vec3(0.0)", MaterialValueType.Vector3));
            Expression b = ResolveInput(node, "B", new Expression("vec3(1.0)", MaterialValueType.Vector3));
            MaterialValueType type = Widest(a.Type, b.Type);
            Expression factor = ResolveInput(node, "Factor", new Expression("0.5", MaterialValueType.Float));
            return new Expression($"mix({AsType(a, type).Code}, {AsType(b, type).Code}, {AsType(factor, MaterialValueType.Float).Code})", type);
        }

        private Expression ResolveNormalMap(MaterialGraphNode node)
        {
            Expression color = ResolveInput(node, "Color", new Expression("vec3(0.5, 0.5, 1.0)", MaterialValueType.Vector3));
            float defaultStrength = MaterialAsset.GetFloat(node.Properties, "strength", 1.0f);
            Expression strength = ResolveInput(node, "Strength", new Expression(ToFloat(defaultStrength), MaterialValueType.Float));
            string normal = $"normalize((({AsType(color, MaterialValueType.Vector3).Code}) * 2.0 - 1.0) * vec3({AsType(strength, MaterialValueType.Float).Code}, {AsType(strength, MaterialValueType.Float).Code}, 1.0))";
            return new Expression(normal, MaterialValueType.Vector3);
        }

        private static MaterialValueType Widest(MaterialValueType a, MaterialValueType b) =>
            (MaterialValueType)System.Math.Max((int)a, (int)b);

        private static Expression AsType(Expression expression, MaterialValueType target)
        {
            if (expression.Type == target)
                return expression;
            if (target == MaterialValueType.Vector3 && expression.Type == MaterialValueType.Float)
                return new Expression($"vec3({expression.Code})", target);
            if (target == MaterialValueType.Vector2 && expression.Type == MaterialValueType.Float)
                return new Expression($"vec2({expression.Code})", target);
            if (target == MaterialValueType.Float && expression.Type == MaterialValueType.Vector3)
                return new Expression($"dot({expression.Code}, vec3(0.2126, 0.7152, 0.0722))", target);
            if (target == MaterialValueType.Float && expression.Type == MaterialValueType.Vector2)
                return new Expression($"({expression.Code}).x", target);
            if (target == MaterialValueType.Vector3 && expression.Type == MaterialValueType.Vector2)
                return new Expression($"vec3({expression.Code}, 0.0)", target);
            if (target == MaterialValueType.Vector2 && expression.Type == MaterialValueType.Vector3)
                return new Expression($"({expression.Code}).xy", target);
            return expression;
        }

        private static string ToFloat(float value) => value.ToString("0.0######", CultureInfo.InvariantCulture);
    }
}
