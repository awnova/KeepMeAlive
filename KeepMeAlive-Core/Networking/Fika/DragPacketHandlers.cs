//====================[ Imports ]====================
using Fika.Core.Networking;
using Fika.Core.Networking.LiteNetLib;

namespace KeepMeAlive.Networking.Fika
{
    //====================[ DragPacketHandlers ]====================
    // Receives drag claims and updates the shared player state.
    internal static class DragPacketHandlers
    {
        public static void Register(IFikaNetworkManager manager)
        {
            manager.RegisterPacket<DragStatePacket, NetPeer>(OnDragState);
            manager.RegisterPacket<DragHeadingPacket, NetPeer>(OnDragHeading);
            manager.RegisterPacket<DropRestPacket, NetPeer>(OnDropRest);
        }

        private static void OnDropRest(DropRestPacket packet, NetPeer peer)
        {
            RevivalDebugLog.LogNetworkTrace($"[Packet] DropRest: {packet.reviveeId} lies at ({packet.x:F2}, {packet.z:F2}) spine {packet.spineYaw:F0} " +
                $"{(packet.onBack ? "on its back" : "on its front")} (from {packet.declarerId})");
            DownedDragController.ApplyDropRest(packet.reviveeId, packet.declarerId, new UnityEngine.Vector3(packet.x, 0f, packet.z), packet.spineYaw, packet.onBack);
        }

        private static void OnDragHeading(DragHeadingPacket packet, NetPeer peer)
        {
            var local = ModUtils.GetYourPlayer();
            if (local != null && local.ProfileId == packet.draggerId) return;
            DragSession.OnRemoteHeading(packet.draggerId, packet.yaw);
        }

        private static void OnDragState(DragStatePacket packet, NetPeer peer)
        {
            bool applied = DownedDragController.ApplyDragState(packet.reviveeId, packet.draggerId, packet.active);
            RevivalDebugLog.LogNetworkTrace(
                $"[Packet] Drag: {packet.draggerId} {(packet.active ? "started dragging" : "released")} {packet.reviveeId} (applied={applied})");

            // Let go: wait for whoever let go to say where it came to rest (unless someone has picked it up since).
            if (!packet.active) DownedDragController.ExpectDropRest(packet.reviveeId, packet.draggerId, packet.declarerId);

            // The revivee's owner settles competing drag claims.
            var local = ModUtils.GetYourPlayer();
            if (local == null || local.ProfileId != packet.reviveeId) return;

            // A refused claim is released straight away: a release is not held back by ClaimSettleTime the way
            // a resync is, so the refused dragger and everyone who recorded them let go now.
            if (packet.active && !applied)
                FikaBridge.SendDragStatePacket(packet.reviveeId, packet.draggerId, false);
            DownedStateController.RequestClaimResync(RMSession.GetPlayerState(packet.reviveeId));
        }
    }
}
