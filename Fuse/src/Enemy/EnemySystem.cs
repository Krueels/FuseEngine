using System.Collections.Generic;
using System.Numerics;
using Fuse.Physics;
using Fuse.Scene;
using Fuse.AssetManagement;
using Fuse.Renderer;
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

    public void DrawDebug(Renderer.MasterRenderer renderer, Camera camera, float aspect)
    {
        var enemyTex = _assets.GetTexture(Core.Bible.Tex(Core.Bible.EnemyIcon));
        var view = camera.GetViewMatrix();
        var proj = camera.GetProjectionMatrix(aspect);

        for (int i = 0; i < _enemies.Count; i++)
        {
            var enemy = _enemies[i];
            if (!enemy.IsDead)
            {
                var pos = enemy.Entity.Transform.Position;
                pos.Y += 2.0f; // Acima da cápsula
                renderer.QueueBillboard(view, proj, enemyTex.ID, pos, new Vector2(0.5f, 0.5f), new Vector4(1, 0, 0, 0.8f));
            }
        }
    }

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
