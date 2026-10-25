//====================[ Imports ]====================
using System;
using System.Collections.Generic;
using EFT;
using EFT.InputSystem;
using UnityEngine;

namespace KeepMeAlive.Gameplay.Medical
{
    //====================[ TeamHealWheel ]====================
    // The local healer's team-heal wheel, opened by Interact on a standing, hurt teammate (BodyInteractable). Two rings:
    // the five categories in fixed places, then the items the healer carries for the one opened. The mouse aims a virtual
    // stick: its direction picks a slot, inside the dead zone nothing. LMB opens a category or uses an item (sent at once,
    // TeamMedical.SendHeal); RMB goes back to the categories, and from the categories closes the wheel. It also closes
    // when the teammate is no longer someone to heal, the healer is further than 3 m from them, or another screen takes
    // over.
    //
    // Timing. The game reads input once a frame in InputManager.Update and dispatches it there (ClientApplication's
    // Update queue): key combinations turn presses into commands (a Press key on its down frame, a Release key on its up
    // frame, a toggle key's start command on down and its end command on up), and every input node gets the frame's
    // command list and then its axes. Interact itself runs inside that dispatch (ActionPanel on BeginInteracting), so the
    // wheel opens mid-dispatch. The HUD's update, which draws the wheel, runs before or after that dispatch in no fixed
    // order. So the wheel takes no input of its own: it gets the same commands and axes the game does, in the same call
    // (TeamHealWheelInputPatches), and the HUD tick (Tick) only checks, refreshes and draws.
    //   commands - Shoot (ToggleShooting) selects and Aim (ToggleAlternativeShooting) goes back, both consumed there, so a
    //              click is never also a shot or an aim. Releases (the end half of a held key) pass untouched. Mouse
    //              scroll is consumed. Any other command is a key: it closes the wheel and goes through as usual (Esc
    //              opens the menu, Tab the inventory, C crouches).
    //   axes     - movement and lean come from keys: a new press (the axis rising from zero) closes the wheel and the
    //              move goes through; one already held when the wheel opened doesn't, until it is let go. Turn is the
    //              mouse: it moves the stick, and turn and look are then zeroed so the camera stays still.
    internal static class TeamHealWheel
    {
        //====================[ Tuning ]====================
        private const float MaxDistanceSq = 3f * 3f;
        // Stick travel per unit of the game's turn axis (Unity's raw mouse axis times the binding's sensitivity); the
        // stick is clamped to radius 1.
        private const float StickGain = 0.05f;
        private const float DeadZone = 0.35f;
        // A movement or lean axis beyond this is a key held.
        private const float MoveThreshold = 0.01f;
        // How often the rings are rebuilt from the inventory and the teammate's health.
        private const float RefreshSeconds = 0.25f;

        // The end half of EFT's toggle key combinations (InputManager.Init), plus the other end commands: what a key or
        // button sends when it is let go. They pass, so nothing held before the wheel opened stays stuck.
        private static readonly HashSet<ECommand> Releases = new HashSet<ECommand>
        {
            ECommand.EndShooting,
            ECommand.EndAlternativeShooting,
            ECommand.FinishHighThrow,
            ECommand.FinishLowThrow,
            ECommand.EndInteracting,
            ECommand.EndSpecialInteracting,
            ECommand.EndSprinting,
            ECommand.EndBreathing,
            ECommand.EndWalk,
            ECommand.VaultingEnd,
            ECommand.RestorePose,
            ECommand.EndLeanLeft,
            ECommand.EndLeanRight,
            ECommand.BlindShootEnd,
            ECommand.EndAnimBlindFireAbove,
            ECommand.EndAnimBlindFireRight,
            ECommand.ReturnFromLeftStep,
            ECommand.ReturnFromRightStep,
            ECommand.MumbleEnd,
            ECommand.MumbleDropdownHide,
            ECommand.StopTalk,
            ECommand.ExitPinLockMode
        };

        //====================[ State ]====================
        private enum Level { Categories, Items }

