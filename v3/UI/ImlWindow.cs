using System;
using System.Collections.Generic;
using UnityEngine;
using Iris.Iml;

namespace Iridium.UI
{
    /// <summary>
    /// UGUI/IML-backed modal dialog. IML is generated via <see cref="BuildIml"/> so any
    /// number of buttons can render without per-instance IML files.
    /// </summary>
    public class ImlWindow : MonoBehaviour
    {
        public enum IconStyle { Information, Success, Warning, Error, Stop }
        public enum ButtonStyle { Element, Primary }

        public class ButtonConfig
        {
            public string Text { get; set; } = string.Empty;
            public Action? OnClick { get; set; }
            public ButtonStyle Style { get; set; } = ButtonStyle.Primary;
            public bool CloseOnClick { get; set; } = true;
        }

        public class Config
        {
            public string Title { get; set; } = "";
            public string Message { get; set; } = "";
            public IconStyle? Icon { get; set; }
            public Vector2 Size { get; set; } = new(400, 200);
            public ButtonConfig[] Buttons { get; set; } = Array.Empty<ButtonConfig>();
            public Action? OnClose { get; set; }
        }

        private IrisGoRenderer _renderer;
        private Config _config = null!;
        private static readonly List<ImlWindow> _activeWindows = new();

        public static ImlWindow Show(Config config)
        {
            var go = new GameObject("ImlWindow");
            DontDestroyOnLoad(go);
            var win = go.AddComponent<ImlWindow>();
            win._config = config;
            win.SetupRenderer();
            _activeWindows.Add(win);
            Main.Logger?.Log("[ImlWindow] Show() complete");
            return win;
        }

        public void Close()
        {
            _activeWindows.Remove(this);
            _config?.OnClose?.Invoke();
            if (_renderer?.RootObject != null)
                Destroy(_renderer.RootObject);
            Destroy(gameObject);
        }

        public static void Close(ImlWindow window)
        {
            window?.Close();
        }

        public static void CloseAll()
        {
            foreach (var w in _activeWindows.ToArray())
                w.Close();
        }

        public static bool HasActiveWindows => _activeWindows.Count > 0;

        /// <summary>Show the first-run welcome dialog. Called once on initial mod enable.</summary>
        public static void ShowFirstRun()
        {
            Show(new Config
            {
                Title = Localization.Get("FirstRunTitle"),
                Message = Localization.Get("FirstRunMessage"),
                Icon = IconStyle.Information,
                Size = new Vector2(400, 200),
                Buttons = new[]
                {
                    new ButtonConfig
                    {
                        Text = Localization.Get("Understand"),
                        Style = ButtonStyle.Primary,
                        CloseOnClick = true,
                        OnClick = () =>
                        {
                            Main.Settings.firstRun = false;
                            Main.Settings.lastVersion = VersionManager.GetFullVersionString();
                            Main.Settings.lastUpgradeMessageSeen_106_beta5 = "1.0.6_beta5";
                            Main.Handler?.SaveSettings(Main.Settings);
                        }
                    }
                }
            });
        }

        /// <summary>Show a version-upgrade notice dialog with content keyed by <paramref name="messageKey"/>.</summary>
        public static void ShowUpgrade(string messageKey)
        {
            Show(new Config
            {
                Title = Localization.Get("UpgradeTitle"),
                Message = Localization.Get(messageKey),
                Icon = IconStyle.Warning,
                Size = new Vector2(400, 200),
                Buttons = new[]
                {
                    new ButtonConfig
                    {
                        Text = Localization.Get("Understand"),
                        Style = ButtonStyle.Primary,
                        CloseOnClick = true,
                        OnClick = () =>
                        {
                            Main.Settings.lastVersion = VersionManager.GetFullVersionString();
                            Main.Settings.lastUpgradeMessageSeen_106_beta5 = "1.0.6_beta5";
                            Main.Handler?.SaveSettings(Main.Settings);
                        }
                    }
                }
            });
        }

        private void SetupRenderer()
        {
            _renderer = new IrisGoRenderer
            {
                ParentTransform = transform,
            };

            // Data context feeds the shared Dialog.iml template. Buttons are declared
            // in the IML as btn1..btn3; unused entries are hidden via hasBtn{i}.
            const int MaxButtons = 3;

            // Dialog sizing: keep a 16:9 (1280x720) aspect ratio scaled to the screen.
            var screenW = Mathf.Max(Screen.width, 1f);
            var dlgW = Mathf.Min(screenW * 0.35f, 720f);
            var dlgH = dlgW * 720f / 1280f;

            var data = new Dictionary<string, object>
            {
                ["title"] = _config.Title ?? string.Empty,
                ["message"] = _config.Message ?? string.Empty,
                ["iconType"] = _config.Icon.HasValue
                    ? _config.Icon.Value.ToString().ToLowerInvariant()
                    : string.Empty,
                ["showIcon"] = _config.Icon.HasValue,
                ["dlgWidth"] = dlgW,
                ["dlgHeight"] = dlgH,
            };
            for (int i = 0; i < MaxButtons; i++)
            {
                var idx = i + 1;
                if (i < _config.Buttons.Length)
                {
                    data[$"btn{idx}Text"] = _config.Buttons[i].Text ?? string.Empty;
                    data[$"btn{idx}Style"] = _config.Buttons[i].Style.ToString().ToLowerInvariant();
                    data[$"hasBtn{idx}"] = true;
                }
                else
                {
                    data[$"btn{idx}Text"] = string.Empty;
                    data[$"btn{idx}Style"] = "element";
                    data[$"hasBtn{idx}"] = false;
                }
            }
            _renderer.SetDataContext(data);

            // Handlers: one OnBtn{i} per button, in declaration order.
            for (int i = 0; i < _config.Buttons.Length; i++)
            {
                var btn = _config.Buttons[i];
                var idx = i + 1;
                _renderer.RegisterHandler($"OnBtn{idx}", (obj) =>
                {
                    try { btn.OnClick?.Invoke(); }
                    catch (Exception ex) { Main.Logger?.Log($"[ImlWindow] OnBtn{idx} handler error: {ex}"); }
                    if (btn.CloseOnClick) Close();
                });
            }

            var uiDir = System.IO.Path.Combine(Main.Handler?.ModPath ?? string.Empty, "Resources", "ui");
            var imlPath = System.IO.Path.Combine(uiDir, "Dialog.iml");
            if (System.IO.File.Exists(imlPath))
                _renderer.LoadFile(imlPath);
            else
                Main.Logger?.Log($"[ImlWindow] Dialog.iml not found: {imlPath}");
            _renderer.Rebuild();
            Main.Logger?.Log("[ImlWindow] Render complete");
        }
    }
}
