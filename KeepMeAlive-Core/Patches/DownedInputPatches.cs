//====================[ Imports ]====================
using System;
using System.Reflection;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using EFT.UI;
using HarmonyLib;
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

    //====================[ BodyProxyFindInteractablePatch ]====================
    // Replaces FindInteractable to skip proxied player capsules and resolve body hits to their BodyInteractable.
    internal class BodyProxyFindInteractablePatch : ModulePatch
    {
        private static readonly UnityEngine.RaycastHit[] Hits = new UnityEngine.RaycastHit[32];
        private const float OwnRayOriginRadiusSq = 0.6f * 0.6f;
        private static int _playerLayer = -1;

        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(GameWorld), nameof(GameWorld.FindInteractable));

        [PatchPrefix]
        private static bool Prefix(UnityEngine.Ray ray, ref UnityEngine.RaycastHit hit, ref UnityEngine.GameObject __result)
        {
            if (!BodyInteractableRuntime.HasAny) return true;
            if (_playerLayer < 0) _playerLayer = UnityEngine.LayerMask.NameToLayer("Player");

            var settings = EFTHardSettings.Instance;
            float maxDistance = UnityEngine.Mathf.Max(settings.LOOT_RAYCAST_DISTANCE, settings.PLAYER_RAYCAST_DISTANCE + settings.BEHIND_CAST);

            int count = UnityEngine.Physics.RaycastNonAlloc(ray, Hits, maxDistance, GameWorld.InteractiveLootMaskWPlayer);

            // Nearest hit that isn't a proxied player's capsule or the caster's own proxy.
            int best = -1;
            for (int i = 0; i < count; i++)
            {
                if (best >= 0 && Hits[i].distance >= Hits[best].distance) continue;
                var collider = Hits[i].collider;
                if (IsProxiedPlayerCapsule(collider) || IsOwnProxy(collider, ray.origin)) continue;
                // Non-player interactables (loot, doors, containers) must respect vanilla LOOT_RAYCAST_DISTANCE.
                if (Hits[i].distance > settings.LOOT_RAYCAST_DISTANCE
                    && collider.GetComponent<BodyInteractable.BodyInteractableProxy>() == null) continue;
                best = i;
            }

            __result = null;
            hit = default;
            if (best < 0) return false;

            hit = Hits[best];
            var go = hit.collider.gameObject;
            if (UnityEngine.Physics.Linecast(ray.origin, hit.point, GameWorld.LootMaskObstruction)) return false;

            var proxy = go.GetComponent<BodyInteractable.BodyInteractableProxy>();
            __result = proxy != null && proxy.Owner != null ? proxy.Owner.gameObject : go;
            return false;
        }

        private static bool IsProxiedPlayerCapsule(UnityEngine.Collider collider)
        {
            if (collider.gameObject.layer != _playerLayer) return false;
            var player = collider.GetComponentInParent<Player>();
            return player != null && BodyInteractableRuntime.Has(player.ProfileId);
        }

        private static bool IsOwnProxy(UnityEngine.Collider collider, UnityEngine.Vector3 rayOrigin)
        {
            var proxy = collider.GetComponent<BodyInteractable.BodyInteractableProxy>();
            var eyes = proxy?.Owner?.Revivee?.PlayerBones?.LootRaycastOrigin;
            return eyes != null && (eyes.position - rayOrigin).sqrMagnitude < OwnRayOriginRadiusSq;
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
                if (weapon == null || !PlayerUtils.IsHuman(__instance)) return true;
                if (!RMSession.TryGetPlayerState(__instance.ProfileId, out var st)) return true;

                bool shouldBlock = st.IsCritical && !st.AllowWeaponEquipForReviveAnim;
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
    // While the inventory is held open programmatically, consume gameplay commands. While downed,
    // also consume the inventory key: the game opens the screen before it checks ignoredCommands.
    internal class SilentInventoryCommandBlockPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(EftGamePlayerOwner), nameof(EftGamePlayerOwner.TranslateCommand));

        [PatchPrefix]
        private static bool Prefix(EftGamePlayerOwner __instance, EFT.InputSystem.ECommand command, ref EFT.InputSystem.InputNode.ETranslateResult __result)
        {
            try
            {
                if (!PlayerUtils.TryGetLocalOwnerPlayer(__instance, out var player)) return true;
                if (!RMSession.TryGetPlayerState(player.ProfileId, out var st)) return true;

                bool blockUi = SyncedServerConfigStore.Config.Gameplay.Downed.BlockUiWhenDowned;
                bool block = st.IsSilentInventoryAnimActive
                             || (st.IsCritical && blockUi && command == EFT.InputSystem.ECommand.ToggleInventory)
                             // The spectate keys (fire, aim, N) are also the game's own.
                             || (DownedSpectate.IsActive && DownedSpectate.BlocksCommand(command));
                if (!block) return true;

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

    //====================[ DraggerReviveInputPatch ]====================
    // The interact key pressed while holding a dragged body starts its revive (the body has no context menu). The press is
    // consumed; the release is not, so the game's own cancel ends the hold.
    internal class DraggerReviveInputPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(EftGamePlayerOwner), nameof(EftGamePlayerOwner.TranslateCommand));

        [PatchPrefix]
        private static bool Prefix(EftGamePlayerOwner __instance, EFT.InputSystem.ECommand command, ref EFT.InputSystem.InputNode.ETranslateResult __result)
        {
            try
            {
                if (command != EFT.InputSystem.ECommand.BeginInteracting || !PlayerUtils.TryGetLocalOwnerPlayer(__instance, out _)) return true;
                if (!DownedDragController.TryBeginReviveHold(__instance)) return true;

                __result = EFT.InputSystem.InputNode.ETranslateResult.BlockAll;
                return false;
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"[DraggerReviveInput] error: {ex.Message}");
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
