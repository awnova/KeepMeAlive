//====================[ Imports ]====================
using System;
using System.Collections;
using EFT;
using EFT.UI.Screens;
using UnityEngine;

namespace KeepMeAlive.Gameplay.Downed
{
    //====================[ DownedStateController ]====================
    // Slim state-machine coordinator. Domain logic lives in:
    //   BodyInteractableRuntime, DownedHealthAndEffectsManager,
    //   DownedMovementController, PostRevivalController, RevivalController
    internal static class DownedStateController
    {
        private static SyncedGameplayConfig Cfg => SyncedServerConfigStore.Config.Gameplay;

        //====================[ Shared Helpers ]====================
        internal static IEnumerator DelayedActionAfterSeconds(float seconds, Action action)
        {
            yield return new WaitForSeconds(seconds);
            try { action?.Invoke(); }
            catch (Exception ex) { Plugin.LogSource.LogError($"[DownedStateController] delayed action error: {ex.Message}"); }
        }

        internal static void ClearTimers(RMPlayer st)
        {
            st.CriticalStateMainTimer?.Stop(); st.CriticalStateMainTimer = null;
            st.ReviveStatusTimer?.Stop(); st.ReviveStatusTimer = null;
            st.RevivePromptTimer?.Stop(); st.RevivePromptTimer = null;
        }

        internal static void ClearRevivePromptTimer(RMPlayer st)
        {
            st.RevivePromptTimer?.Stop();
            st.RevivePromptTimer = null;
        }

        // Shows the current self-revive requirement or the reason self-revive is unavailable.
        internal static void ShowSelfRevivePromptIfEligible(Player player)
        {
            if (player == null || !player.IsYourPlayer) return;

            if (!RevivePolicy.IsSelfReviveEnabled)
            {
                Hud.SetStrip(Hud.StripState.SelfReviveOff);
                return;
            }

            if (!RevivePolicy.HasRequiredItem(ReviveSource.Self, player))
            {
                Hud.SetStrip(Hud.StripState.NeedsStim);
                return;
            }

            int cost = RevivePolicy.LivesCost(ReviveSource.Self);
            var st = RMSession.GetPlayerState(player.ProfileId);

            if (st.LivesRemaining < cost)
            {
                Hud.SetStrip(Hud.StripState.NoLives, cost);
                return;
            }

            Hud.SetStrip(Hud.StripState.Available, cost);
        }

        private const float InventoryCloseRetrySeconds = 0.5f;
        private static float _nextInventoryCloseAttempt;

        // An inventory or loot screen open when the hit landed would otherwise stay usable while
        // downed (DownedInventoryScreenBlockPatch only stops new ones opening).
        private static void CloseInventoryIfOpen(Player player)
        {
            if (!Cfg.Downed.BlockUiWhenDowned) return;
            _nextInventoryCloseAttempt = Time.time + InventoryCloseRetrySeconds;
            try
            {
                if (!EftScreenManager.Instance.CheckCurrentScreen(EEftScreenType.Inventory)) return;
                player.GetComponent<GamePlayerOwner>()?.CloseInventoryIfOpen();
            }
            catch (Exception ex) { Plugin.LogSource.LogWarning($"[DownedStateController] CloseInventoryIfOpen failed: {ex.Message}"); }
        }

        private static void HideAllPanelsAndStop(RMPlayer st)
        {
            Hud.HideAll();
            ClearTimers(st);
        }

