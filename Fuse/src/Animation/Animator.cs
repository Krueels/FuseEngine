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

    // Cross-fade
    private AnimationClip? _prevClip;
    private double _prevTime;
    private float _fadeDuration;
    private float _fadeTimer;
    private float _fadeProgress;
    private Matrix4x4[]? _prevNodeLocals;
    private Matrix4x4[]? _nextNodeLocals;

    public Animator(Skeleton skeleton)
    {
        _skeleton = skeleton;
        FinalBoneMatrices = new Matrix4x4[skeleton.Bones.Length];
        ResetToRest();
    }

    public bool Play(string clipName)
    {
        var clip = GetClip(clipName);
        if (clip == null)
            return false;
        Play(clip);
        return true;
    }

    public void Play(AnimationClip clip, bool restart = true)
    {
        if (CurrentClip == clip && !restart)
            return;
        CurrentClip = clip;
        TimeSeconds = 0.0;
        Playing = true;
    }

    public bool CrossFade(string clipName, float fadeTime)
    {
        var clip = GetClip(clipName);
        if (clip == null)
            return false;
        CrossFade(clip, fadeTime);
        return true;
    }

    public void CrossFade(AnimationClip clip, float fadeTime)
    {
        if (CurrentClip == clip)
            return;

        _prevClip = CurrentClip;
        _prevTime = TimeSeconds;
        CurrentClip = clip;
        TimeSeconds = 0.0;
        Playing = true;

        _fadeDuration = fadeTime;
        _fadeTimer = 0.0f;
        _fadeProgress = 0.0f;

        _prevNodeLocals = new Matrix4x4[_skeleton.Nodes.Length];
        _nextNodeLocals = new Matrix4x4[_skeleton.Nodes.Length];
    }

    public AnimationClip? GetClip(string name) => Model?.Clips.GetValueOrDefault(name);

    internal SkinnedModel? Model { get; set; }

    public void Update(float dt)
    {
        //Logger.Info($"[AnimatorUpdate] clip={CurrentClip?.Name} playing={Playing} dt={dt:F4} bones={FinalBoneMatrices.Length}");
        bool animate = CurrentClip != null && Playing && dt > 0f;
        bool fading = _prevClip != null && _fadeProgress < 1.0f;

        foreach (var node in _skeleton.Nodes)
            node.Local = node.RestLocal;

        if (animate && !Skeleton.DebugFreezeTime)
        {
            TimeSeconds += dt * Speed;

            if (fading)
            {
                _fadeTimer += dt;
                _fadeProgress = MathF.Min(_fadeTimer / _fadeDuration, 1.0f);
            }
        }

        if (animate)
            CurrentClip.Apply(TimeSeconds, _skeleton);

        if (fading)
        {
            // 1. Capturar pose do novo clip (já aplicada em _skeleton.Nodes)
            for (int i = 0; i < _skeleton.Nodes.Length; i++)
                _nextNodeLocals![i] = _skeleton.Nodes[i].Local;

            // 2. Capturar pose do clip antigo
            _prevClip!.Apply(_prevTime, _skeleton);
            for (int i = 0; i < _skeleton.Nodes.Length; i++)
                _prevNodeLocals![i] = _skeleton.Nodes[i].Local;

            // 3. Blend TRS entre antigo e novo
            float w = _fadeProgress;
            for (int i = 0; i < _skeleton.Nodes.Length; i++)
            {
                var oldNode = _prevNodeLocals[i];
                var newNode = _nextNodeLocals[i];

                DecomposeMatrix(oldNode, out Vector3 oldPos, out Quaternion oldRot, out Vector3 oldScale);
                DecomposeMatrix(newNode, out Vector3 newPos, out Quaternion newRot, out Vector3 newScale);

                // Shortest path: inverter sinal do quaternion se dot < 0
                if (Quaternion.Dot(oldRot, newRot) < 0f)
                    newRot = -newRot;

                Vector3 finalPos = Vector3.Lerp(oldPos, newPos, w);
                Quaternion finalRot = Quaternion.Slerp(oldRot, newRot, w);
                Vector3 finalScale = Vector3.Lerp(oldScale, newScale, w);

                _skeleton.Nodes[i].Local = ComposeMatrix(finalPos, finalRot, finalScale);
            }

            // Avançar tempo do clip antigo para manter sincronia
            if (!Skeleton.DebugFreezeTime)
                _prevTime += dt * Speed;

            if (_fadeProgress >= 1.0f)
            {
                _prevClip = null;
                _prevNodeLocals = null;
                _nextNodeLocals = null;
                _fadeDuration = 0;
                _fadeTimer = 0;
            }
        }

        _skeleton.ComputeFinalBoneMatrices(FinalBoneMatrices);

        if (!_dumpedFirstFrame)
        {
            _dumpedFirstFrame = true;
            DumpDebug();
        }
    }

    private static void DecomposeMatrix(Matrix4x4 m, out Vector3 position, out Quaternion rotation, out Vector3 scale)
    {
        // Des-transpõe a matriz para utilizar a decomposição nativa e robusta do .NET
        Matrix4x4 standardMatrix = Matrix4x4.Transpose(m);

        if (!Matrix4x4.Decompose(standardMatrix, out scale, out rotation, out position))
        {
            position = new Vector3(m.M41, m.M42, m.M43);
            rotation = Quaternion.Identity;
            scale = Vector3.One;
        }
    }

    private static Matrix4x4 ComposeMatrix(Vector3 position, Quaternion rotation, Vector3 scale)
    {
        return Matrix4x4.Transpose(
            Matrix4x4.CreateScale(scale)
            * Matrix4x4.CreateFromQuaternion(rotation)
            * Matrix4x4.CreateTranslation(position));
    }

    private bool _dumpedFirstFrame;

    public void DumpDebug()
    {
        Logger.InfoGold($"[SkinnedDump] clip={(CurrentClip?.Name ?? "null")} t={TimeSeconds:F3} bones={_skeleton.Bones.Length} nodes={_skeleton.Nodes.Length}");

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