using System;
using System.Diagnostics;
using System.Numerics;
using Fuse.Core;

namespace Fuse.Enemy;

/// <summary>
/// Coordinates a moving target, pathfinding and the existing path follower.
/// It never moves the spider and it never runs A* as part of every frame.
/// </summary>
public sealed class SpiderNavigationController
{
    private const float Epsilon = 0.0001f;
    private const float ProgressEpsilon = 0.015f;

    private readonly SpiderPathfinding _pathfinding;
    private readonly SpiderPatrol _patrol;
    private readonly SpiderPathFollower _pathFollower;
    private SpiderPathSmoother? _pathSmoother;

    private SpiderNavGraph? _graph;
    private SpiderNavGraph? _pathGraph;
    private SpiderPath _currentPath = SpiderPath.Empty;
    private Vector3 _targetPosition;
    private Vector3 _lastPathTargetPosition;
    private float _timeSinceLastRepath;
    private float _timeWithoutProgress;
    private float _timeBlockedAtWaypoint;
    private float _lastWaypointDistance = float.PositiveInfinity;
    private int _lastWaypointIndex = -1;
    private int _pathGraphNodeCount = -1;
    private float _requiredClearance;
    private bool _hasTarget;
    private bool _hasAttemptedPath;
    private bool _targetNeedsRepath;
    private bool _graphNeedsRepath;
    private bool _hasValidPath;
    private bool _destinationReached;
    private bool _destinationMessageSent;
    private bool _missingGraphMessageSent;

    private float _minRepathInterval = 0.35f;
    private float _maxRepathInterval = 2.0f;
    private float _targetRepathDistance = 1.25f;
    private float _destinationReachDistance = 0.60f;
    private float _targetRepathHysteresis = 0.15f;
    private float _followerNoProgressTimeout = 0.90f;
    private float _waypointBlockedTimeout = 0.40f;
    private float _pathEquivalencePositionTolerance = 0.20f;
    private float _pathEquivalenceNormalToleranceDegrees = 8f;

    public SpiderNavigationController(
        SpiderPathfinding pathfinding,
        SpiderPatrol patrol,
        SpiderNavGraph? graph = null,
        SpiderPathSmoother? pathSmoother = null)
    {
        ArgumentNullException.ThrowIfNull(pathfinding);
        ArgumentNullException.ThrowIfNull(patrol);

        _pathfinding = pathfinding;
        _patrol = patrol;
        _pathFollower = patrol.PathFollower;
        _pathSmoother = pathSmoother;
        SetGraph(graph);
        RequiredClearance = patrol.Clearance;
    }

    /// <summary>
    /// Enables or disables this controller when it is updated directly or by
    /// SpiderPatrol's optional integration.
    /// </summary>
    public bool Enabled { get; set; } = true;

    public SpiderNavGraph? Graph => _graph;
    public SpiderPath CurrentPath => _currentPath;
    public SpiderPathFollower PathFollower => _pathFollower;
    public SpiderPathSmoother? PathSmoother
    {
        get => _pathSmoother;
        set => _pathSmoother = value;
    }
    public bool EnablePathSmoothing { get; set; }
    public Vector3 TargetPosition => _targetPosition;
    public Vector3 LastPathTargetPosition => _lastPathTargetPosition;
    public bool HasTarget => _hasTarget;
    public bool HasValidPath => _hasValidPath && _pathFollower.HasPath;
    public bool ReachedDestination => _destinationReached;
    public float TimeSinceLastRepath => _timeSinceLastRepath;

    /// <summary>
    /// Clearance required when resolving both ends of a path.
    /// </summary>
    public float RequiredClearance
    {
        get => _requiredClearance;
        set
        {
            ValidateNonNegativeFinite(value, nameof(value));
            _requiredClearance = value;
        }
    }

    public float MinRepathInterval
    {
        get => _minRepathInterval;
        set
        {
            ValidateNonNegativeFinite(value, nameof(value));
            if (value > _maxRepathInterval)
                throw new ArgumentOutOfRangeException(nameof(value), "MinRepathInterval cannot exceed MaxRepathInterval.");
            _minRepathInterval = value;
        }
    }

    public float MaxRepathInterval
    {
        get => _maxRepathInterval;
        set
        {
            ValidatePositiveFinite(value, nameof(value));
            if (value < _minRepathInterval)
                throw new ArgumentOutOfRangeException(nameof(value), "MaxRepathInterval cannot be below MinRepathInterval.");
            _maxRepathInterval = value;
        }
    }

    public float TargetRepathDistance
    {
        get => _targetRepathDistance;
        set
        {
            ValidatePositiveFinite(value, nameof(value));
            _targetRepathDistance = value;
        }
    }

