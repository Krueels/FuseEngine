using System;
using System.Numerics;
using Fuse.Behaviours;
using Fuse.Physics;
using JoltPhysicsSharp;

namespace Fuse.Enemy;

/// <summary>
/// Supplies the spider with a desired route and moves its kinematic body only
/// when a stable surface contact exists at the next position. It intentionally
/// does not decide foot placement; that belongs to ProceduralSpiderWalk.
/// </summary>
public sealed class SpiderPatrol : Debug.IGizmoDrawable
{
    public static bool Enabled = true;

    private enum PatrolState { Idle, Walking, Airborne }

    private readonly SpiderEnemy _enemy;
    private readonly PhysicsWorld _physics;
    private readonly SpiderSurfaceSolver _surfaceSolver;
    private readonly Scene.SceneManager _scene;
    private PatrolState _state = PatrolState.Idle;
    private Vector3 _targetPosition;
    private Vector3 _travelForward = Vector3.UnitZ;
    private float _waitTimer;
    private float _waitDuration;
    private float _lostSurfaceTime;
    private float _fallSpeed;
    private bool _initialized;
    private Quaternion _currentRotation = Quaternion.Identity;
    private SpiderSurfaceContact _surfaceContact;

    private const float OrientationSmoothing = 10f;
    private const float GravityStrength = 20f;
    private const float MaxFallSpeed = 30f;
    private const float SurfaceLostGrace = 0.12f;
    private const float MaxSurfaceTransitionRadians = 110f * (MathF.PI / 180f);

    public float CurrentSpeed { get; private set; }
    public Vector3 CurrentVelocity { get; private set; }
    public Vector3 SurfaceNormal => _surfaceContact.IsValid ? _surfaceContact.Normal : Vector3.UnitY;
    public SpiderSurfaceContact SurfaceContact => _surfaceContact;
    public bool HasSurface => _surfaceContact.IsValid && _lostSurfaceTime <= SurfaceLostGrace;

    [Export] public float PatrolRadius { get; set; } = 20f;
    [Export] public float MoveSpeed { get; set; } = 15.5f;
    [Export] public float Acceleration { get; set; } = 5f;
    [Export] public float Deceleration { get; set; } = 8f;
    [Export] public float WalkAnimSpeed { get; set; } = 0.5f;
    [Export] public float MinWaitTime { get; set; } = 1f;
    [Export] public float MaxWaitTime { get; set; } = 3f;
    [Export] public float BodyClearance { get; set; } = 0.86f;

    private static readonly Random s_random = new();

    public SpiderPatrol(SpiderEnemy enemy, PhysicsWorld physics, SpiderSurfaceSolver surfaceSolver, Scene.SceneManager scene)
    {
        _enemy = enemy;
        _physics = physics;
        _surfaceSolver = surfaceSolver;
        _scene = scene;
        Debug.DebugDrawer.Register(this);
    }

    public void Update(float dt)
    {
        if (!Enabled || _enemy.IsDead || !_enemy.Body.IsBuilt)
            return;

        if (!_initialized)
        {
            _waitDuration = RandomWaitTime();
            _currentRotation = _enemy.Body.Rotation(_physics);
            _travelForward = NormalizeOrFallback(Vector3.Transform(Vector3.UnitZ, _currentRotation), Vector3.UnitZ);
            _initialized = true;
        }

        Vector3 bodyPosition = _enemy.Body.Position(_physics);
        Quaternion bodyRotation = _enemy.Body.Rotation(_physics);
        if (_surfaceContact.IsValid)
            _surfaceContact = _surfaceSolver.Refresh(_surfaceContact);

        Vector3 preferredUp = _surfaceContact.IsValid
            ? _surfaceContact.Normal
            : Vector3.Transform(Vector3.UnitY, bodyRotation);
        Vector3 preferredForward = CurrentVelocity.LengthSquared() > 0.001f
            ? CurrentVelocity
            : Vector3.Transform(Vector3.UnitZ, bodyRotation);

        if (_surfaceSolver.TryFindBodyContact(
                bodyPosition,
                preferredUp,
                preferredForward,
                _surfaceContact,
                _enemy.Body.Native,
                out var foundContact))
        {
            _surfaceContact = foundContact;
            _lostSurfaceTime = 0f;
            _fallSpeed = 0f;
        }
        else
        {
            _lostSurfaceTime += dt;
        }

        if (!HasSurface)
        {
            ApplyGravity(dt);
            _state = PatrolState.Idle;
            CurrentSpeed = 0f;

            Vector3 pos = _enemy.Body.Position(_physics);
            if (pos.Y < -50f)
            {
                _physics.BodyInterface.SetPosition(_enemy.Body.Native, new Vector3(pos.X, 5f, pos.Z), Activation.Activate);
                _fallSpeed = 0f;
                CurrentVelocity = Vector3.Zero;
            }
            return;
        }

        Vector3 facingDir = _travelForward;
        if (_state == PatrolState.Walking)
        {
            Vector3 toTarget = ProjectOnPlane(_targetPosition - bodyPosition, _surfaceContact.Normal);
            if (toTarget.LengthSquared() > 0.001f)
                facingDir = Vector3.Normalize(toTarget);
        }

        ConformToSurface(dt, facingDir);

        switch (_state)
        {
            case PatrolState.Idle:
                UpdateIdle(dt);
                break;
            case PatrolState.Walking:
                UpdateWalking(dt);
                break;
            case PatrolState.Airborne:
                _state = PatrolState.Idle;
                _waitTimer = 0f;
                _waitDuration = RandomWaitTime();
                PlayIdleAnimation();
                break;
        }
    }

