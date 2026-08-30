using System;
using System.Numerics;
using Fuse.Debug;
using Fuse.Physics;
using JoltPhysicsSharp;

namespace Fuse.Enemy;

/// <summary>
/// Collision-safe locomotion motor for a creature whose local up axis follows
/// an arbitrary surface. Patrol supplies intent; this class owns adhesion,
/// surface transitions and physical movement.
/// </summary>
public sealed class SpiderSurfaceMotor : IGizmoDrawable, IDisposable
{
    private const float Epsilon = 0.0001f;
    private const float SurfaceLostGrace = 0.18f;
    private const float AdhesionSpeed = 2.6f;
    private const float MaxAirSpeed = 30f;
    private const float TransitionLookAhead = 0.38f;
    private const float TransitionMinDuration = 0.12f;
    private const float TransitionMaxDuration = 0.75f;
    private const float TransitionCooldownDuration = 1.00f;
    private const float TransitionSupportAlignment = 0.86f;
    private const float TransitionCompletionAlignment = 0.96f;
    private const int TransitionStableFrameCount = 3;
    private const float TransitionCorrectionSpeed = 6f;
    private const float OutwardCorrectionSpeed = 4.5f;
    private const float SurfaceNormalResponse = 12f;
    private const float OrientationResponse = 14f;
    private const float MaxDetachedDistance = 100f;

    private readonly PhysicsWorld _physics;
    private readonly RigidBody _body;
    private readonly SpiderSurfaceSolver _solver;
    private readonly CapsuleShape _shape;
    private readonly Vector3 _safeSpawn;
    private ObjectLayer _objectLayer;
    private SpiderSurfaceContact _surfaceContact;
    private Vector3 _surfaceNormal;
    private Vector3 _desiredSurfaceNormal;
    private Vector3 _surfaceForward;
    private Vector3 _desiredDirection;
    private Vector3 _movementDirection;
    private Vector3 _airVelocity;
    private Vector3 _requestedVelocity;
    private Vector3 _debugPredictedPosition;
    private Vector3 _transitionStartNormal;
    private Vector3 _transitionTargetNormal;
    private Vector3 _transitionTargetPoint;
    private SpiderSurfaceContact _transitionPreviousContact;
    private float _lostSurfaceTime;
    private float _transitionElapsed;
    private float _transitionCooldown;
    private int _transitionStableFrames;
    private bool _transitionActive;
    private Vector3 _transitionConstraintNormal;
    private bool _hasTransitionConstraint;
    private SpiderSurfaceContact _transitionGuide;
    private bool _hasTransitionGuide;
    private bool _disposed;

    public CharacterVirtual Character { get; }
    public SpiderSurfaceContact SurfaceContact => _surfaceContact;
    public Vector3 SurfaceNormal => _surfaceNormal;
    public Vector3 DesiredSurfaceNormal => _desiredSurfaceNormal;
    public Vector3 Forward => _surfaceForward;
    public Vector3 Right => NormalizeOrZero(Vector3.Cross(_surfaceForward, _surfaceNormal));
    public Vector3 DesiredDirection => _desiredDirection;
    public Vector3 MovementDirection => _movementDirection;
    public Vector3 CurrentVelocity { get; private set; }
    public float CurrentSpeed { get; private set; }
    public bool IsBlocked { get; private set; }
    public bool HasSurface => _surfaceContact.IsValid && _lostSurfaceTime <= SurfaceLostGrace;
    public bool IsTransitioning => _transitionActive;
    public Vector3 TransitionTargetNormal => _transitionTargetNormal;
    public float TransitionLockRemaining => _transitionCooldown;
    public float Clearance { get; }

    /// <summary>
    /// Restricts the next surface transition to the surface selected by the
    /// pursuit planner. This prevents a corner probe from choosing a different
    /// adjacent wall than the active waypoint.
    /// </summary>
    public void SetTransitionConstraint(Vector3 surfaceNormal)
    {
        surfaceNormal = NormalizeOrZero(surfaceNormal);
        _transitionConstraintNormal = surfaceNormal;
        _hasTransitionConstraint =
            surfaceNormal.LengthSquared() > Epsilon * Epsilon;
    }

    public void ClearTransitionConstraint()
    {
        _transitionConstraintNormal = Vector3.Zero;
        _hasTransitionConstraint = false;
        ClearTransitionGuide();
    }

    /// <summary>
    /// Supplies the contact already validated by the local pursuit planner.
    /// The motor still uses its own collision sweep for movement, but can use
    /// this contact when the instantaneous corner probe sees the old face
    /// instead of rediscovering the same transition every frame.
    /// </summary>
    public void SetTransitionGuide(in SpiderSurfaceContact contact)
    {
        _transitionGuide = contact;
        _hasTransitionGuide = contact.IsValid;
    }

