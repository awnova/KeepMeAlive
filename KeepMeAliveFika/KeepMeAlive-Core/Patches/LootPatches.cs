//====================[ Imports ]====================
using System;
using System.Reflection;
using Comfort.Common;
using EFT;
using EFT.Communications;
using EFT.InputSystem;
using EFT.InventoryLogic;
using EFT.UI.Screens;
using HarmonyLib;
using SPT.Reflection.Patching;

namespace KeepMeAlive.Patches
{
    //====================[ SpecialSlotDefibPatch ]====================
    internal class SpecialSlotDefibPatch : ModulePatch
    {
        //====================[ Patching ]====================
        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(Slot), nameof(Slot.CheckCompatibility));

        [PatchPostfix]
        private static void Postfix(Slot __instance, Item item, ref bool __result)
        {
            if (__result || item == null || !__instance.IsSpecial) return;

            if (InventoryUtils.IsDefib(item))
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

            var owner = address?.GetOwnerOrNull();
            if (owner is Player.PlayerInventoryController pic
                && pic.Player != null
                && RMSession.IsPlayerCritical(pic.Player.ProfileId))
            {
                __result = false;
            }
        }
    }

    // The inventory key pressed while looking at a downed teammate opens their loot (with the looter's own inventory beside
    //====================[ DownedLootGuardPatch ]====================
    // it) instead of the looter's inventory alone. Runs before the game's own handling in GamePlayerOwner.TranslateCommand,
    // which opens the inventory from the battle screen only; BlockAll stops it opening a second screen.
    internal class DownedLootInventoryKeyPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(EftGamePlayerOwner), nameof(EftGamePlayerOwner.TranslateCommand));

        [PatchPrefix]
        private static bool Prefix(EftGamePlayerOwner __instance, ECommand command, ref InputNode.ETranslateResult __result)
        {
            try
            {
                if (command != ECommand.ToggleInventory || !PlayerUtils.TryGetLocalOwnerPlayer(__instance, out var player)) return true;
                if (!(player.InteractableObject is BodyInteractable body) || !body.CanLoot(player)) return true;
                if (!EftScreenManager.Instance.CheckCurrentScreen(EEftScreenType.BattleUI)) return true;

                body.OnLootDowned(__instance);
                __result = InputNode.ETranslateResult.BlockAll;
                return false;
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"[DownedLootInventoryKey] error: {ex.Message}");
                return true;
            }
        }
    }

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
