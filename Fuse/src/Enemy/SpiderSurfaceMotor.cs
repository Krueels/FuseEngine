using System;
using System.Numerics;
using Fuse.Debug;
using Fuse.Physics;
using JoltPhysicsSharp;

namespace Fuse.Enemy;

/// <summary>
/// Collision-safe locomotion motor for a creature whose local up axis follows
/// an arbitrary surface. Patrol supplies intent; this class owns adhesion,
/// surface transitions and all physical movement.
/// </summary>
public sealed class SpiderSurfaceMotor : IGizmoDrawable, IDisposable
{
    private const float Epsilon = 0.0001f;
    private const float SurfaceLostGrace = 0.18f;
    private const float AdhesionSpeed = 2.6f;
    private const float MaxAirSpeed = 30f;
    private const float TransitionLookAhead = 0.38f;
    private const float OutwardCorrectionSpeed = 4.5f;

    private readonly PhysicsWorld _physics;
    private readonly RigidBody _body;
    private readonly SpiderSurfaceSolver _solver;
    private readonly CapsuleShape _shape;
    private readonly Vector3 _safeSpawn;
    private ObjectLayer _objectLayer;
    private SpiderSurfaceContact _surfaceContact;
    private Vector3 _movementDirection;
    private Vector3 _airVelocity;
    private Vector3 _requestedVelocity;
    private Vector3 _debugPredictedPosition;
    private float _lostSurfaceTime;
    private bool _disposed;

    public CharacterVirtual Character { get; }
    public SpiderSurfaceContact SurfaceContact => _surfaceContact;
    public Vector3 SurfaceNormal => _surfaceContact.IsValid
        ? _surfaceContact.Normal
        : NormalizeOrFallback(Character.Up, Vector3.UnitY);
    public Vector3 MovementDirection => _movementDirection;
    public Vector3 CurrentVelocity { get; private set; }
    public float CurrentSpeed { get; private set; }
    public bool IsBlocked { get; private set; }
    public bool HasSurface => _surfaceContact.IsValid && _lostSurfaceTime <= SurfaceLostGrace;
    public float Clearance { get; }

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

        initialNormal = NormalizeOrFallback(initialNormal, Vector3.UnitY);
        initialForward = ProjectOnPlane(initialForward, initialNormal);
        if (initialForward.LengthSquared() <= Epsilon * Epsilon)
            initialForward = BuildFallbackTangent(initialNormal);
        initialForward = Vector3.Normalize(initialForward);
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

        Quaternion rotation = RotationFromSurface(initialNormal, initialForward);
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
            _surfaceContact = initialContact;
            Character.Up = initialContact.Normal;
            Character.Rotation = RotationFromSurface(initialContact.Normal, initialForward);
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
            _surfaceContact = _solver.Refresh(_surfaceContact);

        Vector3 up = SurfaceNormal;
        Vector3 tangent = ProjectOnPlane(intendedDirection, up);
        if (tangent.LengthSquared() <= Epsilon * Epsilon)
            tangent = ProjectOnPlane(_movementDirection, up);
        tangent = NormalizeOrFallback(tangent, BuildFallbackTangent(up));