    public void ClearTransitionGuide()
    {
        _transitionGuide = default;
        _hasTransitionGuide = false;
    }

    /// <summary>
    /// Resets only transient locomotion state for a runtime diagnostic. It is
    /// intentionally separate from normal gameplay movement so a test can
    /// place the character at a mirrored surface without changing navigation.
    /// </summary>
    public void ResetRuntimeTestState(Vector3 position, Vector3 surfaceNormal, Vector3 forward)
    {
        if (_disposed)
            return;
        if (!IsFinite(position))
            throw new ArgumentException("Runtime test position must be finite.", nameof(position));

        surfaceNormal = NormalizeOrZero(surfaceNormal);
        if (surfaceNormal.LengthSquared() <= Epsilon * Epsilon)
            throw new ArgumentException("Runtime test normal must be valid.", nameof(surfaceNormal));

        forward = BuildTangent(surfaceNormal, forward, _surfaceForward);
        if (forward.LengthSquared() <= Epsilon * Epsilon)
            throw new ArgumentException("Runtime test forward must be tangent to the surface.", nameof(forward));

        _surfaceContact = default;
        _lostSurfaceTime = SurfaceLostGrace + 0.001f;
        _surfaceNormal = surfaceNormal;
        _desiredSurfaceNormal = surfaceNormal;
        _surfaceForward = forward;
        _desiredDirection = forward;
        _movementDirection = forward;
        _transitionStartNormal = Vector3.Zero;
        _transitionTargetNormal = Vector3.Zero;
        _transitionTargetPoint = Vector3.Zero;
        _transitionPreviousContact = default;
        _transitionElapsed = 0f;
        _transitionCooldown = 0f;
        _transitionStableFrames = 0;
        _transitionActive = false;
        _transitionConstraintNormal = Vector3.Zero;
        _hasTransitionConstraint = false;
        _transitionGuide = default;
        _hasTransitionGuide = false;
        _airVelocity = Vector3.Zero;
        _requestedVelocity = Vector3.Zero;
        CurrentVelocity = Vector3.Zero;
        CurrentSpeed = 0f;
        IsBlocked = false;
        _debugPredictedPosition = position;

        Character.Position = position;
        Character.Up = surfaceNormal;
        Character.Rotation = RotationFromSurface(surfaceNormal, forward, forward);
        Character.LinearVelocity = Vector3.Zero;
        SyncBody();
    }

    public SpiderSurfaceMotor(
        PhysicsWorld physics,
        RigidBody body,
        SpiderSurfaceSolver solver,
        Vector3 spawnPosition,
        Vector3 initialNormal,
        Vector3 initialForward)
    {
        _physics = physics;
        _body = body;
        _solver = solver;
        _safeSpawn = spawnPosition;
        _objectLayer = physics.ObjectLayer;

        initialNormal = NormalizeOrZero(initialNormal);
        if (initialNormal.LengthSquared() <= Epsilon * Epsilon)
            throw new ArgumentException("SpiderSurfaceMotor requires a valid initial surface normal.", nameof(initialNormal));

        initialForward = BuildTangent(initialNormal, initialForward, Vector3.Zero);
        if (initialForward.LengthSquared() <= Epsilon * Epsilon)
            throw new ArgumentException("SpiderSurfaceMotor requires a valid initial tangent direction.", nameof(initialForward));

        _surfaceNormal = initialNormal;
        _desiredSurfaceNormal = initialNormal;
        _surfaceForward = initialForward;
        _desiredDirection = initialForward;
        _movementDirection = initialForward;

        float halfHeight = MathF.Max(0.01f, body.CapsuleHeight * 0.5f);
        float radius = MathF.Max(0.05f, body.CapsuleRadius);
        Clearance = halfHeight + radius + 0.08f;
        _shape = new CapsuleShape(halfHeight, radius);

        var settings = new CharacterVirtualSettings
        {
            Mass = 80f,
            Shape = _shape,
            Up = initialNormal,
            BackFaceMode = BackFaceMode.CollideWithBackFaces,
            MaxSlopeAngle = float.DegreesToRadians(80f),
            CharacterPadding = 0.025f,
            PenetrationRecoverySpeed = 1f,
            PredictiveContactDistance = 0.08f,
            MaxCollisionIterations = 16,
            MaxConstraintIterations = 24,
            MinTimeRemaining = 1.0e-4f,
            CollisionTolerance = 1.0e-3f,
            MaxNumHits = 256,
            HitReductionCosMaxAngle = 0.999f,
            EnhancedInternalEdgeRemoval = true
        };

        Quaternion rotation = RotationFromSurface(initialNormal, initialForward, initialForward);
        Vector3 position = spawnPosition;
        Character = new CharacterVirtual(settings, in position, in rotation, 0, physics.Native);

        _solver.BeginFrame();
        if (_solver.TryAcquireContact(
                spawnPosition,
                initialNormal,
                initialForward,
                Clearance + 1.25f,
                body.Native,
                out SpiderSurfaceContact initialContact))
        {
            Vector3 contactNormal = NormalizeOrZero(initialContact.Normal);
            if (contactNormal.LengthSquared() > Epsilon * Epsilon)
            {
                _surfaceContact = initialContact;
                _desiredSurfaceNormal = contactNormal;
                _surfaceNormal = contactNormal;
                _surfaceForward = BuildTangent(contactNormal, initialForward, initialForward);
                _movementDirection = _surfaceForward;
                Character.Up = _surfaceNormal;
                Character.Rotation = RotationFromSurface(_surfaceNormal, _surfaceForward, _surfaceForward);
            }
        }

        SyncBody();
        DebugDrawer.Register(this);
    }

