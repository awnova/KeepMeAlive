//====================[ Imports ]====================
using System;
using System.Collections.Generic;
using EFT;
using EFT.HealthSystem;
using EFT.Communications;
using EFT.UI;
using KeepMeAlive.Components;
using KeepMeAlive.Features;
using UnityEngine;

namespace KeepMeAlive.Helpers
{
    //====================[ DeathMode ]====================
    // Centralized death-blocking rules used by DeathPatch. Only ever runs on the owning
    // client: ActiveHealthController exists only for the local human player.
    public static class DeathMode
    {
        private static SyncedGameplayConfig Cfg => SyncedServerConfigStore.Config.Gameplay;

        //====================[ Fields ]====================
        private static readonly Dictionary<string, float> LastLogTime = new();
        private const float LOG_THROTTLE_SECONDS = 5f;

        // A single lethal hit calls Kill() several times in the same frame (destroyed head,
        // overdamage spill, total-health check). Cache the hardcore roll per player per frame
        // so one hit yields exactly one roll and one notification.
        private static readonly Dictionary<string, (int frame, bool allowDeath)> HardcoreRollCache = new();

        public static void ClearCaches()
        {
            LastLogTime.Clear();
            HardcoreRollCache.Clear();
        }

        //====================[ Core Rules ]====================
        // Returns true to block death (enter/keep critical/invuln), false to allow death.
        public static bool ShouldBlockDeath(Player player, EDamageType damageType)
        {
            if (player is null || player.IsAI || DownedStateController.IsRaidEnding) return false;

            if (!SyncedServerConfigStore.IsLoaded)
            {
                RevivalDebugLog.LogDebug("[DeathMode] Server config not loaded; fail-closed allows normal death flow.");
                return false;
            }

            string playerId = player.ProfileId;
            RMSession.TryGetPlayerState(playerId, out var st);

            if (st != null && st.KillOverride)
            {
                RevivalDebugLog.LogDebug($"[DeathMode] KillOverride set for {playerId}, allowing death.");
                return false;
            }

            if (st != null && st.IsCritical)
            {
                if (!Cfg.Protection.BlockDeathInCritical) return false;

                float currentTime = Time.time;
                if (!LastLogTime.TryGetValue(playerId, out float lastTime) || currentTime - lastTime >= LOG_THROTTLE_SECONDS)
                {
                    RevivalDebugLog.LogDebug($"[DeathMode] {playerId} critical; blocking death from {damageType}.");
                    LastLogTime[playerId] = currentTime;
                }
                return true;
            }

            if (st != null && st.IsInvulnerable)
            {
                RevivalDebugLog.LogDebug($"[DeathMode] {playerId} revived/invulnerable; blocking death.");
                return true;
            }

            if (st != null && st.State == RMState.CoolDown)
            {
                return false;
            }

            RevivalDebugLog.LogDebug($"[DeathMode] PREVENT: {playerId} lethal {damageType} -> enter critical.");
            return true;
        }

