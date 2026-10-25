//====================[ Imports ]====================
using System;
using System.Collections.Generic;
using Comfort.Common;
using EFT;
using Fika.Core.Main.Utils;
using Fika.Core.Networking;
using Fika.Core.Networking.LiteNetLib;
using Fika.Core.Networking.LiteNetLib.Utils;
using FikaCoopHandler = global::Fika.Core.Main.Components.CoopHandler;
using FikaFreeCamera = global::Fika.Core.Main.FreeCamera.FreeCamera;
using FikaObservedPlayer = global::Fika.Core.Main.Players.ObservedPlayer;
using FikaPlayer = global::Fika.Core.Main.Players.FikaPlayer;

namespace KeepMeAlive.Networking.Fika
{
    // The only Fika API surface used by the rest of the mod for state queries, peer/spectate
    //====================[ FikaBridge ]====================
    // lookups, and outbound packets. Every Send* method here logs a network trace and pushes
    // the packet over the wire in one step - there is no separate pass-through layer.
    //
    // Note on `broadcast: true`: on a client this only sends to the host (FikaClient.SendData);
    // the host then relays to every other peer except the original sender (FikaServer.OnNetworkReceive).
    // A sender never receives its own packet back, so every Send* caller is responsible for
    // applying its own state locally before or after calling here - see each call site.
    internal static class FikaBridge
    {
        private static readonly Comparison<Player> ByNetId =
        //====================[ Network Utilities ]====================
            (a, b) => ((FikaPlayer)a).NetId.CompareTo(((FikaPlayer)b).NetId);

        public static bool IAmHost()
        {
            return FikaBackendUtils.IsServer;
        }

        public static bool IsHeadless()
        //====================[ Send Helper ]====================
        {
            return FikaBackendUtils.IsHeadless;
        }

        public static bool IsScav()
        {
            return FikaBackendUtils.IsScav;
        }

        public static bool IsObservedPeer(Player player) => player is FikaObservedPlayer;

        public static bool IsFikaBuiltInReviveEnabled() =>
            global::Fika.Core.FikaPlugin.Instance.Settings.ReviveConfig.Enabled;

        // True while Fika's free/spectate camera owns the view. I leave it alone when extracted or dead.
        public static bool IsFikaCamActive()
        {
            if (!ModUtils.TryGetMainCamera(out var cam)) return false;
            var fika = cam.GetComponent<FikaFreeCamera>();
            return fika != null && fika.IsActive;
        }

        // Populates `into` with all human players tracked by Fika's CoopHandler.
        // Returns false if CoopHandler is not available yet.
        public static bool TryGetHumanPlayers(List<Player> into)
        {
            into.Clear();
            if (!FikaCoopHandler.TryGetCoopHandler(out var handler) || handler.HumanPlayers == null)
            {
                return false;
            }

            var humans = handler.HumanPlayers;
            for (int i = 0; i < humans.Count; i++)
            {
                if (humans[i] != null)
                {
                    into.Add(humans[i]);
                }
            }

            return true;
        }

        // Living, non-extracted human teammates other than the local player, in a stable (NetId) order.
        public static void GetSpectateTargets(List<Player> into)
        {
            into.Clear();
            if (!FikaCoopHandler.TryGetCoopHandler(out var handler) || handler.HumanPlayers == null) return;

            var extracted = handler.ExtractedPlayers;
            var humans = handler.HumanPlayers;
            for (int i = 0; i < humans.Count; i++)
            {
                var p = humans[i];
                if (p == null || p.IsYourPlayer || p.PlayerBones == null) continue;
                if (!PlayerUtils.IsAlive(p)) continue;
                if (extracted != null && extracted.Contains(p.NetId)) continue;
                into.Add(p);
            }
            into.Sort(ByNetId);
        }

        private static void SendPacket<T>(ref T packet, DeliveryMethod delivery = DeliveryMethod.ReliableOrdered) where T : struct, INetSerializable
        {
            if (!Singleton<IFikaNetworkManager>.Instantiated)
            {
                return;
            }

            try
            {
                bool broadcast = true;
                Singleton<IFikaNetworkManager>.Instance.SendData(ref packet, delivery, broadcast);
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError(ex);
            }
        }

