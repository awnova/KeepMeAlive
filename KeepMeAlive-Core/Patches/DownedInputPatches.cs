//====================[ Imports ]====================
using System;
using System.Reflection;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using EFT.UI;
using HarmonyLib;
using KeepMeAlive.Components;
using KeepMeAlive.Features;
using KeepMeAlive.Helpers;
using Fika.Core.Main.Players;
using SPT.Reflection.Patching;

namespace KeepMeAlive.Patches
{
    //====================[ AvailableActionsPatch ]====================
    internal class AvailableActionsPatch : ModulePatch
    {
        //====================[ Patching ]====================
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.FirstMethod(
                typeof(InteractionContextHelper),
                method => method.Name == nameof(InteractionContextHelper.GetAvailableActions) &&
                          method.GetParameters().Length == 2 &&
                          method.GetParameters()[0].Name == "owner" &&
                          method.GetParameters()[1].ParameterType == typeof(IInteractive));
        }

        [PatchPrefix]
        private static bool PatchPrefix(GamePlayerOwner owner, IInteractive interactive, ref AvailableInteractionState __result)
        {
            return !BodyInteractableRuntime.TryRouteActions(owner, interactive, ref __result);
        }
    }

    //====================[ DownedFikaWeaponProceedBlockPatch ]====================
    // FikaPlayer overrides Proceed(Weapon, ...) without calling base, and every human player
    // under Fika is a FikaPlayer (local) or ObservedPlayer (remote, left untouched), so this
    // is the only Proceed override that needs blocking.
    internal class DownedFikaWeaponProceedBlockPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(FikaPlayer), nameof(Player.Proceed), new[]
            {
                typeof(Weapon),
                typeof(Callback<IFirearmHandsController>),
                typeof(bool)
            });

        [PatchPrefix]
        private static bool Prefix(Player __instance, Weapon weapon, Callback<IFirearmHandsController> callback)
        {
            try
            {
                if (__instance == null || weapon == null || __instance.IsAI) return true;
                if (!RMSession.TryGetPlayerState(__instance.ProfileId, out var st)) return true;

                bool shouldBlock = (st.State is RMState.BleedingOut or RMState.Reviving) && !st.AllowWeaponEquipForReviveAnim;
                if (!shouldBlock) return true;

                RevivalDebugLog.LogDebug($"[DownedWeaponBlock] Blocked weapon proceed for {__instance.ProfileId} in state={st.State}");
                // Complete the callback for the blocked hands operation.
                callback?.Invoke(new Result<IFirearmHandsController>(null, "Weapon equip blocked while downed"));
                return false;
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"[DownedWeaponBlock] FikaPlayer prefix error: {ex.Message}");
                return true;
            }
        }
    }

    //====================[ SilentInventoryCommandBlockPatch ]====================
    // While the inventory is held open programmatically, consume gameplay commands.
    internal class SilentInventoryCommandBlockPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(EftGamePlayerOwner), nameof(EftGamePlayerOwner.TranslateCommand));

        [PatchPrefix]
        private static bool Prefix(EftGamePlayerOwner __instance, ref EFT.InputSystem.InputNode.ETranslateResult __result)
        {
            try
            {
                if (__instance?.Player == null) return true;
                if (!RMSession.TryGetPlayerState(__instance.Player.ProfileId, out var st)) return true;
                if (!st.IsSilentInventoryAnimActive) return true;

                __result = EFT.InputSystem.InputNode.ETranslateResult.BlockAll;
                return false;
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"[SilentInvCommandBlock] error: {ex.Message}");
                return true;
            }
        }
    }
}
