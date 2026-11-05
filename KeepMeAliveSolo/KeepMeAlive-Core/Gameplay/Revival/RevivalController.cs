//====================[ Imports ]====================
using System;
using System.Collections;
using EFT;
using UnityEngine;

namespace KeepMeAlive.Gameplay.Revival
{
    //====================[ RevivalController ]====================
    // Core revive flow: server-authorized start, the local progress timer, and completion.
    //   Input: SelfReviveInput (self hold)
    //   Presentation: RevivePresentation
    internal static class RevivalController
    {
        //====================[ Shared Revive Start ]====================
        // Longest the revive start waits for the server's authorization before treating it as denied, and for an
        // inventory operation in flight before spending the item (s).
        private const float AuthorizeTimeoutSeconds = 6f;
        private const float ConsumeWaitSeconds = 2f;

        // Authorize with the server -> revalidate -> check/consume the item -> onStart.
        // Releases the server reservation when the attempt ends before starting. onDenied gets a
        // null message when a newer attempt supersedes this one. It runs on the persistent plugin, so a raid
        // boundary mid-way ends it silently: no callback and no cancel (the server reset covers the reservation).
        private static IEnumerator AuthorizeAndStartCoroutine(
            ReviveSource source,
            Player itemOwner,
            string targetId,
            Func<bool> stillValid,
            Action onStart,
            Action<string, Color> onDenied)
        {
            const string sourceName = "self";
            ReviveDebug.Log("Auth_Start", targetId, false, $"source={sourceName}");

            int raidGen = RMSession.RaidGeneration;
            bool RaidEnded()
            {
                if (raidGen == RMSession.RaidGeneration) return false;
                ReviveDebug.Log("Auth_RaidEnded", targetId, false, $"source={sourceName}");
                return true;
            }

            var task = RevivalAuthority.AuthorizeReviveStartAsync(targetId, sourceName);
            float deadline = Time.time + AuthorizeTimeoutSeconds;
            while (!task.IsCompleted && Time.time < deadline)
            {
                yield return null;
            }

            if (RaidEnded()) yield break;

            // No answer in time: denied. The hold it ends blocks bleed-out and give-up, so it must not wait on the server
            // for as long as the HTTP layer would. The cancel queues behind the request, so a late grant is rolled back.
            if (!task.IsCompleted)
            {
                Plugin.LogSource.LogWarning($"[ReviveAuth] No authorization answer after {AuthorizeTimeoutSeconds:F0}s for target={targetId} source={sourceName}; treating it as denied.");
                RevivalAuthority.NotifyReviveStartCancelled(targetId);
                onDenied(PlayerFacingMessages.ReviveDenied.AuthorizationFailed, Color.yellow);
                yield break;
            }

            bool allowed = false;
            string denyReason = PlayerFacingMessages.ReviveDenied.AuthorizationFailed;
            if (task.IsFaulted)
            {
                Plugin.LogSource.LogWarning($"[ReviveAuth] Authorization task fault for target={targetId} source={sourceName}: {task.Exception?.GetBaseException().Message}");
            }
            else
            {
                allowed = task.Result.Allowed;
                denyReason = task.Result.Reason;
            }

            ReviveDebug.Log("Auth_Done", targetId, false, $"source={sourceName} allowed={allowed} reason='{denyReason}'");

            if (!allowed)
            {
                string fallback = PlayerFacingMessages.ReviveDenied.SelfFallback;
                onDenied(string.IsNullOrEmpty(denyReason) ? fallback : denyReason, Color.yellow);
                yield break;
            }

            if (!stillValid())
            {
                RevivalAuthority.NotifyReviveStartCancelled(targetId);
                onDenied(null, Color.yellow);
                yield break;
            }

            // Self revive demands a stim when RequireStim is enabled, which is deleted as the
            // revive starts when ConsumeStim is enabled. Nothing is spent unless everything required is there.
            bool needStim = RevivePolicy.RequireStim(source);
            bool consumeStim = RevivePolicy.ShouldConsumeStim(source);

            if (needStim && !InventoryUtils.HasStim(itemOwner))
            {
                RevivalAuthority.NotifyReviveStartCancelled(targetId);
                onDenied(PlayerFacingMessages.Revive.MissingStimCanceled, Color.red);
                yield break;
            }

            if (consumeStim)
            {
                // Wait out any inventory operation in flight here, not inside the consume coroutine, so the target
                // is checked again right before anything is spent.
                bool inventoryFree = false;
                yield return InventoryUtils.WaitForInventoryFree(itemOwner, ConsumeWaitSeconds, $"{sourceName}Revive", ok => inventoryFree = ok);
                if (RaidEnded()) yield break;
                if (!stillValid())
                {
                    RevivalAuthority.NotifyReviveStartCancelled(targetId);
                    onDenied(null, Color.yellow);
                    yield break;
                }
                if (!inventoryFree)
                {
                    RevivalAuthority.NotifyReviveStartCancelled(targetId);
                    onDenied(PlayerFacingMessages.Revive.InventoryBusyCanceled, Color.yellow);
                    yield break;
                }

                bool stimSpent = false;
                yield return InventoryUtils.ConsumeStimCoroutine(itemOwner, $"{sourceName}Stim", ok => stimSpent = ok);
                if (RaidEnded()) yield break;
                if (!stimSpent)
                {
                    RevivalAuthority.NotifyReviveStartCancelled(targetId);
                    onDenied(PlayerFacingMessages.Revive.MissingStimCanceled, Color.red);
                    yield break;
                }
            }

            // Revalidate the target after the item-consumption coroutine yields.
            if (!stillValid())
            {
                RevivalAuthority.NotifyReviveStartCancelled(targetId);
                onDenied(null, Color.yellow);
                yield break;
            }

            onStart();
        }

