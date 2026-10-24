//====================[ Imports ]====================
using System;
using EFT;
using EFT.HealthSystem;
using UnityEngine;

namespace KeepMeAlive.Gameplay.Medical
{
    //====================[ PatientConditions ]====================
    // The two-condition categories' detail: which bleed and which kind of break the patient has, so an item that treats
    // only the other one is left out (a tourniquet for a light bleed only, an Alu splint for a destroyed limb only).
    // Unknown health reads as "has both", so nothing is hidden on a guess.
    internal readonly struct PatientConditions
    {
        public readonly bool LightBleed, HeavyBleed, Fracture, Destroyed;

        public PatientConditions(bool lightBleed, bool heavyBleed, bool fracture, bool destroyed)
        {
            LightBleed = lightBleed;
            HeavyBleed = heavyBleed;
            Fracture = fracture;
            Destroyed = destroyed;
        }

        public static readonly PatientConditions All = new PatientConditions(true, true, true, true);
    }

    //====================[ PatientNeeds ]====================
    // What a teammate's health says they need, read from their (observed) health controller. Fails open: when the state
    // can't be read, a category counts as needed, so the healer is never kept from helping by a read error.
    internal static class PatientNeeds
    {
        private static SyncedGameplayConfig Cfg => SyncedServerConfigStore.Config.Gameplay;

        private static readonly EBodyPart[] BodyParts =
        {
            EBodyPart.Head, EBodyPart.Chest, EBodyPart.Stomach,
            EBodyPart.LeftArm, EBodyPart.RightArm,
            EBodyPart.LeftLeg, EBodyPart.RightLeg
        };

        //====================[ Categories ]====================
        //   Bleeds    - LightBleeding or HeavyBleeding
        //   Breaks    - a fracture on any part, or a destroyed part surgery can restore
        //   Health    - any part that isn't destroyed with HP below its maximum (a destroyed one is Breaks')
        //   Comfort   - Pain, Contusion, Intoxication or LethalIntoxication
        //   Nutrition - Dehydration or Exhaustion, or hydration / energy short by more than the configured deficit
        public static bool Needs(Player patient, MedCategory category)
        {
            try
            {
                var hc = patient?.HealthController;
                if (hc == null) return true;

                switch (category)
                {
                    case MedCategory.Bleeds:
                        return hc.FindActiveEffect<IBleeding>(EBodyPart.Common) != null;

                    case MedCategory.Breaks:
                        foreach (var part in BodyParts)
                        {
                            if (hc.IsBodyPartBroken(part) || IsSurgicallyRestorable(hc, part)) return true;
                        }
                        return false;

                    case MedCategory.Health:
                        foreach (var part in BodyParts)
                        {
                            if (hc.IsBodyPartDestroyed(part)) continue;
                            var health = hc.GetBodyPartHealth(part);
                            if (health.Current < health.Maximum - 0.5f) return true;
                        }
                        return false;

                    case MedCategory.Comfort:
                        return hc.FindActiveEffect<IPain>(EBodyPart.Common) != null
                               || hc.FindActiveEffect<IContusion>(EBodyPart.Common) != null
                               || hc.FindActiveEffect<IIntoxication>(EBodyPart.Common) != null
                               || hc.FindActiveEffect<ILethalIntoxication>(EBodyPart.Common) != null;

                    case MedCategory.Nutrition:
                        float minDeficit = Mathf.Max(0f, Cfg.TeamHealing.NutritionMinDeficitToDisplay);
                        return hc.FindActiveEffect<IDehydration>(EBodyPart.Common) != null
                               || hc.FindActiveEffect<IExhaustion>(EBodyPart.Common) != null
                               || hc.Hydration.Current < hc.Hydration.Maximum - minDeficit
                               || hc.Energy.Current < hc.Energy.Maximum - minDeficit;

                    default:
                        return true;
                }
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogWarning($"[PatientNeeds] Needs({category}) error (fail-open): {ex.Message}");
                return true;
            }
        }

        public static bool NeedsAny(Player patient)
        {
            foreach (MedCategory category in Enum.GetValues(typeof(MedCategory)))
            {
                if (Needs(patient, category)) return true;
            }
            return false;
        }

        //====================[ Conditions ]====================
        public static PatientConditions ReadConditions(Player patient)
        {
            try
            {
                var hc = patient?.HealthController;
                if (hc == null) return PatientConditions.All;

                bool fracture = false, destroyed = false;
                foreach (var part in BodyParts)
                {
                    if (hc.IsBodyPartBroken(part)) fracture = true;
                    if (IsSurgicallyRestorable(hc, part)) destroyed = true;
                }

                return new PatientConditions(
                    hc.FindActiveEffect<ILightBleeding>(EBodyPart.Common) != null,
                    hc.FindActiveEffect<IHeavyBleeding>(EBodyPart.Common) != null,
                    fracture,
                    destroyed);
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogWarning($"[PatientNeeds] ReadConditions error (fail-open): {ex.Message}");
                return PatientConditions.All;
            }
        }

        // EFT's RestoreBodyPart (CMS / SURV-12) refuses Head and Chest.
        private static bool IsSurgicallyRestorable(IHealthController hc, EBodyPart part)
            => part != EBodyPart.Head && part != EBodyPart.Chest && hc.IsBodyPartDestroyed(part);
    }
}