    public void Update(float dt, Vector3 intendedDirection, float intendedSpeed)
    {
        if (_disposed || !_body.IsBuilt)
            return;

        dt = System.Math.Clamp(dt, 0.0001f, 0.05f);
        intendedSpeed = MathF.Max(0f, intendedSpeed);
        // IsBlocked describes the result of this simulation step only. If it
        // is left latched from a previous collision, pursuit keeps rebuilding
        // local plans forever even after the body has moved away from the
        // obstruction.
        IsBlocked = false;
        _solver.BeginFrame();

        _transitionCooldown = MathF.Max(0f, _transitionCooldown - dt);
        if (_transitionActive)
        {
            _transitionElapsed += dt;
            if (_transitionElapsed >= TransitionMaxDuration)
                CancelSurfaceTransition();
        }

        Vector3 startPosition = Character.Position;
        if (_surfaceContact.IsValid)
        {
            _surfaceContact = _solver.Refresh(_surfaceContact);
            if (!_transitionActive)
                SetDesiredSurfaceNormal(_surfaceContact.Normal);
            else if (IsAlignedWithTransitionTarget(_surfaceContact.Normal))
                _transitionTargetPoint = _surfaceContact.Point;
        }

        Vector3 up = NormalizeOrFallback(_surfaceNormal, _desiredSurfaceNormal);
        Vector3 tangent = BuildTangent(up, intendedDirection, _surfaceForward);
        if (tangent.LengthSquared() <= Epsilon * Epsilon)
            tangent = BuildTangent(up, _movementDirection, _surfaceForward);

        RefreshOrAcquireSupport(startPosition, up, tangent);
        Vector3 normalTarget = _transitionActive
            ? _transitionTargetNormal
            : _desiredSurfaceNormal;
        up = SmoothSurfaceNormal(up, normalTarget, dt);
        _surfaceNormal = up;
        tangent = BuildTangent(up, tangent, _surfaceForward);

        float commandSpeed = intendedSpeed;
        float stepDistance = commandSpeed * dt;
        _debugPredictedPosition = startPosition + tangent * stepDistance;

        if (HasSurface && commandSpeed > Epsilon)
        {
            Vector3 supportNormal = _transitionActive
                ? _transitionTargetNormal
                : up;

            bool supportAhead = _solver.TryFindSupportContact(
                _debugPredictedPosition,
                supportNormal,
                tangent,
                Clearance,
                _body.Native,
                out SpiderSurfaceContact aheadContact);

            SpiderSurfaceContact transitionContact = default;
            bool hasTransition = !_transitionActive &&
                _transitionCooldown <= 0f &&
                _solver.TryFindTransitionContact(
                    startPosition,
                    up,
                    tangent,
                    Clearance,
                    stepDistance + TransitionLookAhead,
                    _body.Native,
                    out transitionContact);

            if (hasTransition &&
                _hasTransitionConstraint &&
                Vector3.Dot(
                    NormalizeOrZero(transitionContact.Normal),
                    _transitionConstraintNormal) < TransitionSupportAlignment)
            {
                hasTransition = false;
            }

            // When the planner has a nearby confirmed guide, prefer it over
            // the instantaneous probe result. The latter can hit the old
            // face at the exact corner and make the target normal oscillate.
            if (_hasTransitionGuide &&
                !_transitionActive &&
                _transitionCooldown <= 0f &&
                TryGetTransitionFromGuide(
                    startPosition,
                    up,
                    tangent,
                out transitionContact))
            {
                hasTransition = true;
            }

            if (hasTransition)
            {
                BeginSurfaceTransition(transitionContact, up, ref tangent, dt);
                up = _surfaceNormal;
            }
            else if (supportAhead)
            {
                ApplySurfaceContact(aheadContact, up, ref tangent, dt);
                up = _surfaceNormal;
            }
            else if (_transitionActive)
            {
                // A convex edge can leave a short physical gap between the
                // old support and the new face. During an already-started
                // transition, requiring supportAhead would stop the motor at
                // that gap and leave it permanently parked on the corner.
                // Keep the transported tangent and let the transition
                // correction velocity bring the capsule onto the target
                // surface. CharacterVirtual still performs the real collision
                // sweep, and TransitionMaxDuration bounds this free segment.
            }
            else
            {
                // Walking into unsupported space is not allowed. The patrol can
                // choose another heading while the body remains safely planted.
                commandSpeed = 0f;
                IsBlocked = true;
            }
        }

        tangent = BuildTangent(up, tangent, _surfaceForward);
        _desiredDirection = tangent;
        _surfaceForward = ResolveForward(tangent, up, _surfaceForward);

        Quaternion targetRotation = RotationFromSurface(up, tangent, _surfaceForward);
        Character.Up = up;
        Character.Rotation = SmoothRotation(Character.Rotation, targetRotation, dt);

        if (HasSurface)
        {
            _airVelocity = Vector3.Zero;
            _requestedVelocity = tangent * commandSpeed - up * AdhesionSpeed +
                                 GetTransitionCorrectionVelocity(startPosition, dt);
        }
        else
        {
            _airVelocity += _physics.Gravity * dt;
            if (_airVelocity.Length() > MaxAirSpeed)
                _airVelocity = Vector3.Normalize(_airVelocity) * MaxAirSpeed;
            _requestedVelocity = _airVelocity;
        }

        Character.LinearVelocity = _requestedVelocity;
        using (var bodyFilter = new EnemyBodyFilter(_body.Native))
        using (var shapeFilter = new DefaultShapeFilter())
        {
            Character.Update(dt, in _objectLayer, _physics.Native, bodyFilter, shapeFilter);
        }

        Vector3 endPosition = Character.Position;
        Vector3 actualVelocity = (endPosition - startPosition) / dt;
        CurrentVelocity = actualVelocity;
        Vector3 actualTangent = ProjectOnPlane(actualVelocity, _surfaceNormal);
        CurrentSpeed = HasSurface ? actualTangent.Length() : actualVelocity.Length();

        if (actualTangent.LengthSquared() > Epsilon * Epsilon)
            _surfaceForward = ResolveForward(actualTangent, _surfaceNormal, _surfaceForward);
        _movementDirection = ResolveForward(_surfaceForward, _surfaceNormal, _movementDirection);

        if (intendedSpeed > Epsilon)
        {
            float requestedTravel = intendedSpeed * dt;
            float actualTravel = Vector3.Dot(endPosition - startPosition, _movementDirection);
            IsBlocked |= actualTravel < requestedTravel * 0.12f;
        }
        else
        {
            IsBlocked = false;
        }

        UpdatePostMoveContact(dt, ref endPosition, up);

        // Recovery is based on distance from the known safe spawn, never on a
        // world coordinate. This keeps the motor valid in every orientation.
        if (!HasSurface &&
            Vector3.DistanceSquared(Character.Position, _safeSpawn) > MaxDetachedDistance * MaxDetachedDistance)
        {
            Character.Position = _safeSpawn;
            Character.LinearVelocity = Vector3.Zero;
            _airVelocity = Vector3.Zero;
            _surfaceContact = default;
            _lostSurfaceTime = SurfaceLostGrace;
        }

        SyncBody();
    }

