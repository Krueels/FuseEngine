using System.Numerics;
using Silk.NET.OpenGL;
using Silk.NET.Assimp;
using Fuse.Animation;
using Fuse.Core;
using File = System.IO.File;
using AiScene = Silk.NET.Assimp.Scene;
using AiMesh = Silk.NET.Assimp.Mesh;

namespace Fuse.Renderer;

public static unsafe class SkinnedModelLoader
{
    private static Assimp? s_assimp;

    private static Assimp Api => s_assimp ??= Assimp.GetApi();

    public static SkinnedModel? Load(GL gl, string path, Func<string, Texture?>? textureResolver = null)
        => LoadCore(gl, path, textureResolver);

    /// <summary>Imports the same rig and clips without allocating GPU resources.</summary>
    public static SkinnedModel? LoadRig(string path) => LoadCore(null, path, null);

    private static SkinnedModel? LoadCore(GL? gl, string path, Func<string, Texture?>? textureResolver)
    {
        if (!File.Exists(path))
        {
            Logger.Error($"Skinned model file not found: {path}");
            return null;
        }

        // Hell2025 usa aiProcess_GlobalScale para Glock (NEW_RIG_FILE) — normaliza unidades do FBX
        // entre nós, canais de animação e offsets. Sem isso, canais e nós ficam em escalas diferentes.
        const uint GlobalScale = 0x8000000u;
        var scene = Api.ImportFile(path,
            (uint)(PostProcessSteps.Triangulate | PostProcessSteps.GenerateSmoothNormals |
                   PostProcessSteps.TransformUVCoords | (PostProcessSteps)0x1u) | GlobalScale);

        if (scene == null || scene->MRootNode == null || scene->MNumMeshes == 0)
        {
            Logger.Error($"Failed to load skinned model: {path}");
            return null;
        }

        // --- 1. Flatten node hierarchy (parents before children) ---
        var nodes = new List<AnimationNode>();
        var nodeMap = new Dictionary<string, int>(StringComparer.Ordinal);

        void Visit(Node* n, int parent)
        {
            int idx = nodes.Count;
            string name = n->MName.ToString();
            var aiMat = n->MTransformation;
            nodes.Add(new AnimationNode { Name = name, Parent = parent, RestLocal = n->MTransformation });
            if (nodes.Count <= 10)
                Logger.Info($"[LoaderDebug] node='{name}' mat=({aiMat.M11:F2},{aiMat.M12:F2},{aiMat.M13:F2},{aiMat.M14:F2} | {aiMat.M21:F2},{aiMat.M22:F2},{aiMat.M23:F2},{aiMat.M24:F2} | {aiMat.M31:F2},{aiMat.M32:F2},{aiMat.M33:F2},{aiMat.M34:F2} | {aiMat.M41:F2},{aiMat.M42:F2},{aiMat.M43:F2},{aiMat.M44:F2})");
            if (!string.IsNullOrEmpty(name))
                nodeMap.TryAdd(name, idx);

            for (uint i = 0; i < n->MNumChildren; i++)
                Visit(n->MChildren[i], idx);
        }
        Visit(scene->MRootNode, -1);

        Matrix4x4 globalInverse = Matrix4x4.Identity;
        if (!Matrix4x4.Invert(scene->MRootNode->MTransformation, out globalInverse))
            globalInverse = Matrix4x4.Identity;

        // --- 2. Collect bones across all meshes ---
        var boneNames = new List<string>();
        var boneOffsets = new List<Matrix4x4>();
        var boneIndexMap = new Dictionary<string, int>(StringComparer.Ordinal);

        for (uint m = 0; m < scene->MNumMeshes; m++)
        {
            var mesh = scene->MMeshes[m];
            for (uint b = 0; b < mesh->MNumBones; b++)
            {
                var bone = mesh->MBones[b];
                string name = bone->MName.ToString();
                if (boneIndexMap.ContainsKey(name))
                    continue;
                boneIndexMap[name] = boneNames.Count;
                boneNames.Add(name);
                boneOffsets.Add(bone->MOffsetMatrix);
            }
        }

        var bones = new Fuse.Animation.Bone[boneNames.Count];
        for (int i = 0; i < boneNames.Count; i++)
        {
            string name = boneNames[i];
            int nodeIdx = nodeMap.TryGetValue(name, out int ni) ? ni : -1;

            int parentBone = -1;
            if (nodeIdx >= 0)
            {
                int p = nodes[nodeIdx].Parent;
                while (p >= 0)
                {
                    if (boneIndexMap.TryGetValue(nodes[p].Name, out parentBone))
                        break;
                    p = nodes[p].Parent;
                }
            }

            bones[i] = new Fuse.Animation.Bone
            {
                Name = name,
                Index = i,
                NodeIndex = nodeIdx,
                OffsetMatrix = boneOffsets[i],
            };
        }

        var skeleton = new Fuse.Animation.Skeleton([.. nodes], nodeMap, bones, Matrix4x4.Identity);

        // --- 4. Build submeshes ---
        string modelDir = Path.GetDirectoryName(path) ?? "";
        var submeshes = new List<SkinnedSubmesh>();

        for (uint m = 0; gl != null && m < scene->MNumMeshes; m++)
        {
            var mesh = scene->MMeshes[m];

            var vertices = BuildVertices(mesh, boneIndexMap);
            var indices = new List<uint>();
            for (int f = 0; f < mesh->MNumFaces; f++)
            {
                var face = mesh->MFaces[f];
                for (int j = 0; j < face.MNumIndices; j++)
                    indices.Add(face.MIndices[j]);
            }

            Texture? tex = ResolveDiffuseTexture(scene, mesh, modelDir, textureResolver);

            submeshes.Add(new SkinnedSubmesh
            {
                Name = mesh->MName.ToString(),
                MaterialSlot = (int)mesh->MMaterialIndex,
                Mesh = new SkinnedMesh(gl, vertices, [.. indices]),
                Texture = tex,
            });
        }

        // --- 5. Animations ---
        var clips = new Dictionary<string, AnimationClip>(StringComparer.Ordinal);
        foreach (var clip in BuildAnimations(scene, nodeMap))
        {
            clips.TryAdd(clip.Name, clip);
        }

        Api.ReleaseImport(scene);

        var model = new SkinnedModel
        {
            SourcePath = path,
            Skeleton = skeleton,
            Submeshes = [.. submeshes],
            Clips = clips,
        };

        // Default clip preference: Idle > Walk > first
        model.DefaultClipName =
            clips.Keys.FirstOrDefault(k => k.Contains("Idle", StringComparison.OrdinalIgnoreCase)) ??
            clips.Keys.FirstOrDefault(k => k.Contains("Walk", StringComparison.OrdinalIgnoreCase)) ??
            clips.Keys.FirstOrDefault() ?? "";

        Logger.Asset($"Skinned model loaded: {path} ({submeshes.Count} submeshes, {bones.Length} bones, {clips.Count} clips)");
        return model;
    }

