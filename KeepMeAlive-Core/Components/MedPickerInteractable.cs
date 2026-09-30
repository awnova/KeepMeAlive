//====================[ Imports ]====================
using EFT;
using EFT.Interactive;
using EFT.InventoryLogic;
using EFT.UI;
using System;
using UnityEngine;
using KeepMeAlive.Features;
using KeepMeAlive.Helpers;

namespace KeepMeAlive.Components
{
    //====================[ MedPickerInteractable ]====================
    // Spawned by BodyInteractable after the healer selects a category.
    // Shows one action per med item in that category the healer can apply to the patient, plus "Cancel".
    // Destroys itself on any selection and restores BodyInteractable's collider.
    public class MedPickerInteractable : InteractableObject
    {
        //====================[ Properties ]====================
        public BodyInteractable OwnerBody { get; private set; }
        public Player Healer              { get; private set; }
        public Player Patient             { get; private set; }

        //====================[ Fields ]====================
        private MedCategory? _category;
        private Collider _collider;

        // Configuration
        // Uses the configured range for the picker. When the setting is unset or <= 0, the picker
        // keeps its historical 3m limit; BodyInteractableRuntime treats <= 0 as unlimited.
        private const float FALLBACK_MAX_DISTANCE_SQ = 9f; // 3 meters squared
        private static float MaxDistanceSq
        {
            get
            {
                float range = SyncedServerConfigStore.Config.Gameplay.TeamHealing.InteractRangeMeters;
                return range > 0f ? range * range : FALLBACK_MAX_DISTANCE_SQ;
            }
        }

        //====================[ Unity Lifecycle ]====================
        private void Awake()
        {
            _collider = GetComponent<Collider>();
            if (_collider != null)
            {
                _collider.enabled = false;
            }
        }

        private void Update()
        {
            if (_collider == null) return;

            // Patient or healer gone, dead or downed: the picker no longer makes sense.
            if (Healer == null || Patient == null
                || Patient.HealthController == null || !Patient.HealthController.IsAlive
                || RMSession.IsPlayerCritical(Patient.ProfileId) || RMSession.IsPlayerCritical(Healer.ProfileId))
            {
                Close();
                return;
            }

            // Walking away closes the picker (and re-enables the teammate's body colliders).
            if ((Healer.Position - Patient.Position).sqrMagnitude > MaxDistanceSq)
            {
                Close();
                return;
            }

            if (!_collider.enabled) _collider.enabled = true;

            // Refresh the med-item wheel as the healer's inventory changes.
            SetStateUpdateTime();
        }

        public void Init(Player healer, Player patient, BodyInteractable ownerBody, MedCategory? category = null)
        {
            Healer    = healer;
            Patient   = patient;
            OwnerBody = ownerBody;
            _category = category;
        }

        public AvailableInteractionState GetActions(GamePlayerOwner owner)
        {
            var actions = new AvailableInteractionState();
            try
            {
                if (Healer == null || Patient == null)
                {
                    Close();
                    return actions;
                }

                actions.Actions.Add(new InteractionAction
                {
                    Name     = PlayerFacingMessages.Interaction.CancelAction,
                    Disabled = false,
                    Action   = Close
                });

                int addedMeds = 0;
                foreach (var item in TeamMedical.GetUsableMedsByCategory(Healer, Patient, _category))
                {
                    Item captured = item;
                    actions.Actions.Add(new InteractionAction
                    {
                        Name     = captured.ShortName.Localized(),
                        Disabled = false,
                        Action   = () => OnPickItem(captured)
                    });
                    addedMeds++;
                }

                // Close the picker when no usable items remain.
                if (addedMeds == 0)
                {
                    Close();
                    return actions;
                }
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"[MedPickerInteractable] GetActions error: {ex.Message}");
            }
            return actions;
        }

        //====================[ Private Helpers ]====================
        private void OnPickItem(Item item)
        {
            // Do NOT close the picker yet. If the user cancels the animation, this picker should remain open.
            TeamMedical.BeginHeal(Healer, Patient, item, (success) =>
            {
                if (success)
                {
                    // Heal successful (applied), return to category screen.
                    Close();
                }
                // If it was cancelled (!success), we do nothing. The picker survives.
            });
        }

        private void Close()
        {
            OwnerBody?.RestoreFromPicker();
            Destroy(gameObject);
        }
    }
}
