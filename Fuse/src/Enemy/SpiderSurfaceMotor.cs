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
    private float _lostSurfaceTime;
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
    public float Clearance { get; }

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
        Character = new CharacterVirtual(settings, ref position, ref rotation, 0, physics.Native);

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
        _solver.BeginFrame();

        Vector3 startPosition = Character.Position;
        if (_surfaceContact.IsValid)
        {
            _surfaceContact = _solver.Refresh(_surfaceContact);
            SetDesiredSurfaceNormal(_surfaceContact.Normal);
        }

        Vector3 up = NormalizeOrFallback(_surfaceNormal, _desiredSurfaceNormal);
        Vector3 tangent = BuildTangent(up, intendedDirection, _surfaceForward);
        if (tangent.LengthSquared() <= Epsilon * Epsilon)
            tangent = BuildTangent(up, _movementDirection, _surfaceForward);

        RefreshOrAcquireSupport(startPosition, up, tangent);
        up = SmoothSurfaceNormal(up, _desiredSurfaceNormal, dt);
        _surfaceNormal = up;
        tangent = BuildTangent(up, tangent, _surfaceForward);

        float commandSpeed = intendedSpeed;
        float stepDistance = commandSpeed * dt;
        _debugPredictedPosition = startPosition + tangent * stepDistance;

        if (HasSurface && commandSpeed > Epsilon)
        {
            bool supportAhead = _solver.TryFindSupportContact(
                _debugPredictedPosition,
                up,
                tangent,
                Clearance,
                _body.Native,
                out SpiderSurfaceContact aheadContact);

            bool hasTransition = _solver.TryFindTransitionContact(
                startPosition,
                up,
                tangent,
                Clearance,
                stepDistance + TransitionLookAhead,
                _body.Native,
                out SpiderSurfaceContact transitionContact);

            if (hasTransition)
            {
                ApplySurfaceContact(transitionContact, up, ref tangent, dt);
                up = _surfaceNormal;
            }
            else if (supportAhead)
            {
                ApplySurfaceContact(aheadContact, up, ref tangent, dt);
                up = _surfaceNormal;
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
            _requestedVelocity = tangent * commandSpeed - up * AdhesionSpeed;
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
            Character.Update(dt, ref _objectLayer, _physics.Native, bodyFilter, shapeFilter);
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
        if (_solver.TryFindSupportContact(
                position,
                up,
                forward,
                Clearance,
                _body.Native,
                out SpiderSurfaceContact support))
        {
            _surfaceContact = support;
            _lostSurfaceTime = 0f;
            SetDesiredSurfaceNormal(support.Normal);
            return;
        }

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

        tangent = TransportAcrossSurface(tangent, previousNormal, nextNormal, _surfaceForward);
        _surfaceContact = contact;
        _lostSurfaceTime = 0f;
        _desiredSurfaceNormal = nextNormal;
        _surfaceNormal = SmoothSurfaceNormal(previousNormal, nextNormal, dt);
        tangent = BuildTangent(_surfaceNormal, tangent, _surfaceForward);
    }

    private void UpdatePostMoveContact(float dt, ref Vector3 position, Vector3 previousUp)
    {
        Vector3 up = _surfaceNormal;
        if (_solver.TryFindSupportContact(
                position,
                up,
                _movementDirection,
                Clearance,
                _body.Native,
                out SpiderSurfaceContact support))
        {
            _surfaceContact = support;
            _lostSurfaceTime = 0f;
            SetDesiredSurfaceNormal(support.Normal);

            Vector3 desiredCenter = support.Point + support.Normal * Clearance;
            float outwardError = Vector3.Dot(desiredCenter - position, support.Normal);
            if (outwardError > 0.01f && outwardError < Clearance)
            {
                float correction = MathF.Min(outwardError, OutwardCorrectionSpeed * dt);
                position += support.Normal * correction;
                Character.Position = position;
            }
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
