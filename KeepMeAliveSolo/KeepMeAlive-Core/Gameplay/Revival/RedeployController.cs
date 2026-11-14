//====================[ Imports ]====================
using System;
using System.Collections;
using EFT;
using EFT.InventoryLogic;
using JsonType;
using UnityEngine;

namespace KeepMeAlive.Gameplay.Revival
{
    //====================[ RedeployController ]====================
    // Redeploy: the downed player leaves their body and gear behind as a corpse and comes back at the nearest spawn point.
    //   Input: RedeployInput (the hold)
    //   Cost, cooldown, restore and invulnerability: the Redeploy section of the server config, through PostRevivalController
    //   Gear and corpse: RedeployCorpse; the spawn: RedeploySpawnPoints
    internal static class RedeployController
    {
        //====================[ Tuning ]====================
        // Longest the server's answer is waited for before the redeploy counts as denied (s).
        private const float AuthorizeTimeoutSeconds = 6f;

        //====================[ Flow ]====================
        // The hold is done: find the spawn, have the server charge the lives, then swap.
        public static void Begin(Player player, RMPlayer st)
        {
            if (!RedeploySpawnPoints.TryFindNearest(player, player.Position, out var spawn))
            {
                Plugin.LogSource.LogWarning($"[Redeploy] No spawn point for {player.ProfileId}; nothing was charged.");
                Fail(player, st, PlayerFacingMessages.Redeploy.NoSpawn);
                return;
            }

            RevivalController.Trace("Redeploy_Begin", player, st, $"| attempt={st.RedeployAttemptId}");
            Plugin.StaticCoroutineRunner.StartCoroutine(Run(player, st, spawn, st.RedeployAttemptId));
        }

        private static IEnumerator Run(Player player, RMPlayer st, RedeploySpawnPoints.Spawn spawn, int attemptId)
        {
            string pid = player.ProfileId;
            int raidGen = RMSession.RaidGeneration;

            var task = RevivalAuthority.RequestRedeployAsync(pid);
            float deadline = Time.time + AuthorizeTimeoutSeconds;
            while (!task.IsCompleted && Time.time < deadline) yield return null;

            // A raid boundary ends it silently: the server reset covers its side.
            if (raidGen != RMSession.RaidGeneration || attemptId != st.RedeployAttemptId) yield break;

            if (!task.IsCompleted)
            {
                // The request may still reach the server and charge: queued behind it, this puts the server back to downed.
                Plugin.LogSource.LogWarning($"[Redeploy] No answer after {AuthorizeTimeoutSeconds:F0}s for {pid}; treating it as denied.");
                RevivalAuthority.NotifyBeginCritical(pid);
                Fail(player, st, PlayerFacingMessages.ReviveDenied.AuthorizationFailed);
                yield break;
            }

            if (task.IsFaulted || !task.Result.Allowed)
            {
                string reason = task.IsFaulted ? null : task.Result.Reason;
                Fail(player, st, string.IsNullOrEmpty(reason) ? PlayerFacingMessages.Redeploy.Failed : reason);
                yield break;
            }

            // The server has charged the lives: from here the swap goes ahead whatever else changed in the meantime.
            if (player == null || !PlayerUtils.IsAlive(player))
            {
                Plugin.LogSource.LogWarning($"[Redeploy] {pid} is gone after the server approved; nothing to swap.");
                yield break;
            }

            Execute(player, st, spawn, task.Result.LivesRemaining);
        }

        private static void Fail(Player player, RMPlayer st, string message)
        {
            st.ClearRedeployInput();
            Hud.HideHold();
            DownedStateController.ClearRevivePromptTimer(st);
            if (!string.IsNullOrEmpty(message)) VFX_UI.Text(Color.yellow, message);
            DownedStateController.ShowSelfRevivePromptIfEligible(player);
        }

        private static void Execute(Player player, RMPlayer st, RedeploySpawnPoints.Spawn spawn, int livesRemaining)
        {
            string pid = player.ProfileId;
            st.LivesRemaining = livesRemaining;
            DownedStateController.TryCommitReviveFinalizeForCycle("Redeploy", pid, st);

            // The server has already charged the lives and marked the player Revived, so every step runs on its own: one that
            // fails is logged and the rest still happen, and the player always ends up revived, never half-swapped and downed.
            InventoryEquipment corpseEquipment = null;
            TransformSync[] bones = null;

            // The corpse first, while the body still lies as it does. A corpse that cannot be made leaves the gear on the
            // player: it is never destroyed without one.
            Step(pid, "Corpse", () =>
            {
                bones = RedeployCorpse.CaptureBones(player);
                RedeployCorpse.MoveLog log = null;
                corpseEquipment = bones != null ? RedeployCorpse.TakeGear(player, out log) : null;
                if (corpseEquipment != null && !RedeployCorpse.Spawn(player, corpseEquipment, bones))
                {
                    RedeployCorpse.Restore(log);
                    corpseEquipment = null;
                }

                if (corpseEquipment == null)
                    Plugin.LogSource.LogWarning($"[Redeploy] No corpse for {pid}; the player keeps their gear.");
                else
                    RedeployCorpse.UncoverForOwner(player, corpseEquipment);
            });

            EndDownedBody(player, st);

            Step(pid, "Teleport", () =>
            {
                player.Teleport(spawn.Position);
                player.MovementContext.Rotation = new Vector2(spawn.Yaw, 0f);
            });

            // From here it is an ordinary revive: restore, god mode and the HUD timer, hands and movement given back,
            // then cooldown, all from the Redeploy section of the config.
            st.ReviveRequestedSource = (int)ReviveSource.Redeploy;
            st.ClearRedeployInput();
            Step(pid, "PostRevival", () => PostRevivalController.BeginPostRevival(player, pid, st, applyFinalize: true));

            RevivalController.Trace("Redeploy_Done", player, st, $"| spawn={spawn.Position} lives={livesRemaining}");
            Plugin.LogSource.LogInfo($"[Redeploy] {pid} redeployed to {spawn.Position}.");
        }

        // One part of the swap; false (and logged) when it threw.
        private static bool Step(string pid, string name, Action step)
        {
            try
            {
                step();
                return true;
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"[Redeploy] {name} failed for {pid}: {ex}");
                return false;
            }
        }

        // Everything the downed state holds on the local body, undone without the get-up roll.
        private static void EndDownedBody(Player player, RMPlayer st)
        {
            string pid = player.ProfileId;
            Step(pid, "StopDownedState", () =>
            {
                RevivalController.StopReviveProgress(st);
                DownedStateController.ClearTimers(st);
                Hud.HideHold();
                TinnitusRing.Stop();
            });

            Step(pid, "ReleaseBody", () =>
            {
                BodyRagdoll.ReleaseForRedeploy(player);
                st.IsLimp = false;
                DownedRagdollController.ClearLocalFall();
            });

            // The downed speed limit comes off first: the scrub forgets it without removing it, and a post-revive speed of
            // 100% then never clears it, leaving the redeployed player crawling.
            Step(pid, "ReleaseMovement", () =>
            {
                DownedMovementController.ClearSpeedLimit(player);
                DownedMovementController.ScrubDownedInputLocks();
            });

            // After the clear (which re-reads the old weight): the gear is gone, so the weight limit is re-read now.
            RedeployCorpse.RefreshWeight(player);
        }
    }
}