    private void ConformToSurface(float dt, Vector3 facingDir)
    {
        Vector3 position = _enemy.Body.Position(_physics);
        Vector3 normal = _surfaceContact.Normal;
        Vector3 targetCenter = _surfaceContact.Point + normal * BodyClearance;
        float normalError = Vector3.Dot(targetCenter - position, normal);
        float adhesion = 1f - MathF.Exp(-18f * dt);
        position += normal * normalError * adhesion;
        _physics.BodyInterface.SetPosition(_enemy.Body.Native, position, Activation.Activate);

        Quaternion targetRotation = RotationFromSurface(normal, facingDir);
        float blend = 1f - MathF.Exp(-OrientationSmoothing * dt);
        _currentRotation = Quaternion.Normalize(Quaternion.Slerp(_currentRotation, targetRotation, blend));
        _physics.BodyInterface.SetRotation(_enemy.Body.Native, _currentRotation, Activation.Activate);
    }

    private void UpdateIdle(float dt)
    {
        CurrentVelocity = Vector3.Zero;
        _waitTimer += dt;
        if (_waitTimer < _waitDuration)
            return;

        _targetPosition = PickSurfaceTarget();
        _state = PatrolState.Walking;
        CurrentSpeed = 0f;
        PlayWalkAnimation();
    }

    private void UpdateWalking(float dt)
    {
        Vector3 position = _enemy.Body.Position(_physics);
        Vector3 toTarget = ProjectOnPlane(_targetPosition - position, _surfaceContact.Normal);
        float remainingDistance = toTarget.Length();
        if (remainingDistance < 0.35f)
        {
            BeginIdle();
            return;
        }

        Vector3 moveDirection = toTarget / remainingDistance;
        float decelerationDistance = MoveSpeed * 0.9f;
        if (remainingDistance < decelerationDistance)
            CurrentSpeed = MathF.Max(CurrentSpeed - Deceleration * dt, remainingDistance / decelerationDistance * MoveSpeed);
        else
            CurrentSpeed = MathF.Min(CurrentSpeed + Acceleration * dt, MoveSpeed);

        float stepDistance = CurrentSpeed * dt;
        Vector3 predictedPosition = position + moveDirection * stepDistance;
        if (!_surfaceSolver.TryFindBodyContact(
                predictedPosition,
                _surfaceContact.Normal,
                moveDirection,
                _surfaceContact,
                _enemy.Body.Native,
                out var nextContact) ||
            AngleBetween(_surfaceContact.Normal, nextContact.Normal) > MaxSurfaceTransitionRadians)
        {
            CurrentSpeed = MathF.Max(0f, CurrentSpeed - Deceleration * dt);
            CurrentVelocity = Vector3.Zero;
            _targetPosition = PickSurfaceTarget();
            return;
        }

        Vector3 targetCenter = nextContact.Point + nextContact.Normal * BodyClearance;
        predictedPosition += nextContact.Normal * Vector3.Dot(targetCenter - predictedPosition, nextContact.Normal);
        _physics.BodyInterface.SetPosition(_enemy.Body.Native, predictedPosition, Activation.Activate);

        float surfaceTurn = AngleBetween(_surfaceContact.Normal, nextContact.Normal);
        if (surfaceTurn > 0.02f)
        {
            _travelForward = TransportAcrossSurface(moveDirection, _surfaceContact.Normal, nextContact.Normal);
            float continuedDistance = MathF.Max(remainingDistance, MathF.Max(2.5f, PatrolRadius * 0.35f));
            _targetPosition = predictedPosition + _travelForward * continuedDistance;
        }
        else
        {
            _travelForward = NormalizeOrFallback(ProjectOnPlane(moveDirection, nextContact.Normal), _travelForward);
        }

        _surfaceContact = nextContact;
        CurrentVelocity = (predictedPosition - position) / MathF.Max(dt, 0.0001f);

        var animator = _enemy.Entity?.Animator;
        if (animator != null)
            animator.Speed = MoveSpeed > 0.001f ? CurrentSpeed / MoveSpeed * WalkAnimSpeed : 0f;
    }

