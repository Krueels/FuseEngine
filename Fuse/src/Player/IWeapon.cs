using System.Numerics;
using Fuse.Renderer;
using Fuse.Animation;
using Fuse.Physics;
using Fuse.AssetManagement;

namespace Fuse.Player;

public interface IWeapon : IDisposable
{
    string Id { get; }
    string ViewmodelModelPath { get; }
    string ViewmodelIdleAnim { get; }
    string ViewmodelFireAnim { get; }
    string ViewmodelReloadAnim { get; }
    string ViewmodelReloadEmptyAnim { get; }
    string ViewmodelDrawAnim { get; }

    // Recoil
    float RecoilYawKick { get; }      // quanto kick horizontal (graus)
    float RecoilPitchKick { get; }    // quanto kick vertical (graus) - geralmente negativo (sobe)
    float RecoilRollKick { get; }     // quanto roll (graus)
    float RecoilRecoverySpeed { get; } // override da câmera (opcional)

    Dictionary<string, string> ViewmodelTextures { get; }

    // Muzzle flash
    Vector3 MuzzleFlashOffset { get; }        // offset relativo à câmera (em world space)
    Vector2 MuzzleFlashSize { get; }          // tamanho em units
    float MuzzleFlashDuration { get; }        // duração em segundos
    string MuzzleFlashTexturePath { get; }    // caminho da textura

    float FireRate { get; }             // tiros por segundo
    float Damage { get; }
    float Range { get; }
    int MagazineSize { get; }
    int CurrentAmmo { get; set; }
    int ReserveAmmo { get; set; }
    bool IsAutomatic { get; }
    float ReloadTime { get; }

    // Debug info
    string CurrentAnimState { get; }
    float CurrentAnimTime { get; }
    float CurrentAnimDuration { get; }
    
    // Called when weapon is equipped
    void OnEquip(WeaponSystem system, Entity viewmodelEntity, Animator animator);

    // Called when weapon is unequipped
    void OnUnequip();

    // Check if weapon can fire (ammo, cooldown, not reloading)
    bool CanFire();

    // Perform the shot - origin/direction from camera
    void Fire(Vector3 origin, Vector3 direction);

    // Start reload process
    void Reload();

    // Update per frame (cooldowns, reload timer, etc)
    void Update(float dt);

    // Update viewmodel animation state
    void UpdateViewmodel(float dt, Animator animator);
}