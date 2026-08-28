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
    public sealed class Enemy : IEnemy, Debug.IGizmoDrawable
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
        private CharacterVirtual? _character;
        private ObjectLayer _objectLayer;
        private PhysicsWorld? _physics;

        public EnemyPatrol? Patrol => _patrol;
        public CharacterVirtual? Character => _character;

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
            _physics = physics;

            // Raycast down from spawn to find the actual floor
            Vector3 safeSpawn = spawnPos;
            if (sceneManager.Raycast(spawnPos + Vector3.UnitY * 2f, -Vector3.UnitY, 10f, out var spawnHit))
            {
                safeSpawn = spawnHit.Position + Vector3.UnitY * (_capsuleHeight * 0.5f + _capsuleRadius + 0.05f);
            }

            // RigidBody kinematic — apenas para registro na cena e detecção de hits pelas armas
            Body.SetCapsule(_capsuleRadius, _capsuleHeight)
                .SetPosition(safeSpawn)
                .SetKinematic(true)
                .SetFriction(0.5f)
                .SetRestitution(0.1f)
                .SetAllowedDOFs(AllowedDOFs.All)
                .Build(physics);

            // CharacterVirtual — movimentação real com gravidade, rampas, escadas
            _objectLayer = physics.ObjectLayer;
            var charSettings = new CharacterVirtualSettings
            {
                Mass = 70f,
                Shape = new CapsuleShape(_capsuleHeight * 0.5f, _capsuleRadius),
                MaxSlopeAngle = float.DegreesToRadians(45.0f),
                CharacterPadding = 0.02f,
                PenetrationRecoverySpeed = 0.9f,
                PredictiveContactDistance = 0.05f,
                MaxCollisionIterations = 10,
                MaxConstraintIterations = 30,
                MinTimeRemaining = 1.0e-4f,
                CollisionTolerance = 1.0e-3f,
                MaxNumHits = 256,
                HitReductionCosMaxAngle = 0.999f,
            };

            Vector3 posVec = safeSpawn;
            Quaternion identity = Quaternion.Identity;
            _character = new CharacterVirtual(charSettings, ref posVec, ref identity, 0, physics.Native);
            

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
            Debug.DebugDrawer.Register(this);
            _initialized = true;
        }

        public void TakeDamage(float damage, Vector3 hitPos, Vector3 hitDirection, PhysicsWorld physics)
        {
            if (IsDead) return;

            Health -= damage;
            Logger.Info($"[Enemy] {Id} took {damage} damage. Health: {Health}/{MaxHealth}");
        }

        public void Update(float dt, PhysicsWorld physics)
        {
            if (IsDead || !_initialized) return;

            _patrol?.Update(dt);

            if (_character != null)
            {
                bool onGround = _character.GroundState == GroundState.OnGround;

                Vector3 vel = _character.LinearVelocity;
                if (onGround)
                {
                    vel.Y = 0f;
                }
                else
                {
                    vel += physics.Gravity * dt;

                    // Safety: clamp fall speed and prevent falling through the floor
                    if (vel.Y < -30f) vel.Y = -30f;
                }
                _character.LinearVelocity = vel;

                var updSettings = new ExtendedUpdateSettings
                {
                    StickToFloorStepDown = new Vector3(0, -0.5f, 0),
                    WalkStairsStepUp = new Vector3(0, 0.5f, 0),
                    WalkStairsMinStepForward = 0.02f,
                    WalkStairsStepForwardTest = 0.15f,
                    WalkStairsCosAngleForwardContact = float.Cos(float.DegreesToRadians(75.0f)),
                    WalkStairsStepDownExtra = Vector3.Zero,
                };

                using var bodyFilter = new EnemyBodyFilter(Body.Native);
                using var shapeFilter = new DefaultShapeFilter();
                _character.ExtendedUpdate(dt, updSettings, ref _objectLayer, physics.Native, bodyFilter, shapeFilter);

                // Safety: if character fell way below the map, teleport back up
                Vector3 charPos = _character.Position;
                if (charPos.Y < -50f)
                {
                    charPos.Y = 5f;
                    _character.Position = charPos;
                    _character.LinearVelocity = Vector3.Zero;
                }

                if (Body.IsBuilt)
                {
                    Quaternion charRot = _character.Rotation;
                    physics.BodyInterface.SetPositionAndRotation(Body.Native, charPos, charRot, Activation.Activate);
                }
            }

            _animator?.Update(dt);
        }

        public void OnDeath(PhysicsWorld physics, Scene.SceneManager? sceneManager = null)
        {
            if (_hasDied) return;
            _hasDied = true;

            Logger.Info($"[Enemy] {Id} died!");
            Entity.Visible = false;

            _character?.Dispose();
            _character = null;

            if (Body.IsBuilt)
            {
                physics.DestroyBody(Body.Native);
                Body.Destroy();
            }

            sceneManager?.ActiveScene.Remove(Entity);
        }

        public void OnDrawGizmos(Debug.DebugDrawer drawer)
        {
            if (IsDead || !Body.IsBuilt) return;

            Vector3 pos = Body.Position(_physics);
            Quaternion rot = Body.Rotation(_physics);
            drawer.DrawCapsule(pos, rot, _capsuleHeight * 0.5f, _capsuleRadius, new Vector3(1, 0, 0));
        }

        public void Dispose()
        {
            _character?.Dispose();
            _character = null;

            if (Entity != null && Entity.MeshOwnedByEntity && Entity.Mesh != null)
            {
                Entity.Mesh.Dispose();
                Entity.Mesh = null;
            }
        }
    }
}