        private static Player _healer;
        private static Player _patient;
        private static Level _level;
        private static MedCategory _category;
        private static List<WheelSlot> _slots = new List<WheelSlot>();
        private static bool _slotsChanged;
        private static int _highlight = -1;
        private static Vector2 _stick;
        // A movement or lean key was down at the last dispatch (or when the wheel opened, until it is let go).
        private static bool _moveHeld;
        private static int _openedFrame;
        private static int _lastClickFrame = -1;
        private static float _nextRefresh;
        private static float _peakTurn;
        private static TeamHealWheelView _view;

        public static bool IsOpen => _healer != null;

        public static bool IsOpenFor(string patientId) =>
            IsOpen && _patient != null && !string.IsNullOrEmpty(patientId) && _patient.ProfileId == patientId;

        //====================[ Open / Close ]====================
        public static void Open(Player healer, Player patient)
        {
            try
            {
                if (healer == null || patient == null || !healer.IsYourPlayer) return;
                // A hold under way or a body in the hands keeps the interact key (and the body's own prompt) theirs.
                if (CrouchHoldState.IsActive(healer) || DownedDragController.IsLocalHoldingBody || DownedDragController.IsPickupLocked) return;

                if (_view == null || !_view.IsAlive) _view = TeamHealWheelView.TryCreate();
                if (_view == null) return;

                Close("reopened");
                _healer = healer;
                _patient = patient;
                _openedFrame = Time.frameCount;
                _lastClickFrame = -1;
                _moveHeld = true;
                _peakTurn = 0f;
                ShowLevel(Level.Categories);
                _view.Show();
                RefreshInteractionMenu(patient);
                RevivalDebugLog.LogDebug($"[TeamHealWheel] opened on {patient.ProfileId} at frame {Time.frameCount}, " +
                    $"distance {Vector3.Distance(healer.Position, patient.Position):F2} m");
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"[TeamHealWheel] Open error: {ex}");
                Close("open failed");
            }
        }

        public static void Close(string reason)
        {
            if (!IsOpen) return;
            var patient = _patient;
            _healer = null;
            _patient = null;
            _slots = new List<WheelSlot>();
            _highlight = -1;
            Hud.ClearWheelStrip();
            _view?.Hide();
            RevivalDebugLog.LogDebug($"[TeamHealWheel] closed at frame {Time.frameCount} after {Time.frameCount - _openedFrame} frames: {reason} " +
                $"(peak turn axis {_peakTurn:F2} per dispatch)");
            RefreshInteractionMenu(patient);
        }

        public static void CloseFor(string patientId, string reason)
        {
            if (IsOpenFor(patientId)) Close(reason);
        }

        // The body's menu is empty while the wheel is open on it (BodyInteractable.BuildActions); a new state time makes
        // the game ask again, so the menu hides on open and Interact comes back on close.
        private static void RefreshInteractionMenu(Player patient)
        {
            if (patient != null && BodyInteractableRuntime.TryGet(patient.ProfileId, out var body)) body.SetStateUpdateTime();
        }

        //====================[ Input (from the game's dispatch) ]====================
        // Each of the local player's commands while the wheel is open. True: the wheel used it (it is removed from the
        // frame's list); false: it goes on as usual.
        internal static bool HandleCommand(ECommand command)
        {
            if (!IsOpen) return false;
            if (Releases.Contains(command)) return false;

            switch (command)
            {
                case ECommand.ToggleShooting:
                case ECommand.TryHighThrow:
                    if (_lastClickFrame != Time.frameCount)
                    {
                        _lastClickFrame = Time.frameCount;
                        RevivalDebugLog.LogDebug($"[TeamHealWheel] select (slot {_highlight}) at frame {Time.frameCount}");
                        Select();
                    }
                    return true;
                case ECommand.ToggleAlternativeShooting:
                case ECommand.TryLowThrow:
                    if (_lastClickFrame != Time.frameCount)
                    {
                        _lastClickFrame = Time.frameCount;
                        RevivalDebugLog.LogDebug($"[TeamHealWheel] back at frame {Time.frameCount}");
                        Back();
                    }
                    return true;
                case ECommand.ScrollNext:
                case ECommand.ScrollPrevious:
                    return true;
            }

            Close($"key command {command}");
            return false;
        }

