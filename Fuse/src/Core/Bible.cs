using Fuse.AssetManagement;
using Fuse.Audio;
using Fuse.Renderer;

namespace Fuse.Core;

public static class Bible
{
    //Font
    public const string DefaultFont = "Fonts/Obelisk-Demo.ttf";


    // Material - Viewmodel
    public const string MAT_Arms = "Materials/MAT_Arms.fmat";

    // Material - Glock
    public const string MAT_Glock = "Materials/MAT_Glock.fmat";

    // Material - AK
    public const string MAT_AK = "Materials/MAT_AK.fmat";

    // Textures - UI
    public const string Crosshair = "UI/crosshair.png";
    public const string CrosshairInteract = "UI/crosshair_interact.png";
    public const string EnemyIcon = "UI/enemy_icon.png";

    // Textures - Weapons
    public const string MuzzleFlash = "FX/muzzle_flash_{0}.png";
    
    // Textures - Environment
    public const string Skybox = "Skybox/skybox_1.png";
    public const string Crate = "dev_measurecrate01.bmp";

    // Textures - Decals
    public const string DecalBulletHoleAlbedo = "decals/decal_bullet_hole.png";

    // Textures - UnisexGuy
    public const string UniSexBody = "UniSexGuyBody_ALB.png";
    public const string UniSexEyes = "UniSexGuyEyes_ALB.png";

    // Models
    public const string GlockModel = "skinned_models/Glock.fbx";
    public const string AKModel = "skinned_models/AKS47U.fbx";

    // Models - UnisexGuy
    public const string UniSexGuy = "skinned_models/UniSexGuyScaled.fbx";
    public const string UniSexGuyIdle = "Animations/UnisexGuy_AKS74U_Idle.fbx";
    public const string UniSexGuyWalk = "Animations/UnisexGuy_AKS74U_Walk.fbx";
    
    // Models - Spider
    public const string SpiderModel = "skinned_models/spider.fbx";


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

    // Shaders - billboard
    public const string ShaderBillboardVert = "Shaders/billboard.vert";
    public const string ShaderBillboardFrag = "Shaders/billboard.frag";

    // Shaders - Decals
    public const string ShaderDecalVert = "Shaders/decals.vert";
    public const string ShaderDecalFrag = "Shaders/decals.frag";

    // Shaders - Post Process
    public const string PostProcessVert = "Shaders/PostProcess/composite.vert";
    public const string PostProcessFrag = "Shaders/PostProcess/composite.frag";

    // Shaders - DeathScreen
    public const string DeathScreenGlsl = "Shaders/death_screen.glsl";
    public const string DeathScreenVert = "Shaders/death_screen.vert";


    // Audio
    public const string BulletImpact = "Audio/weapons/Bullet_Impact_Flesh_";

    // Audio - Glock
    public const string GlockDrawFirst = "Audio/weapons/glock/Glock_DrawFirst.wav";
    public const string GlockReload = "Audio/weapons/glock/Glock_Reload.wav";
    public const string GlockReloadEmpty = "Audio/weapons/glock/Glock_ReloadEmpty.wav";
    public const string GlockFire = "Audio/weapons/glock/Glock_Fire";

    // Audio - AK
    public const string AKFire = "Audio/weapons/ak/AKS74U_Fire";
    public const string AKReload = "Audio/weapons/ak/AKS74U_Reload.wav";
    public const string AKReloadEmpty = "Audio/weapons/ak/AKS74U_ReloadEmpty.wav";

    // Audio - Player
    public const string DeathSound = "Audio/DeathSound.mp3";

    // Audio - Spider
    public const string SpiderFootStep = "Audio/spider_footstep_";

    // Helpers
    public static string Tex(string name) => $"{ResPath.Path}/Textures/{name}";
    public static string Model(string name) => $"{ResPath.Path}/{name}";
    public static string Audio(string name) => $"{ResPath.Path}/{name}";
    public static string Shader(string name) => $"{ResPath.Path}/{name}";
    public static string Font(string name) => $"{ResPath.Path}/{name}";

    public static void PreloadAll(AssetManager assets, AudioSystem? audio = null)
    {
        // --- Texturas ---
        assets.GetTexture(Tex(Crosshair), Fuse.Renderer.TextureColorSpace.Linear);
        assets.GetTexture(Tex(CrosshairInteract), Fuse.Renderer.TextureColorSpace.Linear);
        assets.GetTexture(Tex(EnemyIcon), Fuse.Renderer.TextureColorSpace.Linear);
        //assets.GetTexture(Tex(GlockAlbedo), Fuse.Renderer.TextureColorSpace.Srgb);
        //assets.GetTexture(Tex(ArmsMale), Fuse.Renderer.TextureColorSpace.Srgb);
        assets.GetTexture(Tex(Skybox), Fuse.Renderer.TextureColorSpace.Srgb);
        assets.GetTexture(Tex(Crate), Fuse.Renderer.TextureColorSpace.Srgb);
        for (int i = 0; i < 3; i++)
            assets.GetTexture(Tex(string.Format(MuzzleFlash, i)), Fuse.Renderer.TextureColorSpace.Srgb);

        // --- Modelos skinned (Glock + AK + Inimigo) ---
        assets.GetSkinnedModel(Model(GlockModel));
        assets.GetSkinnedModel(Model(AKModel));
        assets.GetSkinnedModel(Model(UniSexGuy));
        assets.GetSkinnedModel(Model(SpiderModel));

        // --- Animações do inimigo ---
        SkinnedModelLoader.MergeAnimationsFromFile(
            assets.GetSkinnedModel(Model(UniSexGuy))!,
            Model(UniSexGuyIdle), "Idle");
        SkinnedModelLoader.MergeAnimationsFromFile(
            assets.GetSkinnedModel(Model(UniSexGuy))!,
            Model(UniSexGuyWalk), "Walk");

        // --- Áudio ---
        if (audio != null)
        {
            // Glock
            audio.PreloadSound(Audio(GlockDrawFirst));
            audio.PreloadSound(Audio(GlockReload));
            audio.PreloadSound(Audio(GlockReloadEmpty));
            for (int i = 0; i < 4; i++)
                audio.PreloadSound(Audio($"{GlockFire}{i}.wav"));

            // AK
            audio.PreloadSound(Audio(AKFire + "0.wav"));
            audio.PreloadSound(Audio(AKFire + "1.wav"));
            audio.PreloadSound(Audio(AKFire + "2.wav"));
            audio.PreloadSound(Audio(AKFire + "3.wav"));
            audio.PreloadSound(Audio(AKReload));
            audio.PreloadSound(Audio(AKReloadEmpty));

            // Impactos
            for (int i = 0; i < 3; i++)
                audio.PreloadSound(Audio($"{BulletImpact}{i:D2}.mp3"));

            // SpiderFootStep
            for (int i = 1; i <= 15; i++)
                audio.PreloadSound($"{SpiderFootStep}{i:00}.wav");
        }
    }

    public static bool IsEmissiveTexture(string texturePath)
        => !string.IsNullOrEmpty(texturePath) &&
           texturePath.Contains("emi_", StringComparison.OrdinalIgnoreCase);
}
