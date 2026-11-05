//====================[ Imports ]====================
using System;
using System.Reflection;
using EFT;
using HarmonyLib;
using SPT.Reflection.Patching;

namespace KeepMeAlive.Patches
{
    //====================[ RevivalTickPatch ]====================
    // Advances post-revival timers and runs the local downed-state machine
    // from the per-player Player.UpdateTick callback.
    internal class RevivalTickPatch : ModulePatch
    {
        //====================[ Patching / Lifecycle ]====================
        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(Player), nameof(Player.UpdateTick));

        [PatchPostfix]
        private static void PatchPostfix(Player __instance)
        {
            if (!PlayerUtils.IsLocalHuman(__instance)) return;

            try
            {
                if (RMSession.TryGetPlayerState(__instance.ProfileId, out var st))
                {
                    PostRevivalController.TickInvulnerability(__instance, st);
                    PostRevivalController.TickCooldown(__instance, st);
                }

                // Update the local body ragdoll / downed pose.
                BodyRagdoll.Sync(__instance, DownedRagdollController.WantsRagdoll(st));
                DownedVoiceController.Tick(__instance);
                // Takes a ghosted player off the bots again.
                GhostMode.Tick(__instance);

                DownedStateController.TickDowned(__instance);

                // Re-read: TickDowned and the timers above may have changed the state this frame.
                RMSession.TryGetPlayerState(__instance.ProfileId, out var current);
                DownedMovementController.TickSpeedLimit(__instance, current);
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"Error in RevivalTickPatch: {ex.Message}");
            }
        }
    }
}
