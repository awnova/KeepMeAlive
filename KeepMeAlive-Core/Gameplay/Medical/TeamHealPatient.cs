//====================[ Imports ]====================
using System;
using System.Collections;
using EFT;
using EFT.HealthSystem;
using EFT.InventoryLogic;
using UnityEngine;

namespace KeepMeAlive.Gameplay.Medical
{
    //====================[ TeamHealPatient ]====================
    // The patient's side of team healing, on the patient's own machine. A healer's request (TeamHealPacket) applies their
    // item here; the use itself starts frames later, when the meds hands controller reaches its use point and calls
    // ActiveHealthController.DoMedEffect, and that call is where the use's length is known (its start delay plus the use
    // time from the item, the limb, the SurgerySpeed skill and the team-heal multiplier). So the healer is answered from
    // there (TeamHealDoMedEffectPatch): started, with how long the use takes, or failed. A watcher then tells them when the
    // use's effect is over, run to the end or stopped early (a weapon drawn, the patient going down).
    // With EBodyPart.Common the meds controller queues one body part, so one request is one DoMedEffect call.
    internal static class TeamHealPatient
    {
        //====================[ Constants ]====================
        // How long the patient's hands may take to reach the use point (stowing a weapon first) before the healer is told
        // the request failed.
        public const float UseStartTimeoutSeconds = 5f;
        // The item stays applied in the hands after that, so a use that still starts this long after the apply is answered
        // (late) and scaled as usual instead of draining the healer's item unannounced.
        private const float LateStartSeconds = 15f;
        // How long past its expected end a use may still look active before the healer is told it ended anyway.
        private const float EndGraceSeconds = 5f;
        // An effect gone within this much of its expected end ran to the end.
        private const float CompletedSlackSeconds = 0.3f;

        //====================[ State ]====================
        private sealed class IncomingHeal
        {
            public string PatientId, HealerId, ItemId;
            public float ArmedAt;
            public bool Resolved;
            // The healer was told it failed; a later start still counts until LateStartSeconds.
            public bool TimedOut;
        }

        // The request whose item was applied and whose use hasn't started yet. One at a time: a second request while one
        // is waiting is refused (the hands are already busy with the first), unless that one has timed out.
        private static IncomingHeal _pending;
        // The pending request while DoMedEffect runs for its item: the use-time multiplier applies to that call only.
        private static IncomingHeal _inMedEffect;
        // Bumped at the raid boundary so watchers from the last raid stop without sending.
        private static int _generation;

        //====================[ Incoming Request ]====================
        // Started by TeamHealPacketHandlers once the request is confirmed to be about the local player.
        internal static IEnumerator ApplyIncoming(Player patient, string healerId, string itemId)
        {
            int generation = _generation;

            // Wait out any in-flight inventory operation (a revive equip still being processed) before racing ApplyItem.
            yield return InventoryUtils.WaitForInventoryFree(patient, 2f, "TeamHealPacket");
            if (patient == null || generation != _generation) yield break;
            var heal = new IncomingHeal { PatientId = patient.ProfileId, HealerId = healerId, ItemId = itemId };
            string refusal = Apply(patient, heal);
            if (refusal != null)
            {
                FikaBridge.SendTeamHealResultPacket(heal.PatientId, healerId, itemId, false, refusal, 0f);
                yield break;
            }

            float deadline = heal.ArmedAt + UseStartTimeoutSeconds;
            while (!heal.Resolved && generation == _generation && Time.time < deadline) yield return null;
            if (heal.Resolved || generation != _generation) yield break;

            heal.TimedOut = true;
            RevivalDebugLog.LogDebug($"[TeamHealPatient] use of {itemId} not started after {UseStartTimeoutSeconds:F0}s");
            FikaBridge.SendTeamHealResultPacket(heal.PatientId, healerId, itemId, false, PlayerFacingMessages.TeamHeal.PatientBusy, 0f);

            deadline = heal.ArmedAt + LateStartSeconds;
            while (!heal.Resolved && generation == _generation && Time.time < deadline) yield return null;
            if (heal.Resolved || generation != _generation) yield break;

            heal.Resolved = true;
            if (ReferenceEquals(_pending, heal)) _pending = null;
            RevivalDebugLog.LogDebug($"[TeamHealPatient] use of {itemId} never started ({LateStartSeconds:F0}s)");
        }

