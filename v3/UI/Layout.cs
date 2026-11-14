using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace Iridium.UI
{
    /// <summary>
    /// UGUI construction toolkit for the v3 panel: one place for theme tokens, rounded
    /// sprites and the per-widget look, mirroring the role IridiumLayout plays for IMGUI.
    /// Values match <c>Resources/ui/Colors.iml</c>; keep both in sync when retheming.
    /// </summary>
    public static class Layout
    {
        // ---- theme tokens (Colors.iml) ----
        public static readonly Color Backdrop = new Color(0f, 0f, 0f, 0.55f);
        public static readonly Color Window = Hex(0x151617);        // bg-default
        public static readonly Color WindowAlt = Hex(0x0D0E0F);     // bg-alt
        public static readonly Color Control = Hex(0x313338);        // element
        public static readonly Color ControlBorder = Hex(0x494F5C);  // bg-element-border
        public static readonly Color Accent = Hex(0xD973A5);         // primary
        public static readonly Color TextTitle = Hex(0xF8F9FA);      // text-title
        public static readonly Color TextSubtitle = Hex(0xF1F3F5);   // text-subtitle
        public static readonly Color TextNormal = Hex(0xE9ECEF);     // text-normal
        public static readonly Color TextMuted = Hex(0x7D7E7F);      // text-secondary
        public static readonly Color Separator = new Color(1f, 1f, 1f, 0.08f);

        // ---- metrics ----
        public const float TitleBarHeight = 40f;
        public const float WindowRadius = 16f;
        public const float ControlRadius = 8f;
        public const float Spacing = 8f;
        public const float ScrollbarWidth = 8f;

        public static Color Hex(int rgb)
            => new Color(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, 1f);

        // ---- font ----
        private static Font _uiFont;

        /// <summary>System CJK font when available, otherwise Unity's built-in Arial.</summary>
        public static Font UiFont
        {
            get
            {
                if (_uiFont != null) return _uiFont;
                _uiFont = TrySystemFont() ?? Resources.GetBuiltinResource<Font>("Arial.ttf");
                return _uiFont;
            }
        }

        private static Font TrySystemFont()
        {
            string[] candidates =
            {
                "Microsoft YaHei", "Noto Sans CJK SC", "Noto Sans CJK JP", "PingFang SC",
                "WenQuanYi Micro Hei", "Droid Sans Fallback", "SimHei",
            };
            foreach (var name in candidates)
            {
                try
                {
                    var font = Font.CreateDynamicFontFromOSFont(name, 14);
                    if (font != null) return font;
                }
                catch { }
            }
            return null;
        }

        // ---- sprites (cached) ----
        private static readonly Dictionary<string, Sprite> _spriteCache = new();

        /// <summary>Rounded 9-sliced white sprite (tint with Image.color).</summary>
        public static Sprite Rounded(float radius)
        {
            int r = Mathf.Max(Mathf.RoundToInt(radius), 1);
            var key = "rounded:" + r;
            if (_spriteCache.TryGetValue(key, out var cached)) return cached;

            var sprite = Iris.Iml.IrisGoRenderer.RoundedSprite(r);
            _spriteCache[key] = sprite;
            return sprite;
        }

        /// <summary>Full capsule sprite, tinted at draw time (horizontal 9-slice only).</summary>
        public static Sprite Pill(int height)
        {
            int h = Mathf.Max(height, 4);
            var key = "pill:" + h;
            if (_spriteCache.TryGetValue(key, out var cached)) return cached;

            var tex = Iris.Iml.GuiTextureFactory.GetRoundedRect(h * 3, h, h / 2, Color.white, null, 0);
            tex.hideFlags = HideFlags.HideAndDontSave;
            var sprite = Sprite.Create(tex, new UnityEngine.Rect(0, 0, tex.width, tex.height),
                new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect,
                new Vector4(h / 2f, h / 2f, 0f, 0f));
            sprite.hideFlags = HideFlags.HideAndDontSave;
            _spriteCache[key] = sprite;
            return sprite;
        }

        public static Sprite Circle(int size)
        {
            int s = Mathf.Max(size, 4);
            var key = "circle:" + s;
            if (_spriteCache.TryGetValue(key, out var cached)) return cached;

            var tex = Iris.Iml.GuiTextureFactory.GetCircle(s, Color.white);
            tex.hideFlags = HideFlags.HideAndDontSave;
            var sprite = Sprite.Create(tex, new UnityEngine.Rect(0, 0, tex.width, tex.height),
                new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect);
            sprite.hideFlags = HideFlags.HideAndDontSave;
            _spriteCache[key] = sprite;
            return sprite;
        }

        /// <summary>Checkmark glyph. The IMGUI texture is top-down, sprites sample bottom-up.</summary>
        public static Sprite Check(int size, Color color)
        {
            int s = Mathf.Max(size, 4);
            var key = "check:" + s + ":" + ColorUtility.ToHtmlStringRGBA(color);
            if (_spriteCache.TryGetValue(key, out var cached)) return cached;

            var source = Iris.Iml.GuiTextureFactory.GetCheckmark(s, color);
            int w = source.width, h = source.height;
            var pixels = source.GetPixels();
            var flipped = new Color[pixels.Length];
            for (int y = 0; y < h; y++)
                Array.Copy(pixels, (h - 1 - y) * w, flipped, y * w, w);

            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Bilinear,
            };
            tex.SetPixels(flipped);
            tex.Apply(false, true);

            var sprite = Sprite.Create(tex, new UnityEngine.Rect(0, 0, w, h),
                new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect);
            sprite.hideFlags = HideFlags.HideAndDontSave;
            _spriteCache[key] = sprite;
            return sprite;
        }

        // ---- primitive widgets ----

        public static RectTransform Node(Transform parent, string name)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var rect = go.GetComponent<RectTransform>();
            go.transform.SetParent(parent, false);
            return rect;
        }

        public static Image Panel(Transform parent, string name, Color color, float radius, bool raycast)
        {
            var rect = Node(parent, name);
            var image = rect.gameObject.AddComponent<Image>();
            image.sprite = radius > 0 ? Rounded(radius) : null;
            image.type = radius > 0 ? Image.Type.Sliced : Image.Type.Simple;
            image.color = color;
            image.raycastTarget = raycast;
            image.useGUILayout = false;
            Stretch(rect);
            return image;
        }

        public static Text Label(Transform parent, string value, int size, Color color,
            TextAnchor anchor = TextAnchor.MiddleLeft, bool wrap = true)
        {
            var rect = Node(parent, "Text");
            var text = rect.gameObject.AddComponent<Text>();
            text.font = UiFont;
            text.text = value ?? string.Empty;
            text.fontSize = size;
            text.color = color;
            text.alignment = anchor;
            text.supportRichText = true;
            text.raycastTarget = false;
            text.horizontalOverflow = wrap ? HorizontalWrapMode.Wrap : HorizontalWrapMode.Overflow;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            return text;
        }

        public static Button Button(Transform parent, string caption, Action onClick,
            bool primary = false, int fontSize = 14)
        {
            var image = Panel(parent, "Button", primary ? Accent : Control, ControlRadius, true);
            var button = image.gameObject.AddComponent<Button>();
            button.targetGraphic = image;

            var colors = button.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(1.08f, 1.08f, 1.08f, 1f);
            colors.pressedColor = new Color(0.88f, 0.88f, 0.88f, 1f);
            colors.selectedColor = Color.white;
            colors.fadeDuration = 0.06f;
            button.colors = colors;
            if (onClick != null) button.onClick.AddListener(() => onClick());

            var label = Label(image.transform, caption, fontSize,
                primary ? Color.white : TextNormal, TextAnchor.MiddleCenter, wrap: false);
            Stretch(label.rectTransform);
            return button;
        }

        public static void Stretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }
    }
}