    private void RefreshOrAcquireSupport(Vector3 position, Vector3 up, Vector3 forward)
    {
        Vector3 expectedNormal = _transitionActive
            ? _transitionTargetNormal
            : up;

        if (_solver.TryFindSupportContact(
                position,
                expectedNormal,
                forward,
                Clearance,
                _body.Native,
                out SpiderSurfaceContact support))
        {
            if (_transitionActive && !IsAlignedWithTransitionTarget(support.Normal))
                return;

            _surfaceContact = support;
            _lostSurfaceTime = 0f;
            if (_transitionActive)
                _transitionTargetPoint = support.Point;
            else
                SetDesiredSurfaceNormal(support.Normal);
            return;
        }

        if (_transitionActive)
            return;

        if (_surfaceContact.IsValid)
            return;

        if (_solver.TryAcquireContact(
                position,
                up,
                forward,
                Clearance + 1.35f,
                _body.Native,
                out SpiderSurfaceContact acquired))
        {
            _surfaceContact = acquired;
            _lostSurfaceTime = 0f;
            SetDesiredSurfaceNormal(acquired.Normal);
        }
    }

    private void ApplySurfaceContact(
        in SpiderSurfaceContact contact,
        Vector3 previousNormal,
        ref Vector3 tangent,
        float dt)
    {
        Vector3 nextNormal = NormalizeOrZero(contact.Normal);
        if (nextNormal.LengthSquared() <= Epsilon * Epsilon)
            return;
        if (_transitionActive && !IsAlignedWithTransitionTarget(nextNormal))
            return;

        tangent = TransportAcrossSurface(tangent, previousNormal, nextNormal, _surfaceForward);
        _surfaceContact = contact;
        _lostSurfaceTime = 0f;
        if (_transitionActive)
        {
            _transitionTargetPoint = contact.Point;
            _desiredSurfaceNormal = _transitionTargetNormal;
            _surfaceNormal = SmoothSurfaceNormal(
                previousNormal,
                _transitionTargetNormal,
                dt);
        }
        else
        {
            _desiredSurfaceNormal = nextNormal;
            _surfaceNormal = SmoothSurfaceNormal(previousNormal, nextNormal, dt);
        }
        tangent = BuildTangent(_surfaceNormal, tangent, _surfaceForward);
    }

