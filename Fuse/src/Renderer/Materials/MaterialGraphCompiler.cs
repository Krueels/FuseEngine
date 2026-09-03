using System.Globalization;
using System.Numerics;
using System.Text;

namespace Fuse.Renderer.Materials;

public sealed record MaterialTextureSlot(
    string NodeId,
    string UniformName,
    string AssetPath,
    int Slot,
    TextureColorSpace ColorSpace);

public sealed record MaterialTextureArraySlot(
    string NodeId,
    string UniformName,
    IReadOnlyList<string> AssetPaths,
    int Slot,
    TextureColorSpace ColorSpace);

public sealed record MaterialUniformSlot(
    string NodeId,
    string UniformName,
    MaterialValueType Type,
    string ParameterName = "");

public sealed class MaterialGraphCompilation
{
    public required string FragmentSource { get; init; }
    public required string GeneratedSource { get; init; }
    public required IReadOnlyList<MaterialTextureSlot> Textures { get; init; }
    public required IReadOnlyList<MaterialTextureArraySlot> TextureArrays { get; init; }
    public required IReadOnlyList<MaterialUniformSlot> Uniforms { get; init; }
    public required string GraphHash { get; init; }
}

public static class MaterialGraphCompiler
{
    private readonly record struct Expression(
        string Code,
        MaterialValueType Type,
        bool IsWorldNormal = false);