    public float DestinationReachDistance
    {
        get => _destinationReachDistance;
        set
        {
            ValidatePositiveFinite(value, nameof(value));
            _destinationReachDistance = value;
        }
    }

    /// <summary>
    /// Extra distance required after the target threshold is crossed. Keeping
    /// this hysteresis prevents target updates around the threshold from
    /// alternately scheduling and cancelling repaths.
    /// </summary>
    public float TargetRepathHysteresis
    {
        get => _targetRepathHysteresis;
        set
        {
            ValidateNonNegativeFinite(value, nameof(value));
            _targetRepathHysteresis = value;
        }
    }

    public float FollowerNoProgressTimeout
    {
        get => _followerNoProgressTimeout;
        set
        {
            ValidatePositiveFinite(value, nameof(value));
            _followerNoProgressTimeout = value;
        }
    }

    public float WaypointBlockedTimeout
    {
        get => _waypointBlockedTimeout;
        set
        {
            ValidatePositiveFinite(value, nameof(value));
            _waypointBlockedTimeout = value;
        }
    }

    public float PathEquivalencePositionTolerance
    {
        get => _pathEquivalencePositionTolerance;
        set
        {
            ValidateNonNegativeFinite(value, nameof(value));
            _pathEquivalencePositionTolerance = value;
        }
    }

    public float PathEquivalenceNormalToleranceDegrees
    {
        get => _pathEquivalenceNormalToleranceDegrees;
        set
        {
            ValidateNonNegativeFinite(value, nameof(value));
            if (value > 180f)
                throw new ArgumentOutOfRangeException(nameof(value));
            _pathEquivalenceNormalToleranceDegrees = value;
        }
    }

    public int TotalRepaths { get; private set; }
    public int SuccessfulRepaths { get; private set; }
    public int FailedRepaths { get; private set; }
    public TimeSpan? LastPathfindingTime { get; private set; }
    public string LastRepathReason { get; private set; } = string.Empty;
    public int RawWaypointCount { get; private set; }
    public int SmoothedWaypointCount { get; private set; }
    public SpiderPath LastRawPath { get; private set; } = SpiderPath.Empty;
    public SpiderPath LastSmoothedPath { get; private set; } = SpiderPath.Empty;

    /// <summary>
    /// Installs the graph used by future queries. Passing a new generated
    /// graph schedules a repath while the old route remains usable during the
    /// configured minimum interval.
    /// </summary>
    public void SetGraph(SpiderNavGraph? graph)
    {
        if (ReferenceEquals(_graph, graph))
            return;

        _graph = graph;
        _graphNeedsRepath = true;
        _missingGraphMessageSent = false;
    }

    /// <summary>
    /// Use this when a graph was rebuilt in-place and its reference did not
    /// change.
    /// </summary>
    public void NotifyGraphChanged()
    {
        _graphNeedsRepath = true;
    }

    public void SetTarget(Vector3 targetPosition)
    {
        ValidateFinite(targetPosition, nameof(targetPosition));

        bool targetWasSet = _hasTarget;
        _targetPosition = targetPosition;
        _hasTarget = true;

        if (!targetWasSet)
        {
            _targetNeedsRepath = true;
            _destinationReached = false;
            _destinationMessageSent = false;
            return;
        }

        if (_destinationReached &&
            Vector3.DistanceSquared(_targetPosition, _lastPathTargetPosition) >
            _destinationReachDistance * _destinationReachDistance)
        {
            _destinationReached = false;
            _destinationMessageSent = false;
            _targetNeedsRepath = true;
        }

        if (!_hasAttemptedPath || !_hasValidPath)
        {
            _targetNeedsRepath = true;
            return;
        }

        float threshold = _targetRepathDistance + _targetRepathHysteresis;
        if (Vector3.DistanceSquared(_targetPosition, _lastPathTargetPosition) >= threshold * threshold)
            _targetNeedsRepath = true;
    }

    public void ClearTarget()
    {
        _hasTarget = false;
        _targetNeedsRepath = false;
        _destinationReached = false;
        _destinationMessageSent = false;
        _hasValidPath = false;
        _currentPath = SpiderPath.Empty;
        _pathFollower.ClearPath();
    }