    private void BeginSurfaceTransition(
        in SpiderSurfaceContact contact,
        Vector3 previousNormal,
        ref Vector3 tangent,
        float dt)
    {
        Vector3 nextNormal = NormalizeOrZero(contact.Normal);
        previousNormal = NormalizeOrZero(previousNormal);
        if (nextNormal.LengthSquared() <= Epsilon * Epsilon ||
            previousNormal.LengthSquared() <= Epsilon * Epsilon)
        {
            return;
        }

        _transitionPreviousContact = _surfaceContact;
        _transitionStartNormal = previousNormal;
        _transitionTargetNormal = nextNormal;
        _transitionTargetPoint = contact.Point;
        _transitionElapsed = 0f;
        _transitionStableFrames = 0;
        _transitionActive = true;
        _transitionCooldown = 0f;

        tangent = TransportAcrossSurface(
            tangent,
            previousNormal,
            nextNormal,
            _surfaceForward);
        _surfaceContact = contact;
        _lostSurfaceTime = 0f;
        _desiredSurfaceNormal = nextNormal;
        _surfaceNormal = SmoothSurfaceNormal(previousNormal, nextNormal, dt);
        tangent = BuildTangent(_surfaceNormal, tangent, _surfaceForward);
    }

    private bool TryGetTransitionFromGuide(
        Vector3 bodyPosition,
        Vector3 currentNormal,
        Vector3 movementDirection,
        out SpiderSurfaceContact contact)
    {
        contact = default;
        if (!_hasTransitionGuide || !_transitionGuide.IsValid)
            return false;

        currentNormal = NormalizeOrZero(currentNormal);
        movementDirection = NormalizeOrZero(
            ProjectOnPlane(movementDirection, currentNormal));
        Vector3 targetNormal = NormalizeOrZero(_transitionGuide.Normal);
        if (currentNormal.LengthSquared() <= Epsilon * Epsilon ||
            movementDirection.LengthSquared() <= Epsilon * Epsilon ||
            targetNormal.LengthSquared() <= Epsilon * Epsilon)
        {
            return false;
        }

        float normalAlignment = Vector3.Dot(currentNormal, targetNormal);
        if (normalAlignment >= 0.82f || normalAlignment <= -0.42f)
            return false;

        SpiderSurfaceContact refreshed = _solver.Refresh(_transitionGuide);
        if (!refreshed.IsValid)
            return false;

        contact = refreshed;
        targetNormal = NormalizeOrZero(contact.Normal);

        Vector3 targetCenter = contact.Point + targetNormal * Clearance;
        Vector3 toTarget = targetCenter - bodyPosition;
        float distance = toTarget.Length();
        const float maxGuideDistance = 2.25f;
        if (!float.IsFinite(distance) || distance > maxGuideDistance)
        {
            contact = default;
            return false;
        }

        if (!_solver.TryConfirmTransitionContact(
                bodyPosition,
                contact,
                currentNormal,
                movementDirection,
                Clearance,
                _body.Native,
                out SpiderSurfaceContact confirmedContact))
        {
            contact = default;
            return false;
        }

        contact = confirmedContact;
        targetNormal = NormalizeOrZero(contact.Normal);
        targetCenter = contact.Point + targetNormal * Clearance;
        toTarget = targetCenter - bodyPosition;

        Vector3 approachDirection = NormalizeOrZero(
            ProjectOnPlane(toTarget, currentNormal));
        if (approachDirection.LengthSquared() <= Epsilon * Epsilon ||
            Vector3.Dot(approachDirection, movementDirection) < 0.10f)
        {
            contact = default;
            return false;
        }

        return contact.IsValid;
    }