        // The local player's axes for the dispatch, after its commands: movement and lean keys, then the mouse.
        internal static void HandleAxes(float[] axes)
        {
            if (!IsOpen || axes == null || axes.Length <= (int)EAxis.LeanX) return;

            bool moving = Mathf.Abs(axes[(int)EAxis.MoveX]) > MoveThreshold || Mathf.Abs(axes[(int)EAxis.MoveY]) > MoveThreshold
                          || Mathf.Abs(axes[(int)EAxis.LeanX]) > MoveThreshold;
            if (moving && !_moveHeld)
            {
                Close($"movement key (move {axes[(int)EAxis.MoveX]:F2}, {axes[(int)EAxis.MoveY]:F2}, lean {axes[(int)EAxis.LeanX]:F2})");
                return;
            }
            _moveHeld = moving;

            var turn = new Vector2(axes[(int)EAxis.TurnX], -axes[(int)EAxis.TurnY]);
            _peakTurn = Mathf.Max(_peakTurn, turn.magnitude);
            _stick = Vector2.ClampMagnitude(_stick + turn * StickGain, 1f);
            UpdateHighlight();

            axes[(int)EAxis.TurnX] = 0f;
            axes[(int)EAxis.TurnY] = 0f;
            axes[(int)EAxis.LookX] = 0f;
            axes[(int)EAxis.LookY] = 0f;
        }