    private static SkinnedVertex[] BuildVertices(AiMesh* mesh, Dictionary<string, int> boneIndexMap)
    {
        int count = (int)mesh->MNumVertices;
        var vertices = new SkinnedVertex[count];

        // Accumulate raw weights per vertex
        var weights = new Dictionary<uint, List<(int bone, float weight)>>(count);
        for (uint b = 0; b < mesh->MNumBones; b++)
        {
            var bone = mesh->MBones[b];
            int boneIdx = boneIndexMap[bone->MName.ToString()];
            for (uint w = 0; w < bone->MNumWeights; w++)
            {
                var vw = bone->MWeights[w];
                if (vw.MWeight <= 0f || vw.MVertexId >= (uint)count)
                    continue;
                if (!weights.TryGetValue(vw.MVertexId, out var list))
                {
                    list = [];
                    weights[vw.MVertexId] = list;
                }
                list.Add((boneIdx, vw.MWeight));
            }
        }

        for (int i = 0; i < count; i++)
        {
            var pos = mesh->MVertices[i];
            var uv = Vector2.Zero;
            if (mesh->MTextureCoords[0] != null)
                uv = new Vector2(mesh->MTextureCoords[0][i].X, mesh->MTextureCoords[0][i].Y);
            var normal = new Vector3(0, 1, 0);
            if (mesh->MNormals != null)
                normal = new Vector3(mesh->MNormals[i].X, mesh->MNormals[i].Y, mesh->MNormals[i].Z);
            var tangent = mesh->MTangents != null
                ? new Vector3(mesh->MTangents[i].X, mesh->MTangents[i].Y, mesh->MTangents[i].Z)
                : Vector3.Zero;
            var bitangent = mesh->MBitangents != null
                ? new Vector3(mesh->MBitangents[i].X, mesh->MBitangents[i].Y, mesh->MBitangents[i].Z)
                : Vector3.Zero;

            ref var v = ref vertices[i];
            v.Position = new Vector3(pos.X, pos.Y, pos.Z);
            v.TexCoord = uv;
            v.Normal = normal;
            v.Tangent = tangent;
            v.Bitangent = bitangent;

            // Top-4 weights, renormalized. Unweighted vertices bind to root (bone 0).
            if (weights.TryGetValue((uint)i, out var list) && list.Count > 0)
            {
                list.Sort((a, b) => b.weight.CompareTo(a.weight));
                float sum = 0f;
                int n = System.Math.Min(4, list.Count);
                for (int k = 0; k < n; k++) sum += list[k].weight;

                if (sum <= 1e-6f)
                {
                    v.BoneIdX = 0; v.Weights = new Vector4(1, 0, 0, 0);
                }
                else
                {
                    v.BoneIdX = list[0].bone;
                    v.BoneIdY = n > 1 ? list[1].bone : 0;
                    v.BoneIdZ = n > 2 ? list[2].bone : 0;
                    v.BoneIdW = n > 3 ? list[3].bone : 0;
                    float inv = 1f / sum;
                    v.Weights = new Vector4(
                        list[0].weight * inv,
                        n > 1 ? list[1].weight * inv : 0f,
                        n > 2 ? list[2].weight * inv : 0f,
                        n > 3 ? list[3].weight * inv : 0f);
                }
            }
            else
            {
                v.BoneIdX = 0;
                v.Weights = new Vector4(1, 0, 0, 0);
            }
        }

        return vertices;
    }

