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

    // Material - Spider
    public const string MAT_SPIDER_BODY = "Materials/SPIDER_BODY.fmat";

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
    public const string ShaderForwardPlusCull = "Shaders/forward_plus.comp";

    // Shaders - billboard
    public const string ShaderBillboardVert = "Shaders/billboard.vert";
    public const string ShaderBillboardFrag = "Shaders/billboard.frag";

    // Shaders - Decals
    public const string ShaderDecalVert = "Shaders/decals.vert";
    public const string ShaderDecalFrag = "Shaders/decals.frag";

    // Shaders - Post Process
    public const string PostProcessVert = "Shaders/PostProcess/composite.vert";
    public const string PostProcessFrag = "Shaders/PostProcess/composite.frag";

    // Shaders - Volumetric Clouds
    public const string VolumetricCloudFrag = "Shaders/Clouds/volumetric_clouds.frag";
    public const string VolumetricCloudCompositeFrag = "Shaders/Clouds/cloud_composite.frag";
    public const string VolumetricCloudShadowFrag = "Shaders/Clouds/cloud_shadow.frag";
    public const string VolumetricCloudNoiseCompute = "Shaders/Clouds/cloud_noise.comp";
    public const string VolumetricCloudWeatherCompute = "Shaders/Clouds/cloud_weather.comp";

    // Shaders - Ocean
    public const string OceanVert = "Shaders/Ocean/ocean.vert";
    public const string OceanFrag = "Shaders/Ocean/ocean.frag";
    public const string OceanSimulationCompute = "Shaders/Ocean/ocean_simulation.comp";
    public const string OceanSpectrumCompute = "Shaders/Ocean/ocean_spectrum.comp";
    public const string OceanFftCompute = "Shaders/Ocean/ocean_fft.comp";
    public const string OceanResolveCompute = "Shaders/Ocean/ocean_resolve.comp";
    public const string UnderwaterFrag = "Shaders/Ocean/underwater.frag";

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

    /// <summary>
    /// Schedules the assets used by the first gameplay frame. Textures are
    /// decoded in the background and models/shaders are admitted to the bounded
    /// render-thread queue, so map loading no longer performs all work in one call.
    /// </summary>
    public static void QueuePreload(AssetManager assets, AudioSystem? audio = null)
    {
        assets.QueueTexturePreload(Tex(Crosshair), TextureColorSpace.Linear, AssetPriority.Critical);
        assets.QueueTexturePreload(Tex(CrosshairInteract), TextureColorSpace.Linear, AssetPriority.Critical);
        assets.QueueTexturePreload(Tex(EnemyIcon), TextureColorSpace.Linear, AssetPriority.High);
        for (int i = 0; i < 3; i++)
            assets.QueueTexturePreload(Tex(string.Format(MuzzleFlash, i)), TextureColorSpace.Srgb, AssetPriority.High);

        // Viewmodels use the skinned loader. QueueModelPreload would populate
        // only the static-model cache and the weapon would still hitch on equip.
        assets.QueueSkinnedModelPreload(Model(GlockModel), AssetPriority.Critical);
        assets.QueueSkinnedModelPreload(Model(AKModel), AssetPriority.High);
        assets.QueueMaterialPreload(MAT_Arms, AssetPriority.Critical);
        assets.QueueMaterialPreload(MAT_Glock, AssetPriority.High);
        assets.QueueMaterialPreload(MAT_AK, AssetPriority.High);
        assets.QueueMaterialPreload(MAT_SPIDER_BODY, AssetPriority.Normal);

        assets.QueueSkinnedModelPreload(Model(UniSexGuy), AssetPriority.Normal);
        assets.QueueSkinnedModelPreload(Model(SpiderModel), AssetPriority.Normal);

        if (audio == null)
            return;

        audio.QueuePreloadSound(Audio(GlockDrawFirst), AssetPriority.Critical);
        audio.QueuePreloadSound(Audio(GlockReload), AssetPriority.High);
        audio.QueuePreloadSound(Audio(GlockReloadEmpty), AssetPriority.High);
        for (int i = 0; i < 4; i++)
            audio.QueuePreloadSound(Audio($"{GlockFire}{i}.wav"), AssetPriority.High);

        for (int i = 0; i < 4; i++)
            audio.QueuePreloadSound(Audio($"{AKFire}{i}.wav"), AssetPriority.Normal);
        audio.QueuePreloadSound(Audio(AKReload), AssetPriority.Normal);
        audio.QueuePreloadSound(Audio(AKReloadEmpty), AssetPriority.Normal);

        for (int i = 0; i < 3; i++)
            audio.QueuePreloadSound(Audio($"{BulletImpact}{i:D2}.mp3"), AssetPriority.Low);

        // These are the sounds that previously caused the random first-use spike.
        for (int i = 1; i <= 15; i++)
            audio.QueuePreloadSound($"{SpiderFootStep}{i:00}.wav", AssetPriority.High);
    }

    public static void PreloadAll(AssetManager assets, AudioSystem? audio = null)
    {
        // --- Texturas ---
        assets.GetTexture(Tex(Crosshair), Fuse.Renderer.TextureColorSpace.Linear);
        assets.GetTexture(Tex(CrosshairInteract), Fuse.Renderer.TextureColorSpace.Linear);
        assets.GetTexture(Tex(EnemyIcon), Fuse.Renderer.TextureColorSpace.Linear);
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
