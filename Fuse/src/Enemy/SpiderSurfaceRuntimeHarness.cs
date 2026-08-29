using System;
using System.Collections.Generic;
using System.Numerics;
using Fuse.Core;
using Fuse.Debug;
using Fuse.Physics;

namespace Fuse.Enemy;

/// <summary>
/// Runtime scenarios used to validate surface-relative locomotion in a real
/// physics scene. The harness does not create geometry; the caller places the
/// spider at the configured start positions in the test scene.
/// </summary>
public enum SpiderSurfaceRuntimeScenario
{
    FloorPositiveY,
    CeilingNegativeY,
    WallPositiveX,
    WallNegativeX,
    WallPositiveZ,
    WallNegativeZ,
    FloorToWall,
    WallToCeiling,
    ConvexCorner,
    ConcaveCorner
}

public readonly struct SpiderSurfaceRuntimeScenarioDefinition
{
    public SpiderSurfaceRuntimeScenarioDefinition(
        string name,
        Vector3 initialNormal,
        Vector3 expectedFinalNormal,
        Vector3 travelDirection,
        bool isTransition)
    {
        Name = name;
        InitialNormal = initialNormal;
        ExpectedFinalNormal = expectedFinalNormal;
        TravelDirection = travelDirection;
        IsTransition = isTransition;
    }

    public string Name { get; }
    public Vector3 InitialNormal { get; }
    public Vector3 ExpectedFinalNormal { get; }
    public Vector3 TravelDirection { get; }
    public bool IsTransition { get; }
}

/// <summary>
/// A single diagnostic snapshot. It is deliberately public so an external
/// test UI can plot the values without adding logging to the simulation loop.
/// </summary>
public readonly struct SpiderSurfaceRuntimeSample
{
    public SpiderSurfaceRuntimeSample(
        SpiderSurfaceRuntimeScenario scenario,
        float elapsed,
        Vector3 position,
        Vector3 surfaceNormal,
        Vector3 desiredSurfaceNormal,
        Vector3 forward,
        Vector3 right,
        Vector3 desiredDirection,
        Vector3 velocity,
        Quaternion rotation,
        Vector3 surfacePoint,
        float surfaceDistance,
        bool hasContact,
        bool isBlocked,
        int currentWaypointIndex,
        float waypointDistance,
        float currentSpeed)
    {
        Scenario = scenario;
        Elapsed = elapsed;
        Position = position;
        SurfaceNormal = surfaceNormal;
        DesiredSurfaceNormal = desiredSurfaceNormal;
        Forward = forward;
        Right = right;
        DesiredDirection = desiredDirection;
        Velocity = velocity;
        Rotation = rotation;
        SurfacePoint = surfacePoint;
        SurfaceDistance = surfaceDistance;
        HasContact = hasContact;
        IsBlocked = isBlocked;
        CurrentWaypointIndex = currentWaypointIndex;
        WaypointDistance = waypointDistance;
        CurrentSpeed = currentSpeed;
    }

    public SpiderSurfaceRuntimeScenario Scenario { get; }
    public float Elapsed { get; }
    public Vector3 Position { get; }
    public Vector3 SurfaceNormal { get; }
    public Vector3 DesiredSurfaceNormal { get; }
    public Vector3 Forward { get; }
    public Vector3 Right { get; }
    public Vector3 DesiredDirection { get; }
    public Vector3 Velocity { get; }
    public Quaternion Rotation { get; }
    public Vector3 SurfacePoint { get; }
    public float SurfaceDistance { get; }
    public bool HasContact { get; }
    public bool IsBlocked { get; }
    public int CurrentWaypointIndex { get; }
    public float WaypointDistance { get; }
    public float CurrentSpeed { get; }
}

/// <summary>
/// Runtime validator for SpiderSurfaceMotor and SpiderSurfaceSolver.
///
/// Use Update(dt) when this harness owns the patrol tick. When the normal game
/// loop already updates the enemy, call BeginPhysicsStep(dt), then update the
/// enemy, then call EndPhysicsStep(dt). Both modes use the same optional patrol
/// input override and therefore never advance the motor twice in one step.
/// </summary>
public sealed class SpiderSurfaceRuntimeHarness : IGizmoDrawable, IDisposable
{
    private const float Epsilon = 0.0001f;
    private const float TransitionDetectionDegrees = 8f;
    private const float MaxAbruptNormalChangeDegrees = 35f;
    private const float MaxForwardFlipDot = -0.86f;
    private const float ContactLossGrace = 0.08f;
    private const float StallGrace = 0.35f;
    private const float StallSpeed = 0.05f;
    private const float WaypointProgressEpsilon = 0.03f;
    private const float WaypointStallTimeout = 4f;
    private const float ExpectedNormalToleranceDegrees = 25f;
    private const float ExpectedNormalWarmup = 0.35f;
    private const float OscillationWindow = 2f;
    private const float OscillationTransitionDegrees = 18f;
    private const float OscillationSameSurfaceDegrees = 25f;
    private const float MirrorVectorTolerance = 0.20f;
    private const int MaximumCapturedSamples = 4096;

