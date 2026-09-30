//====================[ Imports ]====================
using EFT.InventoryLogic;
using KeepMeAlive.Helpers;
using UnityEngine;

namespace KeepMeAlive.Features
{
    //====================[ TeamHealUseTime ]====================
    // Applies TEAM_HEAL_USE_TIME_MULTIPLIER to exactly one med use: the team-heal item the local
    // patient just started applying. EFT reads the use time inside ActiveHealthController.DoMedEffect
    // (frames after ApplyItem, once the meds hands controller reaches the use point), so the scale
    // is armed per item id and only active for the duration of that DoMedEffect call.
    internal static class TeamHealUseTime
    {
        private const float PendingTimeoutSeconds = 15f;

        private static string _pendingItemId;
        private static float _pendingExpiry;
        private static bool _active;

        public static void SetPending(Item item)
        {
            _pendingItemId = item?.Id;
            _pendingExpiry = Time.time + PendingTimeoutSeconds;
        }

        public static void Clear()
        {
            _pendingItemId = null;
            _active = false;
        }

        public static void BeginMedEffect(Item item)
        {
            _active = _pendingItemId != null && item != null && item.Id == _pendingItemId && Time.time < _pendingExpiry;
        }

        public static void EndMedEffect()
        {
            if (_active) Clear();
        }

        public static float Scale(float useTime)
        {
            if (!_active) return useTime;
            float multiplier = SyncedServerConfigStore.Config.Gameplay.TeamHealing.UseTimeMultiplier;
            return multiplier > 0f ? useTime * multiplier : useTime;
        }
    }
}
