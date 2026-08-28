using Fuse.Core;
using Fuse.Input;
using Fuse.Player;
using Fuse.Enemy;
using Fuse.Scene;
using Fuse.Renderer;
using Fuse.Physics;
using Fuse.Audio;
using Fuse.AssetManagement;

namespace Fuse.Core;

public static class DevShortcuts
{
    public static void HandleInput(
        SceneManager sceneManager,
        Player.Player player,
        WeaponSystem? weaponSystem,
        EnemySystem? enemySystem,
        PhysicsWorld physics,
        AudioSystem audio,
        AssetManager assets,
        MasterRenderer renderer,
        Debug.DebugDrawer debugDrawer,
        ref bool screenshotRequested,
        Action requestMapReload)
    {
        // Screenshots
        if (Input.Input.KeyPressed(KeyCodes.F2))
            screenshotRequested = true;

        if (Input.Input.KeyPressed(KeyCodes.F4))
            renderer.ReloadPostProcessShader();

        if (Input.Input.KeyPressed(KeyCodes.F6))
        {
            EnemyPatrol.Enabled = !EnemyPatrol.Enabled;
            Logger.Info($"[DevShortcuts] Patrol {(EnemyPatrol.Enabled ? "ON" : "OFF")}");
        }
    
        // Map reload
        if (Input.Input.KeyPressed(KeyCodes.F5))
            requestMapReload();

        // Debug drawer toggle
        if (Input.Input.KeyPressed(KeyCodes.F9))
            debugDrawer.Toggle();

        // Post-processing toggle
        if (Input.Input.KeyPressed(KeyCodes.F10))
            renderer.PostPipeline.Settings.Enabled = !renderer.PostPipeline.Settings.Enabled;

        // Shadow toggle
        if (Input.Input.KeyPressed(KeyCodes.F12))
            renderer.ShadowsEnabled = !renderer.ShadowsEnabled;

        // G: Spawn explosion at raycast hit
        if (Input.Input.KeyPressed(KeyCodes.G))
        {
            if (sceneManager.Raycast(player.Camera.Position, player.Camera.Front, 20f, out var hit))
            {
                Explosion.Apply(physics, hit.Position, 105f, 10000.0f, audio);
            }
        }

        // J: Spawn enemy at raycast hit
        if (Input.Input.KeyPressed(KeyCodes.J))
        {
            if (sceneManager.Raycast(player.Camera.Position, player.Camera.Front, 20f, out var hit))
                enemySystem?.SpawnEnemy(hit.Position, 50f);
        }

        // J: Spawn spider at raycast hit
        if (Input.Input.KeyPressed(KeyCodes.V))
        {
            if (sceneManager.Raycast(player.Camera.Position, player.Camera.Front, 20f, out var hit))
                enemySystem?.SpawnSpider(hit.Position, 50f);
        }

        // T: Spray decal
        if (Input.Input.KeyPressed(KeyCodes.T))
        {
            if (sceneManager.Raycast(player.Camera.Position, player.Camera.Front, 20f, out var hit))
            {
                uint sprayTexId = assets.GetTexture(Bible.Tex("decals/afx.png")).ID;
                sceneManager.Renderer.SpawnDecal(hit.Position, hit.Normal, sprayTexId, 1.0f, parentBody: hit.RigidBody, physics: physics);
                audio?.Play3D(Bible.Audio("Audio/Spray.wav"), hit.Position);
            }
        }



        // Weapon switching (1, 0)
        if (Input.Input.KeyPressed(KeyCodes.D1))
            weaponSystem?.SwitchWeapon("glock");
        if (Input.Input.KeyPressed(KeyCodes.D2))
            weaponSystem?.SwitchWeapon("ak");
        if (Input.Input.KeyPressed(KeyCodes.D0))
            weaponSystem?.Unequip();

        // Weapon shooting / reloading when Weapon context is active
        if (InputManager.CurrentContext == InputContext.Weapon)
        {
            if (weaponSystem?.CurrentWeapon?.IsAutomatic == true)
            {
                if (Input.Input.LeftMouseDown())
                    weaponSystem?.TryShoot();
            }
            else
            {
                if (Input.Input.LeftMousePressed())
                    weaponSystem?.TryShoot();
            }

            if (Input.Input.KeyPressed(KeyCodes.R))
                weaponSystem?.Reload();
        }
    }
}