    private static readonly SpiderSurfaceRuntimeScenario[] AllScenarioValues =
    {
        SpiderSurfaceRuntimeScenario.FloorPositiveY,
        SpiderSurfaceRuntimeScenario.CeilingNegativeY,
        SpiderSurfaceRuntimeScenario.WallPositiveX,
        SpiderSurfaceRuntimeScenario.WallNegativeX,
        SpiderSurfaceRuntimeScenario.WallPositiveZ,
        SpiderSurfaceRuntimeScenario.WallNegativeZ,
        SpiderSurfaceRuntimeScenario.FloorToWall,
        SpiderSurfaceRuntimeScenario.WallToCeiling,
        SpiderSurfaceRuntimeScenario.ConvexCorner,
        SpiderSurfaceRuntimeScenario.ConcaveCorner
    };

    private readonly SpiderEnemy _spider;
    private readonly SpiderPatrol _patrol;
    private readonly SpiderSurfaceMotor _motor;
    private readonly PhysicsWorld? _physics;
    private readonly Dictionary<SpiderSurfaceRuntimeScenario, RuntimeScenarioConfiguration> _configurations = new();
    private readonly Dictionary<SpiderSurfaceRuntimeScenario, List<SpiderSurfaceRuntimeSample>> _capturedSamples = new();
    private readonly HashSet<string> _activeAnomalies = new();

    private SpiderSurfaceRuntimeSample _previousSample;
    private bool _hasPreviousSample;
    private bool _scenarioStarted;
    private float _scenarioElapsed;
    private float _totalElapsed;
    private float _transitionLossElapsed;
    private float _stallElapsed;
    private float _oscillationElapsed;
    private int _oscillationCount;
    private Vector3 _lastObservedDesiredNormal;
    private Vector3 _lastTransitionNormal;
    private Vector3 _previousTransitionNormal;
    private bool _hasObservedDesiredNormal;
    private bool _hasLastTransitionNormal;
    private bool _hasPreviousTransitionNormal;
    private int _lastWaypointIndex = -1;
    private float _bestWaypointDistance = float.MaxValue;
    private float _waypointNoProgressElapsed;
    private bool _disposed;

    public SpiderSurfaceRuntimeHarness(SpiderEnemy spider, PhysicsWorld? physics = null)
    {
        ArgumentNullException.ThrowIfNull(spider);
        _spider = spider;
        _physics = physics;
        _patrol = spider.NavigationPatrol ??
            throw new InvalidOperationException("Spider must be initialized before creating the runtime harness.");
        _motor = spider.SurfaceMotor ??
            throw new InvalidOperationException("Spider surface motor is not initialized.");

        Vector3 defaultStart = _motor.Character.Position;
        foreach (SpiderSurfaceRuntimeScenario scenario in AllScenarioValues)
        {
            SpiderSurfaceRuntimeScenarioDefinition definition = GetDefinition(scenario);
            _configurations[scenario] = new RuntimeScenarioConfiguration(
                defaultStart,
                definition.InitialNormal,
                definition.TravelDirection,
                definition.ExpectedFinalNormal,
                true);
        }

        Scenario = SpiderSurfaceRuntimeScenario.FloorPositiveY;
        DebugDrawer.Register(this);
    }

    public static IReadOnlyList<SpiderSurfaceRuntimeScenario> AllScenarios => AllScenarioValues;

    public SpiderSurfaceRuntimeScenario Scenario { get; private set; }
    public int ScenarioIndex => (int)Scenario;
    public bool Enabled { get; set; } = true;
    public bool AutoRunScenarios { get; set; }
    public bool LoopScenarios { get; set; }
    public bool DrivePatrolFromUpdate { get; set; } = true;
    public bool ResetMotorOnScenarioStart { get; set; } = true;
    public bool CaptureSamples { get; set; } = true;
    public bool DebugEnabled { get; set; } = true;
    public float MoveSpeed { get; set; } = 2f;
    public float ScenarioDuration { get; set; } = 3f;
    public float SurfaceDistanceTolerance { get; set; } = 0.75f;
    public float WaypointTimeout { get; set; } = WaypointStallTimeout;
    public SpiderSurfaceRuntimeSample LastSample { get; private set; }
    public bool HasSample { get; private set; }
    public string LastStatus { get; private set; } = "Runtime harness has not started.";
    public IReadOnlyCollection<string> ActiveAnomalies => _activeAnomalies;

