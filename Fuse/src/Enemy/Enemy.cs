using System.Numerics;
using Fuse.Animation;
using Fuse.AssetManagement;
using Fuse.Core;
using Fuse.Physics;
using Fuse.Renderer;
using Fuse.Scene;
using Fuse.Scene.Model;
using JoltPhysicsSharp;
using Silk.NET.OpenGL;

namespace Fuse.Enemy
{
    public sealed class Enemy : IEnemy
    {
        public string Id { get; }
        public Entity Entity { get; private set; } = null!;
        public RigidBody Body { get; private set; } = new();
        public float Health { get; set; }
        public float MaxHealth { get; }
        public bool IsDead => Health <= 0f;

        private readonly AssetManager _assets;
        private Scene.SceneManager? _sceneManager;
        private readonly float _capsuleRadius = 0.4f;
        private readonly float _capsuleHeight = 1.8f;
        private bool _initialized;
        private bool _hasDied;
        private Animation.Animator? _animator;

        public Enemy(string id, float maxHealth = 100f, AssetManager? assets = null)
        {
            Id = id;
            MaxHealth = maxHealth;
            Health = maxHealth;
            _assets = assets!;
        }

        public void Initialize(PhysicsWorld physics, Scene.SceneManager sceneManager, Vector3 spawnPos)
        {
            if (_initialized) return;

            _sceneManager = sceneManager;

            Body.SetCapsule(_capsuleRadius, _capsuleHeight)
                .SetPosition(spawnPos)
                .SetMass(70f)
                .SetFriction(0.5f)
                .SetRestitution(0.1f)
                .SetAllowedDOFs(AllowedDOFs.TranslationX | AllowedDOFs.TranslationY | AllowedDOFs.TranslationZ)
                .Build(physics);

            var model = _assets.GetSkinnedModel($"{Fuse.ResPath.Path}/skinned_models/TrapKing.fbx");
            if (model != null)
            {
                _animator = new Animation.Animator(model.Skeleton);
                model.Link(_animator);

                Entity = sceneManager.ActiveScene.Add(null, Id, Body);
                Entity.SkinnedModel = model;
                Entity.Animator = _animator;
                Entity.Visible = true;

                // Play idle se existir
                //if (!string.IsNullOrEmpty(model.DefaultClipName))
                //    _animator.Play(model.DefaultClipName);
            }
            else
            {
                var meshData = MeshGenerator.GenerateCapsule(_capsuleRadius, _capsuleHeight, 12);
                var capsuleMesh = new Mesh(_assets.Gl, meshData.Vertices, meshData.Indices);
                Entity = sceneManager.ActiveScene.Add(capsuleMesh, Id, Body);
                Entity.Visible = true;
                Entity.Texture = null;
            }

            sceneManager.ActiveScene.RegisterBody(Entity);
            _initialized = true;
        }

        public void TakeDamage(float damage, Vector3 hitPos, Vector3 hitDirection, PhysicsWorld physics)
        {
            if (IsDead) return;

            Health -= damage;
            Logger.Info($"[Enemy] {Id} took {damage} damage. Health: {Health}/{MaxHealth}");

            if (Body.IsBuilt)
            {
                Vector3 impulse = hitDirection * damage * 0.3f;
                physics.BodyInterface.AddImpulse(Body.Native, impulse);
            }
        }

        public void Update(float dt, PhysicsWorld physics)
        {
            if (IsDead || !_initialized) return;
        }

        public void OnDeath(PhysicsWorld physics, Scene.SceneManager? sceneManager = null)
        {
            if (_hasDied) return;
            _hasDied = true;

            Logger.Info($"[Enemy] {Id} died!");
            Entity.Visible = false;

            if (Body.IsBuilt)
            {
                physics.DestroyBody(Body.Native);
                Body.Destroy();
            }

            sceneManager?.ActiveScene.Remove(Entity);
        }

        public void Dispose()
        {
            Entity?.Mesh?.Dispose();
        }
    }
}