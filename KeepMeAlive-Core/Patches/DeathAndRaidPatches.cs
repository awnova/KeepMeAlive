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
    // ActiveHealthController only exists for the local human player (observed players use
    // ObservedHealthController), so this only ever runs on the owning client.
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
                if (player == null || player.IsAI || !player.IsYourPlayer) return true;

                if (DeathMode.ShouldAllowDeathFromHardcoreHeadshot(player, __instance, damageType)
                    || !DeathMode.ShouldBlockDeath(player, damageType))
                {
                    DeathMode.OnLocalDeathAllowed(player, damageType);
                    return true;
                }

                if (!RMSession.TryGetPlayerState(player.ProfileId, out var st) || st.State is RMState.None or RMState.CoolDown)
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
            var player = __instance.Player;
            __state = player != null && !player.IsAI && player.IsYourPlayer;
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
    // Give a taken-over body (the local player's own included) back to EFT before the corpse is created, leaving its
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
                if (__instance == null || __instance.IsAI) return;
                BodyRagdoll.ReleaseForDeath(__instance);
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"[RagdollDeathHandoff] error: {ex.Message}");
            }
        }
    }

    //====================[ Raid Termination Patches ]====================
    // Notify the downed-state controller when CoopGame extracts or stops.
    internal class RaidExtractSafetyPatch : ModulePatch
    {
        //====================[ Patching ]====================
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(global::Fika.Core.Main.GameMode.CoopGame), nameof(global::Fika.Core.Main.GameMode.CoopGame.Extract));

        [PatchPrefix]
        private static void Prefix() => DownedStateController.HandleRaidTerminating("coop-game-extract");
    }

    internal class RaidCoopStopSafetyPatch : ModulePatch
    {
        //====================[ Patching ]====================
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(global::Fika.Core.Main.GameMode.CoopGame), nameof(global::Fika.Core.Main.GameMode.CoopGame.Stop));

        [PatchPrefix]
        private static void Prefix() => DownedStateController.HandleRaidTerminating("coop-game-stop");
    }
}
