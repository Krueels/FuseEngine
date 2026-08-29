using System;
using System.Numerics;
using Fuse.Behaviours;
using Fuse.Debug;
using Fuse.Physics;

namespace Fuse.Enemy;

/// <summary>
/// High-level wandering intent for the spider. This class never changes the
/// rigid body directly; SpiderSurfaceMotor owns every physical displacement.
/// </summary>
public sealed class SpiderPatrol : IGizmoDrawable
{
    public static bool Enabled = true;

    private enum PatrolState { Idle, Walking }

    private readonly SpiderEnemy _enemy;
    private readonly PhysicsWorld _physics;
    private readonly SpiderSurfaceMotor _motor;
    private PatrolState _state = PatrolState.Idle;
    private Vector3 _travelDirection = Vector3.Zero;
    private Vector3 _targetPosition;
    private float _remainingTravel;
    private float _waitTimer;
    private float _waitDuration;
    private float _blockedTimer;
    private bool _initialized;

    public float CurrentSpeed { get; private set; }
    public Vector3 CurrentVelocity { get; private set; }
    public Vector3 CurrentPosition => _enemy.Body.IsBuilt
        ? _enemy.Body.Position(_physics)
        : Vector3.Zero;
    public Vector3 SurfaceNormal => _motor.SurfaceNormal;
    public SpiderSurfaceContact SurfaceContact => _motor.SurfaceContact;
    public bool HasSurface => _motor.HasSurface;
    public bool IsBlocked => _motor.IsBlocked;
    public float Clearance => _motor.Clearance;
    public bool IsMovementReady => !_enemy.IsDead && _enemy.Body.IsBuilt;

    [Export] public float PatrolRadius { get; set; } = 20f;
    [Export] public float MoveSpeed { get; set; } = 9.5f;
    [Export] public float Acceleration { get; set; } = 8f;
    [Export] public float Deceleration { get; set; } = 10f;
    [Export] public float WalkAnimSpeed { get; set; } = 0.5f;
    [Export] public float MinWaitTime { get; set; } = 0.7f;
    [Export] public float MaxWaitTime { get; set; } = 2.0f;
    [Export] public float MinTravelDistance { get; set; } = 4f;

    private static readonly Random s_random = new();

    public SpiderPatrol(SpiderEnemy enemy, PhysicsWorld physics, SpiderSurfaceMotor motor)
    {
        _enemy = enemy;
        _physics = physics;
        _motor = motor;
        DebugDrawer.Register(this);
    }

    public void Update(float dt)
    {
        if (!Enabled || _enemy.IsDead || !_enemy.Body.IsBuilt)
            return;

        if (!_initialized)
        {
            _travelDirection = NormalizeOrFallback(_motor.MovementDirection, _motor.Forward);
            _waitDuration = RandomWaitTime();
            _targetPosition = _enemy.Body.Position(_physics);
            _initialized = true;
        }

        dt = System.Math.Clamp(dt, 0.0001f, 0.05f);
        Vector3 positionBefore = _enemy.Body.Position(_physics);

        if (_state == PatrolState.Idle)
        {
            CurrentSpeed = MathF.Max(0f, CurrentSpeed - Deceleration * dt);
            _motor.Update(dt, _travelDirection, 0f);
            _waitTimer += dt;

            if (_waitTimer >= _waitDuration && _motor.HasSurface)
                BeginWalking();
        }
        else
        {
            float stoppingDistance = CurrentSpeed * CurrentSpeed / MathF.Max(0.01f, 2f * Deceleration);
            if (_remainingTravel <= stoppingDistance + 0.4f)
                CurrentSpeed = MathF.Max(0f, CurrentSpeed - Deceleration * dt);
            else
                CurrentSpeed = MathF.Min(MoveSpeed, CurrentSpeed + Acceleration * dt);

            _motor.Update(dt, _travelDirection, CurrentSpeed);
            _travelDirection = NormalizeOrFallback(
                _motor.MovementDirection,
                ProjectOnPlane(_travelDirection, _motor.SurfaceNormal));

            Vector3 positionAfter = _enemy.Body.Position(_physics);
            float travelled = ProjectOnPlane(positionAfter - positionBefore, _motor.SurfaceNormal).Length();
            _remainingTravel = MathF.Max(0f, _remainingTravel - travelled);
            _targetPosition = positionAfter + _travelDirection * _remainingTravel;

            if (_motor.IsBlocked)
                _blockedTimer += dt;
            else
                _blockedTimer = MathF.Max(0f, _blockedTimer - dt * 2f);

            if (_remainingTravel <= 0.35f || CurrentSpeed <= 0.05f && _remainingTravel < 0.8f)
            {
                BeginIdle();
            }
            else if (_blockedTimer > 0.35f)
            {
                BeginIdle(0.18f, 0.45f);
            }
        }

        CurrentVelocity = _motor.CurrentVelocity;
        CurrentSpeed = _state == PatrolState.Walking ? _motor.CurrentSpeed : 0f;
        UpdateAnimationSpeed();
    }

