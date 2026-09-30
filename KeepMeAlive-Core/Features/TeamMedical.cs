//====================[ Imports ]====================
using System;
using System.Collections;
using System.Collections.Generic;
using EFT;
using EFT.HealthSystem;
using EFT.InventoryLogic;
using UnityEngine;
using KeepMeAlive.Helpers;
using KeepMeAlive.Components;
using KeepMeAlive.Fika;

namespace KeepMeAlive.Features
{
    //====================[ MedCategory ]====================
    // Logical groupings shown to the healer before they pick a specific item.
    public enum MedCategory
    {
        Bleeds,   // treats LightBleeding / HeavyBleeding
        Breaks,   // treats Fracture or DestroyedPart (splints, CMS, SURV12)
        Health,   // restores HP (medkit pool or direct HP effect)
        Comfort,  // relieves Pain / Contusion / Poisoning
        Nutrition // food and drinks only (restores Energy / Hydration)
    }

    //====================[ TeamMedical ]====================
    public static class TeamMedical
    {
        private static SyncedGameplayConfig Cfg => SyncedServerConfigStore.Config.Gameplay;

        //====================[ Constants & Fields ]====================
        private static float HEAL_HOLD_TIME => Cfg.TeamHealing.HoldSeconds;

        // How long a sent item stays reserved if the patient never answers.
        private const float ReservationTimeoutSeconds = 10f;

        // Items this healer has sent to a patient but whose use the patient hasn't confirmed yet.
        // Nothing on the healer's machine marks the item as in use until the patient's med effect
        // syncs back, so without this the same item could be offered (and sent) twice.
        private static readonly Dictionary<string, float> ReservedItemExpiry = new();

        //====================[ Reservations ]====================
        public static void ClearReservations() => ReservedItemExpiry.Clear();

        private static bool IsReserved(string itemId)
        {
            if (!ReservedItemExpiry.TryGetValue(itemId, out var expiry)) return false;
            if (Time.time < expiry) return true;

            ReservedItemExpiry.Remove(itemId);
            VFX_UI.Text(Color.yellow, PlayerFacingMessages.TeamHeal.HealNoResponse);
            return false;
        }

        // Called on the healer when the patient reports back.
        public static void OnHealResult(string itemId, bool success, string reason)
        {
            ReservedItemExpiry.Remove(itemId);
            if (success)
            {
                VFX_UI.Text(Color.green, PlayerFacingMessages.TeamHeal.HealSucceeded);
            }
            else
            {
                VFX_UI.Text(Color.yellow, string.IsNullOrEmpty(reason) ? PlayerFacingMessages.TeamHeal.PatientBusy : reason);
            }
        }

        //====================[ Incoming Team Heal ]====================
        // Applies an incoming TeamHealPacket on the patient's own machine. Called by
        // TeamHealPacketHandlers once the packet is confirmed to be about the local player.
        internal static IEnumerator ApplyIncomingTeamHeal(Player patient, string healerId, string itemId)
        {
            // Wait out any in-flight inventory operation on the patient (e.g. a revive equip still
            // being processed) before racing ApplyItem against it.
            yield return ModUtils.WaitForInventoryFree(patient, 2f, "TeamHealPacket");

            bool success = false;
            string reason = PlayerFacingMessages.TeamHeal.PatientBusy;
            try
            {
                // Resolve the item after the inventory wait.
                Player healer = ModUtils.GetPlayerById(healerId);
                Item item = FindItemInInventory(healer, itemId);

                if (patient == null || patient.HealthController == null || !patient.HealthController.IsAlive || RMSession.IsPlayerCritical(patient.ProfileId))
                {
                    reason = PlayerFacingMessages.TeamHeal.PatientUnavailable;
                }
                else if (!ModUtils.IsTeamHealItemValid(healer, item, out var invalidReason))
                {
                    reason = invalidReason;
                }
                else
                {
                    TeamHealUseTime.SetPending(item);
                    success = patient.HealthController.ApplyItem(item, EBodyPart.Common);
                    if (!success) TeamHealUseTime.Clear();
                }
            }
            catch (Exception ex)
            {
                TeamHealUseTime.Clear();
                Plugin.LogSource.LogError($"[Packet] TeamHeal apply error: {ex.Message}");
            }

            if (success)
            {
                VFX_UI.Text(Color.green, PlayerFacingMessages.TeamHeal.YouWereHealed);
            }

            if (patient != null)
            {
                FikaBridge.SendTeamHealResultPacket(patient.ProfileId, healerId, itemId, success, success ? string.Empty : reason);
            }
        }

