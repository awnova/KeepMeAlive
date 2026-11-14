//====================[ Imports ]====================
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Comfort.Common;
using EFT;
using EFT.AssetsManager;
using EFT.Interactive;
using EFT.InventoryLogic;
using JsonType;
using UnityEngine;

namespace KeepMeAlive.Gameplay.Revival
{
    //====================[ RedeployCorpse ]====================
    // The gear a redeploying player leaves behind: the body pose, the equipment the corpse holds, and the corpse itself.
    //
    // The player keeps what is in IsKeptSlot, anything the game will not let be looted off a body (IsUnlootable), and the
    // pockets item with its special slots; everything else (the pocket grids' contents included) moves
    // to a fresh equipment that becomes a real, lootable corpse.
    internal static class RedeployCorpse
    {
        //====================[ Moves ]====================
        // One thing moved to the corpse, so a failed spawn can put it all back.
        internal readonly struct SlotMove
        {
            public readonly Slot From, To;
            public readonly Item Item;

            public SlotMove(Slot from, Slot to, Item item)
            {
                From = from;
                To = to;
                Item = item;
            }
        }

        internal readonly struct GridMove
        {
            public readonly Grid From, To;
            public readonly Item Item;
            public readonly LocationInGrid Location;

            public GridMove(Grid from, Grid to, Item item, LocationInGrid location)
            {
                From = from;
                To = to;
                Item = item;
                Location = location;
            }
        }

        // What TakeGear moved, kept for Restore.
        internal sealed class MoveLog
        {
            internal readonly List<SlotMove> Slots = new();
            internal readonly List<GridMove> Grids = new();
            internal int Count => Slots.Count + Grids.Count;
        }

        //====================[ Rule ]====================
        // Slots the redeployed player keeps wearing or carrying. Pockets is handled on its own: the item stays, its contents go.
        public static bool IsKeptSlot(EquipmentSlot slot) =>
            slot is EquipmentSlot.SecuredContainer or EquipmentSlot.Scabbard or EquipmentSlot.ArmBand or EquipmentSlot.Dogtag
                or EquipmentSlot.Pockets;

        // A kept slot keeps its item, except an ArmBand that holds things: WTT PackNStrap puts its belts (searchable
        // containers with grids) in the vanilla ArmBand slot, and a belt is gear that goes to the corpse. A plain
        // armband holds nothing and stays.
        private static bool IsKept(EquipmentSlot slot, Item item, EPlayerSide side)
        {
            if (!IsKeptSlot(slot)) return IsUnlootable(item, slot.ToString(), side);
            if (slot != EquipmentSlot.ArmBand) return true;
            return !(item is CompoundItem container && ((container.Grids?.Length ?? 0) > 0 || (container.Slots?.Length ?? 0) > 0));
        }

        // The game's own body-loot lock (UnlootableComponent.IsUnlootableFrom): a template marked Unlootable from this
        // container for this side can never be taken off a corpse, so moving it there would lose it. The side is passed in
        // rather than read from the container's owner, so the owner and every peer's observed copy decide the same way.
        private static bool IsUnlootable(Item item, string containerId, EPlayerSide side)
        {
            var template = item?.GetItemComponent<UnlootableComponent>()?.Template;
            return template != null && containerId != null && containerId.Contains(template.SlotName) && template.Side.CheckSide(side);
        }

        //====================[ Gear ]====================
        // Moves the gear into a new equipment and returns it, or null when it could not be set up (nothing moved).
        public static InventoryEquipment TakeGear(Player player, out MoveLog log)
        {
            log = new MoveLog();
            try
            {
                var equipment = player.Equipment;
                var factory = Singleton<ItemFactory>.Instance;
                if (equipment == null || factory == null) return null;

                var corpseEquipment = factory.CreateItem(MongoID.Generate(), equipment.TemplateId, null) as InventoryEquipment;
                if (corpseEquipment == null) return null;

                MoveGear(equipment, corpseEquipment, factory, log, player.Side);
                return corpseEquipment;
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"[Redeploy] TakeGear failed: {ex}");
                Restore(log);
                return null;
            }
        }