    private void BeginWalking()
    {
        Vector3 normal = _motor.SurfaceNormal;
        BuildTangentBasis(normal, _motor.MovementDirection, out Vector3 forward, out Vector3 right);

        float angle = (float)(s_random.NextDouble() * MathF.Tau);
        _travelDirection = NormalizeOrFallback(
            forward * MathF.Cos(angle) + right * MathF.Sin(angle),
            forward);

        float minimum = MathF.Min(MinTravelDistance, MathF.Max(1f, PatrolRadius));
        _remainingTravel = minimum + (float)s_random.NextDouble() * MathF.Max(0.5f, PatrolRadius - minimum);
        _targetPosition = _enemy.Body.Position(_physics) + _travelDirection * _remainingTravel;
        _blockedTimer = 0f;
        _waitTimer = 0f;
        _state = PatrolState.Walking;
        PlayWalkAnimation();
    }

    private void BeginIdle(float? minimumWait = null, float? maximumWait = null)
    {
        _state = PatrolState.Idle;
        CurrentSpeed = 0f;
        CurrentVelocity = Vector3.Zero;
        _waitTimer = 0f;
        _blockedTimer = 0f;
        _waitDuration = minimumWait.HasValue && maximumWait.HasValue
            ? minimumWait.Value + (float)s_random.NextDouble() * (maximumWait.Value - minimumWait.Value)
            : RandomWaitTime();
        _targetPosition = _enemy.Body.Position(_physics);
        PlayIdleAnimation();
    }

    private float RandomWaitTime() =>
        MinWaitTime + (float)s_random.NextDouble() * MathF.Max(0f, MaxWaitTime - MinWaitTime);

    private void UpdateAnimationSpeed()
    {
        var animator = _enemy.Entity?.Animator;
        if (animator != null)
            animator.Speed = MoveSpeed > 0.001f ? CurrentSpeed / MoveSpeed * WalkAnimSpeed : 0f;
    }

    private void PlayIdleAnimation()
    {
        var animator = _enemy.Entity?.Animator;
        if (animator == null)
            return;
        if (animator.GetClip("Idle") != null)
            animator.CrossFade("Idle", 0.25f);
        else if (!string.IsNullOrEmpty(animator.Model?.DefaultClipName))
            animator.CrossFade(animator.Model.DefaultClipName, 0.25f);
    }

    private void PlayWalkAnimation()
    {
        var animator = _enemy.Entity?.Animator;
        if (animator?.GetClip("Walk") != null)
            animator.CrossFade("Walk", 0.25f);
    }

    private static void BuildTangentBasis(
        Vector3 normal,
        Vector3 desiredForward,
        out Vector3 forward,
        out Vector3 right)
    {
        normal = NormalizeOrZero(normal);
        if (normal.LengthSquared() <= 0.0001f)
        {
            forward = Vector3.Zero;
            right = Vector3.Zero;
            return;
        }

        forward = NormalizeOrZero(ProjectOnPlane(desiredForward, normal));
        if (forward.LengthSquared() <= 0.0001f)
            forward = BuildFallbackTangent(normal, desiredForward);

        right = NormalizeOrZero(Vector3.Cross(forward, normal));
        forward = NormalizeOrZero(Vector3.Cross(normal, right));
    }

    private static Vector3 ProjectOnPlane(Vector3 value, Vector3 normal) =>
        value - normal * Vector3.Dot(value, normal);

    private static Vector3 NormalizeOrFallback(Vector3 value, Vector3 fallback)
    {
        if (value.LengthSquared() > 0.0001f)
            return Vector3.Normalize(value);
        if (fallback.LengthSquared() > 0.0001f)
            return Vector3.Normalize(fallback);
        return Vector3.Zero;
    }

    private static Vector3 NormalizeOrZero(Vector3 value) =>
        value.LengthSquared() > 0.0001f ? Vector3.Normalize(value) : Vector3.Zero;

    private static Vector3 BuildFallbackTangent(Vector3 normal, Vector3 preferred)
    {
        normal = NormalizeOrZero(normal);
        if (normal.LengthSquared() <= 0.0001f)
            return Vector3.Zero;

        Vector3 tangent = NormalizeOrZero(ProjectOnPlane(preferred, normal));
        if (tangent.LengthSquared() > 0.0001f)
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

    public void OnDrawGizmos(DebugDrawer drawer)
    {
        if (_enemy.IsDead || !_enemy.Body.IsBuilt)
            return;

        Vector3 position = _enemy.Body.Position(_physics);
        Vector3 normal = _motor.SurfaceNormal;
        BuildTangentBasis(normal, _travelDirection, out Vector3 forward, out Vector3 right);

        Vector3 stateColor = _state == PatrolState.Walking
            ? new Vector3(0.1f, 1f, 0.25f)
            : new Vector3(1f, 0.85f, 0.15f);

        drawer.DrawBox(_targetPosition, Quaternion.Identity, new Vector3(0.14f), new Vector3(1f, 0.2f, 0.2f));
        drawer.PushLine(position, _targetPosition, stateColor);
        drawer.PushLine(position, position + _travelDirection * 2f, new Vector3(0.1f, 0.55f, 1f));

        const int segments = 32;
        Vector3 previous = position + forward * PatrolRadius;
        for (int i = 1; i <= segments; i++)
        {
            float angle = MathF.Tau * i / segments;
            Vector3 next = position + (forward * MathF.Cos(angle) + right * MathF.Sin(angle)) * PatrolRadius;
            drawer.PushLine(previous, next, new Vector3(0.35f, 0.35f, 0.1f));
            previous = next;
        }
    }
}
