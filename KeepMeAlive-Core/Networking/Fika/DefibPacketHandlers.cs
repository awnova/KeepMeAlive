//====================[ Imports ]====================
using EFT;
using EFT.InventoryLogic;
using Fika.Core.Networking;
using Fika.Core.Networking.LiteNetLib;

namespace KeepMeAlive.Networking.Fika
{
    //====================[ DefibPacketHandlers ]====================
    // Receiver for the defib use counter, which Fika's inventory sync does not carry.
    internal static class DefibPacketHandlers
    {
        //====================[ Registration ]====================
        public static void Register(IFikaNetworkManager manager)
        {
            manager.RegisterPacket<DefibUsesPacket, NetPeer>(OnDefibUses);
        }

        //====================[ DefibUses ]====================
        private static void OnDefibUses(DefibUsesPacket packet, NetPeer peer)
        {
            RevivalDebugLog.LogNetworkTrace($"[Packet] DefibUses: {packet.ownerId} item {packet.itemId} usesLeft={packet.usesLeft:F0}");

            // The owner already holds the new value.
            Player owner = ModUtils.GetPlayerById(packet.ownerId);
            if (owner == null || owner.IsYourPlayer) return;

            var items = owner.Inventory?.AllRealPlayerItems;
            if (items == null) return;

            foreach (Item item in items)
            {
                if (item == null || !string.Equals(item.Id, packet.itemId, System.StringComparison.Ordinal)) continue;

                var resource = item.GetItemComponent<ResourceComponent>();
                if (resource == null) return;

                // Uses only go down, so a late or repeated packet can never put a use back.
                if (packet.usesLeft < resource.Value)
                {
                    resource.Value = packet.usesLeft;
                    item.RaiseRefreshEvent();
                }
                return;
            }

            // The item is not in the observed copy of that inventory (yet); nothing to correct.
            RevivalDebugLog.LogNetworkTrace($"[Packet] DefibUses: item {packet.itemId} not found on {packet.ownerId}");
        }
    }
}
