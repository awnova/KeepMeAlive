//====================[ Imports ]====================
using System;
using EFT;
using EFT.HealthSystem;
using KeepMeAlive.Components;

namespace KeepMeAlive.Helpers
{
    //====================[ ReviveItemCooldown Effect Types ]====================
    internal interface IReviveItemCooldown : IHealthEffect { }
    internal class ReviveItemCooldownEffect : ActiveHealthController.Effect, IReviveItemCooldown { }
    internal class ReviveItemCooldownNetworkEffect : NetworkHealthController.Effect, IReviveItemCooldown { }

    //====================[ PostReviveEffects ]====================
    // Single entry point for everything that happens to a player the moment revival completes.
    // Called by DownedStateController.FinishRevive (authoritative path) and by the Fika
    // packet handlers (OnRevivedPacket / resync) for remote-player / edge-case paths.
    internal static class PostReviveEffects
    {
        // Fractures only ever attach to limbs â€” Head/Chest/Stomach never fracture in EFT.
        private static readonly EBodyPart[] FractureBodyParts =
        {
            EBodyPart.LeftArm, EBodyPart.RightArm,
            EBodyPart.LeftLeg, EBodyPart.RightLeg
        };

        //====================[ Public Entry Points ]====================
        // Applies source-specific post-revival effects and local health changes.
        // Set applyDebuffs to false for resync or late-join paths.
        public static void Apply(Player player, ReviveSource source, bool applyDebuffs = true)
        {
            if (player?.ActiveHealthController == null) return;

            var cfg = RevivePolicy.PostRevive(source);

            try { RestoreBodyParts(player, cfg); }
            catch (Exception ex) { Plugin.LogSource.LogError($"[PostReviveEffects] RestoreBodyParts error: {ex.Message}"); }

            try { RemoveBleeds(player, cfg); }
            catch (Exception ex) { Plugin.LogSource.LogError($"[PostReviveEffects] RemoveBleeds error: {ex.Message}"); }

            try { RemoveFractures(player, cfg); }
            catch (Exception ex) { Plugin.LogSource.LogError($"[PostReviveEffects] RemoveFractures error: {ex.Message}"); }

            try { NormalizeTinnitusDuration(player); }
            catch (Exception ex) { Plugin.LogSource.LogError($"[PostReviveEffects] NormalizeTinnitusDuration error: {ex.Message}"); }

            if (applyDebuffs)
            {
                try { ApplyDebuffs(player, cfg); }
                catch (Exception ex) { Plugin.LogSource.LogError($"[PostReviveEffects] ApplyDebuffs error: {ex.Message}"); }
            }
        }

        // Returns the source-specific invulnerability duration.
        public static float GetInvulnDuration(ReviveSource source)
            => RevivePolicy.PostRevive(source).InvulnerabilityDurationSeconds;

        // Returns the source-specific revival cooldown duration.
        public static float GetCooldownDuration(ReviveSource source)
            => RevivePolicy.PostRevive(source).CooldownSeconds;

        // Returns the source-specific speed multiplier during invulnerability.
        // Config percentages use 100 for normal speed; numeric values are honored as-is.
        public static float GetInvulnSpeedMultiplier(ReviveSource source)
        {
            float pct = RevivePolicy.PostRevive(source).InvulnerabilitySpeedPercent;

            if (float.IsNaN(pct) || float.IsInfinity(pct)) return 1f;
            return pct / 100f;
        }

        //====================[ Body Part Restoration ]====================
        private static void RestoreBodyParts(Player player, SyncedPostReviveSourceConfig cfg)
        {
            if (!cfg.RestoreBodyParts) return;

            var hc = player.ActiveHealthController;
            if (hc == null) return;

            foreach (EBodyPart part in Enum.GetValues(typeof(EBodyPart)))
            {
                if (part == EBodyPart.Common) continue;
                // DO NOT check IsBodyPartDestroyed here! 
                // Vitals like Stomach might be saved at exactly 1 HP by the downed state,
                // so they are not formally "destroyed" but still need to be restored to their target percentages.
                RestoreOneBodyPart(hc, part, cfg.RestorePercent);
            }
        }

