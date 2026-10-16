//====================[ Imports ]====================
using System.Reflection;
using EFT;
using Fika.Core.Main.Players;
using HarmonyLib;
using KeepMeAlive.Features;
using SPT.Reflection.Patching;

namespace KeepMeAlive.Patches
{
    //====================[ Player Pose Patches ]====================
    // Pose a player's bones ourselves (the dragger's arms, the defibrillator hands) straight after that player's own
    // late pass (animation IK, first-person shoulders, hands controller), so nothing of the game's runs after it and
    // rewrites them (BodyAnimation.AfterPlayerPose). Player.LateUpdate covers the local player; ObservedPlayer
    // overrides it without calling the base.
    internal class PlayerPosePatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(Player), nameof(Player.LateUpdate));

        [PatchPostfix]
        private static void Postfix(Player __instance)
        {
            BodyAnimation.AfterPlayerPose(__instance);
        }
    }

    internal class ObservedPlayerPosePatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(ObservedPlayer), nameof(ObservedPlayer.LateUpdate));

        [PatchPostfix]
        private static void Postfix(ObservedPlayer __instance)
        {
            BodyAnimation.AfterPlayerPose(__instance);
        }
    }
}
