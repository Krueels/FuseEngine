using System;
using System.Collections.Generic;
using System.Drawing.Printing;
using System.Numerics;
using System.Text;
using Fuse.Behaviours;
using JoltPhysicsSharp;

namespace Fuse.Enemy;

public sealed class EnemyPatrol : Debug.IGizmoDrawable
{
    private enum PatrolState { Idle, Walking }

    private readonly Enemy _enemy;
    private readonly Physics.PhysicsWorld _physics;
    private PatrolState _state = PatrolState.Idle;
    private Vector3 _targetPos;
    private float _waitTimer;
    private float _waitDuration;
    private float _targetYRotation;
    private float _currentYRotation;
    private const float RotationSpeed = 8f;
    private float _currentSpeed;
    private bool _initialized;

    public float CurrentSpeed => _currentSpeed;

    [Export] public float PatrolRadius { get; set; } = 20f;
    [Export] public float MoveSpeed { get; set; } = 4f;
    [Export] public float Acceleration { get; set; } = 6f;
    [Export] public float Deceleration { get; set; } = 10f;
    [Export] public float WalkAnimSpeed { get; set; } = 0.5f;
    [Export] public float MinWaitTime { get; set; } = 1.5f;
    [Export] public float MaxWaitTime { get; set; } = 4f;

    private static readonly Random s_random = new();

    public EnemyPatrol(Enemy enemy, Physics.PhysicsWorld physics)
    {
        _enemy = enemy;
        _physics = physics;
        Debug.DebugDrawer.Register(this);

        Quaternion spawnRot = enemy.Body.Rotation(physics);
        _currentYRotation = MathF.Atan2(spawnRot.X, spawnRot.Z);
    }

    public void Update(float dt)
    {
        if (_enemy.IsDead || !_enemy.Entity?.Body?.IsBuilt == true) return;

        if (!_initialized)
        {
            _waitDuration = RandomWaitTime();
            _waitTimer = 0f;
            _initialized = true;
        }

        switch (_state)
        {
            case PatrolState.Idle:
                UpdateIdle(dt);
                break;
            case PatrolState.Walking:
                UpdateWalking(dt);
                break;
        }
    }

    private void UpdateIdle(float dt)
    {
        _waitTimer += dt;

        if (_waitTimer >= _waitDuration)
        {
            _targetPos = PickRandomPoint();
            _state = PatrolState.Walking;
            _currentSpeed = 0f;
            PlayWalkAnimation();
            _enemy.Entity?.Animator!.Speed = 0f;
        }
    }

    private void UpdateWalking(float dt)
    {
        Vector3 currentPos = _enemy.Body.Position(_physics);
        Vector3 dir = _targetPos - currentPos;
        dir.Y = 0f;
        float dist = dir.Length();

        if (dist < 0.3f)
        {
            _state = PatrolState.Idle;
            _currentSpeed = 0f;
            _waitTimer = 0f;
            _waitDuration = RandomWaitTime();
            PlayIdleAnimation();
            return;
        }

        // Dois raycasts: alto (paredes) e baixo (objetos como mesas)
        Vector3 moveDir = dir / dist;
        Vector3 rayHigh = currentPos + Vector3.UnitY * 1.0f;
        Vector3 rayLow = currentPos + Vector3.UnitY * 0.1f;
        if (RaycastSelf(rayHigh, moveDir, 1.5f) || RaycastSelf(rayLow, moveDir, 1.5f))
        {
            _targetPos = PickRandomPoint();
            return;
        }

        float decelDistance = MoveSpeed * 0.8f;
        if (dist < decelDistance)
            _currentSpeed = MathF.Max(_currentSpeed - Deceleration * dt, dist / decelDistance * MoveSpeed);
        else
            _currentSpeed = MathF.Min(_currentSpeed + Acceleration * dt, MoveSpeed);

        Vector3 newPos = currentPos + moveDir * _currentSpeed * dt;
        _physics.BodyInterface.SetPosition(_enemy.Body.Native, newPos, JoltPhysicsSharp.Activation.Activate);

        var animator = _enemy.Entity?.Animator;
        if (animator != null)
            animator.Speed = (_currentSpeed / MoveSpeed) * WalkAnimSpeed;

        // Rotação
        _targetYRotation = MathF.Atan2(moveDir.X, moveDir.Z);
        _currentYRotation = LerpAngle(_currentYRotation, _targetYRotation, RotationSpeed * dt);
        Quaternion rot = Quaternion.CreateFromAxisAngle(Vector3.UnitY, _currentYRotation);
        _physics.BodyInterface.SetRotation(_enemy.Body.Native, rot, JoltPhysicsSharp.Activation.Activate);
    }