        internal static void ApplyRevivingState(string playerId, RMPlayer playerState)
        {
            RMSession.SetPlayerState(playerId, RMState.Reviving);
            playerState.ClearSelfReviveInput();
        }

        //====================[ Self Revive Start ]====================
        internal static void BeginSelfReviveStart(Player player, RMPlayer st)
        {
            string pid = player.ProfileId;
            int attemptId = st.SelfReviveAttemptId;
            Trace("SelfAuth_Begin", player, st, $"attempt={attemptId}");

            Plugin.StaticCoroutineRunner.StartCoroutine(AuthorizeAndStartCoroutine(
                ReviveSource.Self,
                player,
                pid,
                stillValid: () => attemptId == st.SelfReviveAttemptId && st.State == RMState.BleedingOut && st.SelfReviveAwaitingAuth,
                onStart: () =>
                {
                    StopReviveProgress(st);
                    ApplyRevivingState(pid, st);
                    Trace("SelfAuth_StateSetReviving", player, st);
                },
                onDenied: (message, color) =>
                {
                    // A newer attempt owns the input state; leave it alone.
                    if (attemptId != st.SelfReviveAttemptId) return;

                    st.ClearSelfReviveInput();
                    Trace("SelfAuth_Denied", player, st, $"| reason='{message}'");
                    if (message != null)
                    {
                        DownedStateController.CancelReviveState(player, st, message, color);
                    }
                }));
        }

        //====================[ Revive Progress ]====================
        // Runs on the local player's BleedingOut -> Reviving edge (DownedStateController.TickDowned).
        internal static void StartRevive(Player player, RMPlayer st, string reason)
        {
            if (player == null || st == null) return;
            if (!player.IsYourPlayer)
            {
                ReviveDebug.Log("StartRevive_SkipNonLocal", player.ProfileId, false, $"reason={reason}");
                return;
            }
            if (st.State != RMState.Reviving) return;

            if (st.IsReviveProgressActive)
            {
                Trace("StartRevive_SkipDuplicate", player, st, $"reason={reason}");
                return;
            }

            float duration = RevivePolicy.ProgressSeconds;
            Trace("StartRevive", player, st, $"| duration={duration:F2} reason={reason}");

            RevivePresentation.PlayReviveAnimation(player, st);

            st.CriticalStateMainTimer?.Stop();
            st.CriticalStateMainTimer = null;
            st.ReviveStatusTimer?.Stop();
            st.ReviveStatusTimer = Hud.ShowStatus(Hud.StatusKind.Reviving, duration);

            Hud.HideHold();
            Hud.HideStrip();
            DownedStateController.ClearRevivePromptTimer(st);

            int expectedCycle = st.ReviveCycleId;
            st.ReviveProgressCoroutine = Plugin.StaticCoroutineRunner.StartCoroutine(
                DownedStateController.DelayedActionAfterSeconds(duration, () => OnReviveProgressComplete(player, expectedCycle)));
        }

