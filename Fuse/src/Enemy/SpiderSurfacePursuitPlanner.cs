using System;
using System.Numerics;
using Fuse.Debug;
using JoltPhysicsSharp;

namespace Fuse.Enemy;

/// <summary>
/// Performs short-horizon surface decisions for pursuit. This is deliberately
/// not a NavMesh or a persistent navigation graph: it probes only the nearby
/// surfaces that the physics solver can actually reach.
/// </summary>
public sealed class SpiderSurfacePursuitPlanner : IGizmoDrawable
{
    private const float Epsilon = 0.0001f;
    private const float TransitionSearchDistance = 12f;
    private const float SurfaceAdvanceDistance = 3.0f;
    private const float SurfaceWaypointReachDistance = 0.30f;
    private const float DecisionInterval = 0.16f;
    private const float FailedSearchCooldown = 0.24f;
    private const float TargetSurfaceAlignmentThreshold = 0.72f;
    private const float TargetSurfaceDetourAllowance = 4.0f;

    private readonly SpiderSurfaceSolver _solver;

    private Vector3 _plannedDirection;
    private Vector3 _debugCurrentPosition;
    private Vector3 _debugTargetPosition;
    private Vector3 _plannedWaypointPosition;
    private Vector3 _plannedSourceNormal;
    private SpiderSurfaceContact _plannedTransition;
    private float _plannedClearance;
    private float _plannedScore = float.MaxValue;
    private float _decisionTimer;
    private float _searchCooldown;
    private bool _hasPlan;
    private bool _hasSurfaceWaypoint;

    public SpiderSurfacePursuitPlanner(SpiderSurfaceSolver solver)
    {
        _solver = solver ?? throw new ArgumentNullException(nameof(solver));
        DebugDrawer.Register(this);
    }

    public bool HasPlan => _hasPlan;
    public Vector3 PlannedDirection => _plannedDirection;
    public SpiderSurfaceContact PlannedTransition => _plannedTransition;

    public bool IsOnPlannedSourceSurface(Vector3 currentSurfaceNormal)
    {
        if (!_hasPlan)
            return false;

        Vector3 currentNormal = NormalizeOrZero(currentSurfaceNormal);
        return currentNormal.LengthSquared() > Epsilon * Epsilon &&
               _plannedSourceNormal.LengthSquared() > Epsilon * Epsilon &&
               Vector3.Dot(currentNormal, _plannedSourceNormal) >= 0.88f;
    }

    /// <summary>
    /// Resolves the current steering direction toward the selected transition
    /// point. Unlike PlannedDirection, this is recomputed from the spider's
    /// current position and surface every frame, so it behaves as a waypoint
    /// instead of a permanently latched world-space direction.
    /// </summary>
    public bool TryGetSteeringDirection(
        Vector3 currentPosition,
        Vector3 currentSurfaceNormal,
        out Vector3 direction,
        out float distance)
    {
        direction = Vector3.Zero;
        distance = float.MaxValue;
        if (!_hasPlan)
            return false;

        SpiderSurfaceContact refreshed = _solver.Refresh(_plannedTransition);
        if (_plannedTransition.IsValid && refreshed.IsValid)
            _plannedTransition = refreshed;

        Vector3 currentNormal = NormalizeOrZero(currentSurfaceNormal);
        if (currentNormal.LengthSquared() <= Epsilon * Epsilon)
            return false;

        if (_hasSurfaceWaypoint)
        {
            Vector3 toWaypoint = _plannedWaypointPosition - currentPosition;
            distance = toWaypoint.Length();
            direction = NormalizeOrZero(
                ProjectOnPlane(toWaypoint, currentNormal));
            return direction.LengthSquared() > Epsilon * Epsilon;
        }

        Vector3 plannedNormal = NormalizeOrZero(_plannedTransition.Normal);
        if (plannedNormal.LengthSquared() <= Epsilon * Epsilon ||
            !_plannedTransition.IsValid)
        {
            return false;
        }

        Vector3 transitionCenter =
            _plannedTransition.Point + plannedNormal * _plannedClearance;
        Vector3 toTransition = transitionCenter - currentPosition;
        distance = toTransition.Length();
        if (!float.IsFinite(distance))
        {
            distance = float.MaxValue;
            return false;
        }

        direction = NormalizeOrZero(
            ProjectOnPlane(toTransition, currentNormal));
        if (direction.LengthSquared() <= Epsilon * Epsilon &&
            IsOnPlannedSourceSurface(currentNormal))
        {
            // When waiting at the edge for the post-transition lock to end,
            // preserve a small committed direction into the chosen surface.
            direction = NormalizeOrZero(
                ProjectOnPlane(_plannedDirection, currentNormal));
        }
        return direction.LengthSquared() > Epsilon * Epsilon;
    }

