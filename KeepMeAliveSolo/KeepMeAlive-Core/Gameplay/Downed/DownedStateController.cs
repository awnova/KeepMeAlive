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
    //   DownedHealthAndEffectsManager, DownedMovementController,
    //   PostRevivalController, RevivalController
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

            if (!RevivePolicy.HasLivesFor(ReviveSource.Self, st.LivesRemaining))
            {
                Hud.SetStrip(Hud.StripState.NoLives, cost);
                return;
            }

            Hud.SetStrip(Hud.StripState.Available, RevivePolicy.LivesSpent(ReviveSource.Self, st.LivesRemaining));
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

            // A self-revive hold that ends early cuts the self-stim short.
            SelfStimFlow.Cancel(player);

            st.ClearSelfReviveInput();
            st.AllowWeaponEquipForReviveAnim = false;
            Hud.HideHold();
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
            if (player == null || !player.IsYourPlayer) return;

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
                st.ClearSelfReviveInput();
                st.IsSilentInventoryAnimActive = false;
                st.IsSilentReviveBlurActive = false;
                st.AllowWeaponEquipForReviveAnim = false;
                st.SelfReviveAttemptId = 0;
                st.GiveUpHoldTime = -1f;
                st.LastObservedState = st.State;

                DownedHealthAndEffectsManager.RestoreVitalsToMinimum(player);

                if (Cfg.Protection.EnableGhostMode) GhostMode.EnterGhostMode(player);
                if (Cfg.Protection.EnableGodMode) GodMode.Enable(player);

                ExternalInputOwners.ReleaseAll("downed");
                if (Cfg.Downed.BlockUiWhenDowned) Hud.SetInputBlocked(true);
                // LivesRemaining is updated at raid start and after a revive.
                DownedRagdollController.Begin(player, st);
                RevivalAuthority.NotifyBeginCritical(id);

                CloseInventoryIfOpen(player);

                // Before the effects: the contusion's ring waits for this cry.
                DownedVoiceController.OnLocalDowned(player);
                DownedHealthAndEffectsManager.ApplyCriticalEffects(player);
                DownedMovementController.ApplyRevivableState(player);
                if (Cfg.Downed.UnconsciousOnDowned) RevivePresentation.ApplyUnconscious(player);
                HeartbeatEffect.Start(player, st);
                DownedScreenEffects.Start(player);
                ShowCriticalStateUI(player, st);

                Plugin.LogSource.LogInfo($"[Downed] Player {id} entered critical state");
            }
            catch (Exception ex) { Plugin.LogSource.LogError($"[DownedStateController] EnterDowned error: {ex.Message}"); }
        }

        // True between raid stop and the next GameWorld.OnGameStarted; death blocking is
        // disabled during this interval.
        public static bool IsRaidEnding { get; set; }

        // Called when the local player extracts or the raid ends. Cleans up the local
        // player's state, UI, and input locks.
        public static void HandleLocalPlayerExtracting()
        {
            try
            {
                var local = PlayerUtils.GetYourPlayer();
                if (local == null) return;

                ExternalInputOwners.ReleaseAll("extract");

                if (RMSession.TryGetPlayerState(local.ProfileId, out var st))
                {
                    RevivePresentation.Stop(local, st, "local-game-extract");
                    RevivalController.StopReviveProgress(st);
                    HeartbeatEffect.Stop(st);
                    ClearTimers(st);
                    st.ClearSelfReviveInput();
                    st.CooldownTimer = 0f;
                    st.InvulnerabilityTimer = 0f;
                    st.CriticalTimer = 0f;
                    st.IsLimp = false;
                    RMSession.SetPlayerState(local.ProfileId, RMState.None);
                }

                GhostMode.ClearGhostFlag(local.ProfileId);
                GodMode.Disable(local);
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

        // Called from Harmony patches on raid-terminating engine calls (BaseLocalGame.Stop, GameWorld.Dispose).
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
                    // Invalidates a self-revive still authorizing: the RMPlayer it captured outlives RMSession.Clear().
                    st.SelfReviveAttemptId++;
                    st.ClearSelfReviveInput();
                }

                _nextInventoryCloseAttempt = 0f;
                HeartbeatEffect.Clear();
                LocalBodyCamera.Clear();

                TinnitusScope.Clear();
                TinnitusRing.Stop();

                DownedScreenEffects.Stop();
                Hud.HideAll();
                Hud.SetInputBlocked(false);
                DownedMovementController.ScrubDownedInputLocks();
                BodyRagdoll.ClearAll();
                DownedRagdollController.ClearLocalFall();
                BodyAnimation.ClearAll();
                DownedVoiceController.ClearAll();
                GhostMode.ClearAll();
                GodMode.Clear();
                DeathMode.ClearCaches();
                ExternalInputOwners.ReleaseAll("raid boundary");
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
                    DownedScreenEffects.Stop();
                    TinnitusRing.Stop();
                }

                st.ClearSelfReviveInput();
                st.AllowWeaponEquipForReviveAnim = false;
                st.GiveUpHoldTime = -1f;

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
                // Once per change, not per tick: this runs every frame while downed.
                ReviveDebug.Log("TickDowned_StateChanged", player.ProfileId, player.IsYourPlayer, $"{st.LastObservedState} -> {st.State}");

                if (st.State == RMState.Reviving)
                {
                    RevivalController.StartRevive(player, st, "StateTransition");
                }

                st.LastObservedState = st.State;
            }

            // Check this before critical-state cleanup so movement can end the limp state.
            DownedRagdollController.TickLocal(player, st);

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

            bool gaveUp = TickGiveUp(player, st);
            if (st.State == RMState.BleedingOut && !st.IsSelfReviving && (st.CriticalTimer <= 0f || gaveUp))
            {
                st.GiveUpHoldTime = -1f;
                RevivePresentation.Stop(player, st, "BleedoutFallback");
                ClearTimers(st);
                if (player.IsYourPlayer)
                {
                    Hud.SetInputBlocked(false);
                }
                DeathMode.ForceBleedout(player);
            }
        }

        private const float GiveUpHoldSeconds = 2f;

        // Requires a hold to confirm the give-up action.
        private static bool TickGiveUp(Player player, RMPlayer st)
        {
            bool eligible = player.IsYourPlayer && st.State == RMState.BleedingOut && !st.IsSelfReviving;
            if (!eligible)
            {
                // Self-revive now owns the prompt; drop any active give-up hold.
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

        //====================[ UI ]====================
        private static void ShowCriticalStateUI(Player player, RMPlayer st)
        {
            if (!player.IsYourPlayer) return;
            try
            {
                Hud.HideHold();
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
