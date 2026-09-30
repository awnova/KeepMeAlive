//====================[ Imports ]====================
using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Comfort.Common;
using EFT.UI;
using EFT.Communications;

namespace KeepMeAlive.Helpers
{
    //====================[ VFX_UI ]====================
    // Borrows two native HUD panels for KMA prompts:
    //   Transit   = GameUI.LocationTransitTimerPanel (upper-center bleed-out / reviving bar)
    //   Objective = GameUI.BattleUiPanelExtraction    (bottom-center hold prompts)
    // Every native property we touch is snapshotted on first show and restored on hide, so
    // EFT/Fika's own use of these panels (transit countdown, extraction prompts) is untouched.
    public static class VFX_UI
    {
        //====================[ ColorSpec ]====================
        public readonly struct ColorSpec
        {
            public readonly Color From;
            public readonly Color To;
            public readonly bool  IsGradient;

            private ColorSpec(Color from, Color to, bool isGradient)
            {
                From = from;
                To = to;
                IsGradient = isGradient;
            }

            public static ColorSpec Solid(Color c)                 => new ColorSpec(c, c, false);
            public static ColorSpec Gradient(Color from, Color to) => new ColorSpec(from, to, true);
            public static implicit operator ColorSpec(Color c)     => Solid(c);
        }

        public static ColorSpec Gradient(Color from, Color to) => ColorSpec.Gradient(from, to);

        //====================[ Slots ]====================
        private static readonly PanelSlot s_transit = new PanelSlot
        {
            Name      = "TransitPanel",
            GetPanel  = ui => ui.LocationTransitTimerPanel,
            GetLabel  = p => ((LocationTransitTimerPanel)p)._extractionLabel,
            // Display() rather than Show(float): Show starts the native Co_Countdown, which would
            // overwrite our label every frame. Alpha reset covers a native fade interrupted by Close().
            Open      = (p, _) => { p.Display(); if (p._infoPanel != null) p._infoPanel.alpha = 1f; },
            Anchor    = new Vector2(0.5f, 0.5f),
            Offset    = new Vector2(0f, 220f),  // raised above the CharacterHealthPanel body icons
            // Native Co_Countdown sizes the label to its preferred width; we need a fixed box.
            FixedSize = new Vector2(318f, 51f),
            // Left, not Center: the label mixes a centered line (inline <align="center">, see
            // PlayerFacingMessages.Downed.BleedingOut) with a countdown that already fills the box.
            Alignment = TextAlignmentOptions.Left,
        };

        private static readonly PanelSlot s_objective = new PanelSlot
        {
            Name      = "ObjectivePanel",
            GetPanel  = ui => ui.BattleUiPanelExtraction,
            GetLabel  = p => ((BattleUIPanelExtraction)p)._extractionLabel,
            Open      = (p, label) => ((BattleUIPanelExtraction)p).Show(label),
            Anchor    = new Vector2(0.5f, 0f),
            Offset    = new Vector2(0f, 50f),
            Alignment = TextAlignmentOptions.Center,
            HideGlow  = true,
        };

        static VFX_UI()
        {
            try
            {
                var go = new GameObject("VFX_UI_Driver");
                UnityEngine.Object.DontDestroyOnLoad(go);
                go.AddComponent<VfxUiDriver>();
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"[VFX_UI] driver creation failed: {ex.Message}");
            }
        }

        //====================[ Public API ]====================
        public static void Text(Color color, string message) =>
            NotificationManager.DisplayMessageNotification(
                message, ENotificationDurationType.Long, ENotificationIconType.Default, color);

        public static CustomTimer TransitPanel(ColorSpec color, string label, float seconds = 0f) =>
            s_transit.Show(color, label, seconds);

        public static CustomTimer ObjectivePanel(ColorSpec color, string label, float seconds = 0f) =>
            s_objective.Show(color, label, seconds);

        public static void HideTransitPanel()   => s_transit.Hide();
        public static void HideObjectivePanel() => s_objective.Hide();

        public static void HideAll()
        {
            s_transit.Hide();
            s_objective.Hide();
        }

        // Pre-initializes panel references during raid load.
        public static bool TryWarmupPanels()
        {
            var ui = MonoBehaviourSingleton<GameUI>.Instance;
            if (ui == null || !ui.gameObject.activeInHierarchy) return false;

            bool transit   = s_transit.TryBind(ui);
            bool objective = s_objective.TryBind(ui);
            return transit && objective;
        }