    private static List<AnimationClip> BuildAnimations(AiScene* scene, Dictionary<string, int> nodeMap)
    {
        var result = new List<AnimationClip>((int)scene->MNumAnimations);

        for (uint a = 0; a < scene->MNumAnimations; a++)
        {
            var anim = scene->MAnimations[a];
            double duration = anim->MDuration;
            if (duration <= 0)
            {
                Logger.Warn($"Skipping empty animation '{anim->MName}'");
                continue;
            }

            string rawName = anim->MName.ToString();
            string name = rawName.Contains('|') ? rawName[(rawName.LastIndexOf('|') + 1)..] : rawName;
            if (string.IsNullOrWhiteSpace(name))
                name = $"animation_{a}";

            var channels = new List<AnimationChannel>((int)anim->MNumChannels);
            for (uint c = 0; c < anim->MNumChannels; c++)
            {
                var src = anim->MChannels[c];
                string nodeName = src->MNodeName.ToString();

                var posTimes = CopyKeys(src->MPositionKeys, src->MNumPositionKeys, k => new Vector3(k.MValue.X, k.MValue.Y, k.MValue.Z), out var positions);
                var rotTimes = CopyKeys(src->MRotationKeys, src->MNumRotationKeys, k => new Quaternion(k.MValue.X, k.MValue.Y, k.MValue.Z, k.MValue.W), out var rotations);
                var sclTimes = CopyKeys(src->MScalingKeys, src->MNumScalingKeys, k => new Vector3(k.MValue.X, k.MValue.Y, k.MValue.Z), out var scalings);

                channels.Add(new AnimationChannel
                {
                    NodeName = nodeName,
                    NodeIndex = nodeMap.TryGetValue(nodeName, out int ni) ? ni : -1,
                    PositionTimes = posTimes,
                    Positions = positions,
                    RotationTimes = rotTimes,
                    Rotations = rotations,
                    ScalingTimes = sclTimes,
                    Scalings = scalings,
                });
            }

            int resolved = channels.Count(c => c.NodeIndex >= 0);
            Logger.Info($"[Skinned] Clip '{name}': {channels.Count} channels, {resolved} resolved to nodes");
            var unresolved = channels.Where(c => c.NodeIndex < 0).Select(c => c.NodeName).Distinct().Take(6).ToList();
            if (unresolved.Count > 0)
                Logger.Warn($"[Skinned] Clip '{name}' unresolved nodes: {string.Join(", ", unresolved)}");

            double ticksPerSecond = anim->MTicksPerSecond;
            double durationSeconds = ticksPerSecond > 0 ? duration / ticksPerSecond : 0;
            Logger.Info($"[AnimLoad] Clip='{name}' DurationTicks={duration} TicksPerSecond={ticksPerSecond} CalcDurationSec={durationSeconds:F3} Loop={name is "Idle" or "Walk"}");
            if (ticksPerSecond <= 0 || ticksPerSecond > 1000 || durationSeconds < 0.1 || durationSeconds > 60) // valores absurdos = normalizar 
            {
                ticksPerSecond = 30.0f;
                Logger.Warn($"[AnimLoad] Normalizando TicksPerSecond para 30 (original={anim->MTicksPerSecond})");
            }



            result.Add(new AnimationClip
            {
                Name = name,
                DurationTicks = duration,
                TicksPerSecond = ticksPerSecond,
                Channels = [.. channels],
                Loop = name.Contains("Idle", StringComparison.OrdinalIgnoreCase) || name.Contains("Walk", StringComparison.OrdinalIgnoreCase),
            });
        }

        return result;
    }

