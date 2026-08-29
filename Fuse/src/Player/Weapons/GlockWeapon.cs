using System.Numerics;
using Fuse.Renderer;
using Fuse.Animation;
using Fuse.Physics;
using Fuse.AssetManagement;
using Fuse.Core;
using Fuse.Audio;
using Fuse.Math;
using JoltPhysicsSharp;
using Silk.NET.OpenGL;

namespace Fuse.Player.Weapons;

public sealed class GlockWeapon : IWeapon
{
    public string Id => "glock";
    public string ViewmodelModelPath => Bible.Model(Bible.GlockModel);

    private static readonly string[] _glockSubmeshes = ["GlockBarrel", "GlockMagazine", "GlockMagazine_02", "GlockReceiver", "GlockSlide", "GlockSlideUnLock", "GlockTrigger"];
    public Dictionary<string, string> ViewmodelTextures => _glockSubmeshes.ToDictionary(s => s, _ => Bible.Tex(Bible.GlockAlbedo));

    // Muzzle flash
    public Vector3 MuzzleFlashOffset => new(0.1f, -0.08f, 0.58f);
    public Vector2 MuzzleFlashSize => new(0.5f, 0.5f);
    public float MuzzleFlashDuration => 0.02f;
    public string MuzzleFlashTexturePath => Bible.Tex(string.Format(Bible.MuzzleFlash, Random.Shared.Next(3)));

    public string ViewmodelIdleAnim => "Idle";
    public string ViewmodelFireAnim => "Fire1";

    public readonly string[] _fireAnims = Enumerable.Range(1, 3).Select(i => $"Fire{i}").ToArray();
    public string ViewmodelReloadAnim => "Reload";
    public string ViewmodelReloadEmptyAnim => "ReloadEmpty";
    public string ViewmodelDrawAnim => "DrawFirst";

    public string ReloadAudioPath => Bible.Audio(Bible.GlockReload);
    public string ReloadEmptyAudioPath => Bible.Audio(Bible.GlockReloadEmpty);

    public float FireRate => 12.0f;      // 720 RPM
    public float Damage => 25f;
    public float Range => 100f;
    public int MagazineSize => 17;
    public int CurrentAmmo { get; set; }
    public int ReserveAmmo { get; set; } = 120;
    public bool IsAutomatic => false;    // Semi-auto
    public float ReloadTime => 1.67f;

    // Recoil
    public float RecoilPitchKick => 5.8f;
    public float RecoilYawKick => 1.8f;

    private WeaponSystem? _system;
    private float _nextFireTime;
    private bool _isReloading;
    private float _reloadTimer;
    private Animator? _animator;

    // Estado de animação
    private enum AnimState { None, Drawing, Idle, Firing, Reloading }
    private AnimState _animState = AnimState.None;
    private float _animEndTime = 0f;
    private readonly Random _fireRng = new();
    private bool _pendingAutoReload;
    private bool _wasMoving;
    private Vector3 _lastMoveDir;

    private readonly string[] _fireSounds = Enumerable.Range(0, 4).Select(i => $"{Bible.GlockFire}{i}.wav").ToArray();

    // Walk anim speed por estado de movimento
    private const float WalkAnimSpeedCrouch = 0.5f;
    private const float WalkAnimSpeedWalk = .8f;
    private const float WalkAnimSpeedSprint = 1.0f;
    private const float WalkSpeedCrouch = 2.0f;
    private const float WalkSpeedWalk = 4.0f;
    private const float WalkSpeedSprint = 6.0f;

    public string CurrentAnimState => _animState.ToString();
    public float CurrentAnimTime => (float)(_animator?.TimeSeconds ?? 0);
    public float CurrentAnimDuration => (float)(_animator?.CurrentClip?.DurationSeconds ?? 0);

    public void OnEquip(WeaponSystem system, Entity viewmodelEntity, Animator animator)
    {
        _system = system;
        _animator = animator;
        CurrentAmmo = MagazineSize;
        _isReloading = false;
        _reloadTimer = 0f;
        _nextFireTime = 0f;
        _wasMoving = false;
        _lastMoveDir = Vector3.UnitX;

        if (!string.IsNullOrEmpty(ViewmodelDrawAnim))
        {
            _animator.Speed = 1.0f;
            _animator.Play(ViewmodelDrawAnim);
            _system?.Audio.Play(Bible.Audio(Bible.GlockDrawFirst));
            _animState = AnimState.Drawing;
            _animEndTime = (float)(_animator.CurrentClip?.DurationSeconds ?? 0.5f);
        }
        else
        {
            _animator.Speed = 1.0f;
            _animator.Play(ViewmodelIdleAnim);
            _animState = AnimState.Idle;
        }
    }

    public void OnUnequip()
    {
        _system = null;
        _animator = null;
    }