        internal static void CancelReviveState(Player player, RMPlayer st, string message = null, Color? messageColor = null)
        {
            if (st.State == RMState.Reviving)
            {
                RevivalDebugLog.LogSelfReviveTrace($"[SelfReviveTrace] CancelReviveState ignored while already Reviving for {player?.ProfileId}");
                return;
            }

            // Preserve unconscious state when canceling a self-revive attempt that
            // didn't complete — the player is still downed and should stay locked out.
            bool preserveUnconscious = st.State == RMState.BleedingOut
                                       && Cfg.Downed.UnconsciousOnDowned
                                       && st.IsSilentInventoryAnimActive;

            if (!preserveUnconscious)
                RevivePresentation.Stop(player, st, "CancelReviveState");

            // A self-revive hold that ends early cuts the self-stim short on every peer.
            SelfStimFlow.Cancel(player);

            st.IsBeingRevived = false;
            st.ClearSelfReviveInput();
            st.AllowWeaponEquipForReviveAnim = false;
            Hud.HideHold();
            Hud.HideStatusOverlay();
            ClearRevivePromptTimer(st);
            ShowSelfRevivePromptIfEligible(player);

            if (!string.IsNullOrEmpty(message))
            {
                VFX_UI.Text(messageColor ?? Color.yellow, message);
            }
        }

        private static void BeginNewReviveCycle(RMPlayer st)
        {
            st.ReviveCycleId++;
            st.FinalizedReviveCycleId = -1;
            st.HasInitializedInvulnerability = false;
        }

        internal static bool TryCommitReviveFinalizeForCycle(string source, string playerId, RMPlayer st)
        {
            if (st.IsReviveFinalizeCommittedForCurrentCycle)
            {
                RevivalDebugLog.LogReviveFlow($"[ReviveFinalize] Suppressed duplicate finalize ({source}) for {playerId} in cycle {st.ReviveCycleId}");
                return false;
            }

            st.FinalizedReviveCycleId = st.ReviveCycleId;
            return true;
        }

        //====================[ Enter / Exit Downed ]====================
        public static void EnterDowned(Player player, EDamageType damageType)
        {
            if (player == null) return;

            try
            {
                string id = player.ProfileId;
                var st = RMSession.GetPlayerState(id);

                RevivePresentation.Stop(player, st, "EnterDowned_Reset");

                // Only a healthy player goes down. A lethal hit during the cooldown never gets here: DeathMode lets it kill.
                if (st.State != RMState.None) return;

                st.CooldownTimer = 0f;
                st.KillOverride = false;
                st.PlayerDamageType = damageType;
                RMSession.SetPlayerState(id, RMState.BleedingOut);
                BeginNewReviveCycle(st);
                st.CriticalTimer = Cfg.Downed.CriticalStateSeconds;
                RevivalController.StopReviveProgress(st);
                st.ClearReviveSession();
                st.ClearSelfReviveInput();
                st.IsSilentInventoryAnimActive = false;
                st.IsSilentReviveBlurActive = false;
                st.AllowWeaponEquipForReviveAnim = false;
                st.SelfReviveAttemptId = 0;
                st.GiveUpHoldTime = -1f;
                st.LastObservedState = st.State;

                TeamHealWheel.CloseFor(id, "patient went down");
                DownedHealthAndEffectsManager.RestoreVitalsToMinimum(player);

                if (Cfg.Protection.EnableGhostMode) GhostMode.EnterGhostModeById(id);
                if (Cfg.Protection.EnableGodMode) GodMode.Enable(player);

                if (player.IsYourPlayer)
                {
                    if (Cfg.Downed.BlockUiWhenDowned) Hud.SetInputBlocked(true);
                    // LivesRemaining is updated at raid start and after a revive.
                    DownedRagdollController.Begin(player, st);
                    FikaBridge.SendBleedingOutPacket(id, st.CriticalTimer, st.LivesRemaining, st);
                    RevivalAuthority.NotifyBeginCritical(id);
                    st.ResyncCooldown = -1f;

                    CloseInventoryIfOpen(player);

                    // Before the effects: the contusion's ring waits for this cry.
                    DownedVoiceController.OnLocalDowned(player);
                    DownedHealthAndEffectsManager.ApplyCriticalEffects(player);
                    DownedMovementController.ApplyRevivableState(player);
                    if (Cfg.Downed.UnconsciousOnDowned) RevivePresentation.ApplyUnconscious(player);
                    HeartbeatEffect.Start(player, st);
                    DownedScreenEffects.Start(player);
                    ShowCriticalStateUI(player, st);
                }

                Plugin.LogSource.LogInfo($"[Downed] Player {id} entered critical state (local={player.IsYourPlayer})");
            }
            catch (Exception ex) { Plugin.LogSource.LogError($"[DownedStateController] EnterDowned error: {ex.Message}"); }
        }

