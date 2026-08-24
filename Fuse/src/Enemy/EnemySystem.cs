using System.Collections.Generic;
using System.Numerics;
using Fuse.Physics;
using Fuse.Scene;
using Fuse.AssetManagement;
using JoltPhysicsSharp;

namespace Fuse.Enemy;

public sealed class EnemySystem : IDisposable
{
    private readonly PhysicsWorld _physics;
    private readonly Scene.SceneManager _sceneManager;
    private readonly AssetManager _assets;
    private readonly List<Enemy> _enemies = new();

    public EnemySystem(PhysicsWorld physics, Scene.SceneManager sceneManager, AssetManager assets)
    {
        _physics = physics;
        _sceneManager = sceneManager;
        _assets = assets;
    }

    public Enemy SpawnEnemy(Vector3 position, float health = 100f)
    {
        string id = $"enemy_{_enemies.Count}";
        var enemy = new Enemy(id, health, _assets);
        enemy.Initialize(_physics, _sceneManager, position);
        _enemies.Add(enemy);
        return enemy;
    }

    public void Update(float dt)
    {
        for (int i = _enemies.Count - 1; i >= 0; i--)
        {
            var e = _enemies[i];
            if (e.IsDead)
            {
                e.OnDeath(_physics, _sceneManager);
                e.Dispose();
                _enemies.RemoveAt(i);
            }
            else
            {
                e.Update(dt, _physics);
            }
        }
    }

    public bool TryGetEnemy(BodyID bodyId, out Enemy? enemy)
    {
        var entity = _sceneManager.ActiveScene.GetEntityByBody(bodyId);
        if (entity != null)
        {
            enemy = _enemies.Find(e => e.Entity.Id == entity.Id);
            return enemy != null;
        }
        enemy = null;
        return false;
    }

    public IReadOnlyList<Enemy> GetEnemies() => _enemies;

    public void Clear()
    {
        for (int i = _enemies.Count - 1; i >= 0; i--)
        {
            var e = _enemies[i];

            if (e.Body.IsBuilt)
            {
                _physics.DestroyBody(e.Body.Native);
                e.Body.Destroy();
            }

            _sceneManager.ActiveScene.Remove(e.Entity);
            e.Dispose();
        }

        _enemies.Clear();
    }

    public void Dispose()
    {
        Clear();
    }
}