        // Hardcore headshot rule. Returns true to allow death immediately.
        // Only the hit that would first down the player rolls; once downed/invulnerable/on cooldown
        // the normal ShouldBlockDeath rules apply.
        public static bool ShouldAllowDeathFromHardcoreHeadshot(Player player, ActiveHealthController healthController, EDamageType damageType)
        {
            if (!Cfg.Protection.EnableHardcoreMode || !Cfg.Protection.HardcoreHeadshotsAreFatal)
            {
                return false;
            }

            string playerId = player.ProfileId;
            if (RMSession.TryGetPlayerState(playerId, out var st) && st.State != RMState.None)
            {
                return false;
            }

            if (damageType != EDamageType.Bullet || player.LastDamagedBodyPart != EBodyPart.Head
                || healthController.GetBodyPartHealth(EBodyPart.Head, true).Current >= 1f)
            {
                return false;
            }

            int frame = Time.frameCount;
            if (HardcoreRollCache.TryGetValue(playerId, out var cached) && cached.frame == frame)
            {
                return cached.allowDeath;
            }

            // Config value is 0-1 (0.75 = 75% chance to survive into critical state).
            float roll = UnityEngine.Random.Range(0f, 1f);
            bool allowDeath = roll >= Cfg.Protection.HardcoreCriticalStateChance;
            HardcoreRollCache[playerId] = (frame, allowDeath);

            if (!allowDeath)
            {
                Plugin.LogSource.LogInfo($"[DeathMode] Hardcore headshot spared (roll {roll:F2}). Enter critical.");
                NotificationManager.DisplayMessageNotification(
                    PlayerFacingMessages.Death.HeadshotCritical,
                    ENotificationDurationType.Default, ENotificationIconType.Alert, Color.green);
                return false;
            }

            Plugin.LogSource.LogInfo($"[DeathMode] Hardcore headshot: killed instantly (roll {roll:F2}).");
            NotificationManager.DisplayMessageNotification(
                PlayerFacingMessages.Death.HeadshotKilled,
                ENotificationDurationType.Default, ENotificationIconType.Alert, Color.red);
            return true;
        }

        //====================[ Executions ]====================
        // Called when a local death is allowed through without ForceBleedout (hardcore headshot,
        // cooldown, kill override). Makes sure UI/locks/audio are torn down and every peer and
        // the server learn the player is dead, so observers don't keep them marked as downed.
        public static void OnLocalDeathAllowed(Player player)
        {
            if (player is null || !player.IsYourPlayer) return;
            if (!RMSession.TryGetPlayerState(player.ProfileId, out var st)) return;
            if (st.State == RMState.None && !st.KillOverride) return;

            try
            {
                string id = player.ProfileId;
                bool alreadyAnnounced = st.KillOverride;

                DownedStateController.PrepareForDeath(player, "DeathAllowed");
                RMSession.SetPlayerState(id, RMState.None);
                st.KillOverride = true;
                st.FinalizedReviveCycleId = -1;
                GhostMode.ClearGhostFlag(id);
                GodMode.Disable(player);
                LastLogTime.Remove(id);

                if (!alreadyAnnounced)
                {
                    RevivalAuthority.NotifyReset(id);
                    Fika.FikaBridge.SendPlayerStateResetPacket(id, isDead: true);
                }
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"[DeathMode] OnLocalDeathAllowed error: {ex.Message}");
            }
        }

        // Force the player to die now (end of critical bleed-out or give-up).
        public static void ForceBleedout(Player player)
        {
            if (player is null) return;

            try
            {
                string id = player.ProfileId;
                var st = RMSession.GetPlayerState(id);

                DownedStateController.PrepareForDeath(player, "ForceBleedout");

                st.KillOverride = true;
                st.IsBeingRevived = false;
                RMSession.SetPlayerState(id, RMState.None);
                st.FinalizedReviveCycleId = -1;

                VFX_UI.Text(Color.black, PlayerFacingMessages.Death.YouDied);

                RevivalAuthority.NotifyReset(id);

                // Clear ghost flag but don't re-add to enemy lists - Kill() fires BSG's death handlers to auto-cleanup.
                GhostMode.ClearGhostFlag(id);
                GodMode.Disable(player);

                // Sent before Kill() on the same reliable-ordered channel as Fika's own death sync,
                // so observers clear the downed state before the body dies on their machine.
                Fika.FikaBridge.SendPlayerStateResetPacket(id, isDead: true);

                LastLogTime.Remove(id);

                var hc = player.ActiveHealthController;
                if (hc == null) return;

                var chest = hc.BodyState[EBodyPart.Chest].Health;
                hc.BodyState[EBodyPart.Chest].Health = new HealthValue(0f, chest.Maximum, 0f);
                hc.Kill(st.PlayerDamageType);

                Plugin.LogSource.LogInfo($"[DeathMode] {id} has died (forced bleedout).");
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"[DeathMode] ForceBleedout error: {ex.Message}");
            }
        }
    }
}
