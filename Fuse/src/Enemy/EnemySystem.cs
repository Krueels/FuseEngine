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
    private readonly List<SpiderEnemy> _spiders = new();

    public EnemySystem(PhysicsWorld physics, Scene.SceneManager sceneManager, AssetManager assets)
    {
        _physics = physics;
        _sceneManager = sceneManager;
        _assets = assets;
    }

    public IReadOnlyList<Enemy> GetEnemies() => _enemies;
    public Enemy SpawnEnemy(Vector3 position, float health = 100f)
    {
        string id = $"enemy_{_enemies.Count}";
        var enemy = new Enemy(id, health, _assets);
        enemy.Initialize(_physics, _sceneManager, position);
        _enemies.Add(enemy);
        return enemy;
    }

    public IReadOnlyList<SpiderEnemy> GetSpiders() => _spiders;

    public SpiderEnemy SpawnSpider(Vector3 position, float health = 80f)
    {
        string id = $"spider_{_spiders.Count}";
        var spider = new SpiderEnemy(id, health, _assets);
        spider.Initialize(_physics, _sceneManager, position);
        _spiders.Add(spider);
        return spider;
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

        for (int i = _spiders.Count - 1; i >= 0; i--)
        {
            var s = _spiders[i];
            if (s.IsDead)
            {
                s.OnDeath(_physics, _sceneManager);
                s.Dispose();
                _spiders.RemoveAt(i);
            }
            else
            {
                s.Update(dt, _physics);
            }
        }
    }

    public bool TryGetEnemy(BodyID bodyId, out IEnemy? enemy)
    {
        var entity = _sceneManager.ActiveScene.GetEntityByBody(bodyId);
        if (entity != null)
        {
            var e = _enemies.Find(e => e.Entity.Id == entity.Id);
            if (e != null) { enemy = e; return true; }
            var s = _spiders.Find(s => s.Entity.Id == entity.Id);
            if (s != null) { enemy = s; return true; }
        }
        enemy = null;
        return false;
    }


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

        for (int i = 0; i < _spiders.Count; i++)
        {
            var spider = _spiders[i];
            if (!spider.IsDead)
            {
                var pos = spider.Entity.Transform.Position;
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

        for (int i = _spiders.Count - 1; i >= 0; i--)
        {
            var s = _spiders[i];

            if (s.Body.IsBuilt)
            {
                _physics.DestroyBody(s.Body.Native);
                s.Body.Destroy();
            }

            _sceneManager.ActiveScene.Remove(s.Entity);
            s.Dispose();
        }

        _spiders.Clear();
    }

    public void Dispose()
    {
        Clear();
    }
}