    private void ApplyGravity(float dt)
    {
        _fallSpeed = MathF.Min(_fallSpeed + GravityStrength * dt, MaxFallSpeed);
        CurrentVelocity = -Vector3.UnitY * _fallSpeed;
        Vector3 position = _enemy.Body.Position(_physics) + CurrentVelocity * dt;
        _physics.BodyInterface.SetPosition(_enemy.Body.Native, position, Activation.Activate);
    }

    private Vector3 PickSurfaceTarget()
    {
        Vector3 position = _enemy.Body.Position(_physics);
        BuildTangentBasis(_surfaceContact.Normal, _travelForward, out Vector3 forward, out Vector3 right);
        float radius = 2f + (float)s_random.NextDouble() * MathF.Max(1f, PatrolRadius - 2f);

        Vector3 bestTarget = position;
        bool hasTarget = false;

        for (int i = 0; i < 10; i++)
        {
            float angle = (float)(s_random.NextDouble() * MathF.Tau);
            Vector3 dir = Vector3.Normalize(forward * MathF.Cos(angle) + right * MathF.Sin(angle));
            Vector3 candidate = position + dir * radius;

            // A candidate is projected back towards the active support normal,
            // not towards world-down. This is what keeps a wall-walking spider
            // on a +X or -X wall instead of sending it to the floor.
            if (_surfaceSolver.TryProjectToSurface(candidate, _surfaceContact.Normal, _enemy.Body.Native, out var surface))
            {
                bestTarget = surface.Point + surface.Normal * BodyClearance;
                hasTarget = true;
                break;
            }
        }

        // No valid projection means the surface has ended. Returning the
        // current center makes patrol pause and retry rather than walking into
        // unsupported space or selecting the world floor.
        return hasTarget ? bestTarget : position;
    }

    private void BeginIdle()
    {
        _state = PatrolState.Idle;
        CurrentSpeed = 0f;
        CurrentVelocity = Vector3.Zero;
        _waitTimer = 0f;
        _waitDuration = RandomWaitTime();
        PlayIdleAnimation();
    }