        //====================[ Public API ]====================

        // Searches a player's inventory for an item with the given instance ID. Works for both local and remote players.
        public static Item FindItemInInventory(Player player, string itemId)
        {
            try
            {
                var items = player?.Inventory?.AllRealPlayerItems;
                if (items == null)
                {
                    return null;
                }

                foreach (var item in items)
                {
                    if (item?.Id == itemId)
                    {
                        return item;
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"[TeamMedical] FindItemInInventory error: {ex.Message}");
            }
            
            return null;
        }

        // The single inventory query behind every med list: each med/food item in the healer's
        // inventory that isn't reserved, in use or empty and maps to at least one category, paired
        // with those categories so callers never classify twice.
        // One entry per item type (e.g. one bandage even if healer carries three), keeping the
        // instance with the most resource left so a nearly-empty kit found first never hides a full one.
        // Classifies candidate items from their templates with ClassifyMed.
        private static List<(Item item, List<MedCategory> categories)> GetCandidateMeds(Player healer)
        {
            var result = new List<(Item item, List<MedCategory> categories)>();
            try
            {
                if (healer?.Inventory == null)
                {
                    return result;
                }

                var bestByTemplate = new Dictionary<string, int>();
                foreach (var item in healer.Inventory.AllRealPlayerItems)
                {
                    if (item is not Meds && item is not FoodDrink)
                    {
                        continue;
                    }

                    // Exclude reserved or currently used items.
                    if (IsReserved(item.Id) || ModUtils.IsItemInUse(healer, item))
                    {
                        continue;
                    }

                    // Exclude medkits with no remaining charges.
                    var kit = item.GetItemComponent<MedKitComponent>();
                    if (kit != null && kit.HpResource < float.Epsilon)
                        continue;

                    // Exclude items that do not map to a recognized category.
                    var categories = ClassifyMed(item);
                    if (categories.Count == 0)
                    {
                        continue;
                    }

                    string tpl = item.StringTemplateId;
                    if (bestByTemplate.TryGetValue(tpl, out int index))
                    {
                        if (RemainingResource(item) > RemainingResource(result[index].item)) result[index] = (item, categories);
                        continue;
                    }

                    bestByTemplate[tpl] = result.Count;
                    result.Add((item, categories));
                }
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"[TeamMedical] GetCandidateMeds error: {ex.Message}");
            }

            return result;
        }

        private static float RemainingResource(Item item)
        {
            var kit = item.GetItemComponent<MedKitComponent>();
            if (kit != null) return kit.HpResource;
            var food = item.GetItemComponent<FoodDrinkComponent>();
            if (food != null) return food.HpPercent;
            return item.StackObjectsCount;
        }

        //====================[ Category Helpers ]====================