        // Applies the healer's item; null when applied, otherwise why not.
        private static string Apply(Player patient, IncomingHeal heal)
        {
            IncomingHeal previous = null;
            try
            {
                Player healer = PlayerUtils.GetPlayerById(heal.HealerId);
                Item item = MedCatalog.Find(healer, heal.ItemId);

                if (!PlayerUtils.IsAliveAndStanding(patient))
                    return PlayerFacingMessages.TeamHeal.PatientUnavailable;
                if (!InventoryUtils.IsTeamHealItemValid(healer, item, out var invalidReason))
                    return invalidReason;
                if (_pending != null && !_pending.Resolved && !_pending.TimedOut)
                    return PlayerFacingMessages.TeamHeal.PatientBusy;

                // A refused apply leaves a timed-out request armed for its late start.
                previous = _pending;
                _pending = heal;
                heal.ArmedAt = Time.time;
                if (patient.HealthController.ApplyItem(item, EBodyPart.Common)) return null;

                if (ReferenceEquals(_pending, heal)) _pending = previous;
                return PlayerFacingMessages.TeamHeal.PatientBusy;
            }
            catch (Exception ex)
            {
                if (ReferenceEquals(_pending, heal)) _pending = previous;
                Plugin.LogSource.LogError($"[TeamHealPatient] apply error: {ex.Message}");
                return PlayerFacingMessages.TeamHeal.PatientBusy;
            }
        }

        //====================[ DoMedEffect Hooks ]====================
        // Prefix: the call is the pending request's use if it is the local player's and for that item.
        public static void BeginMedEffect(Player player, Item item)
        {
            var heal = _pending;
            _inMedEffect = heal != null && !heal.Resolved && player != null && player.IsYourPlayer && item != null && item.Id == heal.ItemId
                ? heal
                : null;
        }

        // The use time EFT reads inside that call (HealthEffectsComponent.UseTime), scaled for a team heal.
        public static float Scale(float useTime)
        {
            if (_inMedEffect == null) return useTime;
            float multiplier = SyncedServerConfigStore.Config.Gameplay.TeamHealing.UseTimeMultiplier;
            return multiplier > 0f ? useTime * multiplier : useTime;
        }

        // Postfix: the effect the use made (null: the item had nothing to apply to). The healer is answered here.
        public static void OnMedEffect(IHealthEffect effect)
        {
            var heal = _inMedEffect;
            if (heal == null) return;
            _inMedEffect = null;
            heal.Resolved = true;
            if (ReferenceEquals(_pending, heal)) _pending = null;

            if (effect == null)
            {
                RevivalDebugLog.LogDebug($"[TeamHealPatient] DoMedEffect for {heal.ItemId} returned no effect");
                FikaBridge.SendTeamHealResultPacket(heal.PatientId, heal.HealerId, heal.ItemId, false, PlayerFacingMessages.TeamHeal.PatientBusy, 0f);
                return;
            }

            float useSeconds = effect.TimeLeft;
            RevivalDebugLog.LogDebug($"[TeamHealPatient] use of {heal.ItemId} started {Time.time - heal.ArmedAt:F2}s after apply{(heal.TimedOut ? " (late)" : "")}: " +
                $"TimeLeft={effect.TimeLeft:F2} OverallDuration={effect.OverallDuration:F2} WorkStateTime={effect.WorkStateTime:F2} " +
                $"CurStateTimeLeft={effect.CurStateTimeLeft:F2} State={effect.State}");

            FikaBridge.SendTeamHealResultPacket(heal.PatientId, heal.HealerId, heal.ItemId, true, string.Empty, useSeconds);
            Plugin.StaticCoroutineRunner.StartCoroutine(WatchUse(heal, effect, useSeconds));
        }

        // Finalizer: whatever happened, the multiplier is for that one call.
        public static void EndMedEffect() => _inMedEffect = null;

        //====================[ Use Watcher ]====================
        // The effect leaves Added / Started when its use is over: at its end, or early through ForceResidue (an inventory
        // event that stops med use) or removal.
        private static IEnumerator WatchUse(IncomingHeal heal, IHealthEffect effect, float useSeconds)
        {
            int generation = _generation;
            float start = Time.time;
            float deadline = start + useSeconds + EndGraceSeconds;
            var lastState = effect.State;

            while (generation == _generation && IsRunning(effect) && Time.time < deadline)
            {
                if (effect.State != lastState)
                {
                    RevivalDebugLog.LogDebug($"[TeamHealPatient] {heal.ItemId} effect {lastState} -> {effect.State} at {Time.time - start:F2}s, TimeLeft={effect.TimeLeft:F2}");
                    lastState = effect.State;
                }
                yield return null;
            }
            if (generation != _generation) yield break;

            float elapsed = Time.time - start;
            bool completed = elapsed >= useSeconds - CompletedSlackSeconds;
            RevivalDebugLog.LogDebug($"[TeamHealPatient] use of {heal.ItemId} over after {elapsed:F2}s of {useSeconds:F2}s, state {effect.State} (completed={completed})");
            FikaBridge.SendTeamHealUseEndedPacket(heal.PatientId, heal.HealerId, heal.ItemId, completed);
        }

        private static bool IsRunning(IHealthEffect effect) =>
            effect.State == EEffectState.Added || effect.State == EEffectState.Started || effect.State == EEffectState.Paused;

        //====================[ Lifecycle ]====================
        // Raid boundary.
        public static void Clear()
        {
            _pending = null;
            _inMedEffect = null;
            _generation++;
        }
    }
}
