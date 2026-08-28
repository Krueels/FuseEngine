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
        private EnemyPatrol? _patrol;

        public EnemyPatrol? Patrol => _patrol;

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
            

            var model = _assets.GetSkinnedModel(Bible.Model(Bible.UniSexGuy));
            if (model != null)
            {
                SkinnedModelLoader.MergeAnimationsFromFile(model, Bible.Model(Bible.UniSexGuyIdle), "Idle");
                SkinnedModelLoader.MergeAnimationsFromFile(model, Bible.Model(Bible.UniSexGuyWalk), "Walk");

                model.HiddenSubmeshes.Add("Glock");
                model.HiddenSubmeshes.Add("Shotgun_Mesh");
                model.HiddenSubmeshes.Add("SM_Knife_01");

                var bodyTex = _assets.GetTexture(Bible.Tex(Bible.UniSexBody));
                var eyesTex = _assets.GetTexture(Bible.Tex(Bible.UniSexEyes));

                foreach (var sub in model.Submeshes)
                {
                    if (sub.Name == "CC_Base_Body" && bodyTex != null)
                        sub.Texture = bodyTex;
                    else if (sub.Name == "CC_Base_Eye" && eyesTex != null)
                        sub.Texture = eyesTex;
                }

                _animator = new Animation.Animator(model.Skeleton);
                _animator.Speed = 0.5f;
                model.Link(_animator);

                Entity = sceneManager.ActiveScene.Add(null, Id, Body);
                Entity.SkinnedModel = model;
                Entity.Animator = _animator;
                Entity.Visible = true;
                Entity.ModelOffset = new System.Numerics.Vector3(0f, -1.25f, 0f);
                Entity.ModelScale = new System.Numerics.Vector3(145.4f, 145.4f, 145.4f);

                // Play idle se existir
                if (_animator.GetClip("Idle") != null)
                {
                    var idleClip = _animator.GetClip("Idle");
                    idleClip.Loop = true;
                    var walkClip = _animator.GetClip("Walk");
                    walkClip.Loop = true;
                    _animator.Play("Idle");
                }
                else if (!string.IsNullOrEmpty(model.DefaultClipName))
                {
                    var idleClip = _animator.GetClip(model.DefaultClipName);
                    if (idleClip != null) idleClip.Loop = true;
                    _animator.Play(model.DefaultClipName);
                }
            }
            else
            {
                var meshData = MeshGenerator.GenerateCapsule(_capsuleRadius, _capsuleHeight, 12);
                var capsuleMesh = new Mesh(_assets.Gl, meshData.Vertices, meshData.Indices);
                Entity = sceneManager.ActiveScene.Add(capsuleMesh, Id, Body);
                Entity.MeshOwnedByEntity = true;
                Entity.Visible = true;
                Entity.Texture = null;
            }

            sceneManager.ActiveScene.RegisterBody(Entity);
            _patrol = new EnemyPatrol(this, physics);
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

            _patrol?.Update(dt);
            _animator?.Update(dt);
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
            if (Entity != null && Entity.MeshOwnedByEntity && Entity.Mesh != null)
            {
                Entity.Mesh.Dispose();
                Entity.Mesh = null;
            }
        }
    }
}
