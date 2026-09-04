using System.Numerics;

namespace Fuse.Animation;

internal static class SpiderLocomotionMath
{
    public const float Epsilon = 1e-5f;
    public static bool Finite(Vector3 v) => float.IsFinite(v.LengthSquared());
    public static Vector3 Normal(Vector3 v, Vector3 fallback) =>
        Finite(v) && v.LengthSquared() > Epsilon * Epsilon ? Vector3.Normalize(v) :
        Finite(fallback) && fallback.LengthSquared() > Epsilon * Epsilon ? Vector3.Normalize(fallback) : Vector3.UnitY;
    public static Vector3 Project(Vector3 v, Vector3 n) => v - n * Vector3.Dot(v, n);
    public static Vector3 Point(Matrix4x4 m) => new(m.M14, m.M24, m.M34);
    public static float Angle(Vector3 a, Vector3 b) => MathF.Acos(System.Math.Clamp(Vector3.Dot(Normal(a, b), Normal(b, a)), -1f, 1f));

    public static Quaternion RotationBetween(Vector3 from, Vector3 to, Vector3 preferredAxis)
    {
        from = Normal(from, Vector3.UnitY);
        to = Normal(to, from);
        float dot = System.Math.Clamp(Vector3.Dot(from, to), -1f, 1f);
        if (dot > 0.999999f) return Quaternion.Identity;
        Vector3 axis = Vector3.Cross(from, to);
        if (dot < -0.99999f)
            axis = Project(preferredAxis, from);
        if (axis.LengthSquared() < Epsilon * Epsilon)
            axis = Vector3.Cross(from, MathF.Abs(from.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX);
        return Quaternion.CreateFromAxisAngle(Normal(axis, Vector3.UnitZ), MathF.Acos(dot));
    }

    public static Vector3 AngularVelocity(Quaternion previous, Quaternion current, float dt)
    {
        Quaternion delta = Quaternion.Normalize(current * Quaternion.Inverse(previous));
        if (delta.W < 0f) delta = -delta;
        Vector3 xyz = new(delta.X, delta.Y, delta.Z);
        float sine = xyz.Length();
        return sine < Epsilon || dt <= Epsilon ? Vector3.Zero :
            xyz / sine * (2f * MathF.Atan2(sine, System.Math.Clamp(delta.W, 0f, 1f)) / dt);
    }

    // Both horizontal and normal velocity vanish at lift-off and touchdown.
    public static Vector3 StepPoint(Vector3 start, Vector3 end, Vector3 startNormal, Vector3 endNormal, float lift, float t)
    {
        t = System.Math.Clamp(t, 0f, 1f);
        float u = t * t * t * (t * (t * 6f - 15f) + 10f);
        Vector3 n = Vector3.Transform(Normal(startNormal, endNormal),
            Quaternion.Slerp(Quaternion.Identity, RotationBetween(startNormal, endNormal, end - start), u));
        float arc = MathF.Sin(MathF.PI * t);
        return Vector3.Lerp(start, end, u) + n * (arc * arc * lift);
    }

    public static float SegmentDistanceSquared(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
    {
        Vector3 u = b - a, v = d - c, w = a - c;
        float aa = Vector3.Dot(u, u), bb = Vector3.Dot(u, v), cc = Vector3.Dot(v, v);
        float dd = Vector3.Dot(u, w), ee = Vector3.Dot(v, w);
        if (aa < Epsilon && cc < Epsilon) return w.LengthSquared();
        float s = aa < Epsilon ? 0f : System.Math.Clamp((bb * ee - cc * dd) / MathF.Max(Epsilon, aa * cc - bb * bb), 0f, 1f);
        float t = cc < Epsilon ? 0f : System.Math.Clamp((bb * s + ee) / cc, 0f, 1f);
        s = aa < Epsilon ? 0f : System.Math.Clamp((bb * t - dd) / aa, 0f, 1f);
        return Vector3.DistanceSquared(a + u * s, c + v * t);
    }

    public static bool ContainsSupport(ReadOnlySpan<Vector3> points, Vector3 center, Vector3 up, float margin)
    {
        if (points.Length < 3) return false;
        Vector3 x = Normal(Project(Vector3.UnitX, up), Project(Vector3.UnitZ, up));
        Vector3 y = Vector3.Cross(up, x);
        Span<Vector2> sorted = stackalloc Vector2[points.Length];
        for (int i = 0; i < points.Length; i++)
        {
            Vector3 p = points[i] - center;
            sorted[i] = new(Vector3.Dot(p, x), Vector3.Dot(p, y));
        }
        // Eight legs: insertion sort avoids a per-tick heap allocation.
        for (int i = 1; i < sorted.Length; i++)
            for (int j = i; j > 0 && (sorted[j].X < sorted[j - 1].X ||
                 sorted[j].X == sorted[j - 1].X && sorted[j].Y < sorted[j - 1].Y); j--)
                (sorted[j], sorted[j - 1]) = (sorted[j - 1], sorted[j]);
        Span<Vector2> hull = stackalloc Vector2[points.Length * 2];
        int count = 0;
        for (int i = 0; i < sorted.Length; i++)
        {
            while (count >= 2 && Cross(hull[count - 1] - hull[count - 2], sorted[i] - hull[count - 1]) <= 0f) count--;
            hull[count++] = sorted[i];
        }
        int lower = count;
        for (int i = sorted.Length - 2; i >= 0; i--)
        {
            while (count > lower && Cross(hull[count - 1] - hull[count - 2], sorted[i] - hull[count - 1]) <= 0f) count--;
            hull[count++] = sorted[i];
        }
        if (--count < 3) return false;
        for (int i = 0; i < count; i++)
        {
            Vector2 edge = hull[(i + 1) % count] - hull[i];
            if (Cross(edge, -hull[i]) < margin * edge.Length()) return false;
        }
        return true;
    }
    private static float Cross(Vector2 a, Vector2 b) => a.X * b.Y - a.Y * b.X;
}
