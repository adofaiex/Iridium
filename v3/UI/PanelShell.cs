using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Iridium.UI
{
    /// <summary>
    /// UGUI settings window (dimmer, title bar, drag/resize, content slot) hosting
    /// <see cref="Iris.Iml.IrisGoRenderer"/>.
    /// Mirrored in v2 as an IMGUI window: the loader assemblies are built against v2 but
    /// ship with every game version, so the loaded Iridium assembly decides the flavour.
    /// </summary>
    public static class PanelShell
    {
        private const float TitleBarHeight = 40f;
        private const float CloseButtonSize = 34f;
        private const float Margin = 20f;
        private static readonly Color BackdropColor = new Color(0f, 0f, 0f, 0.5f);
        private static readonly Color PanelColor = new Color(0x15 / 255f, 0x16 / 255f, 0x17 / 255f, 1f);
        private static readonly Color TitleColor = new Color(0xF8 / 255f, 0xF9 / 255f, 0xFA / 255f, 1f);
        private static readonly Color CloseColor = new Color(0xE9 / 255f, 0xEC / 255f, 0xEF / 255f, 1f);
        private static readonly Color CloseHover = new Color(0xD9 / 255f, 0x73 / 255f, 0xA5 / 255f, 1f);

        private static GameObject? _root;
        private static RectTransform? _panel;
        private static RectTransform? _content;
        private static Text? _titleLabel;
        private static bool _visible;
        private static bool _contentAttached;
        private static bool _geometryRestored;
        private static bool _interacting;
        private static bool _warnedMissingSettings;

        public static bool IsVisible => _visible;

        /// <summary>RectTransform the settings tree parents itself to. Null until shown.</summary>
        public static RectTransform? Content => _content;

        /// <summary>Called once when the mod is enabled / the panel is created.</summary>
        public static void Show()
        {
            EnsureShell();
            SetVisible(true);
        }

        public static void SetVisible(bool visible)
        {
            EnsureShell();
            if (_root == null) return; // Settings not ready yet; nothing to toggle.
            if (_visible == visible) return;
            _visible = visible;
            _root.SetActive(visible);
            if (!visible)
            {
                // Persist what the user changed before the panel went away: the IMGUI
                // path used to flush every frame, UGUI has no such frame.
                SaveGeometry();
            }
        }

        public static void Toggle() => SetVisible(!_visible);

        /// <summary>
        /// Per-frame host hook (wired from every loader's update). Owns the panel hotkey
        /// and keeps the settings tree in sync with the data context.
        /// </summary>
        public static void Tick(float dt)
        {
            // Stay lazy: nothing is built until the panel is actually opened.
            // While the user records a new hotkey in the settings UI, don't act on it —
            // otherwise binding e.g. Ctrl+F9 would immediately close the panel.
            bool capturing = Main.Settings != null && Main.Settings.IsCapturingHotkey;
            var hotkey = Main.Settings?.panelToggleHotkey;
            if (!capturing && !string.IsNullOrEmpty(hotkey) && CheckHotkey(hotkey!)) Toggle();

            if (_visible && _root != null) LayoutPanel();
        }

        /// <summary>
        /// IMGUI frame hook. v3 renders nothing through IMGUI; the hook only exists so
        /// loader code can stay identical across game versions.
        /// </summary>
        public static void RenderGui(Action imGuiContent)
        {
        }

        public static void Dispose()
        {
            if (_root != null) UnityEngine.Object.Destroy(_root);
            _root = null;
            _panel = null;
            _content = null;
            _titleLabel = null;
            _visible = false;
            _contentAttached = false;
            _geometryRestored = false;
        }

        // ---- construction ----

        private static void EnsureShell()
        {
            if (_root != null) return;
            if (Main.Settings == null)
            {
                if (!_warnedMissingSettings)
                {
                    _warnedMissingSettings = true;
                    Main.Logger?.Warning("[PanelShell] Settings not initialized, panel disabled");
                }
                return;
            }

            _root = new GameObject("IridiumPanel", typeof(RectTransform));
            UnityEngine.Object.DontDestroyOnLoad(_root);

            var canvas = _root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 32760;
            var scaler = _root.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;
            _root.AddComponent<GraphicRaycaster>();
            EnsureEventSystem();

            // Dimmer: swallows clicks so the panel behaves like a modal.
            var backdrop = new GameObject("Backdrop", typeof(RectTransform));
            var backdropImg = backdrop.AddComponent<Image>();
            backdropImg.color = BackdropColor;
            Stretch(backdrop.GetComponent<RectTransform>());
            backdrop.transform.SetParent(_root.transform, false);
            var blocker = backdrop.AddComponent<PanelDragRelay>();
            blocker.DragTarget = null; // click-only (dismiss)

            // Window
            _panel = new GameObject("Window", typeof(RectTransform)).GetComponent<RectTransform>();
            _panel.SetParent(_root.transform, false);
            var panelImg = _panel.gameObject.AddComponent<Image>();
            panelImg.color = PanelColor;
            // Rounded window, matching the IMGUI theme (Background container radius 16).
            // The sprite is a plain white 9-slice, so tinting keeps the theme color.
            panelImg.sprite = Iris.Iml.IrisGoRenderer.RoundedSprite(16);
            panelImg.type = Image.Type.Sliced;

            // Title bar
            var titleBar = new GameObject("TitleBar", typeof(RectTransform));
            var titleRect = titleBar.GetComponent<RectTransform>();
            titleRect.anchorMin = new Vector2(0f, 1f);
            titleRect.anchorMax = new Vector2(1f, 1f);
            titleRect.pivot = new Vector2(0.5f, 1f);
            titleRect.offsetMin = new Vector2(0f, -TitleBarHeight);
            titleRect.offsetMax = Vector2.zero;
            // A RectTransform alone is not clickable: UGUI raycasts against Graphics.
            // Without this invisible Image the title bar swallowed no pointer events and
            // the window could not be dragged.
            var titleHit = titleBar.AddComponent<Image>();
            titleHit.color = new Color(1f, 1f, 1f, 0.001f);
            titleHit.raycastTarget = true;
            titleHit.useGUILayout = false;
            titleBar.transform.SetParent(_panel, false);

            _titleLabel = new GameObject("Title", typeof(RectTransform)).AddComponent<Text>();
            _titleLabel.text = Main.Handler?.ModId ?? "Iridium";
            _titleLabel.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            _titleLabel.fontSize = 18;
            _titleLabel.color = TitleColor;
            _titleLabel.alignment = TextAnchor.MiddleLeft;
            _titleLabel.raycastTarget = false;
            var titleTextRect = _titleLabel.rectTransform;
            titleTextRect.anchorMin = Vector2.zero;
            titleTextRect.anchorMax = Vector2.one;
            titleTextRect.offsetMin = new Vector2(14f, 0f);
            titleTextRect.offsetMax = new Vector2(-(CloseButtonSize + 14f), 0f);
            _titleLabel.transform.SetParent(titleBar.transform, false);

            var drag = titleBar.AddComponent<PanelDragRelay>();
            drag.DragTarget = _panel;

            var closeGo = new GameObject("CloseButton", typeof(RectTransform));
            var closeImg = closeGo.AddComponent<Image>();
            closeImg.color = Color.clear;
            var closeBtn = closeGo.AddComponent<Button>();
            closeBtn.targetGraphic = closeImg;
            var closeColors = closeBtn.colors;
            closeColors.normalColor = Color.clear;
            closeColors.highlightedColor = CloseHover;
            closeColors.pressedColor = CloseHover;
            closeBtn.colors = closeColors;
            closeBtn.onClick.AddListener(() => SetVisible(false));
            var closeRect = closeGo.GetComponent<RectTransform>();
            closeRect.anchorMin = new Vector2(1f, 1f);
            closeRect.anchorMax = new Vector2(1f, 1f);
            closeRect.pivot = new Vector2(1f, 1f);
            closeRect.sizeDelta = new Vector2(CloseButtonSize, CloseButtonSize);
            closeRect.anchoredPosition = new Vector2(-6f, -3f);
            closeGo.transform.SetParent(titleBar.transform, false);

            var closeLabel = new GameObject("Label", typeof(RectTransform)).AddComponent<Text>();
            closeLabel.text = "\u00d7";
            closeLabel.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            closeLabel.fontSize = 28;
            closeLabel.color = CloseColor;
            closeLabel.alignment = TextAnchor.MiddleCenter;
            closeLabel.raycastTarget = false;
            Stretch(closeLabel.rectTransform);
            closeLabel.transform.SetParent(closeGo.transform, false);

            // Content area hosting the settings tree. The IML brings its own <ScrollView>
            // for the long option list, so the panel body itself does not scroll: one
            // scroll authority only (nested ScrollRects steal each other's wheel input
            // and make content height impossible to reason about).
            var contentGo = new GameObject("Content", typeof(RectTransform));
            var contentLayout = contentGo.AddComponent<VerticalLayoutGroup>();
            contentLayout.childControlWidth = true;
            contentLayout.childControlHeight = true;
            // "control" = use the child's preferred size; "forceExpand" = stretch it to
            // fill. Width must be forced (full-width rows), height must not (rows hug).
            contentLayout.childForceExpandWidth = true;
            contentLayout.childForceExpandHeight = false;
            contentLayout.childAlignment = TextAnchor.UpperLeft;
            contentLayout.padding = new RectOffset(0, 0, 0, 0);
            _content = contentGo.GetComponent<RectTransform>();
            _content.anchorMin = Vector2.zero;
            _content.anchorMax = Vector2.one;
            _content.pivot = new Vector2(0.5f, 1f);
            _content.offsetMin = new Vector2(0f, 0f);
            _content.offsetMax = new Vector2(0f, -TitleBarHeight);
            contentGo.transform.SetParent(_panel, false);

            // Resize handles (Windows-style): every edge and corner drags independently.
            // They live on the panel so they follow it, and corners are added last so they
            // win the raycast over the (overlapping) edge strips.
            const float edgeThickness = 7f;
            const float cornerSize = 14f;

            AddResizeHandle(_panel, "ResizeLeft",
                new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(0f, 0.5f),
                new Vector2(edgeThickness, 0f), Vector2.zero, ResizeEdge.Left);
            AddResizeHandle(_panel, "ResizeRight",
                new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(1f, 0.5f),
                new Vector2(edgeThickness, 0f), Vector2.zero, ResizeEdge.Right);
            AddResizeHandle(_panel, "ResizeTop",
                new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f),
                new Vector2(0f, edgeThickness), Vector2.zero, ResizeEdge.Top);
            AddResizeHandle(_panel, "ResizeBottom",
                new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0.5f, 0f),
                new Vector2(0f, edgeThickness), Vector2.zero, ResizeEdge.Bottom);

            AddResizeHandle(_panel, "ResizeTopLeft",
                new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(cornerSize, cornerSize), Vector2.zero, ResizeEdge.TopLeft);
            AddResizeHandle(_panel, "ResizeTopRight",
                new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(1f, 1f),
                new Vector2(cornerSize, cornerSize), Vector2.zero, ResizeEdge.TopRight);
            AddResizeHandle(_panel, "ResizeBottomLeft",
                new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(0f, 0f),
                new Vector2(cornerSize, cornerSize), Vector2.zero, ResizeEdge.BottomLeft);
            AddResizeHandle(_panel, "ResizeBottomRight",
                new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(1f, 0f),
                new Vector2(cornerSize, cornerSize), Vector2.zero, ResizeEdge.BottomRight);

            _root.SetActive(_visible);
            LayoutPanel();
        }

        private static void EnsureEventSystem()
        {
            // UGUI needs exactly one EventSystem to route pointer events. The game may
            // already provide one (or a legacy input module); only create ours if not.
            if (UnityEngine.Object.FindObjectOfType<EventSystem>() != null) return;
            var esGo = new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));
            UnityEngine.Object.DontDestroyOnLoad(esGo);
        }

        private static void Stretch(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }

        /// <summary>
        /// Restores the window geometry once, then leaves position/size to the user.
        /// </summary>
        private static void LayoutPanel()
        {
            if (_panel == null) return;

            if (!_geometryRestored)
            {
                _geometryRestored = true;
                RestoreGeometry();
            }

            // Never re-position the window while the user is dragging it: a per-frame
            // clamp would pull it away from the cursor.
            if (!_interacting) ClampPanel();

            AttachSettingsContent();

            // Reflow live: the content must follow the window edge while dragging.
            // (The list height is only touched when it really changes, see below.)
            FitSettingsScrollView();
        }

        private static void ClampPanel()
        {
            if (_panel == null) return;
            _panel.sizeDelta = new Vector2(
                Mathf.Max(_panel.sizeDelta.x, MinWidth),
                Mathf.Max(_panel.sizeDelta.y, MinHeight));
            _panel.anchoredPosition = ClampKeepVisible(_panel.anchoredPosition, _panel.sizeDelta);
        }

        /// <summary>
        /// Stretch the IML's own list <see cref="ScrollRect"/> to whatever vertical space
        /// is left in the window, so the list grows with the window (and with resizing)
        /// instead of sitting at the IML's fixed height.
        /// </summary>
        private static void FitSettingsScrollView()
        {
            if (_content == null || _panel == null) return;
            var scroll = _content.GetComponentInChildren<ScrollRect>(true);
            if (scroll == null) return;
            var le = scroll.GetComponent<LayoutElement>();
            if (le == null) return;

            // Title bar + search row + tab bar + status line, approximated as a share of the
            // window so it stays right when the window is resized or the UI is scaled.
            float chrome = Mathf.Clamp(_panel.rect.height * 0.22f, 150f, 280f);
            float wanted = Mathf.Max(_panel.rect.height - TitleBarHeight - chrome, 240f);
            if (Mathf.Abs(le.preferredHeight - wanted) < 0.5f) return;
            le.preferredHeight = wanted;
            le.minHeight = wanted;
        }

        /// <summary>Hand the settings renderer the scroll view's content slot (once).</summary>
        private static void AttachSettingsContent()
        {
            if (_contentAttached || _content == null || Main.Settings == null) return;
            if (!Main.Settings.AttachUi(_content)) return;
            _contentAttached = true;
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

        private static void AddResizeHandle(
            RectTransform panel, string name,
            Vector2 anchorMin, Vector2 anchorMax, Vector2 pivot,
            Vector2 size, Vector2 position, ResizeEdge edge)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var img = go.AddComponent<Image>();
            img.color = new Color(1f, 1f, 1f, 0.002f); // invisible but raycastable
            img.raycastTarget = true;
            img.useGUILayout = false;

            var rect = go.GetComponent<RectTransform>();
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.pivot = pivot;
            rect.sizeDelta = size;
            rect.anchoredPosition = position;

            go.AddComponent<PanelResizeRelay>().Init(panel, edge);
            go.transform.SetParent(panel, false);
        }

        private enum ResizeEdge { Left, Right, Top, Bottom, TopLeft, TopRight, BottomLeft, BottomRight }

        /// <summary>
        /// Windows-style edge/corner resizing: the opposite edge stays put, the result is
        /// clamped to the minimum size and to the canvas bounds.
        /// </summary>
        private class PanelResizeRelay : MonoBehaviour, IPointerDownHandler, IDragHandler, IPointerUpHandler
        {
            private RectTransform _target;
            private ResizeEdge _edge;
            private Vector2 _pressScreen;
            private Vector2 _pressSize;
            private Vector2 _pressPosition;

            public void Init(RectTransform target, ResizeEdge edge)
            {
                _target = target;
                _edge = edge;
            }

            public void OnPointerDown(PointerEventData eventData)
            {
                if (_target == null) return;
                _pressed = true;
                _interacting = true;
                _pressScreen = ScreenToCanvas(eventData);
                _pressSize = _target.sizeDelta;
                _pressPosition = _target.anchoredPosition;
            }

            public void OnDrag(PointerEventData eventData)
            {
                if (_target == null) return;

                var delta = ScreenToCanvas(eventData) - _pressScreen;
                float width = _pressSize.x;
                float height = _pressSize.y;
                float x = _pressPosition.x;
                float y = _pressPosition.y;

                bool left = _edge is ResizeEdge.Left or ResizeEdge.TopLeft or ResizeEdge.BottomLeft;
                bool right = _edge is ResizeEdge.Right or ResizeEdge.TopRight or ResizeEdge.BottomRight;
                bool top = _edge is ResizeEdge.Top or ResizeEdge.TopLeft or ResizeEdge.TopRight;
                bool bottom = _edge is ResizeEdge.Bottom or ResizeEdge.BottomLeft or ResizeEdge.BottomRight;

                if (left)
                {
                    float applied = Mathf.Min(delta.x, width - MinWidth); // stop at minimum width
                    width -= applied;
                    x += applied / 2f;
                }
                else if (right)
                {
                    width = Mathf.Max(width + delta.x, MinWidth);
                }

                if (top)
                {
                    height = Mathf.Max(height + delta.y, MinHeight);
                }
                else if (bottom)
                {
                    float applied = Mathf.Max(delta.y, MinHeight - height);
                    height -= applied;
                    y += applied / 2f;
                }

                // The grabbed edge tracks the cursor exactly: no clamping of the opposite
                // edge, so the window may extend past the screen like a real Windows
                // window (it used to be pushed back and felt laggy).
                _target.sizeDelta = new Vector2(width, height);
                _target.anchoredPosition = new Vector2(x, y);
            }

            public void OnPointerUp(PointerEventData eventData)
            {
                _pressed = false;
                _interacting = false;
                if (_target != null) _target.anchoredPosition = ClampKeepVisible(_target.anchoredPosition, _target.sizeDelta);
                SaveGeometry();
            }

            private bool _pressed;

            /// <summary>Screen point → canvas-local point, i.e. the same space as anchoredPosition.</summary>
            private Vector2 ScreenToCanvas(PointerEventData eventData)
            {
                var canvas = GetComponentInParent<Canvas>();
                if (canvas != null && canvas.rootCanvas != null)
                {
                    var canvasRect = canvas.rootCanvas.transform as RectTransform;
                    if (canvasRect != null && RectTransformUtility.ScreenPointToLocalPointInRectangle(
                            canvasRect, eventData.position, eventData.pressEventCamera, out var local))
                    {
                        return local;
                    }
                }
                return eventData.position;
            }
        }

        private const float MinWidth = 800f;
        private const float MinHeight = 560f;

        /// <summary>
        /// Keeps the window usable: at least <see cref="KeepVisible"/> canvas units of it
        /// stay on screen. Unlike a hard clamp this lets the window overhang the edges
        /// (Windows behaviour) so dragging the chrome does not fight the cursor.
        /// </summary>
        private static Vector2 ClampKeepVisible(Vector2 position, Vector2 size)
        {
            float canvasW = 1920f, canvasH = 1080f;
            if (_root != null && _root.transform is RectTransform canvasRect
                && canvasRect.rect.width > 1f && canvasRect.rect.height > 1f)
            {
                canvasW = canvasRect.rect.width;
                canvasH = canvasRect.rect.height;
            }

            const float KeepVisible = 120f;
            float limitX = Mathf.Max(0f, (canvasW + size.x) / 2f - KeepVisible);
            float limitY = Mathf.Max(0f, (canvasH + size.y) / 2f - KeepVisible);
            return new Vector2(
                Mathf.Clamp(position.x, -limitX, limitX),
                Mathf.Clamp(position.y, -limitY, limitY));
        }

        /// <summary>Restore-time clamp: keep a freshly restored window fully on screen.</summary>
        private static Vector2 ClampIntoCanvas(Vector2 position, Vector2 size)
        {
            float canvasW = 1920f, canvasH = 1080f;
            if (_root != null && _root.transform is RectTransform canvasRect
                && canvasRect.rect.width > 1f && canvasRect.rect.height > 1f)
            {
                canvasW = canvasRect.rect.width;
                canvasH = canvasRect.rect.height;
            }

            float limitX = Mathf.Max(0f, (canvasW - size.x) / 2f);
            float limitY = Mathf.Max(0f, (canvasH - size.y) / 2f);
            return new Vector2(
                Mathf.Clamp(position.x, -limitX, limitX),
                Mathf.Clamp(position.y, -limitY, limitY));
        }

        // ---- geometry persistence ----

        private static void SaveGeometry()
        {
            if (_panel == null || Main.Settings == null) return;
            Main.Settings.SetWindowRect(
                _panel.anchoredPosition.x, _panel.anchoredPosition.y,
                _panel.sizeDelta.x, _panel.sizeDelta.y);
            Main.Settings.Save();
        }

        /// <summary>Restore the saved window position/size, or use the default layout.</summary>
        private static void RestoreGeometry()
        {
            if (_panel == null) return;

            float canvasW = 1920f, canvasH = 1080f;
            if (_root != null && _root.transform is RectTransform canvasRect
                && canvasRect.rect.width > 1f && canvasRect.rect.height > 1f)
            {
                canvasW = canvasRect.rect.width;
                canvasH = canvasRect.rect.height;
            }

            _panel.anchorMin = new Vector2(0.5f, 0.5f);
            _panel.anchorMax = new Vector2(0.5f, 0.5f);
            _panel.pivot = new Vector2(0.5f, 0.5f);

            float x = 0f, y = 0f;
            float w = canvasW * 0.92f, h = canvasH * 0.9f;

            if (Main.Settings != null && Main.Settings.HasWindowRect)
            {
                Main.Settings.GetWindowRect(out x, out y, out w, out h);
                w = Mathf.Max(w, MinWidth);
                h = Mathf.Max(h, MinHeight);
            }

            _panel.sizeDelta = new Vector2(w, h);
            _panel.anchoredPosition = ClampIntoCanvas(new Vector2(x, y), _panel.sizeDelta);
        }

        /// <summary>Title-bar dragging / dimmer dismissal.</summary>
        private class PanelDragRelay : MonoBehaviour, IPointerDownHandler, IPointerClickHandler, IDragHandler, IPointerUpHandler
        {
            public RectTransform? DragTarget;

            private Vector2 _pressScreenPos;
            private Vector2 _pressAnchoredPos;
            private bool _dragging;

            public void OnPointerDown(PointerEventData eventData)
            {
                _dragging = false;
                if (DragTarget == null) return;
                _pressScreenPos = ScreenToCanvas(eventData);
                _pressAnchoredPos = DragTarget.anchoredPosition;
                _interacting = true;
            }

            public void OnDrag(PointerEventData eventData)
            {
                if (DragTarget == null) return;
                if (!eventData.dragging) return;
                _dragging = true;

                // Both points are in canvas-local space, exactly like anchoredPosition.
                var delta = ScreenToCanvas(eventData) - _pressScreenPos;
                DragTarget.anchoredPosition = ClampKeepVisible(_pressAnchoredPos + delta, DragTarget.sizeDelta);
            }

            public void OnPointerUp(PointerEventData eventData)
            {
                _interacting = false;
                if (_dragging) SaveGeometry();
                _dragging = false;
            }

            public void OnPointerClick(PointerEventData eventData)
            {
                // A press on the dimmer that never turned into a drag dismisses the panel.
                if (DragTarget == null && !_dragging && eventData.button == PointerEventData.InputButton.Left)
                    SetVisible(false);
            }

            private Vector2 ScreenToCanvas(PointerEventData eventData)
            {
                var canvas = GetComponentInParent<Canvas>();
                if (canvas != null && canvas.rootCanvas != null)
                {
                    var canvasRect = canvas.rootCanvas.transform as RectTransform;
                    if (canvasRect != null && RectTransformUtility.ScreenPointToLocalPointInRectangle(
                            canvasRect, eventData.position, eventData.pressEventCamera, out var local))
                    {
                        return local;
                    }
                }
                return eventData.position;
            }
        }
    }
}
