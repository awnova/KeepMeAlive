//====================[ Imports ]====================
using System;
using System.Reflection;
using HarmonyLib;
using EFT;
using EFT.HealthSystem;
using SPT.Reflection.Patching;

namespace KeepMeAlive.Patches
{
    //====================[ DeathPatch ]====================
    // ActiveHealthController only exists for the local human player.
    internal class DeathPatch : ModulePatch
    {
        //====================[ Patching ]====================
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(ActiveHealthController), nameof(ActiveHealthController.Kill));

        [PatchPrefix]
        private static bool Prefix(ActiveHealthController __instance, EDamageType damageType)
        {
            try
            {
                var player = __instance.Player;
                if (!PlayerUtils.IsLocalHuman(player)) return true;

                if (DeathMode.ShouldAllowDeathFromHeadshot(player, __instance, damageType)
                    || !DeathMode.ShouldBlockDeath(player, damageType))
                {
                    DeathMode.OnLocalDeathAllowed(player, damageType);
                    return true;
                }

                if (!RMSession.TryGetPlayerState(player.ProfileId, out var st) || st.State == RMState.None)
                {
                    DownedStateController.EnterDowned(player, damageType);
                }

                return false;
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"Error in Death prevention patch: {ex.Message}");
                return true;
            }
        }
    }

    //====================[ DownedVitalsApplyDamagePatch ]====================
    // Brackets the local player's ApplyDamage so vitals restored by the downing Kill() are
    // restored again after the same hit's overdamage spread.
    internal class DownedVitalsApplyDamagePatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(ActiveHealthController), nameof(ActiveHealthController.ApplyDamage));

        [PatchPrefix]
        private static void Prefix(ActiveHealthController __instance, out bool __state)
        {
            __state = PlayerUtils.IsLocalHuman(__instance.Player);
            if (__state) DownedHealthAndEffectsManager.BeginApplyDamage();
        }

        [PatchFinalizer]
        private static void Finalizer(ActiveHealthController __instance, bool __state)
        {
            if (!__state) return;
            try { DownedHealthAndEffectsManager.EndApplyDamage(__instance.Player); }
            catch (Exception ex) { Plugin.LogSource.LogError($"[DownedVitalsApplyDamage] error: {ex.Message}"); }
        }
    }

    //====================[ RagdollDeathHandoffPatch ]====================
    // Give a taken-over body back to EFT before the corpse is created, leaving its
    // bones in the pose on screen.
    internal class RagdollDeathHandoffPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(Player), nameof(Player.OnDead));

        [PatchPrefix]
        private static void Prefix(Player __instance)
        {
            try
            {
                if (!PlayerUtils.IsLocalHuman(__instance)) return;
                BodyRagdoll.ReleaseForDeath(__instance);
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"[RagdollDeathHandoff] error: {ex.Message}");
            }
        }
    }

    //====================[ Raid Lifecycle Patches ]====================
    // TarkovApplication.LocalGameCreate calls GameWorld.OnGameStarted immediately after localGame.Run completes.
    internal class GameWorldOnGameStartedPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(GameWorld), nameof(GameWorld.OnGameStarted));

        [PatchPostfix]
        private static void Postfix(GameWorld __instance) => RaidLifecycle.OnGameStarted(__instance);
    }

    // Every single-player raid exit (extract, menu leave, MIA timer, death) funnels into BaseLocalGame<EftGamePlayerOwner>.Stop.
    internal class RaidLocalStopSafetyPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(BaseLocalGame<EftGamePlayerOwner>), nameof(BaseLocalGame<EftGamePlayerOwner>.Stop));

        [PatchPrefix]
        private static void Prefix()
        {
            DownedStateController.HandleLocalPlayerExtracting();
            RaidLifecycle.OnGameEnded("local-game-stop");
        }
    }

    // Fallback guarantee on GameWorld teardown.
    internal class GameWorldDisposeSafetyPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(GameWorld), nameof(GameWorld.Dispose));

        [PatchPrefix]
        private static void Prefix() => RaidLifecycle.OnGameEnded("gameworld-dispose");
    }
}