    private float GetHorizontalSpeed()
    {
        var vel = _system?.PlayerVelocity ?? Vector3.Zero;
        return MathF.Sqrt(vel.X * vel.X + vel.Z * vel.Z);
    }

    private float GetWalkAnimSpeed(float speed)
    {
        if (speed <= WalkSpeedCrouch)
            return WalkAnimSpeedCrouch;
        if (speed <= WalkSpeedWalk)
        {
            float t = (speed - WalkSpeedCrouch) / (WalkSpeedWalk - WalkSpeedCrouch);
            return WalkAnimSpeedCrouch + (WalkAnimSpeedWalk - WalkAnimSpeedCrouch) * t;
        }
        float t2 = (speed - WalkSpeedWalk) / (WalkSpeedSprint - WalkSpeedWalk);
        return MathF.Min(WalkAnimSpeedWalk + (WalkAnimSpeedSprint - WalkAnimSpeedWalk) * t2, WalkAnimSpeedSprint);
    }

    private void TransitionToIdle(bool crossfade = true)
    {
        _animState = AnimState.Idle;
        float speed = GetHorizontalSpeed();
        _wasMoving = speed > 0.5f;
        if (_wasMoving)
        {
            var vel = _system?.PlayerVelocity ?? Vector3.Zero;
            _lastMoveDir = Vector3.Normalize(new Vector3(vel.X, 0, vel.Z));
        }
        if (_animator != null)
        {
            if (crossfade)
                _animator.CrossFade(_wasMoving ? "Walk" : "Idle", 0.5f);
            else
                _animator.Play(_wasMoving ? "Walk" : "Idle");
            _animator.Speed = _wasMoving ? GetWalkAnimSpeed(speed) : 1.0f;
        }
    }

    public bool CanFire()
    {
        if (_isReloading) return false;
        if (CurrentAmmo <= 0) return false;
        if (_system == null) return false;

        float currentTime = (float)Engine.Time;
        return currentTime >= _nextFireTime;
    }

    public void Fire(Vector3 origin, Vector3 direction)
    {
        if (!CanFire()) return;

        float currentTime = (float)Engine.Time;
        _nextFireTime = currentTime + (1.0f / FireRate);
        CurrentAmmo--;

        // Trigger fire animation
        if (_animator != null)
        {
            _animator.CancelTransition();
            string anim = _fireAnims[_fireRng.Next(_fireAnims.Length)];
            _animator.Speed = 1.0f;
            _animator.Play(anim);
            _animState = AnimState.Firing;
            _animEndTime = (float)(_animator.CurrentClip?.DurationSeconds ?? 0.2f);
        }

        // NOVO: Aplicar recoil na câmera via WeaponSystem
        if (_system != null)
        {
            var cam = _system.Player.Camera;
            float pitchKick = RecoilPitchKick;
            float yawKick = (float)(_fireRng.NextDouble() - 0.5) * 2 * RecoilYawKick;
            cam.AddRecoil(yawKick, pitchKick);
            cam.AddShakeTilt(1.8f);
        }

        // Play fire sound
        _system?.Audio?.Play(_fireSounds[_fireRng.Next(_fireSounds.Length)], volume: 1.0f);

        // Muzzle flash light (temporary)
        SpawnMuzzleFlash(origin + direction * 0.5f, direction);

        // Hitscan raycast
        PerformHitscan(origin, direction);

        // Auto-reload if empty
        _pendingAutoReload = CurrentAmmo <= 0 && ReserveAmmo > 0;
    }

    public void Reload()
    {
        if (_isReloading) return;
        if (CurrentAmmo >= MagazineSize) return;
        if (ReserveAmmo <= 0) return;

        _isReloading = true;
        _reloadTimer = ReloadTime;

        if (_animator != null)
        {
            _animator.CancelTransition();
            string reloadAnim = CurrentAmmo == 0 && !string.IsNullOrEmpty(ViewmodelReloadEmptyAnim)
                ? ViewmodelReloadEmptyAnim : ViewmodelReloadAnim;
            if (!string.IsNullOrEmpty(reloadAnim))
            {
                _animator.Speed = 1.0f;
                _animator.Play(reloadAnim);
            }
        }

        _animState = AnimState.Reloading;

        string soundFile = CurrentAmmo == 0 ? ReloadEmptyAudioPath : ReloadAudioPath;
        _system?.Audio?.Play(soundFile, volume: 1.0f);
    }

