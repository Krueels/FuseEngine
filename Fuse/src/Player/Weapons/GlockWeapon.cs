using System.Numerics;
using Fuse.Renderer;
using Fuse.Animation;
using Fuse.Physics;
using Fuse.AssetManagement;
using Fuse.Core;
using Fuse.Audio;
using JoltPhysicsSharp;
using Silk.NET.OpenGL;

namespace Fuse.Player.Weapons;

public sealed class GlockWeapon : IWeapon
{
    public string Id => "glock";
    public string ViewmodelModelPath => $"{Fuse.ResPath.Path}/skinned_models/Glock.fbx";

    public Dictionary<string, string> ViewmodelTextures => new()
    {
        {"GlockBarrel", $"{Fuse.ResPath.Path}/Textures/weapons/glock/Glock_ALB.png" },
        {"GlockMagazine", $"{Fuse.ResPath.Path}/Textures/weapons/glock/Glock_ALB.png" },
        {"GlockMagazine_02", $"{Fuse.ResPath.Path}/Textures/weapons/glock/Glock_ALB.png" },
        {"GlockReceiver", $"{Fuse.ResPath.Path}/Textures/weapons/glock/Glock_ALB.png" },
        {"GlockSlide", $"{Fuse.ResPath.Path}/Textures/weapons/glock/Glock_ALB.png" },
        {"GlockSlideUnLock", $"{Fuse.ResPath.Path}/Textures/weapons/glock/Glock_ALB.png" },
        {"GlockTrigger", $"{Fuse.ResPath.Path}/Textures/weapons/glock/Glock_ALB.png" },
    };

    public string ViewmodelIdleAnim => "Idle";
    public string ViewmodelFireAnim => "Fire1";

    public readonly string[] _fireAnims = ["Fire1", "Fire2", "Fire3"];
    public string ViewmodelReloadAnim => "Reload";
    public string ViewmodelReloadEmptyAnim => "ReloadEmpty";
    public string ViewmodelDrawAnim => "Draw";

    public string ReloadAudioPath => $"{Fuse.ResPath.Path}/Audio/weapons/glock/Glock_Reload.wav";
    public string ReloadEmptyAudioPath => $"{Fuse.ResPath.Path}/Audio/weapons/glock/Glock_ReloadEmpty.wav";

    public float FireRate => 12.0f;      // 720 RPM
    public float Damage => 25f;
    public float Range => 100f;
    public int MagazineSize => 17;
    public int CurrentAmmo { get; set; }
    public int ReserveAmmo { get; set; } = 120;
    public bool IsAutomatic => false;    // Semi-auto
    public float ReloadTime => 1.67f;

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

    private readonly string[] _fireSounds = ["Audio/weapons/glock/Glock_Fire0.wav", "Audio/weapons/glock/Glock_Fire1.wav", "Audio/weapons/glock/Glock_Fire2.wav", "Audio/weapons/glock/Glock_Fire3.wav"];
    private const float WalkAnimSpeedMax = 1.8f;
    private const float WalkAnimSpeedDivisor = 6.25f;

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

    private void TransitionToIdle()
    {
        _animState = AnimState.Idle;
        Vector3 vel = _system?.PlayerVelocity ?? Vector3.Zero;
        float speed = MathF.Sqrt(vel.X * vel.X + vel.Z * vel.Z);
        _wasMoving = speed > 0.5f;
        if (_wasMoving)
            _lastMoveDir = Vector3.Normalize(new Vector3(vel.X, 0, vel.Z));
        if (_animator != null)
        {
            _animator.CrossFade(_wasMoving ? "Walk" : "Idle", 0.2f);
            _animator.Speed = _wasMoving ? MathF.Max(0.8f, MathF.Min(speed / WalkAnimSpeedDivisor, WalkAnimSpeedMax)) : 1.0f;
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
            string anim = _fireAnims[_fireRng.Next(_fireAnims.Length)];
            _animator.Speed = 1.0f;
            _animator.Play(anim);
            _animState = AnimState.Firing;
            _animEndTime = (float)(_animator.CurrentClip?.DurationSeconds ?? 0.2f);
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
                        TransitionToIdle();
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
                Vector3 vel = _system?.PlayerVelocity ?? Vector3.Zero;
                float speed = MathF.Sqrt(vel.X * vel.X + vel.Z * vel.Z);
                bool isMoving = speed > 0.5f;

                if (isMoving != _wasMoving)
                {
                    if (_animator != null)
                    {
                        _animator.CrossFade(isMoving ? "Walk" : "Idle", 0.2f);
                        _animator.Speed = isMoving ? MathF.Max(0.8f, MathF.Min(speed / WalkAnimSpeedDivisor, WalkAnimSpeedMax)) : 1.0f;
                    }
                    _wasMoving = isMoving;
                }
                else if (isMoving)
                {
                    _animator.Speed = MathF.Max(0.8f, MathF.Min(speed / WalkAnimSpeedDivisor, WalkAnimSpeedMax));

                    Vector3 dir = Vector3.Normalize(new Vector3(vel.X, 0, vel.Z));
                    float dot = Vector3.Dot(_lastMoveDir, dir);
                    if (dot < 0.3f && _animator != null)
                        _animator.CrossFade("Walk", 0.2f);
                    _lastMoveDir = dir;
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
        if (_system == null) return;

        using var bpFilter = new Physics.DefaultBroadPhaseLayerFilter();
        using var olFilter = new Physics.DefaultObjectLayerFilter();
        using var bodyFilter = new Physics.DefaultBodyFilter();

        Vector3 dirScaled = direction * Range;
        var ray = new Ray(ref origin, ref dirScaled);

        if (!_system.Physics.NarrowPhaseQuery.CastRay(ray, out var hit, bpFilter, olFilter, bodyFilter))
            return;

        Vector3 hitPos = origin + direction * Range * hit.Fraction;
        // Note: ContactNormal não está disponível no RayCastResult do JoltPhysicsSharp
        // Para normal, precisaríamos de um shape cast ou query adicional
        Vector3 hitNormal = -direction; // Aproximação: oposto à direção do tiro

        // Hit effect
        SpawnHitEffect(hitPos, hitNormal);

        // Apply damage to interactable/physics body
        ApplyDamage(hit.BodyID, hitPos, direction);
    }

    private void SpawnMuzzleFlash(Vector3 position, Vector3 direction)
    {
        // TODO: Adicionar particle system ou light temporária
        // Por enquanto apenas log
        Logger.Info($"[Glock] Muzzle flash at {position}");
    }

    private void SpawnHitEffect(Vector3 position, Vector3 normal)
    {
        // TODO: Decal, particle, som de impacto
        Logger.Info($"[Glock] Hit at {position}, normal {normal}");
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
                Logger.Info($"[Glock] Hit interactable: {interactable.GetType().Name}");
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
    }
}