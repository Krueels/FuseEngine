using System.Collections.Generic;
using System.Numerics;
using Fuse.Animation;
using Fuse.AssetManagement;
using Fuse.Audio;
using Fuse.Core;
using Fuse.Enemy;
using Fuse.Input;
using Fuse.Physics;
using Fuse.Renderer;
using Fuse.Scene;

namespace Fuse.Player;

public class WeaponSystem : IDisposable
{
    // Propriedades públicas para armas acessarem
    public PhysicsWorld Physics => _physics;
    public AudioSystem Audio => _audio;
    public AssetManager Assets => _assets;
    public Scene.SceneManager SceneManager => _sceneManager;
    public Vector3 PlayerVelocity => _player.LinearVelocity;
    public Enemy.EnemySystem? EnemySystem {  get; set; }

    private readonly global::Fuse.Player.Player _player;
    private readonly Camera _camera;
    private readonly PhysicsWorld _physics;
    private readonly AssetManager _assets;
    private readonly AudioSystem _audio;
    private readonly Scene.SceneManager _sceneManager;

    private readonly Dictionary<string, IWeapon> _weapons = new();
    private readonly Dictionary<string, (int CurrentAmmo, int ReserveAmmo)> _ammoState = new();
    private IWeapon? _currentWeapon;
    private string? _currentWeaponId;

    private Entity? _viewmodelEntity;
    private Animator? _viewmodelAnimator;
    private SkinnedModel? _viewmodelModel;

    // Viewmodel offset (ajustável via debug UI)
    public Vector3 ViewmodelOffset { get; set; } = new Vector3(0.0f, -0.83f, 0.0f);
    public Vector3 ViewmodelRotationDeg { get; set; } = new Vector3(0f, 90f, 0f);

    // Debug: freeze viewmodel position
    public bool FreezeViewmodel { get; set; } = false;
    private Vector3 _frozenPosition;
    private Quaternion _frozenRotation;

    public WeaponSystem(global::Fuse.Player.Player player, Camera camera, PhysicsWorld physics,
                        AssetManager assets, AudioSystem audio, Scene.SceneManager sceneManager)
    {
        _player = player;
        _camera = camera;
        _physics = physics;
        _assets = assets;
        _audio = audio;
        _sceneManager = sceneManager;
    }

    public void RegisterWeapon(IWeapon weapon)
    {
        _weapons[weapon.Id] = weapon;
        Logger.Info($"[WeaponSystem] Registered weapon: {weapon.Id}");
    }

    public bool Equip(string weaponId)
    {
        if (!_weapons.TryGetValue(weaponId, out var weapon))
        {
            Logger.Error($"[WeaponSystem] Weapon not found: {weaponId}");
            return false;
        }

        // Unequip current weapon first
        if (_currentWeapon != null)
        {
            _ammoState[_currentWeaponId!] = (_currentWeapon.CurrentAmmo, _currentWeapon.ReserveAmmo);
            _currentWeapon.OnUnequip();
            DestroyViewmodel();
        }

        _currentWeapon = weapon;
        _currentWeaponId = weaponId;

        // Create viewmodel
        CreateViewmodel(weapon);

        //Texuta padrão para ArmsMale
        if (_viewmodelModel != null)
        {
            var armsTex = _assets.GetTexture($"{Fuse.ResPath.Path}/Textures/ArmsMale_ALB.png");
            foreach (var sub in _viewmodelModel.Submeshes)
            {
                if (sub.Name == "ArmsMale" && armsTex != null)
                    sub.Texture = armsTex;
            }
        }

        // aplicar texturas por submesh
        if (_viewmodelModel != null && weapon.ViewmodelTextures != null)
        {
            foreach(var sub in _viewmodelModel.Submeshes)
            {
                if (weapon.ViewmodelTextures.TryGetValue(sub.Name, out string? texPath))
                {
                    var tex = _assets.GetTexture(texPath);
                    if (tex != null)
                        sub.Texture = tex;
                }
            }
        }

        // Initialize weapon
        weapon.OnEquip(this, _viewmodelEntity!, _viewmodelAnimator!);

        // restaurar estado de munição
        if (_ammoState.TryGetValue(weaponId, out var state))
        {
            weapon.CurrentAmmo = state.CurrentAmmo;
            weapon.ReserveAmmo = state.ReserveAmmo;
        }

        // Ativar contexto de arma para permitir input
        InputManager.RequestContext(InputContext.Weapon);

        Logger.Info($"[WeaponSystem] Equipped: {weaponId}");
        return true;
    }

    public void Unequip()
    {
        if (_currentWeapon != null)
        {
            _ammoState[_currentWeaponId!] = (_currentWeapon.CurrentAmmo, _currentWeapon.ReserveAmmo);
            _currentWeapon.OnUnequip();
            _currentWeapon = null;
            _currentWeaponId = null;
        }
        DestroyViewmodel();
        InputManager.ReleaseContext(InputContext.Weapon);
    }

    public bool TryShoot()
    {
        if (_currentWeapon == null || !_currentWeapon.CanFire())
            return false;

        _currentWeapon.Fire(_camera.Position, _camera.Front);
        return true;
    }

    public void Reload()
    {
        _currentWeapon?.Reload();
    }

    public void SwitchWeapon(string weaponId)
    {
        Equip(weaponId);
    }

    public void Update(float dt)
    {
        _currentWeapon?.Update(dt);
        if (_viewmodelAnimator != null)
            _currentWeapon?.UpdateViewmodel(dt, _viewmodelAnimator);

        UpdateViewmodelTransform();
    }

