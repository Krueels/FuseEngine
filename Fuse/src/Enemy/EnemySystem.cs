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
    private Player.Player? _player;
    private float _contactDamageCooldown;

    public EnemySystem(PhysicsWorld physics, Scene.SceneManager sceneManager, AssetManager assets)
    {
        _physics = physics;
        _sceneManager = sceneManager;
        _assets = assets;
    }

    public void SetPlayer(Player.Player player) => _player = player;

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

    public SpiderEnemy SpawnSpider(Vector3 position, float health = 80f, Vector3? surfaceNormal = null)
    {
        string id = $"spider_{_spiders.Count}";
        var spider = new SpiderEnemy(id, health, _assets);
        spider.Initialize(_physics, _sceneManager, position, surfaceNormal);
        _spiders.Add(spider);
        return spider;
    }

    public void Update(float dt)
    {
        if (SpiderPatrol.PursuitEnabled && _player != null && !_player.IsDead)
            SpiderPatrol.SetPursuitTarget(_player.Position);

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

    public void UpdateContactDamage(float dt)
    {
        if (_player == null || _player.IsDead) return;

        _contactDamageCooldown -= dt;
        if (_contactDamageCooldown > 0f) return;

        Vector3 playerPos = _player.Position;

        foreach (var e in _enemies)
        {
            if (e.IsDead || e.Character == null) continue;
            Vector3 enemyPos = e.Character.Position;
            float dist = Vector3.Distance(playerPos, enemyPos);
            if (dist < 1.8f)
            {
                Vector3 dir = Vector3.Normalize(playerPos - enemyPos);
                _player.TakeDamage(15f, dir);
                _contactDamageCooldown = 0.5f;
                return;
            }
        }

        foreach (var s in _spiders)
        {
            if (s.IsDead || !s.Body.IsBuilt) continue;
            Vector3 spiderPos = s.Entity.Transform.Position;
            float dist = Vector3.Distance(playerPos, spiderPos);
            if (dist < 1.8f)
            {
                Vector3 dir = Vector3.Normalize(playerPos - spiderPos);
                _player.TakeDamage(15f, dir);
                _contactDamageCooldown = 0.5f;
                return;
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

            e.Character?.Dispose();

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
