using System;
using UnityEngine;
using static Iridium.UI.IridiumLayout;

namespace Iridium.UI
{
    /// <summary>
    /// IMGUI settings window (title bar, close, drag) wrapping the v2 settings UI.
    /// Mirrored in v3 as a UGUI window; the loaded Iridium assembly decides the flavour.
    /// </summary>
    public static class PanelShell
    {
        private const float TitleBarHeight = 40f;

        private static bool _visible;
        private static Rect _rect;
        private static Vector2 _scrollPos;
        private static bool _dragging;
        private static Vector2 _dragOffset;

        public static bool IsVisible => _visible;

        /// <summary>v2 renders everything through IMGUI, so there is no UGUI slot.</summary>
        public static RectTransform Content => null;

        public static void Show() => SetVisible(true);

        public static void SetVisible(bool visible)
        {
            if (_visible == visible) return;
            _visible = visible;
            if (!visible) Main.Settings?.Save();
        }

        public static void Toggle() => SetVisible(!_visible);

        /// <summary>Per-frame host hook: owns the panel hotkey.</summary>
        public static void Tick(float dt)
        {
            var hotkey = Main.Settings?.panelToggleHotkey;
            if (!string.IsNullOrEmpty(hotkey) && CheckHotkey(hotkey)) Toggle();
        }

        /// <summary>Draws the window chrome and invokes the IMGUI content inside it.</summary>
        public static void RenderGui(Action imGuiContent)
        {
            if (!_visible) return;

            EnsureTexturesAlive();

            // Keep the window on screen when the resolution shrinks.
            float w = Mathf.Min(Mathf.Max(Screen.width * 0.8f, 960f), Screen.width - 40f);
            float h = Mathf.Min(Mathf.Max(Screen.height * 0.8f, 720f), Screen.height - 40f);
            if (_rect.width <= 0f || _rect.height <= 0f)
                _rect = new Rect((Screen.width - w) / 2f, (Screen.height - h) / 2f, w, h);

            Render(
                Area(_rect,
                    VBox(
                        ContainerStyle.Background,
                        null,
                        WidthMax,
                        HBox(
                            ContainerStyle.None,
                            null,
                            WidthMax,
                            Text("Iridium", TextStyle.Title),
                            Fill(),
                            Button("\u00d7", ButtonStyle.Element, () => SetVisible(false),
                                GUILayout.Width(28), GUILayout.Height(28))
                        ),
                        Space(8),
                        ScrollView(_scrollPos, p => _scrollPos = p, WidthMax, () => imGuiContent?.Invoke())
                    )
                )
            );

            _rect.x = (int)_rect.x;
            _rect.y = (int)_rect.y;

            HandleWindowDrag();
        }

        public static void Dispose()
        {
            _visible = false;
            _dragging = false;
        }

        private static void HandleWindowDrag()
        {
            var e = Event.current;
            if (e == null) return;
            switch (e.type)
            {
                case EventType.MouseDown:
                    // 只在标题栏区域内才启动拖拽，排除关闭按钮区域
                    if (e.mousePosition.y >= _rect.y && e.mousePosition.y <= _rect.y + TitleBarHeight
                        && e.mousePosition.x < _rect.xMax - 36)
                    {
                        _dragging = true;
                        _dragOffset = e.mousePosition - _rect.position;
                        e.Use();
                    }
                    break;
                case EventType.MouseUp:
                    _dragging = false;
                    break;
                case EventType.MouseDrag:
                    if (_dragging)
                    {
                        _rect.position = e.mousePosition - _dragOffset;
                        e.Use();
                    }
                    break;
            }
        }

        private static bool CheckHotkey(string hotkey)
        {
            if (string.IsNullOrEmpty(hotkey)) return false;

            var parts = hotkey.Split('+');
            KeyCode? targetKey = null;
            bool needCtrl = false, needAlt = false, needShift = false;

            foreach (var part in parts)
            {
                var p = part.Trim();
                if (string.IsNullOrEmpty(p)) continue;

                switch (p.ToLowerInvariant())
                {
                    case "ctrl": needCtrl = true; break;
                    case "alt": needAlt = true; break;
                    case "shift": needShift = true; break;
                    default:
                        if (Enum.TryParse<KeyCode>(p, ignoreCase: true, out var kc)) targetKey = kc;
                        break;
                }
            }

            if (targetKey == null) return false;

            if (needCtrl && !(Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl))) return false;
            if (needAlt && !(Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt))) return false;
            if (needShift && !(Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift))) return false;

            return Input.GetKeyDown(targetKey.Value);
        }
    }
}