        //====================[ Revival Packet Senders ]====================
        // st carries how the body went down (DownedRagdollController.Begin sets it first).
        public static void SendBleedingOutPacket(string playerId, float timeRemaining, int livesRemaining, RMPlayer st)
        {
            RevivalDebugLog.LogNetworkTrace($"Sending bleeding out packet for {playerId}");
            BleedingOutPacket packet = new()
            {
                playerId = playerId,
                timeRemaining = timeRemaining,
                livesRemaining = livesRemaining,
                fallKind = st.DownFallKind,
                pelvisX = st.DownPelvis.x,
                pelvisY = st.DownPelvis.y,
                pelvisZ = st.DownPelvis.z,
                yaw = st.DownYaw,
                offsetX = st.LyingOffset.x,
                offsetZ = st.LyingOffset.y
            };
            SendPacket(ref packet);
        }

        // When the local player helps someone downed, I record the claim here; Fika never echoes it to the owner.
        // own packet back, then everyone else is told. Always use these two rather than the raw packets, or this
        // machine never learns of its own claim.
        public static void ClaimTeamHelp(string reviveeId, string reviverId)
        {
            RevivePacketHandlers.ApplyTeamHelp(reviveeId, RMSession.GetPlayerState(reviveeId), reviverId);
            SendTeamHelpPacket(reviveeId, reviverId);
        }

        // The local player stops helping (cancelled, interrupted, or no longer possible): released here too, then
        // everyone else is told.
        public static void ReleaseTeamHelp(string reviveeId, string reviverId)
        {
            RevivePacketHandlers.ApplyTeamCancel(reviveeId, RMSession.GetPlayerState(reviveeId), reviverId);
            SendTeamCancelPacket(reviveeId, reviverId);
        }

        private static void SendTeamHelpPacket(string reviveeId, string reviverId)
        {
            RevivalDebugLog.LogNetworkTrace($"Sending team help packet: {reviverId} helping {reviveeId}");
            TeamHelpPacket packet = new() { reviveeId = reviveeId, reviverId = reviverId };
            SendPacket(ref packet);
        }

        private static void SendTeamCancelPacket(string reviveeId, string reviverId)
        {
            RevivalDebugLog.LogNetworkTrace($"Sending team cancel packet: {reviverId} cancelled helping {reviveeId}");
            TeamCancelPacket packet = new() { reviveeId = reviveeId, reviverId = reviverId };
            SendPacket(ref packet);
        }

        public static void SendSelfStimPacket(string playerId, float holdSeconds, float yaw)
        {
            RevivalDebugLog.LogNetworkTrace($"Sending self stim packet for {playerId}: hold {holdSeconds:F1}s, yaw {yaw:F0}");
            SelfStimPacket packet = new() { playerId = playerId, holdSeconds = holdSeconds, yaw = yaw };
            SendPacket(ref packet);
        }

        public static void SendSelfStimCancelPacket(string playerId)
        {
            RevivalDebugLog.LogNetworkTrace($"Sending self stim cancel packet for {playerId}");
            SelfStimCancelPacket packet = new() { playerId = playerId };
            SendPacket(ref packet);
        }

        public static void SendSelfReviveStartPacket(string playerId)
        {
            RevivalDebugLog.LogNetworkTrace($"Sending self revive start packet for {playerId}");
            SelfReviveStartPacket packet = new() { playerId = playerId };
            SendPacket(ref packet);
        }

        public static void SendTeamReviveStartPacket(string reviveeId, string reviverId, ReviveSource source)
        {
            RevivalDebugLog.LogNetworkTrace($"Sending team revive start packet: {reviverId} reviving {reviveeId}");
            TeamReviveStartPacket packet = new() { reviveeId = reviveeId, reviverId = reviverId, source = (int)source };
            SendPacket(ref packet);
        }

        public static void SendRevivedPacket(string playerId, string reviverId, ReviveSource source)
        {
            RevivalDebugLog.LogNetworkTrace($"Sending revived packet for {playerId}");
            RevivedPacket packet = new() { playerId = playerId, reviverId = reviverId, source = (int)source };
            SendPacket(ref packet);
        }

        public static void SendPlayerStateResetPacket(string playerId, bool isDead, float cooldownSeconds = 0f)
        {
            RevivalDebugLog.LogNetworkTrace($"Sending state reset packet for {playerId} (isDead={isDead}, cooldown={cooldownSeconds:F0}s)");
            PlayerStateResetPacket packet = new() { playerId = playerId, isDead = isDead, cooldownSeconds = cooldownSeconds };
            SendPacket(ref packet);
        }