    /// <summary>
    /// Returns true after locomotion has reached the surface selected by the
    /// local plan. The planned direction belongs to the previous surface and
    /// must not remain active after this point.
    /// </summary>
    public bool IsPlannedSurfaceReached(Vector3 currentSurfaceNormal)
    {
        if (!_hasPlan)
            return false;

        Vector3 currentNormal = NormalizeOrZero(currentSurfaceNormal);
        if (_hasSurfaceWaypoint)
        {
            return IsOnPlannedSourceSurface(currentNormal) &&
                   Vector3.Distance(
                       _plannedWaypointPosition,
                       _debugCurrentPosition) <= SurfaceWaypointReachDistance;
        }

        if (!_plannedTransition.IsValid)
            return false;

        Vector3 plannedNormal = NormalizeOrZero(_plannedTransition.Normal);
        return currentNormal.LengthSquared() > Epsilon * Epsilon &&
               plannedNormal.LengthSquared() > Epsilon * Epsilon &&
               Vector3.Dot(currentNormal, plannedNormal) >= 0.88f;
    }

    /// <summary>
    /// Returns the direction of the current local plan. Physics probing is
    /// performed only when allowSearch is true and the decision interval has
    /// elapsed. An existing plan is returned during the cooldown.
    /// </summary>
    public bool TryGetDirection(
        float dt,
        Vector3 currentPosition,
        Vector3 currentNormal,
        Vector3 preferredForward,
        Vector3 targetPosition,
        float clearance,
        BodyID selfBody,
        bool allowSearch,
        out Vector3 direction,
        Vector3 targetSurfaceNormal = default)
    {
        dt = System.Math.Clamp(dt, 0.0001f, 0.05f);
        _decisionTimer = MathF.Max(0f, _decisionTimer - dt);
        _searchCooldown = MathF.Max(0f, _searchCooldown - dt);
        _debugCurrentPosition = currentPosition;
        _debugTargetPosition = targetPosition;
        _plannedClearance = MathF.Max(0.01f, clearance);

        if (_hasPlan)
        {
            // A selected surface is a committed local step. Re-evaluating all
            // candidates while approaching a corner makes equally good walls
            // replace one another every few frames.
            return TryGetSteeringDirection(
                currentPosition,
                currentNormal,
                out direction,
                out _);
        }

        if (!allowSearch ||
            _decisionTimer > 0f ||
            _searchCooldown > 0f)
        {
            direction = Vector3.Zero;
            return false;
        }

        if (TryFindBestTransition(
                currentPosition,
                currentNormal,
                preferredForward,
                targetPosition,
                targetSurfaceNormal,
                _plannedClearance,
                selfBody,
                out Vector3 bestDirection,
                out SpiderSurfaceContact bestTransition,
                out float bestScore))
        {
            _plannedDirection = bestDirection;
            _hasSurfaceWaypoint = false;
            _plannedWaypointPosition = Vector3.Zero;
            _plannedSourceNormal = NormalizeOrZero(currentNormal);
            _plannedTransition = bestTransition;
            _plannedScore = bestScore;
            _hasPlan = true;

            _decisionTimer = DecisionInterval;
            _searchCooldown = 0f;
            return TryGetSteeringDirection(
                currentPosition,
                currentNormal,
                out direction,
                out _);
        }

        if (TryCreateSurfaceWaypoint(
                currentPosition,
                currentNormal,
                preferredForward,
                targetPosition))
        {
            _decisionTimer = DecisionInterval;
            _searchCooldown = 0f;
            return TryGetSteeringDirection(
                currentPosition,
                currentNormal,
                out direction,
                out _);
        }

        // A failed search never destroys a route that may still be usable.
        // The caller can keep trying its previous direction after this retry
        // cooldown expires.
        _decisionTimer = FailedSearchCooldown;
        _searchCooldown = FailedSearchCooldown;
        return TryGetSteeringDirection(
            currentPosition,
            currentNormal,
            out direction,
            out _);
    }

