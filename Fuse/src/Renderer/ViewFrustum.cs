using System.Numerics;
using Fuse.Math;

namespace Fuse.Renderer;

/// <summary>
/// Frustum extracted from System.Numerics row-vector matrices (depth range 0..W).
/// </summary>
public readonly struct ViewFrustum
{
    private readonly Vector4 _left;
    private readonly Vector4 _right;
    private readonly Vector4 _bottom;
    private readonly Vector4 _top;
    private readonly Vector4 _near;
    private readonly Vector4 _far;

    public ViewFrustum(Matrix4x4 matrix)
    {
        _left = NormalizePlane(new Vector4(
            matrix.M11 + matrix.M14,
            matrix.M21 + matrix.M24,
            matrix.M31 + matrix.M34,
            matrix.M41 + matrix.M44));
        _right = NormalizePlane(new Vector4(
            matrix.M14 - matrix.M11,
            matrix.M24 - matrix.M21,
            matrix.M34 - matrix.M31,
            matrix.M44 - matrix.M41));
        _bottom = NormalizePlane(new Vector4(
            matrix.M12 + matrix.M14,
            matrix.M22 + matrix.M24,
            matrix.M32 + matrix.M34,
            matrix.M42 + matrix.M44));
        _top = NormalizePlane(new Vector4(
            matrix.M14 - matrix.M12,
            matrix.M24 - matrix.M22,
            matrix.M34 - matrix.M32,
            matrix.M44 - matrix.M42));
        _near = NormalizePlane(new Vector4(matrix.M13, matrix.M23, matrix.M33, matrix.M43));
        _far = NormalizePlane(new Vector4(
            matrix.M14 - matrix.M13,
            matrix.M24 - matrix.M23,
            matrix.M34 - matrix.M33,
            matrix.M44 - matrix.M43));
    }

    public bool Intersects(BoundingSphere sphere) =>
        IsInside(_left, sphere) && IsInside(_right, sphere) &&
        IsInside(_bottom, sphere) && IsInside(_top, sphere) &&
        IsInside(_near, sphere) && IsInside(_far, sphere);

    public bool Intersects(AABB bounds)
    {
        if (!bounds.IsValid) return true;
        Vector3 center = bounds.GetCenter();
        Vector3 extents = bounds.GetHalfExtents();
        return IsInside(_left, center, extents) && IsInside(_right, center, extents) &&
               IsInside(_bottom, center, extents) && IsInside(_top, center, extents) &&
               IsInside(_near, center, extents) && IsInside(_far, center, extents);
    }

    private static Vector4 NormalizePlane(Vector4 plane)
    {
        float length = new Vector3(plane.X, plane.Y, plane.Z).Length();
        return length > 1e-6f ? plane / length : plane;
    }

    private static bool IsInside(Vector4 plane, BoundingSphere sphere) =>
        Vector3.Dot(new Vector3(plane.X, plane.Y, plane.Z), sphere.Center) + plane.W >= -sphere.Radius;

    private static bool IsInside(Vector4 plane, Vector3 center, Vector3 extents)
    {
        Vector3 normal = new(plane.X, plane.Y, plane.Z);
        float projectedRadius = Vector3.Dot(Vector3.Abs(normal), extents);
        return Vector3.Dot(normal, center) + plane.W >= -projectedRadius;
    }
}