    /// <summary>Returns the fixed geometric preset for a named scenario.</summary>
    public static SpiderSurfaceRuntimeScenarioDefinition GetDefinition(SpiderSurfaceRuntimeScenario scenario) =>
        scenario switch
        {
            SpiderSurfaceRuntimeScenario.FloorPositiveY => new(
                "floor +Y", Vector3.UnitY, Vector3.UnitY, Vector3.UnitZ, false),
            SpiderSurfaceRuntimeScenario.CeilingNegativeY => new(
                "ceiling -Y", -Vector3.UnitY, -Vector3.UnitY, Vector3.UnitZ, false),
            SpiderSurfaceRuntimeScenario.WallPositiveX => new(
                "wall +X", Vector3.UnitX, Vector3.UnitX, Vector3.UnitZ, false),
            SpiderSurfaceRuntimeScenario.WallNegativeX => new(
                "wall -X", -Vector3.UnitX, -Vector3.UnitX, Vector3.UnitZ, false),
            SpiderSurfaceRuntimeScenario.WallPositiveZ => new(
                "wall +Z", Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitX, false),
            SpiderSurfaceRuntimeScenario.WallNegativeZ => new(
                "wall -Z", -Vector3.UnitZ, -Vector3.UnitZ, Vector3.UnitX, false),
            SpiderSurfaceRuntimeScenario.FloorToWall => new(
                "floor -> wall", Vector3.UnitY, Vector3.UnitX, Vector3.UnitX, true),
            SpiderSurfaceRuntimeScenario.WallToCeiling => new(
                "wall -> ceiling", Vector3.UnitX, -Vector3.UnitY, Vector3.UnitY, true),
            SpiderSurfaceRuntimeScenario.ConvexCorner => new(
                "convex corner", Vector3.UnitY, Vector3.UnitX, Vector3.UnitX, true),
            SpiderSurfaceRuntimeScenario.ConcaveCorner => new(
                "concave corner", Vector3.UnitY, Vector3.UnitX, Vector3.UnitX, true),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };

    /// <summary>
    /// Sets the scene-specific coordinates and expected normals for a preset.
    /// The presets describe the ten requested orientations; geometry remains
    /// owned by the test scene rather than being generated by this class.
    /// </summary>
    public void ConfigureScenario(
        SpiderSurfaceRuntimeScenario scenario,
        Vector3 startPosition,
        Vector3? initialNormal = null,
        Vector3? travelDirection = null,
        Vector3? expectedFinalNormal = null,
        bool resetMotor = true)
    {
        SpiderSurfaceRuntimeScenarioDefinition definition = GetDefinition(scenario);
        Vector3 configuredNormal = initialNormal ?? definition.InitialNormal;
        Vector3 configuredDirection = travelDirection ?? definition.TravelDirection;
        Vector3 configuredExpectedNormal = expectedFinalNormal ?? definition.ExpectedFinalNormal;

        if (!IsFinite(startPosition) ||
            !IsFinite(configuredNormal) ||
            !IsFinite(configuredDirection) ||
            !IsFinite(configuredExpectedNormal))
        {
            throw new ArgumentException("Runtime scenario configuration must contain finite vectors.");
        }

        _configurations[scenario] = new RuntimeScenarioConfiguration(
            startPosition,
            configuredNormal,
            configuredDirection,
            configuredExpectedNormal,
            resetMotor);
        if (scenario == Scenario)
            _scenarioStarted = false;
    }

