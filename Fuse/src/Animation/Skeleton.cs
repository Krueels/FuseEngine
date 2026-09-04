using System.Numerics;
using Fuse.Core;

namespace Fuse.Animation;

public sealed class AnimationNode
{
    public required string Name { get; init; }
    public int Parent { get; init; } = -1;
    public Matrix4x4 RestLocal;
    public Matrix4x4 RestGlobal;
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
        GlobalInverse = globalInverse;

        foreach (var n in Nodes)
        {
            n.Local = n.RestLocal;
            n.Global = Matrix4x4.Identity;
        }

        // Save the bind-pose global transform. Runtime systems can use it to
        // read animation markers without ever modifying the skeleton.
        ComputeGlobalTransforms();
        foreach (var n in Nodes)
            n.RestGlobal = n.Global;
    }

    /// <summary>Animation locals are instance state; cached mesh assets must not share them.</summary>
    public Skeleton CreateInstance() => new(
        Nodes.Select(n => new AnimationNode
        {
            Name = n.Name, Parent = n.Parent, RestLocal = n.RestLocal
        }).ToArray(),
        new Dictionary<string, int>(_nodeMap, _nodeMap.Comparer),
        Bones, GlobalInverse);

    public bool TryGetNodeIndex(string name, out int index)
    {
        if (_nodeMap.TryGetValue(name, out index))
            return true;

        for (int i = 0; i < Nodes.Length; i++)
        {
            if (Nodes[i].Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                index = i;
                return true;
            }
        }

        index = -1;
        return false;
    }

    /// <summary>
    /// Returns only the rotational delta of an animated node relative to the
    /// bind pose. Translation and scale are deliberately discarded.
    /// </summary>
    public bool TryGetNodeAnimationRotation(string name, out Quaternion delta)
    {
        delta = Quaternion.Identity;
        if (!TryGetNodeIndex(name, out int index))
            return false;

        AnimationNode node = Nodes[index];
        if (!TryExtractRotation(node.RestGlobal, out Quaternion restRotation) ||
            !TryExtractRotation(node.Global, out Quaternion currentRotation))
            return false;

        // current = delta * rest in the animation convention used by the
        // skeleton, therefore delta = current * inverse(rest).
        delta = currentRotation * Quaternion.Inverse(restRotation);
        if (delta.LengthSquared() < 0.000001f || !IsFinite(delta))
        {
            delta = Quaternion.Identity;
            return true;
        }

        delta = Quaternion.Normalize(delta);
        return IsFinite(delta);
    }

    private static bool TryExtractRotation(Matrix4x4 animationMatrix, out Quaternion rotation)
    {
        rotation = Quaternion.Identity;
        Matrix4x4 standardMatrix = Matrix4x4.Transpose(animationMatrix);
        if (!Matrix4x4.Decompose(standardMatrix, out _, out rotation, out _))
            return false;
        if (!IsFinite(rotation) || rotation.LengthSquared() < 0.000001f)
        {
            rotation = Quaternion.Identity;
            return false;
        }

        rotation = Quaternion.Normalize(rotation);
        return true;
    }

    public void ComputeGlobalTransforms()
    {
        //for (int i = 0; i < MathF.Min(Nodes.Length, 10); i++)
        //{
        //    var n = Nodes[i];
        //    Logger.Info($"[SkeletonDebug] node[{i}]='{n.Name}' parent={n.Parent} restLocalT=({n.RestLocal.M41:F2},{n.RestLocal.M42:F2},{n.RestLocal.M43:F2}) localT=({n.Local.M41:F2},{n.Local.M42:F2},{n.Local.M43:F2})");
        //}

        // Log do resultado final
        //for (int b = 0; b < MathF.Min(Bones.Length, 5); b++)
        //{
        //    var bone = Bones[b];
        //    if (bone.NodeIndex < 0 || bone.NodeIndex >= Nodes.Length) continue;
        //    var g = Nodes[bone.NodeIndex].Global;
        //    var final = g * bone.OffsetMatrix;
        //    Logger.Info($"[FinalResult] '{bone.Name}' final=({final.M11:F2},{final.M12:F2},{final.M13:F2},{final.M14:F2} | {final.M21:F2},{final.M22:F2},{final.M23:F2},{final.M24:F2} | {final.M31:F2},{final.M32:F2},{final.M33:F2},{final.M34:F2} | {final.M41:F2},{final.M42:F2},{final.M43:F2},{final.M44:F2})");
        //}
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

        // Log temporário
        //if (Bones.Length > 0)
        //{
        //    var b0 = Bones[0];
        //    if (b0.NodeIndex >= 0 && b0.NodeIndex < Nodes.Length)
        //    {
        //        var g = Nodes[b0.NodeIndex].Global;
        //        Logger.Info($"[SkeletonDebug] bone0='{b0.Name}' nodeIdx={b0.NodeIndex} globalT=({g.M41:F2},{g.M42:F2},{g.M43:F2}) offsetT=({b0.OffsetMatrix.M41:F2},{b0.OffsetMatrix.M42:F2},{b0.OffsetMatrix.M43:F2})");
        //    }
        //}

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

    private static bool IsFinite(in Quaternion q) =>
        float.IsFinite(q.X) && float.IsFinite(q.Y) &&
        float.IsFinite(q.Z) && float.IsFinite(q.W);
}
