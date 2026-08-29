using System;
using System.Numerics;
using Fuse.Debug;

namespace Fuse.Enemy;

/// <summary>
/// Converts a SpiderPath into the current movement intent for locomotion.
/// This class does not perform physics queries and does not control speed,
/// rotation or displacement.
/// </summary>
public sealed class SpiderPathFollower : IGizmoDrawable
{
    private const float Epsilon = 0.0001f;

    private static readonly Vector3 CurrentWaypointColor = new(0.10f, 1f, 0.25f);
    private static readonly Vector3 NextWaypointColor = new(1f, 0.65f, 0.10f);
    private static readonly Vector3 TargetNormalColor = new(1f, 0.85f, 0.10f);
    private static readonly Vector3 DesiredDirectionColor = new(0.10f, 0.75f, 1f);

    private SpiderPath _currentPath = SpiderPath.Empty;
    private int _currentWaypointIndex = -1;
    private Vector3 _lastPosition;
    private Vector3 _lastSurfaceNormal;

    public SpiderPathFollower()
    {
        DebugDrawer.Register(this);
    }

    public SpiderPath CurrentPath => _currentPath;
    public int CurrentWaypointIndex => _currentWaypointIndex;
    public Vector3 CurrentTargetPosition { get; private set; }
    public Vector3 CurrentTargetSurfaceNormal { get; private set; }
    public Vector3 DesiredDirection { get; private set; }
    public bool HasPath { get; private set; }
    public bool ReachedDestination { get; private set; }

    public float WaypointReachDistance
    {
        get => _waypointReachDistance;
        set
        {
            if (!float.IsFinite(value) || value <= 0f)
                throw new ArgumentOutOfRangeException(nameof(value));
            _waypointReachDistance = value;
        }
    }

    public bool DebugEnabled { get; set; } = true;
    public float DebugWaypointRadius { get; set; } = 0.12f;
    public float DebugNormalLength { get; set; } = 0.45f;
    public float DebugDirectionLength { get; set; } = 0.75f;

    private float _waypointReachDistance = 0.35f;

    /// <summary>
    /// Replaces the current path and starts at its first waypoint. The first
    /// waypoint is skipped on the next Update if it is already within the
    /// configured reach distance.
    /// </summary>
    public void SetPath(SpiderPath? path)
    {
        _currentPath = path ?? SpiderPath.Empty;
        _currentWaypointIndex = _currentPath.IsEmpty ? -1 : 0;
        HasPath = !_currentPath.IsEmpty;
        ReachedDestination = false;
        CurrentTargetPosition = Vector3.Zero;
        CurrentTargetSurfaceNormal = Vector3.Zero;
        DesiredDirection = Vector3.Zero;

        if (HasPath)
            SetCurrentTarget();
    }

    /// <summary>
    /// Replaces the path and immediately resolves the first useful waypoint
    /// against the current body position.
    /// </summary>
    public void SetPath(
        SpiderPath? path,
        Vector3 currentPosition,
        Vector3 currentSurfaceNormal)
    {
        SetPath(path);
        Update(currentPosition, currentSurfaceNormal);
    }

    /// <summary>
    /// Replaces a route without blindly sending the follower back to waypoint
    /// zero. When a waypoint near the previous target still exists, that
    /// waypoint is selected; otherwise the new route starts at its first
    /// waypoint. This keeps progress when a dynamic repath produces a nearly
    /// identical route without skipping an unrelated obstacle detour.
    /// </summary>
    public void ReplacePathPreservingProgress(
        SpiderPath? path,
        Vector3 currentPosition,
        Vector3 currentSurfaceNormal,
        float waypointMatchDistance = 0.75f)
    {
        if (!float.IsFinite(waypointMatchDistance) || waypointMatchDistance < 0f)
            throw new ArgumentOutOfRangeException(nameof(waypointMatchDistance));

        bool hadPath = HasPath;
        Vector3 previousTarget = CurrentTargetPosition;
        SetPath(path);

        if (hadPath && HasPath &&
            IsFinite(previousTarget) &&
            waypointMatchDistance > 0f)
        {
            float bestDistanceSquared = waypointMatchDistance * waypointMatchDistance;
            int bestIndex = -1;
            for (int i = 0; i < _currentPath.Waypoints.Count; i++)
            {
                float distanceSquared = Vector3.DistanceSquared(
                    previousTarget,
                    _currentPath.Waypoints[i].Position);
                if (distanceSquared > bestDistanceSquared)
                    continue;

                bestDistanceSquared = distanceSquared;
                bestIndex = i;
            }

            if (bestIndex >= 0)
            {
                _currentWaypointIndex = bestIndex;
                SetCurrentTarget();
            }
        }

        Update(currentPosition, currentSurfaceNormal);
    }

    public void ClearPath()
    {
        SetPath(null);
    }

    /// <summary>
    /// Advances reached waypoints and calculates a normalized tangent-space
    /// direction for the current target.
    /// </summary>
    public void Update(Vector3 currentPosition, Vector3 currentSurfaceNormal)
    {
        ValidatePosition(currentPosition, nameof(currentPosition));
        _lastPosition = currentPosition;

        Vector3 currentNormal = NormalizeOrZero(currentSurfaceNormal);
        if (currentNormal.LengthSquared() > Epsilon * Epsilon)
            _lastSurfaceNormal = currentNormal;

        if (!HasPath)
        {
            DesiredDirection = Vector3.Zero;
            return;
        }

        while (HasPath)
        {
            SpiderNavNode waypoint = _currentPath.Waypoints[_currentWaypointIndex];
            SetCurrentTarget();

            if (Vector3.DistanceSquared(currentPosition, waypoint.Position) >
                _waypointReachDistance * _waypointReachDistance)
            {
                Vector3 effectiveNormal = currentNormal.LengthSquared() > Epsilon * Epsilon
                    ? currentNormal
                    : NormalizeOrZero(waypoint.SurfaceNormal);
                if (effectiveNormal.LengthSquared() <= Epsilon * Epsilon)
                    effectiveNormal = _lastSurfaceNormal;

                DesiredDirection = CalculateDesiredDirection(
                    currentPosition,
                    effectiveNormal,
                    waypoint.Position,
                    waypoint.SurfaceNormal,
                    DesiredDirection);
                return;
            }

            if (_currentWaypointIndex >= _currentPath.Waypoints.Count - 1)
            {
                ReachedDestination = true;
                HasPath = false;
                DesiredDirection = Vector3.Zero;
                return;
            }

            _currentWaypointIndex++;
        }

        DesiredDirection = Vector3.Zero;
    }