        RefreshOrAcquireSupport(startPosition, up, tangent);
        up = SurfaceNormal;
        tangent = NormalizeOrFallback(ProjectOnPlane(tangent, up), BuildFallbackTangent(up));

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
                tangent = TransportAcrossSurface(tangent, up, transitionContact.Normal);
                _surfaceContact = transitionContact;
                _lostSurfaceTime = 0f;
                up = transitionContact.Normal;
            }
            else if (supportAhead)
            {
                _surfaceContact = aheadContact;
                _lostSurfaceTime = 0f;
                up = aheadContact.Normal;
                tangent = NormalizeOrFallback(ProjectOnPlane(tangent, up), _movementDirection);
            }
            else
            {
                // Walking into unsupported space is not allowed. The patrol can
                // choose another heading while the body remains safely planted.
                commandSpeed = 0f;
                IsBlocked = true;
            }
        }

        Quaternion targetRotation = RotationFromSurface(up, tangent);
        Character.Up = up;
        Character.Rotation = targetRotation;

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
        CurrentSpeed = HasSurface ? ProjectOnPlane(actualVelocity, up).Length() : actualVelocity.Length();
        _movementDirection = NormalizeOrFallback(ProjectOnPlane(tangent, up), BuildFallbackTangent(up));

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

        if (Character.Position.Y < -50f)
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
            Character.Up = acquired.Normal;
        }
    }

    private void UpdatePostMoveContact(float dt, ref Vector3 position, Vector3 previousUp)
    {
        Vector3 up = SurfaceNormal;
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
            _surfaceContact = acquired;
            _lostSurfaceTime = 0f;
            Character.Up = acquired.Normal;
            _movementDirection = NormalizeOrFallback(
                ProjectOnPlane(_movementDirection, acquired.Normal),
                BuildFallbackTangent(acquired.Normal));
            return;
        }

        _surfaceContact = default;
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

    private static Quaternion RotationFromSurface(Vector3 normal, Vector3 desiredForward)
    {
        normal = NormalizeOrFallback(normal, Vector3.UnitY);
        Vector3 forward = ProjectOnPlane(desiredForward, normal);
        forward = NormalizeOrFallback(forward, BuildFallbackTangent(normal));
        Vector3 right = NormalizeOrFallback(Vector3.Cross(normal, forward), Vector3.UnitX);
        forward = NormalizeOrFallback(Vector3.Cross(right, normal), forward);

        Matrix4x4 rotation = new(
            right.X, right.Y, right.Z, 0f,
            normal.X, normal.Y, normal.Z, 0f,
            forward.X, forward.Y, forward.Z, 0f,
            0f, 0f, 0f, 1f);
        return Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(rotation));
    }

    private static Vector3 TransportAcrossSurface(Vector3 direction, Vector3 oldNormal, Vector3 newNormal)
    {
        oldNormal = NormalizeOrFallback(oldNormal, Vector3.UnitY);
        newNormal = NormalizeOrFallback(newNormal, oldNormal);
        float dot = System.Math.Clamp(Vector3.Dot(oldNormal, newNormal), -1f, 1f);
        Vector3 transported = direction;

        if (dot < 0.9999f)
        {
            Vector3 axis = Vector3.Cross(oldNormal, newNormal);
            if (axis.LengthSquared() > Epsilon * Epsilon)
            {
                Quaternion rotation = Quaternion.CreateFromAxisAngle(
                    Vector3.Normalize(axis),
                    MathF.Acos(dot));
                transported = Vector3.Transform(direction, rotation);
            }
        }

        return NormalizeOrFallback(
            ProjectOnPlane(transported, newNormal),
            BuildFallbackTangent(newNormal));
    }

    private static Vector3 BuildFallbackTangent(Vector3 normal)
    {
        normal = NormalizeOrFallback(normal, Vector3.UnitY);
        Vector3 tangent = ProjectOnPlane(Vector3.UnitZ, normal);
        if (tangent.LengthSquared() <= Epsilon * Epsilon)
            tangent = ProjectOnPlane(Vector3.UnitX, normal);
        return NormalizeOrFallback(tangent, Vector3.UnitX);
    }

    private static Vector3 ProjectOnPlane(Vector3 value, Vector3 normal) =>
        value - normal * Vector3.Dot(value, normal);

    private static Vector3 NormalizeOrFallback(Vector3 value, Vector3 fallback)
    {
        if (value.LengthSquared() > Epsilon * Epsilon)
            return Vector3.Normalize(value);
        if (fallback.LengthSquared() > Epsilon * Epsilon)
            return Vector3.Normalize(fallback);
        return Vector3.UnitZ;
    }

    public void OnDrawGizmos(DebugDrawer drawer)
    {
        if (_disposed)
            return;

        Vector3 position = Character.Position;
        Vector3 up = SurfaceNormal;
        Vector3 surfaceColor = HasSurface
            ? new Vector3(0.15f, 1f, 0.25f)
            : new Vector3(1f, 0.2f, 0.15f);

        drawer.DrawCapsule(position, Character.Rotation, _body.CapsuleHeight * 0.5f, _body.CapsuleRadius, new Vector3(0.2f, 0.8f, 1f));
        drawer.PushLine(position, position + up * 1.25f, surfaceColor);
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