    private void UpdatePostMoveContact(float dt, ref Vector3 position, Vector3 previousUp)
    {
        Vector3 up = _transitionActive
            ? _transitionTargetNormal
            : _surfaceNormal;
        if (_solver.TryFindSupportContact(
                position,
                up,
                _movementDirection,
                Clearance,
                _body.Native,
                out SpiderSurfaceContact support))
        {
            if (_transitionActive && !IsAlignedWithTransitionTarget(support.Normal))
                return;

            _surfaceContact = support;
            _lostSurfaceTime = 0f;
            if (_transitionActive)
            {
                _transitionTargetPoint = support.Point;
                _desiredSurfaceNormal = _transitionTargetNormal;
            }
            else
            {
                SetDesiredSurfaceNormal(support.Normal);
            }

            ApplySupportCorrection(
                support,
                _transitionActive ? _transitionTargetNormal : support.Normal,
                ref position,
                dt,
                _transitionActive);

            if (_transitionActive)
                UpdateTransitionStability(support.Normal);
            return;
        }

        if (_transitionActive)
        {
            _lostSurfaceTime = MathF.Min(
                SurfaceLostGrace,
                _lostSurfaceTime + dt);
            return;
        }

        _lostSurfaceTime += dt;
        if (_lostSurfaceTime <= SurfaceLostGrace)
            return;

        if (_solver.TryAcquireContact(
                position,
                previousUp,
                _movementDirection,
                Clearance + 1.35f,
                _body.Native,
                out SpiderSurfaceContact acquired))
        {
            Vector3 acquiredNormal = NormalizeOrZero(acquired.Normal);
            if (acquiredNormal.LengthSquared() > Epsilon * Epsilon)
            {
                _surfaceContact = acquired;
                _lostSurfaceTime = 0f;
                _movementDirection = TransportAcrossSurface(
                    _movementDirection,
                    _surfaceNormal,
                    acquiredNormal,
                    _surfaceForward);
                SetDesiredSurfaceNormal(acquiredNormal);
                return;
            }
        }

        _surfaceContact = default;
    }

    private void ApplySupportCorrection(
        in SpiderSurfaceContact support,
        Vector3 supportNormal,
        ref Vector3 position,
        float dt,
        bool isTransition)
    {
        supportNormal = NormalizeOrZero(supportNormal);
        if (supportNormal.LengthSquared() <= Epsilon * Epsilon)
            return;

        Vector3 desiredCenter = support.Point + supportNormal * Clearance;
        float normalError = Vector3.Dot(
            desiredCenter - position,
            supportNormal);
        if (!float.IsFinite(normalError))
            return;

        float maximumError = Clearance * (isTransition ? 1.5f : 1f);
        if (MathF.Abs(normalError) > maximumError)
            return;

        if (!isTransition && normalError <= 0.01f)
            return;

        float correctionSpeed = isTransition
            ? TransitionCorrectionSpeed
            : OutwardCorrectionSpeed;
        float correction = System.Math.Clamp(
            normalError,
            -correctionSpeed * dt,
            correctionSpeed * dt);
        if (MathF.Abs(correction) <= 0.0001f)
            return;

        position += supportNormal * correction;
        Character.Position = position;
    }

    private Vector3 GetTransitionCorrectionVelocity(Vector3 position, float dt)
    {
        if (!_transitionActive || dt <= Epsilon)
            return Vector3.Zero;

        Vector3 normal = NormalizeOrZero(_transitionTargetNormal);
        if (normal.LengthSquared() <= Epsilon * Epsilon)
            return Vector3.Zero;

        Vector3 desiredCenter = _transitionTargetPoint + normal * Clearance;
        float normalError = Vector3.Dot(desiredCenter - position, normal);
        if (!float.IsFinite(normalError) ||
            MathF.Abs(normalError) > Clearance * 1.5f)
        {
            return Vector3.Zero;
        }

        float speed = System.Math.Clamp(
            normalError / dt,
            -TransitionCorrectionSpeed,
            TransitionCorrectionSpeed);
        return normal * speed;
    }

    private void UpdateTransitionStability(Vector3 supportNormal)
    {
        if (!_transitionActive)
            return;

        float supportAlignment = Vector3.Dot(
            NormalizeOrZero(supportNormal),
            _transitionTargetNormal);
        float normalAlignment = Vector3.Dot(
            NormalizeOrZero(_surfaceNormal),
            _transitionTargetNormal);

        if (_transitionElapsed >= TransitionMinDuration &&
            supportAlignment >= TransitionSupportAlignment &&
            normalAlignment >= TransitionCompletionAlignment)
        {
            _transitionStableFrames++;
        }
        else
        {
            _transitionStableFrames = 0;
        }

        if (_transitionStableFrames >= TransitionStableFrameCount)
            CompleteSurfaceTransition();
    }

    private bool IsAlignedWithTransitionTarget(Vector3 normal)
    {
        if (!_transitionActive)
            return true;

        normal = NormalizeOrZero(normal);
        return normal.LengthSquared() > Epsilon * Epsilon &&
               Vector3.Dot(normal, _transitionTargetNormal) >=
               TransitionSupportAlignment;
    }