    public void StartScenario(SpiderSurfaceRuntimeScenario scenario)
    {
        if (_disposed)
            return;

        Scenario = scenario;
        ResetMonitorState();
        _capturedSamples[scenario] = new List<SpiderSurfaceRuntimeSample>();
        RuntimeScenarioConfiguration configuration = _configurations[scenario];
        Vector3 initialNormal = NormalizeOrZero(configuration.InitialNormal);
        Vector3 travelDirection = NormalizeOrZero(configuration.TravelDirection);
        if (initialNormal.LengthSquared() <= Epsilon * Epsilon)
            throw new ArgumentException("Runtime scenario requires a valid initial normal.");
        if (travelDirection.LengthSquared() <= Epsilon * Epsilon)
            throw new ArgumentException("Runtime scenario requires a valid travel direction.");

        if (ResetMotorOnScenarioStart && configuration.ResetMotor)
        {
            _motor.ResetRuntimeTestState(
                configuration.StartPosition,
                initialNormal,
                travelDirection);
        }

        _patrol.RuntimeMovementOverrideEnabled = true;
        _scenarioStarted = true;
        LastStatus = $"Scenario started: {GetDefinition(scenario).Name}";
    }

    public void StartAutomaticSuite()
    {
        AutoRunScenarios = true;
        StartScenario(SpiderSurfaceRuntimeScenario.FloorPositiveY);
    }

    public void Stop()
    {
        AutoRunScenarios = false;
        _scenarioStarted = false;
        _patrol.RuntimeMovementOverrideEnabled = false;
        _activeAnomalies.Clear();
        LastStatus = "Runtime harness stopped.";
    }

    /// <summary>
    /// Standalone mode. Do not also call SpiderEnemy.Update for this spider in
    /// the same frame; this method performs the patrol/motor tick itself.
    /// </summary>
    public void Update(float dt)
    {
        if (!Enabled || _disposed)
            return;

        BeginPhysicsStep(dt);
        if (DrivePatrolFromUpdate)
        {
            if (_physics != null)
                _spider.Update(dt, _physics);
            else
                _patrol.Update(dt);
        }
        EndPhysicsStep(dt);
    }

    /// <summary>
    /// Call immediately before the regular engine update. The patrol receives
    /// the scenario input through its optional runtime override.
    /// </summary>
    public void BeginPhysicsStep(float dt)
    {
        if (!Enabled || _disposed)
            return;

        dt = SanitizeDeltaTime(dt);
        EnsureScenarioStarted();
        if (!_scenarioStarted)
            return;

        if (AutoRunScenarios && _scenarioElapsed >= MathF.Max(0.1f, ScenarioDuration))
            AdvanceAutomaticScenario();

        RuntimeScenarioConfiguration configuration = _configurations[Scenario];
        Vector3 direction = NormalizeOrZero(configuration.TravelDirection);
        if (direction.LengthSquared() <= Epsilon * Epsilon)
            direction = _motor.Forward;

        _patrol.RuntimeMovementDirection = direction;
        _patrol.RuntimeMovementSpeed = float.IsFinite(MoveSpeed) ? MathF.Max(0f, MoveSpeed) : 0f;
        _patrol.RuntimeMovementOverrideEnabled = true;
    }

    /// <summary>Call immediately after the regular engine update.</summary>
    public void EndPhysicsStep(float dt)
    {
        if (!Enabled || _disposed || !_scenarioStarted)
            return;

        dt = SanitizeDeltaTime(dt);
        CaptureAndValidate(dt);
        _scenarioElapsed += dt;
        _totalElapsed += dt;

        if (AutoRunScenarios && _scenarioElapsed >= MathF.Max(0.1f, ScenarioDuration))
            AdvanceAutomaticScenario();
    }

