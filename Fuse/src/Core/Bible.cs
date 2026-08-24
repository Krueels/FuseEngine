using Fuse.AssetManagement;

namespace Fuse.Core;

public static class Bible
{
    // UI
    public const string Crosshair = "UI/crosshair.png";
    public const string CrosshairInteract = "UI/crosshair_interact.png";
    public const string EnemyIcon = "UI/enemy_icon.png";

    // Weapons
    public const string GlockAlbedo = "weapons/glock/Glock_ALB.png";
    public const string MuzzleFlash = "FX/muzzle_flash.png";

    // Viewmodel
    public const string ArmsMale = "ArmsMale_ALB.png";

    // Environment
    public const string Skybox = "skybox_1.png";
    public const string Crate = "dev_measurecrate01.bmp";

    // Models
    public const string GlockModel = "skinned_models/Glock.fbx";
    public const string TrapKingModel = "skinned_models/TrapKing.fbx";

    // Shaders
    public const string ShaderDefaultVert = "Shaders/default.vert";
    public const string ShaderDefaultFrag = "Shaders/default.frag";
    public const string ShaderSkyboxVert = "Shaders/skybox.vert";
    public const string ShaderSkyboxFrag = "Shaders/skybox.frag";
    public const string ShaderShadowVert = "Shaders/shadow.vert";
    public const string ShaderShadowFrag = "Shaders/shadow.frag";
    public const string ShaderPointShadowVert = "Shaders/point_shadow.vert";
    public const string ShaderPointShadowFrag = "Shaders/point_shadow.frag";
    public const string ShaderSkinnedVert = "Shaders/skinned.vert";
    public const string ShaderSkinnedShadowVert = "Shaders/shadow_skinned.vert";
    public const string ShaderPointShadowSkinnedVert = "Shaders/point_shadow_skinned.vert";
    public const string ShaderBillboardVert = "Shaders/billboard.vert";
    public const string ShaderBillboardFrag = "Shaders/billboard.frag";

    // Shaders - Post Process
    public const string PostProcessVert = "Shaders/PostProcess/composite.vert";
    public const string PostProcessFrag = "Shaders/PostProcess/composite.frag";

    // Audio
    public const string GlockReload = "Audio/weapons/glock/Glock_Reload.wav";
    public const string GlockReloadEmpty = "Audio/weapons/glock/Glock_ReloadEmpty.wav";
    public const string GlockFire0 = "Audio/weapons/glock/Glock_Fire0.wav";
    public const string GlockFire1 = "Audio/weapons/glock/Glock_Fire1.wav";
    public const string GlockFire2 = "Audio/weapons/glock/Glock_Fire2.wav";
    public const string GlockFire3 = "Audio/weapons/glock/Glock_Fire3.wav";

    // Bullet Impact
    public const string BulletImpactFleshPrefix = "Audio/weapons/Bullet_Impact_Flesh_";

    // Helpers
    public static string Tex(string name) => $"{ResPath.Path}/Textures/{name}";
    public static string Model(string name) => $"{ResPath.Path}/{name}";
    public static string Audio(string name) => $"{ResPath.Path}/{name}";
    public static string Shader(string name) => $"{ResPath.Path}/{name}";

    public static void PreloadAll(AssetManager assets)
    {
        assets.GetTexture(Tex(Crosshair));
        assets.GetTexture(Tex(CrosshairInteract));
        assets.GetTexture(Tex(EnemyIcon));
        assets.GetTexture(Tex(GlockAlbedo));
        assets.GetTexture(Tex(MuzzleFlash));
        assets.GetTexture(Tex(ArmsMale));
        assets.GetTexture(Tex(Skybox));
        assets.GetTexture(Tex(Crate));
        assets.GetSkinnedModel(Model(GlockModel));
        assets.GetSkinnedModel(Model(TrapKingModel));
    }
}