    private void CompleteSurfaceTransition()
    {
        if (!_transitionActive)
            return;

        _transitionActive = false;
        _transitionCooldown = TransitionCooldownDuration;
        _surfaceNormal = _transitionTargetNormal;
        _desiredSurfaceNormal = _transitionTargetNormal;
        _transitionStartNormal = Vector3.Zero;
        _transitionTargetNormal = Vector3.Zero;
        _transitionTargetPoint = Vector3.Zero;
        _transitionPreviousContact = default;
        _transitionElapsed = 0f;
        _transitionStableFrames = 0;
    }

    private void CancelSurfaceTransition()
    {
        if (!_transitionActive)
            return;

        Vector3 previousNormal = NormalizeOrZero(_transitionStartNormal);
        if (previousNormal.LengthSquared() > Epsilon * Epsilon)
        {
            _movementDirection = TransportAcrossSurface(
                _movementDirection,
                _surfaceNormal,
                previousNormal,
                _surfaceForward);
            _desiredSurfaceNormal = previousNormal;
        }

        if (_transitionPreviousContact.IsValid)
            _surfaceContact = _transitionPreviousContact;

        _transitionActive = false;
        _transitionCooldown = TransitionCooldownDuration;
        _transitionStartNormal = Vector3.Zero;
        _transitionTargetNormal = Vector3.Zero;
        _transitionTargetPoint = Vector3.Zero;
        _transitionPreviousContact = default;
        _transitionElapsed = 0f;
        _transitionStableFrames = 0;
    }

    private void SetDesiredSurfaceNormal(Vector3 normal)
    {
        Vector3 normalized = NormalizeOrZero(normal);
        if (normalized.LengthSquared() > Epsilon * Epsilon)
            _desiredSurfaceNormal = normalized;
    }

    private void SyncBody()
    {
        if (!_body.IsBuilt)
            return;

        _physics.BodyInterface.SetPositionAndRotation(
            _body.Native,
            Character.Position,
            Character.Rotation,
            Activation.Activate);
    }