    public void Update(float dt)
    {
        if (_animator == null || _animator.CurrentClip == null) return;

        float clipDuration = (float)_animator.CurrentClip.DurationSeconds;
        float currentTime = (float)_animator.TimeSeconds;

        switch (_animState)
        {
            case AnimState.Drawing:
                if (currentTime >= clipDuration * 0.95f)
                    TransitionToIdle();
                break;

            case AnimState.Firing:
                if (currentTime >= clipDuration * 0.95f)
                {
                    if (_pendingAutoReload)
                    {
                        _pendingAutoReload = false;
                        Reload();
                    }
                    else
                    {
                        TransitionToIdle(false);
                    }
                }
                break;

            case AnimState.Reloading:
                _reloadTimer -= dt;
                if (_reloadTimer <= 0f)
                {
                    _isReloading = false;
                    int needed = MagazineSize - CurrentAmmo;
                    int taken = System.Math.Min(needed, ReserveAmmo);
                    CurrentAmmo += taken;
                    ReserveAmmo -= taken;
                    TransitionToIdle();
                }
                break;

            case AnimState.Idle:
                var vel = _system?.PlayerVelocity ?? Vector3.Zero;
                float speed = GetHorizontalSpeed();
                bool isMoving = speed > 0.5f;

                if (isMoving != _wasMoving)
                {
                    if (_animator != null)
                    {
                        _animator.CrossFade(isMoving ? "Walk" : "Idle", 0.5f);
                        if (isMoving)
                            _animator.Speed = GetWalkAnimSpeed(speed);
                        else
                            _animator.Speed = 1.0f;
                    }
                    _wasMoving = isMoving;
                }
                else if (isMoving)
                {
                    // Velocidade da animação = velocidade do player em tempo real, direto
                    _animator.Speed = GetWalkAnimSpeed(speed);
                    _lastMoveDir = Vector3.Normalize(new Vector3(vel.X, 0, vel.Z));
                }

                break;
        }
    }

    public void UpdateViewmodel(float dt, Animator animator)
    {
        // Animações são controladas via Play() nos métodos acima
        // Aqui poderia adicionar bobbing, sway, etc no futuro
    }

    public void Dispose() { }

    private void PerformHitscan(Vector3 origin, Vector3 direction)
    {
        if (_system?.SceneManager == null) return;

        if (!_system.SceneManager.Raycast(
                origin,
                direction,
                Range,
                out var hit,
                excludedBodies: _system.EnemySystem?
                    .GetSpiderMovementBodiesForDamageRaycast()))
            return;

        // Apply damage to interactable/physics body
        ApplyDamage(hit.BodyID, hit.Position, direction);

        // Hit effect
        bool isEnemy = _system?.EnemySystem?.TryGetEnemy(hit.BodyID, out _) == true;
        if (!isEnemy)
        {
            SpawnHitEffect(hit.Position, hit.Normal, hit.RigidBody);
        }
    }

    private void SpawnMuzzleFlash(Vector3 position, Vector3 direction)
    {
        _system?.ShowMuzzleFlash(MuzzleFlashOffset, MuzzleFlashSize, MuzzleFlashDuration);
        //Logger.Info($"[Glock] Muzzle flash at {position}");
    }

    private void SpawnHitEffect(Vector3 position, Vector3 normal, Physics.RigidBody? parentBody = null)
    {
        // TODO: Decal, particle, som de impacto
        var idx = Random.Shared.Next(3);
        _system?.Audio.Play3D($"{Bible.BulletImpact}{idx:D2}.mp3", position, volume: 0.5f);
        _system?.SpawnImpactDecal(position, normal, parentBody: parentBody);
    }


    private void ApplyDamage(JoltPhysicsSharp.BodyID bodyId, Vector3 hitPos, Vector3 direction)
    {
        // Apply damage at enemy
        if (_system?.EnemySystem?.TryGetEnemy(bodyId, out var enemy) == true)
        {
            enemy.TakeDamage(Damage, hitPos, direction, _system.Physics);
            return; // não aplicar impulso em inimigos
        }
        
        // Try to get interactable
        if (_system.Physics.BodyInterface.IsAdded(bodyId))
        {
            var interactable = Interaction.InteractionSystem.GetInteractable(
                _system.Physics.BodyInterface, bodyId,
                _system.Physics.BodyInterface.GetUserData(bodyId)
                );

            if (interactable != null)
            {
                Logger.Info($"[Glock] Hit interactable: {interactable.GetType().Name}");
            }
        }

        // Apply impulse - BodyInterface acorda o corpo automaticamente
        float mass = 1.0f;
        BodyLockRead readLock = default;
        _system.Physics.BodyLockInterface.LockRead(bodyId, out readLock);
        if (readLock.Succeeded && readLock.Body.IsDynamic)
            mass = 1.0f / readLock.Body.MotionProperties.InverseMassUnchecked;
        if (readLock.Succeeded)
            _system.Physics.BodyLockInterface.UnlockRead(readLock);

        Vector3 impulse = direction * Damage * 0.5f * mass;
        _system.Physics.BodyInterface.AddImpulse(bodyId, impulse);

        // spawn impact decal
        //_system?.SpawnImpactDecal(hitPos, direction);
    }
}
