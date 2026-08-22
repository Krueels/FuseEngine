using System.Numerics;
using Fuse.Core;

namespace Fuse.Animation;

public sealed class Animator
{
    private readonly Skeleton _skeleton;

    public AnimationClip? CurrentClip { get; private set; }
    public double TimeSeconds { get; set; }
    public float Speed { get; set; } = 1.0f;
    public bool Playing { get; set; } = true;
    public Matrix4x4[] FinalBoneMatrices { get; }

    public Animator(Skeleton skeleton)
    {
        _skeleton = skeleton;
        FinalBoneMatrices = new Matrix4x4[skeleton.Bones.Length];
        ResetToRest();
    }

    public void Play(AnimationClip clip, bool restart = true)
    {
        if (CurrentClip == clip && !restart)
            return;
        CurrentClip = clip;
        TimeSeconds = 0.0;
        Playing = true;
    }

    public bool Play(string clipName)
    {
        var clip = GetClip(clipName);
        if (clip == null)
            return false;
        Play(clip);
        return true;
    }

    public AnimationClip? GetClip(string name) => Model?.Clips.GetValueOrDefault(name);

    internal SkinnedModel? Model { get; set; }

    public void Update(float dt)
    {
        bool animate = CurrentClip != null && Playing && dt > 0f;

        // Nós sem canal de animação mantêm o RestLocal (bind) — nunca ficam zerados
        foreach (var node in _skeleton.Nodes)
            node.Local = node.RestLocal;

        if (animate && !Skeleton.DebugFreezeTime)
        {
            TimeSeconds += dt * Speed;
        }

        if (animate)
        {
            CurrentClip.Apply(TimeSeconds, _skeleton);
        }

        _skeleton.ComputeFinalBoneMatrices(FinalBoneMatrices);

        if (!_dumpedFirstFrame)
        {
            _dumpedFirstFrame = true;
            DumpDebug();
        }
    }

    private bool _dumpedFirstFrame;

    public void DumpDebug()
    {
        Logger.InfoGold($"[SkinnedDump] clip={(CurrentClip?.Name ?? "null")} t={TimeSeconds:F3} bones={_skeleton.Bones.Length} nodes={_skeleton.Nodes.Length}");

        // Escaneia TODOS os ossos e reporta os piores deltas (caça dedos/mãos explodindo)
        var worst = new List<(string name, float dT, float dLin, int idx)>();
        for (int i = 0; i < _skeleton.Bones.Length; i++)
        {
            var bone = _skeleton.Bones[i];
            if (bone.NodeIndex < 0) continue;
            var g = _skeleton.Nodes[bone.NodeIndex].Global;
            var delta = g * bone.OffsetMatrix;
            float dT = new Vector3(delta.M14, delta.M24, delta.M34).Length();
            float dLin = System.Math.Max(
                System.Math.Abs(delta.M11 - 1f),
                System.Math.Max(System.Math.Abs(delta.M22 - 1f), System.Math.Abs(delta.M33 - 1f)));
            worst.Add((bone.Name, dT, dLin, i));
        }

        int exploded = worst.Count(w => w.dT > 5f);
        Logger.InfoGold($"[SkinnedDump] bones com |deltaT|>5: {exploded}/{worst.Count}");
        foreach (var w in worst.OrderByDescending(w => w.dT).Take(6))
        {
            Matrix4x4 f = FinalBoneMatrices[w.idx];
            Logger.InfoGold($"[SkinnedDump] WORST '{w.name}' deltaT={w.dT:F2} deltaLin={w.dLin:F3} finalT=({f.M41:F2}, {f.M42:F2}, {f.M43:F2})");
        }

        // Cadeia completa do pior osso: nome, translação local/rest/global de cada ancestral.
        // Se global divergir com locais iguais aos do repouso, a corrupção está entre
        // Apply e ComputeGlobalTransforms — não nos dados do arquivo.
        if (worst.Count > 0 && _skeleton.Nodes.Length > 0)
        {
            var w0 = worst[0];
            int ni = _skeleton.Bones[w0.idx].NodeIndex;
            double tps = CurrentClip?.TicksPerSecond ?? 30.0;
            Logger.InfoGold($"[SkinnedChain] pior='{w0.name}' t={TimeSeconds:F3} ticks={TimeSeconds * tps:F1} dur={(CurrentClip != null ? CurrentClip.DurationTicks.ToString() : "?")}");
            int guard = 0;
            while (ni >= 0 && guard++ < 16)
            {
                var n = _skeleton.Nodes[ni];
                Logger.InfoGold($"[SkinnedChain] '{n.Name}' localT=({n.Local.M14:F3},{n.Local.M24:F3},{n.Local.M34:F3}) restT=({n.RestLocal.M14:F3},{n.RestLocal.M24:F3},{n.RestLocal.M34:F3}) globalT=({n.Global.M14:F2},{n.Global.M24:F2},{n.Global.M34:F2})");
                ni = n.Parent;
            }
        }
    }

    private void ResetToRest()
    {
        foreach (var node in _skeleton.Nodes)
            node.Local = node.RestLocal;
    }
}