    public static MaterialGraphCompilation Compile(MaterialAsset asset, string fragmentTemplatePath)
    {
        string fragmentSource = Shader.PreprocessIncludes(
            File.ReadAllText(fragmentTemplatePath),
            Path.GetDirectoryName(fragmentTemplatePath)!);

        MaterialGraphNode output = asset.Graph.FindOutput()
            ?? throw new InvalidDataException($"Material '{asset.Name}' has no PBROutput node.");

        var state = new CompilerState(asset.Graph);
        string generated = state.Generate(output);
        fragmentSource = BuildFragmentSource(fragmentSource, generated, fragmentTemplatePath);

        string graphHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            Encoding.UTF8.GetBytes(generated)));

        return new MaterialGraphCompilation
        {
            FragmentSource = fragmentSource,
            GeneratedSource = generated,
            Textures = state.Textures,
            TextureArrays = state.TextureArrays,
            Uniforms = state.Uniforms,
            GraphHash = graphHash
        };
    }

    public static string BuildFragmentSource(string fragmentTemplate, string generated, string templateName = "material shader")
    {
        const string marker = "/*__FUSE_MATERIAL_GRAPH__*/";
        if (!fragmentTemplate.Contains(marker, StringComparison.Ordinal))
            throw new InvalidDataException($"Shader template '{templateName}' does not contain {marker}.");

        string fragmentSource = fragmentTemplate.Replace(marker, generated, StringComparison.Ordinal);
        int versionEnd = fragmentSource.IndexOf('\n');
        return versionEnd >= 0
            ? fragmentSource.Insert(versionEnd + 1, "#define FUSE_CUSTOM_MATERIAL 1\n")
            : "#define FUSE_CUSTOM_MATERIAL 1\n" + fragmentSource;
    }

    private sealed class CompilerState
    {
        private readonly MaterialGraph _graph;
        private readonly Dictionary<(string Node, string Socket), Expression> _cache = [];
        private readonly HashSet<string> _visiting = [];
        private readonly Dictionary<string, MaterialTextureSlot> _textureByNode = [];
        private readonly Dictionary<(string Node, string Property), MaterialTextureArraySlot> _textureArrayByNode = [];
        private readonly Dictionary<string, MaterialUniformSlot> _uniformByNode = [];
        private int _nextTextureSlot;

        public List<MaterialTextureSlot> Textures { get; } = [];
        public List<MaterialTextureArraySlot> TextureArrays { get; } = [];
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
            Expression ao = ResolveInput(output, "AO", new Expression("uMaterialAO", MaterialValueType.Float));
            bool hasNormalMap = _graph.Links.Any(link =>
                link.ToNode == output.Id &&
                link.ToSocket.Equals("Normal", StringComparison.OrdinalIgnoreCase) &&
                !tangentNormal.IsWorldNormal);

            var source = new StringBuilder();
            source.AppendLine(GlslHelpers);
            source.AppendLine("uniform vec3 uMaterialBaseColor;");
            source.AppendLine("uniform float uMaterialRoughness;");
            source.AppendLine("uniform float uMaterialMetallic;");
            source.AppendLine("uniform vec3 uMaterialEmission;");
            source.AppendLine("uniform float uMaterialAlpha;");
            source.AppendLine("uniform float uMaterialAO;");

            foreach (MaterialTextureSlot texture in Textures)
                source.AppendLine($"uniform sampler2D {texture.UniformName};");
            foreach (MaterialTextureArraySlot textureArray in TextureArrays)
                source.AppendLine($"uniform sampler2DArray {textureArray.UniformName};");
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

            source.AppendLine("MaterialSurface EvaluateMaterial(vec2 materialUv, vec3 worldPosition, vec3 worldNormal, vec3 worldTangent, vec3 worldBitangent)");
            source.AppendLine("{");
            source.AppendLine("    MaterialSurface surface;");
            source.AppendLine($"    surface.baseColor = {AsType(baseColor, MaterialValueType.Vector3).Code};");
            source.AppendLine($"    surface.tangentNormal = normalize({AsType(tangentNormal, MaterialValueType.Vector3).Code});");
            source.AppendLine($"    surface.roughness = clamp({AsType(roughness, MaterialValueType.Float).Code}, 0.02, 1.0);");
            source.AppendLine($"    surface.metallic = clamp({AsType(metallic, MaterialValueType.Float).Code}, 0.0, 1.0);");
            source.AppendLine($"    surface.emission = {AsType(emission, MaterialValueType.Vector3).Code};");
            source.AppendLine($"    surface.alpha = clamp({AsType(alpha, MaterialValueType.Float).Code}, 0.0, 1.0);");
            source.AppendLine($"    surface.ao = clamp({AsType(ao, MaterialValueType.Float).Code}, 0.0, 1.0);");
            source.AppendLine($"    surface.hasNormalMap = {(hasNormalMap ? "1.0" : "0.0")};");
            source.AppendLine($"    surface.normalSpace = {(tangentNormal.IsWorldNormal ? "1.0" : "0.0")};");
            source.AppendLine("    surface.legacyLighting = 0.0;");
            source.AppendLine("    return surface;");
            source.AppendLine("}");
            return source.ToString();
        }

        private Expression ResolveInput(MaterialGraphNode node, string socket, Expression fallback)
        {
            MaterialGraphLink? link = _graph.Links.LastOrDefault(candidate =>
                candidate.ToNode == node.Id &&
                candidate.ToSocket.Equals(socket, StringComparison.OrdinalIgnoreCase));
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

            Expression result;
            try
            {
                result = node.Type switch
                {
                    "Texture2D" => ResolveTexture(node, socket),
                    "ScalarTexture" => ResolveScalarTexture(node),
                    "PackedMetallicRoughness" => ResolvePackedMetallicRoughness(node, socket),
                    "Texture2DArray" => ResolveTexture2DArray(node, socket),
                    "TriplanarTexture" => ResolveTriplanarTexture(node, socket),
                    "TriplanarNormal" => ResolveTriplanarNormal(node),
                    "TerrainLayer" => ResolveTerrainLayer(node, socket),
                    "TerrainLayerBlend" => ResolveTerrainLayerBlend(node, socket),
                    "Color" => ResolveUniform(node, MaterialValueType.Vector3, "uMatColor"),
                    "Float" => ResolveUniform(node, MaterialValueType.Float, "uMatFloat"),
                    "Vector2" => ResolveUniform(node, MaterialValueType.Vector2, "uMatVector2"),
                    "Vector3" => ResolveUniform(node, MaterialValueType.Vector3, "uMatVector"),
                    "UV" => new Expression("materialUv", MaterialValueType.Vector2),
                    "WorldPosition" => new Expression("worldPosition", MaterialValueType.Vector3),
                    "WorldNormal" => new Expression("normalize(worldNormal)", MaterialValueType.Vector3, true),
                    "Swizzle" => ResolveSwizzle(node, socket),
                    "Mapping" => ResolveMapping(node),
                    "Multiply" => ResolveBinary(node, "*"),
                    "Add" => ResolveBinary(node, "+"),
                    "Subtract" => ResolveBinary(node, "-"),
                    "Divide" => ResolveBinary(node, "/"),
                    "Min" => ResolveBinary(node, "min"),
                    "Max" => ResolveBinary(node, "max"),
                    "Power" => ResolveBinary(node, "pow"),
                    "Math" => ResolveMath(node),
                    "Abs" => ResolveUnary(node, "abs"),
                    "Normalize" => ResolveNormalize(node),
                    "Length" => ResolveLength(node),
                    "Dot" => ResolveDot(node),
                    "OneMinus" => ResolveUnary(node, "one_minus"),
                    "Clamp" => ResolveClamp(node),
                    "Saturate" => ResolveUnary(node, "saturate"),
                    "Smoothstep" => ResolveSmoothstep(node),
                    "Remap" => ResolveRemap(node),
                    "TerrainHeight" => new Expression("worldPosition.y", MaterialValueType.Float),
                    "TerrainSlope" => new Expression(
                        "(1.0 - clamp(dot(normalize(worldNormal), vec3(0.0, 1.0, 0.0)), 0.0, 1.0))",
                        MaterialValueType.Float),
                    "Noise2D" => ResolveNoise(node, false),
                    "FBMNoise" => ResolveNoise(node, true),
                    "DomainWarp" => ResolveDomainWarp(node),
                    "Lerp" => ResolveLerp(node),
                    "NormalMap" => ResolveNormalMap(node),
                    "NormalBlend" => ResolveNormalBlend(node),
                    "HeightBlend" => ResolveHeightBlend(node),
                    "Reroute" => ResolveInput(node, "Input", new Expression("vec3(0.0)", MaterialValueType.Vector3)),
                    _ => throw new InvalidDataException($"Unsupported material node type '{node.Type}'.")
                };
            }
            finally
            {
                _visiting.Remove(node.Id);
            }

            _cache[key] = result;
            return result;
        }

        private Expression ResolveTexture(MaterialGraphNode node, string socket)
        {
            MaterialTextureSlot texture = RegisterTexture(node,
                MaterialAsset.GetString(node.Properties, "color_space", node.Type == "Texture2D" ? "sRGB" : "linear"));
            Expression uv = ResolveInput(node, "UV", new Expression("materialUv", MaterialValueType.Vector2));
            string sample = $"texture({texture.UniformName}, {AsType(uv, MaterialValueType.Vector2).Code})";
            return socket.Equals("Alpha", StringComparison.OrdinalIgnoreCase)
                ? new Expression(sample + ".a", MaterialValueType.Float)
                : new Expression(sample + ".rgb", MaterialValueType.Vector3);
        }

        private Expression ResolveScalarTexture(MaterialGraphNode node) =>
            new(ResolveTextureSample(node) + ".r", MaterialValueType.Float);

        private Expression ResolvePackedMetallicRoughness(MaterialGraphNode node, string socket)
        {
            string sample = ResolveTextureSample(node);
            return socket.Equals("Metallic", StringComparison.OrdinalIgnoreCase)
                ? new Expression(sample + ".b", MaterialValueType.Float)
                : new Expression(sample + ".g", MaterialValueType.Float);
        }

        private string ResolveTextureSample(MaterialGraphNode node)
        {
            MaterialTextureSlot texture = RegisterTexture(node,
                MaterialAsset.GetString(node.Properties, "color_space", node.Type == "Texture2D" ? "sRGB" : "linear"));
            Expression uv = ResolveInput(node, "UV", new Expression("materialUv", MaterialValueType.Vector2));
            return $"texture({texture.UniformName}, {AsType(uv, MaterialValueType.Vector2).Code})";
        }

        private Expression ResolveTexture2DArray(MaterialGraphNode node, string socket)
        {
            List<string> paths = MaterialAsset.GetStringArray(node.Properties, "paths");
            if (paths.Count == 0)
            {
                string fallback = socket.Equals("Alpha", StringComparison.OrdinalIgnoreCase)
                    ? "1.0"
                    : "vec3(0.5)";
                return new Expression(fallback,
                    socket.Equals("Alpha", StringComparison.OrdinalIgnoreCase)
                        ? MaterialValueType.Float
                        : MaterialValueType.Vector3);
            }

            MaterialTextureArraySlot texture = RegisterTextureArray(node, "paths", paths,
                MaterialAsset.GetString(node.Properties, "color_space", "sRGB"));
            Expression uv = ResolveInput(node, "UV", new Expression("materialUv", MaterialValueType.Vector2));
            Expression layer = ResolveInput(node, "Layer", PropertyFloat(node, "layer", 0.0f));
            string sample = $"FuseSampleTextureArray({texture.UniformName}, {AsType(uv, MaterialValueType.Vector2).Code}, {AsType(layer, MaterialValueType.Float).Code}, {ToFloat(texture.AssetPaths.Count - 1)})";
            return socket.Equals("Alpha", StringComparison.OrdinalIgnoreCase)
                ? new Expression(sample + ".a", MaterialValueType.Float)
                : new Expression(sample + ".rgb", MaterialValueType.Vector3);
        }

        private Expression ResolveTriplanarTexture(MaterialGraphNode node, string socket)
        {
            string path = MaterialAsset.GetString(node.Properties, "path", "").Trim();
            if (string.IsNullOrWhiteSpace(path))
            {
                return socket.Equals("Alpha", StringComparison.OrdinalIgnoreCase)
                    ? new Expression("1.0", MaterialValueType.Float)
                    : new Expression("vec3(0.5)", MaterialValueType.Vector3);
            }

            MaterialTextureSlot texture = RegisterTexture(node,
                MaterialAsset.GetString(node.Properties, "color_space", "sRGB"));
            Expression position = ResolveInput(node, "Position", new Expression("worldPosition", MaterialValueType.Vector3));
            Expression normal = ResolveInput(node, "Normal", new Expression("normalize(worldNormal)", MaterialValueType.Vector3, true));
            float tiling = MaterialAsset.GetFloat(node.Properties, "tiling", 0.01f);
            float sharpness = MaterialAsset.GetFloat(node.Properties, "sharpness", 4.0f);
            string sample = $"FuseSampleTriplanar({texture.UniformName}, {AsType(position, MaterialValueType.Vector3).Code}, {AsType(normal, MaterialValueType.Vector3).Code}, {ToFloat(tiling)}, {ToFloat(sharpness)})";
            return socket.Equals("Alpha", StringComparison.OrdinalIgnoreCase)
                ? new Expression(sample + ".a", MaterialValueType.Float)
                : new Expression(sample + ".rgb", MaterialValueType.Vector3);
        }

        private Expression ResolveTriplanarNormal(MaterialGraphNode node)
        {
            string path = MaterialAsset.GetString(node.Properties, "path", "").Trim();
            if (string.IsNullOrWhiteSpace(path))
                return new Expression("vec3(0.0, 0.0, 1.0)", MaterialValueType.Vector3);

            MaterialTextureSlot texture = RegisterTexture(node,
                MaterialAsset.GetString(node.Properties, "color_space", "data"));
            Expression position = ResolveInput(node, "Position", new Expression("worldPosition", MaterialValueType.Vector3));
            Expression normal = ResolveInput(node, "Normal", new Expression("normalize(worldNormal)", MaterialValueType.Vector3, true));
            float tiling = MaterialAsset.GetFloat(node.Properties, "tiling", 0.01f);
            float sharpness = MaterialAsset.GetFloat(node.Properties, "sharpness", 4.0f);
            float strength = MaterialAsset.GetFloat(node.Properties, "strength", 1.0f);
            string worldSample = $"FuseSampleTriplanarNormal({texture.UniformName}, {AsType(position, MaterialValueType.Vector3).Code}, {AsType(normal, MaterialValueType.Vector3).Code}, {ToFloat(tiling)}, {ToFloat(sharpness)}, {ToFloat(strength)})";
            return new Expression(
                $"FuseWorldToTangent({worldSample}, normalize(worldNormal), worldTangent, worldBitangent)",
                MaterialValueType.Vector3);
        }

        private Expression ResolveTerrainLayer(MaterialGraphNode node, string socket)
        {
            Expression position = ResolveInput(node, "Position", new Expression("worldPosition", MaterialValueType.Vector3));
            Expression normal = ResolveInput(node, "Normal", new Expression("normalize(worldNormal)", MaterialValueType.Vector3, true));
            Expression layer = ResolveInput(node, "Layer", PropertyFloat(node, "layer", 0.0f));
            float tiling = MaterialAsset.GetFloat(node.Properties, "tiling", 0.01f);
            float sharpness = MaterialAsset.GetFloat(node.Properties, "sharpness", 4.0f);
            string positionCode = AsType(position, MaterialValueType.Vector3).Code;
            string normalCode = AsType(normal, MaterialValueType.Vector3).Code;
            string layerCode = AsType(layer, MaterialValueType.Float).Code;

            switch (socket)
            {
                case "Color":
                    return SampleTerrainLayerArray(node, "albedo_paths", positionCode, normalCode, layerCode,
                        tiling, sharpness, ".rgb", "vec3(0.5)");
                case "Normal":
                {
                    List<string> paths = MaterialAsset.GetStringArray(node.Properties, "normal_paths");
                    if (paths.Count == 0)
                        return new Expression("vec3(0.0, 0.0, 1.0)", MaterialValueType.Vector3);
                    MaterialTextureArraySlot texture = RegisterTextureArray(node, "normal_paths", paths, "data");
                    string worldSample = $"FuseSampleTriplanarNormalArray({texture.UniformName}, {positionCode}, {normalCode}, {layerCode}, {ToFloat(texture.AssetPaths.Count - 1)}, {ToFloat(tiling)}, {ToFloat(sharpness)}, 1.0)";
                    return new Expression(
                        $"FuseWorldToTangent({worldSample}, normalize(worldNormal), worldTangent, worldBitangent)",
                        MaterialValueType.Vector3);
                }
                case "Roughness":
                    return SampleTerrainLayerArray(node, "orm_paths", positionCode, normalCode, layerCode,
                        tiling, sharpness, ".g", "0.5");
                case "AO":
                    return SampleTerrainLayerArray(node, "orm_paths", positionCode, normalCode, layerCode,
                        tiling, sharpness, ".r", "1.0");
                case "Height":
                    return SampleTerrainLayerArray(node, "height_paths", positionCode, normalCode, layerCode,
                        tiling, sharpness, ".r", "0.5");
                default:
                    return new Expression("vec3(0.0)", MaterialValueType.Vector3);
            }
        }

        private Expression SampleTerrainLayerArray(
            MaterialGraphNode node,
            string property,
            string positionCode,
            string normalCode,
            string layerCode,
            float tiling,
            float sharpness,
            string channel,
            string fallback)
        {
            List<string> paths = MaterialAsset.GetStringArray(node.Properties, property);
            if (paths.Count == 0)
            {
                MaterialValueType fallbackType = fallback.StartsWith("vec", StringComparison.Ordinal)
                    ? MaterialValueType.Vector3
                    : MaterialValueType.Float;
                return new Expression(fallback, fallbackType);
            }

            MaterialTextureArraySlot texture = RegisterTextureArray(node, property, paths,
                property.Equals("albedo_paths", StringComparison.OrdinalIgnoreCase) ? "sRGB" : "data");
            string sample = $"FuseSampleTriplanarArray({texture.UniformName}, {positionCode}, {normalCode}, {layerCode}, {ToFloat(texture.AssetPaths.Count - 1)}, {ToFloat(tiling)}, {ToFloat(sharpness)})";
            MaterialValueType resultType = channel.Equals(".rgb", StringComparison.OrdinalIgnoreCase)
                ? MaterialValueType.Vector3
                : MaterialValueType.Float;
            return new Expression(sample + channel, resultType);
        }

        private Expression ResolveTerrainLayerBlend(MaterialGraphNode node, string socket)
        {
            var colors = new Expression[4];
            var normals = new Expression[4];
            var roughness = new Expression[4];
            var ao = new Expression[4];
            var heights = new Expression[4];
            var weights = new string[4];

            for (int i = 0; i < 4; i++)
            {
                string prefix = $"Layer{i}";
                colors[i] = ResolveInput(node, prefix + "Color",
                    PropertyVector3(node, $"layer{i}_color", Vector3.One));
                normals[i] = ResolveInput(node, prefix + "Normal",
                    PropertyVector3(node, $"layer{i}_normal", new Vector3(0, 0, 1)));
                roughness[i] = ResolveInput(node, prefix + "Roughness",
                    PropertyFloat(node, $"layer{i}_roughness", 0.5f));
                ao[i] = ResolveInput(node, prefix + "AO",
                    PropertyFloat(node, $"layer{i}_ao", 1.0f));
                heights[i] = ResolveInput(node, prefix + "Height",
                    PropertyFloat(node, $"layer{i}_height", 0.5f));
                Expression weight = ResolveInput(node, prefix + "Weight",
                    PropertyFloat(node, $"layer{i}_weight", i == 0 ? 1.0f : 0.0f));
                weights[i] = $"FuseTerrainLayerWeight({AsType(weight, MaterialValueType.Float).Code}, {AsType(heights[i], MaterialValueType.Float).Code})";
            }

            string total = "max(" + string.Join(" + ", weights) + ", 0.0001)";
            string WeightedSum(Func<int, string> value)
            {
                return string.Join(" + ", Enumerable.Range(0, 4).Select(i =>
                    $"({value(i)} * {weights[i]})"));
            }

            return socket switch
            {
                "Color" => new Expression(
                    $"(({WeightedSum(i => AsType(colors[i], MaterialValueType.Vector3).Code)}) / {total})",
                    MaterialValueType.Vector3),
                "Normal" => new Expression(
                    $"normalize(({WeightedSum(i => AsType(normals[i], MaterialValueType.Vector3).Code)}) / {total})",
                    MaterialValueType.Vector3,
                    normals.All(expression => expression.IsWorldNormal)),
                "Roughness" => new Expression(
                    $"(({WeightedSum(i => AsType(roughness[i], MaterialValueType.Float).Code)}) / {total})",
                    MaterialValueType.Float),
                "AO" => new Expression(
                    $"(({WeightedSum(i => AsType(ao[i], MaterialValueType.Float).Code)}) / {total})",
                    MaterialValueType.Float),
                "Height" => new Expression(
                    $"(({WeightedSum(i => AsType(heights[i], MaterialValueType.Float).Code)}) / {total})",
                    MaterialValueType.Float),
                _ => new Expression("vec3(0.0)", MaterialValueType.Vector3)
            };
        }

        private Expression ResolveSwizzle(MaterialGraphNode node, string socket)
        {
            Expression value = ResolveInput(node, "Vector", new Expression("vec3(0.0)", MaterialValueType.Vector3));
            string code = AsType(value, MaterialValueType.Vector3).Code;
            return socket.ToUpperInvariant() switch
            {
                "X" => new Expression($"({code}).x", MaterialValueType.Float),
                "Y" => new Expression($"({code}).y", MaterialValueType.Float),
                "Z" => new Expression($"({code}).z", MaterialValueType.Float),
                "XY" => new Expression($"({code}).xy", MaterialValueType.Vector2),
                "XZ" => new Expression($"({code}).xz", MaterialValueType.Vector2),
                "YZ" => new Expression($"({code}).yz", MaterialValueType.Vector2),
                _ => new Expression(code, MaterialValueType.Vector3, value.IsWorldNormal)
            };
        }

        private Expression ResolveMapping(MaterialGraphNode node)
        {
            Expression coordinates = ResolveInput(node, "Coordinates", new Expression("materialUv", MaterialValueType.Vector2));
            string input = AsType(coordinates, MaterialValueType.Vector2).Code;
            Vector2 scale = MaterialAsset.GetVector2(node.Properties, "scale", Vector2.One);
            Vector2 offset = MaterialAsset.GetVector2(node.Properties, "offset", Vector2.Zero);
            float rotation = MaterialAsset.GetFloat(node.Properties, "rotation", 0.0f);
            string scaled = $"(({input}) * {ToVec2(scale)})";
            string rotated = rotation == 0.0f
                ? scaled
                : $"(({scaled}) * mat2(cos({ToFloat(rotation)}), -sin({ToFloat(rotation)}), sin({ToFloat(rotation)}), cos({ToFloat(rotation)})))";
            return new Expression($"({rotated} + {ToVec2(offset)})", MaterialValueType.Vector2);
        }

        private Expression ResolveMath(MaterialGraphNode node)
        {
            string operation = MaterialAsset.GetString(node.Properties, "operation", "Multiply");
            return operation.Trim().ToLowerInvariant() switch
            {
                "add" => ResolveBinary(node, "+"),
                "subtract" or "sub" => ResolveBinary(node, "-"),
                "multiply" or "mul" => ResolveBinary(node, "*"),
                "divide" or "div" => ResolveBinary(node, "/"),
                "min" => ResolveBinary(node, "min"),
                "max" => ResolveBinary(node, "max"),
                "power" or "pow" => ResolveBinary(node, "pow"),
                _ => ResolveBinary(node, "*")
            };
        }

        private Expression ResolveBinary(MaterialGraphNode node, string operation)
        {
            Expression a = ResolveInput(node, "A", new Expression("vec3(0.0)", MaterialValueType.Vector3));
            Expression b = ResolveInput(node, "B", operation is "*" or "/" or "pow"
                ? new Expression("vec3(1.0)", MaterialValueType.Vector3)
                : new Expression("vec3(0.0)", MaterialValueType.Vector3));
            MaterialValueType type = Widest(a.Type, b.Type);
            string aCode = AsType(a, type).Code;
            string bCode = AsType(b, type).Code;
            string expression = operation switch
            {
                "min" or "max" or "pow" => $"{operation}({aCode}, {bCode})",
                _ => $"({aCode} {operation} {bCode})"
            };
            return new Expression(expression, type,
                a.IsWorldNormal && b.IsWorldNormal && type == MaterialValueType.Vector3);
        }

        private Expression ResolveUnary(MaterialGraphNode node, string operation)
        {
            Expression input = ResolveInput(node, "Input", new Expression("vec3(0.0)", MaterialValueType.Vector3));
            string code = input.Code;
            string result = operation switch
            {
                "one_minus" => $"(1.0 - {code})",
                "saturate" => $"clamp({code}, 0.0, 1.0)",
                _ => $"{operation}({code})"
            };
            return new Expression(result, input.Type, input.IsWorldNormal && operation == "normalize");
        }

        private Expression ResolveNormalize(MaterialGraphNode node)
        {
            Expression input = ResolveInput(node, "Input", new Expression("vec3(0.0, 0.0, 1.0)", MaterialValueType.Vector3));
            return new Expression($"normalize({AsType(input, MaterialValueType.Vector3).Code})", MaterialValueType.Vector3, input.IsWorldNormal);
        }

        private Expression ResolveLength(MaterialGraphNode node)
        {
            Expression input = ResolveInput(node, "Input", new Expression("vec3(0.0)", MaterialValueType.Vector3));
            return new Expression($"length({AsType(input, MaterialValueType.Vector3).Code})", MaterialValueType.Float);
        }

        private Expression ResolveDot(MaterialGraphNode node)
        {
            Expression a = ResolveInput(node, "A", new Expression("vec3(0.0, 1.0, 0.0)", MaterialValueType.Vector3));
            Expression b = ResolveInput(node, "B", new Expression("vec3(0.0, 1.0, 0.0)", MaterialValueType.Vector3));
            return new Expression($"dot({AsType(a, MaterialValueType.Vector3).Code}, {AsType(b, MaterialValueType.Vector3).Code})", MaterialValueType.Float);
        }

        private Expression ResolveClamp(MaterialGraphNode node)
        {
            Expression value = ResolveInput(node, "Value", new Expression("vec3(0.0)", MaterialValueType.Vector3));
            Expression min = ResolveInput(node, "Min", new Expression("vec3(0.0)", MaterialValueType.Vector3));
            Expression max = ResolveInput(node, "Max", new Expression("vec3(1.0)", MaterialValueType.Vector3));
            MaterialValueType type = Widest(value.Type, Widest(min.Type, max.Type));
            return new Expression($"clamp({AsType(value, type).Code}, {AsType(min, type).Code}, {AsType(max, type).Code})", type);
        }

        private Expression ResolveSmoothstep(MaterialGraphNode node)
        {
            Expression value = ResolveInput(node, "Value", new Expression("0.0", MaterialValueType.Float));
            Expression edge0 = ResolveInput(node, "Edge0", new Expression("0.0", MaterialValueType.Float));
            Expression edge1 = ResolveInput(node, "Edge1", new Expression("1.0", MaterialValueType.Float));
            return new Expression($"smoothstep({AsType(edge0, MaterialValueType.Float).Code}, {AsType(edge1, MaterialValueType.Float).Code}, {AsType(value, MaterialValueType.Float).Code})", MaterialValueType.Float);
        }

        private Expression ResolveRemap(MaterialGraphNode node)
        {
            Expression value = ResolveInput(node, "Value", new Expression("0.0", MaterialValueType.Float));
            Expression inMin = ResolveInput(node, "InMin", new Expression("0.0", MaterialValueType.Float));
            Expression inMax = ResolveInput(node, "InMax", new Expression("1.0", MaterialValueType.Float));
            Expression outMin = ResolveInput(node, "OutMin", new Expression("0.0", MaterialValueType.Float));
            Expression outMax = ResolveInput(node, "OutMax", new Expression("1.0", MaterialValueType.Float));
            return new Expression($"FuseRemap({AsType(value, MaterialValueType.Float).Code}, {AsType(inMin, MaterialValueType.Float).Code}, {AsType(inMax, MaterialValueType.Float).Code}, {AsType(outMin, MaterialValueType.Float).Code}, {AsType(outMax, MaterialValueType.Float).Code})", MaterialValueType.Float);
        }

        private Expression ResolveNoise(MaterialGraphNode node, bool fractal)
        {
            Expression coordinates = ResolveInput(node, "Coordinates", new Expression("worldPosition.xz", MaterialValueType.Vector2));
            string coordinateCode = AsType(coordinates, MaterialValueType.Vector2).Code;
            float scale = MaterialAsset.GetFloat(node.Properties, "scale", 0.01f);
            float seed = MaterialAsset.GetFloat(node.Properties, "seed", 0.0f);
            if (!fractal)
                return new Expression($"FuseValueNoise2D(({coordinateCode}) * {ToFloat(scale)} + vec2({ToFloat(seed)}, {ToFloat(seed * 1.37f)}))", MaterialValueType.Float);

            int octaves = System.Math.Clamp((int)MathF.Round(MaterialAsset.GetFloat(node.Properties, "octaves", 5)), 1, 8);
            float lacunarity = MaterialAsset.GetFloat(node.Properties, "lacunarity", 2.0f);
            float gain = MaterialAsset.GetFloat(node.Properties, "gain", 0.5f);
            return new Expression($"FuseFbm2D({coordinateCode}, {ToFloat(scale)}, {octaves}, {ToFloat(lacunarity)}, {ToFloat(gain)}, {ToFloat(seed)})", MaterialValueType.Float);
        }

        private Expression ResolveDomainWarp(MaterialGraphNode node)
        {
            Expression coordinates = ResolveInput(node, "Coordinates", new Expression("worldPosition.xz", MaterialValueType.Vector2));
            float scale = MaterialAsset.GetFloat(node.Properties, "scale", 0.01f);
            float strength = MaterialAsset.GetFloat(node.Properties, "strength", 0.25f);
            int octaves = System.Math.Clamp((int)MathF.Round(MaterialAsset.GetFloat(node.Properties, "octaves", 3)), 1, 6);
            float seed = MaterialAsset.GetFloat(node.Properties, "seed", 0.0f);
            return new Expression($"FuseDomainWarp({AsType(coordinates, MaterialValueType.Vector2).Code}, {ToFloat(scale)}, {ToFloat(strength)}, {octaves}, {ToFloat(seed)})", MaterialValueType.Vector2);
        }

        private Expression ResolveLerp(MaterialGraphNode node)
        {
            Expression a = ResolveInput(node, "A", new Expression("vec3(0.0)", MaterialValueType.Vector3));
            Expression b = ResolveInput(node, "B", new Expression("vec3(1.0)", MaterialValueType.Vector3));
            MaterialValueType type = Widest(a.Type, b.Type);
            Expression factor = ResolveInput(node, "Factor", new Expression("0.5", MaterialValueType.Float));
            return new Expression($"mix({AsType(a, type).Code}, {AsType(b, type).Code}, {AsType(factor, MaterialValueType.Float).Code})", type,
                a.IsWorldNormal && b.IsWorldNormal && type == MaterialValueType.Vector3);
        }

        private Expression ResolveNormalBlend(MaterialGraphNode node)
        {
            Expression a = ResolveInput(node, "A", new Expression("vec3(0.0, 0.0, 1.0)", MaterialValueType.Vector3));
            Expression b = ResolveInput(node, "B", new Expression("vec3(0.0, 0.0, 1.0)", MaterialValueType.Vector3));
            Expression factor = ResolveInput(node, "Factor", new Expression("0.5", MaterialValueType.Float));
            return new Expression(
                $"normalize(mix({AsType(a, MaterialValueType.Vector3).Code}, {AsType(b, MaterialValueType.Vector3).Code}, clamp({AsType(factor, MaterialValueType.Float).Code}, 0.0, 1.0)))",
                MaterialValueType.Vector3,
                a.IsWorldNormal && b.IsWorldNormal);
        }

        private Expression ResolveHeightBlend(MaterialGraphNode node)
        {
            Expression a = ResolveInput(node, "A", new Expression("vec3(0.0)", MaterialValueType.Vector3));
            Expression b = ResolveInput(node, "B", new Expression("vec3(1.0)", MaterialValueType.Vector3));
            Expression heightA = ResolveInput(node, "HeightA", new Expression("0.5", MaterialValueType.Float));
            Expression heightB = ResolveInput(node, "HeightB", new Expression("0.5", MaterialValueType.Float));
            Expression weight = ResolveInput(node, "Weight", new Expression("0.5", MaterialValueType.Float));
            string factor = $"FuseHeightBlendFactor({AsType(heightA, MaterialValueType.Float).Code}, {AsType(heightB, MaterialValueType.Float).Code}, {AsType(weight, MaterialValueType.Float).Code})";
            MaterialValueType type = Widest(a.Type, b.Type);
            return new Expression($"mix({AsType(a, type).Code}, {AsType(b, type).Code}, {factor})", type);
        }

        private Expression ResolveNormalMap(MaterialGraphNode node)
        {
            Expression color = ResolveInput(node, "Color",
                new Expression("vec3(0.5, 0.5, 1.0)", MaterialValueType.Vector3));
            float defaultStrength = MaterialAsset.GetFloat(node.Properties, "strength", 1.0f);
            Expression strength = ResolveInput(node, "Strength",
                new Expression(ToFloat(defaultStrength), MaterialValueType.Float));
            string colorCode = AsType(color, MaterialValueType.Vector3).Code;
            string strengthCode = AsType(strength, MaterialValueType.Float).Code;
            string normal = $"normalize((({colorCode}) * 2.0 - 1.0) * vec3({strengthCode}, {strengthCode}, 1.0))";
            return new Expression(normal, MaterialValueType.Vector3);
        }

        private MaterialTextureSlot RegisterTexture(MaterialGraphNode node, string colorSpace)
        {
            if (_textureByNode.TryGetValue(node.Id, out MaterialTextureSlot? existing))
                return existing;

            int slot = AllocateTextureSlot();
            var texture = new MaterialTextureSlot(
                node.Id,
                $"uMaterialTexture{slot}",
                MaterialAsset.GetString(node.Properties, "path", ""),
                slot,
                ParseColorSpace(colorSpace));
            Textures.Add(texture);
            _textureByNode[node.Id] = texture;
            return texture;
        }

        private MaterialTextureArraySlot RegisterTextureArray(
            MaterialGraphNode node,
            string property,
            IReadOnlyList<string> paths,
            string colorSpace)
        {
            var key = (node.Id, property);
            if (_textureArrayByNode.TryGetValue(key, out MaterialTextureArraySlot? existing))
                return existing;

            int slot = AllocateTextureSlot();
            var normalizedPaths = paths
                .Select(MaterialAsset.NormalizeAssetPath)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .ToArray();
            var texture = new MaterialTextureArraySlot(
                $"{node.Id}:{property}",
                $"uMaterialTextureArray{TextureArrays.Count}",
                normalizedPaths,
                slot,
                ParseColorSpace(colorSpace));
            TextureArrays.Add(texture);
            _textureArrayByNode[key] = texture;
            return texture;
        }

        private int AllocateTextureSlot()
        {
            if (_nextTextureSlot >= MaterialRuntime.MaxTextureSlots)
                throw new InvalidDataException($"Material graph exceeds the limit of {MaterialRuntime.MaxTextureSlots} texture samplers (2D and arrays combined).");
            return _nextTextureSlot++;
        }

        private Expression ResolveUniform(MaterialGraphNode node, MaterialValueType type, string prefix)
        {
            if (!_uniformByNode.TryGetValue(node.Id, out MaterialUniformSlot? uniform))
            {
                string parameterName = MaterialAsset.GetString(node.Properties, "parameter_name", "").Trim();
                if (!MaterialAsset.GetBool(node.Properties, "expose", false))
                    parameterName = "";
                uniform = new MaterialUniformSlot(node.Id, $"{prefix}{Uniforms.Count}", type, parameterName);
                Uniforms.Add(uniform);
                _uniformByNode[node.Id] = uniform;
            }
            return new Expression(uniform.UniformName, type);
        }

        private static Expression PropertyFloat(MaterialGraphNode node, string key, float fallback) =>
            new(ToFloat(MaterialAsset.GetFloat(node.Properties, key, fallback)), MaterialValueType.Float);

        private static Expression PropertyVector3(MaterialGraphNode node, string key, Vector3 fallback) =>
            new(ToVec3(MaterialAsset.GetVector3(node.Properties, key, fallback)), MaterialValueType.Vector3);

        private static TextureColorSpace ParseColorSpace(string value) => value.Trim().ToLowerInvariant() switch
        {
            "srgb" or "s-rgb" or "color" => TextureColorSpace.Srgb,
            "data" => TextureColorSpace.Data,
            "linear" => TextureColorSpace.Linear,
            _ => TextureColorSpace.Srgb
        };

        private static MaterialValueType Widest(MaterialValueType a, MaterialValueType b) =>
            (MaterialValueType)System.Math.Max((int)a, (int)b);

        private static Expression AsType(Expression expression, MaterialValueType target)
        {
            if (expression.Type == target)
                return expression;
            if (target == MaterialValueType.Vector3 && expression.Type == MaterialValueType.Float)
                return new Expression($"vec3({expression.Code})", target);
            if (target == MaterialValueType.Vector3 && expression.Type == MaterialValueType.Vector2)
                return new Expression($"vec3({expression.Code}, 0.0)", target);
            if (target == MaterialValueType.Vector2 && expression.Type == MaterialValueType.Float)
                return new Expression($"vec2({expression.Code})", target);
            if (target == MaterialValueType.Vector2 && expression.Type == MaterialValueType.Vector3)
                return new Expression($"({expression.Code}).xy", target);
            if (target == MaterialValueType.Float && expression.Type == MaterialValueType.Vector3)
                return new Expression($"dot({expression.Code}, vec3(0.2126, 0.7152, 0.0722))", target);
            if (target == MaterialValueType.Float && expression.Type == MaterialValueType.Vector2)
                return new Expression($"({expression.Code}).x", target);
            return expression;
        }

        private static string ToFloat(float value) => value.ToString("0.0######", CultureInfo.InvariantCulture);
        private static string ToVec2(Vector2 value) => $"vec2({ToFloat(value.X)}, {ToFloat(value.Y)})";
        private static string ToVec3(Vector3 value) => $"vec3({ToFloat(value.X)}, {ToFloat(value.Y)}, {ToFloat(value.Z)})";

        private const string GlslHelpers = """
            float FuseHash21(vec2 p)
            {
                p = fract(p * vec2(123.34, 456.21));
                p += dot(p, p + 45.32);
                return fract(p.x * p.y);
            }

            float FuseValueNoise2D(vec2 p)
            {
                vec2 cell = floor(p);
                vec2 local = fract(p);
                local = local * local * (3.0 - 2.0 * local);
                float a = FuseHash21(cell);
                float b = FuseHash21(cell + vec2(1.0, 0.0));
                float c = FuseHash21(cell + vec2(0.0, 1.0));
                float d = FuseHash21(cell + vec2(1.0, 1.0));
                return mix(mix(a, b, local.x), mix(c, d, local.x), local.y);
            }

            float FuseFbm2D(vec2 p, float scale, int octaves, float lacunarity, float gain, float seed)
            {
                vec2 position = p * scale + vec2(seed * 17.13, seed * 31.71);
                float amplitude = 0.5;
                float sum = 0.0;
                float normalization = 0.0;
                for (int i = 0; i < 8; ++i)
                {
                    if (i >= octaves)
                        break;
                    sum += FuseValueNoise2D(position) * amplitude;
                    normalization += amplitude;
                    position = position * lacunarity + vec2(17.0, 11.0);
                    amplitude *= gain;
                }
                return sum / max(normalization, 0.0001);
            }

            vec2 FuseDomainWarp(vec2 p, float scale, float strength, int octaves, float seed)
            {
                vec2 offset = vec2(
                    FuseFbm2D(p + vec2(13.7, 4.2), scale, octaves, 2.0, 0.5, seed + 1.0),
                    FuseFbm2D(p + vec2(2.8, 19.1), scale, octaves, 2.0, 0.5, seed + 7.0));
                return p + (offset - vec2(0.5)) * strength;
            }

            float FuseRemap(float value, float inMin, float inMax, float outMin, float outMax)
            {
                float factor = clamp((value - inMin) / max(inMax - inMin, 0.00001), 0.0, 1.0);
                return mix(outMin, outMax, factor);
            }

            vec3 FuseTriplanarWeights(vec3 normal, float sharpness)
            {
                vec3 weights = pow(abs(normalize(normal)), vec3(max(sharpness, 1.0)));
                return weights / max(weights.x + weights.y + weights.z, 0.0001);
            }

            vec4 FuseSampleTriplanar(sampler2D textureSampler, vec3 position, vec3 normal, float tiling, float sharpness)
            {
                vec3 weights = FuseTriplanarWeights(normal, sharpness);
                vec4 xProjection = texture(textureSampler, position.yz * tiling);
                vec4 yProjection = texture(textureSampler, position.xz * tiling);
                vec4 zProjection = texture(textureSampler, position.xy * tiling);
                return xProjection * weights.x + yProjection * weights.y + zProjection * weights.z;
            }

            vec4 FuseSampleTextureArray(sampler2DArray textureSampler, vec2 uv, float layer, float maxLayer)
            {
                float slice = clamp(floor(layer + 0.5), 0.0, maxLayer);
                return texture(textureSampler, vec3(uv, slice));
            }

            vec4 FuseSampleTriplanarArray(sampler2DArray textureSampler, vec3 position, vec3 normal, float layer, float maxLayer, float tiling, float sharpness)
            {
                vec3 weights = FuseTriplanarWeights(normal, sharpness);
                vec4 xProjection = FuseSampleTextureArray(textureSampler, position.yz * tiling, layer, maxLayer);
                vec4 yProjection = FuseSampleTextureArray(textureSampler, position.xz * tiling, layer, maxLayer);
                vec4 zProjection = FuseSampleTextureArray(textureSampler, position.xy * tiling, layer, maxLayer);
                return xProjection * weights.x + yProjection * weights.y + zProjection * weights.z;
            }

            vec3 FuseDecodeProjectedNormal(vec3 encoded, vec3 axisNormal)
            {
                vec3 tangentNormal = normalize(encoded * 2.0 - 1.0);
                vec3 result;
                if (abs(axisNormal.x) > 0.5)
                    result = vec3(tangentNormal.z, tangentNormal.x, tangentNormal.y);
                else if (abs(axisNormal.y) > 0.5)
                    result = vec3(tangentNormal.x, tangentNormal.z, tangentNormal.y);
                else
                    result = vec3(tangentNormal.x, tangentNormal.y, tangentNormal.z);
                if (dot(result, axisNormal) < 0.0)
                    result -= axisNormal * 2.0 * dot(result, axisNormal);
                return normalize(result);
            }

            vec3 FuseSampleTriplanarNormal(sampler2D textureSampler, vec3 position, vec3 normal, float tiling, float sharpness, float strength)
            {
                vec3 weights = FuseTriplanarWeights(normal, sharpness);
                vec3 xNormal = FuseDecodeProjectedNormal(texture(textureSampler, position.yz * tiling).rgb, vec3(1.0, 0.0, 0.0));
                vec3 yNormal = FuseDecodeProjectedNormal(texture(textureSampler, position.xz * tiling).rgb, vec3(0.0, 1.0, 0.0));
                vec3 zNormal = FuseDecodeProjectedNormal(texture(textureSampler, position.xy * tiling).rgb, vec3(0.0, 0.0, 1.0));
                vec3 blended = normalize(xNormal * weights.x + yNormal * weights.y + zNormal * weights.z);
                return normalize(mix(vec3(0.0, 0.0, 1.0), blended, clamp(strength, 0.0, 2.0)));
            }

            vec3 FuseSampleTriplanarNormalArray(sampler2DArray textureSampler, vec3 position, vec3 normal, float layer, float maxLayer, float tiling, float sharpness, float strength)
            {
                vec3 weights = FuseTriplanarWeights(normal, sharpness);
                vec3 xNormal = FuseDecodeProjectedNormal(FuseSampleTextureArray(textureSampler, position.yz * tiling, layer, maxLayer).rgb, vec3(1.0, 0.0, 0.0));
                vec3 yNormal = FuseDecodeProjectedNormal(FuseSampleTextureArray(textureSampler, position.xz * tiling, layer, maxLayer).rgb, vec3(0.0, 1.0, 0.0));
                vec3 zNormal = FuseDecodeProjectedNormal(FuseSampleTextureArray(textureSampler, position.xy * tiling, layer, maxLayer).rgb, vec3(0.0, 0.0, 1.0));
                vec3 blended = normalize(xNormal * weights.x + yNormal * weights.y + zNormal * weights.z);
                return normalize(mix(vec3(0.0, 0.0, 1.0), blended, clamp(strength, 0.0, 2.0)));
            }

            vec3 FuseWorldToTangent(vec3 worldValue, vec3 worldNormal, vec3 worldTangent, vec3 worldBitangent)
            {
                vec3 normal = normalize(worldNormal);
                vec3 tangent = worldTangent;
                if (dot(tangent, tangent) < 0.000001)
                {
                    vec3 reference = abs(normal.y) < 0.9 ? vec3(0.0, 1.0, 0.0) : vec3(0.0, 0.0, 1.0);
                    tangent = normalize(cross(reference, normal));
                }
                tangent = normalize(tangent - normal * dot(normal, tangent));
                vec3 bitangent = worldBitangent;
                if (dot(bitangent, bitangent) < 0.000001)
                    bitangent = cross(normal, tangent);
                bitangent = normalize(bitangent - normal * dot(normal, bitangent));
                if (dot(cross(normal, tangent), bitangent) < 0.0)
                    bitangent = -bitangent;
                return normalize(vec3(dot(worldValue, tangent), dot(worldValue, bitangent), dot(worldValue, normal)));
            }

            float FuseTerrainLayerWeight(float weight, float height)
            {
                return max(weight, 0.0) * mix(0.25, 1.0, clamp(height, 0.0, 1.0));
            }

            float FuseHeightBlendFactor(float heightA, float heightB, float weight)
            {
                float a = heightA + (1.0 - clamp(weight, 0.0, 1.0));
                float b = heightB + clamp(weight, 0.0, 1.0);
                float maximum = max(a, b);
                float weightA = max(a - maximum, 0.0);
                float weightB = max(b - maximum, 0.0);
                return weightB / max(weightA + weightB, 0.0001);
            }
            """;
    }
}
