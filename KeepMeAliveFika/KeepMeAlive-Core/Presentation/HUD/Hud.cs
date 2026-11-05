//====================[ Imports ]====================
using System;
using System.Linq;
using EFT.UI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace KeepMeAlive.Presentation.HUD
{
    //====================[ Hud ]====================
    // The mod's own downed/revive HUD, drawn on its own overlay canvas in Tarkov's Bender font:
    //   status bar   (top)           bleed-out, being revived, reviving, invulnerable
    //   hold bar     (above strip)   self-revive, give-up, reviving a teammate, healing a teammate
    //   control strip (bottom)       the downed controls and what self-revive costs
    // The same canvas carries the full-screen input blocker used while downed, and the team-heal wheel (TeamHealWheelView),
    // which this tick drives (TeamHealWheel.Tick) and whose controls take over the strip while it is open.
    // Timers are CustomTimer so callers keep the same contract as before: DownedStateController reads
    // st.CriticalStateMainTimer to drive CriticalTimer, and Stop() on a returned timer clears its bar.
    internal static class Hud
    {
        //====================[ Types ]====================
        internal enum StatusKind { Bleeding, BeingRevived, Reviving, Invulnerable }
        internal enum HoldKind { SelfRevive, GiveUp, ReviveTeammate, Heal }
        internal enum StripState { Hidden, Available, NoLives, NeedsStim, NeedsDefib, SelfReviveOff }
        internal enum SpectateHint { Hidden, Available, Active }
        internal enum WheelStrip { Hidden, Categories, Items }

        //====================[ Constants ]====================
        // The game's UI font. Bender's bold, italic and bold-italic SDF assets are marked "DON'T USE!", so bold is
        // a style on this one. There is no fallback font: without it the HUD stays hidden.
        private const string FontAssetName = "Jovanny Lemonad - Bender Normal SDF";

        private const float ReferenceWidth = 1920f;
        private const float ReferenceHeight = 1080f;

        // Sizes follow the mockup in em of this font size.
        private const float FontPx = 22f;
        private const float StripFontScale = 0.92f;
        private const float BarHeightEm = 2.7f;
        private const float BarPadEm = 2.2f;
        private const float EdgeEm = 0.22f;
        private const float DotEm = 0.6f;
        private const float DropScale = 1.3f; // the drop is drawn this much larger than the round dot
        private const float LetterSpacing = 10f; // TMP units; 10 = 0.1 em
        private const float MinStatusWidthEm = 18f;
        private const float MinHoldWidthEm = 22f;

        // The slim bleed-out bar under the status bar while spectating (mockup: .86 em text, 2.1 em tall, 1.4 em padding,
        // 3.2 em below the status bar's top).
        private const float SubFontScale = 0.86f;
        private const float SubHeightEm = 2.1f;
        private const float SubPadEm = 1.4f;
        private const float SubOffsetEm = 3.2f;

        // Distances from the screen edge, in reference pixels.
        private const float StatusTopPx = 56f;
        private const float HoldBottomPx = 135f;
        private const float StripBottomPx = 49f;

        private const float PulseSeconds = 1.1f;

        private const string BlockerName = "Blocker";
        private const string RootName = "KeepMeAlive_Hud";

        // The palette is shared with the team-heal wheel (TeamHealWheelView).
        internal static readonly Color BarColor = new Color(13f / 255f, 15f / 255f, 17f / 255f, 0.88f);
        internal static readonly Color Red = Hex(0xE5484D);
        internal static readonly Color Green = Hex(0x38E07B);
        internal static readonly Color TextColor = Hex(0xE1DFD6);
        internal static readonly Color GreyColor = Hex(0x9CA3AF);
        internal static readonly Color DimColor = Hex(0x4B5563);
        private static readonly Color Cyan = Hex(0x4FD1E0);
        private static readonly Color Blue = Hex(0x5AA9FF);
        private const string GoldTag = "#E5B358";
        private const string TextTag = "#E1DFD6";
        private const string GreyTag = "#9CA3AF";
        private const string DimTag = "#4B5563";
        private const string ChipTag = "#4B556388";

        //====================[ State ]====================
        private static GameObject _root;
        private static GameObject _blocker;
        private static Bar _status;
        private static Bar _sub;
        private static Bar _hold;
        private static Bar _strip;
        private static TMP_FontAsset _font;
        private static bool _fontFailLogged;

        // Status: the bleed-out timer lives under any overlay (being revived, reviving, invulnerable).
        private static CustomTimer _bleed;
        private static float _bleedTotal;
        private static int _lives;
        private static CustomTimer _overlayTimer;
        private static StatusKind? _overlayKind;
        private static float _overlayTotal;
        private static string _overlayName;

        private static CustomTimer _holdTimer;
        private static HoldKind? _holdKind;
        private static float _holdTotal;
        private static string _holdName;

        private static StripState _stripState = StripState.Hidden;
        private static int _stripCost;
        // Set while the local player drags a body: the strip is then the one revive prompt, on the game's interact key.
        private static string _stripInteractKey;
        // Looking at a downed teammate: "Tap [key] Drag  Hold [key] Revive  [inventory key] Loot" (whichever are offered), while
        // no other strip shows.
        // The revive carries the same chips as the strip: its cost, or why it can't be done.
        private static string _hintKey;
        private static bool _hintDrag;
        private static StripState _hintRevive = StripState.Hidden;
        private static int _hintCost;
        // "[inventory key] Loot" on the same hint, independent of the interact entries.
        private static string _hintLootKey;
        private static SpectateHint _spectateHint = SpectateHint.Hidden;
        private static string _spectateName;
        // While the team-heal wheel is open the strip is its controls only: which ring it shows.
        private static WheelStrip _wheelStrip = WheelStrip.Hidden;
        private static bool _spectateHeadcam;
        private static bool _spectateNightVision;
        private static bool _inputBlocked;

        // Last rendered text keys, so strings are only rebuilt when something visible changes.
        private static string _statusKey, _subKey, _holdKey, _stripKey;
        private static int _statusTick = -1, _subTick = -1, _holdTick = -1;

        internal static bool IsInputBlocked => _inputBlocked;

        //====================[ Public API: build ]====================
        // Builds the canvas and resolves the font. False until the game's UI exists.
        internal static bool Warmup() => EnsureBuilt();

        // The canvas root and the font, for views drawn on the same canvas (the team-heal wheel). False without the font.
        internal static bool TryGetLayer(out RectTransform root, out TMP_FontAsset font)
        {
            root = null;
            font = null;
            if (!EnsureBuilt() || _font == null) return false;
            root = (RectTransform)_root.transform;
            font = _font;
            return true;
        }

        //====================[ Public API: status ]====================
        // Starts the bleed-out countdown. The returned timer is the authority for the remaining time.
        internal static CustomTimer StartBleed(float seconds, int livesLeft)
        {
            if (!EnsureBuilt()) return null;

            DropBleed();
            var timer = new CustomTimer();
            _bleed = timer;
            _bleedTotal = Mathf.Max(0.001f, seconds);
            _lives = livesLeft;
            timer.OnCompleted += () => { if (_bleed == timer) _bleed = null; };
            timer.StartCountdown(seconds);
            return timer;
        }

        // Shows an overlay on the status bar. Passing a stopped timer's owner a new one replaces it silently.
        // BeingRevived and Reviving stay up at zero until replaced or hidden; Invulnerable clears when its timer ends.
        internal static CustomTimer ShowStatus(StatusKind kind, float seconds, string name = null)
        {
            if (!EnsureBuilt()) return null;

            DropOverlay();
            var timer = new CustomTimer();
            _overlayTimer = timer;
            _overlayKind = kind;
            _overlayTotal = Mathf.Max(0.001f, seconds);
            _overlayName = name;
            timer.OnCompleted += () =>
            {
                if (_overlayTimer != timer) return;
                _overlayTimer = null;
                if (kind == StatusKind.Invulnerable) _overlayKind = null;
            };
            timer.StartCountdown(seconds);
            return timer;
        }

        internal static void HideStatusOverlay() => DropOverlay();

        internal static void HideStatus()
        {
            DropOverlay();
            DropBleed();
        }

        //====================[ Public API: hold bar ]====================
        internal static CustomTimer ShowHold(HoldKind kind, float seconds, string name = null)
        {
            if (!EnsureBuilt()) return null;

            DropHold();
            var timer = new CustomTimer();
            _holdTimer = timer;
            _holdKind = kind;
            _holdTotal = Mathf.Max(0.001f, seconds);
            _holdName = name;
            timer.OnCompleted += () =>
            {
                if (_holdTimer != timer) return;
                _holdTimer = null;
                _holdKind = null;
            };
            timer.StartCountdown(seconds);
            return timer;
        }

        internal static void HideHold() => DropHold();

        // Hides the hold bar only while it still shows the given timer, so a bar that replaced it stays.
        internal static void HideHold(CustomTimer shown)
        {
            if (shown != null && ReferenceEquals(_holdTimer, shown)) DropHold();
        }

        //====================[ Public API: control strip ]====================
        internal static void SetStrip(StripState state, int cost = 0)
        {
            _stripState = state;
            _stripCost = cost;
            _stripInteractKey = null;
        }

        // The dragger's strip: "Hold [interact key] Revive" for the body in their hands (state says whether it can be afforded).
        internal static void SetDraggerStrip(StripState state, int cost, string interactKey)
        {
            _stripState = state;
            _stripCost = cost;
            _stripInteractKey = interactKey;
        }

        // The key the game has bound to interact.
        internal static string InteractKeyName() =>
            global::Comfort.Common.Singleton<global::EFT.Settings.SettingsManager>.Instance?.Control.Settings
                .GetKeyName(global::EFT.InputSystem.EGameKey.Interact) ?? "F";

        // The key the game has bound to the inventory.
        internal static string InventoryKeyName() =>
            global::Comfort.Common.Singleton<global::EFT.Settings.SettingsManager>.Instance?.Control.Settings
                .GetKeyName(global::EFT.InputSystem.EGameKey.Inventory) ?? "Tab";

        // A key as the game's own prompts name it (Esc, BSpace, LShift...); mouse buttons come out in the game's language.
        // The game names None as nothing, so an unbound key keeps Unity's name.
        private static string KeyLabel(KeyCode key)
        {
            string name = global::EFT.KeyTools.GetKeyNameAlias(key);
            return string.IsNullOrEmpty(name) ? key.ToString() : name;
        }

        internal static void SetLookHint(bool drag, StripState revive, int cost, bool loot)
        {
            _hintKey = drag || revive != StripState.Hidden ? InteractKeyName() : null;
            _hintDrag = drag;
            _hintRevive = revive;
            _hintCost = cost;
            _hintLootKey = loot ? InventoryKeyName() : null;
        }

        internal static void ClearLookHint()
        {
            _hintKey = null;
            _hintLootKey = null;
        }

        private static bool HasLookHint => _hintKey != null || _hintLootKey != null;

        // Drops the strip only if it is the dragger's, so a downed player's own strip is never touched.
        internal static void ClearDraggerStrip()
        {
            if (_stripInteractKey != null) HideStrip();
        }

        // The spectate part of the strip. Independent of SetStrip so its callers need not know about it.
        internal static void SetSpectate(SpectateHint hint, string name = null, bool headcam = false, bool nightVision = false)
        {
            _spectateHint = hint;
            _spectateName = name;
            _spectateHeadcam = headcam;
            _spectateNightVision = nightVision;
        }

        internal static void SetWheelStrip(WheelStrip strip) => _wheelStrip = strip;

        internal static void ClearWheelStrip() => _wheelStrip = WheelStrip.Hidden;

        // Forces every bar to rebuild its text on the next update (the Language setting changed: same state, new words).
        internal static void RefreshText()
        {
            _statusKey = null;
            _subKey = null;
            _holdKey = null;
            _stripKey = null;
        }

        internal static void HideStrip()
        {
            _stripState = StripState.Hidden;
            _stripInteractKey = null;
            _hintKey = null;
            _hintLootKey = null;
            _stripKey = null;
        }

        //====================[ Public API: blocker / reset ]====================
        // While blocked, a transparent full-screen image swallows pointer input on the battle HUD beneath.
        internal static void SetInputBlocked(bool blocked)
        {
            try
            {
                if (FikaBridge.IsHeadless()) return;

                _inputBlocked = blocked;
                if (!blocked)
                {
                    if (_blocker != null) _blocker.SetActive(false);
                    return;
                }

                if (!EnsureBuilt()) return;
                _blocker.SetActive(IsBattleUiActive());
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogWarning($"[Hud] SetInputBlocked({blocked}) warn: {ex.Message}");
            }
        }

        // Clears every bar. The input blocker is left to SetInputBlocked.
        internal static void HideAll()
        {
            DropOverlay();
            DropBleed();
            DropHold();
            HideStrip();
            ClearWheelStrip();
        }

        //====================[ Timer slots ]====================
        // Each slot's reference is cleared before the timer is stopped, so the completion handler (which Stop() also
        // fires) recognises a replaced timer and ignores it.
        private static void DropBleed()
        {
            var t = _bleed;
            _bleed = null;
            _statusKey = null;
            _statusTick = -1;
            t?.Stop();
        }

        private static void DropOverlay()
        {
            var t = _overlayTimer;
            _overlayTimer = null;
            _overlayKind = null;
            _statusKey = null;
            _statusTick = -1;
            t?.Stop();
        }

        private static void DropHold()
        {
            var t = _holdTimer;
            _holdTimer = null;
            _holdKind = null;
            _holdKey = null;
            _holdTick = -1;
            t?.Stop();
        }

        //====================[ Per-frame ]====================
        internal static void Tick()
        {
            if (_root == null) return;

            try
            {
                // Timers keep running even if a bar is not visible, so they still complete.
                if (_bleed != null && _bleed.IsRunning) _bleed.Update();
                if (_overlayTimer != null && _overlayTimer.IsRunning) _overlayTimer.Update();
                if (_holdTimer != null && _holdTimer.IsRunning) _holdTimer.Update();

                if (_blocker != null)
                {
                    bool shouldBlock = _inputBlocked && IsBattleUiActive();
                    if (_blocker.activeSelf != shouldBlock) _blocker.SetActive(shouldBlock);
                }

                if (_status == null) return; // no font: nothing to draw

                TeamHealWheel.Tick();

                float pulse = 0.675f + 0.325f * Mathf.Cos(Time.unscaledTime / PulseSeconds * Mathf.PI * 2f);
                RenderStatus(pulse);
                RenderHold();
                RenderStrip();
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"[Hud] Tick error: {ex.Message}");
            }
        }

        // Esc menu and Settings share CommonUI with EftBattleUIScreen; stepping the blocker aside when BattleUI is not
        // the current screen keeps menus clickable regardless of canvas sortingOrder.
        internal static bool IsBattleUiActive()
        {
            var mgr = EFT.UI.Screens.EftScreenManager.Instance;
            return mgr == null || mgr.CheckCurrentScreen(EFT.UI.Screens.EEftScreenType.BattleUI);
        }

        private static void RenderStatus(float pulse)
        {
            // Spectating: the top bar says who is being watched and the player's own bleed-out drops to the slim bar.
            // Any overlay (being revived, reviving, invulnerable) takes the top bar back; spectating ends with those anyway.
            if (_spectateHint == SpectateHint.Active && !_overlayKind.HasValue && _bleed != null)
            {
                RenderSpectating(pulse);
                return;
            }

            _sub.SetActive(false);
            _subKey = null;
            _subTick = -1;
            _status.SetTrack(true);

            StatusKind kind;
            CustomTimer timer;
            float total;
            string name = null;
            if (_overlayKind.HasValue)
            {
                kind = _overlayKind.Value;
                timer = _overlayTimer;
                total = _overlayTotal;
                name = _overlayName;
            }
            else if (_bleed != null)
            {
                kind = StatusKind.Bleeding;
                timer = _bleed;
                total = _bleedTotal;
            }
            else
            {
                _status.SetActive(false);
                _statusKey = null;
                _statusTick = -1;
                return;
            }

            float remaining = timer != null ? Mathf.Max(0f, (float)timer.GetTimeSpan().TotalSeconds) : 0f;

            Color color = kind switch
            {
                StatusKind.Bleeding => Red,
                StatusKind.Invulnerable => Blue,
                _ => Green
            };

            // Bleeding and invulnerable drain; the revive states fill.
            bool drains = kind == StatusKind.Bleeding || kind == StatusKind.Invulnerable;
            float fill = Mathf.Clamp01(drains ? remaining / total : 1f - remaining / total);

            _status.SetActive(true);

            int tick = kind == StatusKind.Bleeding ? Mathf.CeilToInt(remaining) : Mathf.CeilToInt(remaining * 10f);
            string key = $"{kind}|{name}|{_lives}";
            if (key != _statusKey || tick != _statusTick)
            {
                bool layoutChanged = key != _statusKey;
                _statusKey = key;
                _statusTick = tick;
                _status.SetText(BuildStatusText(kind, color, name, tick), layoutChanged);
            }

            _status.SetEdge(color, fill, false);
            bool pulses = kind != StatusKind.Invulnerable;
            _status.SetDotDrop(kind == StatusKind.Bleeding);
            _status.SetDot(color, pulses ? pulse : 1f);
        }

        private static void RenderSpectating(float pulse)
        {
            float remaining = Mathf.Max(0f, (float)_bleed.GetTimeSpan().TotalSeconds);

            _status.SetActive(true);
            string topKey = $"Spectating|{_spectateName}|{_spectateHeadcam}";
            if (topKey != _statusKey)
            {
                _statusKey = topKey;
                _statusTick = -1;
                _status.SetText(BuildSpectatingText(), true);
            }
            _status.SetTrack(false);
            _status.SetEdge(Green, 0f, false);
            _status.SetDotDrop(false);
            _status.SetDot(Green, pulse);

            int tick = Mathf.CeilToInt(remaining);
            string subKey = $"Sub|{_lives}";
            if (subKey != _subKey || tick != _subTick)
            {
                bool layoutChanged = subKey != _subKey;
                _subKey = subKey;
                _subTick = tick;
                _sub.SetText(BuildYouBleedingText(tick), layoutChanged);
            }
            _sub.SetActive(true);
            _sub.SetTrack(true);
            _sub.SetDotDrop(true);
            _sub.SetEdge(Red, Mathf.Clamp01(remaining / _bleedTotal), false);
            _sub.SetDot(Red, 1f);
        }

        private static string BuildSpectatingText()
        {
            var sb = new System.Text.StringBuilder(120);
            sb.Append("<color=").Append(ColorTag(Green)).Append("><b>").Append(PlayerFacingMessages.HudLabels.Spectating).Append("</b></color>");
            Separator(sb);
            sb.Append("<color=").Append(TextTag).Append("><noparse>").Append(_spectateName).Append("</noparse></color>");
            Separator(sb);
            sb.Append("<color=").Append(GreyTag).Append('>')
              .Append(_spectateHeadcam ? PlayerFacingMessages.HudLabels.HeadCam : PlayerFacingMessages.HudLabels.ThirdPerson).Append("</color>");
            return sb.ToString();
        }

        private static string BuildYouBleedingText(int seconds)
        {
            string red = ColorTag(Red);
            var sb = new System.Text.StringBuilder(120);
            sb.Append("<color=").Append(red).Append("><b>").Append(PlayerFacingMessages.HudLabels.BleedingOut).Append("</b></color>");
            Separator(sb);
            sb.Append("<color=").Append(TextTag).Append('>').Append(PlayerFacingMessages.HudLabels.LivesLeft(_lives)).Append("</color>");
            Separator(sb);
            sb.Append("<color=").Append(red).Append('>').Append(Digits(MinutesSeconds(seconds))).Append("</color>");
            return sb.ToString();
        }

        private static string BuildStatusText(StatusKind kind, Color color, string name, int tick)
        {
            string c = ColorTag(color);
            string head = kind switch
            {
                StatusKind.Bleeding => PlayerFacingMessages.HudLabels.BleedingOut,
                StatusKind.BeingRevived => PlayerFacingMessages.HudLabels.BeingRevived,
                StatusKind.Reviving => PlayerFacingMessages.HudLabels.Reviving,
                _ => PlayerFacingMessages.HudLabels.Invulnerable
            };

            var sb = new System.Text.StringBuilder(160);
            sb.Append("<color=").Append(c).Append("><b>").Append(head).Append("</b></color>");

            if (kind == StatusKind.Bleeding)
            {
                Separator(sb);
                sb.Append("<color=").Append(TextTag).Append('>').Append(PlayerFacingMessages.HudLabels.LivesLeft(_lives)).Append("</color>");
            }
            else if (!string.IsNullOrEmpty(name))
            {
                Separator(sb);
                sb.Append("<color=").Append(TextTag).Append("><noparse>").Append(name).Append("</noparse></color>");
            }

            Separator(sb);
            sb.Append("<color=").Append(c).Append('>').Append(Digits(kind == StatusKind.Bleeding ? MinutesSeconds(tick) : Tenths(tick))).Append("</color>");
            return sb.ToString();
        }

        private static void RenderHold()
        {
            if (!_holdKind.HasValue)
            {
                _hold.SetActive(false);
                _holdKey = null;
                _holdTick = -1;
                return;
            }

            var kind = _holdKind.Value;
            float remaining = _holdTimer != null ? Mathf.Max(0f, (float)_holdTimer.GetTimeSpan().TotalSeconds) : 0f;
            Color color = kind switch
            {
                HoldKind.SelfRevive => Blue,
                HoldKind.GiveUp => Red,
                HoldKind.ReviveTeammate => Cyan,
                _ => Green
            };

            _hold.SetActive(true);

            int tick = Mathf.CeilToInt(remaining * 10f);
            string key = $"{kind}|{_holdName}";
            if (key != _holdKey || tick != _holdTick)
            {
                bool layoutChanged = key != _holdKey;
                _holdKey = key;
                _holdTick = tick;

                string head = kind switch
                {
                    HoldKind.SelfRevive => PlayerFacingMessages.HudLabels.RevivingYourself,
                    HoldKind.GiveUp => PlayerFacingMessages.HudLabels.GivingUp,
                    HoldKind.ReviveTeammate => PlayerFacingMessages.HudLabels.Reviving,
                    _ => PlayerFacingMessages.HudLabels.Healing
                };

                var sb = new System.Text.StringBuilder(120);
                sb.Append("<color=").Append(ColorTag(color)).Append("><b>").Append(head).Append("</b></color>");
                if (!string.IsNullOrEmpty(_holdName))
                    sb.Append(" <color=").Append(TextTag).Append("><noparse>").Append(_holdName).Append("</noparse></color>");
                sb.Append("  <color=").Append(TextTag).Append('>').Append(Digits(Tenths(tick))).Append("</color>");
                _hold.SetText(sb.ToString(), layoutChanged);
            }

            _hold.SetEdge(color, Mathf.Clamp01(1f - remaining / _holdTotal), true);
        }

        private static void RenderStrip()
        {
            if (_wheelStrip != WheelStrip.Hidden)
            {
                RenderWheelStrip();
                return;
            }

            bool hiddenByStatus = _overlayKind == StatusKind.BeingRevived || _overlayKind == StatusKind.Reviving;
            bool hint = _stripState == StripState.Hidden && HasLookHint;
            if ((_stripState == StripState.Hidden && !hint) || hiddenByStatus)
            {
                _strip.SetActive(false);
                _stripKey = null;
                return;
            }

            _strip.SetActive(true);

            var selfKey = KeepMeAliveSettings.SELF_REVIVAL_KEY.Value;
            var giveUpKey = KeepMeAliveSettings.GIVE_UP_KEY.Value;
            string key = $"{_stripState}|{_stripCost}|{_stripInteractKey}|{hint}|{_hintKey}|{_hintDrag}|{_hintRevive}|{_hintCost}|{_hintLootKey}|{selfKey}|{giveUpKey}|{_spectateHint}|{_spectateHeadcam}|{_spectateNightVision}";
            if (key != _stripKey)
            {
                _stripKey = key;
                _strip.SetText(BuildStripText(selfKey, giveUpKey), true);
            }
        }

        // The wheel's mouse controls: "[ LMB ] OPEN  [ RMB ] CLOSE" on the categories, "[ LMB ] USE  [ RMB ] BACK" on the items.
        private static void RenderWheelStrip()
        {
            _strip.SetActive(true);
            string key = $"Wheel|{_wheelStrip}";
            if (key == _stripKey) return;
            _stripKey = key;

            bool items = _wheelStrip == WheelStrip.Items;
            var sb = new System.Text.StringBuilder(120);
            KeyItem(sb, KeyLabel(KeyCode.Mouse0), items ? PlayerFacingMessages.HealWheel.Use : PlayerFacingMessages.HealWheel.Open);
            sb.Append("<space=1.8em>");
            KeyItem(sb, KeyLabel(KeyCode.Mouse1), items ? PlayerFacingMessages.HealWheel.Back : PlayerFacingMessages.HealWheel.Close);
            _strip.SetText(sb.ToString(), true);
        }

        private static string BuildStripText(KeyCode selfKey, KeyCode giveUpKey)
        {
            var sb = new System.Text.StringBuilder(200);

            if (_stripState == StripState.Hidden && HasLookHint)
            {
                bool revive = _hintRevive != StripState.Hidden;
                if (_hintDrag) HintItem(sb, PlayerFacingMessages.HudLabels.Tap, PlayerFacingMessages.HudLabels.Drag);
                if (_hintDrag && revive) sb.Append("<space=1.8em>");
                if (revive)
                {
                    HintItem(sb, PlayerFacingMessages.HudLabels.Hold, PlayerFacingMessages.HudLabels.Revive);
                    StripChips(sb, _hintRevive, _hintCost);
                }
                if (_hintLootKey != null)
                {
                    if (_hintDrag || revive) sb.Append("<space=1.8em>");
                    KeyItem(sb, _hintLootKey, PlayerFacingMessages.HudLabels.Loot);
                }
                return sb.ToString();
            }

            if (_stripInteractKey != null)
            {
                sb.Append("<color=").Append(GreyTag).Append("><size=80%>").Append(PlayerFacingMessages.HudLabels.Hold).Append("</size></color> ");
                KeyItem(sb, _stripInteractKey, PlayerFacingMessages.HudLabels.Revive);
                StripChips(sb, _stripState, _stripCost);
                return sb.ToString();
            }

            // While spectating the strip is the camera controls only (self-revive and give-up are off until the player
            // returns to their body).
            if (_spectateHint == SpectateHint.Active)
            {
                KeyItem(sb, KeyLabel(DownedSpectate.NextKey) + " / " + KeyLabel(DownedSpectate.PreviousKey), PlayerFacingMessages.HudLabels.Switch);
                sb.Append("<space=1.8em>");
                KeyItem(sb, KeyLabel(DownedSpectate.ViewKey), PlayerFacingMessages.HudLabels.Camera);
                string head = PlayerFacingMessages.HudLabels.Head;
                string third = PlayerFacingMessages.HudLabels.ThirdPerson;
                sb.Append(' ');
                State(sb, _spectateHeadcam ? Current(head) : head, _spectateHeadcam ? third : Current(third), " / ");
                sb.Append("<space=1.8em>");
                KeyItem(sb, KeyLabel(DownedSpectate.VisionKey), PlayerFacingMessages.HudLabels.NightVision);
                sb.Append(' ');
                if (_spectateNightVision)
                    sb.Append("<color=").Append(ColorTag(Green)).Append("><size=80%><b>").Append(PlayerFacingMessages.HudLabels.On).Append("</b></size></color>");
                else
                    sb.Append("<color=").Append(GreyTag).Append("><size=80%>").Append(PlayerFacingMessages.HudLabels.Off).Append("</size></color>");
                sb.Append("<space=1.8em>");
                KeyItem(sb, KeyLabel(DownedSpectate.ReturnKey), PlayerFacingMessages.HudLabels.ReturnToBody);
                return sb.ToString();
            }

            switch (_stripState)
            {
                case StripState.SelfReviveOff:
                    sb.Append("<color=").Append(Red.ToHex()).Append("><b>").Append(PlayerFacingMessages.HudLabels.WaitForTeammate).Append("</b></color>");
                    break;
                default:
                    StripItem(sb, true, selfKey, PlayerFacingMessages.HudLabels.SelfRevive);
                    StripChips(sb, _stripState, _stripCost);
                    break;
            }

            // Between self-revive and give-up, as in the mockup.
            if (_spectateHint == SpectateHint.Available)
            {
                sb.Append("<space=1.8em>");
                KeyItem(sb, KeyLabel(DownedSpectate.EnterKey), PlayerFacingMessages.HudLabels.Spectate);
            }

            sb.Append("<space=1.8em>");
            StripItem(sb, true, giveUpKey, PlayerFacingMessages.HudLabels.GiveUp);
            return sb.ToString();
        }

        private static void HintItem(System.Text.StringBuilder sb, string how, string label)
        {
            sb.Append("<color=").Append(GreyTag).Append("><size=80%>").Append(how).Append("</size></color> ");
            KeyItem(sb, _hintKey, label);
        }

        // What the revive costs, or why it can't be done. On the dragger's strip and the look hint the lives are the downed
        // teammate's (a revive never costs the reviver any).
        private static void StripChips(System.Text.StringBuilder sb, StripState state, int cost)
        {
            switch (state)
            {
                case StripState.Available when cost > 0:
                    Chip(sb, PlayerFacingMessages.HudLabels.Cost(cost), GreyTag);
                    break;
                case StripState.NoLives:
                    Chip(sb, PlayerFacingMessages.HudLabels.NotEnoughLives, Red.ToHex());
                    break;
                case StripState.NeedsStim:
                    Chip(sb, PlayerFacingMessages.HudLabels.NeedsStim, Red.ToHex());
                    break;
                case StripState.NeedsDefib:
                    Chip(sb, PlayerFacingMessages.HudLabels.NeedsDefib, Red.ToHex());
                    break;
            }
        }

        // The active choice of a pair is bright and bold, the other grey; the pair itself is small, like a state tag.
        private static string Current(string s) => "<color=" + TextTag + "><b>" + s + "</b></color>";

        private static void State(System.Text.StringBuilder sb, string first, string second, string separator) =>
            sb.Append("<color=").Append(GreyTag).Append("><size=80%>").Append(first).Append(separator).Append(second).Append("</size></color>");

        private static void KeyItem(System.Text.StringBuilder sb, string keyText, string label)
        {
            sb.Append("<color=").Append(GoldTag).Append("><b>[ ").Append(keyText).Append(" ]</b></color> ");
            sb.Append("<color=").Append(TextTag).Append('>').Append(label).Append("</color>");
        }

        private static void StripItem(System.Text.StringBuilder sb, bool hold, KeyCode key, string label)
        {
            if (hold)
                sb.Append("<color=").Append(GreyTag).Append("><size=80%>").Append(PlayerFacingMessages.HudLabels.Hold).Append("</size></color> ");
            sb.Append("<color=").Append(GoldTag).Append("><b>[ ").Append(KeyLabel(key)).Append(" ]</b></color> ");
            sb.Append("<color=").Append(TextTag).Append('>').Append(label).Append("</color>");
        }

        private static void Chip(System.Text.StringBuilder sb, string text, string colorTag)
        {
            sb.Append("  <mark=").Append(ChipTag).Append("><color=").Append(colorTag).Append("><b><size=80%> ")
              .Append(text).Append(" </size></b></color></mark>");
        }

        private static void Separator(System.Text.StringBuilder sb) =>
            sb.Append("  <color=").Append(DimTag).Append(">|</color>  ");

        //====================[ Text helpers ]====================
        // Bender's digits are proportional, so numbers are set monospaced to keep the bar from shifting.
        private static string Digits(string s) => "<mspace=0.56em>" + s + "</mspace>";

        private static string MinutesSeconds(int totalSeconds)
        {
            int total = Mathf.Max(0, totalSeconds);
            return (total / 60).ToString("00") + ":" + (total % 60).ToString("00");
        }

        private static string Tenths(int tenths)
        {
            int t = Mathf.Max(0, tenths);
            return (t / 10).ToString() + "." + (t % 10).ToString();
        }

        private static string ColorTag(Color c) => c.ToHex();

        private static string ToHex(this Color c) => "#" + ColorUtility.ToHtmlStringRGB(c);

        private static Color Hex(int rgb) =>
            new Color(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, 1f);

        //====================[ Build ]====================
        private static bool EnsureBuilt()
        {
            if (FikaBridge.IsHeadless()) return false;
            if (_root != null) return true;

            try
            {
                if (!MonoBehaviourSingleton<GameUI>.Instantiated) return false;
                var gameUi = MonoBehaviourSingleton<GameUI>.Instance;
                if (gameUi == null) return false;

                _root = new GameObject(RootName, typeof(RectTransform));
                UnityEngine.Object.DontDestroyOnLoad(_root);

                var canvas = _root.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.sortingOrder = SortingOrderAbove(gameUi);

                var scaler = _root.AddComponent<CanvasScaler>();
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = new Vector2(ReferenceWidth, ReferenceHeight);
                scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
                scaler.matchWidthOrHeight = 0.5f;

                _root.AddComponent<GraphicRaycaster>();
                _root.AddComponent<HudDriver>();

                // The blocker and the timers work without the font; only the bars need it.
                BuildBlocker();

                _status = _sub = _hold = _strip = null;
                if (_font == null) _font = ResolveFont();
                if (_font != null)
                {
                    float bar = FontPx * BarHeightEm;
                    float stripFont = FontPx * StripFontScale;
                    float subFont = FontPx * SubFontScale;
                    _status = Bar.Create(_root.transform, "Status", _font, FontPx, bar, FontPx * BarPadEm, true, StatusTopPx, true, FontPx * MinStatusWidthEm);
                    _sub = Bar.Create(_root.transform, "Sub", _font, subFont, subFont * SubHeightEm, subFont * SubPadEm * 2f, true, StatusTopPx + FontPx * SubOffsetEm, true, 0f);
                    _sub.SetActive(false);
                    _hold = Bar.Create(_root.transform, "Hold", _font, FontPx, bar, FontPx * BarPadEm, false, HoldBottomPx, false, FontPx * MinHoldWidthEm);
                    _strip = Bar.Create(_root.transform, "Strip", _font, stripFont, stripFont * BarHeightEm, stripFont * BarPadEm, false, StripBottomPx, false, 0f);
                    _status.SetActive(false);
                    _hold.SetActive(false);
                    _strip.SetActive(false);
                }

                LogCanvases(canvas);
                Plugin.LogSource.LogInfo($"[Hud] built (font '{(_font != null ? _font.name : "none")}', sortingOrder {canvas.sortingOrder})");
                return true;
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"[Hud] build failed: {ex}");
                if (_root != null) UnityEngine.Object.Destroy(_root);
                _root = null;
                return false;
            }
        }

        private static void BuildBlocker()
        {
            _blocker = new GameObject(BlockerName, typeof(RectTransform), typeof(Image));
            _blocker.transform.SetParent(_root.transform, false);
            var rect = _blocker.GetComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;

            var image = _blocker.GetComponent<Image>();
            image.color = new Color(0f, 0f, 0f, 0f);
            image.raycastTarget = true;
            _blocker.SetActive(false);
        }

        // Just above GameUI's own canvas, so the blocker covers the HUD but not menus placed higher.
        private static int SortingOrderAbove(GameUI gameUi)
        {
            var canvas = gameUi.GetComponent<Canvas>() ?? gameUi.GetComponentInParent<Canvas>();
            return canvas != null ? canvas.rootCanvas.sortingOrder + 1 : 0;
        }

        // Logged once per build so the sort order can be checked against every other canvas in the raid.
        private static void LogCanvases(Canvas ours)
        {
            try
            {
                foreach (var c in Resources.FindObjectsOfTypeAll<Canvas>().Where(c => c.isRootCanvas && c.gameObject.scene.IsValid()))
                {
                    Plugin.LogSource.LogDebug(
                        $"[Hud] canvas '{c.name}' mode={c.renderMode} sortingOrder={c.sortingOrder} active={c.gameObject.activeInHierarchy}{(c == ours ? " (ours)" : "")}");
                }
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogDebug($"[Hud] canvas listing failed: {ex.Message}");
            }
        }

        private static TMP_FontAsset ResolveFont()
        {
            var fonts = Resources.LoadAll<TMP_FontAsset>("UI/Fonts");
            var font = fonts.FirstOrDefault(f => f != null && f.name == FontAssetName);
            if (font == null && !_fontFailLogged)
            {
                _fontFailLogged = true;
                Plugin.LogSource.LogError(
                    $"[Hud] font '{FontAssetName}' not found in Resources/UI/Fonts ({string.Join(", ", fonts.Select(f => f.name))}); HUD stays hidden.");
            }
            return font;
        }

        //====================[ Bar ]====================
        // One flat bar: background, optional dot, rich text, a track and an edge that fills from the left.
        // The hold bar also tints its whole background up to the edge.
        private sealed class Bar
        {
            private RectTransform _rect;
            private RectTransform _wash;
            private RectTransform _edge;
            private Image _washImage;
            private Image _edgeImage;
            private Image _trackImage;
            private Image _dot;
            private TextMeshProUGUI _text;
            private float _textLeftPx;
            private float _textRightPx;
            private float _minWidth;
            private float _edgePx;
            private float _dotPx;

            private static Sprite _circle;
            private static Sprite _drop;

            public static Bar Create(Transform parent, string name, TMP_FontAsset font, float fontPx, float heightPx, float padPx,
                bool anchorTop, float edgeDistancePx, bool withDot, float minWidthPx)
            {
                float sidePadPx = padPx * 0.5f;
                var bar = new Bar { _textLeftPx = sidePadPx, _textRightPx = sidePadPx, _minWidth = minWidthPx, _edgePx = fontPx * EdgeEm };

                var go = new GameObject(name, typeof(RectTransform), typeof(Image));
                go.transform.SetParent(parent, false);
                bar._rect = go.GetComponent<RectTransform>();
                var bg = go.GetComponent<Image>();
                bg.color = BarColor;
                bg.raycastTarget = false;

                var anchor = new Vector2(0.5f, anchorTop ? 1f : 0f);
                bar._rect.anchorMin = bar._rect.anchorMax = bar._rect.pivot = anchor;
                bar._rect.anchoredPosition = new Vector2(0f, anchorTop ? -edgeDistancePx : edgeDistancePx);
                bar._rect.sizeDelta = new Vector2(minWidthPx, heightPx);

                // Tinted background up to the edge (hold bar only), then the faint track, then the edge.
                bar._washImage = NewImage(go.transform, "Wash", out bar._wash);
                bar._wash.anchorMin = Vector2.zero;
                bar._wash.anchorMax = new Vector2(0f, 1f);
                bar._wash.offsetMin = bar._wash.offsetMax = Vector2.zero;

                var track = bar._trackImage = NewImage(go.transform, "Track", out var trackRect);
                track.color = new Color(1f, 1f, 1f, 0.08f);
                trackRect.anchorMin = Vector2.zero;
                trackRect.anchorMax = new Vector2(1f, 0f);
                trackRect.offsetMin = Vector2.zero;
                trackRect.offsetMax = new Vector2(0f, bar._edgePx);

                bar._edgeImage = NewImage(go.transform, "Edge", out bar._edge);
                bar._edge.anchorMin = Vector2.zero;
                bar._edge.anchorMax = new Vector2(0f, 0f);
                bar._edge.offsetMin = Vector2.zero;
                bar._edge.offsetMax = new Vector2(0f, bar._edgePx);

                if (withDot)
                {
                    float dotPx = bar._dotPx = fontPx * DotEm;
                    bar._dot = NewImage(go.transform, "Dot", out var dotRect);
                    bar._dot.sprite = Circle();
                    dotRect.anchorMin = dotRect.anchorMax = new Vector2(0f, 0.5f);
                    dotRect.pivot = new Vector2(0.5f, 0.5f);
                    dotRect.sizeDelta = new Vector2(dotPx, dotPx);
                    dotRect.anchoredPosition = new Vector2(sidePadPx + dotPx * 0.5f, 0f);
                    bar._textLeftPx = sidePadPx + dotPx * 1.6f;
                }

                var textGo = new GameObject("Text", typeof(RectTransform));
                textGo.transform.SetParent(go.transform, false);
                var textRect = textGo.GetComponent<RectTransform>();
                textRect.anchorMin = Vector2.zero;
                textRect.anchorMax = Vector2.one;
                textRect.offsetMin = new Vector2(bar._textLeftPx, 0f);
                textRect.offsetMax = new Vector2(-bar._textRightPx, 0f);

                bar._text = textGo.AddComponent<TextMeshProUGUI>();
                bar._text.font = font;
                bar._text.fontSize = fontPx;
                bar._text.fontStyle = FontStyles.UpperCase;
                bar._text.characterSpacing = LetterSpacing;
                bar._text.alignment = TextAlignmentOptions.Center;
                bar._text.enableWordWrapping = false;
                bar._text.overflowMode = TextOverflowModes.Overflow;
                bar._text.richText = true;
                bar._text.raycastTarget = false;
                return bar;
            }

            public void SetActive(bool active)
            {
                if (_rect.gameObject.activeSelf != active) _rect.gameObject.SetActive(active);
            }

            public void SetText(string rich, bool resize)
            {
                _text.text = rich;
                if (!resize) return;

                _text.ForceMeshUpdate(true);
                float width = Mathf.Max(_minWidth, _text.preferredWidth + _textLeftPx + _textRightPx);
                _rect.sizeDelta = new Vector2(width, _rect.sizeDelta.y);
            }

            public void SetEdge(Color color, float fill01, bool wash)
            {
                _edgeImage.color = color;
                _edge.anchorMax = new Vector2(fill01, 0f);

                _washImage.enabled = wash;
                if (!wash) return;
                var washColor = color;
                washColor.a = 0.22f;
                _washImage.color = washColor;
                _wash.anchorMax = new Vector2(fill01, 1f);
            }

            public void SetTrack(bool visible)
            {
                if (_trackImage.enabled != visible) _trackImage.enabled = visible;
            }

            public void SetDot(Color color, float alpha)
            {
                if (_dot == null) return;
                color.a = alpha;
                _dot.color = color;
            }

            public void SetDotDrop(bool drop)
            {
                if (_dot == null) return;
                var sprite = drop ? Drop() : Circle();
                if (_dot.sprite == sprite) return;
                _dot.sprite = sprite;
                float px = drop ? _dotPx * DropScale : _dotPx;
                _dot.rectTransform.sizeDelta = new Vector2(px, px);
            }

            private static Image NewImage(Transform parent, string name, out RectTransform rect)
            {
                var go = new GameObject(name, typeof(RectTransform), typeof(Image));
                go.transform.SetParent(parent, false);
                rect = go.GetComponent<RectTransform>();
                var image = go.GetComponent<Image>();
                image.raycastTarget = false;
                return image;
            }

            // A blood drop: MathWorld's teardrop curve (x = cos t, y = sin t * sin^2(t / 2)) with the point at the top,
            // 4x4 supersampled.
            private static Sprite Drop()
            {
                if (_drop != null) return _drop;

                const int size = 32;
                const int ss = 4;
                float half = size * 0.5f, scale = half - 1f;
                var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
                var pixels = new Color32[size * size];
                for (int y = 0; y < size; y++)
                {
                    for (int x = 0; x < size; x++)
                    {
                        int hits = 0;
                        for (int sy = 0; sy < ss; sy++)
                        {
                            for (int sx = 0; sx < ss; sx++)
                            {
                                float u = (y + (sy + 0.5f) / ss - half) / scale;
                                float v = (x + (sx + 0.5f) / ss - half) / scale;
                                if (u > -1f && u < 1f)
                                {
                                    float t = Mathf.Acos(u);
                                    float s = Mathf.Sin(t * 0.5f);
                                    if (Mathf.Abs(v) <= Mathf.Sin(t) * s * s) hits++;
                                }
                            }
                        }
                        pixels[y * size + x] = new Color32(255, 255, 255, (byte)(255 * hits / (ss * ss)));
                    }
                }
                tex.SetPixels32(pixels);
                tex.Apply(false);
                _drop = Sprite.Create(tex, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), 100f);
                return _drop;
            }

            private static Sprite Circle()
            {
                if (_circle != null) return _circle;

                const int size = 32;
                var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
                var pixels = new Color32[size * size];
                float r = size * 0.5f;
                for (int y = 0; y < size; y++)
                {
                    for (int x = 0; x < size; x++)
                    {
                        float d = Mathf.Sqrt((x + 0.5f - r) * (x + 0.5f - r) + (y + 0.5f - r) * (y + 0.5f - r));
                        byte a = (byte)(Mathf.Clamp01(r - d) * 255f);
                        pixels[y * size + x] = new Color32(255, 255, 255, a);
                    }
                }
                tex.SetPixels32(pixels);
                tex.Apply(false);
                _circle = Sprite.Create(tex, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), 100f);
                return _circle;
            }
        }
    }

    //====================[ HudDriver ]====================
    internal sealed class HudDriver : MonoBehaviour
    {
        private void Update() => Hud.Tick();
    }
}