        //====================[ Per Frame ]====================
        // From the HUD's tick: the checks that don't come from input, the rings' refresh, and drawing.
        internal static void Tick()
        {
            if (!IsOpen) return;

            try
            {
                string reason = CloseReason();
                if (reason != null)
                {
                    Close(reason);
                    return;
                }

                if (Time.time >= _nextRefresh) Rebuild();
                if (!IsOpen) return;

                // Re-asserted each frame so a HUD reset elsewhere can't leave the wheel without its controls.
                Hud.SetWheelStrip(_level == Level.Items ? Hud.WheelStrip.Items : Hud.WheelStrip.Categories);
                _view.Render(_slots, _slotsChanged, _highlight, _stick);
                _slotsChanged = false;
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"[TeamHealWheel] Tick error: {ex}");
                Close("tick error");
            }
        }

        private static string CloseReason()
        {
            if (_view == null || !_view.IsAlive) return "view gone";
            if (!PlayerUtils.IsAlive(_healer)) return "healer gone or dead";
            if (!PlayerUtils.IsAlive(_patient)) return "patient gone or dead";
            if (RMSession.IsPlayerCritical(_healer.ProfileId)) return "healer downed";
            if (RMSession.IsPlayerCritical(_patient.ProfileId)) return "patient downed";
            float distanceSq = (_healer.Position - _patient.Position).sqrMagnitude;
            if (distanceSq > MaxDistanceSq) return $"too far ({Mathf.Sqrt(distanceSq):F2} m)";
            if (_healer.IsInventoryOpened) return "inventory open";
            if (!Hud.IsBattleUiActive()) return "another screen";
            return null;
        }

        // The stick's direction picks the slot; inside the dead zone nothing (except a single-item ring, which covers 360°).
        private static void UpdateHighlight()
        {
            int n = _slots.Count;
            if (n == 0)
            {
                _highlight = -1;
                return;
            }
            if (n == 1)
            {
                _highlight = 0;
                return;
            }
            if (_stick.magnitude < DeadZone)
            {
                _highlight = -1;
                return;
            }

            float angle = Mathf.Atan2(_stick.x, _stick.y) * Mathf.Rad2Deg;
            if (angle < 0f) angle += 360f;
            float slice = 360f / n;
            _highlight = Mathf.FloorToInt((angle + slice / 2f) / slice) % n;
        }

        private static void Select()
        {
            if (_highlight < 0 || _highlight >= _slots.Count) return;
            var slot = _slots[_highlight];
            if (!slot.Enabled) return;

            if (_level == Level.Categories)
            {
                _category = slot.Category;
                ShowLevel(Level.Items);
                return;
            }

            var healer = _healer;
            var patient = _patient;
            Close("item used");
            TeamMedical.SendHeal(healer, patient, slot.Item);
        }

        private static void Back()
        {
            if (_level == Level.Items) ShowLevel(Level.Categories);
            else Close("back from categories");
        }

        //====================[ Rings ]====================
        // A new ring starts with nothing picked.
        private static void ShowLevel(Level level)
        {
            _level = level;
            _stick = Vector2.zero;
            _highlight = -1;
            Hud.SetWheelStrip(level == Level.Items ? Hud.WheelStrip.Items : Hud.WheelStrip.Categories);
            Rebuild();
        }

        // The items ring goes back to the categories once the teammate no longer needs that category or nothing for it is
        // left.
        private static void Rebuild()
        {
            _nextRefresh = Time.time + RefreshSeconds;
            _slotsChanged = true;

            if (_level == Level.Items)
            {
                var offers = PatientNeeds.Needs(_patient, _category)
                    ? TeamMedical.OffersFor(_healer, _patient, _category)
                    : new List<MedOffer>();
                if (offers.Count > 0)
                {
                    _slots = ItemSlots(offers);
                    UpdateHighlight();
                    return;
                }
                ShowLevel(Level.Categories);
                return;
            }

            _slots = CategorySlots(TeamMedical.Categories(_healer, _patient));
            UpdateHighlight();
        }

        private static List<WheelSlot> CategorySlots(List<WheelCategory> categories)
        {
            var slots = new List<WheelSlot>(categories.Count);
            foreach (var category in categories)
            {
                var slot = new WheelSlot { Category = category.Category, Label = CategoryName(category.Category), Enabled = category.CanOpen };
                if (!category.Needed)
                {
                    slot.Sub = PlayerFacingMessages.HealWheel.NotNeeded;
                    slot.SubTone = WheelSubTone.Dim;
                }
                else if (category.Offers.Count == 0)
                {
                    slot.Sub = PlayerFacingMessages.HealWheel.NoMeds;
                    slot.SubTone = WheelSubTone.Bad;
                }
                else
                {
                    slot.Sub = PlayerFacingMessages.HealWheel.Items(category.Offers.Count);
                }
                slots.Add(slot);
            }
            return slots;
        }

        private static List<WheelSlot> ItemSlots(List<MedOffer> offers)
        {
            var slots = new List<WheelSlot>(offers.Count);
            foreach (var offer in offers)
            {
                slots.Add(new WheelSlot
                {
                    Category = _category,
                    Item = offer.Item,
                    Count = offer.Count,
                    Enabled = true,
                    Label = offer.Item.LocalizedShortName(),
                    FullName = offer.Item.LocalizedName(),
                    Amount = AmountText(MedCatalog.Amount(offer.Item, offer.Categories))
                });
            }
            return slots;
        }

        private static string CategoryName(MedCategory category) => category switch
        {
            MedCategory.Bleeds => PlayerFacingMessages.HealWheel.Bleeds,
            MedCategory.Breaks => PlayerFacingMessages.HealWheel.Breaks,
            MedCategory.Health => PlayerFacingMessages.HealWheel.Health,
            MedCategory.Comfort => PlayerFacingMessages.HealWheel.Comfort,
            _ => PlayerFacingMessages.HealWheel.Nutrition
        };

        // "300 / 300 HP", "4 / 5 USES", "60 / 100" (food and drink) or "1 USE".
        private static string AmountText(MedAmount amount) => amount.Unit switch
        {
            MedAmountUnit.Hp => $"{amount.Current} / {amount.Max} {PlayerFacingMessages.HealWheel.Hp}",
            MedAmountUnit.Uses => $"{amount.Current} / {amount.Max} {PlayerFacingMessages.HealWheel.Uses}",
            MedAmountUnit.Portion => $"{amount.Current} / {amount.Max}",
            _ => PlayerFacingMessages.HealWheel.OneUse
        };
    }
}