        // True between FikaGameEndedEvent and the next GameWorldStartedEvent; death blocking is
        // disabled during this interval.
        public static bool IsRaidEnding { get; set; }

        // Called from CoopGame.Extract when the local player extracts. Cleans up only the local
        // player's state, drag, UI, and input locks without wiping the raid for remaining teammates.
        public static void HandleLocalPlayerExtracting()
        {
            try
            {
                var local = PlayerUtils.GetYourPlayer();
                if (local == null) return;

                if (DownedDragController.IsLocalDragging)
                    DownedDragController.StopDrag(local, "coop-game-extract");
                if (local.CurrentState is CrouchHoldState hold)
                    hold.Cancel();

                TeamHealWheel.Close("coop-game-extract");
                DownedLootGuard.End();

                if (RMSession.TryGetPlayerState(local.ProfileId, out var st))
                {
                    RevivePresentation.Stop(local, st, "coop-game-extract");
                    RevivalController.StopReviveProgress(st);
                    HeartbeatEffect.Stop(st);
                    ClearTimers(st);
                    st.ClearReviveSession();
                    st.ClearSelfReviveInput();
                    st.CooldownTimer = 0f;
                    st.InvulnerabilityTimer = 0f;
                    st.CriticalTimer = 0f;
                    st.IsLimp = false;
                    RMSession.SetPlayerState(local.ProfileId, RMState.None);
                }

                GhostMode.ClearGhostFlag(local.ProfileId);
                GodMode.Disable(local);
                DownedSpectate.Clear();
                DownedScreenEffects.Stop();
                TinnitusRing.Stop();
                Hud.HideAll();
                Hud.SetInputBlocked(false);
                DownedMovementController.ScrubDownedInputLocks();
                DownedRagdollController.ClearLocalFall();
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"[DownedStateController] HandleLocalPlayerExtracting error: {ex.Message}");
            }
        }