        // Returns every MedCategory that applies to this item based on template effects.
        private static List<MedCategory> ClassifyMed(Item item)
        {
            var categories = new List<MedCategory>();
            try
            {
                if (item?.Template is not IHealthEffectsComponentTemplate template) return categories;

                var dmg = template.DamageEffects;
                var hp  = template.HealthEffects;

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

                // Restores HP via a medkit resource pool, a direct HP effect, or a positive
                // HealthRate stim buff (e.g. Propital — no MedKitComponent).
                if ((item.Template is MedsTemplate medsTemplate && medsTemplate.MaxHpResource > 0) ||
                    (hp != null && hp.TryGetValue(EHealthFactorType.Health, out var hpEffect) &&
                     hpEffect != null && hpEffect.Value > 0) ||
                    HasHealthRateBuff(item))
                    categories.Add(MedCategory.Health);
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"[TeamMedical] ClassifyMed error: {ex.Message}");
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

        // Health category: medkits below the configured HP-resource minimum are hidden, unless the
        // item also has a positive HealthRate stim buff. Items like a modded Salewa (low HpResource
        // + HealthRegeneration) must bypass the threshold so they still appear when nearly depleted.
        private static bool MeetsHealthThreshold(Item item)
        {
            var kit = item.GetItemComponent<MedKitComponent>();
            if (kit == null) return true;
            float minHp = Mathf.Max(Cfg.TeamHealing.MinHpResourceToDisplay, float.Epsilon);
            return kit.HpResource >= minHp || HasHealthRateBuff(item);
        }

        private static bool MatchesCategory(Item item, List<MedCategory> categories, MedCategory category)
            => categories.Contains(category) && (category != MedCategory.Health || MeetsHealthThreshold(item));

        // For two-condition categories, retain items that treat the patient's known condition.
        private static bool TreatsSoleCondition(bool hasA, bool hasB, bool treatsA, bool treatsB)
            => !(hasA && !hasB && !treatsA) && !(hasB && !hasA && !treatsB);

        // Returns usable meds for the given category only. Pass null to get all.
        public static IEnumerable<Item> GetUsableMedsByCategory(
            Player healer, Player patient, MedCategory? category)
        {
            if (patient == null) yield break;

            // For the Bleeds category, detect the patient's bleed types and select applicable items.
            bool patientHasLight = false, patientHasHeavy = false;
            if (category == MedCategory.Bleeds)
            {
                try
                {
                    var hc = patient.HealthController;
                    if (hc != null)
                    {
                        // ILightBleeding = LightBleeding active effect
                        // IHeavyBleeding = HeavyBleeding active effect
                        patientHasLight = hc.FindActiveEffect<ILightBleeding>(EBodyPart.Common) != null;
                        patientHasHeavy = hc.FindActiveEffect<IHeavyBleeding>(EBodyPart.Common) != null;
                    }
                    else { patientHasLight = patientHasHeavy = true; } // Treat both bleed types as possible.
                }
                catch { patientHasLight = patientHasHeavy = true; } // Treat both bleed types as possible.
            }

            // For the Breaks category, detect whether the patient has fractures, destroyed limbs,
            // Alu splint treats fractures; CMS/SURV-12 treat fractures and destroyed limbs.
            bool patientHasFracture = false, patientHasDestroyed = false;
            if (category == MedCategory.Breaks)
            {
                try
                {
                    var hc = patient.HealthController;
                    if (hc != null)
                    {
                        foreach (var part in _allBodyParts)
                        {
                            if (hc.IsBodyPartBroken(part))    patientHasFracture  = true;
                            if (hc.IsBodyPartDestroyed(part)) patientHasDestroyed = true;
                            if (patientHasFracture && patientHasDestroyed) break;
                        }
                    }
                    else { patientHasFracture = patientHasDestroyed = true; } // Treat both conditions as possible.
                }
                catch { patientHasFracture = patientHasDestroyed = true; } // Treat both conditions as possible.
            }

            foreach (var (med, categories) in GetCandidateMeds(healer))
            {
                if (category != null && !MatchesCategory(med, categories, category.Value))
                    continue;

                var dmg = (med.Template as IHealthEffectsComponentTemplate)?.DamageEffects;
                if (dmg != null)
                {
                    if (category == MedCategory.Bleeds &&
                        !TreatsSoleCondition(patientHasLight, patientHasHeavy,
                            dmg.ContainsKey(EDamageEffectType.LightBleeding),
                            dmg.ContainsKey(EDamageEffectType.HeavyBleeding)))
                        continue;

                    if (category == MedCategory.Breaks &&
                        !TreatsSoleCondition(patientHasFracture, patientHasDestroyed,
                            dmg.ContainsKey(EDamageEffectType.Fracture),
                            dmg.ContainsKey(EDamageEffectType.DestroyedPart)))
                        continue;
                }

                yield return med;
            }
        }

        // Template-only check: does the healer carry at least one available med that CAN treat
        // the given category — regardless of whether the patient currently needs it.
        // Used by BodyInteractable to decide which category buttons to render. Shares
        // GetCandidateMeds with the picker, so a button never opens onto an empty list.
        public static bool HealerHasMedForCategory(Player healer, MedCategory category)
        {
            foreach (var (item, categories) in GetCandidateMeds(healer))
            {
                if (MatchesCategory(item, categories, category)) return true;
            }
            return false;
        }

        // Checks if the patient currently has an active condition that the given category can treat.
        // Returns true when the health state cannot be determined.
        //
        //   Bleeds  → IBleeding (LightBleeding / HeavyBleeding)
        //   Breaks  → fracture on any part (IsBodyPartBroken) OR destroyed / blacked limb (IsBodyPartDestroyed)
        //   Health  → any non-destroyed body-part with HP below maximum (medkits can treat)
        //   Comfort → catch-all: Pain (IPain), Contusion (IContusion),
        //             Intoxication (IIntoxication), LethalIntoxication (ILethalIntoxication),
        //             Dehydration (IDehydration), Exhaustion (IExhaustion),
        //             or Hydration / Energy below 80 % of maximum.
        private static readonly EBodyPart[] _allBodyParts =
        {
            EBodyPart.Head, EBodyPart.Chest, EBodyPart.Stomach,
            EBodyPart.LeftArm, EBodyPart.RightArm,
            EBodyPart.LeftLeg, EBodyPart.RightLeg
        };

        public static bool PatientNeedsCategory(Player patient, MedCategory category)
        {
            try
            {
                if (patient?.HealthController == null) return true;
                var hc = patient.HealthController;

                switch (category)
                {
                    case MedCategory.Bleeds:
                        // IBleeding = LightBleeding | HeavyBleeding
                        return hc.FindActiveEffect<IBleeding>(EBodyPart.Common) != null;

                    case MedCategory.Breaks:
                        // Fracture (IsBodyPartBroken) OR destroyed/blacked limb (IsBodyPartDestroyed).
                        // Surgical kits (CMS / SURV-12) treat both conditions.
                        foreach (var part in _allBodyParts)
                        {
                            if (hc.IsBodyPartBroken(part) || hc.IsBodyPartDestroyed(part))
                                return true;
                        }
                        return false;

                    case MedCategory.Health:
                        // Any limb with HP damage that a medkit can restore.
                        // Destroyed (blacked) limbs require a surgical kit → Breaks, not Health.
                        foreach (var part in _allBodyParts)
                        {
                            if (hc.IsBodyPartDestroyed(part)) continue;
                            var h = hc.GetBodyPartHealth(part);
                            if (h.Current < h.Maximum - 0.5f) return true;
                        }
                        return false;

                    case MedCategory.Comfort:
                        // --- Active damage effects ---
                        // Pain (IPain)
                        if (hc.FindActiveEffect<IPain>(EBodyPart.Common) != null) return true;
                        // Contusion (IContusion)
                        if (hc.FindActiveEffect<IContusion>(EBodyPart.Common) != null) return true;
                        // Intoxication / Poisoning (IIntoxication)
                        if (hc.FindActiveEffect<IIntoxication>(EBodyPart.Common) != null) return true;
                        // Lethal Intoxication (ILethalIntoxication)
                        if (hc.FindActiveEffect<ILethalIntoxication>(EBodyPart.Common) != null) return true;
                        // Dehydration — critically low hydration actively dealing damage (IDehydration)
                        if (hc.FindActiveEffect<IDehydration>(EBodyPart.Common) != null) return true;
                        // Exhaustion — critically low energy actively dealing damage (IExhaustion)
                        if (hc.FindActiveEffect<IExhaustion>(EBodyPart.Common) != null) return true;
                        return false;

                    case MedCategory.Nutrition:
                        // Food/drink need: critically low effects or any meaningful deficit.
                        float nutritionMinDeficit = Mathf.Max(0f, Cfg.TeamHealing.NutritionMinDeficitToDisplay);
                        if (hc.FindActiveEffect<IDehydration>(EBodyPart.Common) != null) return true;
                        if (hc.FindActiveEffect<IExhaustion>(EBodyPart.Common) != null) return true;
                        if (hc.Hydration.Current < hc.Hydration.Maximum - nutritionMinDeficit) return true;
                        if (hc.Energy.Current    < hc.Energy.Maximum    - nutritionMinDeficit) return true;
                        return false;

                    default:
                        return true;
                }
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogWarning($"[TeamMedical] PatientNeedsCategory error (fail-open): {ex.Message}");
                return true;
            }
        }

        //====================[ Internal Logic ]====================
        
        // Called by MedPickerInteractable after healer selects a specific med item. Starts hold-to-heal animation and queues packet.
        public static void BeginHeal(Player healer, Player patient, Item item, Action<bool> onComplete)
        {
            try
            {
                if (healer == null || patient == null || item == null)
                {
                    onComplete?.Invoke(false);
                    return;
                }

                // A hold is already running; re-planting would cancel it without its callback firing.
                // The running hold's handler still reports back to the picker.
                if (healer.CurrentManagedState is PlantPlayerState) return;

                if (healer.CurrentState is not IdlePlayerState)
                {
                    VFX_UI.Text(Color.yellow, PlayerFacingMessages.TeamHeal.CannotHealWhileMoving);
                    onComplete?.Invoke(false);
                    return;
                }

                VFX_UI.ObjectivePanel(Color.green, PlayerFacingMessages.TeamHeal.HealingObjective, HEAL_HOLD_TIME);

                var handler = new HealCompleteHandler
                {
                    healer       = healer,
                    patient      = patient,
                    healerId     = healer.ProfileId,
                    patientId    = patient.ProfileId,
                    selectedItem = item,
                    onComplete   = onComplete
                };

                healer.CurrentManagedState.Plant(true, false, HEAL_HOLD_TIME, handler.Complete);
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"[TeamMedical] BeginHeal error: {ex.Message}");
                onComplete?.Invoke(false);
            }
        }