        //====================[ Driver Tick ]====================
        internal static void InternalUpdate()
        {
            float tPing = Mathf.PingPong(Time.unscaledTime / 7.5f, 1f); // 15s ping-pong gradient loop
            s_transit.Tick(tPing);
            s_objective.Tick(tPing);
        }

        private static Color LerpPreserveA(Color a, Color b, float t)
        {
            var c = Color.Lerp(a, b, t);
            c.a = a.a;
            return c;
        }

        //====================[ PanelSlot ]====================
        private sealed class PanelSlot
        {
            // Config
            public string Name;
            public Func<GameUI, BattleUIPanel> GetPanel;
            public Func<BattleUIPanel, TextMeshProUGUI> GetLabel;
            public Action<BattleUIPanel, string> Open;
            public Vector2 Anchor;
            public Vector2 Offset;
            public Vector2? FixedSize;
            public TextAlignmentOptions Alignment;
            public bool HideGlow;

            // Bound refs
            private BattleUIPanel     _panel;
            private RectTransform     _rect;
            private Image             _bg;
            private Image             _glow;
            private ContentSizeFitter _fitter;
            private TextMeshProUGUI   _text;

            // Native snapshot, taken when we take ownership, restored on Hide
            private bool    _owned;
            private Vector2 _nAnchorMin, _nAnchorMax, _nPivot, _nPos, _nSize;
            private ContentSizeFitter.FitMode _nFit;
            private Color   _nBg;
            private bool    _nGlow;
            private float   _nFontSize;
            private bool    _nAutoSize;
            private TextAlignmentOptions _nAlign;

            // Runtime
            private CustomTimer _timer;
            private ColorSpec   _color;
            private string      _label;
            private bool        _loop;

            public bool TryBind(GameUI ui)
            {
                var panel = GetPanel(ui);
                if (panel == null) return false;
                if (panel == _panel && _rect != null) return true;

                // Refresh references and snapshot for the current panel instance.
                StopTimer();
                _owned = false;
                _loop  = false;

                _panel  = panel;
                _rect   = panel.transform.childCount > 0 ? panel.transform.GetChild(0) as RectTransform : null;
                _bg     = _rect != null ? _rect.GetComponent<Image>() : null;
                _fitter = _rect != null ? _rect.GetComponent<ContentSizeFitter>() : null;
                _text   = GetLabel(panel);
                _glow   = HideGlow ? FindGlow() : null;
                return _rect != null;
            }

            public CustomTimer Show(ColorSpec color, string label, float seconds)
            {
                var ui = MonoBehaviourSingleton<GameUI>.Instance;
                if (ui == null || !ui.gameObject.activeInHierarchy || !TryBind(ui)) return null;

                try
                {
                    StopTimer();
                    if (!_owned)
                    {
                        Snapshot();
                        _owned = true;
                    }

                    Open(_panel, label);
                    ApplyStyle();
                    if (_glow != null) _glow.enabled = false;

                    _color = color;
                    _label = label;
                    if (_bg != null) _bg.color = color.From;
                    if (_text != null) _text.text = label;

                    if (seconds <= 0f)
                    {
                        _loop = color.IsGradient;
                        return null;
                    }

                    _loop = false;
                    var timer = _timer = new CustomTimer();
                    // Subscribe before StartCountdown so its initial tick formats the label immediately.
                    timer.OnTick      += (span, formatted, p01) => OnTimerTick(timer, span, formatted, p01);
                    timer.OnCompleted += () => OnTimerDone(timer);
                    timer.StartCountdown(seconds, label);
                    return timer;
                }
                catch (Exception ex)
                {
                    Plugin.LogSource.LogError($"[VFX_UI.{Name}] {ex.Message}");
                    return null;
                }
            }

            public void Hide()
            {
                StopTimer();
                _loop = false;
                if (!_owned) return; // never close a panel EFT/Fika is showing
                _owned = false;

                try
                {
                    if (_panel != null)
                    {
                        _panel.Close();
                        Restore();
                    }
                }
                catch (Exception ex)
                {
                    Plugin.LogSource.LogWarning($"[VFX_UI.{Name}] hide warn: {ex.Message}");
                }
            }