        internal static void StopReviveProgress(RMPlayer st)
        {
            if (st.ReviveProgressCoroutine == null) return;
            Plugin.StaticCoroutineRunner.StopCoroutine(st.ReviveProgressCoroutine);
            st.ReviveProgressCoroutine = null;
        }

        private static void OnReviveProgressComplete(Player player, int expectedCycle)
        {
            if (player == null) return;
            var st = RMSession.GetPlayerState(player.ProfileId);

            // This callback is the tail of the one progress coroutine; its handle is now spent.
            st.ReviveProgressCoroutine = null;
            Trace("ReviveProgressComplete", player, st, $"| cycle={st.ReviveCycleId} expected={expectedCycle}");

            if (st.ReviveCycleId != expectedCycle || st.State != RMState.Reviving)
            {
                Trace("ReviveProgressIgnored", player, st, "| stale cycle or no longer Reviving");
                return;
            }

            CompleteLocalRevive(player, st);
        }

        //====================[ Revive Completion ]====================
        private static void CompleteLocalRevive(Player player, RMPlayer st)
        {
            string pid = player.ProfileId;
            if (!DownedStateController.TryCommitReviveFinalizeForCycle("LocalRevive", pid, st)) return;

            Plugin.StaticCoroutineRunner.StartCoroutine(NotifyReviveCompleteCoroutine(pid));

            // Sets Revived, stops the presentation, god mode, effects and the invulnerability panel.
            PostRevivalController.BeginPostRevival(player, pid, st, applyFinalize: true);
            Trace("CompleteLocalRevive", player, st);
            Plugin.LogSource.LogInfo($"[Downed] revive complete for {pid}");
        }

        // The RMSession write (main-thread only - its dictionary isn't thread-safe) happens after
        // the coroutine resumes, never on the background task.
        private static IEnumerator NotifyReviveCompleteCoroutine(string playerId)
        {
            int raidGen = RMSession.RaidGeneration;
            var task = RevivalAuthority.NotifyReviveCompleteAsync(playerId);

            while (!task.IsCompleted)
            {
                yield return null;
            }

            if (raidGen != RMSession.RaidGeneration) yield break;

            if (task.IsFaulted || task.Result == null)
            {
                Plugin.LogSource.LogWarning($"[ReviveAuth] Server did not confirm revive completion for {playerId}; lives count not updated.");
                yield break;
            }

            if (RMSession.TryGetPlayerState(playerId, out var st))
            {
                st.LivesRemaining = task.Result.Value;
            }
        }

        //====================[ Tracing ]====================
        // One call feeds both debug channels.
        internal static void Trace(string step, Player player, RMPlayer st, string details = null)
        {
            string id = player?.ProfileId ?? "<null>";
            ReviveDebug.Log(step, id, player?.IsYourPlayer ?? false, details);

            if (st == null || !RevivalDebugLog.IsSelfReviveTraceEnabled) return;

            RevivalDebugLog.LogSelfReviveTrace(
                $"[SelfReviveTrace] {id} step='{step}' state={st.State} selfReviving={st.IsSelfReviving} awaitingAuth={st.SelfReviveAwaitingAuth} progress={st.IsReviveProgressActive} {details ?? string.Empty}");
        }
    }
}