    public void ClearPlan()
    {
        _hasPlan = false;
        _hasSurfaceWaypoint = false;
        _plannedDirection = Vector3.Zero;
        _plannedWaypointPosition = Vector3.Zero;
        _plannedSourceNormal = Vector3.Zero;
        _plannedTransition = default;
        _plannedScore = float.MaxValue;
        _decisionTimer = 0f;
        _searchCooldown = 0f;
    }

    public void AbandonPlan()
    {
        ClearPlan();
        _decisionTimer = FailedSearchCooldown;
        _searchCooldown = FailedSearchCooldown;
    }

    private bool TryCreateSurfaceWaypoint(
        Vector3 currentPosition,
        Vector3 currentNormal,
        Vector3 preferredForward,
        Vector3 targetPosition)
    {
        currentNormal = NormalizeOrZero(currentNormal);
        if (currentNormal.LengthSquared() <= Epsilon * Epsilon)
            return false;

        Vector3 direction = NormalizeOrZero(
            ProjectOnPlane(targetPosition - currentPosition, currentNormal));
        if (direction.LengthSquared() <= Epsilon * Epsilon)
        {
            direction = NormalizeOrZero(
                ProjectOnPlane(preferredForward, currentNormal));
        }
        if (direction.LengthSquared() <= Epsilon * Epsilon)
            direction = BuildFallbackTangent(currentNormal, preferredForward);
        if (direction.LengthSquared() <= Epsilon * Epsilon)
            return false;

        _plannedDirection = direction;
        _plannedWaypointPosition = currentPosition +
                                    direction * SurfaceAdvanceDistance;
        _plannedSourceNormal = currentNormal;
        _plannedTransition = default;
        _plannedScore = Vector3.Distance(
            _plannedWaypointPosition,
            targetPosition);
        _hasSurfaceWaypoint = true;
        _hasPlan = true;
        return true;
    }

    private bool TryFindBestTransition(
        Vector3 currentPosition,
        Vector3 currentNormal,
        Vector3 preferredForward,
        Vector3 targetPosition,
        Vector3 targetSurfaceNormal,
        float clearance,
        BodyID selfBody,
        out Vector3 bestDirection,
        out SpiderSurfaceContact bestTransition,
        out float bestScore)
    {
        return TryFindBestTransitionAtDistance(
            currentPosition,
            currentNormal,
            preferredForward,
            targetPosition,
            targetSurfaceNormal,
            clearance,
            selfBody,
            TransitionSearchDistance,
            out bestDirection,
            out bestTransition,
            out bestScore);
    }