            public void Tick(float tPing)
            {
                if (!_owned) return;

                // Timer keeps running even if EFT/Fika Close()d the panel under us, so it still completes.
                if (_timer != null && _timer.IsRunning) _timer.Update();
                if (!_owned || _panel == null || !_panel.isActiveAndEnabled) return; // completion may have hidden us

                // Re-stamped while owned: the host panels were observed resetting these after a single set.
                if (_text != null) _text.alignment = Alignment;
                ApplyAnchor();

                if (_loop && _bg != null) _bg.color = LerpPreserveA(_color.From, _color.To, tPing);
            }

            private void OnTimerTick(CustomTimer timer, TimeSpan span, string formatted, float p01)
            {
                if (timer != _timer) return;

                if (_text != null) _text.text = FormatLabel(_label, span, formatted);
                if (_bg != null)
                {
                    _bg.color = _color.IsGradient && p01 >= 0f
                        ? LerpPreserveA(_color.From, _color.To, p01)
                        : _color.From;
                }
            }

            private void OnTimerDone(CustomTimer timer)
            {
                if (timer != _timer) return; // Ignore callbacks from earlier timers.
                _timer = null;
                Hide();
            }

            // Clear the timer reference before stopping it.
            private void StopTimer()
            {
                var t = _timer;
                _timer = null;
                t?.Stop();
            }

            private static string FormatLabel(string label, TimeSpan span, string formatted)
            {
                if (label.IndexOf('{') >= 0)
                {
                    try { return string.Format(label, Math.Max(0f, (float)span.TotalSeconds)); }
                    catch (FormatException) { }
                }
                return $"{label}: {formatted}";
            }

            private void ApplyStyle()
            {
                ApplyAnchor();
                if (FixedSize.HasValue && _rect != null)
                {
                    if (_fitter != null) _fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
                    _rect.sizeDelta = FixedSize.Value;
                }
                if (_text != null)
                {
                    _text.enableAutoSizing = false;
                    _text.fontSize = 22f;
                    _text.alignment = Alignment;
                }
            }

            private void ApplyAnchor()
            {
                if (_rect == null) return;
                _rect.anchorMin = _rect.anchorMax = _rect.pivot = Anchor;
                _rect.anchoredPosition = Offset;
            }

            private void Snapshot()
            {
                if (_rect != null)
                {
                    _nAnchorMin = _rect.anchorMin;
                    _nAnchorMax = _rect.anchorMax;
                    _nPivot     = _rect.pivot;
                    _nPos       = _rect.anchoredPosition;
                    _nSize      = _rect.sizeDelta;
                }
                if (_fitter != null) _nFit = _fitter.horizontalFit;
                if (_bg != null) _nBg = _bg.color;
                if (_glow != null) _nGlow = _glow.enabled;
                if (_text != null)
                {
                    _nFontSize = _text.fontSize;
                    _nAutoSize = _text.enableAutoSizing;
                    _nAlign    = _text.alignment;
                }
            }

            private void Restore()
            {
                if (_rect != null)
                {
                    _rect.anchorMin        = _nAnchorMin;
                    _rect.anchorMax        = _nAnchorMax;
                    _rect.pivot            = _nPivot;
                    _rect.anchoredPosition = _nPos;
                    _rect.sizeDelta        = _nSize;
                }
                if (_fitter != null) _fitter.horizontalFit = _nFit;
                if (_bg != null) _bg.color = _nBg;
                if (_glow != null) _glow.enabled = _nGlow;
                if (_text != null)
                {
                    _text.fontSize         = _nFontSize;
                    _text.enableAutoSizing = _nAutoSize;
                    _text.alignment        = _nAlign;
                }
            }

            // Name match only: a size heuristic can grab the icon or a full-screen root Image.
            private Image FindGlow()
            {
                foreach (var img in _panel.GetComponentsInChildren<Image>(true))
                {
                    if (img != _bg && img.gameObject.name.IndexOf("glow", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        Plugin.LogSource.LogDebug($"[VFX_UI.{Name}] glow image: '{img.gameObject.name}'");
                        return img;
                    }
                }
                Plugin.LogSource.LogDebug($"[VFX_UI.{Name}] no 'glow' image found; glow left as-is");
                return null;
            }
        }
    }

    //====================[ VfxUiDriver ]====================
    internal sealed class VfxUiDriver : MonoBehaviour
    {
        private void Update() => VFX_UI.InternalUpdate();
    }
}