    private static double[] CopyKeys<TVal>(VectorKey* keys, uint numKeys, Func<VectorKey, TVal> valSel, out TVal[] values)
    {
        values = new TVal[numKeys];
        var times = new double[numKeys];
        for (uint i = 0; i < numKeys; i++)
        {
            times[i] = keys[i].MTime;
            values[i] = valSel(keys[i]);
        }
        return times;
    }

    private static double[] CopyKeys<TVal>(QuatKey* keys, uint numKeys, Func<QuatKey, TVal> valSel, out TVal[] values)
    {
        values = new TVal[numKeys];
        var times = new double[numKeys];
        for (uint i = 0; i < numKeys; i++)
        {
            times[i] = keys[i].MTime;
            values[i] = valSel(keys[i]);
        }
        return times;
    }

    private static Texture? ResolveDiffuseTexture(AiScene* scene, AiMesh* mesh, string modelDir, Func<string, Texture?>? resolver)
    {
        if (resolver == null || mesh->MMaterialIndex >= scene->MNumMaterials)
            return null;

        var material = scene->MMaterials[mesh->MMaterialIndex];
        AssimpString texPath = default;
        if (Api.GetMaterialTexture(material, TextureType.Diffuse, 0, &texPath, null, null, null, null, null, null) != Return.Success)
            return null;

        string raw = texPath.ToString();
        if (string.IsNullOrWhiteSpace(raw) || raw.StartsWith('*'))
            return null;

        string fileName = Path.GetFileName(raw.Replace('\\', '/'));
        string candidate = Path.Combine(modelDir, fileName);
        if (!File.Exists(candidate))
            return null;

        return resolver(candidate);
    }

    /// <summary>
    /// Carrega animações de um FBX separado e mergea no modelo existente.
    /// Requer que o skeleton/node hierarchy seja IDÊNTICO (mesmos nomes de bones).
    /// </summary>
    public static void MergeAnimationsFromFile(SkinnedModel model, string animFbxPath, string? clipName = null)
    {
        if (!File.Exists(animFbxPath))
        {
            Logger.Error($"Animation FBX not found: {animFbxPath}");
            return;
        }

        if (!string.IsNullOrEmpty(clipName) && model.Clips.ContainsKey(clipName))
            return;

        var scene = Api.ImportFile(animFbxPath,
            (uint)(PostProcessSteps.Triangulate | PostProcessSteps.GenerateSmoothNormals |
                   PostProcessSteps.TransformUVCoords | (PostProcessSteps)0x1u) | 0x8000000u);

        if (scene == null || scene->MRootNode == null)
        {
            Logger.Error($"Failed to load animation FBX: {animFbxPath}");
            return;
        }

        // Build nodeMap do modelo DESTINO (já existe no skeleton)
        var destNodeMap = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < model.Skeleton.Nodes.Length; i++)
        {
            destNodeMap[model.Skeleton.Nodes[i].Name] = i;
        }

        // Extrai animações do arquivo de animação
        var newClips = BuildAnimations(scene, destNodeMap);

        foreach (var clip in newClips)
        {
            string finalName = clipName ?? clip.Name;
            if (model.Clips.ContainsKey(finalName))
            {
                Logger.Warn($"[MergeAnimations] Clip '{finalName}' já existe, sobrescrevendo...");
            }
            clip.Name = finalName;
            model.Clips[clip.Name] = clip;
            Logger.Info($"[MergeAnimations] Adicionado clip: '{clip.Name}' ({clip.DurationSeconds:F2}s, Loop={clip.Loop})");
        }

        Api.ReleaseImport(scene);

        // Atualiza DefaultClipName se não tinha
        if (string.IsNullOrEmpty(model.DefaultClipName) && model.Clips.Count > 0)
        {
            model.DefaultClipName = model.Clips.Keys.FirstOrDefault(k => k.Contains("Idle", StringComparison.OrdinalIgnoreCase))
                                 ?? model.Clips.Keys.FirstOrDefault(k => k.Contains("Walk", StringComparison.OrdinalIgnoreCase))
                                 ?? model.Clips.Keys.FirstOrDefault() ?? "";
        }
    }
}