    private static Quaternion RotationFromSurface(
        Vector3 normal,
        Vector3 desiredForward,
        Vector3 previousForward)
    {
        normal = NormalizeOrZero(normal);
        if (normal.LengthSquared() <= Epsilon * Epsilon)
            return Quaternion.Identity;

        // Keep the requested construction explicit. The second cross product
        // re-orthogonalises forward after right was computed.
        Vector3 forward = NormalizeOrZero(ProjectOnPlane(desiredForward, normal));
        if (forward.LengthSquared() <= Epsilon * Epsilon)
            forward = NormalizeOrZero(ProjectOnPlane(previousForward, normal));
        if (forward.LengthSquared() <= Epsilon * Epsilon)
            forward = BuildFallbackTangent(normal, previousForward);
        if (forward.LengthSquared() <= Epsilon * Epsilon)
            return Quaternion.Identity;

        Vector3 right = NormalizeOrZero(Vector3.Cross(forward, normal));
        if (right.LengthSquared() <= Epsilon * Epsilon)
            return Quaternion.Identity;
        forward = NormalizeOrZero(Vector3.Cross(normal, right));
        if (forward.LengthSquared() <= Epsilon * Epsilon)
            return Quaternion.Identity;

        // Cross(forward, up) is the requested local right convention. The
        // render/physics matrix is right-handed, therefore its physical right
        // row is the negated convention vector while up and forward remain
        // exactly the requested directions.
        Vector3 matrixRight = -right;
        Matrix4x4 rotation = new(
            matrixRight.X, matrixRight.Y, matrixRight.Z, 0f,
            normal.X, normal.Y, normal.Z, 0f,
            forward.X, forward.Y, forward.Z, 0f,
            0f, 0f, 0f, 1f);
        return Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(rotation));
    }

    private static Quaternion SmoothRotation(Quaternion current, Quaternion target, float dt)
    {
        float blend = 1f - MathF.Exp(-OrientationResponse * dt);
        return Quaternion.Normalize(Quaternion.Slerp(current, target, blend));
    }

    private static Vector3 TransportAcrossSurface(
        Vector3 direction,
        Vector3 oldNormal,
        Vector3 newNormal,
        Vector3 previousForward)
    {
        oldNormal = NormalizeOrZero(oldNormal);
        newNormal = NormalizeOrZero(newNormal);
        if (newNormal.LengthSquared() <= Epsilon * Epsilon)
            return NormalizeOrZero(direction);
        if (oldNormal.LengthSquared() <= Epsilon * Epsilon)
            return BuildTangent(newNormal, direction, previousForward);

        Vector3 tangent = NormalizeOrZero(ProjectOnPlane(direction, oldNormal));
        if (tangent.LengthSquared() <= Epsilon * Epsilon)
            tangent = BuildTangent(oldNormal, previousForward, Vector3.Zero);

        float dot = System.Math.Clamp(Vector3.Dot(oldNormal, newNormal), -1f, 1f);
        Vector3 axis = Vector3.Cross(oldNormal, newNormal);
        if (axis.LengthSquared() > Epsilon * Epsilon)
        {
            Quaternion rotation = Quaternion.CreateFromAxisAngle(
                Vector3.Normalize(axis),
                MathF.Acos(dot));
            tangent = Vector3.Transform(tangent, rotation);
        }
        else if (dot < 0f)
        {
            Vector3 flipAxis = BuildFallbackTangent(oldNormal, tangent);
            if (flipAxis.LengthSquared() > Epsilon * Epsilon)
            {
                Quaternion rotation = Quaternion.CreateFromAxisAngle(flipAxis, MathF.PI);
                tangent = Vector3.Transform(tangent, rotation);
            }
        }

        return BuildTangent(newNormal, tangent, previousForward);
    }

    private static Vector3 BuildTangent(Vector3 normal, Vector3 desired, Vector3 fallback)
    {
        normal = NormalizeOrZero(normal);
        if (normal.LengthSquared() <= Epsilon * Epsilon)
            return Vector3.Zero;

        Vector3 tangent = NormalizeOrZero(ProjectOnPlane(desired, normal));
        if (tangent.LengthSquared() <= Epsilon * Epsilon)
            tangent = NormalizeOrZero(ProjectOnPlane(fallback, normal));
        if (tangent.LengthSquared() <= Epsilon * Epsilon)
            tangent = BuildFallbackTangent(normal, desired);
        return tangent;
    }

    private static Vector3 ResolveForward(Vector3 desired, Vector3 normal, Vector3 previous)
    {
        Vector3 resolved = BuildTangent(normal, desired, previous);
        return resolved.LengthSquared() > Epsilon * Epsilon
            ? resolved
            : NormalizeOrZero(previous);
    }

    private static Vector3 SmoothSurfaceNormal(Vector3 current, Vector3 target, float dt)
    {
        current = NormalizeOrZero(current);
        target = NormalizeOrZero(target);
        if (target.LengthSquared() <= Epsilon * Epsilon)
            return current;
        if (current.LengthSquared() <= Epsilon * Epsilon)
            return target;

        float blend = 1f - MathF.Exp(-SurfaceNormalResponse * dt);
        Vector3 result = NormalizeOrZero(Vector3.Lerp(current, target, blend));
        return result.LengthSquared() > Epsilon * Epsilon ? result : current;
    }

    private static Vector3 BuildFallbackTangent(Vector3 normal, Vector3 preferred)
    {
        normal = NormalizeOrZero(normal);
        if (normal.LengthSquared() <= Epsilon * Epsilon)
            return Vector3.Zero;

        Vector3 tangent = NormalizeOrZero(ProjectOnPlane(preferred, normal));
        if (tangent.LengthSquared() > Epsilon * Epsilon)
            return tangent;

        // These are only an arbitrary reference set for the degenerate case;
        // no one of them is treated as world up or as a movement direction.
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
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    public void OnDrawGizmos(DebugDrawer drawer)
    {
        if (_disposed)
            return;

        Vector3 position = Character.Position;
        Vector3 currentNormal = _surfaceNormal;
        Vector3 desiredNormal = _desiredSurfaceNormal;
        Vector3 forward = _surfaceForward;
        Vector3 right = Right;
        Vector3 desiredDirection = _desiredDirection;
        Vector3 surfaceColor = HasSurface
            ? new Vector3(0.15f, 1f, 0.25f)
            : new Vector3(1f, 0.2f, 0.15f);

        drawer.DrawCapsule(position, Character.Rotation, _body.CapsuleHeight * 0.5f, _body.CapsuleRadius, new Vector3(0.2f, 0.8f, 1f));
        drawer.PushLine(position, position + currentNormal * 1.25f, surfaceColor);
        drawer.PushLine(position, position + desiredNormal * 1.0f, new Vector3(1f, 0.2f, 0.85f));
        drawer.PushLine(position, position + forward * 1.5f, new Vector3(0.1f, 0.4f, 1f));
        drawer.PushLine(position, position + right * 1.0f, new Vector3(1f, 0.65f, 0.1f));
        drawer.PushLine(position, position + desiredDirection * 1.15f, new Vector3(0.1f, 1f, 1f));
        drawer.PushLine(position, position + _requestedVelocity * 0.20f, new Vector3(0.2f, 0.55f, 1f));
        drawer.DrawSphere(_debugPredictedPosition, Quaternion.Identity, 0.08f, IsBlocked ? new Vector3(1f, 0.15f, 0.1f) : new Vector3(0.2f, 1f, 0.8f));
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Character.Dispose();
        _shape.Dispose();
    }
}
