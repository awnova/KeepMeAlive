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
    // Has no collider: while it exists, hits on the patient's body proxies resolve to it.
    // Destroys itself when closed or when a heal is applied.
    public class MedPickerInteractable : InteractableObject
    {
        //====================[ Properties ]====================
        public BodyInteractable OwnerBody { get; private set; }
        public Player Healer              { get; private set; }
        public Player Patient             { get; private set; }

        //====================[ Fields ]====================
        private MedCategory? _category;
        private bool _closed;

        // Track action changes to avoid resetting the selection.
        private GamePlayerOwner _viewerOwner;
        private int _shownSignature;
        private float _nextSignaturePoll;
        private const float SignaturePollInterval = 0.25f;

        // Configuration
        private const float MaxDistanceSq = 9f; // 3 meters squared

        //====================[ Unity Lifecycle ]====================
        private void Update()
        {
            // Patient or healer gone, dead or downed: the picker no longer makes sense.
            if (Healer == null || Patient == null
                || Patient.HealthController == null || !Patient.HealthController.IsAlive
                || RMSession.IsPlayerCritical(Patient.ProfileId) || RMSession.IsPlayerCritical(Healer.ProfileId))
            {
                Close();
                return;
            }

            // Walking away closes the picker.
            if ((Healer.Position - Patient.Position).sqrMagnitude > MaxDistanceSq)
            {
                Close();
                return;
            }

            RefreshListIfActionsChanged();
        }

        // Refresh only when the available actions change.
        private void RefreshListIfActionsChanged()
        {
            if (_viewerOwner == null || Time.time < _nextSignaturePoll) return;
            _nextSignaturePoll = Time.time + SignaturePollInterval;

            var viewer = _viewerOwner.Player;
            if (viewer == null || !ReferenceEquals(viewer.InteractableObject, this)) return;

            if (BodyInteractable.Signature(BuildActions()) != _shownSignature) SetStateUpdateTime();
        }

        public void Init(Player healer, Player patient, BodyInteractable ownerBody, MedCategory? category = null)
        {
            Healer    = healer;
            Patient   = patient;
            OwnerBody = ownerBody;
            _category = category;
        }

        // Called on each list build; records what the list shows.
        public AvailableInteractionState GetActions(GamePlayerOwner owner)
        {
            var actions = BuildActions();

            // Only "Cancel" left: nothing usable remains, so close instead of showing it.
            if (actions.Actions.Count <= 1)
            {
                Close();
                return new AvailableInteractionState();
            }

            _viewerOwner = owner;
            _shownSignature = BodyInteractable.Signature(actions);
            _nextSignaturePoll = Time.time + SignaturePollInterval;
            return actions;
        }

        private AvailableInteractionState BuildActions()
        {
            var actions = new AvailableInteractionState();
            if (_closed || Healer == null || Patient == null) return actions;

            try
            {
                actions.Actions.Add(new InteractionAction
                {
                    Name     = PlayerFacingMessages.Interaction.CancelAction,
                    Disabled = false,
                    Action   = Close
                });

                foreach (var item in TeamMedical.GetUsableMedsByCategory(Healer, Patient, _category))
                {
                    Item captured = item;
                    actions.Actions.Add(new InteractionAction
                    {
                        Name     = captured.ShortName.Localized(),
                        Disabled = false,
                        Action   = () => OnPickItem(captured)
                    });
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

        // Safe to call late (e.g. from a heal callback after the picker was already destroyed).
        private void Close()
        {
            if (_closed || this == null) return;
            _closed = true;
            if (OwnerBody != null) OwnerBody.RestoreFromPicker(this);
            Destroy(gameObject);
        }
    }
}