    /// <summary>
    /// Updates timers and only invokes SpiderPathfinding when a repath reason
    /// is active and the minimum interval has elapsed.
    /// </summary>
    public void Update(float dt)
    {
        if (!Enabled || !_hasTarget || !_patrol.IsMovementReady)
            return;

        if (!float.IsFinite(dt) || dt <= 0f)
            return;

        dt = MathF.Min(dt, 0.25f);
        _timeSinceLastRepath += dt;

        Vector3 currentPosition = _patrol.CurrentPosition;
        Vector3 currentSurfaceNormal = _patrol.SurfaceNormal;
        ValidateFinite(currentPosition, "SpiderPatrol.CurrentPosition");

        _pathFollower.Update(currentPosition, currentSurfaceNormal);
        UpdateProgress(dt);

        if (Vector3.DistanceSquared(currentPosition, _targetPosition) <=
            _destinationReachDistance * _destinationReachDistance)
        {
            MarkDestinationReached();
            return;
        }

        string? reason = DetermineRepathReason();
        if (reason == null)
            return;

        LastRepathReason = reason;
        if (_hasAttemptedPath && _timeSinceLastRepath < _minRepathInterval)
            return;

        if (_graph == null)
        {
            if (!_missingGraphMessageSent)
            {
                Logger.Warn("[SpiderNavigation] Path request postponed: no navigation graph is assigned.");
                _missingGraphMessageSent = true;
            }

            return;
        }

        TryCalculatePath(reason, currentPosition, currentSurfaceNormal);
    }

    private string? DetermineRepathReason()
    {
        if (!_hasAttemptedPath)
            return "InitialPath";

        if (_graphNeedsRepath ||
            !ReferenceEquals(_graph, _pathGraph) ||
            _pathGraph != null && _pathGraphNodeCount != _pathGraph.Count)
        {
            return "GraphChanged";
        }

        if (!_hasValidPath || !_pathFollower.HasPath)
        {
            return _pathFollower.ReachedDestination
                ? "PathEndedBeforeDestination"
                : "NoValidPath";
        }

        if (_timeBlockedAtWaypoint >= _waypointBlockedTimeout)
            return "WaypointNoLongerReachable";

        if (_timeWithoutProgress >= _followerNoProgressTimeout)
            return "FollowerStalled";

        if (_targetNeedsRepath)
            return "TargetMoved";

        if (_timeSinceLastRepath >= _maxRepathInterval)
            return "MaxRepathInterval";

        return null;
    }

    private void TryCalculatePath(
        string reason,
        Vector3 currentPosition,
        Vector3 currentSurfaceNormal)
    {
        bool isRepath = _hasAttemptedPath;
        bool oldPathUsable = IsCurrentPathUsable();
        if (isRepath)
            TotalRepaths++;

        Logger.Info(
            $"[SpiderNavigation] {(isRepath ? "Repath" : "Initial path")} requested: " +
            $"reason={reason}, target={_targetPosition}.");

        Stopwatch timer = Stopwatch.StartNew();
        bool pathFound = false;
        SpiderPath newPath = SpiderPath.Empty;
        try
        {
            pathFound = _pathfinding.TryFindPath(
                _graph!,
                currentPosition,
                _targetPosition,
                _requiredClearance,
                currentSurfaceNormal,
                out newPath) && !newPath.IsEmpty;
        }
        catch (Exception exception)
        {
            Logger.Warn($"[SpiderNavigation] Path calculation threw an exception: {exception.Message}");
        }

        timer.Stop();
        LastPathfindingTime = timer.Elapsed;
        _hasAttemptedPath = true;
        _timeSinceLastRepath = 0f;

        if (!pathFound)
        {
            if (isRepath)
                FailedRepaths++;

            Logger.Warn(
                $"[SpiderNavigation] Path failed: reason={reason}, " +
                $"elapsed={LastPathfindingTime.Value.TotalMilliseconds:F2}ms.");

            _targetNeedsRepath = true;
            if (oldPathUsable)
            {
                _hasValidPath = true;
                Logger.Warn("[SpiderNavigation] Previous path retained because it is still usable.");
            }
            else
            {
                _hasValidPath = false;
                _currentPath = SpiderPath.Empty;
                _pathFollower.ClearPath();
            }

            return;
        }

        if (isRepath)
            SuccessfulRepaths++;

        SpiderPath pathToFollow = newPath;
        if (EnablePathSmoothing && _pathSmoother != null)
        {
            try
            {
                pathToFollow = _pathSmoother.Smooth(
                    _graph!,
                    newPath,
                    _requiredClearance);
            }
            catch (Exception exception)
            {
                // Smoothing is an optional optimization. A valid raw path is
                // still safe to follow if a custom smoother cannot run.
                Logger.Warn(
                    $"[SpiderNavigation] Path smoothing failed; raw path retained: {exception.Message}");
                pathToFollow = newPath;
            }
        }

        RawWaypointCount = newPath.Count;
        SmoothedWaypointCount = pathToFollow.Count;
        LastRawPath = newPath;
        LastSmoothedPath = pathToFollow;

        bool equivalent = oldPathUsable && ArePathsEquivalent(_currentPath, pathToFollow);
        if (!equivalent)
        {
            _pathFollower.ReplacePathPreservingProgress(
                pathToFollow,
                currentPosition,
                currentSurfaceNormal,
                _pathEquivalencePositionTolerance);
        }

        _currentPath = pathToFollow;
        _pathGraph = _graph;
        _pathGraphNodeCount = _graph!.Count;
        _graphNeedsRepath = false;
        _lastPathTargetPosition = _targetPosition;
        _targetNeedsRepath = false;
        _hasValidPath = _pathFollower.HasPath;

        Logger.Info(
            $"[SpiderNavigation] Path calculated: rawWaypoints={RawWaypointCount}, " +
            $"smoothedWaypoints={SmoothedWaypointCount}, " +
            $"cost={pathToFollow.TotalCost:F2}, " +
            $"elapsed={LastPathfindingTime.Value.TotalMilliseconds:F2}ms" +
            (equivalent ? "; follower progress preserved without restart." : "."));
    }

