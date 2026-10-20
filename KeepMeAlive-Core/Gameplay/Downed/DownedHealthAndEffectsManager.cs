//====================[ Imports ]====================
using System;
using System.Collections;
using EFT;
using EFT.HealthSystem;
using UnityEngine;

namespace KeepMeAlive.Gameplay.Downed
{
    //====================[ DownedHealthAndEffectsManager ]====================
    internal static class DownedHealthAndEffectsManager
    {
        private static SyncedGameplayConfig Cfg => SyncedServerConfigStore.Config.Gameplay;

        //====================[ Constants ]====================
        private static readonly EBodyPart[] TrackedBodyParts =
        {
            EBodyPart.Head, EBodyPart.Chest, EBodyPart.Stomach,
            EBodyPart.LeftArm, EBodyPart.RightArm, EBodyPart.LeftLeg, EBodyPart.RightLeg
        };

        // The downing Kill() fires mid ApplyDamage; EFT then spreads the hit's overdamage, which can
        // destroy the parts just restored. Track the local player's ApplyDamage nesting so the
        // restore is repeated once the hit has fully resolved.
        private static int _applyDamageDepth;
        private static bool _restoreAfterDamage;

        //====================[ Public API ]====================
        public static void BeginApplyDamage() => _applyDamageDepth++;

        public static void EndApplyDamage(Player player)
        {
            if (_applyDamageDepth > 0) _applyDamageDepth--;
            if (_applyDamageDepth > 0 || !_restoreAfterDamage) return;
            _restoreAfterDamage = false;
            if (RMSession.IsPlayerCritical(player.ProfileId)) RestoreVitalsToMinimum(player);
        }

        public static void ResetDamageTracking()
        {
            _applyDamageDepth = 0;
            _restoreAfterDamage = false;
        }

        // Restore destroyed body parts to 1 HP on downed entry (toggleable via config).
        public static void RestoreVitalsToMinimum(Player player)
        {
            if (player == null || !player.IsYourPlayer || player.ActiveHealthController is not { } hc) return;
            if (_applyDamageDepth > 0) _restoreAfterDamage = true;
            try
            {
                bool restoredAny = false;
                for (int i = 0; i < TrackedBodyParts.Length; i++)
                {
                    var part = TrackedBodyParts[i];
                    if (hc.IsBodyPartDestroyed(part) && hc.FullRestoreBodyPart(part))
                    {
                        restoredAny = true;
                        float delta = 1f - hc.GetBodyPartHealth(part).Current;
                        if (delta < -0.01f) hc.ChangeHealth(part, delta, default);
                    }
                }

                // FullRestoreBodyPart builds a fresh HealthValue (DownMult 1); when god mode is already
                // on, reapply the coefficient so the restored parts are protected like the rest.
                if (restoredAny && hc.DamageCoeff <= 0f) hc.SetDamageCoeff(hc.DamageCoeff);
            }
            catch (Exception ex) { Plugin.LogSource.LogError($"[DownedHealthAndEffects] RestoreVitalsToMinimum error: {ex.Message}"); }
        }

        // Apply critical visual effects. The downed speed limit is driven per tick by
        // DownedMovementController.TickSpeedLimit.
        public static void ApplyCriticalEffects(Player player)
        {
            if (player == null || !player.IsYourPlayer) return;
            try
            {
                if (player.ActiveHealthController != null)
                {
                    var hc = player.ActiveHealthController;
                    GodMode.RunWithDamageEnabled(player, () =>
                    {
                        // Added directly: DoStun scales the duration by the player's eye protection
                        // (thermals make it zero), so MaxStunSeconds would not be seconds.
                        float stunSeconds = Math.Min(Cfg.Revival.CriticalStateSeconds, Cfg.Revival.MaxStunSeconds);
                        if (Cfg.Revival.ApplyStunOnDowned && stunSeconds > 0f)
                            hc.AddEffect<ActiveHealthController.Stun>(EBodyPart.Head, 0f, ActiveHealthController.Stun.StunBuildUpTime + stunSeconds, null, 1f);
                    });

                    if (Cfg.Revival.ApplyContusionOnDowned)
                    {
                        // The contusion's tinnitus ducks and muffles the whole mix for 15 s; it would bury the cry
                        // of going down, so it starts once the cry has been heard.
                        var speaker = player.Speaker;
                        float wait = speaker != null && speaker.Busy ? Mathf.Min(speaker.TimeLeft, MaxContusionWait) : 0f;
                        if (wait > 0f)
                            Plugin.StaticCoroutineRunner.StartCoroutine(ContusionAfter(player, wait, RMSession.GetPlayerState(player.ProfileId).ReviveCycleId));
                        else
                            ApplyDownedContusion(player);
                    }
                }
            }
            catch (Exception ex) { Plugin.LogSource.LogError($"[DownedHealthAndEffects] ApplyCriticalEffects error: {ex.Message}"); }
        }

        // Longest the downed contusion waits for the cry of going down (hit lines run up to about 2 s).
        private const float MaxContusionWait = 2.5f;

        // The downed contusion, unless this downed state has ended (or a new one begun) meanwhile.
        private static IEnumerator ContusionAfter(Player player, float wait, int cycleId)
        {
            yield return new WaitForSeconds(wait);
            if (player == null || player.HealthController is not { IsAlive: true }) yield break;
            if (!RMSession.TryGetPlayerState(player.ProfileId, out var st)
                || st.State != RMState.BleedingOut || st.ReviveCycleId != cycleId) yield break;
            try { ApplyDownedContusion(player); }
            catch (Exception ex) { Plugin.LogSource.LogError($"[DownedHealthAndEffects] ContusionAfter error: {ex.Message}"); }
        }

        private static void ApplyDownedContusion(Player player)
        {
            var hc = player.ActiveHealthController;
            if (hc == null) return;
            GodMode.RunWithDamageEnabled(player, () =>
                TinnitusScope.Run(() => hc.DoContusion(Cfg.Revival.CriticalStateSeconds, 1f)));
        }
    }
}