    public void OnDrawGizmos(DebugDrawer drawer)
    {
        if (!DebugEnabled || _currentPath.IsEmpty || _currentWaypointIndex < 0)
            return;

        SpiderNavNode currentWaypoint = _currentPath.Waypoints[_currentWaypointIndex];
        drawer.DrawSphere(
            currentWaypoint.Position,
            Quaternion.Identity,
            DebugWaypointRadius,
            CurrentWaypointColor);
        drawer.PushLine(
            currentWaypoint.Position,
            currentWaypoint.Position + CurrentTargetSurfaceNormal * DebugNormalLength,
            TargetNormalColor);

        if (HasPath)
        {
            drawer.PushLine(
                _lastPosition,
                _lastPosition + DesiredDirection * DebugDirectionLength,
                DesiredDirectionColor);
        }

        int nextWaypointIndex = _currentWaypointIndex + 1;
        if (nextWaypointIndex >= _currentPath.Waypoints.Count)
            return;

        SpiderNavNode nextWaypoint = _currentPath.Waypoints[nextWaypointIndex];
        drawer.DrawSphere(
            nextWaypoint.Position,
            Quaternion.Identity,
            DebugWaypointRadius * 0.8f,
            NextWaypointColor);
        drawer.PushLine(currentWaypoint.Position, nextWaypoint.Position, NextWaypointColor);
    }

    private void SetCurrentTarget()
    {
        if (_currentWaypointIndex < 0 || _currentWaypointIndex >= _currentPath.Waypoints.Count)
            return;

        SpiderNavNode waypoint = _currentPath.Waypoints[_currentWaypointIndex];
        CurrentTargetPosition = waypoint.Position;
        CurrentTargetSurfaceNormal = NormalizeOrZero(waypoint.SurfaceNormal);
    }

    private static Vector3 CalculateDesiredDirection(
        Vector3 currentPosition,
        Vector3 currentSurfaceNormal,
        Vector3 targetPosition,
        Vector3 targetSurfaceNormal,
        Vector3 previousDirection)
    {
        Vector3 normal = NormalizeOrZero(currentSurfaceNormal);
        Vector3 targetNormal = NormalizeOrZero(targetSurfaceNormal);
        Vector3 toTarget = targetPosition - currentPosition;

        if (normal.LengthSquared() > Epsilon * Epsilon)
        {
            Vector3 tangentDirection = ProjectOnPlane(toTarget, normal);
            if (tangentDirection.LengthSquared() > Epsilon * Epsilon)
                return Vector3.Normalize(tangentDirection);

            // At a sharp transition the target vector can be almost entirely
            // along the current normal. The opposite target normal points
            // toward the destination surface while remaining tangent here.
            if (targetNormal.LengthSquared() > Epsilon * Epsilon)
            {
                tangentDirection = ProjectOnPlane(-targetNormal, normal);
                if (tangentDirection.LengthSquared() > Epsilon * Epsilon)
                    return Vector3.Normalize(tangentDirection);
            }

            tangentDirection = ProjectOnPlane(previousDirection, normal);
            if (tangentDirection.LengthSquared() > Epsilon * Epsilon)
                return Vector3.Normalize(tangentDirection);

            return BuildFallbackTangent(normal);
        }

        if (toTarget.LengthSquared() > Epsilon * Epsilon)
            return Vector3.Normalize(toTarget);

        return Vector3.Zero;
    }

    private static Vector3 ProjectOnPlane(Vector3 value, Vector3 normal) =>
        value - normal * Vector3.Dot(value, normal);

    private static Vector3 BuildFallbackTangent(Vector3 normal)
    {
        normal = NormalizeOrZero(normal);
        if (normal.LengthSquared() <= Epsilon * Epsilon)
            return Vector3.Zero;

        Vector3 reference = Vector3.UnitX;
        float smallestAlignment = MathF.Abs(Vector3.Dot(normal, reference));
        Vector3[] basisCandidates = { Vector3.UnitY, Vector3.UnitZ };
        foreach (Vector3 candidate in basisCandidates)
        {
            float alignment = MathF.Abs(Vector3.Dot(normal, candidate));
            if (alignment >= smallestAlignment)
                continue;

            reference = candidate;
            smallestAlignment = alignment;
        }

        return NormalizeOrZero(Vector3.Cross(normal, reference));
    }

    private static Vector3 NormalizeOrZero(Vector3 value)
    {
        return value.LengthSquared() > Epsilon * Epsilon
            ? Vector3.Normalize(value)
            : Vector3.Zero;
    }

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) &&
        float.IsFinite(value.Y) &&
        float.IsFinite(value.Z);

    private static void ValidatePosition(Vector3 position, string parameterName)
    {
        if (!float.IsFinite(position.X) ||
            !float.IsFinite(position.Y) ||
            !float.IsFinite(position.Z))
        {
            throw new ArgumentException("Position must contain finite values.", parameterName);
        }
    }
}
