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
    // The owner does all of it; every other peer runs ApplyFromPacket for its own copy of the player (the player object
    // itself is never replaced, so their Fika identity stays).
    internal static class RedeployController
    {
        //====================[ Tuning ]====================
        // Longest the server's answer is waited for before the redeploy counts as denied (s).
        private const float AuthorizeTimeoutSeconds = 6f;

        //====================[ Owner ]====================
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

            // A packet that cannot carry the corpse still tells the peers about the swap itself.
            if (!Step(pid, "Packet", () => FikaBridge.SendRedeployPacket(pid, corpseEquipment, bones, spawn.Position, spawn.Yaw, livesRemaining)))
                Step(pid, "PacketWithoutCorpse", () => FikaBridge.SendRedeployPacket(pid, null, null, spawn.Position, spawn.Yaw, livesRemaining));

            EndDownedBody(player, st);

            Step(pid, "Teleport", () =>
            {
                player.Teleport(spawn.Position);
                player.MovementContext.Rotation = new Vector2(spawn.Yaw, 0f);
            });

            // From here it is an ordinary revive: restore, god mode and the HUD timer, hands and movement given back,
            // then cooldown, all from the Redeploy section of the config.
            st.ReviveRequestedSource = (int)ReviveSource.Redeploy;
            st.IsBeingRevived = false;
            st.CurrentReviverId = string.Empty;
            st.ClearRedeployInput();
            Step(pid, "PostRevival", () => PostRevivalController.BeginPostRevival(player, pid, st, applyFinalize: true));
            st.ResyncCooldown = -1f;

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
                st.HasDropRest = false;
                st.DropDeclarerId = string.Empty;
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

        //====================[ Other Peers ]====================
        // The owner's copy of the swap, for this peer's copy of the player (never the local player).
        public static void ApplyFromPacket(RedeployPacket packet)
        {
            string pid = packet.playerId;
            if (string.IsNullOrEmpty(pid) || PlayerUtils.IsLocalPlayer(pid)) return;

            Player player = PlayerUtils.GetPlayerById(pid);
            var st = RMSession.GetPlayerState(pid);
            var spawn = new Vector3(packet.spawnX, packet.spawnY, packet.spawnZ);

            try
            {
                ReleaseBodyAttachments(pid, st);

                if (packet.hasCorpse && player != null) LeaveCorpse(player, packet);

                if (player != null)
                {
                    BodyRagdoll.ReleaseForRedeploy(player);
                    // The remote body would otherwise glide there between two position updates.
                    player.Teleport(spawn);
                }

                st.IsLimp = false;
                st.HasDropRest = false;
                st.DropDeclarerId = string.Empty;
                st.LivesRemaining = packet.livesRemaining;

                // The same finalize a Revived packet gets: Revived state, invulnerability timer, ghost flag cleared.
                RevivalController.FinalizeRevivalFromPacket(player, pid, string.Empty, ReviveSource.Redeploy);
                RevivalDebugLog.LogNetworkTrace($"[Redeploy] {pid} applied from packet (corpse={packet.hasCorpse}).");
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"[Redeploy] Applying the packet for {pid} failed: {ex}");
            }
        }

        // Builds the corpse from the owner's equipment, posed as this peer's own copy of the body lies.
        private static void LeaveCorpse(Player player, RedeployPacket packet)
        {
            var equipment = packet.equipment as InventoryEquipment;
            if (equipment == null)
            {
                Plugin.LogSource.LogWarning($"[Redeploy] Corpse for {player.ProfileId} arrived without equipment; none made here.");
                return;
            }

            // The owner moved the gear off the body, so this copy loses it whether or not a corpse can be drawn here.
            RedeployCorpse.DropGear(player);

            var bones = RedeployCorpse.CaptureBones(player);
            if (!RedeployCorpse.FitsRig(player, bones)) bones = FikaBridge.FloatsToBones(packet.bones);
            if (!RedeployCorpse.FitsRig(player, bones))
            {
                Plugin.LogSource.LogWarning($"[Redeploy] No usable body pose for {player.ProfileId}'s corpse; none made here.");
                return;
            }

            RedeployCorpse.Spawn(player, equipment, bones);
        }

        // Anything a teammate has going on this body ends with it.
        private static void ReleaseBodyAttachments(string pid, RMPlayer st)
        {
            if (st.IsBeingDragged)
            {
                string draggerId = st.CurrentDraggerId;
                var dragger = PlayerUtils.GetPlayerById(draggerId);
                if (PlayerUtils.IsLocalPlayer(dragger)) DownedDragController.StopDrag(dragger, "redeployed", notify: true);
                else DownedDragController.ApplyDragState(pid, draggerId, false);
            }

            TeamHealWheel.CloseFor(pid, "redeployed");
            DownedLootGuard.End();
        }
    }
}
