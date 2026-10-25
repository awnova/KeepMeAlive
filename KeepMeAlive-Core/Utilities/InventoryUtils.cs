using Diz.LanguageExtensions;
using EFT;
using EFT.InventoryLogic;
using System;
using System.Collections;
using UnityEngine;

namespace KeepMeAlive.Utilities
{
    internal static class InventoryUtils
    {
        public static bool HasTemplate(Item item, string templateId)
        {
            if (item == null || string.IsNullOrEmpty(templateId))
            {
                return false;
            }

            string tpl = item.StringTemplateId ?? (string)item.TemplateId;
            return string.Equals(tpl, templateId, StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsDefib(Item item) => HasTemplate(item, DefibIds.TemplateId);

        public static bool IsStim(Item item) => HasTemplate(item, StimIds.TemplateId);

        public static Item FindItemById(Player player, string itemId)
        {
            if (string.IsNullOrEmpty(itemId))
            {
                return null;
            }

            try
            {
                var items = player?.Inventory?.AllRealPlayerItems;
                if (items == null)
                {
                    return null;
                }

                foreach (var item in items)
                {
                    if (item != null && item.Id == itemId)
                    {
                        return item;
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"FindItemById error: {ex.Message}");
            }

            return null;
        }

        internal static Item FindDefib(Player player)
        {
            try
            {
                var items = player?.Inventory?.AllRealPlayerItems;
                if (items == null)
                {
                    return null;
                }

                const string templateId = DefibIds.TemplateId;

                // The one with the fewest uses left is used, so partly used ones are finished before a fresh one is
                // started. A defib is removed on its last use, so none has less than 1.
                Item best = null;
                float bestUses = float.MaxValue;
                foreach (var it in items)
                {
                    if (!IsDefib(it))
                    {
                        continue;
                    }

                    float uses = UsesLeft(it);
                    if (uses < bestUses)
                    {
                        best = it;
                        bestUses = uses;
                    }
                }

                if (best != null)
                {
                    return best;
                }

                Plugin.LogSource.LogDebug($"FindDefib: no item matching {templateId} found for {player?.ProfileId}");
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"FindDefib error: {ex.Message}");
            }

            return null;
        }

        public static bool HasDefib(Player player) => FindDefib(player) != null;

        // Remaining uses from the item's resource counter (the server sets MaxResource); 0 when spent. Without a counter
        // (server not configured for uses) the defib counts as a single use.
        private static float UsesLeft(Item defib)
        {
            var resource = defib.GetItemComponent<ResourceComponent>();
            return resource != null && resource.MaxResource > 0f ? Mathf.Floor(resource.Value) : 1f;
        }

        // Spends one use of the local player's defib; the last use removes the item as a network transaction
        // on their controller. The caller waits for the inventory to be free first (WaitForInventoryFree), so it can
        // recheck the revive right before the use is spent. onDone reports whether a use was actually spent.
        public static IEnumerator ConsumeDefibCoroutine(Player player, string label, Action<bool> onDone)
        {
            if (!PlayerUtils.IsLocalPlayer(player))
            {
                onDone?.Invoke(false);
                yield break;
            }

            var controller = player.InventoryController;
            var item = FindDefib(player);
            if (controller == null || item == null)
            {
                Plugin.LogSource.LogWarning($"[{label}] No defib left to consume for {player.ProfileId}");
                onDone?.Invoke(false);
                yield break;
            }

            // More than one use left: lower the counter, redraw the cell and tell the other peers (the counter is not an
            // inventory operation, so Fika does not carry it). The profile saves the value at raid end.
            var resource = item.GetItemComponent<ResourceComponent>();
            if (resource != null && resource.MaxResource > 0f && UsesLeft(item) > 1f)
            {
                resource.Value = UsesLeft(item) - 1f;
                item.RaiseRefreshEvent();
                FikaBridge.SendDefibUsesPacket(player.ProfileId, item.Id, resource.Value);
                onDone?.Invoke(true);
                yield break;
            }

            yield return RemoveOneCoroutine(controller, item, label, onDone);
        }

        // The reviver's stim: the first item carrying the stim template (Adrenaline unless the server names another).
        internal static Item FindStim(Player player)
        {
            try
            {
                var items = player?.Inventory?.AllRealPlayerItems;
                if (items == null)
                {
                    return null;
                }

                string templateId = StimIds.TemplateId;
                foreach (var it in items)
                {
                    if (IsStim(it))
                    {
                        return it;
                    }
                }

                Plugin.LogSource.LogDebug($"FindStim: no item matching {templateId} found for {player?.ProfileId}");
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"FindStim error: {ex.Message}");
            }

            return null;
        }

        public static bool HasStim(Player player) => FindStim(player) != null;

        // Deletes one stim from the local player's inventory as a network transaction on their controller. The caller
        // waits for the inventory to be free first, as for the defib. onDone reports whether it was removed.
        public static IEnumerator ConsumeStimCoroutine(Player player, string label, Action<bool> onDone)
        {
            if (!PlayerUtils.IsLocalPlayer(player))
            {
                onDone?.Invoke(false);
                yield break;
            }

            var controller = player.InventoryController;
            var item = FindStim(player);
            if (controller == null || item == null)
            {
                Plugin.LogSource.LogWarning($"[{label}] No stim left to consume for {player.ProfileId}");
                onDone?.Invoke(false);
                yield break;
            }

            yield return RemoveOneCoroutine(controller, item, label, onDone);
        }

        // Removes the item, or one of its stack, through the owner's controller, which Fika replicates and the host
        // validates. onDone reports whether it was removed.
        private static IEnumerator RemoveOneCoroutine(InventoryController controller, Item item, string label, Action<bool> onDone)
        {
            OperationResult operation = item.StackObjectsCount > 1
                ? (OperationResult)ItemManipulator.SplitToNowhere(item, 1, controller, controller, simulate: true)
                : (OperationResult)ItemManipulator.Remove(item, controller, simulate: true);

            if (operation.Failed)
            {
                Plugin.LogSource.LogWarning($"[{label}] Could not build remove operation for {item.Id}: {operation.Error}");
                onDone?.Invoke(false);
                yield break;
            }

            var task = controller.TryRunNetworkTransaction(operation);
            while (!task.IsCompleted) yield return null;

            bool failed = task.IsFaulted || task.Result == null || task.Result.Failed;
            if (failed)
            {
                Plugin.LogSource.LogWarning($"[{label}] Removal failed for {item.Id}: {(task.IsFaulted ? task.Exception?.GetBaseException().Message : task.Result?.Error)}");
            }
            onDone?.Invoke(!failed);
        }

        // True when the player's inventory controller has an operation in flight, such as an
        // item apply/use or pending equip.
        private static bool HasAnyActiveInventoryEvent(Player player)
        {
            var events = (player?.InventoryController as ItemController)?.ActiveEvents;
            if (events == null) return false;
            foreach (var activeEvent in events)
            {
                if (activeEvent != null) return true;
            }
            return false;
        }

        // True while the item is being used (e.g. a med currently applied by someone).
        public static bool IsItemInUse(Player owner, Item item)
        {
            var events = (owner?.InventoryController as ItemController)?.ActiveEvents;
            if (events == null || item == null) return false;
            foreach (var activeEvent in events)
            {
                if (activeEvent?.Item == item) return true;
            }
            return false;
        }

        // Yields until the player has no active inventory event or timeoutSeconds elapses.
        // Proceeds after the timeout.
        public static IEnumerator WaitForInventoryFree(Player player, float timeoutSeconds, string label)
        {
            float elapsed = 0f;
            while (player != null && HasAnyActiveInventoryEvent(player) && elapsed < timeoutSeconds)
            {
                elapsed += Time.deltaTime;
                yield return null;
            }

            if (player != null && elapsed >= timeoutSeconds && HasAnyActiveInventoryEvent(player))
            {
                Plugin.LogSource.LogWarning($"[{label}] WaitForInventoryFree timed out after {timeoutSeconds}s; proceeding anyway.");
            }
        }

        public static bool IsMedOrFoodDepleted(Item item)
        {
            if (item == null) return true;

            var kit = item.GetItemComponent<MedKitComponent>();
            if (kit != null && kit.HpResource < float.Epsilon)
            {
                return true;
            }

            var food = item.GetItemComponent<FoodDrinkComponent>();
            return food != null && food.HpPercent < float.Epsilon;
        }

        // Returns true if the item exists, belongs to the healer, and has resource remaining.
        public static bool IsTeamHealItemValid(Player healer, Item item, out string reason)
        {
            reason = null;
            if (item == null || item.Parent == null || healer?.InventoryController == null)
            {
                reason = PlayerFacingMessages.TeamHeal.ItemUnavailable;
                return false;
            }

            if (!ReferenceEquals(item.Parent.GetOwner(), healer.InventoryController))
            {
                reason = PlayerFacingMessages.TeamHeal.ItemUnavailable;
                return false;
            }

            if (IsMedOrFoodDepleted(item))
            {
                reason = PlayerFacingMessages.TeamHeal.ItemUnavailable;
                return false;
            }

            return true;
        }
    }
}