        private static void RestoreOneBodyPart(ActiveHealthController hc, EBodyPart part, SyncedBodyRestorePercentConfig restore)
        {
            try
            {
                float pct = GetRestorePercent(part, restore);
                bool isDestroyed = hc.IsBodyPartDestroyed(part);

                // Non-destroyed parts with 0% target: nothing to heal.
                if (!isDestroyed && pct <= 0f) return;

                if (isDestroyed && !hc.FullRestoreBodyPart(part)) return;

                var current = hc.GetBodyPartHealth(part);
                // Destroyed parts always get at least 1 HP so vitals aren't left at zero.
                float newHp = isDestroyed ? Math.Max(1f, current.Maximum * pct) : current.Maximum * pct;
                float delta = newHp - current.Current;

                if (isDestroyed)
                {
                    // Part was destroyed and just fully restored â€” set to the configured percentage
                    // (FullRestoreBodyPart sets it to max, so we need to reduce it to the target).
                    if (Math.Abs(delta) > 0.01f)
                        hc.ChangeHealth(part, delta, default);
                }
                else if (delta > 0.01f)
                {
                    // Part was not destroyed â€” only increase health up to the target percentage.
                    hc.ChangeHealth(part, delta, default);
                }

                // If the limb was utterly destroyed, we mimic a real CMS/SurvKit and wipe ALL negative effects
                // on this specific limb right away. (If it wasn't destroyed, we leave it alone and let the
                // config-driven RemoveBleeds/RemoveFractures methods handle it below).
                if (isDestroyed)
                {
                    hc.RemoveNegativeEffects(part);
                }
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"[PostReviveEffects] RestoreOneBodyPart {part} error: {ex.Message}");
            }
        }

        private static float GetRestorePercent(EBodyPart part, SyncedBodyRestorePercentConfig restore) => part switch
        {
            EBodyPart.Head    => restore.Head / 100f,
            EBodyPart.Chest   => restore.Chest / 100f,
            EBodyPart.Stomach => restore.Stomach / 100f,
            EBodyPart.LeftArm or EBodyPart.RightArm => restore.Arms / 100f,
            EBodyPart.LeftLeg or EBodyPart.RightLeg => restore.Legs / 100f,
            _ => 0.5f
        };

        //====================[ Effect Removal ]====================
        // Removes active light and heavy bleeds from every body part; fresh wounds remain.
        private static void RemoveBleeds(Player player, SyncedPostReviveSourceConfig cfg)
        {
            if (!cfg.RemoveBleeds) return;

            var hc = player.ActiveHealthController;
            if (hc == null) return;

            // Remove all standard heavy/light bleeds
            hc.RemoveBleedingEffects(EBodyPart.Common);
        }

        // Removes fractures from limbs while preserving bleeds and other negative effects.
        private static void RemoveFractures(Player player, SyncedPostReviveSourceConfig cfg)
        {
            if (!cfg.RemoveFractures) return;

            var hc = player.ActiveHealthController;
            if (hc == null) return;

            for (int i = 0; i < FractureBodyParts.Length; i++)
                hc.ResidueEffectsWhere(FractureBodyParts[i], effect => effect is IFracture);
        }

        // Resets active tinnitus/contusion to a fixed 10-second post-revive duration.
        private static void NormalizeTinnitusDuration(Player player)
        {
            var hc = player.ActiveHealthController;
            if (hc == null) return;

            if (hc.FindActiveEffect<IContusion>(EBodyPart.Common) == null)
            {
                return;
            }

            // Replace any existing contusion/tinnitus with a short 10-second tail (ring: engine minimum, 15s).
            hc.ResidueEffectsWhere(EBodyPart.Common, effect => effect is IContusion);
            TinnitusScope.Run(() => hc.DoContusion(10f, 1f));
        }

        //====================[ Post-Revival Debuffs ]====================
        private static void ApplyDebuffs(Player player, SyncedPostReviveSourceConfig cfg)
        {
            var hc = player.ActiveHealthController;
            if (hc == null) return;

            if (cfg.ApplyContusionOnRevive)
            {
                float dur = cfg.ContusionDurationSeconds;
                TinnitusScope.Run(() => hc.DoContusion(dur, 1f));
            }

            if (cfg.ApplyPainOnRevive)
            {
                // Applied to head; full-body sway/hands shake at default strength.
                float painDur = cfg.PainDurationSeconds;
                hc.DoPain(EBodyPart.Head, 0f, painDur, painDur * 0.5f);
            }
        }

        //====================[ Cooldown Effect ]====================
        // Applies the cooldown icon; Fika syncs the effect to peers.
        // Call only for the local player (IsYourPlayer).
        public static void ApplyCooldownEffect(Player player, float cooldownDuration)
        {
            if (player?.ActiveHealthController == null)
            {
                Plugin.LogSource.LogWarning("[PostReviveEffects] ApplyCooldownEffect: ActiveHealthController is null, skipping.");
                return;
            }
            try
            {
                RevivalDebugLog.LogDebug(
                    $"[PostReviveEffects] Applying ReviveItemCooldown effect: hc={player.ActiveHealthController.GetType().Name}, workTime={cooldownDuration}");
                var effect = player.ActiveHealthController.AddEffect<ReviveItemCooldownEffect>(
                    EBodyPart.Chest,
                    delayTime: null,
                    workTime: cooldownDuration,
                    residueTime: 3f);
                RevivalDebugLog.LogDebug(
                    $"[PostReviveEffects] ReviveItemCooldown applied: {effect?.GetType().Name}, Type={effect?.Type?.Name}, State={effect?.State}");
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"[PostReviveEffects] ApplyCooldownEffect error: {ex}");
            }
        }
    }
}
