using System.Numerics;

namespace Fuse.Math;

public static class MathUtils
{
    public static float Lerp(float a, float b, float t)
    {
        return a + (b - a) * MathF.Max(0f, MathF.Min(1f, t));
    }

    public static float LerpUnclamped(float a, float b, float t)
    {
        return a + (b - a) * t;
    }

    public static Vector3 Lerp(Vector3 a, Vector3 b, float t)
    {
        t = MathF.Max(0f, MathF.Min(1f, t));
        return new Vector3(
            a.X + (b.X - a.X) * t,
            a.Y + (b.Y - a.Y) * t,
            a.Z + (b.Z - a.Z) * t);
    }

    public static Vector3 LerpUnclamped(Vector3 a, Vector3 b, float t)
    {
        return new Vector3(
            a.X + (b.X - a.X) * t,
            a.Y + (b.Y - a.Y) * t,
            a.Z + (b.Z - a.Z) * t);
    }

    public static Quaternion Slerp(Quaternion a, Quaternion b, float t)
    {
        t = MathF.Max(0f, MathF.Min(1f, t));
        return Quaternion.Slerp(a, b, t);
    }

    public static float SmoothStep(float a, float b, float t)
    {
        t = MathF.Max(0f, MathF.Min(1f, t));
        t = t * t * (3f - 2f * t);
        return a + (b - a) * t;
    }

    public static float InverseLerp(float a, float b, float value)
    {
        if (a == b) return 0f;
        return MathF.Max(0f, MathF.Min(1f, (value - a) / (b - a)));
    }

    public static float Remap(float value, float fromMin, float fromMax, float toMin, float toMax)
    {
        float t = InverseLerp(fromMin, fromMax, value);
        return Lerp(toMin, toMax, t);
    }

    public static float Damp(float current, float target, float smoothing, float dt)
    {
        return current + (target - current) * (1f - MathF.Exp(-smoothing * dt));
    }

    public static Vector3 Damp(Vector3 current, Vector3 target, float smoothing, float dt)
    {
        float factor = 1f - MathF.Exp(-smoothing * dt);
        return current + (target - current) * factor;
    }

    public static float Clamp(float value, float min, float max)
    {
        return MathF.Max(min, MathF.Min(max, value));
    }

    public static float Clamp01(float value)
    {
        return MathF.Max(0f, MathF.Min(1f, value));
    }
}