    public void PhysicsUpdate(float dt)
    {
        // Para armas com projéteis físicos no futuro
    }

    public IWeapon? CurrentWeapon => _currentWeapon;
    public string? CurrentWeaponId => _currentWeaponId;
    public bool HasWeapon => _currentWeapon != null;

    private void CreateViewmodel(IWeapon weapon)
    {
        var model = _assets.GetSkinnedModel(weapon.ViewmodelModelPath);
        if (model == null)
        {
            Logger.Error($"[WeaponSystem] Failed to load viewmodel: {weapon.ViewmodelModelPath}");
            return;
        }

        _viewmodelModel = model;
        _viewmodelModel.HiddenSubmeshes.Add("Supressor");
        _viewmodelModel.HiddenSubmeshes.Add("LeupoldRedDot");
        _viewmodelModel.HiddenSubmeshes.Add("LeupoldRedDotGlass");
        _viewmodelAnimator = new Animator(model.Skeleton);
        model.Link(_viewmodelAnimator);

        _viewmodelEntity = _sceneManager.ActiveScene.Add(null, $"viewmodel_{weapon.Id}");
        _viewmodelEntity.SkinnedModel = model;
        _viewmodelEntity.Animator = _viewmodelAnimator;
        _viewmodelEntity.Visible = true;  // FORÇAR VISÍVEL

        if (!string.IsNullOrEmpty(model.DefaultClipName))
            _viewmodelAnimator.Play(model.DefaultClipName);
        else if (!string.IsNullOrEmpty(weapon.ViewmodelIdleAnim))
            _viewmodelAnimator.Play(weapon.ViewmodelIdleAnim);

        UpdateViewmodelTransform();

        // DEBUG
        Logger.Info($"[WeaponSystem] Viewmodel created: {_viewmodelEntity.Id}");
        Logger.Info($"[WeaponSystem] Scene entities count: {_sceneManager.ActiveScene.Entities.Count}");
        Logger.Info($"[WeaponSystem] Viewmodel Visible: {_viewmodelEntity.Visible}, SkinnedModel: {_viewmodelEntity.SkinnedModel != null}, Animator: {_viewmodelEntity.Animator != null}");
        Logger.Info($"[WeaponSystem] Viewmodel Pos: {_viewmodelEntity.Transform.Position}, Scale: {_viewmodelEntity.Transform.Scale}");
    }

    private void DestroyViewmodel()
    {
        if (_viewmodelEntity != null)
        {
            _sceneManager.ActiveScene.Remove(_viewmodelEntity);
            _viewmodelEntity = null;
        }
        _viewmodelAnimator = null;
        _viewmodelModel = null;
    }

    internal void UpdateViewmodelTransform()
    {
        if (_viewmodelEntity == null || _player == null) return;

        // Se congelado, não atualiza posição/rotação
        if (FreezeViewmodel)
        {
            // Na primeira vez que congela, salva a posição atual
            if (_frozenPosition == Vector3.Zero && _frozenRotation == Quaternion.Identity)
            {
                _frozenPosition = _viewmodelEntity.Transform.Position;
                _frozenRotation = _viewmodelEntity.Transform.Rotation;
            }
            return; // Mantém posição congelada
        }
        else
        {
            // Reset quando descongela
            _frozenPosition = Vector3.Zero;
            _frozenRotation = Quaternion.Identity;
        }

        var cam = _player.Camera;
        var camPos = cam.Position;
        var camRot = cam.Rotation;

        // Fix inverted yaw
        var camRotFixed = new Quaternion(camRot.X, -camRot.Y, camRot.Z, -camRot.W);

        // Use adjustable offset
        var offset = ViewmodelOffset;
        var viewmodelPos = camPos + Vector3.Transform(offset, camRotFixed);

        // Apply rotation offset
        var rotOffset = Vector3.DegreesToRadians(ViewmodelRotationDeg);
        var rotOffsetQuat = Quaternion.CreateFromYawPitchRoll(rotOffset.Y, rotOffset.X, rotOffset.Z);

        _viewmodelEntity.Transform.Position = viewmodelPos;
        _viewmodelEntity.Transform.Rotation = camRotFixed * rotOffsetQuat;
        _viewmodelEntity.Transform.Scale = Vector3.One;
    }

    public void TeleportViewmodelToCamera()
    {
        if (_viewmodelEntity == null || _player == null) return;

        var cam = _player.Camera;
        var camPos = cam.Position;
        var camRot = cam.Rotation;
        var camRotFixed = new Quaternion(camRot.X, -camRot.Y, camRot.Z, -camRot.W);

        var offset = ViewmodelOffset;
        var viewmodelPos = camPos + Vector3.Transform(offset, camRotFixed);

        var rotOffset = Vector3.DegreesToRadians(ViewmodelRotationDeg);
        var rotOffsetQuat = Quaternion.CreateFromYawPitchRoll(rotOffset.Y, rotOffset.X, rotOffset.Z);

        _viewmodelEntity.Transform.Position = viewmodelPos;
        _viewmodelEntity.Transform.Rotation = camRotFixed * rotOffsetQuat;

        Logger.Info($"[WeaponSystem] Viewmodel teleported to camera: {viewmodelPos}");
    }

    public void Dispose()
    {
        Unequip();
        foreach (var weapon in _weapons.Values)
            weapon.Dispose();
        _weapons.Clear();
    }
}