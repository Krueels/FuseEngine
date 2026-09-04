using System.Numerics;
using Fuse.Enemy;
using static Fuse.Animation.SpiderLocomotionMath;

namespace Fuse.Animation;

internal readonly record struct SpiderLegPose(Vector3 Hip, Vector3 Knee, Vector3 Ankle, Vector3 Tip)
{
    public Vector3 this[int i] => i switch { 0 => Hip, 1 => Knee, 2 => Ankle, _ => Tip };
}

/// <summary>
/// Calibrated planar hinges with a bounded hip swivel. The distal segment is
/// solved together with the contact, so the solver never clamps a validated
/// target to a different point. A two-segment rig uses the same solve with L3=0.
/// </summary>
internal sealed class SpiderLegKinematics
{
    public SpiderLegPose Rest { get; }
    public SpiderJointLimits Limits { get; }
    public float L1 { get; }
    public float L2 { get; }
    public float L3 { get; }
    public float Reach => L1 + L2 + L3;
    private readonly Vector3 _outward;
    private readonly Vector3 _restPole;

    public SpiderLegKinematics(SpiderLegPose rest, SpiderJointLimits limits)
    {
        Rest = rest; Limits = limits;
        L1 = Vector3.Distance(rest.Hip, rest.Knee);
        L2 = Vector3.Distance(rest.Knee, rest.Ankle);
        L3 = Vector3.Distance(rest.Ankle, rest.Tip);
        _outward = Normal(Project(rest.Tip - rest.Hip, Vector3.UnitY), rest.Knee - rest.Hip);
        Vector3 axis = Normal(rest.Ankle - rest.Hip, _outward);
        _restPole = Normal(Project(rest.Knee - rest.Hip, axis), Vector3.UnitY);
    }

    public bool TrySolve(Vector3 target, Vector3 normal, in SpiderLegPose previous,
        out SpiderLegPose result, Func<SpiderLegPose, bool>? accept = null)
    {
        result = default;
        if (!Finite(target) || !Finite(normal) || L1 < Epsilon || L2 < Epsilon) return false;
        Vector3 radial = Normal(Project(target - Rest.Hip, normal), _outward);
        float restPitch = L3 > Epsilon ? MathF.Atan2(Vector3.Dot(Normal(Rest.Tip - Rest.Ankle, -normal), radial),
            Vector3.Dot(Normal(Rest.Tip - Rest.Ankle, -normal), -normal)) : 0f;
        float bestScore = float.MaxValue;
        float previousPitch = L3 > Epsilon ? MathF.Atan2(Vector3.Dot(Normal(previous.Tip - previous.Ankle, -normal), radial),
            Vector3.Dot(Normal(previous.Tip - previous.Ankle, -normal), -normal)) : 0f;
        ReadOnlySpan<float> pitches = [0f, 20f, -20f, 40f, -40f, 60f, -60f];
        ReadOnlySpan<float> swivels = [0f, 20f, -20f, 40f, -40f];
        for (int p = -1; p < (L3 > Epsilon ? pitches.Length : 0); p++)
        {
            float pitch = System.Math.Clamp(p < 0 ? previousPitch : restPitch + float.DegreesToRadians(pitches[p]),
                -float.DegreesToRadians(Limits.FootPitchDegrees), float.DegreesToRadians(Limits.FootPitchDegrees));
            Vector3 distal = -normal * MathF.Cos(pitch) + radial * MathF.Sin(pitch);
            Vector3 ankle = target - distal * L3;
            Vector3 offset = ankle - Rest.Hip;
            float distance = offset.Length();
            if (distance <= MathF.Abs(L1 - L2) + Epsilon || distance >= L1 + L2 - Epsilon) continue;
            Vector3 axis = offset / distance;
            float along = (L1 * L1 - L2 * L2 + distance * distance) / (2f * distance);
            float height = MathF.Sqrt(MathF.Max(0f, L1 * L1 - along * along));
            Vector3 pole = Normal(Project(_restPole, axis), Project(Vector3.UnitY, axis));
            Vector3 previousPole = Normal(Project(previous.Knee - Rest.Hip, axis), pole);
            // Include the exact previous pole/pitch, not a quantized or damped
            // version. Otherwise even an unchanged valid stance can drift into
            // a limit and fail its own next solve.
            for (int s = -1; s < swivels.Length; s++)
            {
                Vector3 bend = s < 0 ? previousPole :
                    Vector3.Transform(pole, Quaternion.CreateFromAxisAngle(axis, float.DegreesToRadians(swivels[s])));
                if (Angle(bend, Normal(Project(_restPole, axis), bend)) > float.DegreesToRadians(Limits.KneePlaneDegrees)) continue;
                Vector3 knee = Rest.Hip + axis * along + bend * height;
                var pose = new SpiderLegPose(Rest.Hip, knee, ankle, target);
                if (!WithinLimits(pose)) continue;
                float score = Vector3.DistanceSquared(knee, previous.Knee) + Vector3.DistanceSquared(ankle, previous.Ankle) +
                    0.05f * Vector3.DistanceSquared(knee, Rest.Knee);
                if (score >= bestScore || accept?.Invoke(pose) == false) continue;
                result = pose;
                bestScore = score;
            }
        }
        return bestScore < float.MaxValue;
    }

    private bool WithinLimits(in SpiderLegPose p)
    {
        Vector3 upper = p.Knee - p.Hip, lower = p.Ankle - p.Knee;
        float knee = float.RadiansToDegrees(Angle(upper, lower));
        if (knee < Limits.MinimumKneeBendDegrees || knee > Limits.MaximumKneeBendDegrees ||
            float.RadiansToDegrees(Angle(upper, Rest.Knee - Rest.Hip)) > Limits.HipSwingDegrees ||
            float.RadiansToDegrees(Angle(Project(upper, Vector3.UnitY), Project(Rest.Knee - Rest.Hip, Vector3.UnitY))) > Limits.HipYawDegrees)
            return false;
        return L3 < Epsilon || float.RadiansToDegrees(Angle(lower, p.Tip - p.Ankle)) <= Limits.MaximumAnkleBendDegrees;
    }
}
