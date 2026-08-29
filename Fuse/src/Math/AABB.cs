using System.Numerics;

namespace Fuse.Math;

public struct AABB
{
    private Vector3 _extents;
    private Vector3 _center;
    private Vector3 _boundsMin;
    private Vector3 _boundsMax;

    private const float Large = 1e30f;

    public AABB()
    {
        _boundsMin = new Vector3(Large);
        _boundsMax = new Vector3(-Large);
        CalculateCenterAndExtents();
    }

    public AABB(Vector3 min, Vector3 max)
    {
        _boundsMin = min;
        _boundsMax = max;
        CalculateCenterAndExtents();
    }

    public readonly Vector3 GetCenter() => _center;
    public readonly Vector3 GetBoundsMin() => _boundsMin;
    public readonly Vector3 GetBoundsMax() => _boundsMax;
    public readonly Vector3 GetExtents() => _extents;
    public readonly Vector3 GetHalfExtents() => _extents * 0.5f;
    public readonly bool IsValid =>
        _boundsMin.X <= _boundsMax.X &&
        _boundsMin.Y <= _boundsMax.Y &&
        _boundsMin.Z <= _boundsMax.Z;

    public static AABB FromPoints(ReadOnlySpan<Vector3> points)
    {
        var bounds = new AABB();
        for (int i = 0; i < points.Length; i++)
            bounds.Grow(points[i]);
        return bounds;
    }

    public void Grow(AABB b)
    {
        if (b._boundsMin.X != Large && b._boundsMin.X != -Large)
        {
            Grow(b._boundsMin);
            Grow(b._boundsMax);
        }
        CalculateCenterAndExtents();
    }

    public void Grow(Vector3 p)
    {
        _boundsMin = Vector3.Min(_boundsMin, p);
        _boundsMax = Vector3.Max(_boundsMax, p);
        CalculateCenterAndExtents();
    }

    public readonly float Area()
    {
        Vector3 e = _boundsMax - _boundsMin;
        return 2.0f * (e.X * e.Y + e.Y * e.Z + e.Z * e.X);
    }

    public readonly bool ContainsPoint(Vector3 point) =>
        point.X >= _boundsMin.X && point.X <= _boundsMax.X &&
        point.Y >= _boundsMin.Y && point.Y <= _boundsMax.Y &&
        point.Z >= _boundsMin.Z && point.Z <= _boundsMax.Z;

    public readonly bool IntersectsSphere(Vector3 sphereCenter, float radius)
    {
        Vector3 closest = Vector3.Clamp(sphereCenter, _boundsMin, _boundsMax);
        float distSq = Vector3.DistanceSquared(closest, sphereCenter);
        return distSq <= radius * radius;
    }

    public readonly bool IntersectsAABB(AABB other) =>
        _boundsMin.X <= other._boundsMax.X && _boundsMax.X >= other._boundsMin.X &&
        _boundsMin.Y <= other._boundsMax.Y && _boundsMax.Y >= other._boundsMin.Y &&
        _boundsMin.Z <= other._boundsMax.Z && _boundsMax.Z >= other._boundsMin.Z;

    public readonly bool IntersectsAABB(AABB other, float threshold)
    {
        var inflatedMinA = _boundsMin - new Vector3(threshold);
        var inflatedMaxA = _boundsMax + new Vector3(threshold);
        var inflatedMinB = other._boundsMin - new Vector3(threshold);
        var inflatedMaxB = other._boundsMax + new Vector3(threshold);

        return inflatedMinA.X <= inflatedMaxB.X && inflatedMaxA.X >= inflatedMinB.X &&
               inflatedMinA.Y <= inflatedMaxB.Y && inflatedMaxA.Y >= inflatedMinB.Y &&
               inflatedMinA.Z <= inflatedMaxB.Z && inflatedMaxA.Z >= inflatedMinB.Z;
    }

    public readonly Vector3 NearestPointTo(Vector3 worldPosition) =>
        Vector3.Clamp(worldPosition, _boundsMin, _boundsMax);

    public readonly AABB Inflated(float amount)
    {
        if (!IsValid) return this;
        Vector3 padding = new(MathF.Max(0.0f, amount));
        return new AABB(_boundsMin - padding, _boundsMax + padding);
    }

    public readonly AABB Transformed(Matrix4x4 matrix)
    {
        if (!IsValid) return this;

        Span<Vector3> corners = stackalloc Vector3[8];
        GetCorners(corners);

        var result = new AABB();
        for (int i = 0; i < corners.Length; i++)
            result.Grow(Vector3.Transform(corners[i], matrix));
        return result;
    }

    public readonly void GetCorners(Span<Vector3> destination)
    {
        if (destination.Length < 8)
            throw new ArgumentException("AABB corner destination must contain at least 8 elements.", nameof(destination));

        destination[0] = new Vector3(_boundsMin.X, _boundsMin.Y, _boundsMin.Z);
        destination[1] = new Vector3(_boundsMax.X, _boundsMin.Y, _boundsMin.Z);
        destination[2] = new Vector3(_boundsMin.X, _boundsMax.Y, _boundsMin.Z);
        destination[3] = new Vector3(_boundsMax.X, _boundsMax.Y, _boundsMin.Z);
        destination[4] = new Vector3(_boundsMin.X, _boundsMin.Y, _boundsMax.Z);
        destination[5] = new Vector3(_boundsMax.X, _boundsMin.Y, _boundsMax.Z);
        destination[6] = new Vector3(_boundsMin.X, _boundsMax.Y, _boundsMax.Z);
        destination[7] = new Vector3(_boundsMax.X, _boundsMax.Y, _boundsMax.Z);
    }

    private void CalculateCenterAndExtents()
    {
        _center = (_boundsMin + _boundsMax) * 0.5f;
        _extents = _boundsMax - _boundsMin;
    }
}

public readonly struct BoundingSphere
{
    public BoundingSphere(Vector3 center, float radius)
    {
        Center = center;
        Radius = MathF.Max(0.0f, radius);
    }

    public Vector3 Center { get; }
    public float Radius { get; }

    public static BoundingSphere FromAABB(AABB bounds)
    {
        if (!bounds.IsValid)
            return new BoundingSphere(Vector3.Zero, 0.0f);

        Vector3 center = bounds.GetCenter();
        return new BoundingSphere(center, Vector3.Distance(center, bounds.GetBoundsMax()));
    }

    public BoundingSphere Transformed(Matrix4x4 matrix)
    {
        Vector3 transformedCenter = Vector3.Transform(Center, matrix);
        float scaleX = new Vector3(matrix.M11, matrix.M12, matrix.M13).Length();
        float scaleY = new Vector3(matrix.M21, matrix.M22, matrix.M23).Length();
        float scaleZ = new Vector3(matrix.M31, matrix.M32, matrix.M33).Length();
        return new BoundingSphere(transformedCenter, Radius * MathF.Max(scaleX, MathF.Max(scaleY, scaleZ)));
    }
}