    private static Quaternion RotationFromSurface(Vector3 normal, Vector3 desiredForward)
    {
        BuildTangentBasis(normal, desiredForward, out Vector3 forward, out Vector3 right);

        // Montagem correta da matriz por LINHAS (System.Numerics):
        // Linha 1 = Eixo X local (Direita / Right)
        // Linha 2 = Eixo Y local (Cima / Normal)
        // Linha 3 = Eixo Z local (Frente / Forward)
        Matrix4x4 rotation = new Matrix4x4(
            right.X, right.Y, right.Z, 0f,
            normal.X, normal.Y, normal.Z, 0f,
            forward.X, forward.Y, forward.Z, 0f,
            0f, 0f, 0f, 1f
        );

        return Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(rotation));
    }

    private static void BuildTangentBasis(Vector3 normal, Vector3 desiredForward, out Vector3 forward, out Vector3 right)
    {
        normal = NormalizeOrFallback(normal, Vector3.UnitY);
        forward = ProjectOnPlane(desiredForward, normal);
        if (forward.LengthSquared() < 0.0001f)
        {
            // Keep a consistent tangent for normals +X and -X. The old
            // Cross(UnitY, normal) fallback inverted the heading by 180°.
            forward = ProjectOnPlane(Vector3.UnitZ, normal);
            if (forward.LengthSquared() < 0.0001f)
                forward = ProjectOnPlane(Vector3.UnitX, normal);
        }
        forward = NormalizeOrFallback(forward, Vector3.UnitZ);
        right = NormalizeOrFallback(Vector3.Cross(normal, forward), Vector3.UnitX);
        forward = NormalizeOrFallback(Vector3.Cross(right, normal), forward);
    }

    private static Vector3 ProjectOnPlane(Vector3 vector, Vector3 normal) => vector - normal * Vector3.Dot(vector, normal);

    private static Vector3 NormalizeOrFallback(Vector3 value, Vector3 fallback) =>
        value.LengthSquared() > 0.0001f ? Vector3.Normalize(value) : Vector3.Normalize(fallback);

    private static Vector3 TransportAcrossSurface(Vector3 direction, Vector3 oldNormal, Vector3 newNormal)
    {
        oldNormal = NormalizeOrFallback(oldNormal, Vector3.UnitY);
        newNormal = NormalizeOrFallback(newNormal, oldNormal);
        float dot = System.Math.Clamp(Vector3.Dot(oldNormal, newNormal), -1f, 1f);

        Vector3 transported = direction;
        if (dot < 0.9999f)
        {
            Vector3 axis = Vector3.Cross(oldNormal, newNormal);
            if (axis.LengthSquared() > 0.0001f)
            {
                Quaternion rotation = Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), MathF.Acos(dot));
                transported = Vector3.Transform(direction, rotation);
            }
        }

        Vector3 tangent = ProjectOnPlane(transported, newNormal);
        if (tangent.LengthSquared() <= 0.0001f)
            tangent = ProjectOnPlane(_fallbackTangent(newNormal), newNormal);
        return NormalizeOrFallback(tangent, Vector3.UnitZ);

        static Vector3 _fallbackTangent(Vector3 normal) =>
            MathF.Abs(normal.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitZ;
    }

    private static float AngleBetween(Vector3 a, Vector3 b) =>
        MathF.Acos(System.Math.Clamp(Vector3.Dot(NormalizeOrFallback(a, Vector3.UnitY), NormalizeOrFallback(b, Vector3.UnitY)), -1f, 1f));

    private float RandomWaitTime() => MinWaitTime + (float)s_random.NextDouble() * (MaxWaitTime - MinWaitTime);

    private void PlayIdleAnimation()
    {
        var animator = _enemy.Entity?.Animator;
        if (animator == null) return;
        if (animator.GetClip("Idle") != null) animator.CrossFade("Idle", 1f);
        else if (!string.IsNullOrEmpty(animator.Model?.DefaultClipName)) animator.CrossFade(animator.Model.DefaultClipName, 1f);
    }

    private void PlayWalkAnimation()
    {
        var animator = _enemy.Entity?.Animator;
        if (animator?.GetClip("Walk") != null) animator.CrossFade("Walk", 1f);
    }

    public void OnDrawGizmos(Debug.DebugDrawer drawer)
    {
        if (_enemy.IsDead || !_enemy.Body.IsBuilt)
            return;

        Vector3 position = _enemy.Body.Position(_physics);
        drawer.DrawBox(_targetPosition, Quaternion.Identity, new Vector3(0.14f), new Vector3(1f, 0.2f, 0.2f));
        drawer.PushLine(position, _targetPosition, _state == PatrolState.Walking ? new Vector3(0.1f, 1f, 0.2f) : new Vector3(1f, 1f, 0.2f));
        drawer.PushLine(position, position + CurrentVelocity, new Vector3(0.1f, 0.5f, 1f));

        if (_surfaceContact.IsValid)
        {
            drawer.DrawSphere(_surfaceContact.Point, Quaternion.Identity, 0.08f, new Vector3(0.1f, 1f, 0.2f));
            drawer.PushLine(_surfaceContact.Point, _surfaceContact.Point + _surfaceContact.Normal * BodyClearance, new Vector3(0.1f, 1f, 0.2f));
        }
    }
}
