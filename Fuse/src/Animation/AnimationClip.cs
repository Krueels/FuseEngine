using System.Numerics;

namespace Fuse.Animation;

public sealed class AnimationChannel
{
    public required string NodeName { get; init; }
    public int NodeIndex { get; set; } = -1;

    public double[] PositionTimes { get; init; } = [];
    public Vector3[] Positions { get; init; } = [];

    public double[] RotationTimes { get; init; } = [];
    public Quaternion[] Rotations { get; init; } = [];

    public double[] ScalingTimes { get; init; } = [];
    public Vector3[] Scalings { get; init; } = [];
}

public sealed class AnimationClip
{
    private const double Epsilon = 1e-7;

    public required string Name { get; init; }
    public double DurationTicks { get; init; }
    public double TicksPerSecond { get; init; }
    public AnimationChannel[] Channels { get; init; } = [];

    public void Apply(double timeSeconds, Skeleton skeleton)
    {
        if (DurationTicks <= 0)
            return;

        double tps = TicksPerSecond > 0 ? TicksPerSecond : 30.0;
        double ticks = timeSeconds * tps;
        ticks %= DurationTicks;
        if (ticks < 0)
            ticks += DurationTicks;

        foreach (var ch in Channels)
        {
            if (ch.NodeIndex < 0)
                continue;

            // Canal sem nenhuma key não deve zerar o Local do nó — mantém o RestLocal
            if (ch.Positions.Length == 0 && ch.Rotations.Length == 0 && ch.Scalings.Length == 0)
                continue;

            var node = skeleton.Nodes[ch.NodeIndex];
            // Extrai escala do repouso corretamente: comprimento dos vetores base (colunas para Assimp row-major)
            // RestLocal é row-major (Assimp): escala = comprimento das LINHAS da parte 3x3
            var restScale = new Vector3(
                MathF.Sqrt(node.RestLocal.M11 * node.RestLocal.M11 + node.RestLocal.M12 * node.RestLocal.M12 + node.RestLocal.M13 * node.RestLocal.M13),
                MathF.Sqrt(node.RestLocal.M21 * node.RestLocal.M21 + node.RestLocal.M22 * node.RestLocal.M22 + node.RestLocal.M23 * node.RestLocal.M23),
                MathF.Sqrt(node.RestLocal.M31 * node.RestLocal.M31 + node.RestLocal.M32 * node.RestLocal.M32 + node.RestLocal.M33 * node.RestLocal.M33));
            node.Local = ComposeLocal(ch, ticks, restScale);
        }
    }

private static Matrix4x4 ComposeLocal(AnimationChannel ch, double ticks, Vector3 restScale)
        {
            bool hasPos = SamplePosition(ch, ticks, out var pos);
            bool hasRot = SampleRotation(ch, ticks, out var rot);

            if (!hasPos && !hasRot)
                return Matrix4x4.Identity;

            if (!hasPos) pos = Vector3.Zero;
            if (!hasRot) rot = Quaternion.Identity;

            // Quaternion não-unitário vira escala fantasma no CreateFromQuaternion
            float lenSq = rot.LengthSquared();
            if (lenSq is < 0.999f or > 1.001f)
                rot = Quaternion.Normalize(rot);

            // Usa ROTAÇÃO e TRANSLAÇÃO da animação, mas ESCALA DO REPOUSO
            // (ignora stretch-to/IK scaling exportado pelo Blender em magazine/dedos)
            return Matrix4x4.Transpose(
                Matrix4x4.CreateScale(restScale)
                * Matrix4x4.CreateFromQuaternion(rot)
                * Matrix4x4.CreateTranslation(pos));
        }

    public static bool SamplePosition(AnimationChannel ch, double t, out Vector3 value)
    {
        var keys = ch.Positions;
        if (keys.Length == 0) { value = default; return false; }
        if (keys.Length == 1 || t <= ch.PositionTimes[0]) { value = keys[0]; return true; }
        if (t >= ch.PositionTimes[^1]) { value = keys[^1]; return true; }

        int i = FindSegment(ch.PositionTimes, t);
        float a = (float)((t - ch.PositionTimes[i]) / (ch.PositionTimes[i + 1] - ch.PositionTimes[i]));
        value = Vector3.Lerp(keys[i], keys[i + 1], a);
        return true;
    }

    public static bool SampleRotation(AnimationChannel ch, double t, out Quaternion value)
    {
        var keys = ch.Rotations;
        if (keys.Length == 0) { value = default; return false; }
        if (keys.Length == 1 || t <= ch.RotationTimes[0]) { value = keys[0]; return true; }
        if (t >= ch.RotationTimes[^1]) { value = keys[^1]; return true; }

        int i = FindSegment(ch.RotationTimes, t);
        float a = (float)((t - ch.RotationTimes[i]) / (ch.RotationTimes[i + 1] - ch.RotationTimes[i]));
        value = Quaternion.Slerp(keys[i], keys[i + 1], a);
        return true;
    }

    public static bool SampleScaling(AnimationChannel ch, double t, out Vector3 value)
    {
        var keys = ch.Scalings;
        if (keys.Length == 0) { value = default; return false; }
        if (keys.Length == 1 || t <= ch.ScalingTimes[0]) { value = keys[0]; return true; }
        if (t >= ch.ScalingTimes[^1]) { value = keys[^1]; return true; }

        int i = FindSegment(ch.ScalingTimes, t);
        float a = (float)((t - ch.ScalingTimes[i]) / (ch.ScalingTimes[i + 1] - ch.ScalingTimes[i]));
        value = Vector3.Lerp(keys[i], keys[i + 1], a);
        return true;
    }

    private static int FindSegment(double[] times, double t)
    {
        for (int i = 0; i < times.Length - 1; i++)
        {
            if (t < times[i + 1] - Epsilon)
                return i;
        }
        return times.Length - 2;
    }
}