        //====================[ Periodic State Resync ]====================
        // Broadcasts local player's full revival state to all peers. Called periodically while active, and immediately on transitions.
        public static void SendPlayerStateResyncPacket(string playerId, RMPlayer st)
        {
            PlayerStateResyncPacket packet = new()
            {
                playerId              = playerId,
                state                 = (int)st.State,
                criticalTimer         = st.CriticalTimer,
                invulTimer            = st.InvulnerabilityTimer,
                cooldownTimer         = st.CooldownTimer,
                reviverId             = st.CurrentReviverId ?? "",
                reviveRequestedSource = st.ReviveRequestedSource,
                livesRemaining        = st.LivesRemaining,
                draggerId             = st.CurrentDraggerId ?? "",
                limp                  = st.IsLimp,
                hasLyingOffset        = st.HasLyingOffset,
                lyingOffsetX          = st.LyingOffset.x,
                lyingOffsetZ          = st.LyingOffset.y,
                hasDropRest           = st.HasDropRest,
                dropX                 = st.DropRestPelvis.x,
                dropZ                 = st.DropRestPelvis.z,
                dropSpineYaw          = st.DropSpineYaw,
                dropOnBack            = st.DropOnBack,
                selfReviving          = st.IsSelfReviving
            };
            SendPacket(ref packet);
        }

        public static void SendGetUpPacket(string playerId, float yaw)
        {
            RevivalDebugLog.LogNetworkTrace($"Sending get-up packet for {playerId} yaw {yaw:F0}");
            GetUpPacket packet = new() { playerId = playerId, yaw = yaw };
            SendPacket(ref packet);
        }

        //====================[ Drag Packet Senders ]====================
        // declarerId: on a release of a lifted body, who will say where it comes to rest (empty: nobody will).
        public static void SendDragStatePacket(string reviveeId, string draggerId, bool active, string declarerId = "")
        {
            RevivalDebugLog.LogNetworkTrace($"Sending drag packet: {draggerId} {(active ? "dragging" : "released")} {reviveeId}" +
                (string.IsNullOrEmpty(declarerId) ? "" : $", rest to come from {declarerId}"));
            DragStatePacket packet = new() { reviveeId = reviveeId, draggerId = draggerId, active = active, declarerId = declarerId };
            SendPacket(ref packet);
        }

        // Where a body let go of came to rest, as the declarer's own ragdoll of it settled.
        public static void SendDropRestPacket(string reviveeId, string declarerId, UnityEngine.Vector3 pelvis, float spineYaw, bool onBack)
        {
            RevivalDebugLog.LogNetworkTrace($"Sending drop rest: {reviveeId} lies at {pelvis} spine {spineYaw:F0} {(onBack ? "on its back" : "on its front")} (from {declarerId})");
            DropRestPacket packet = new() { reviveeId = reviveeId, declarerId = declarerId, x = pelvis.x, z = pelvis.z, spineYaw = spineYaw, onBack = onBack };
            SendPacket(ref packet);
        }

        // Several times a second while dragging; only the latest matters.
        public static void SendDragHeadingPacket(string draggerId, float yaw)
        {
            DragHeadingPacket packet = new() { draggerId = draggerId, yaw = yaw };
            SendPacket(ref packet, DeliveryMethod.Sequenced);
        }

        //====================[ Team Healing Packet Senders ]====================
        public static void SendTeamHealPacket(string patientId, string healerId, string itemId)
        {
            RevivalDebugLog.LogNetworkTrace($"Sending team heal packet: {healerId} healing {patientId} with {itemId}");
            TeamHealPacket packet = new() { patientId = patientId, healerId = healerId, itemId = itemId };
            SendPacket(ref packet);
        }

        public static void SendTeamHealResultPacket(string patientId, string healerId, string itemId, bool success, string reason, float useSeconds)
        {
            RevivalDebugLog.LogNetworkTrace($"Sending team heal result: {patientId} -> {healerId} item {itemId} success={success} use={useSeconds:F2}s");
            TeamHealResultPacket packet = new() { patientId = patientId, healerId = healerId, itemId = itemId, success = success, reason = reason, useSeconds = useSeconds };
            SendPacket(ref packet);
        }

        public static void SendDefibUsesPacket(string ownerId, string itemId, float usesLeft)
        {
            RevivalDebugLog.LogNetworkTrace($"Sending defib uses: {ownerId} item {itemId} usesLeft={usesLeft:F0}");
            DefibUsesPacket packet = new() { ownerId = ownerId, itemId = itemId, usesLeft = usesLeft };
            SendPacket(ref packet);
        }

        public static void SendTeamHealUseEndedPacket(string patientId, string healerId, string itemId, bool completed)
        {
            RevivalDebugLog.LogNetworkTrace($"Sending team heal use ended: {patientId} -> {healerId} item {itemId} completed={completed}");
            TeamHealUseEndedPacket packet = new() { patientId = patientId, healerId = healerId, itemId = itemId, completed = completed };
            SendPacket(ref packet);
        }
    }
}