        // Puts everything TakeGear moved back where it was (a failed corpse spawn).
        public static void Restore(MoveLog log)
        {
            if (log == null) return;

            for (int i = log.Grids.Count - 1; i >= 0; i--)
            {
                var move = log.Grids[i];
                try
                {
                    move.To.RemoveWithoutRestrictions(move.Item);
                    move.From.AddItemWithoutRestrictions(move.Item, move.Location);
                }
                catch (Exception ex) { Plugin.LogSource.LogError($"[Redeploy] Restore grid item failed: {ex.Message}"); }
            }

            for (int i = log.Slots.Count - 1; i >= 0; i--)
            {
                var move = log.Slots[i];
                try
                {
                    move.To.RemoveItemWithoutRestrictions();
                    move.From.AddWithoutRestrictions(move.Item);
                    move.To.ApplyContainedItem();
                    move.From.ApplyContainedItem();
                }
                catch (Exception ex) { Plugin.LogSource.LogError($"[Redeploy] Restore slot item failed: {ex.Message}"); }
            }

            log.Slots.Clear();
            log.Grids.Clear();
        }

        // Moves every slot that is not kept, then the pocket contents, logging each move for Restore.
        private static void MoveGear(InventoryEquipment equipment, InventoryEquipment target, ItemFactory factory, MoveLog log,
            EPlayerSide side)
        {
            foreach (var slotName in InventoryEquipment.AllSlotNames)
            {
                var from = equipment.GetSlot(slotName);
                var item = from?.ContainedItem;
                if (item == null || IsKept(slotName, item, side)) continue;

                var to = target?.GetSlot(slotName);
                var removed = from.RemoveItemWithoutRestrictions();
                if (removed.Failed)
                {
                    Plugin.LogSource.LogWarning($"[Redeploy] Could not take {item.TemplateId} from {slotName}: {removed.Error}");
                    continue;
                }

                if (to != null)
                {
                    var added = to.AddWithoutRestrictions(item);
                    if (added.Failed)
                    {
                        // Never lose an item: it stays where it was.
                        from.AddWithoutRestrictions(item);
                        from.ApplyContainedItem();
                        Plugin.LogSource.LogWarning($"[Redeploy] Could not give {slotName} to the corpse: {added.Error}");
                        continue;
                    }
                    log?.Slots.Add(new SlotMove(from, to, item));
                    to.ApplyContainedItem();
                }

                // The body's slot view follows the reactive value, not the raw container change.
                from.ApplyContainedItem();
            }

            MovePockets(equipment, target, factory, log, side);
        }

        // The pockets item stays on the player; what is in its grids goes. The corpse gets a matching empty pockets item
        // to hold it, each item in the same cell. Its special slots stay with the player: the game never lets anyone but
        // the owner take from a special slot (SlotItemAddress.IsSpecialSlotAddress), so on a corpse they could not be looted.
        private static void MovePockets(InventoryEquipment equipment, InventoryEquipment target, ItemFactory factory, MoveLog log,
            EPlayerSide side)
        {
            var pockets = equipment.GetSlot(EquipmentSlot.Pockets)?.ContainedItem as CompoundItem;
            if (pockets?.Grids == null) return;

            CompoundItem corpsePockets = null;
            if (target != null)
            {
                corpsePockets = factory.CreateItem(MongoID.Generate(), pockets.TemplateId, null) as CompoundItem;
                var corpsePocketsSlot = target.GetSlot(EquipmentSlot.Pockets);
                if (corpsePockets == null || corpsePocketsSlot == null || corpsePocketsSlot.AddWithoutRestrictions(corpsePockets).Failed)
                {
                    Plugin.LogSource.LogWarning("[Redeploy] No pockets on the corpse; the pocket contents stay with the player.");
                    return;
                }
                corpsePocketsSlot.ApplyContainedItem();
            }

            for (int g = 0; g < pockets.Grids.Length; g++)
            {
                var source = pockets.Grids[g];
                var destination = corpsePockets != null && g < corpsePockets.Grids.Length ? corpsePockets.Grids[g] : null;
                if (target != null && destination == null) continue;

                foreach (var pair in source.ItemCollection.ToArray())
                {
                    var item = pair.Key;
                    var location = pair.Value;

                    if (IsUnlootable(item, source.ID, side)) continue;
                    if (source.RemoveWithoutRestrictions(item).Failed) continue;

                    if (destination != null)
                    {
                        if (destination.AddItemWithoutRestrictions(item, location).Failed)
                        {
                            source.AddItemWithoutRestrictions(item, location);
                            continue;
                        }
                        log?.Grids.Add(new GridMove(source, destination, item, location));
                    }
                }
            }
        }

