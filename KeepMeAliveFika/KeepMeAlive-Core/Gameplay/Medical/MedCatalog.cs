//====================[ Imports ]====================
using System;
using System.Collections.Generic;
using EFT;
using EFT.HealthSystem;
using EFT.InventoryLogic;
using UnityEngine;

namespace KeepMeAlive.Gameplay.Medical
{
    //====================[ MedOffer ]====================
    // One item type the healer can give: the copy that would be used (the one with the most left, so a nearly empty kit
    // found first never hides a full one), how many usable copies they carry, and the categories it treats.
    internal sealed class MedOffer
    {
        public Item Item;
        public int Count;
        public List<MedCategory> Categories;
    }

    //====================[ MedAmount ]====================
    // How much the offered copy has left, as the wheel shows it: a medkit's resource (HP for a Health item, uses for the
    // rest), a food or drink's portion, or a single use.
    internal enum MedAmountUnit { Hp, Uses, Portion, Single }

    internal readonly struct MedAmount
    {
        public readonly int Current, Max;
        public readonly MedAmountUnit Unit;

        public MedAmount(int current, int max, MedAmountUnit unit)
        {
            Current = current;
            Max = max;
            Unit = unit;
        }
    }

    //====================[ MedCatalog ]====================
    // What a healer carries for a teammate, from item templates alone. Every med list (the wheel's rings, whether Interact
    // is offered) comes from one inventory pass (Offers), so a category never opens onto an empty list.
    internal static class MedCatalog
    {
        private static SyncedGameplayConfig Cfg => SyncedServerConfigStore.Config.Gameplay;

        //====================[ Offers ]====================
        // Each med or food type in the healer's inventory that maps to a category and has a copy left that isn't empty, in
        // use or excluded (sent and not yet confirmed: TeamMedical's reservations). Inventory order is kept.
        public static List<MedOffer> Offers(Player healer, Func<Item, bool> excluded)
        {
            var result = new List<MedOffer>();
            try
            {
                var items = healer?.Inventory?.AllRealPlayerItems;
                if (items == null) return result;

                var byTemplate = new Dictionary<string, MedOffer>();
                foreach (var item in items)
                {
                    if (item is not Meds && item is not FoodDrink) continue;
                    if (IsEmpty(item) || InventoryUtils.IsItemInUse(healer, item) || (excluded != null && excluded(item))) continue;

                    if (byTemplate.TryGetValue(item.StringTemplateId, out var offer))
                    {
                        offer.Count += Mathf.Max(1, item.StackObjectsCount);
                        if (Remaining(item) > Remaining(offer.Item)) offer.Item = item;
                        continue;
                    }

                    var categories = Classify(item);
                    if (categories.Count == 0) continue;

                    offer = new MedOffer { Item = item, Count = Mathf.Max(1, item.StackObjectsCount), Categories = categories };
                    byTemplate[item.StringTemplateId] = offer;
                    result.Add(offer);
                }
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"[MedCatalog] Offers error: {ex.Message}");
            }
            return result;
        }

        // The offers for one category that treat what the patient has: Health also needs the configured minimum resource,
        // and Bleeds / Breaks drop items that treat only the condition the patient doesn't have.
        public static List<MedOffer> ForCategory(List<MedOffer> offers, MedCategory category, PatientConditions conditions)
        {
            var result = new List<MedOffer>();
            foreach (var offer in offers)
            {
                if (!offer.Categories.Contains(category)) continue;
                if (category == MedCategory.Health && !MeetsHealthThreshold(offer.Item)) continue;

                var dmg = (offer.Item.Template as IHealthEffectsComponentTemplate)?.DamageEffects;
                if (dmg != null)
                {
                    if (category == MedCategory.Bleeds &&
                        !TreatsSoleCondition(conditions.LightBleed, conditions.HeavyBleed,
                            dmg.ContainsKey(EDamageEffectType.LightBleeding), dmg.ContainsKey(EDamageEffectType.HeavyBleeding)))
                        continue;

                    if (category == MedCategory.Breaks &&
                        !TreatsSoleCondition(conditions.Fracture, conditions.Destroyed,
                            dmg.ContainsKey(EDamageEffectType.Fracture), dmg.ContainsKey(EDamageEffectType.DestroyedPart)))
                        continue;
                }

                result.Add(offer);
            }
            return result;
        }

        //====================[ Item State ]====================
        // An item by instance id anywhere in a player's inventory; works for local and observed players.
        public static Item Find(Player player, string itemId) =>
            InventoryUtils.FindItemById(player, itemId);

