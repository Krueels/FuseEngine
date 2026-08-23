using System;
using System.Numerics;
using Fuse.Physics;
using Fuse.Renderer;
using Fuse.Scene;

namespace Fuse.Enemy;

public interface IEnemy : IDisposable
{
    string Id { get; }
    Entity Entity { get; }
    RigidBody Body { get; }
    float Health { get; set; }
    float MaxHealth { get; }
    bool IsDead { get; }

    void Initialize(PhysicsWorld physics, SceneManager sceneManager, Vector3 spawnPos);
    void TakeDamage(float damage, Vector3 hitPos, Vector3 hitDirection, PhysicsWorld physics);
    void Update(float dt, PhysicsWorld physics);
    void OnDeath(PhysicsWorld physics, SceneManager? sceneManager = null);
}