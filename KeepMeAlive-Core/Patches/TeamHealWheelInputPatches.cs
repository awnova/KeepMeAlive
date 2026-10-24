//====================[ Imports ]====================
using System;
using System.Reflection;
using EFT;
using EFT.InputSystem;
using HarmonyLib;
using SPT.Reflection.Patching;

namespace KeepMeAlive.Patches
{
    //====================[ Team-Heal Wheel Input ]====================
    // The team-heal wheel's input is the local player's own dispatch (see TeamHealWheel for the timing): each command and
    // the frame's axes go to the wheel first while it is open, in the same call the game makes.
    internal class TeamHealWheelCommandPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(EftGamePlayerOwner), nameof(EftGamePlayerOwner.TranslateCommand));

        // A command the wheel used is removed with Block: BlockAll would clear the rest of the frame's command list for
        // every node (InputNode.TranslateInput), dropping keys pressed in the same frame.
        [PatchPrefix]
        private static bool Prefix(EftGamePlayerOwner __instance, ECommand command, ref InputNode.ETranslateResult __result)
        {
            try
            {
                if (!TeamHealWheel.IsOpen || __instance?.Player == null || !__instance.Player.IsYourPlayer) return true;
                if (!TeamHealWheel.HandleCommand(command)) return true;

                __result = InputNode.ETranslateResult.Block;
                return false;
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"[TeamHealWheelCommand] error: {ex.Message}");
                return true;
            }
        }
    }

    // Movement keys may close the wheel; the mouse's turn moves its pointer and turn and look are zeroed before the
    // player's translators read them (MoveInputTranslator: Look from LookX/Y, Rotate from TurnX/Y).
    internal class TeamHealWheelLookPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(GamePlayerOwner), nameof(GamePlayerOwner.TranslateAxes));

        [PatchPrefix]
        private static void Prefix(GamePlayerOwner __instance, ref float[] axes)
        {
            try
            {
                if (!TeamHealWheel.IsOpen || __instance?.Player == null || !__instance.Player.IsYourPlayer) return;
                TeamHealWheel.HandleAxes(axes);
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"[TeamHealWheelLook] error: {ex.Message}");
            }
        }
    }
}
