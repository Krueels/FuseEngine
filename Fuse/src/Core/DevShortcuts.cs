using Fuse.Core;
using Fuse.Input;
using Fuse.Player;
using Fuse.Enemy;
using Fuse.Scene;
using Fuse.Renderer;
using Fuse.Physics;
using Fuse.Audio;
using Fuse.AssetManagement;
using System.Security.Cryptography;

namespace Fuse.Core;

public static class DevShortcuts
{
    private static bool _enableAI = true;

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

        // F3: make every spider point its movement direction toward the player.
        if (Input.Input.KeyPressed(KeyCodes.F3))
        {
            SpiderPatrol.SetPursuitEnabled(!SpiderPatrol.PursuitEnabled);
            Logger.Info($"[DevShortcuts] Spider pursuit {(SpiderPatrol.PursuitEnabled ? "ON" : "OFF")}");
            GameNotify.Info($"Spider pursuit {(SpiderPatrol.PursuitEnabled ? "ON" : "OFF")}");
        }

        if (Input.Input.KeyPressed(KeyCodes.F4))
            renderer.ReloadPostProcessShader();

        if (Input.Input.KeyPressed(KeyCodes.F6))
        {

            _enableAI = !_enableAI;
            EnemyPatrol.Enabled = _enableAI;
            SpiderPatrol.Enabled = _enableAI;
            Logger.Info($"[DevShortcuts] Patrol {(_enableAI ? "ON" : "OFF")}");
            GameNotify.Info($"PatrolAI {(_enableAI ? "ON" : "OFF")}");
        }
    
        // Map reload
        if (Input.Input.KeyPressed(KeyCodes.F5))
            requestMapReload();

        // Debug drawer toggle
        if (Input.Input.KeyPressed(KeyCodes.F9))
            debugDrawer.Toggle();

        // Post-processing toggle
        if (Input.Input.KeyPressed(KeyCodes.F10))
        {
            renderer.PostPipeline.Settings.Enabled = !renderer.PostPipeline.Settings.Enabled;
            GameNotify.Info($"PostProcessing {(renderer.PostPipeline.Settings.Enabled ? "ON" : "OFF")}");
        }

        // Shadow toggle
        if (Input.Input.KeyPressed(KeyCodes.F12))
        {
            renderer.ShadowsEnabled = !renderer.ShadowsEnabled;
            GameNotify.Info($"Shadow {(renderer.ShadowsEnabled ? "ON" : "OFF")}");
        }

        if (InputManager.CurrentContext == InputContext.Gameplay || InputManager.CurrentContext == InputContext.Weapon)
        {

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

            // V: Spawn spider at raycast hit
            if (Input.Input.KeyPressed(KeyCodes.V))
            {
                if (sceneManager.Raycast(player.Camera.Position, player.Camera.Front, 20f, out var hit))
                    enemySystem?.SpawnSpider(hit.Position, 50f, hit.Normal);
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

        }

        if (Input.Input.KeyPressed(KeyCodes.Z))
        {
            player.TakeDamage(30f);
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
