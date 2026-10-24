//====================[ Imports ]====================
using System;
using System.Collections.Generic;
using EFT;
using EFT.HealthSystem;
using EFT.InventoryLogic;
using EFT.UI.DragAndDrop;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace KeepMeAlive.Presentation.HUD
{
    //====================[ WheelSlot ]====================
    // What one slot of the team-heal wheel shows, built by TeamHealWheel. A category slot has an effect icon and a
    // sub-line; an item slot has the item (its icon, short name, how many are carried, and its full name and amount left
    // for the centre).
    internal enum WheelSubTone { Normal, Dim, Bad }

    internal sealed class WheelSlot
    {
        public string Label;
        public bool Enabled;

        // Category slots.
        public MedCategory Category;
        public string Sub;
        public WheelSubTone SubTone;

        // Item slots.
        public Item Item;
        public int Count;
        public string FullName;
        public string Amount;
    }

    //====================[ TeamHealWheelView ]====================
    // The team-heal wheel, drawn on the HUD's own canvas (Hud.TryGetLayer) to the approved mockup. The mockup draws the
    // wheel in a 670-unit square that is 40% of the screen wide, so at the 1920x1080 reference one unit is 768/670 px.
    // Back to front: a full-screen dim, the ring's segments (with the highlighted one's green wash and rim), icons, names
    // and sub-lines, the hub disc, the pointer, and the centre (a medical cross, or the highlighted item's full name over
    // a boxed amount left). Behind the HUD's own bars, so the strip below stays readable.
    internal sealed class TeamHealWheelView
    {
        //====================[ Layout (mockup units) ]====================
        private const float WheelPx = 768f;
        private const float Unit = WheelPx / 670f;
        private const float CenterAboveScreenMiddlePx = 1080f * 0.05f; // the wheel sits at 45% of the screen height

        private const float OuterRadius = 296f;
        private const float InnerRadius = 142f;
        private const float IconRadius = 218f;
        private const float HubRadius = InnerRadius - 8f;
        private const float HubStroke = 2f;
        private const float RimWidth = 6f;
        private const float GapDegrees = 1.4f;
        private const float MaxIconSize = 66f;
        private const float IconRaise = 14f;
        private const float PointerTip = InnerRadius - 12f;
        private const float PointerBase = InnerRadius - 26f;
        private const float PointerHalfSpread = 5f;
        // The pointer shows once the aim is this far out (of 1).
        private const float PointerMinStick = 0.1f;

        private const float NameFont = 15f;
        private const float SubFont = 12f;
        private const float CountFont = 13f;

        //====================[ Layout (centre, reference px) ]====================
        // The mockup's centre text is 0.78 em of a font 1/26 of the wheel's width.
        private const float CenterFontPx = 23f;
        private const float CenterWidthPx = WheelPx * 0.38f;
        private const float TitleScale = 1.28f;
        private const float ChipScale = 0.72f; // 0.9 em chips at 0.8 em
        private const float CrossBoxEm = 5.2f;

        //====================[ Colours ]====================
        private static readonly Color DimScreen = new Color(0f, 0f, 0f, 0.42f);
        private static readonly Color SegmentHighlight = new Color(30f / 255f, 34f / 255f, 37f / 255f, 0.95f);
        private static readonly Color HubStrokeColor = new Color(1f, 1f, 1f, 0.08f);
        private static Color Wash => new Color(Hud.Green.r, Hud.Green.g, Hud.Green.b, 0.14f);
        private static readonly Color ItemIconTint = new Color(1f, 1f, 1f, 0.8f);

        //====================[ State ]====================
        private readonly GameObject _root;
        private readonly RectTransform _wheel;
        private readonly TMP_FontAsset _font;
        private readonly Transform _segmentLayer, _iconLayer, _textLayer;
        private readonly List<SlotView> _slotViews = new List<SlotView>();
        private readonly WheelPointerGraphic _pointer;
        private readonly GameObject _cross;
        private readonly TextMeshProUGUI _title;
        private readonly RectTransform _chip;
        private readonly TextMeshProUGUI _chipText;
        private readonly string _times;

        private int _shownCount = -1;
        private int _shownHighlight = -2;
        private WheelSlot _shownCenter;

        public bool IsAlive => _root != null;

        //====================[ Build ]====================
        public static TeamHealWheelView TryCreate()
        {
            if (!Hud.TryGetLayer(out var layer, out var font)) return null;
            try
            {
                return new TeamHealWheelView(layer, font);
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"[TeamHealWheelView] build failed: {ex}");
                return null;
            }
        }

        private TeamHealWheelView(RectTransform layer, TMP_FontAsset font)
        {
            _font = font;
            _times = font.HasCharacter('×') ? "×" : "x";

            _root = new GameObject("TeamHealWheel", typeof(RectTransform));
            _root.transform.SetParent(layer, false);
            _root.transform.SetAsFirstSibling();
            Stretch((RectTransform)_root.transform);

            var dim = NewChild<Image>(_root.transform, "Dim");
            Stretch(dim.rectTransform);
            dim.color = DimScreen;

            _wheel = NewRect(_root.transform, "Wheel");
            _wheel.anchorMin = _wheel.anchorMax = _wheel.pivot = new Vector2(0.5f, 0.5f);
            _wheel.sizeDelta = new Vector2(WheelPx, WheelPx);
            _wheel.anchoredPosition = new Vector2(0f, CenterAboveScreenMiddlePx);

            _segmentLayer = NewRect(_wheel, "Segments");
            _iconLayer = NewRect(_wheel, "Icons");
            _textLayer = NewRect(_wheel, "Texts");

            var hub = NewShape(_wheel, "Hub");
            hub.SetShape(0f, HubRadius * Unit, 0f, 360f);
            hub.color = Hud.BarColor;
            var hubStroke = NewShape(_wheel, "HubStroke");
            hubStroke.SetShape((HubRadius - HubStroke * 0.5f) * Unit, (HubRadius + HubStroke * 0.5f) * Unit, 0f, 360f);
            hubStroke.color = HubStrokeColor;

            _pointer = NewChild<WheelPointerGraphic>(_wheel, "Pointer");
            _pointer.color = Hud.TextColor;

            // The cross: the mockup's plus is two 10x32 bars in a 40-unit box.
            float box = CenterFontPx * CrossBoxEm;
            _cross = NewRect(_wheel, "Cross").gameObject;
            var vertical = NewChild<Image>(_cross.transform, "Vertical");
            vertical.color = Hud.Green;
            vertical.rectTransform.sizeDelta = new Vector2(box * 10f / 40f, box * 32f / 40f);
            var horizontal = NewChild<Image>(_cross.transform, "Horizontal");
            horizontal.color = Hud.Green;
            horizontal.rectTransform.sizeDelta = new Vector2(box * 32f / 40f, box * 10f / 40f);

            _title = NewText(_wheel, "Title", CenterFontPx * TitleScale, 10f, Hud.TextColor);
            _title.fontStyle = FontStyles.UpperCase | FontStyles.Bold;
            _title.enableWordWrapping = true;
            _title.rectTransform.sizeDelta = new Vector2(CenterWidthPx, 0f);

            var chipBorder = NewChild<Image>(_wheel, "Chip");
            chipBorder.sprite = BorderSprite();
            chipBorder.type = Image.Type.Sliced;
            chipBorder.color = Hud.DimColor;
            _chip = chipBorder.rectTransform;
            _chipText = NewText(_chip, "Text", CenterFontPx * ChipScale, 8f, Hud.GreyColor);
            _chipText.fontStyle = FontStyles.UpperCase | FontStyles.Bold;
            Stretch(_chipText.rectTransform);

            _root.SetActive(false);
        }

        //====================[ Show / Hide ]====================
        public void Show()
        {
            _shownCount = -1;
            _shownHighlight = -2;
            _shownCenter = null;
            _root.SetActive(true);
        }

        public void Hide()
        {
            if (_root == null) return;
            foreach (var slot in _slotViews) slot.UnbindIcon();
            _root.SetActive(false);
        }

        public void Destroy()
        {
            Hide();
            if (_root != null) UnityEngine.Object.Destroy(_root);
        }

        //====================[ Render ]====================
        // slotsChanged: the slot list was rebuilt since the last call (contents, not just the highlight).
        public void Render(List<WheelSlot> slots, bool slotsChanged, int highlight, Vector2 stick)
        {
            int n = slots.Count;
            if (n != _shownCount) Layout(n);

            if (slotsChanged || n != _shownCount || highlight != _shownHighlight)
            {
                for (int i = 0; i < n; i++)
                    _slotViews[i].Show(slots[i], i == highlight, slotsChanged || n != _shownCount);
                _shownHighlight = highlight;
            }
            _shownCount = n;

            if (stick.magnitude >= PointerMinStick)
            {
                _pointer.enabled = true;
                float angle = Mathf.Atan2(stick.x, stick.y) * Mathf.Rad2Deg;
                _pointer.SetShape(angle, PointerTip * Unit, PointerBase * Unit, PointerHalfSpread);
            }
            else _pointer.enabled = false;

            RenderCenter(highlight >= 0 && highlight < n && slots[highlight].Item != null ? slots[highlight] : null, slotsChanged);
        }

        // The cross, or the highlighted item's full name over its boxed amount left.
        private void RenderCenter(WheelSlot item, bool slotsChanged)
        {
            if (!slotsChanged && ReferenceEquals(item, _shownCenter)) return;
            _shownCenter = item;

            _cross.SetActive(item == null);
            _title.gameObject.SetActive(item != null);
            _chip.gameObject.SetActive(item != null);
            if (item == null) return;

            _title.text = item.FullName;
            _title.ForceMeshUpdate(true);
            float titleHeight = _title.preferredHeight;
            _title.rectTransform.sizeDelta = new Vector2(CenterWidthPx, titleHeight);

            _chipText.text = item.Amount;
            _chipText.ForceMeshUpdate(true);
            float chipFont = CenterFontPx * ChipScale;
            float chipHeight = _chipText.preferredHeight + chipFont * 0.1f + 2f;
            _chip.sizeDelta = new Vector2(_chipText.preferredWidth + chipFont * 1.1f + 2f, chipHeight);

            float gap = CenterFontPx * 0.3f;
            float total = titleHeight + gap + chipHeight;
            _title.rectTransform.anchoredPosition = new Vector2(0f, total * 0.5f - titleHeight * 0.5f);
            _chip.anchoredPosition = new Vector2(0f, -total * 0.5f + chipHeight * 0.5f);
        }

        // Slot i is centred at i * slice degrees clockwise from the top.
        private void Layout(int n)
        {
            while (_slotViews.Count < n) _slotViews.Add(new SlotView(this, _slotViews.Count));
            for (int i = 0; i < _slotViews.Count; i++) _slotViews[i].SetActive(i < n);
            if (n == 0) return;

            float slice = 360f / n;
            // Icon size: the mockup's min(66, R_ICON * sin(pi / max(n, 3))).
            float size = Mathf.Min(MaxIconSize, IconRadius * Mathf.Sin(Mathf.PI / Mathf.Max(n, 3)));
            for (int i = 0; i < n; i++)
                _slotViews[i].Place(n == 1 ? 0f : i * slice - slice / 2f + GapDegrees / 2f, n == 1 ? 360f : slice - GapDegrees, i * slice, size);
        }

        //====================[ SlotView ]====================
        private sealed class SlotView
        {
            private readonly WheelSegmentGraphic _segment, _wash, _rim;
            private readonly Image _icon;
            private readonly TextMeshProUGUI _name, _sub, _count;
            private readonly string _times;

            private Item _boundItem;
            private ItemIcon _itemIcon;
            private Action _unbind;
            private MedCategory? _boundCategory;

            public SlotView(TeamHealWheelView view, int index)
            {
                _times = view._times;
                _segment = NewShape(view._segmentLayer, $"Segment{index}");
                _wash = NewShape(view._segmentLayer, $"Wash{index}");
                _wash.color = Wash;
                _rim = NewShape(view._segmentLayer, $"Rim{index}");
                _rim.color = Hud.Green;

                _icon = NewChild<Image>(view._iconLayer, $"Icon{index}");
                _icon.preserveAspect = true;

                _name = view.NewText(view._textLayer, $"Name{index}", NameFont * Unit, 8f, Hud.GreyColor);
                _name.fontStyle = FontStyles.UpperCase | FontStyles.Bold;
                _sub = view.NewText(view._textLayer, $"Sub{index}", SubFont * Unit, 10f, Hud.GreyColor);
                _sub.fontStyle = FontStyles.UpperCase | FontStyles.Bold;
                _count = view.NewText(view._textLayer, $"Count{index}", CountFont * Unit, 4f, Hud.GreyColor);
                _count.fontStyle = FontStyles.Bold;
                _count.alignment = TextAlignmentOptions.Left;
                _count.rectTransform.pivot = new Vector2(0f, 0.5f);
            }

            public void SetActive(bool active)
            {
                if (!active) UnbindIcon();
                _segment.gameObject.SetActive(active);
                _wash.gameObject.SetActive(active);
                _rim.gameObject.SetActive(active);
                _icon.gameObject.SetActive(active);
                _name.gameObject.SetActive(active);
                _sub.gameObject.SetActive(active);
                _count.gameObject.SetActive(active);
            }

            // Geometry: the segment's start and sweep (degrees clockwise from the top), its centre angle and icon size.
            public void Place(float start, float sweep, float centre, float iconSize)
            {
                _segment.SetShape(InnerRadius * Unit, OuterRadius * Unit, start, sweep);
                _wash.SetShape(InnerRadius * Unit, OuterRadius * Unit, start, sweep);
                _rim.SetShape((OuterRadius - RimWidth) * Unit, OuterRadius * Unit, start, sweep);

                // Mockup positions are in its y-down units around the slot's icon point; UI y is up.
                Vector2 c = WheelSegmentGraphic.Direction(centre) * IconRadius;
                _icon.rectTransform.anchoredPosition = new Vector2(c.x, c.y + IconRaise) * Unit;
                _icon.rectTransform.sizeDelta = new Vector2(iconSize, iconSize) * Unit;
                _name.rectTransform.anchoredPosition = new Vector2(c.x, c.y - iconSize / 2f - 0.75f) * Unit;
                _sub.rectTransform.anchoredPosition = new Vector2(c.x, c.y - iconSize / 2f - 19.8f) * Unit;
                _count.rectTransform.anchoredPosition = new Vector2(c.x + iconSize / 2f - 2f, c.y - iconSize / 2f + 20.5f) * Unit;
            }

            public void Show(WheelSlot slot, bool highlighted, bool contentChanged)
            {
                bool lit = highlighted && slot.Enabled;
                _segment.color = highlighted ? SegmentHighlight : Hud.BarColor;
                _wash.enabled = lit;
                _rim.enabled = lit;

                if (contentChanged)
                {
                    _name.text = slot.Label;
                    _sub.text = slot.Sub ?? string.Empty;
                    _count.text = slot.Item != null && slot.Count > 1 ? _times + slot.Count : string.Empty;
                    if (slot.Item != null) BindItemIcon(slot.Item);
                    else BindCategoryIcon(slot.Category);
                }

                _name.color = !slot.Enabled ? Hud.DimColor : lit ? Hud.TextColor : Hud.GreyColor;
                _sub.color = slot.SubTone == WheelSubTone.Bad ? Hud.Red : slot.SubTone == WheelSubTone.Dim ? Hud.DimColor : Hud.GreyColor;
                // Item icons are the game's colour renders, so they are only faded; category icons are tinted like the text.
                _icon.color = slot.Item != null
                    ? (lit ? Color.white : ItemIconTint)
                    : (!slot.Enabled ? Hud.DimColor : lit ? Color.white : Hud.GreyColor);
            }

            // The game's icon for the item; some are rendered later, so the sprite follows the icon's Changed event.
            private void BindItemIcon(Item item)
            {
                if (ReferenceEquals(item, _boundItem)) return;
                UnbindIcon();
                _boundItem = item;
                try
                {
                    _itemIcon = ItemViewFactory.LoadItemIcon(item);
                    _unbind = _itemIcon.Changed.Bind(ApplyItemSprite);
                }
                catch (Exception ex)
                {
                    Plugin.LogSource.LogWarning($"[TeamHealWheelView] item icon for {item.TemplateId} failed: {ex.Message}");
                    _icon.enabled = false;
                }
            }

            private void ApplyItemSprite()
            {
                if (_icon == null || _itemIcon == null) return;
                _icon.sprite = _itemIcon.Sprite;
                _icon.enabled = _itemIcon.Sprite != null;
            }

            private void BindCategoryIcon(MedCategory category)
            {
                if (_boundCategory == category && _boundItem == null) return;
                UnbindIcon();
                _boundCategory = category;
                var sprite = CategoryIcon(category);
                _icon.sprite = sprite;
                _icon.enabled = sprite != null;
            }

            public void UnbindIcon()
            {
                _unbind?.Invoke();
                _unbind = null;
                _itemIcon = null;
                _boundItem = null;
                _boundCategory = null;
            }
        }

        //====================[ Category Icons ]====================
        // The game's own status-effect icons; Health has none, so it uses the HP icon from item tooltips. A missing
        // sprite leaves the slot without an icon.
        private static Sprite CategoryIcon(MedCategory category)
        {
            try
            {
                var icons = EFTHardSettings.Instance?.StaticIcons;
                if (icons == null) return null;
                var effects = icons.EffectIcons;
                switch (category)
                {
                    case MedCategory.Bleeds: return effects?.HeavyBleeding;
                    case MedCategory.Breaks: return effects?.Fracture;
                    case MedCategory.Comfort: return effects?.Pain;
                    case MedCategory.Nutrition: return effects?.Dehydration;
                    case MedCategory.Health:
                        return icons.HealEffectSprites != null && icons.HealEffectSprites.TryGetValue(EHealthFactorType.Health, out var hp) ? hp : null;
                }
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogWarning($"[TeamHealWheelView] category icon for {category} failed: {ex.Message}");
            }
            return null;
        }

        //====================[ Helpers ]====================
        private TextMeshProUGUI NewText(Transform parent, string name, float sizePx, float spacing, Color color)
        {
            var text = NewChild<TextMeshProUGUI>(parent, name);
            text.font = _font;
            text.fontSize = sizePx;
            text.characterSpacing = spacing;
            text.color = color;
            text.alignment = TextAlignmentOptions.Center;
            text.enableWordWrapping = false;
            text.overflowMode = TextOverflowModes.Overflow;
            text.richText = false;
            text.rectTransform.sizeDelta = new Vector2(200f, sizePx * 1.4f);
            return text;
        }

        private static WheelSegmentGraphic NewShape(Transform parent, string name)
        {
            var shape = NewChild<WheelSegmentGraphic>(parent, name);
            shape.rectTransform.sizeDelta = Vector2.zero;
            return shape;
        }

        private static T NewChild<T>(Transform parent, string name) where T : Graphic
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var graphic = go.AddComponent<T>();
            graphic.raycastTarget = false;
            var rect = graphic.rectTransform;
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0.5f);
            return graphic;
        }

        private static RectTransform NewRect(Transform parent, string name)
        {
            var rect = new GameObject(name, typeof(RectTransform)).GetComponent<RectTransform>();
            rect.SetParent(parent, false);
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = Vector2.zero;
            return rect;
        }

        private static void Stretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }

        // A 1 px outline for the amount chip, nine-sliced to any size.
        private static Sprite _border;

        private static Sprite BorderSprite()
        {
            if (_border != null) return _border;
            const int size = 4;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Point };
            var pixels = new Color32[size * size];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                    pixels[y * size + x] = new Color32(255, 255, 255, (byte)(x == 0 || y == 0 || x == size - 1 || y == size - 1 ? 255 : 0));
            tex.SetPixels32(pixels);
            tex.Apply(false);
            // 100 px per unit matches the canvas's reference, so the 1 px border draws 1 reference px wide.
            _border = Sprite.Create(tex, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect, new Vector4(1f, 1f, 1f, 1f));
            return _border;
        }
    }
}