    /// <summary>
    /// Compares the captured +X and -X runs under a reflection through the
    /// YZ plane. The right-vector sign is corrected for mirror handedness.
    /// </summary>
    public bool TryValidateMirroredXPair(out string message)
    {
        if (!_capturedSamples.TryGetValue(SpiderSurfaceRuntimeScenario.WallPositiveX, out List<SpiderSurfaceRuntimeSample>? positive) ||
            !_capturedSamples.TryGetValue(SpiderSurfaceRuntimeScenario.WallNegativeX, out List<SpiderSurfaceRuntimeSample>? negative) ||
            positive.Count == 0 || negative.Count == 0)
        {
            message = "Both wall +X and wall -X scenarios must have captured samples.";
            SetAnomaly("mirror-x-missing", true, message);
            return false;
        }

        int count = System.Math.Min(positive.Count, negative.Count);
        bool equivalent = positive.Count == negative.Count;
        Vector3 mirrorPlaneNormal = Vector3.UnitX;
        for (int i = 0; i < count; i++)
        {
            SpiderSurfaceRuntimeSample a = positive[i];
            SpiderSurfaceRuntimeSample b = negative[i];
            equivalent &= b.HasContact == a.HasContact;
            equivalent &= Vector3.Distance(Reflect(a.Position, mirrorPlaneNormal), b.Position) <= MirrorVectorTolerance;
            equivalent &= Vector3.Distance(Reflect(a.SurfaceNormal, mirrorPlaneNormal), b.SurfaceNormal) <= MirrorVectorTolerance;
            equivalent &= Vector3.Distance(Reflect(a.Forward, mirrorPlaneNormal), b.Forward) <= MirrorVectorTolerance;
            equivalent &= Vector3.Distance(-Reflect(a.Right, mirrorPlaneNormal), b.Right) <= MirrorVectorTolerance;
            equivalent &= Vector3.Distance(Reflect(a.DesiredDirection, mirrorPlaneNormal), b.DesiredDirection) <= MirrorVectorTolerance;
            equivalent &= MathF.Abs(a.CurrentSpeed - b.CurrentSpeed) <= MirrorVectorTolerance;
            equivalent &= MathF.Abs(a.SurfaceDistance - b.SurfaceDistance) <= MirrorVectorTolerance ||
                          (!float.IsFinite(a.SurfaceDistance) && !float.IsFinite(b.SurfaceDistance));
            if (!equivalent)
                break;
        }

        message = equivalent
            ? "Mirrored +X/-X locomotion is equivalent within tolerance."
            : "Mirrored +X/-X locomotion diverged beyond tolerance.";
        SetAnomaly("mirror-x-divergence", !equivalent, message);
        return equivalent;
    }

