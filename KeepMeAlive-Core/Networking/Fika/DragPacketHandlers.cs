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
        }

        private static void OnDragState(DragStatePacket packet, NetPeer peer)
        {
            bool applied = DownedDragController.ApplyDragState(packet.reviveeId, packet.draggerId, packet.active);
            RevivalDebugLog.LogNetworkTrace(
                $"[Packet] Drag: {packet.draggerId} {(packet.active ? "started dragging" : "released")} {packet.reviveeId} (applied={applied})");

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
