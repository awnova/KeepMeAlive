//====================[ Imports ]====================
using System;
using System.Collections;
using EFT;
using KeepMeAlive.Components;
using KeepMeAlive.Fika;
using KeepMeAlive.Helpers;
using UnityEngine;

namespace KeepMeAlive.Features
{
    //====================[ RevivalController ]====================
    // Core revive flow: server-authorized start (shared by self and team revive), the local
    // progress timer, and completion. Fika never echoes a packet back to its sender, so every
    // sender applies its own state locally before broadcasting.
    //   Input: SelfReviveInput (self hold), BodyInteractable (team plant hold)
    //   Presentation: RevivePresentation
    internal static class RevivalController
    {
        //====================[ Shared Revive Start ]====================
        // Authorize with the server -> revalidate -> check/consume the item -> onStart.
        // Releases the server reservation when the attempt ends before starting. onDenied gets a
        // null message when a newer attempt supersedes this one.
        private static IEnumerator AuthorizeAndStartCoroutine(
            ReviveSource source,
            Player itemOwner,
            string targetId,
            string reviverId,
            Func<bool> stillValid,
            Action onStart,
            Action<string, Color> onDenied)
        {
            string sourceName = source == ReviveSource.Self ? "self" : "team";
            ReviveDebug.Log("Auth_Start", targetId, false, $"reviver={reviverId} source={sourceName}");

            var task = RevivalAuthority.AuthorizeReviveStartAsync(targetId, reviverId, sourceName);
            while (!task.IsCompleted)
            {
                yield return null;
            }

            bool allowed = false;
            string denyReason = PlayerFacingMessages.ReviveDenied.AuthorizationFailed;
            if (task.IsFaulted)
            {
                Plugin.LogSource.LogWarning($"[ReviveAuth] Authorization task fault for target={targetId} reviver={reviverId} source={sourceName}: {task.Exception?.GetBaseException().Message}");
            }
            else
            {
                allowed = task.Result.Allowed;
                denyReason = task.Result.Reason;
            }

            ReviveDebug.Log("Auth_Done", targetId, false, $"reviver={reviverId} source={sourceName} allowed={allowed} reason='{denyReason}'");

            if (!allowed)
            {
                string fallback = source == ReviveSource.Self ? PlayerFacingMessages.ReviveDenied.SelfFallback : PlayerFacingMessages.ReviveDenied.TeamFallback;
                onDenied(string.IsNullOrEmpty(denyReason) ? fallback : denyReason, Color.yellow);
                yield break;
            }

            if (!stillValid())
            {
                RevivalAuthority.NotifyReviveStartCancelled(targetId);
                onDenied(null, Color.yellow);
                yield break;
            }

            if (RevivePolicy.RequiresItem)
            {
                bool hasItem = ModUtils.HasReviveItem(itemOwner);
                if (hasItem && RevivePolicy.MustConsumeItem(source))
                {
                    yield return ModUtils.ConsumeReviveItemCoroutine(itemOwner, $"{sourceName}ReviveItem", ok => hasItem = ok);
                }

                if (!hasItem)
                {
                    RevivalAuthority.NotifyReviveStartCancelled(targetId);
                    onDenied(PlayerFacingMessages.Revive.MissingItemCanceled, Color.red);
                    yield break;
                }

                // Revalidate the target after the item-consumption coroutine yields.
                if (!stillValid())
                {
                    RevivalAuthority.NotifyReviveStartCancelled(targetId);
                    onDenied(null, Color.yellow);
                    yield break;
                }
            }

            onStart();
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
                pid,
                stillValid: () => attemptId == st.SelfReviveAttemptId && st.State == RMState.BleedingOut && st.SelfReviveAwaitingAuth,
                onStart: () =>
                {
                    StopReviveProgress(st);
                    // No loopback: apply what the SelfReviveStart receivers apply, then broadcast.
                    RevivePacketHandlers.ApplyRevivingState(pid, st, ReviveSource.Self, pid);
                    Trace("SelfAuth_StateSetReviving", player, st, "| sending SelfReviveStart packet");
                    FikaBridge.SendSelfReviveStartPacket(pid);
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

        //====================[ Team Revive Start ]====================
        internal static void BeginTeamReviveStart(Player reviver, string targetId)
        {
            string reviverId = reviver.ProfileId;
            ReviveDebug.Log("TeamAuth_Begin", targetId, false, $"reviver={reviverId}");

            Plugin.StaticCoroutineRunner.StartCoroutine(AuthorizeAndStartCoroutine(
                ReviveSource.Team,
                reviver,
                targetId,
                reviverId,
                stillValid: () =>
                {
                    // The reviver may have gone down or died while authorizing / consuming the item.
                    if (reviver == null || reviver.HealthController == null || !reviver.HealthController.IsAlive
                        || RMSession.IsPlayerCritical(reviverId))
                        return false;

                    var target = RMSession.GetPlayerState(targetId);
                    return target.State == RMState.BleedingOut
                        && (string.IsNullOrEmpty(target.CurrentReviverId) || target.CurrentReviverId == reviverId);
                },
                onStart: () =>
                {
                    // No loopback: apply what the TeamReviveStart receivers apply, then broadcast, so
                    // this machine stops offering "Revive" on a teammate who is already being revived.
                    RevivePacketHandlers.ApplyRevivingState(targetId, RMSession.GetPlayerState(targetId), ReviveSource.Team, reviverId);
                    FikaBridge.SendTeamReviveStartPacket(targetId, reviverId);
                    Plugin.LogSource.LogInfo($"Revive hold completed for {targetId}");
                },
                onDenied: (message, color) =>
                {
                    ReviveDebug.Log("TeamAuth_Denied", targetId, false, $"reviver={reviverId} reason='{message}'");
                    FikaBridge.SendTeamCancelPacket(targetId, reviverId);
                    if (message != null)
                    {
                        VFX_UI.Text(color, message);
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

            var source = (ReviveSource)st.ReviveRequestedSource;
            float duration = RevivePolicy.GetProgressDuration(source);
            Trace("StartRevive", player, st, $"| duration={duration:F2} reason={reason}");

            RevivePresentation.PlayReviveAnimation(player, st);

            st.CriticalStateMainTimer?.Stop();
            st.CriticalStateMainTimer = VFX_UI.TransitPanel(VFX_UI.Gradient(Color.red, Color.green), PlayerFacingMessages.Revive.RevivingProgress, duration);

            VFX_UI.HideObjectivePanel();
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

            // Read before BeginPostRevival, which clears the reviver.
            var source = (ReviveSource)st.ReviveRequestedSource;
            string reviverId = source == ReviveSource.Self ? string.Empty : (st.CurrentReviverId ?? string.Empty);

            FikaBridge.SendRevivedPacket(pid, reviverId);
            st.ResyncCooldown = -1f;
            Plugin.StaticCoroutineRunner.StartCoroutine(NotifyReviveCompleteCoroutine(pid));

            // Sets Revived, stops the presentation, god mode, effects and the invulnerability panel.
            PostRevivalController.BeginPostRevival(player, pid, st, applyFinalize: true);

            var msg = source == ReviveSource.Self
                ? PlayerFacingMessages.ReviveComplete.LocalSelf
                : PlayerFacingMessages.ReviveComplete.LocalTeam;
            PlayerMessageRouter.Notify(Color.green, msg, MessageAudience.LocalPlayer);
            Trace("CompleteLocalRevive", player, st, $"| reviver='{reviverId}'");
            Plugin.LogSource.LogInfo($"[Downed] revive complete for {pid}");
        }

        // The RMSession write (main-thread only - its dictionary isn't thread-safe) happens after
        // the coroutine resumes, never on the background task.
        private static IEnumerator NotifyReviveCompleteCoroutine(string playerId)
        {
            var task = RevivalAuthority.NotifyReviveCompleteAsync(playerId);

            while (!task.IsCompleted)
            {
                yield return null;
            }

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

        // Remote players only: Fika never delivers our own RevivedPacket back to us.
        internal static void FinalizeRevivalFromPacket(Player player, string playerId, string reviverId)
        {
            // player may be null when this machine has no Player object for them; the state
            // bookkeeping and ghost-flag clear must still happen.
            if (player != null && player.IsYourPlayer)
            {
                ReviveDebug.Log("FinalizeFromPacket_IgnoredLocal", playerId, true, $"reviver={reviverId}");
                return;
            }

            var st = RMSession.GetPlayerState(playerId);
            bool isSelf = string.IsNullOrEmpty(reviverId) || reviverId == playerId;
            st.ReviveRequestedSource = (int)(isSelf ? ReviveSource.Self : ReviveSource.Team);
            st.CurrentReviverId = isSelf ? string.Empty : reviverId;

            PostRevivalController.BeginPostRevival(player, playerId, st, applyFinalize: false);
            DownedStateController.ClearTimers(st);
            ReviveDebug.Log("FinalizeFromPacket_Done", playerId, false, $"reviver={reviverId}");
        }

        //====================[ Tracing ]====================
        // One call feeds both debug channels; the self-revive trace only fires for self flows.
        internal static void Trace(string step, Player player, RMPlayer st, string details = null)
        {
            string id = player?.ProfileId ?? "<null>";
            ReviveDebug.Log(step, id, player?.IsYourPlayer ?? false, details);

            if (st == null || !RevivalDebugLog.IsSelfReviveTraceEnabled) return;
            if ((ReviveSource)st.ReviveRequestedSource != ReviveSource.Self && !st.IsSelfReviving) return;

            RevivalDebugLog.LogSelfReviveTrace(
                $"[SelfReviveTrace] {id} step='{step}' state={st.State} reviver='{st.CurrentReviverId}' beingRevived={st.IsBeingRevived} selfReviving={st.IsSelfReviving} awaitingAuth={st.SelfReviveAwaitingAuth} progress={st.IsReviveProgressActive} {details ?? string.Empty}");
        }
    }
}