        public static bool IsEmpty(Item item) =>
            InventoryUtils.IsMedOrFoodDepleted(item);

        public static MedAmount Amount(Item item, List<MedCategory> categories)
        {
            var kit = item.GetItemComponent<MedKitComponent>();
            if (kit != null && kit.MaxHpResource > 0)
            {
                var unit = categories.Contains(MedCategory.Health) ? MedAmountUnit.Hp : MedAmountUnit.Uses;
                return new MedAmount(Mathf.RoundToInt(kit.HpResource), kit.MaxHpResource, unit);
            }

            var food = item.GetItemComponent<FoodDrinkComponent>();
            if (food != null && food.MaxResource > 0f)
                return new MedAmount(Mathf.RoundToInt(food.HpPercent), Mathf.RoundToInt(food.MaxResource), MedAmountUnit.Portion);

            return new MedAmount(1, 1, MedAmountUnit.Single);
        }

        private static float Remaining(Item item)
        {
            var kit = item.GetItemComponent<MedKitComponent>();
            if (kit != null) return kit.HpResource;
            var food = item.GetItemComponent<FoodDrinkComponent>();
            if (food != null) return food.HpPercent;
            return item.StackObjectsCount;
        }

        //====================[ Classification ]====================
        // Every category the item's template treats.
        private static List<MedCategory> Classify(Item item)
        {
            var categories = new List<MedCategory>();
            try
            {
                if (item?.Template is not IHealthEffectsComponentTemplate template) return categories;

                var dmg = template.DamageEffects;
                var hp = template.HealthEffects;

                // Food and drinks are always (and only through this) Nutrition.
                if (item is FoodDrink)
                    categories.Add(MedCategory.Nutrition);

                if (dmg != null && (dmg.ContainsKey(EDamageEffectType.LightBleeding) ||
                                    dmg.ContainsKey(EDamageEffectType.HeavyBleeding)))
                    categories.Add(MedCategory.Bleeds);

                if (dmg != null && (dmg.ContainsKey(EDamageEffectType.Fracture) ||
                                    dmg.ContainsKey(EDamageEffectType.DestroyedPart)))
                    categories.Add(MedCategory.Breaks);

                if ((dmg != null && (dmg.ContainsKey(EDamageEffectType.Pain) ||
                                     dmg.ContainsKey(EDamageEffectType.Contusion) ||
                                     dmg.ContainsKey(EDamageEffectType.Intoxication) ||
                                     dmg.ContainsKey(EDamageEffectType.LethalIntoxication))) ||
                    (hp != null && hp.ContainsKey(EHealthFactorType.Poisoning)))
                    categories.Add(MedCategory.Comfort);

                // Restores HP via a medkit resource pool, a direct HP effect, or a positive HealthRate stim buff
                // (e.g. Propital, which has no MedKitComponent).
                if ((item.Template is MedsTemplate medsTemplate && medsTemplate.MaxHpResource > 0) ||
                    (hp != null && hp.TryGetValue(EHealthFactorType.Health, out var hpEffect) &&
                     hpEffect != null && hpEffect.Value > 0) ||
                    HasHealthRateBuff(item))
                    categories.Add(MedCategory.Health);
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"[MedCatalog] Classify error: {ex.Message}");
            }
            return categories;
        }

        private static bool HasHealthRateBuff(Item item)
        {
            var stimBuffs = item.GetItemComponent<StimulatorBuffsComponent>();
            if (stimBuffs == null) return false;
            foreach (var buff in stimBuffs.BuffSettings)
            {
                if (buff.BuffType == EStimulatorBuffType.HealthRate && buff.Value > 0) return true;
            }
            return false;
        }

        // Health: medkits below the configured HP-resource minimum are hidden, unless the item also has a positive HealthRate
        // stim buff (a modded Salewa with low HpResource plus regeneration must still show when nearly depleted).
        private static bool MeetsHealthThreshold(Item item)
        {
            var kit = item.GetItemComponent<MedKitComponent>();
            if (kit == null) return true;
            float minHp = Mathf.Max(Cfg.TeamHealing.MinHpResourceToDisplay, float.Epsilon);
            return kit.HpResource >= minHp || HasHealthRateBuff(item);
        }

        // For a two-condition category, keep an item unless the patient has only one condition and the item can't treat it.
        private static bool TreatsSoleCondition(bool hasA, bool hasB, bool treatsA, bool treatsB)
            => !(hasA && !hasB && !treatsA) && !(hasB && !hasA && !treatsB);
    }
}