    private bool TryFindBestTransitionAtDistance(
        Vector3 currentPosition,
        Vector3 currentNormal,
        Vector3 preferredForward,
        Vector3 targetPosition,
        Vector3 targetSurfaceNormal,
        float clearance,
        BodyID selfBody,
        float lookAhead,
        out Vector3 bestDirection,
        out SpiderSurfaceContact bestTransition,
        out float bestScore)
    {
        currentNormal = NormalizeOrZero(currentNormal);
        targetSurfaceNormal = NormalizeOrZero(targetSurfaceNormal);
        if (currentNormal.LengthSquared() <= Epsilon * Epsilon ||
            !BuildTangentBasis(
                currentNormal,
                preferredForward,
                out Vector3 forward,
                out Vector3 right))
        {
            bestDirection = Vector3.Zero;
            bestTransition = default;
            bestScore = float.MaxValue;
            return false;
        }

        Vector3[] directions =
        {
            forward,
            -forward,
            right,
            -right,
            NormalizeOrFallback(forward + right, forward),
            NormalizeOrFallback(forward - right, forward),
            NormalizeOrFallback(-forward + right, -forward),
            NormalizeOrFallback(-forward - right, -forward)
        };

        bestDirection = Vector3.Zero;
        bestTransition = default;
        bestScore = float.MaxValue;
        float bestTransitionDistance = float.MaxValue;

        foreach (Vector3 candidateDirection in directions)
        {
            if (candidateDirection.LengthSquared() <= Epsilon * Epsilon ||
                !_solver.TryFindTransitionContact(
                    currentPosition,
                    currentNormal,
                    candidateDirection,
                    clearance,
                    lookAhead,
                    selfBody,
                    out SpiderSurfaceContact candidate))
            {
                continue;
            }

            Vector3 nextNormal = NormalizeOrZero(candidate.Normal);
            if (nextNormal.LengthSquared() <= Epsilon * Epsilon ||
                !IsFinite(candidate.Point) ||
                !IsFinite(nextNormal))
            {
                continue;
            }

            Vector3 nextCenter = candidate.Point + nextNormal * clearance;
            Vector3 routeDirection = NormalizeOrZero(
                ProjectOnPlane(nextCenter - currentPosition, currentNormal));
            if (routeDirection.LengthSquared() <= Epsilon * Epsilon)
                routeDirection = candidateDirection;

            float targetDistance = Vector3.Distance(nextCenter, targetPosition);
            float transitionDistance = Vector3.Distance(currentPosition, nextCenter);
            float normalChange = 1f - System.Math.Clamp(
                Vector3.Dot(currentNormal, nextNormal),
                -1f,
                1f);
            float targetSurfaceAlignment = targetSurfaceNormal.LengthSquared() >
                    Epsilon * Epsilon
                ? Vector3.Dot(nextNormal, targetSurfaceNormal)
                : 0f;
            bool reachesTargetSurface =
                targetSurfaceNormal.LengthSquared() > Epsilon * Epsilon &&
                targetSurfaceAlignment >= TargetSurfaceAlignmentThreshold;
            bool bestReachesTargetSurface =
                targetSurfaceNormal.LengthSquared() > Epsilon * Epsilon &&
                Vector3.Dot(
                    NormalizeOrZero(bestTransition.Normal),
                    targetSurfaceNormal) >= TargetSurfaceAlignmentThreshold;

            if (!float.IsFinite(targetDistance) ||
                !float.IsFinite(transitionDistance) ||
                !float.IsFinite(normalChange))
            {
                continue;
            }

            float score = targetDistance +
                          transitionDistance * 0.35f +
                          normalChange * MathF.Max(0.5f, clearance * 0.55f) +
                          (targetSurfaceNormal.LengthSquared() > Epsilon * Epsilon
                              ? (1f - targetSurfaceAlignment) * 1.5f
                              : 0f);

            // Once the spider is on an intermediate face, a candidate leading
            // to the player's surface is preferable to another local face.
            // The detour allowance prevents this preference from selecting a
            // remote or impractical transition, while still allowing the
            // required wall -> ground step.
            if (targetSurfaceNormal.LengthSquared() > Epsilon * Epsilon &&
                reachesTargetSurface != bestReachesTargetSurface)
            {
                if (reachesTargetSurface &&
                    transitionDistance <= bestTransitionDistance +
                    TargetSurfaceDetourAllowance)
                {
                    bestDirection = routeDirection;
                    bestTransition = candidate;
                    bestScore = score;
                    bestTransitionDistance = transitionDistance;
                    continue;
                }

                if (bestReachesTargetSurface)
                    continue;
            }

            bool isCloserTransition =
                transitionDistance < bestTransitionDistance - 0.35f;
            bool isComparableTransition =
                MathF.Abs(transitionDistance - bestTransitionDistance) <= 0.35f;
            if (!isCloserTransition &&
                (!isComparableTransition || score >= bestScore))
                continue;

            // The probe direction is only how the candidate was discovered.
            // The actual movement must point to the resolved transition center;
            // at convex corners these two directions are not necessarily equal.
            bestDirection = routeDirection;
            bestTransition = candidate;
            bestScore = score;
            bestTransitionDistance = transitionDistance;
        }

        return bestDirection.LengthSquared() > Epsilon * Epsilon &&
               bestTransition.IsValid;
    }