    public void OnDrawGizmos(DebugDrawer drawer)
    {
        if (!DebugEnabled || !HasSample || _disposed)
            return;

        SpiderSurfaceRuntimeSample sample = LastSample;
        Vector3 position = sample.Position;
        Vector3 color = _activeAnomalies.Count == 0
            ? new Vector3(0.15f, 1f, 0.25f)
            : new Vector3(1f, 0.12f, 0.08f);

        drawer.DrawSphere(position, Quaternion.Identity, 0.12f, color);
        drawer.PushLine(position, position + sample.SurfaceNormal * 1.25f, new Vector3(0.15f, 1f, 0.25f));
        drawer.PushLine(position, position + sample.DesiredSurfaceNormal * 1.05f, new Vector3(1f, 0.15f, 0.85f));
        drawer.PushLine(position, position + sample.Forward * 1.40f, new Vector3(0.10f, 0.35f, 1f));
        drawer.PushLine(position, position + sample.Right * 1.00f, new Vector3(1f, 0.65f, 0.10f));
        drawer.PushLine(position, position + sample.DesiredDirection * 1.15f, new Vector3(0.10f, 1f, 1f));

        if (sample.HasContact)
        {
            drawer.DrawSphere(sample.SurfacePoint, Quaternion.Identity, 0.07f, new Vector3(1f, 0.85f, 0.10f));
            drawer.PushLine(sample.SurfacePoint, sample.SurfacePoint + sample.SurfaceNormal * 0.45f, new Vector3(1f, 0.85f, 0.10f));
        }

        SpiderPathFollower follower = _patrol.PathFollower;
        if (follower.HasPath)
        {
            drawer.DrawSphere(
                follower.CurrentTargetPosition,
                Quaternion.Identity,
                0.10f,
                new Vector3(0.95f, 0.25f, 0.15f));
            drawer.PushLine(position, follower.CurrentTargetPosition, new Vector3(0.95f, 0.45f, 0.15f));
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _patrol.RuntimeMovementOverrideEnabled = false;
        _capturedSamples.Clear();
        _activeAnomalies.Clear();
    }

    private void EnsureScenarioStarted()
    {
        if (!_scenarioStarted)
            StartScenario(Scenario);
    }

    private void AdvanceAutomaticScenario()
    {
        int nextIndex = (int)Scenario + 1;
        if (nextIndex >= AllScenarioValues.Length)
        {
            if (!LoopScenarios)
            {
                Stop();
                return;
            }

            nextIndex = 0;
        }

        StartScenario(AllScenarioValues[nextIndex]);
    }

    private void CaptureAndValidate(float dt)
    {
        SpiderSurfaceContact contact = _motor.SurfaceContact;
        Vector3 surfaceNormal = _motor.SurfaceNormal;
        Vector3 contactNormal = NormalizeOrZero(contact.Normal);
        bool hasContact = _motor.HasSurface && contact.IsValid &&
                          contactNormal.LengthSquared() > Epsilon * Epsilon;
        float surfaceDistance = hasContact
            ? MathF.Abs(Vector3.Dot(_motor.Character.Position - contact.Point, contactNormal))
            : float.NaN;
        SpiderPathFollower follower = _patrol.PathFollower;
        int waypointIndex = follower.HasPath ? follower.CurrentWaypointIndex : -1;
        float waypointDistance = follower.HasPath
            ? Vector3.Distance(_motor.Character.Position, follower.CurrentTargetPosition)
            : float.NaN;

        SpiderSurfaceRuntimeSample sample = new(
            Scenario,
            _scenarioElapsed,
            _motor.Character.Position,
            surfaceNormal,
            _motor.DesiredSurfaceNormal,
            _motor.Forward,
            _motor.Right,
            _motor.DesiredDirection,
            _motor.CurrentVelocity,
            _motor.Character.Rotation,
            contact.IsValid ? contact.Point : Vector3.Zero,
            surfaceDistance,
            hasContact,
            _motor.IsBlocked,
            waypointIndex,
            waypointDistance,
            _motor.CurrentSpeed);

        LastSample = sample;
        HasSample = true;
        if (CaptureSamples)
        {
            if (!_capturedSamples.TryGetValue(Scenario, out List<SpiderSurfaceRuntimeSample>? samples))
            {
                samples = new List<SpiderSurfaceRuntimeSample>();
                _capturedSamples[Scenario] = samples;
            }

            if (samples.Count < MaximumCapturedSamples)
                samples.Add(sample);
        }

        ValidateFiniteValues(sample);
        ValidateBasis(sample);
        ValidateTransitions(sample, dt);
        ValidateStall(sample, dt);
        ValidateWaypointProgress(sample, dt);
        ValidateExpectedNormal(sample);
        _previousSample = sample;
        _hasPreviousSample = true;
    }

    private void ValidateFiniteValues(in SpiderSurfaceRuntimeSample sample)
    {
        bool invalid = !IsFinite(sample.Position) ||
                       !IsFinite(sample.SurfaceNormal) ||
                       !IsFinite(sample.DesiredSurfaceNormal) ||
                       !IsFinite(sample.Forward) ||
                       !IsFinite(sample.Right) ||
                       !IsFinite(sample.DesiredDirection) ||
                       !IsFinite(sample.Velocity) ||
                       !IsFinite(sample.SurfacePoint) ||
                       !IsFinite(sample.Rotation) ||
                       !float.IsFinite(sample.CurrentSpeed) ||
                       sample.CurrentSpeed < -0.01f;
        if (IsFinite(sample.Rotation))
            invalid |= MathF.Abs(sample.Rotation.LengthSquared() - 1f) > 0.05f;
        if (sample.HasContact)
            invalid |= !float.IsFinite(sample.SurfaceDistance);
        if (sample.CurrentWaypointIndex >= 0)
            invalid |= !float.IsFinite(sample.WaypointDistance);

        SetAnomaly(
            "non-finite-state",
            invalid,
            "Invalid finite/normalized state detected in position, normal, direction, velocity, quaternion or distance.");
    }

    private void ValidateBasis(in SpiderSurfaceRuntimeSample sample)
    {
        bool invalid = sample.HasContact &&
                       (LengthOutsideUnitRange(sample.SurfaceNormal) ||
                        LengthOutsideUnitRange(sample.Forward) ||
                        LengthOutsideUnitRange(sample.Right) ||
                        MathF.Abs(Vector3.Dot(sample.SurfaceNormal, sample.Forward)) > 0.03f ||
                        MathF.Abs(Vector3.Dot(sample.SurfaceNormal, sample.Right)) > 0.03f ||
                        MathF.Abs(Vector3.Dot(sample.Forward, sample.Right)) > 0.03f);

        SetAnomaly(
            "invalid-local-basis",
            invalid,
            "SurfaceNormal/Forward/Right are not a finite orthonormal surface basis.");

        bool invalidDistance = sample.HasContact &&
                               MathF.Abs(sample.SurfaceDistance - _motor.Clearance) > SurfaceDistanceTolerance;
        SetAnomaly(
            "surface-distance",
            invalidDistance,
            $"Distance to support surface is {sample.SurfaceDistance:F3}; expected approximately {_motor.Clearance:F3}.");
    }

    private void ValidateTransitions(in SpiderSurfaceRuntimeSample sample, float dt)
    {
        float transitionAngle = AngleDegrees(sample.SurfaceNormal, sample.DesiredSurfaceNormal);
        bool transitionActive = transitionAngle >= TransitionDetectionDegrees;
        if (transitionActive && !sample.HasContact)
            _transitionLossElapsed += dt;
        else
            _transitionLossElapsed = 0f;

        SetAnomaly(
            "transition-contact-loss",
            _transitionLossElapsed > ContactLossGrace,
            "Surface contact was lost while the desired surface normal was changing.");

        bool abruptNormal = _hasPreviousSample &&
                            AngleDegrees(_previousSample.SurfaceNormal, sample.SurfaceNormal) > MaxAbruptNormalChangeDegrees;
        SetAnomaly(
            "abrupt-normal-change",
            abruptNormal,
            "Current surface normal changed by more than the runtime threshold in one frame.");

        bool forwardFlip = _hasPreviousSample &&
                           Vector3.Dot(_previousSample.Forward, sample.Forward) < MaxForwardFlipDot;
        SetAnomaly(
            "forward-flip",
            forwardFlip,
            "Forward direction flipped close to 180 degrees.");

        RegisterNormalTransition(sample.DesiredSurfaceNormal, dt);
        SetAnomaly(
            "surface-oscillation",
            _oscillationCount >= 2,
            "Desired surface normal is alternating between surfaces.");
    }

    private void ValidateStall(in SpiderSurfaceRuntimeSample sample, float dt)
    {
        bool stalled = sample.HasContact &&
                       sample.DesiredDirection.LengthSquared() > 0.35f * 0.35f &&
                       sample.CurrentSpeed < StallSpeed &&
                       !sample.IsBlocked;
        _stallElapsed = stalled ? _stallElapsed + dt : 0f;

        SetAnomaly(
            "desired-direction-stall",
            _stallElapsed > StallGrace,
            "The spider is almost stationary despite a valid DesiredDirection.");
    }

    private void ValidateWaypointProgress(in SpiderSurfaceRuntimeSample sample, float dt)
    {
        if (sample.CurrentWaypointIndex < 0)
        {
            _lastWaypointIndex = -1;
            _bestWaypointDistance = float.MaxValue;
            _waypointNoProgressElapsed = 0f;
            SetAnomaly("waypoint-stall", false, string.Empty);
            return;
        }

        if (sample.CurrentWaypointIndex != _lastWaypointIndex)
        {
            _lastWaypointIndex = sample.CurrentWaypointIndex;
            _bestWaypointDistance = sample.WaypointDistance;
            _waypointNoProgressElapsed = 0f;
        }
        else if (sample.WaypointDistance < _bestWaypointDistance - WaypointProgressEpsilon)
        {
            _bestWaypointDistance = sample.WaypointDistance;
            _waypointNoProgressElapsed = 0f;
        }
        else
        {
            _waypointNoProgressElapsed += dt;
        }

        bool stalled = _waypointNoProgressElapsed > MathF.Max(0.5f, WaypointTimeout) &&
                       sample.WaypointDistance > _patrol.PathFollower.WaypointReachDistance + 0.15f;
        SetAnomaly(
            "waypoint-stall",
            stalled,
            $"Waypoint {sample.CurrentWaypointIndex} has not made progress for {_waypointNoProgressElapsed:F2}s.");
    }

    private void ValidateExpectedNormal(in SpiderSurfaceRuntimeSample sample)
    {
        RuntimeScenarioConfiguration configuration = _configurations[Scenario];
        Vector3 expected = NormalizeOrZero(configuration.ExpectedFinalNormal);
        bool mismatch = expected.LengthSquared() > Epsilon * Epsilon &&
                        _scenarioElapsed > ExpectedNormalWarmup &&
                        AngleDegrees(sample.SurfaceNormal, expected) > ExpectedNormalToleranceDegrees;
        SetAnomaly(
            "unexpected-surface-normal",
            mismatch,
            $"Actual surface normal is not approaching the expected {GetDefinition(Scenario).Name} normal.");
    }

    private void RegisterNormalTransition(Vector3 desiredNormal, float dt)
    {
        Vector3 normalized = NormalizeOrZero(desiredNormal);
        if (normalized.LengthSquared() <= Epsilon * Epsilon)
            return;

        _oscillationElapsed += dt;
        if (_oscillationElapsed > OscillationWindow)
        {
            _oscillationElapsed = 0f;
            _oscillationCount = 0;
            _hasPreviousTransitionNormal = false;
        }

        if (!_hasObservedDesiredNormal)
        {
            _lastObservedDesiredNormal = normalized;
            _lastTransitionNormal = normalized;
            _hasObservedDesiredNormal = true;
            _hasLastTransitionNormal = true;
            return;
        }

        if (AngleDegrees(_lastObservedDesiredNormal, normalized) < OscillationTransitionDegrees)
            return;

        if (_hasLastTransitionNormal)
        {
            _previousTransitionNormal = _lastTransitionNormal;
            _hasPreviousTransitionNormal = true;
        }

        bool isAlternating = _hasPreviousTransitionNormal &&
                             AngleDegrees(_previousTransitionNormal, normalized) <= OscillationSameSurfaceDegrees &&
                             AngleDegrees(_lastTransitionNormal, normalized) > OscillationSameSurfaceDegrees;
        if (isAlternating)
            _oscillationCount++;

        _lastTransitionNormal = normalized;
        _hasLastTransitionNormal = true;
        _lastObservedDesiredNormal = normalized;
        _oscillationElapsed = 0f;
    }

    private void ResetMonitorState()
    {
        _scenarioStarted = false;
        _scenarioElapsed = 0f;
        _transitionLossElapsed = 0f;
        _stallElapsed = 0f;
        _oscillationElapsed = 0f;
        _oscillationCount = 0;
        _lastObservedDesiredNormal = Vector3.Zero;
        _lastTransitionNormal = Vector3.Zero;
        _previousTransitionNormal = Vector3.Zero;
        _hasObservedDesiredNormal = false;
        _hasLastTransitionNormal = false;
        _hasPreviousTransitionNormal = false;
        _lastWaypointIndex = -1;
        _bestWaypointDistance = float.MaxValue;
        _waypointNoProgressElapsed = 0f;
        _hasPreviousSample = false;
        _activeAnomalies.Clear();
        HasSample = false;
        LastSample = default;
    }

    private void SetAnomaly(string key, bool condition, string message)
    {
        if (!condition)
        {
            _activeAnomalies.Remove(key);
            return;
        }

        if (_activeAnomalies.Add(key))
        {
            Logger.Warn($"[SpiderRuntime] {_spider.Id} / {GetDefinition(Scenario).Name}: {message}");
        }
    }

    private static float SanitizeDeltaTime(float dt) =>
        float.IsFinite(dt) ? System.Math.Clamp(dt, 0.0001f, 0.05f) : 0.016f;

    private static float AngleDegrees(Vector3 a, Vector3 b)
    {
        a = NormalizeOrZero(a);
        b = NormalizeOrZero(b);
        if (a.LengthSquared() <= Epsilon * Epsilon || b.LengthSquared() <= Epsilon * Epsilon)
            return 0f;
        return MathF.Acos(System.Math.Clamp(Vector3.Dot(a, b), -1f, 1f)) * (180f / MathF.PI);
    }

    private static bool LengthOutsideUnitRange(Vector3 value) =>
        value.Length() < 0.90f || value.Length() > 1.10f;

    private static Vector3 Reflect(Vector3 value, Vector3 planeNormal) =>
        value - 2f * planeNormal * Vector3.Dot(value, planeNormal);

    private static Vector3 NormalizeOrZero(Vector3 value) =>
        value.LengthSquared() > Epsilon * Epsilon ? Vector3.Normalize(value) : Vector3.Zero;

    private static bool IsFinite(Vector3 value) =>
        !float.IsNaN(value.X) && !float.IsInfinity(value.X) &&
        !float.IsNaN(value.Y) && !float.IsInfinity(value.Y) &&
        !float.IsNaN(value.Z) && !float.IsInfinity(value.Z);

    private static bool IsFinite(Quaternion value) =>
        !float.IsNaN(value.X) && !float.IsInfinity(value.X) &&
        !float.IsNaN(value.Y) && !float.IsInfinity(value.Y) &&
        !float.IsNaN(value.Z) && !float.IsInfinity(value.Z) &&
        !float.IsNaN(value.W) && !float.IsInfinity(value.W);

    private readonly struct RuntimeScenarioConfiguration
    {
        public RuntimeScenarioConfiguration(
            Vector3 startPosition,
            Vector3 initialNormal,
            Vector3 travelDirection,
            Vector3 expectedFinalNormal,
            bool resetMotor)
        {
            StartPosition = startPosition;
            InitialNormal = initialNormal;
            TravelDirection = travelDirection;
            ExpectedFinalNormal = expectedFinalNormal;
            ResetMotor = resetMotor;
        }

        public Vector3 StartPosition { get; }
        public Vector3 InitialNormal { get; }
        public Vector3 TravelDirection { get; }
        public Vector3 ExpectedFinalNormal { get; }
        public bool ResetMotor { get; }
    }
}