        // Called from Harmony patches on raid-terminating engine calls (CoopGame.Stop)
        // and from FikaGameEndedEvent.
        public static void HandleRaidTerminating(string reason)
        {
            try
            {
                IsRaidEnding = true;
                ResetForRaidBoundary(reason);
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"[DownedStateController] HandleRaidTerminating({reason}) error: {ex.Message}");
            }
        }

        // Clears every piece of per-raid state. Called on raid start and raid end so nothing
        // (input locks, ghost flags, UI, coroutines) can leak from one raid into the next.
        public static void ResetForRaidBoundary(string reason)
        {
            try
            {
                var local = PlayerUtils.GetYourPlayer();
                if (local != null && RMSession.TryGetPlayerState(local.ProfileId, out var st))
                {
                    RevivePresentation.Stop(local, st, reason);
                    RevivalController.StopReviveProgress(st);
                    HeartbeatEffect.Stop(st);
                    ClearTimers(st);
                }

                _nextInventoryCloseAttempt = 0f;
                HeartbeatEffect.Clear();
                LocalBodyCamera.Clear();
                DownedLootGuard.End();

                TinnitusScope.Clear();
                TinnitusRing.Stop();

                DownedSpectate.Clear();
                DownedScreenEffects.Stop();
                Hud.HideAll();
                Hud.SetInputBlocked(false);
                DownedMovementController.ScrubDownedInputLocks();
                DownedDragController.Clear();
                DownedRagdollController.ClearLocalFall();
                BodyAnimation.ClearAll();
                DownedVoiceController.ClearAll();
                GhostMode.ClearAll();
                GodMode.Clear();
                BodyInteractableRuntime.Clear();
                DeathMode.ClearCaches();
                TeamHealWheel.Close("raid boundary");
                TeamMedical.Clear();
                TeamHealPatient.Clear();
                DownedHealthAndEffectsManager.ResetDamageTracking();
                RMSession.Clear();
                RevivalDebugLog.LogDebug($"[Downed] Raid-boundary reset ({reason})");
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"[DownedStateController] ResetForRaidBoundary({reason}) error: {ex}");
            }
        }

        public static void PrepareForDeath(Player player, string reason = "PrepareForDeath")
        {
            if (player == null) return;

            try
            {
                var st = RMSession.GetPlayerState(player.ProfileId);

                RevivePresentation.Stop(player, st, reason);
                RevivalController.StopReviveProgress(st);
                HeartbeatEffect.Stop(st);
                if (player.IsYourPlayer)
                {
                    DownedSpectate.OnDeath();
                    DownedScreenEffects.Stop();
                    TinnitusRing.Stop();
                }

                st.IsBeingRevived = false;
                st.ClearSelfReviveInput();
                st.AllowWeaponEquipForReviveAnim = false;
                st.GiveUpHoldTime = -1f;
                st.CurrentReviverId = string.Empty;

                if (player.IsYourPlayer)
                {
                    HideAllPanelsAndStop(st);
                    Hud.SetInputBlocked(false);
                    DownedMovementController.ReleaseProne(player);
                    DownedMovementController.ReleaseEmptyHands(player);
                }
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogWarning($"[DownedStateController] PrepareForDeath error: {ex.Message}");
            }
        }

        //====================[ Per-Frame Ticks ]====================
        public static void TickDowned(Player player)
        {
            var st = RMSession.GetPlayerState(player.ProfileId);

            if (st.State != st.LastObservedState)
            {
                if (st.State == RMState.Reviving)
                {
                    RevivalController.StartRevive(player, st, "StateTransition");
                }

                st.LastObservedState = st.State;
            }

            // Check this before critical-state cleanup so movement can end the limp state.
            DownedRagdollController.TickLocal(player, st);
            // Before the critical-state early-out below: a revive that ends the state must also end the spectating.
            DownedSpectate.Tick(player, st);

            if (!st.IsCritical)
            {
                if (player.IsYourPlayer && Hud.IsInputBlocked)
                {
                    Hud.SetInputBlocked(false);
                }
                // Safety net: any exit from the critical state clears the screen effects.
                if (player.IsYourPlayer && DownedScreenEffects.IsActive) DownedScreenEffects.Stop();
                return;
            }

            ReviveDebug.Log("TickDowned_Enter", player.ProfileId, player.IsYourPlayer, $"state={st.State}");

            // While Reviving the transit panel shows revive progress, not bleed-out, so CriticalTimer
            // is left frozen at its value when the revive started (it drives the screen effects).
            if (st.State == RMState.BleedingOut)
            {
                if (st.CriticalStateMainTimer is { IsRunning: true })
                {
                    st.CriticalTimer = (float)st.CriticalStateMainTimer.GetTimeSpan().TotalSeconds;
                }
                else
                {
                    st.CriticalTimer -= Time.deltaTime;
                    if (st.CriticalStateMainTimer == null) TryLazyShowTransitTimer(player, st);
                }
            }

            if (player.IsYourPlayer) DownedScreenEffects.Tick(st);

            // The game's close can be refused (e.g. while another close is still in flight), so keep
            // retrying until the screen is actually gone.
            if (player.IsYourPlayer && Time.time >= _nextInventoryCloseAttempt) CloseInventoryIfOpen(player);

            SelfReviveInput.Tick(player, st);

            if (st.IsBeingRevived && st.State == RMState.BleedingOut && !string.IsNullOrEmpty(st.CurrentReviverId))
            {
                st.BeingRevivedWatchdogTimer -= Time.deltaTime;
                if (st.BeingRevivedWatchdogTimer <= 0f)
                {
                    Plugin.LogSource.LogWarning($"[Downed] Reviver watchdog expired for {player.ProfileId}; clearing IsBeingRevived");
                    st.CurrentReviverId = string.Empty;
                    CancelReviveState(player, st, PlayerFacingMessages.Downed.ReviverTimedOut, Color.yellow);
                    // Observers never run this watchdog; the resync is how they drop the claim.
                    RequestClaimResync(st);
                }
            }

            bool gaveUp = TickGiveUp(player, st);
            if (st.State == RMState.BleedingOut && !st.IsBeingRevived && !st.IsSelfReviving && (st.CriticalTimer <= 0f || gaveUp))
            {
                st.GiveUpHoldTime = -1f;
                RevivePresentation.Stop(player, st, "BleedoutFallback");
                ClearTimers(st);
                if (player.IsYourPlayer)
                {
                    Hud.SetInputBlocked(false);
                }
                BodyInteractableRuntime.Remove(player.ProfileId);
                DeathMode.ForceBleedout(player);
            }
        }

        private const float GiveUpHoldSeconds = 2f;

        // Requires a hold to confirm the give-up action.
        private static bool TickGiveUp(Player player, RMPlayer st)
        {
            bool eligible = player.IsYourPlayer && st.State == RMState.BleedingOut && !st.IsBeingRevived && !st.IsSelfReviving
                            && !DownedSpectate.IsActive;
            if (!eligible)
            {
                // Another flow (team help / self-revive) now owns the prompt; drop any active give-up hold.
                if (st.GiveUpHoldTime >= 0f && !st.IsSelfReviving)
                {
                    Hud.HideHold();
                    ClearRevivePromptTimer(st);
                }
                st.GiveUpHoldTime = -1f;
                return false;
            }

            KeyCode key = KeepMeAliveSettings.GIVE_UP_KEY.Value;

            if (st.GiveUpHoldTime < 0f)
            {
                if (!Input.GetKeyDown(key)) return false;

                st.GiveUpHoldTime = 0f;
                Hud.HideHold();
                ClearRevivePromptTimer(st);
                st.RevivePromptTimer = Hud.ShowHold(Hud.HoldKind.GiveUp, GiveUpHoldSeconds);
                return false;
            }

            if (!Input.GetKey(key))
            {
                st.GiveUpHoldTime = -1f;
                Hud.HideHold();
                ClearRevivePromptTimer(st);
                ShowSelfRevivePromptIfEligible(player);
                return false;
            }

            st.GiveUpHoldTime += Time.deltaTime;
            return st.GiveUpHoldTime >= GiveUpHoldSeconds;
        }

        public static void TickResync(Player player)
        {
            if (!player.IsYourPlayer) return;

            var st = RMSession.GetPlayerState(player.ProfileId);
            if (st.State == RMState.None || (st.ResyncCooldown -= Time.deltaTime) > 0f) return;

            st.ResyncCooldown = 5f;
            if (st.ResyncFollowUp)
            {
                st.ResyncFollowUp = false;
                st.ResyncCooldown = DownedDragController.ClaimSettleTime + 0.5f;
            }
            FikaBridge.SendPlayerStateResyncPacket(player.ProfileId, st);
        }

        // Owner only, when a claim on them (drag or team-revive help) changes: a resync now for anyone who missed
        // the change, and one more once the claim has settled. Peers ignore resyncs for ClaimSettleTime after their
        // own claim change, so without the second one a crossed claim would only settle on the periodic resync.
        internal static void RequestClaimResync(RMPlayer st)
        {
            st.ResyncCooldown = -1f;
            st.ResyncFollowUp = true;
        }

        //====================[ UI ]====================
        private static void ShowCriticalStateUI(Player player, RMPlayer st)
        {
            if (!player.IsYourPlayer) return;
            try
            {
                Hud.HideHold();
                Hud.HideStatusOverlay();
                st.CriticalStateMainTimer = Hud.StartBleed(Cfg.Downed.CriticalStateSeconds, st.LivesRemaining);
                ShowSelfRevivePromptIfEligible(player);
            }
            catch (Exception ex) { Plugin.LogSource.LogError($"[DownedStateController] ShowCriticalStateUI error: {ex.Message}"); }
        }

        private static void TryLazyShowTransitTimer(Player player, RMPlayer st)
        {
            if (!player.IsYourPlayer) return;
            if (st.CriticalTimer <= 0.5f) return;
            st.CriticalStateMainTimer = Hud.StartBleed(st.CriticalTimer, st.LivesRemaining);
        }
    }
}
