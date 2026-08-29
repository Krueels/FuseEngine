using System;
using System.Numerics;
using Fuse.Scene;
using JoltPhysicsSharp;

namespace Fuse.Enemy;

/// <summary>
/// Resolves the physical surface beneath/around a pursuit target. It does
/// not build navigation data; it only converts a world target into a stable
/// collision point and surface normal that the spider planner can pursue.
/// </summary>
public sealed class SpiderTargetSurfaceResolver
{
    private const float Epsilon = 0.0001f;
    private const float PreferredNormalWeight = 0.35f;

    private readonly SceneManager _scene;

    public SpiderTargetSurfaceResolver(SceneManager scene)
    {
        _scene = scene ?? throw new ArgumentNullException(nameof(scene));
    }

    public bool TryResolve(
        Vector3 targetPosition,
        Vector3 preferredNormal,
        float searchDistance,
        out SpiderTargetSurface surface,
        BodyID? excludedBody = null)
    {
        surface = default;
        if (!IsFinite(targetPosition))
            return false;

        searchDistance = MathF.Max(0.5f, searchDistance);
        preferredNormal = NormalizeOrZero(preferredNormal);

        Vector3[] directions = BuildProbeDirections(
            preferredNormal,
            targetPosition);
        float halfDistance = searchDistance * 0.5f;
        float bestScore = float.MaxValue;

        foreach (Vector3 direction in directions)
        {
            Vector3 origin = targetPosition - direction * halfDistance;
            if (!_scene.Raycast(
                    origin,
                    direction,
                    searchDistance,
                    out SceneRaycastHit hit,
                    excludedBody,
                    collideWithBackFaces: true))
            {
                continue;
            }

            Vector3 normal = NormalizeOrZero(hit.Normal);
            if (normal.LengthSquared() <= Epsilon * Epsilon ||
                Vector3.Dot(normal, -direction) < 0.05f)
            {
                continue;
            }

            float targetDistance = Vector3.Distance(hit.Position, targetPosition);
            float normalPenalty = preferredNormal.LengthSquared() > Epsilon * Epsilon
                ? 1f - MathF.Max(-1f, Vector3.Dot(normal, preferredNormal))
                : 0f;
            float score = targetDistance + normalPenalty * PreferredNormalWeight;
            if (!float.IsFinite(score) || score >= bestScore)
                continue;

            bestScore = score;
            surface = new SpiderTargetSurface(
                true,
                hit.Position,
                normal,
                hit.BodyID,
                targetDistance);
        }

        return surface.IsValid;
    }

    private static Vector3[] BuildProbeDirections(
        Vector3 preferredNormal,
        Vector3 targetPosition)
    {
        if (preferredNormal.LengthSquared() > Epsilon * Epsilon &&
            BuildTangentBasis(
                preferredNormal,
                Vector3.Zero,
                out Vector3 forward,
                out Vector3 right))
        {
            return new[]
            {
                -preferredNormal,
                preferredNormal,
                forward,
                -forward,
                right,
                -right,
                NormalizeOrFallback(-preferredNormal + forward, -preferredNormal),
                NormalizeOrFallback(-preferredNormal - forward, -preferredNormal),
                NormalizeOrFallback(-preferredNormal + right, -preferredNormal),
                NormalizeOrFallback(-preferredNormal - right, -preferredNormal)
            };
        }

        // The player currently has no arbitrary-surface locomotion normal.
        // This fallback samples a symmetric frame around the target until a
        // real collision normal is found; the spider itself never branches on
        // a global axis.
        return new[]
        {
            Vector3.UnitY,
            -Vector3.UnitY,
            Vector3.UnitX,
            -Vector3.UnitX,
            Vector3.UnitZ,
            -Vector3.UnitZ
        };
    }

    private static bool BuildTangentBasis(
        Vector3 normal,
        Vector3 preferredForward,
        out Vector3 forward,
        out Vector3 right)
    {
        normal = NormalizeOrZero(normal);
        forward = NormalizeOrZero(
            ProjectOnPlane(preferredForward, normal));
        if (forward.LengthSquared() <= Epsilon * Epsilon)
            forward = BuildFallbackTangent(normal);

        right = NormalizeOrZero(Vector3.Cross(forward, normal));
        forward = NormalizeOrZero(Vector3.Cross(normal, right));
        return forward.LengthSquared() > Epsilon * Epsilon &&
               right.LengthSquared() > Epsilon * Epsilon;
    }

    private static Vector3 BuildFallbackTangent(Vector3 normal)
    {
        Vector3 reference = Vector3.UnitX;
        float smallestAlignment = MathF.Abs(Vector3.Dot(normal, reference));
        Vector3[] candidates = { Vector3.UnitY, Vector3.UnitZ };
        foreach (Vector3 candidate in candidates)
        {
            float alignment = MathF.Abs(Vector3.Dot(normal, candidate));
            if (alignment >= smallestAlignment)
                continue;

            reference = candidate;
            smallestAlignment = alignment;
        }

        return NormalizeOrZero(Vector3.Cross(normal, reference));
    }

    private static Vector3 ProjectOnPlane(Vector3 value, Vector3 normal) =>
        value - normal * Vector3.Dot(value, normal);

    private static Vector3 NormalizeOrFallback(Vector3 value, Vector3 fallback)
    {
        Vector3 normalized = NormalizeOrZero(value);
        return normalized.LengthSquared() > Epsilon * Epsilon
            ? normalized
            : NormalizeOrZero(fallback);
    }

    private static Vector3 NormalizeOrZero(Vector3 value) =>
        value.LengthSquared() > Epsilon * Epsilon
            ? Vector3.Normalize(value)
            : Vector3.Zero;

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) &&
        float.IsFinite(value.Y) &&
        float.IsFinite(value.Z);
}

public readonly struct SpiderTargetSurface
{
    public SpiderTargetSurface(
        bool isValid,
        Vector3 point,
        Vector3 normal,
        BodyID bodyId,
        float distance)
    {
        IsValid = isValid;
        Point = point;
        Normal = normal;
        BodyId = bodyId;
        Distance = distance;
    }

    public bool IsValid { get; }
    public Vector3 Point { get; }
    public Vector3 Normal { get; }
    public BodyID BodyId { get; }
    public float Distance { get; }
}