        //====================[ HealCompleteHandler ]====================
        internal class HealCompleteHandler
        {
            //====================[ Fields ]====================
            public Player        healer;
            public Player        patient;
            public string        healerId;
            public string        patientId;
            public Item          selectedItem;
            public Action<bool>  onComplete;

            //====================[ Callbacks ]====================
            public void Complete(bool result)
            {
                VFX_UI.HideObjectivePanel();

                if (!result)
                {
                    VFX_UI.Text(Color.yellow, PlayerFacingMessages.TeamHeal.HealingCancelled);
                    onComplete?.Invoke(false);
                    return;
                }

                // Validate patient availability.
                if (patient == null || patient.HealthController == null || !patient.HealthController.IsAlive)
                {
                    VFX_UI.Text(Color.yellow, PlayerFacingMessages.TeamHeal.PatientUnavailable);
                    onComplete?.Invoke(true); // Close the UI
                    return;
                }

                // The item may have been used, moved or already sent while we were holding.
                if (IsReserved(selectedItem.Id) || ModUtils.IsItemInUse(healer, selectedItem)
                    || !ModUtils.IsTeamHealItemValid(healer, selectedItem, out _))
                {
                    VFX_UI.Text(Color.yellow, PlayerFacingMessages.TeamHeal.ItemUnavailable);
                    onComplete?.Invoke(true);
                    return;
                }

                ReservedItemExpiry[selectedItem.Id] = Time.time + ReservationTimeoutSeconds;
                VFX_UI.Text(Color.green, PlayerFacingMessages.TeamHeal.HealingTeammate);

                // Broadcast to all machines; the patient applies the item and answers with a
                // TeamHealResultPacket, which releases the reservation.
                FikaBridge.SendTeamHealPacket(patientId, healerId, selectedItem.Id);
                onComplete?.Invoke(true); // Close the UI.
            }
        }
    }
}
