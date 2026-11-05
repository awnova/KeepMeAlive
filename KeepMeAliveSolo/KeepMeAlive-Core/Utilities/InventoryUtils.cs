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

        public static bool IsStim(Item item) => StimPriority(item) >= 0;

        // The item's place in the stim list, or -1 when it isn't a stim.
        private static int StimPriority(Item item)
        {
            var ids = StimIds.TemplateIds;
            for (int i = 0; i < ids.Count; i++)
            {
                if (HasTemplate(item, ids[i])) return i;
            }
            return -1;
        }

        // The local player's stim: the carried item whose template comes first in the stim list (Adrenaline unless the server
        // names others).
        internal static Item FindStim(Player player)
        {
            try
            {
                var items = player?.Inventory?.AllRealPlayerItems;
                if (items == null)
                {
                    return null;
                }

                Item best = null;
                int bestPriority = int.MaxValue;
                foreach (var it in items)
                {
                    int priority = StimPriority(it);
                    if (priority >= 0 && priority < bestPriority)
                    {
                        best = it;
                        bestPriority = priority;
                        if (priority == 0) break;
                    }
                }

                if (best != null)
                {
                    return best;
                }

                Plugin.LogSource.LogDebug($"FindStim: no item matching {string.Join(", ", StimIds.TemplateIds)} found for {player?.ProfileId}");
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"FindStim error: {ex.Message}");
            }

            return null;
        }

        public static bool HasStim(Player player) => FindStim(player) != null;

        // Deletes one stim from the local player's inventory as a transaction on their controller. The caller
        // waits for the inventory to be free first. onDone reports whether it was removed.
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

        // Removes the item, or one of its stack, through the owner's controller. onDone reports whether it was removed.
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
            float elapsed = 0f;
            while (!task.IsCompleted && elapsed < RemoveTimeoutSeconds)
            {
                elapsed += Time.deltaTime;
                yield return null;
            }

            // A removal with no answer would hang the revive start; past the timeout it counts as failed.
            if (!task.IsCompleted)
            {
                Plugin.LogSource.LogWarning($"[{label}] Removal of {item.Id} got no answer in {RemoveTimeoutSeconds}s; treating it as failed");
                onDone?.Invoke(false);
                yield break;
            }

            bool failed = task.IsFaulted || task.Result == null || task.Result.Failed;
            if (failed)
            {
                Plugin.LogSource.LogWarning($"[{label}] Removal failed for {item.Id}: {(task.IsFaulted ? task.Exception?.GetBaseException().Message : task.Result?.Error)}");
            }
            onDone?.Invoke(!failed);
        }

        private const float RemoveTimeoutSeconds = 5f;

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

        // Yields until the player has no active inventory event or timeoutSeconds elapses.
        // Proceeds after the timeout.
        // onDone(true) if the inventory came free, false on timeout.
        public static IEnumerator WaitForInventoryFree(Player player, float timeoutSeconds, string label, Action<bool> onDone = null)
        {
            float elapsed = 0f;
            while (player != null && HasAnyActiveInventoryEvent(player) && elapsed < timeoutSeconds)
            {
                elapsed += Time.deltaTime;
                yield return null;
            }

            bool free = player == null || !HasAnyActiveInventoryEvent(player);
            if (!free)
            {
                Plugin.LogSource.LogWarning($"[{label}] WaitForInventoryFree timed out after {timeoutSeconds}s ({DescribeActiveEvents(player)}; {PlayerUtils.DescribeHands(player)})");
            }
            onDone?.Invoke(free);
        }

        // As WaitForInventoryFree, and until Player.ProcessStatus is idle. Use before a hands change.
        // onDone(true) when idle, false on timeout.
        public static IEnumerator WaitForHandsIdle(Player player, float timeoutSeconds, string label, Action<bool> onDone = null)
        {
            float elapsed = 0f;
            while (player != null && (HasAnyActiveInventoryEvent(player) || !PlayerUtils.IsHandsIdle(player)) && elapsed < timeoutSeconds)
            {
                elapsed += Time.deltaTime;
                yield return null;
            }

            bool idle = player == null || (!HasAnyActiveInventoryEvent(player) && PlayerUtils.IsHandsIdle(player));
            if (!idle)
            {
                Plugin.LogSource.LogWarning($"[{label}] WaitForHandsIdle timed out after {timeoutSeconds}s ({DescribeActiveEvents(player)}; {PlayerUtils.DescribeHands(player)})");
            }
            onDone?.Invoke(idle);
        }

        private static string DescribeActiveEvents(Player player)
        {
            var events = (player?.InventoryController as ItemController)?.ActiveEvents;
            if (events == null) return "events=none";
            var names = new System.Collections.Generic.List<string>();
            foreach (var activeEvent in events)
            {
                if (activeEvent != null) names.Add(activeEvent.GetType().Name);
            }
            return names.Count == 0 ? "events=none" : $"events={string.Join(",", names)}";
        }
    }
}
