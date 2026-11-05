//====================[ Imports ]====================
using System;
using System.Collections.Generic;
using EFT;
using EFT.InventoryLogic;
using UnityEngine;

namespace KeepMeAlive.Gameplay.Medical
{
    //====================[ WheelCategory ]====================
    // One slot of the wheel's first ring: whether the patient needs it and what the healer carries for it (empty when it
    // isn't needed: an unneeded category never opens).
    internal readonly struct WheelCategory
    {
        public readonly MedCategory Category;
        public readonly bool Needed;
        public readonly List<MedOffer> Offers;

        public WheelCategory(MedCategory category, bool needed, List<MedOffer> offers)
        {
            Category = category;
            Needed = needed;
            Offers = offers;
        }

        public bool CanOpen => Needed && Offers.Count > 0;
    }

    //====================[ TeamMedical ]====================
    // The healer's side of team healing. The healer picks an item on the wheel (TeamHealWheel); SendHeal reserves it and
    // asks the patient's machine to use it. The patient answers once the use has started (TeamHealPatient) with how long it
    // takes, which runs the hold bar here, and again when the use ends. The item's own drain and "in use" state come back
    // through Fika's health sync, as for any use of someone else's meds.
    internal static class TeamMedical
    {
        //====================[ Constants ]====================
        // How long a sent item stays reserved if the patient never answers: their inventory wait (2 s) plus their wait for
        // the use to start (TeamHealPatient.UseStartTimeoutSeconds, 5 s), with room for the round trip.
        private const float ReservationTimeoutSeconds = 12f;
        // Once the use has started the item stays reserved until the patient says it ended, or this long past its use
        // time: the in-use state Fika syncs back may lag the answer.
        private const float InUseGraceSeconds = 5f;

        //====================[ State ]====================
        // Items sent to a patient whose use hasn't ended yet. Nothing on this machine marks the item as in use until the
        // patient's med effect syncs back, so without this the same item could be offered (and sent) twice.
        // Started: the patient answered that the use began, so running out is no longer "never answered".
        private struct Reservation
        {
            public float Expiry;
            public bool Started;
        }

        private static readonly Dictionary<string, Reservation> Reservations = new();

        // The hold bar showing a teammate's use of one of this healer's items, so only that use's end hides it.
        private static CustomTimer _useBar;
        private static string _useBarItemId;

        //====================[ Wheel Queries ]====================
        // All five categories in enum order (the wheel's fixed places), from one inventory pass.
        public static List<WheelCategory> Categories(Player healer, Player patient)
        {
            var offers = MedCatalog.Offers(healer, IsReserved);
            var conditions = PatientNeeds.ReadConditions(patient);
            var result = new List<WheelCategory>(5);
            foreach (MedCategory category in Enum.GetValues(typeof(MedCategory)))
            {
                bool needed = PatientNeeds.Needs(patient, category);
                result.Add(new WheelCategory(category, needed,
                    needed ? MedCatalog.ForCategory(offers, category, conditions) : new List<MedOffer>()));
            }
            return result;
        }

        // The items ring for one category.
        public static List<MedOffer> OffersFor(Player healer, Player patient, MedCategory category) =>
            MedCatalog.ForCategory(MedCatalog.Offers(healer, IsReserved), category, PatientNeeds.ReadConditions(patient));

        // Whether Interact opens onto anything: some category the patient needs has an item.
        public static bool HasAnythingToGive(Player healer, Player patient)
        {
            foreach (var category in Categories(healer, patient))
            {
                if (category.CanOpen) return true;
            }
            return false;
        }

        //====================[ Sending ]====================
        // Sends the item to the patient at once. False (with the reason shown) when it can't be sent.
        public static bool SendHeal(Player healer, Player patient, Item item)
        {
            try
            {
                if (healer == null || item == null) return false;

                if (!PlayerUtils.IsAliveAndStanding(patient))
                {
                    VFX_UI.Text(Color.yellow, PlayerFacingMessages.TeamHeal.PatientUnavailable);
                    return false;
                }

                // The item may have been used, moved or sent since the wheel listed it.
                if (IsReserved(item) || InventoryUtils.IsItemInUse(healer, item) || !InventoryUtils.IsTeamHealItemValid(healer, item, out _))
                {
                    VFX_UI.Text(Color.yellow, PlayerFacingMessages.TeamHeal.ItemUnavailable);
                    return false;
                }

                Reservations[item.Id] = new Reservation { Expiry = Time.time + ReservationTimeoutSeconds };

                // Broadcast; the patient's machine applies it and answers with TeamHealResultPacket.
                FikaBridge.SendTeamHealPacket(patient.ProfileId, healer.ProfileId, item.Id);
                return true;
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"[TeamMedical] SendHeal error: {ex.Message}");
                return false;
            }
        }

        //====================[ Patient Answers ]====================
        // The patient's use started (success, with its length) or never will (the reason).
        public static void OnHealResult(string itemId, bool success, string reason, float useSeconds)
        {
            if (success) Reservations[itemId] = new Reservation { Expiry = Time.time + Mathf.Max(0f, useSeconds) + InUseGraceSeconds, Started = true };
            else Reservations.Remove(itemId);
            RevivalDebugLog.LogNetworkTrace($"[TeamHeal] result for {itemId}: success={success} use={useSeconds:F2}s reason='{reason}'");

            if (!success)
            {
                VFX_UI.Text(Color.yellow, string.IsNullOrEmpty(reason) ? PlayerFacingMessages.TeamHeal.PatientBusy : reason);
                return;
            }

            // A hold the healer started meanwhile (a revive) keeps its own bar.
            var healer = PlayerUtils.GetYourPlayer();
            if (useSeconds <= 0f || CrouchHoldState.IsActive(healer)) return;
            var item = MedCatalog.Find(healer, itemId);
            _useBar = Hud.ShowHold(Hud.HoldKind.Heal, useSeconds, item?.LocalizedShortName());
            _useBarItemId = itemId;
        }

        // The patient's use of the item ended: run to the end, or stopped early.
        public static void OnHealEnded(string itemId, bool completed)
        {
            RevivalDebugLog.LogNetworkTrace($"[TeamHeal] use of {itemId} ended (completed={completed})");
            Reservations.Remove(itemId);

            if (_useBarItemId == itemId)
            {
                Hud.HideHold(_useBar);
                _useBar = null;
                _useBarItemId = null;
            }

            // A completed use is told by the bar running out.
            if (!completed) VFX_UI.Text(Color.yellow, PlayerFacingMessages.TeamHeal.HealInterrupted);
        }

        //====================[ Reservations ]====================
        private static bool IsReserved(Item item)
        {
            if (!Reservations.TryGetValue(item.Id, out var reservation)) return false;
            if (Time.time < reservation.Expiry) return true;

            Reservations.Remove(item.Id);
            if (!reservation.Started) VFX_UI.Text(Color.yellow, PlayerFacingMessages.TeamHeal.HealNoResponse);
            return false;
        }

        //====================[ Lifecycle ]====================
        // Raid boundary.
        public static void Clear()
        {
            Reservations.Clear();
            _useBar = null;
            _useBarItemId = null;
        }
    }
}
