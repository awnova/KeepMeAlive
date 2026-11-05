//====================[ Imports ]====================
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Logging;
using EFT.HealthSystem;
using System;
using System.Reflection;
using UnityEngine;

namespace KeepMeAlive
{
    //====================[ Plugin ]====================
    // Soft dependency so SAIN (if installed) is already in Chainloader.PluginInfos when Awake runs.
    [BepInDependency("me.sol.sain", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInPlugin("com.awnova.keepmealive", "KeepMeAlive", "1.1.0")]
    public class Plugin : BaseUnityPlugin
    {
        //====================[ Shared State ]====================
        public static ManualLogSource LogSource;
        public static MonoBehaviour StaticCoroutineRunner;

        public static bool SAINInstalled { get; private set; }

        //====================[ Unity Lifecycle ]====================
        private void Awake()
        {
            SAINInstalled = Chainloader.PluginInfos.ContainsKey("me.sol.sain");

            LogSource = Logger;
            StaticCoroutineRunner = this;

            LogAssemblyInfo();
            LogSource.LogInfo("KeepMeAlive plugin loaded!");

            KeepMeAliveSettings.Init(Config);
            SyncedServerConfigStore.Load();

            // Register ReviveCooldown effect interface into EFT's effect name registry.
            // Must run before any health controllers are instantiated.
            RegisterReviveCooldownEffectTypes();

            EnableCorePatches();
            EnableGhostModePatches();
        }

        //====================[ Patch Registration ]====================
        private static void EnableCorePatches()
        {
            new RevivalTickPatch().Enable();
            new DeathPatch().Enable();
            new DownedVitalsApplyDamagePatch().Enable();
            new RagdollDeathHandoffPatch().Enable();
            new GameWorldOnGameStartedPatch().Enable();
            new RaidLocalStopSafetyPatch().Enable();
            new GameWorldDisposeSafetyPatch().Enable();
            new DownedWeaponProceedBlockPatch().Enable();
            new DownedTryProceedBlockPatch().Enable();
            new DownedInventoryScreenBlockPatch().Enable();
            new DownedApplyItemBlockPatch().Enable();
            new DownedQuickSlotSelectorBlockPatch().Enable();
            new SilentInventoryCommandBlockPatch().Enable();
            new TinnitusCapPatch().Enable();
            new DownedBreathPatch().Enable();
            new DownedVoiceTracePatch().Enable();
            new DownedDeathLinePatch().Enable();
            new DownedBloodApplyVignettePatch().Enable();
            new DownedBloodRenderPatch().Enable();
            new DownedVignetteFlickerPatch().Enable();
        }

        private static void EnableGhostModePatches()
        {
            new GhostModeGroupPatch().Enable();
            new GhostModeMemoryPatch().Enable();
            LogSource.LogInfo("GhostMode patches enabled (BotsGroup.AddEnemy + BotMemory.AddEnemy).");

            if (SAINInstalled)
            {
                new GhostModeSAINPatch().Enable();
                LogSource.LogInfo("GhostMode SAIN patch enabled (EnemyListController.tryAddEnemy).");
            }
        }

        //====================[ Diagnostics ]====================
        private static void LogAssemblyInfo()
        {
            try
            {
                var asm = Assembly.GetExecutingAssembly();
                LogSource.LogInfo($"Revival assembly: {asm.GetName().Name} v{asm.GetName().Version}");
            }
            catch (Exception ex)
            {
                LogSource.LogWarning($"Could not retrieve assembly info: {ex.Message}");
            }
        }

        //====================[ ReviveCooldown Type Registration ]====================
        // Registers ReviveCooldown effect interface into EFT's effect name dictionary
        // so the HUD tooltip resolves properly.
        private static void RegisterReviveCooldownEffectTypes()
        {
            try
            {
                LogSource.LogInfo("[Plugin] Registering ReviveCooldown effect type...");
                // A placeholder: raid start sets the player-facing name (PostReviveEffects.RefreshCooldownEffectName),
                // since the game's language is not loaded yet.
                HealthHelper._effectNames[typeof(IReviveCooldown)] = "ReviveCooldown";

                LogSource.LogInfo(
                    $"[Plugin] ReviveCooldown registered - interface: {HealthHelper._effectNames.ContainsKey(typeof(IReviveCooldown))}");
            }
            catch (Exception ex)
            {
                LogSource.LogError($"[Plugin] ReviveCooldown type registration failed: {ex}");
            }
        }
    }
}
