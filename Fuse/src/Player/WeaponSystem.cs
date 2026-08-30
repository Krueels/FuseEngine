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
    public global::Fuse.Player.Player Player => _player;

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

    // Muzzle flash
    private bool _muzzleFlashVisible;
    private float _muzzleFlashTimer;
    private Vector3 _muzzleFlashOffset;
    private Vector2 _muzzleFlashSize;
    private Texture? _muzzleFlashTexture;
    private Light? _muzzleFlashLight;

    // Viewmodel offset (ajustável via debug UI)
    public Vector3 ViewmodelOffset { get; set; } = new Vector3(0.0f, -0.83f, 0.0f);
    public Vector3 ViewmodelRotationDeg { get; set; } = new Vector3(0f, 90f, 0f);

    // Debug: freeze viewmodel position
    public bool FreezeViewmodel { get; set; } = false;
    public bool MuzzleFlashVisible => _muzzleFlashVisible;
    public Vector3 MuzzleFlashPosition { get; private set; }
    public Vector2 MuzzleFlashSize => _muzzleFlashSize;
    public Texture? MuzzleFlashTexture => _muzzleFlashTexture;
    public bool ForceMuzzleFlash { get; set; }
    public Vector2 MuzzleFlashSizeEdit { get; set; } = new(0.3f, 0.3f);


    // ImGui editável
    public Vector3 MuzzleFlashOffsetEdit { get; set; }

    private Vector3 _frozenPosition;
    private Quaternion _frozenRotation;

    // Render-only camera pose. The gameplay camera itself is never modified.
    private string? _cameraAnimationNode;
    private Quaternion _cameraAnimationRotation = Quaternion.Identity;
    private bool _cameraAnimationActive;
    private Vector3 _renderFront;
    private Vector3 _renderUp;
    private Matrix4x4 _renderViewMatrix;

    public Vector3 RenderFront => _renderFront;
    public Matrix4x4 RenderViewMatrix => _renderViewMatrix;

    // Debug
    public Debug.DebugDrawer? DebugDrawer { get; set; }
    public bool DecalDebugEnabled { get; set; } = false;

    public WeaponSystem(global::Fuse.Player.Player player, Camera camera, PhysicsWorld physics,
                        AssetManager assets, AudioSystem audio, Scene.SceneManager sceneManager)
    {
        _player = player;
        _camera = camera;
        _physics = physics;
        _assets = assets;
        _audio = audio;
        _sceneManager = sceneManager;

        _camera.GetViewBasis(out _renderFront, out _renderUp);
        _renderViewMatrix = _camera.GetViewMatrix(_renderFront, _renderUp);
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

        // aplicar somente materiais aos braços e à arma.
        var armsMaterial = _assets.GetMaterial(Bible.MAT_Arms);

        var weaponMaterial = string.IsNullOrWhiteSpace(weapon.ViewmodelMaterialPath)
            ? null
            : _assets.GetMaterial(weapon.ViewmodelMaterialPath);

        if (_viewmodelModel != null)
        {
            foreach (var sub in _viewmodelModel.Submeshes)
            {
                sub.Material = null;

                // Braços usam exclusivamente MAT_Arms.
                if (sub.Name.Equals("ArmsMale", StringComparison.OrdinalIgnoreCase))
                {
                    sub.Material = armsMaterial;
                    sub.Texture = null;
                    continue;
                }

                // Todos os demais submeshes da Glock usam exclusivamente MAT_Glock.
                if (weaponMaterial != null)
                {
                    sub.Material = weaponMaterial;
                    sub.Texture = null;
                }
            }
        }

        // Initialize weapon
        weapon.OnEquip(this, _viewmodelEntity!, _viewmodelAnimator!);

        // Inicializar offset/size editáveis com os valores padrão da arma
        MuzzleFlashOffsetEdit = weapon.MuzzleFlashOffset;
        MuzzleFlashSizeEdit = weapon.MuzzleFlashSize;

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

        // Use the same render direction as the crosshair/world image. The
        // origin remains the gameplay camera position and never moves.
        UpdateCameraAnimationPose();
        UpdateRenderCameraPose();
        _currentWeapon.Fire(_camera.Position, _renderFront);
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

        // Atualizar muzzle flash
        if (_muzzleFlashVisible)
        {
            if (!ForceMuzzleFlash)
            {
                _muzzleFlashTimer -= dt;
                if (_muzzleFlashTimer <= 0)
                {
                    _muzzleFlashVisible = false;

                    // Remover luz do muzzle flash quando timer acaba
                    if (_muzzleFlashLight != null && _sceneManager?.ActiveScene != null)
                    {
                        _sceneManager.ActiveScene.RemoveLight(_muzzleFlashLight);
                        _muzzleFlashLight = null;
                    }
                }
            }

            // Atualizar posição da luz junto com billboard
            if (_muzzleFlashLight != null)
                _muzzleFlashLight.Position = MuzzleFlashPosition;

            UpdateMuzzleFlashPosition();
        }

    }

    /// <summary>
    /// Updates only camera-dependent weapon visuals. This must run once per
    /// rendered frame, independently of the fixed physics timestep, otherwise
    /// the viewmodel can remain several frames behind mouse camera movement.
    /// </summary>
    public void RenderUpdate(float dt)
    {
        UpdateCameraAnimationPose();
        UpdateRenderCameraPose();
        UpdateViewmodelTransform(dt);
    }

    public void Render(Renderer.MasterRenderer renderer, Camera camera, float aspect)
    {
        if (_muzzleFlashVisible && _muzzleFlashTexture != null)
        {
            var view = _renderViewMatrix;
            var proj = camera.GetProjectionMatrix(aspect);
            renderer.QueueBillboard(view, proj,
                _muzzleFlashTexture.ID,
                MuzzleFlashPosition,
                _muzzleFlashSize,
                new Vector4(1, 1, 1, 1));
        }
    }


    private void UpdateMuzzleFlashPosition()
    {
        if (_player == null) return;

        var cam = _player.Camera;
        var camPos = cam.Position;

        // Usar offset editável no ImGui em tempo real
        var offset = MuzzleFlashOffsetEdit;
        MuzzleFlashPosition = camPos
            + cam.Front * offset.Z    // frente
            + cam.Up * offset.Y       // cima
            + cam.Right * offset.X;   // direita
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

        _cameraAnimationNode = model.Skeleton.Nodes
            .FirstOrDefault(node => node.Name.Equals("camera", StringComparison.OrdinalIgnoreCase))?.Name;
        _cameraAnimationRotation = Quaternion.Identity;
        _cameraAnimationActive = false;

        _viewmodelEntity = _sceneManager.ActiveScene.Add(null, $"viewmodel_{weapon.Id}");
        _viewmodelEntity.SkinnedModel = model;
        _viewmodelEntity.Animator = _viewmodelAnimator;
        _viewmodelEntity.Visible = true;  // FORÇAR VISÍVEL
        _viewmodelEntity.IsViewmodel = true;

        if (!string.IsNullOrEmpty(model.DefaultClipName))
            _viewmodelAnimator.Play(model.DefaultClipName);
        else if (!string.IsNullOrEmpty(weapon.ViewmodelIdleAnim))
            _viewmodelAnimator.Play(weapon.ViewmodelIdleAnim);

        UpdateViewmodelTransform(0f);

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
        _cameraAnimationNode = null;
        _cameraAnimationRotation = Quaternion.Identity;
        _cameraAnimationActive = false;
    }

    private void UpdateCameraAnimationPose()
    {
        _cameraAnimationActive = false;
        _cameraAnimationRotation = Quaternion.Identity;

        if (_viewmodelAnimator == null || string.IsNullOrEmpty(_cameraAnimationNode))
            return;

        if (!_viewmodelAnimator.TryGetNodeAnimationRotation(
                _cameraAnimationNode, out Quaternion rotation))
            return;

        _cameraAnimationRotation = rotation;
        _cameraAnimationActive = true;
    }

    private void UpdateRenderCameraPose()
    {
        _camera.GetViewBasis(out Vector3 front, out Vector3 up);

        if (_cameraAnimationActive)
        {
            front = Vector3.Transform(front, _cameraAnimationRotation);
            up = Vector3.Transform(up, _cameraAnimationRotation);
        }

        if (front.LengthSquared() < 0.000001f)
            front = _camera.Front;
        else
            front = Vector3.Normalize(front);

        // Re-orthogonalize the basis so animation never introduces shear.
        up -= front * Vector3.Dot(up, front);
        if (up.LengthSquared() < 0.000001f)
            up = _camera.Up;
        else
            up = Vector3.Normalize(up);

        _renderFront = front;
        _renderUp = up;
        _renderViewMatrix = _camera.GetViewMatrix(front, up);
    }

    internal void UpdateViewmodelTransform(float dt)
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

        // Viewmodel segue APENAS a rotação da câmera (que já inclui recoil)
        var offset = ViewmodelOffset;
        var viewmodelPos = camPos + Vector3.Transform(offset, camRotFixed);

        // Rotação base apenas - camera.Rotation já inclui recoil
        var rotOffset = Vector3.DegreesToRadians(ViewmodelRotationDeg);
        var rotOffsetQuat = Quaternion.CreateFromYawPitchRoll(rotOffset.Y, rotOffset.X, rotOffset.Z);

        _viewmodelEntity.Transform.Position = viewmodelPos;
        _viewmodelEntity.Transform.Rotation = camRotFixed * rotOffsetQuat;
        _viewmodelEntity.Transform.Scale = Vector3.One;
    }

    public void ShowMuzzleFlash(Vector3 offset, Vector2 size, float duration)
    {
        _muzzleFlashOffset = offset;
        _muzzleFlashSize = MuzzleFlashSizeEdit;
        _muzzleFlashTimer = duration;
        _muzzleFlashVisible = true;

        if (_currentWeapon != null)
        {
            _muzzleFlashTexture = _assets.GetTexture(_currentWeapon.MuzzleFlashTexturePath, TextureColorSpace.Srgb);
        }

        if (_sceneManager?.ActiveScene != null)
        {
            _muzzleFlashLight = new Light
            {
                Id = "muzzle_flash_light",
                Type = LightType.Point,
                Position = MuzzleFlashPosition,
                Color = Vector3.One,
                Radius = 15.0f,
                Intensity = 50.0f,
                CastShadows = false,
                Dynamic = true,
                Enabled = true,
            };
            _sceneManager.ActiveScene.AddLight(_muzzleFlashLight);
        }

        // Atualizar posição imediatamente para evitar frame com posição desatualizada
        UpdateMuzzleFlashPosition();
    }

    public void SpawnImpactDecal(Vector3 position, Vector3 normal, string decalType = "bullet_hole", Physics.RigidBody? parentBody = null)
    {
        if (_sceneManager?.Renderer == null) return;
        uint textureId = _assets.GetTexture(Bible.Tex(Bible.DecalBulletHoleAlbedo), TextureColorSpace.Srgb).ID;
        _sceneManager.Renderer.SpawnDecal(position, normal, textureId, size: 0.10f, lifeTime: 30f, fadeStart: 0.7f, parentBody: parentBody, physics: _physics);
    }





    // kept for reference – no longer used internally
    private Matrix4x4 CreateDecalProjection(Vector3 pos, Vector3 normal, float size)
    {
        Vector3 forward = normal;
        Vector3 right = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, forward));
        if (right.LengthSquared() < 0.001f) right = Vector3.Normalize(Vector3.Cross(Vector3.UnitZ, forward));
        Vector3 up = Vector3.Cross(forward, right);

        Matrix4x4 view = Matrix4x4.CreateLookAt(pos, pos + forward, up);
        Matrix4x4 proj = Matrix4x4.CreateOrthographic(size * 2, size * 2, 0.01f, size);
        return proj * view;
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
