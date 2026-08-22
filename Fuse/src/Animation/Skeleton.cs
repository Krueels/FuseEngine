using System.Numerics;

namespace Fuse.Animation;

public sealed class AnimationNode
{
    public required string Name { get; init; }
    public int Parent { get; init; } = -1;
    public Matrix4x4 RestLocal;
    public Matrix4x4 Local;
    public Matrix4x4 Global;
}

public sealed class Skeleton
{
    private readonly Dictionary<string, int> _nodeMap;
    public AnimationNode[] Nodes { get; }
    public Bone[] Bones { get; }
    public Matrix4x4 GlobalInverse { get; }

    public Skeleton(AnimationNode[] nodes, Dictionary<string, int> nodeMap, Bone[] bones, Matrix4x4 globalInverse)
    {
        Nodes = nodes;
        _nodeMap = nodeMap;
        Bones = bones;

        foreach (var n in Nodes)
        {
            n.Local = n.RestLocal;
            n.Global = Matrix4x4.Identity;
        }
    }

    public bool TryGetNodeIndex(string name, out int index) => _nodeMap.TryGetValue(name, out index);

    public void ComputeGlobalTransforms()
    {
        for (int i = 0; i < Nodes.Length; i++)
        {
            var n = Nodes[i];
            // Grids em espaço Assimp (column-convention): global = pai * local
            n.Global = n.Parent < 0 ? n.Local : Nodes[n.Parent].Global * n.Local;
        }
    }

    public static bool DebugBindPoseOnly = false; // F7 alterna em runtime p/ debug
    public static bool DebugFreezeTime = false;   // F5: congela tempo no key0 (isola sampling)
    public static bool DebugUploadRawGrid = true; // F4: convenção provada EM TELA (GLSL aplica o grid direto)

    public void ComputeFinalBoneMatrices(Matrix4x4[] output)
    {
        if (DebugBindPoseOnly)
        {
            for (int i = 0; i < output.Length; i++)
                output[i] = Matrix4x4.Identity;
            return;
        }

        ComputeGlobalTransforms();

        // Hell2025 style: final = global * OffsetMatrix (OffsetMatrix = inverse bind matrix)
        // Sem GlobalInverse, sem BindGlobal, sem transpose — SSBO std430 é column-major nativo.
        for (int i = 0; i < output.Length; i++)
            output[i] = Matrix4x4.Identity;

        for (int b = 0; b < Bones.Length; b++)
        {
            var bone = Bones[b];
            if (bone.NodeIndex < 0 || (uint)bone.Index >= (uint)output.Length)
                continue;

            var global = Nodes[bone.NodeIndex].Global;
            var final = global * bone.OffsetMatrix;

            output[bone.Index] = IsFinite(final) ? final : Matrix4x4.Identity;
        }
    }

    public static bool IsFinite(in Matrix4x4 m) =>
        float.IsFinite(m.M11) && float.IsFinite(m.M12) && float.IsFinite(m.M13) && float.IsFinite(m.M14) &&
        float.IsFinite(m.M21) && float.IsFinite(m.M22) && float.IsFinite(m.M23) && float.IsFinite(m.M24) &&
        float.IsFinite(m.M31) && float.IsFinite(m.M32) && float.IsFinite(m.M33) && float.IsFinite(m.M34) &&
        float.IsFinite(m.M41) && float.IsFinite(m.M42) && float.IsFinite(m.M43) && float.IsFinite(m.M44);
}