    private static bool BuildTangentBasis(
        Vector3 normal,
        Vector3 preferredForward,
        out Vector3 forward,
        out Vector3 right)
    {
        normal = NormalizeOrZero(normal);
        forward = NormalizeOrZero(ProjectOnPlane(preferredForward, normal));
        if (forward.LengthSquared() <= Epsilon * Epsilon)
            forward = BuildFallbackTangent(normal, preferredForward);

        right = NormalizeOrZero(Vector3.Cross(forward, normal));
        forward = NormalizeOrZero(Vector3.Cross(normal, right));
        return forward.LengthSquared() > Epsilon * Epsilon &&
               right.LengthSquared() > Epsilon * Epsilon;
    }

    private static Vector3 BuildFallbackTangent(Vector3 normal, Vector3 preferred)
    {
        normal = NormalizeOrZero(normal);
        if (normal.LengthSquared() <= Epsilon * Epsilon)
            return Vector3.Zero;

        Vector3 tangent = NormalizeOrZero(ProjectOnPlane(preferred, normal));
        if (tangent.LengthSquared() > Epsilon * Epsilon)
            return tangent;

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

    public void OnDrawGizmos(DebugDrawer drawer)
    {
        if (!_hasPlan ||
            !IsFinite(_debugCurrentPosition) ||
            !IsFinite(_debugTargetPosition))
        {
            return;
        }

        Vector3 normal = NormalizeOrZero(_plannedTransition.Normal);
        Vector3 transitionCenter = _plannedTransition.IsValid
            ? _plannedTransition.Point + normal * _plannedClearance
            : _plannedWaypointPosition.LengthSquared() > Epsilon * Epsilon
                ? _plannedWaypointPosition
                : _debugCurrentPosition + _plannedDirection * 1.5f;

        if (!IsFinite(transitionCenter))
            return;

        // Cyan: segment currently being used to reach the selected surface.
        drawer.PushLine(
            _debugCurrentPosition,
            transitionCenter,
            new Vector3(0.1f, 1f, 1f));
        drawer.DrawSphere(
            transitionCenter,
            Quaternion.Identity,
            0.15f,
            new Vector3(0.1f, 1f, 0.35f));

        // Yellow: remaining pursuit intent after the local transition. This
        // line is diagnostic; the motor still validates the actual movement.
        drawer.PushLine(
            transitionCenter,
            _debugTargetPosition,
            new Vector3(1f, 0.85f, 0.1f));

        if (normal.LengthSquared() > Epsilon * Epsilon)
        {
            drawer.PushLine(
                transitionCenter,
                transitionCenter + normal * 0.75f,
                new Vector3(1f, 0.2f, 0.85f));
        }
    }
}
