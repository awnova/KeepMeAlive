//====================[ Imports ]====================
using EFT;
using EFT.InventoryLogic;
using KeepMeAlive.Helpers;

namespace KeepMeAlive.Features
{
    //====================[ DownedLootGuard ]====================
    // Restricts what the LOCAL looter may take from a downed (still alive) teammate.
    //
    // Enforced on the looter's own controller. Fika replays each player's operations through
    // that player's controller, keeping the revivee's synced operations separate.
    //
    // Loose loot inside grids (rig, backpack, pocket contents) stays lootable. Anything sitting
    // in a slot of the revivee's equipment tree (worn gear, weapons and their attachments/mags,
    // armor plates, special slots) and the revive item itself are protected.
    internal static class DownedLootGuard
    {
        private static ItemController _looterController;
        private static Player _revivee;

        public static void Begin(Player looter, Player revivee)
        {
            _looterController = looter?.InventoryController;
            _revivee = revivee;
        }

        public static void End()
        {
            _looterController = null;
            _revivee = null;
        }

        public static bool IsBlocked(ItemController actor, IOperationResult result)
        {
            if (_revivee == null || actor == null || result == null || !ReferenceEquals(actor, _looterController)) return false;

            var equipment = _revivee.Equipment;
            if (equipment == null) return false;

            switch (result)
            {
                case SwapResult swap:
                    return IsProtected(swap.Item, equipment) || IsProtected(swap.Item2, equipment)
                        || IsProtectedDestination(swap.To, equipment) || IsProtectedDestination(swap.To2, equipment);
                case MoveResult move:
                    return IsProtected(move.Item, equipment) || IsProtectedDestination(move.To, equipment);
                case IItemOperationResult itemResult:
                    return IsProtected(itemResult.Item, equipment);
                default:
                    return false;
            }
        }

        private static bool IsProtected(Item item, Item equipment)
        {
            if (item == null || item.GetRootItem() != equipment) return false;
            if (item.CurrentAddress is SlotItemAddress) return true;

            string reviveTpl = SyncedServerConfigStore.Config.RevivalItem.TemplateId;
            return !string.IsNullOrEmpty(reviveTpl) && string.Equals(item.StringTemplateId, reviveTpl, System.StringComparison.OrdinalIgnoreCase);
        }

        // Handles transactions that place items into the revivee's worn slots.
        private static bool IsProtectedDestination(ItemAddress address, Item equipment) =>
            address is SlotItemAddress && address.GetRootItem() == equipment;
    }
}