    private void UpdateProgress(float dt)
    {
        if (!_pathFollower.HasPath)
        {
            _timeWithoutProgress = 0f;
            _timeBlockedAtWaypoint = 0f;
            _lastWaypointIndex = -1;
            _lastWaypointDistance = float.PositiveInfinity;
            return;
        }

        int waypointIndex = _pathFollower.CurrentWaypointIndex;
        float waypointDistance = Vector3.Distance(
            _patrol.CurrentPosition,
            _pathFollower.CurrentTargetPosition);

        bool madeProgress = _lastWaypointIndex != waypointIndex ||
                            waypointDistance < _lastWaypointDistance - ProgressEpsilon;
        if (madeProgress || _lastWaypointIndex < 0)
            _timeWithoutProgress = 0f;
        else
            _timeWithoutProgress += dt;

        _lastWaypointIndex = waypointIndex;
        _lastWaypointDistance = waypointDistance;

        if (_patrol.IsBlocked && _pathFollower.DesiredDirection.LengthSquared() > Epsilon * Epsilon)
            _timeBlockedAtWaypoint += dt;
        else
            _timeBlockedAtWaypoint = MathF.Max(0f, _timeBlockedAtWaypoint - dt * 2f);
    }

    private bool IsCurrentPathUsable() =>
        _hasValidPath &&
        _pathFollower.HasPath &&
        !_pathFollower.ReachedDestination &&
        !_currentPath.IsEmpty;

    private bool ArePathsEquivalent(SpiderPath first, SpiderPath second)
    {
        if (first.IsEmpty || second.IsEmpty || first.Count != second.Count)
            return false;

        float positionToleranceSquared =
            _pathEquivalencePositionTolerance * _pathEquivalencePositionTolerance;
        float minimumNormalAgreement = MathF.Cos(
            _pathEquivalenceNormalToleranceDegrees * MathF.PI / 180f);

        for (int i = 0; i < first.Count; i++)
        {
            SpiderNavNode firstNode = first.Waypoints[i];
            SpiderNavNode secondNode = second.Waypoints[i];
            if (Vector3.DistanceSquared(firstNode.Position, secondNode.Position) > positionToleranceSquared)
                return false;

            if (Vector3.Dot(firstNode.SurfaceNormal, secondNode.SurfaceNormal) < minimumNormalAgreement)
                return false;
        }

        return true;
    }

    private void MarkDestinationReached()
    {
        if (_destinationReached)
            return;

        _destinationReached = true;
        _hasValidPath = false;
        _pathFollower.ClearPath();
        if (_destinationMessageSent)
            return;

        _destinationMessageSent = true;
        Logger.Info($"[SpiderNavigation] Destination reached: target={_targetPosition}.");
    }

    private static void ValidatePositiveFinite(float value, string parameterName)
    {
        if (!float.IsFinite(value) || value <= 0f)
            throw new ArgumentOutOfRangeException(parameterName);
    }

    private static void ValidateNonNegativeFinite(float value, string parameterName)
    {
        if (!float.IsFinite(value) || value < 0f)
            throw new ArgumentOutOfRangeException(parameterName);
    }

    private static void ValidateFinite(Vector3 value, string parameterName)
    {
        if (!float.IsFinite(value.X) ||
            !float.IsFinite(value.Y) ||
            !float.IsFinite(value.Z))
        {
            throw new ArgumentException("Vector must contain finite values.", parameterName);
        }
    }
}
