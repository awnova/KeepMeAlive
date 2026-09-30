//====================[ Imports ]====================
using System;
using System.Reflection;
using Comfort.Common;
using EFT;
using EFT.Communications;
using EFT.InventoryLogic;
using EFT.UI;
using HarmonyLib;
using KeepMeAlive.Components;
using KeepMeAlive.Features;
using KeepMeAlive.Helpers;
using SPT.Reflection.Patching;

namespace KeepMeAlive.Patches
{
    //====================[ SpecialSlotReviveItemPatch ]====================
    internal class SpecialSlotReviveItemPatch : ModulePatch
    {
        //====================[ Patching ]====================
        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(Slot), nameof(Slot.CheckCompatibility));

        [PatchPostfix]
        private static void Postfix(Slot __instance, Item item, ref bool __result)
        {
            if (__result || item == null || !__instance.IsSpecial) return;

            var revivalTpl = SyncedServerConfigStore.Config.RevivalItem.TemplateId;
            var itemTpl = item.StringTemplateId ?? (string)item.TemplateId;

            if (!string.IsNullOrEmpty(revivalTpl) && string.Equals(itemTpl, revivalTpl, StringComparison.OrdinalIgnoreCase))
            {
                __result = true;
            }
        }
    }

    //====================[ DownedPlayerLootPatch ]====================
    // Allows the viewer's search controller to access a downed (BleedingOut) player's inventory.
    internal class DownedPlayerLootPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(SearchController), nameof(SearchController.HiddenInsideAnotherAlivePlayer));

        [PatchPostfix]
        private static void PatchPostfix(ref bool __result, ItemAddress address)
        {
            if (!__result) return;

            var owner = address.GetOwnerOrNull();
            if (owner is Player.PlayerInventoryController pic
                && RMSession.IsPlayerCritical(pic.Player.ProfileId))
            {
                __result = false;
            }
        }
    }

    //====================[ DownedLootGuardPatch ]====================
    // Every UI inventory action (drag/drop, quick-move, swap, split, discard) funnels through
    // ItemController.RunNetworkTransaction on the acting player's own controller.
    internal class DownedLootGuardPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(ItemController), nameof(ItemController.RunNetworkTransaction),
                new[] { typeof(IOperationResult), typeof(Callback) });

        [PatchPrefix]
        private static bool Prefix(ItemController __instance, IOperationResult operationResult, Callback callback)
        {
            try
            {
                if (!DownedLootGuard.IsBlocked(__instance, operationResult)) return true;

                callback?.Fail("Can't take equipped gear from a downed teammate");
                NotificationManager.DisplayWarningNotification(PlayerFacingMessages.Interaction.LootProtectedItem);
                return false;
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"[DownedLootGuard] error: {ex.Message}");
                return true;
            }
        }
    }
}