        //====================[ After The Move ]====================
        // Raw container moves raise no inventory events, so the game never re-weighs the player: without this the
        // redeployed player keeps the weight, and the overweight slowdown, of the gear left on the corpse (the game
        // only re-weighs on its next item event).
        public static void RefreshWeight(Player player)
        {
            try
            {
                var inventory = player?.InventoryController?.Inventory;
                if (inventory == null) return;
                inventory.TotalWeight.SetDirty();
                inventory.TotalWeightEliteSkill.SetDirty();
                player.Physical?.OnWeightUpdated();
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"[Redeploy] Weight refresh failed: {ex.Message}");
            }
        }

        // The owner's own gear: the game's search controller still knows each item at its old address on the body, so
        // the moved items count as unknown there, and finding them again raises no "found" event (they were found
        // before), so a search would show the corpse's pockets empty. Uncovering the corpse records every item at
        // its new address and marks its containers searched, as the game does for the player's own equipment.
        public static void UncoverForOwner(Player player, InventoryEquipment corpseEquipment)
        {
            try
            {
                if (player?.SearchController is ActiveSearchController search && corpseEquipment != null)
                    search.UncoverContent(corpseEquipment);
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"[Redeploy] Uncovering the corpse failed: {ex.Message}");
            }
        }

        //====================[ Pose ]====================
        // Where each bone of the body is right now, in the order the game's own corpses use (the player prefab's rigidbody
        // spawners), so the corpse lies exactly as the body did. Null when the rig is unavailable.
        public static TransformSync[] CaptureBones(Player player)
        {
            var pool = player != null ? player.GetComponent<PlayerPoolObject>() : null;
            var spawners = pool?.RigidbodySpawners;
            if (spawners == null || spawners.Length == 0) return null;

            var bones = new TransformSync[spawners.Length];
            for (int i = 0; i < spawners.Length; i++)
            {
                var t = spawners[i].transform;
                bones[i] = new TransformSync { Position = t.position, Rotation = t.rotation };
            }
            return bones;
        }

        //====================[ Corpse ]====================
        // Spawns the corpse where the body lies, through the game's own corpse spawn.
        public static bool Spawn(Player player, InventoryEquipment equipment, TransformSync[] bones)
        {
            try
            {
                var world = Singleton<GameWorld>.Instance;
                if (world == null || equipment == null || bones == null) return false;

                var json = new JsonCorpse
                {
                    Id = equipment.Id,
                    Position = player.Transform.position,
                    Rotation = player.Transform.eulerAngles,
                    Item = equipment,
                    Customization = player.Profile.Customization,
                    Side = player.Side,
                    Bones = bones,
                    // The player keeps their own profile id; the corpse gets one of its own so nothing mistakes it for them.
                    ProfileID = MongoID.Generate().ToString(),
                    IsZombieCorpse = false,
                    IsContainer = true,
                    PlatformId = -1
                };

                world.SpawnLootCorpse(json);
                StartSettle(FindCorpse(world, equipment.Id));
                return true;
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"[Redeploy] Corpse spawn failed: {ex}");
                return false;
            }
        }

        //====================[ Settle ]====================
        // The game's spawned corpses are "still": posed colliders with no rigidbodies, so a body that lay a little off the
        // ground stays frozen as it was. A redeploy corpse instead gets the game's own death ragdoll (CorpseRagdoll), which
        // simulates from the pose, puts itself to sleep and then strips its rigidbodies, exactly as a killed player's does.
        // No sync is needed: Fika doesn't sync its own corpses' settled poses either, and each peer settles its own copy.

        // Longest an observed corpse simulates before it is frozen where it is. The game's own cap is 15 s for a host corpse and
        // 30 s for an observed one; this caps the observed one (s).
        private const float SettleCapSeconds = 8f;
        // How long the bodies must all be asleep before an observed corpse is frozen early (s).
        private const float SettleAsleepSeconds = 0.5f;

        // SpawnLootCorpse returns nothing; the corpse just registered itself as loot under its equipment's id.
        private static Corpse FindCorpse(GameWorld world, string equipmentId)
        {
            var loot = world.LootList;
            for (int i = loot.Count - 1; i >= 0; i--)
                if (loot[i] is Corpse corpse && corpse != null && corpse.ItemId == equipmentId) return corpse;
            return null;
        }

        private static void StartSettle(Corpse corpse)
        {
            if (corpse == null)
            {
                Plugin.LogSource.LogWarning("[Redeploy] Spawned corpse not found; it stays as posed.");
                return;
            }
            Plugin.StaticCoroutineRunner.StartCoroutine(Settle(corpse));
        }

        private static IEnumerator Settle(Corpse corpse)
        {
            // One frame first: the body's own ragdoll contacts (Shells, which collides with Deadbody) are destroyed only at
            // the end of the frame the redeploy ran in, and the player is teleported away in it.
            yield return null;
            if (corpse == null || corpse.Ragdoll != null) yield break;

            try
            {
                corpse.HasRagdoll = true;
                corpse.TryToCreateRagdoll(false);
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"[Redeploy] Corpse ragdoll failed to start: {ex.Message}");
                yield break;
            }

            // A host corpse stops itself (asleep, or 15 s at most). An observed one waits 30 s for a Fika "done" packet
            // that never comes for this corpse, so it is watched and frozen here.
            if (corpse is not ObservedCorpse observed) yield break;
            var ragdoll = corpse.Ragdoll;
            var spawners = corpse.GetComponent<PlayerPoolObject>()?.RigidbodySpawners;
            if (ragdoll == null || spawners == null) yield break;

            float started = Time.time, asleepSince = -1f;
            while (observed != null && !ragdoll._isPhysicsDone)
            {
                float now = Time.time;
                if (AllAsleep(spawners)) { if (asleepSince < 0f) asleepSince = now; }
                else asleepSince = -1f;

                bool asleep = asleepSince >= 0f && now - asleepSince >= SettleAsleepSeconds && now - started >= 1f;
                if (now - started >= SettleCapSeconds || asleep)
                {
                    // The game's own way: ForceStill stops the bodies and ends the ragdoll's working cycle.
                    try { observed.ForceStill(); }
                    catch (Exception ex) { Plugin.LogSource.LogError($"[Redeploy] Corpse freeze failed: {ex.Message}"); }
                    yield break;
                }
                yield return null;
            }
        }

        private static bool AllAsleep(RigidbodySpawner[] spawners)
        {
            for (int i = 0; i < spawners.Length; i++)
            {
                var body = spawners[i].Rigidbody;
                if (body != null && !body.isKinematic && !body.IsSleeping()) return false;
            }
            return true;
        }
    }
}
