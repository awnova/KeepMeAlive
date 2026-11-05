//====================[ Imports ]====================
using System;
using System.Collections.Generic;
using System.Reflection;
using Comfort.Common;
using EFT;
using EFT.InputSystem;
using EFT.InventoryLogic;
using EFT.UI;
using HarmonyLib;
using SPT.Reflection.Patching;

namespace KeepMeAlive.Patches
{
    //====================[ DownedWeaponProceedBlockPatch ]====================
    // Blocks Player.Proceed(Weapon, ...) while downed except when RevivePresentation draws a weapon.
    internal class DownedWeaponProceedBlockPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(Player), nameof(Player.Proceed), new[]
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
                if (weapon == null || !PlayerUtils.IsHuman(__instance)) return true;
                if (!RMSession.TryGetPlayerState(__instance.ProfileId, out var st)) return true;

                bool shouldBlock = st.IsCritical && !st.AllowWeaponEquipForReviveAnim;
                if (!shouldBlock) return true;

                RevivalDebugLog.LogDebug($"[DownedWeaponBlock] Blocked weapon proceed for {__instance.ProfileId} in state={st.State}");
                // Complete the callback for the blocked hands operation.
                callback?.Invoke(new Result<IFirearmHandsController>(null, "Weapon equip blocked while downed"));
                DownedMovementController.RefuseHandsChange(__instance, null);
                return false;
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"[DownedWeaponBlock] Player prefix error: {ex.Message}");
                return true;
            }
        }
    }

    //====================[ DownedTryProceedBlockPatch ]====================
    // TryProceed is where every item-to-hands request lands (TrySetLastEquippedWeapon, SetFirstAvailableItem,
    // SetInHands(Item), quick slots) before it splits into the per-type Proceed overloads; blocking the weapon one
    // alone let knives, grenades and usable items through. Left-hand items don't change the hands controller.
    internal class DownedTryProceedBlockPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(Player), nameof(Player.TryProceed), new[]
            {
                typeof(Item),
                typeof(Callback<IHandsController>),
                typeof(bool)
            });

        [PatchPrefix]
        private static bool Prefix(Player __instance, Item item, Callback<IHandsController> completeCallback)
        {
            try
            {
                if (!__instance.IsYourPlayer || item == null || item.LeftHandItem) return true;
                if (!RMSession.TryGetPlayerState(__instance.ProfileId, out var st)) return true;
                if (!st.IsCritical || st.AllowWeaponEquipForReviveAnim) return true;

                RevivalDebugLog.LogDebug($"[DownedHandsBlock] Refused {item.GetType().Name} to hands for {__instance.ProfileId} in state={st.State}");
                DownedMovementController.RefuseHandsChange(__instance, completeCallback);
                return false;
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"[DownedHandsBlock] TryProceed prefix error: {ex.Message}");
                return true;
            }
        }
    }

    //====================[ SilentInventoryCommandBlockPatch ]====================
    // While the inventory is held open programmatically, consume gameplay commands. While downed,
    // also consume the inventory key: the game opens the screen before it checks ignoredCommands.
    internal class SilentInventoryCommandBlockPatch : ModulePatch
    {
        // The end half of EFT's toggle key combinations (InputManager.Init), plus the other end commands: what a key or
        // button sends when it is let go. They pass, so nothing held when the player goes down stays stuck.
        private static readonly HashSet<ECommand> Releases = new HashSet<ECommand>
        {
            ECommand.EndShooting,
            ECommand.EndAlternativeShooting,
            ECommand.FinishHighThrow,
            ECommand.FinishLowThrow,
            ECommand.EndInteracting,
            ECommand.EndSpecialInteracting,
            ECommand.EndSprinting,
            ECommand.EndBreathing,
            ECommand.EndWalk,
            ECommand.VaultingEnd,
            ECommand.RestorePose,
            ECommand.EndLeanLeft,
            ECommand.EndLeanRight,
            ECommand.BlindShootEnd,
            ECommand.EndAnimBlindFireAbove,
            ECommand.EndAnimBlindFireRight,
            ECommand.ReturnFromLeftStep,
            ECommand.ReturnFromRightStep,
            ECommand.MumbleEnd,
            ECommand.MumbleDropdownHide,
            ECommand.StopTalk,
            ECommand.ExitPinLockMode
        };

        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(EftGamePlayerOwner), nameof(EftGamePlayerOwner.TranslateCommand));

        [PatchPrefix]
        private static bool Prefix(EftGamePlayerOwner __instance, ECommand command, ref InputNode.ETranslateResult __result)
        {
            try
            {
                if (!PlayerUtils.TryGetLocalOwnerPlayer(__instance, out var player)) return true;
                if (!RMSession.TryGetPlayerState(player.ProfileId, out var st)) return true;
                // Key releases pass, so a key held at the downing is let go.
                if (Releases.Contains(command)) return true;

                bool blockUi = SyncedServerConfigStore.Config.Gameplay.Downed.BlockUiWhenDowned;
                bool block = st.IsSilentInventoryAnimActive
                             || (st.IsCritical && blockUi && command == ECommand.ToggleInventory);
                if (!block) return true;

                __result = InputNode.ETranslateResult.BlockAll;
                return false;
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"[SilentInvCommandBlock] error: {ex.Message}");
                return true;
            }
        }
    }

    //====================[ DownedApplyItemBlockPatch ]====================
    // Blocks item-based healing for a downed local player, including inventory and quick-slot use.
    internal class DownedApplyItemBlockPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(EFT.HealthSystem.PlayerHealthController), nameof(EFT.HealthSystem.PlayerHealthController.ApplyItem),
                new[] { typeof(Item), typeof(EFT.NetworkPackets.OneAndList<EBodyPart>), typeof(float?) });

        [PatchPrefix]
        private static bool Prefix(EFT.HealthSystem.PlayerHealthController __instance, ref bool __result)
        {
            try
            {
                var player = __instance?.Player;
                if (!PlayerUtils.IsLocalHuman(player) || !RMSession.IsPlayerCritical(player.ProfileId)) return true;

                RevivalDebugLog.LogDebug($"[DownedApplyItemBlock] Blocked item use for {player.ProfileId}");
                __result = false;
                return false;
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"[DownedApplyItemBlock] error: {ex.Message}");
                return true;
            }
        }
    }

    //====================[ DownedQuickSlotSelectorBlockPatch ]====================
    // Quick-slot views use their own input nodes, so ignoredCommands does not block their selectors.
    internal class DownedQuickSlotSelectorBlockPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(InventoryScreenQuickAccessPanel), nameof(InventoryScreenQuickAccessPanel.TryShowSelectors));

        [PatchPrefix]
        private static bool Prefix(ref bool __result)
        {
            try
            {
                var local = PlayerUtils.GetYourPlayer();
                if (local == null || !RMSession.IsPlayerCritical(local.ProfileId)) return true;

                __result = false;
                return false;
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"[DownedQuickSlotSelectorBlock] error: {ex.Message}");
                return true;
            }
        }
    }

    //====================[ DownedInventoryScreenBlockPatch ]====================
    // Block inventory screens while downed to prevent gear changes and item use.
    internal class DownedInventoryScreenBlockPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(EftGamePlayerOwner), nameof(EftGamePlayerOwner.ShowInventoryScreen));

        [PatchPrefix]
        private static bool Prefix(EftGamePlayerOwner __instance, Action exitAction)
        {
            try
            {
                if (!SyncedServerConfigStore.Config.Gameplay.Downed.BlockUiWhenDowned) return true;
                if (!PlayerUtils.TryGetLocalOwnerPlayer(__instance, out var player) || !RMSession.IsPlayerCritical(player.ProfileId)) return true;

                RevivalDebugLog.LogDebug($"[DownedInventoryBlock] Blocked inventory screen for {player.ProfileId}");
                // Same as the game's own refusal path: run the exit action so callers see the screen closed.
                exitAction?.Invoke();
                return false;
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"[DownedInventoryBlock] error: {ex.Message}");
                return true;
            }
        }
    }
}