    private static float LerpAngle(float from, float to, float t)
    {
        float diff = to - from;
        while (diff > MathF.PI) diff -= MathF.PI * 2f;
        while (diff < -MathF.PI) diff += MathF.PI * 2f;
        return from + diff * MathF.Min(t, 1f);
    }

    private Vector3 PickRandomPoint()
    {
        Vector3 pos = _enemy.Entity.Transform.Position;
        Vector3 rayOrigin = pos + Vector3.UnitY * 1.0f;

        for (int i = 0; i < 5; i++)
        {
            float angle = (float)(s_random.NextDouble() * System.Math.PI * 2.0);
            float radius = (float)s_random.NextDouble() * PatrolRadius;
            Vector3 target = new Vector3(
                pos.X + MathF.Cos(angle) * radius,
                pos.Y,
                pos.Z + MathF.Sin(angle) * radius
            );

            Vector3 dir = target - pos;
            float dist = dir.Length();
            if (dist < 0.01f) continue;

            if (!RaycastSelf(rayOrigin, dir / dist, dist))
                return target;
        }

        return pos;
    }

    private float RandomWaitTime()
    {
        return MinWaitTime + (float)s_random.NextDouble() * (MaxWaitTime + MinWaitTime);
    }

    private bool RaycastSelf(Vector3 origin, Vector3 direction, float maxDistance)
    {
        using var bodyFilter = new Physics.EnemyBodyFilter(_enemy.Body.Native);
        using var bpFilter = new Physics.DefaultBroadPhaseLayerFilter();
        using var olFilter = new Physics.DefaultObjectLayerFilter();

        Vector3 dirNorm = Vector3.Normalize(direction);
        Vector3 dirScaled = dirNorm * maxDistance;
        var ray = new Ray(ref origin, ref dirScaled);

        return _physics.NarrowPhaseQuery.CastRay(ray, out _, bpFilter, olFilter, bodyFilter);
    }

    private void PlayIdleAnimation()
    {
        var animator = _enemy.Entity?.Animator;
        if (animator == null) return;

        if (animator.GetClip("Idle") != null)
            animator.CrossFade("Idle", 1f);
        else if (!string.IsNullOrEmpty(animator.Model?.DefaultClipName))
            animator.CrossFade(animator.Model.DefaultClipName, 1f);
    }

    private void PlayWalkAnimation()
    {
        var animator = _enemy.Entity?.Animator;
        if (animator == null) return;

        if (animator.GetClip("Walk") != null)
            animator.CrossFade("Walk", 1f);
    }

    public void OnDrawGizmos(Debug.DebugDrawer drawer)
    {
        if (_enemy.IsDead || !_enemy.Entity?.Body?.IsBuilt == true) return;

        Vector3 pos = _enemy.Body.Position(_physics);

        drawer.DrawBox(_targetPos, Quaternion.Identity, new Vector3(0.15f), new Vector3(1, 0, 0));
        drawer.PushLine(pos + Vector3.UnitY * 0.5f, _targetPos + Vector3.UnitY * 0.5f, _state == PatrolState.Walking ? new Vector3(0, 1, 0) : new Vector3(1, 1, 0));
        drawer.DrawCircle(pos + Vector3.UnitY * 0.05f, PatrolRadius, new Vector3(1, 1, 0));
    }

}