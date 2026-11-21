using System;
using System.Collections.Generic;
using UnityEngine;
using Iridium.UI;
using Iridium.Config;
using Iridium.Patches;
using Iridium.Patches.Bugfix;
using Iridium.Patches.Compatibility;
using Iridium.Patches.Sound;
using Iridium.Patches.Editor;
using Iridium.Patches.Optimizer;
using Iridium.Patches.UI;
using System.Linq;
using static Iridium.UI.IridiumLayout;
using Iris.Iml;

namespace Iridium
{
    public class Settings
    {
        public string language = "en";
        public bool firstRun = true;
        public string? lastVersion = null;
        public string? lastUpgradeMessageSeen_106_beta5 = null;

        public OptimizerSettings optimizer = new();
        public UISettings ui = new();
        public LobbyMusicSettings lobbyMusic = new();
        public MemorySettings memory = new();
        public CompatibilitySettings compatibility = new();
        public HitSoundSettings hitSound = new();
        public JudgeTextSettings judgeText = new();
        public PatchModeSettings patchMode = new();
        public EditorShortcutSettings editorShortcuts = new();
        public AsyncInputSettings asyncInput = new();

        // 无内置管理器界面的加载器（MelonLoader / BepInEx）用它打开或关闭设置面板。
        public string panelToggleHotkey = "Ctrl+F9";

        // ---- UI implementation (standalone UGUI panel vs UMM's own IMGUI page) ----

        public const string UiImplementationPanel = "panel";
        public const string UiImplementationUmm = "umm";

        /// <summary>
        /// UMM's settings page only exists when UMM itself loaded the mod. Detected from
        /// the handler type so v3 needs no loader-side API: "UmmHandler" is the only
        /// loader that owns an IMGUI settings host.
        /// </summary>
        public static bool UmmAvailable
        {
            get
            {
                var handler = Main.Handler;
                return handler != null && handler.GetType().Name == "UmmHandler";
            }
        }

        /// <summary>
        /// Configured implementation with the loader fallback applied: a stored "umm"
        /// is ignored (and never written back) when the mod is not loaded by UMM.
        /// </summary>
        public string EffectiveUiImplementation =>
            ui.uiImplementation == UiImplementationUmm && UmmAvailable
                ? UiImplementationUmm
                : UiImplementationPanel;

        // What the currently open surface is rendered with. Latched when a surface opens,
        // so switching the setting never tears down the UI the user is looking at; the
        // new implementation applies the next time the panel / UMM page is opened.
        private string _activeUiImplementation = UiImplementationPanel;

        public string ActiveUiImplementation => _activeUiImplementation;

        public void LatchUiImplementation() => _activeUiImplementation = EffectiveUiImplementation;

        // ---- IML-facing view of the setting (expressions read settings.*) ----

        // What the UI should show as selected: the effective value, so a stored "umm" on
        // a non-UMM loader is displayed as the panel fallback that is actually in use.
        public string uiImplementation => EffectiveUiImplementation;
        public bool ummAvailable => UmmAvailable;
        public bool uiImplementationPending => ActiveUiImplementation != EffectiveUiImplementation;

        // Set every frame UMM's settings page is drawn; OnGUI only fires while its window
        // is open, so a frame gap means the page was just (re)opened. The sentinel must be
        // a small negative number, not int.MinValue: the subtraction wraps around and would
        // report "visible" on the very first frame.
        private int _ummPageLastFrame = -1000;

        public bool IsUmmPageVisible => Time.frameCount - _ummPageLastFrame <= 1;


        private string? _defaultLobbyMusicPathCache;
        private string? _fastLobbyMusicPathCache;

        private string _currentTab = "general";
        public string currentTab => _currentTab;

        // Optimizer 子 Tab（basics/gameplay/fx/advanced），与 currentTab 同构
        private string _optimizerSubTab = "basics";
        public string optimizerSubTab => _optimizerSubTab;

        // 顶层 Tab 栏的搜索框：非空时显示全局结果页（跨所有 Tab 匹配选项），
        // 点击结果跳转到对应 Tab/子 Tab 并自动展开区块与 Info 说明。
        private string _searchQuery = "";
        public string searchQuery => _searchQuery;
        public bool IsSearching => _searchQuery.Trim().Length > 0;

        public void SetSearchQuery(string value)
        {
            _searchQuery = value ?? "";
        }

        /// <summary>单个搜索结果（供 IML ForEach/Template 渲染的字段集合）。</summary>
        public sealed class SearchResultItem
        {
            public string LabelKey = "";   // 选项显示名（已本地化）
            public string TabKey = "";     // 顶层 Tab
            public string TabName = "";    // 顶层 Tab 显示名（已本地化）
            public string SubTabKey = "";  // Optimizer 子 Tab（可为空）
            public string SubTabName = ""; // 子 Tab 显示名（可为空）
            public string JumpKey = "";    // 跳转用键（= LabelKey）
        }

        // 搜索结果卡片第二行（位置文本）的富文本字号
        private const int SearchLocFontSize = 11;

        /// <summary>
        /// 富文本高亮：把 label 中匹配 q 的片段（不区分大小写）包上主题色标记。
        /// 无匹配或 q 为空时原样返回。转义 XML 特殊字符防止 richText 注入。
        /// </summary>
        private static string HighlightMatch(string label, string q)
        {
            if (string.IsNullOrEmpty(label))
                return label ?? "";
            label = System.Security.SecurityElement.Escape(label);
            if (string.IsNullOrEmpty(q))
                return label;

            int idx = label.IndexOf(q, StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
                return label;

            // 只高亮第一处匹配（与IndexOf匹配语义一致）
            return label.Substring(0, idx)
                + "<color=#D973A5>" + label.Substring(idx, q.Length) + "</color>"
                + label.Substring(idx + q.Length);
        }

        /// <summary>按当前搜索词查询全部命中项（最多 30 条，按索引顺序 = Tab 顺序）。</summary>
        public List<SearchResultItem> GetSearchResults()
        {
            var results = new List<SearchResultItem>();
            string q = _searchQuery.Trim();
            if (q.Length == 0)
                return results;

            foreach (var entry in SettingsSearchIndex.Entries)
            {
                if (!MatchesKey(entry.LabelKey, q))
                    continue;
                results.Add(new SearchResultItem
                {
                    LabelKey = Localization.Get(entry.LabelKey),
                    TabKey = entry.Tab,
                    TabName = Localization.Get(TabDisplayNameKey(entry.Tab)),
                    SubTabKey = entry.SubTab ?? "",
                    SubTabName = entry.SubTab != null ? Localization.Get(SubTabDisplayNameKey(entry.SubTab)) : "",
                    JumpKey = entry.LabelKey,
                });
                if (results.Count >= 30)
                    break;
            }
            return results;
        }

        private static string TabDisplayNameKey(string tab) => tab switch
        {
            "general" => "GeneralSettings",
            "optimizer" => "OptimizerSettings",
            "editor" => "EditorSettings",
            "compatibility" => "CompatibilitySettings",
            "audio" => "AudioSettings",
            _ => tab,
        };

        public static string SubTabDisplayNameKey(string subTab) => subTab switch
        {
            "basics" => "SubTabBasics",
            "gameplay" => "SubTabGameplay",
            "fx" => "SubTabFx",
            "advanced" => "SubTabAdvanced",
            _ => subTab,
        };

        /// <summary>点击搜索结果：跳转 Tab/子 Tab 并自动展开区块、高亮目标选项。</summary>
        public void JumpToSearchResult(string labelKey)
        {
            foreach (var entry in SettingsSearchIndex.Entries)
            {
                if (entry.LabelKey != labelKey)
                    continue;

                _currentTab = entry.Tab;
                if (entry.SubTab != null)
                    _optimizerSubTab = entry.SubTab;

                // 跳转目标在 optimizer 内时必须确保总开关已开，
                // 否则子页整体不渲染（Optimizer.iml 壳的 enableOptimizer 门控），
                // 表现为跳转后页面空白。
                if (entry.Tab == "optimizer" && !optimizer.enableOptimizer)
                {
                    optimizer.enableOptimizer = true;
                    if (optimizer.disableShadows)
                        QualitySettings.shadows = ShadowQuality.Disable;
                    AsyncPatchManager.UpdateOptimizerPatchesAsync();
                }

                // 精准定位：展开目标子页的全部折叠区块（不知道选项属于哪个
                // 区块，全展开保证可见；用户收起后不会再次自动展开）
                if (entry.Tab == "optimizer")
                {
                    foreach (string s in new[] { "image", "rendering", "easing", "particle", "scene", "loading", "tween", "extreme", "memory", "advancedMemory" })
                        _sectionExpanded[s] = true;
                }

                // 自动展开 Info 说明（默认用 LabelKey + "Info"）
                string infoKey = entry.InfoKey ?? entry.LabelKey + "Info";
                if (Localization.Get(infoKey) != infoKey) // 存在该键才展开
                    _infoExpanded.Add(infoKey);

                // 行高亮：目标选项显示高亮标记，数秒后自动消失
                _highlightedKey = labelKey;
                _highlightExpireTime = Time.unscaledTime + 6f;

                _searchQuery = "";
                return;
            }
        }

        // 跳转定位高亮：目标选项行显示标记直到过期
        private string _highlightedKey = "";
        private float _highlightExpireTime;
        public string highlightedKey =>
            (Time.unscaledTime < _highlightExpireTime) ? _highlightedKey : "";

        /// <summary>IML 查询：某个选项当前是否处于跳转高亮状态。</summary>
        public bool IsHighlighted(string labelKey) => highlightedKey == labelKey;

        private static bool MatchesKey(string key, string q)
        {
            string label = Localization.Get(key);
            if (label.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            // 也匹配键名本身（便于用英文设置名搜索）
            return key.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // Collapsible option groups, collapsed by default.
        private readonly Dictionary<string, bool> _sectionExpanded = new();
        public bool expandImageOpts => IsSectionExpanded("image");
        public bool expandRenderingOpts => IsSectionExpanded("rendering");
        public bool expandEasingOpts => IsSectionExpanded("easing");
        public bool expandParticleOpts => IsSectionExpanded("particle");
        public bool expandSceneOpts => IsSectionExpanded("scene");
        public bool expandLoadingOpts => IsSectionExpanded("loading");
        public bool expandTweenOpts => IsSectionExpanded("tween");
        public bool expandExtremeOpts => IsSectionExpanded("extreme");
        public bool expandMemoryOpts => IsSectionExpanded("memory");
        public bool expandAdvancedMemoryOpts => IsSectionExpanded("advancedMemory");

        private bool IsSectionExpanded(string key) => _sectionExpanded.TryGetValue(key, out var v) && v;

        private void ToggleSection(string key)
        {
            _sectionExpanded[key] = !IsSectionExpanded(key);
        }

        // Per-option info explanations: clicking the ⓘ icon next to an option
        // toggles a localized description rendered below the row.
        private readonly HashSet<string> _infoExpanded = new();
        public bool IsInfoExpanded(string key) => _infoExpanded.Contains(key);

        private void ToggleInfo(string key)
        {
            if (!_infoExpanded.Remove(key)) _infoExpanded.Add(key);
        }

        public string GetSectionHeader(string key, string labelKey)
        {
            return (IsSectionExpanded(key) ? "▾ " : "▸ ") + Localization.Get(labelKey);
        }
        private Vector2 _contentScrollPosition = Vector2.zero;
        private SizesGroup.Holder _sizesHolder = new();

        private int _compatFlashMode = -1;
        private int _compatCamRelMode = -1;
        private bool _isBindingPauseKey = false;
        private int _bindKeyStartFrame = -1;
        private string _bindingTarget = null;
        /// <summary>Display-only text while recording (never written to Settings.xml).</summary>
        private string _bindingPreview = null;
        private int _bindingOldKey, _bindingOldMods;

        private string[] _cachedTabDisplayNames = System.Array.Empty<string>();
        private string _cachedLanguage = "";

        private IrisGoRenderer _renderer;
        private bool _rendererInitialized = false;

        /// <summary>IMGUI renderer used when the settings are painted inside UMM's page.</summary>
        private IrisGuiRenderer? _guiRenderer;
        private bool _guiRendererInitialized;

        /// <summary>
        /// Renderer that registration calls go to while the bindings are being built.
        /// Both backends share the exact same IML bindings (functions, handlers, data
        /// context), so a second renderer only needs its registrations replayed.
        /// </summary>
        private IImlRenderer? _registrationTarget;

        private IImlRenderer Target => _registrationTarget ?? _renderer;

        /// <summary>
        /// Generic handler registration helper: <see cref="IImlRenderer"/> does not expose
        /// the typed overload, so dispatch on the concrete backend that is being bound.
        /// </summary>
        private void RegisterTargetHandler<T>(string name, Action<T> handler)
        {
            if (Target is IrisGoRenderer go) go.RegisterHandler(name, handler);
            else if (Target is IrisGuiRenderer gui) gui.RegisterHandler(name, handler);
        }


        private string[] GetTabDisplayNames()
        {
            if (_cachedLanguage != language || _cachedTabDisplayNames.Length == 0)
            {
                _cachedTabDisplayNames = new[] { "EnableOptimizer", "UISettings", "LevelSelectSettings", "CompatibilitySettings", "HitSoundSettings", "EditorShortcuts", "AsyncInputSettings" }
                    .Select(n => Localization.Get(n)).ToArray();
                _cachedLanguage = language;
            }
            return _cachedTabDisplayNames;
        }

        /// <summary>UGUI renderer backing the settings panel (see <see cref="Iridium.UI.PanelShell"/>).</summary>
        public IrisGoRenderer UI
        {
            get
            {
                InitializeRenderer();
                return _renderer;
            }
        }

        /// <summary>
        /// Hands the settings tree a parent transform (the panel's scroll content) and
        /// renders it. Called once by the panel shell.
        /// </summary>
        public bool AttachUi(Transform parent)
        {
            InitializeRenderer();
            if (parent == null) return false;
            _renderer.ParentTransform = parent;
            // The panel is sized in canvas units; scale the theme metrics with it so text
            // and controls don't look lost inside a large window.
            _renderer.UiScale = 1.25f;
            // The panel already provides the window: fill its width, hug the height.
            _renderer.Fit = IrisGoRenderer.WrapperFit.StretchWidth;
            // UGUI has no per-frame redraw: rebuild only when something changed, and
            // never while the user is typing (it would drop focus mid-word).
            _renderer.AutoRebuildOnChange = true;
            _renderer.OnHandlerInvoked = () => _renderer.MarkDirty();
            _renderer.Visible = true;

            string imlPath = System.IO.Path.Combine(
                Main.Handler?.ModPath ?? "",
                "Resources", "ui", "Settings.iml");
            if (!System.IO.File.Exists(imlPath))
            {
                Main.Logger?.Error($"[Settings] IML file not found: {imlPath}");
                return false;
            }
            _renderer.LoadFile(imlPath);
            _renderer.Rebuild();
            return true;
        }

        private void InitializeRenderer()
        {
            if (_rendererInitialized) return;

            _renderer = new IrisGoRenderer();
            // Bindings are shared with the IMGUI backend; replay them onto this renderer.
            _registrationTarget = _renderer;
            try { BindRenderer(); }
            finally { _registrationTarget = null; }
            _rendererInitialized = true;
        }

        private void InitializeGuiRenderer()
        {
            if (_guiRendererInitialized) return;

            _guiRenderer = new IrisGuiRenderer();
            _registrationTarget = _guiRenderer;
            try { BindRenderer(); }
            finally { _registrationTarget = null; }
            // IrisGuiRenderer draws through IridiumLayout's IMGUI engine (same theme
            // tokens, metrics and textures as the UGUI toolkit).
            _guiRenderer.SetLayout(new IridiumLayoutAdapter());
            _guiRendererInitialized = true;
        }

        /// <summary>Data context, functions and handlers shared by both renderers.</summary>
        private void BindRenderer()
        {
            if (Main.Logger != null)
                Target.LogDelegate = msg => Main.Logger.Log(msg);  // 适配签名
            Target.SetHotReload(false);

            // DataContext wraps 'this' as 'settings' for IML expressions; set once since 'this' is a reference.
            Target.SetDataContext(new { settings = this });

            Target.RegisterFunction("localize", args =>
            {
                if (args.Length > 0 && args[0] is string key)
                    return Localization.Get(key);
                return "";
            });

            Target.RegisterFunction("getVersion", args => VersionManager.GetFullVersionString());
            Target.RegisterFunction("getAsyncStatus", args =>
                AsyncPatchManager.IsProcessing ? "⏳ " + Localization.Get("AsyncPatchProcessing") : "");
            Target.RegisterFunction("getLanguages", args =>
            {
                var langs = Localization.AvailableLanguages;
                var result = new List<object>();
                foreach (var l in langs)
                    result.Add(new { key = l, displayName = Localization.GetDisplayName(l) });
                return result;
            });

            Target.RegisterFunction("getShortcutDisplay", args =>
            {
                if (args.Length >= 2 && args[0] is int key && args[1] is int mods)
                {
                    // While this row is being recorded, show the pending combination.
                    // The IML passes the committed key/mods, so matching them against the
                    // armed target is how we know which row is recording.
                    if (IsRecordingRow(key, mods)) return _bindingPreview;
                    return ShortcutDisplay(key, mods);
                }
                return "";
            });

            RegisterTargetHandler<string>("OnTabClick", key => { _currentTab = key; });
            RegisterTargetHandler<string>("OnOptimizerSubTabClick", key => { _optimizerSubTab = key; });
            RegisterTargetHandler<string>("OnSearchChanged", value => SetSearchQuery(value));
            Target.RegisterHandler("OnSearchClear", () => SetSearchQuery(""));
            RegisterTargetHandler<string>("OnSearchResultClick", key => JumpToSearchResult(key));
            Target.RegisterFunction("getSearchResults", args =>
            {
                var list = new List<object>();
                string q = _searchQuery.Trim();
                foreach (var r in GetSearchResults())
                {
                    // 双行卡片：第一行选项名（匹配片段高亮），
                    // 第二行小号灰色位置文本（richText 由 BuildGuiStyle 开启）
                    string loc = r.SubTabName.Length > 0
                        ? $"{r.TabName} / {r.SubTabName}"
                        : r.TabName;

                    string nameHtml = HighlightMatch(r.LabelKey, q);
                    string title =
                        nameHtml + "\n" +
                        $"<size={SearchLocFontSize}><color=#8A9097>{loc}</color></size>";

                    list.Add(new
                    {
                        label = r.LabelKey,
                        name = r.LabelKey,
                        loc = loc,
                        title = title,
                        tab = r.TabName,
                        subTab = r.SubTabName,
                        jumpKey = r.JumpKey,
                    });
                }
                return list;
            });
            RegisterTargetHandler<string>("OnSectionToggle", key => ToggleSection(key));
            // The panel used to be a loader-specific IMGUI window; now that it is UGUI it
            // exists on every loader, so its hotkey is a first-class setting.
            Target.RegisterFunction("getPanelHotkey", args =>
                _isBindingPauseKey && !string.IsNullOrEmpty(_bindingPreview) ? _bindingPreview : panelToggleHotkey);
            Target.RegisterHandler("OnBindPanelHotkey", () => ToggleBinding("panelToggle"));
            RegisterTargetHandler<string>("OnLanguageClick", lang =>
            {
                // Guard the cast: a missing ForEach scope used to deliver null here and
                // silently write a null language (no translation, no highlight).
                if (string.IsNullOrEmpty(lang) || lang == language) return;
                language = lang;
                Save();
            });
            RegisterTargetHandler<string>("OnInfoToggle", key => ToggleInfo(key));
            Target.RegisterFunction("isInfoExpanded", args =>
                args.Length > 0 && args[0] is string key && IsInfoExpanded(key));
            Target.RegisterFunction("isHighlighted", args =>
                args.Length > 0 && args[0] is string key && IsHighlighted(key));
            // 选项行文本：跳转高亮期间返回主题色富文本，使对应行文字变色定位
            Target.RegisterFunction("optLabel", args =>
            {
                if (args.Length < 1 || args[0] is not string key)
                    return "";
                string label = Localization.Get(key);
                if (IsHighlighted(key))
                    return $"<color=#D973A5>{label}</color>";
                return label;
            });

            RegisterShortcutHandlers();

            RegisterOptimizerHandlers();
            RegisterUIHandlers();
            RegisterLevelSelectHandlers();
            RegisterCompatibilityHandlers();
            RegisterHitSoundHandlers();
            RegisterEditorShortcutsHandlers();
            RegisterAsyncInputHandlers();
        }

        private void RegisterOptimizerHandlers()
        {
            Target.RegisterHandler("OnOptimizerToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                optimizer.enableOptimizer = value;
                if (value)
                {
                    if (optimizer.disableShadows) QualitySettings.shadows = ShadowQuality.Disable;
                    AsyncPatchManager.UpdateOptimizerPatchesAsync();
                }
                else
                {
                    QualitySettings.shadows = ShadowQuality.All;
                    AsyncPatchManager.UpdateOptimizerPatchesAsync();
                }
                Save();
            });

            Target.RegisterHandler("OnCompressToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                optimizer.dontCompress = !value;
                Iridium.Patches.Optimizer.OptimizerShared.ResetTextureOptimizationState();
                Save();
            });

            Target.RegisterHandler("OnShowSavedMemoryToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                optimizer.dontShowSavedMemory = !value;
                Save();
            });

            Target.RegisterHandler("OnLossyCompressionToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                optimizer.useLossyCompression = value;
                Iridium.Patches.Optimizer.OptimizerShared.ResetTextureOptimizationState();
                Save();
            });

            Target.RegisterHandler("OnLossyQualityChanged", (obj) =>
            {
                if (obj is float f)
                {
                    optimizer.lossyQuality = Mathf.Clamp((int)f, 10, 100);
                    Iridium.Patches.Optimizer.OptimizerShared.ResetTextureOptimizationState();
                    Save();
                }
            });

            Target.RegisterHandler("OnMultipleOf4Toggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                optimizer.dontResizeMultipleOf4 = !value;
                Iridium.Patches.Optimizer.OptimizerShared.ResetTextureOptimizationState();
                Save();
            });

            Target.RegisterHandler("OnDivideByChanged", (obj) =>
            {
                if (obj is float f)
                {
                    optimizer.divideBy = Mathf.Clamp((int)f, 1, 4);
                    Iridium.Patches.Optimizer.OptimizerShared.ResetTextureOptimizationState();
                    Save();
                }
            });

            Target.RegisterHandler("OnDontResizeColliderToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                optimizer.dontResizeCollider = !value;
                Save();
            });

            Target.RegisterHandler("OnDisableShadowsToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                optimizer.disableShadows = value;
                if (optimizer.enableOptimizer && optimizer.disableShadows)
                    QualitySettings.shadows = ShadowQuality.Disable;
                else
                    QualitySettings.shadows = ShadowQuality.All;
                Save();
            });

            Target.RegisterHandler("OnRenderScaleToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                optimizer.enableRenderScale = value;
                AsyncPatchManager.UpdateOptimizerPatchesAsync();
                Save();
            });

            Target.RegisterHandler("OnRenderScalePercentChanged", (obj) =>
            {
                if (obj is float f)
                {
                    optimizer.renderScalePercent = Mathf.Clamp((int)f, 30, 100);
                    Save();
                }
            });

            Target.RegisterHandler("OnOptimizeDecorationUpdateToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                optimizer.optimizeDecorationUpdate = value;
                Save();
            });

            Target.RegisterHandler("OnOptimizeHitboxDetectionToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                optimizer.optimizeHitboxDetection = value;
                AsyncPatchManager.UpdateOptimizerPatchesAsync();
                Save();
            });

            Target.RegisterHandler("OnOptimizeTileUpdateToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                optimizer.optimizeTileUpdate = value;
                Save();
            });

            Target.RegisterHandler("OnOptimizeMoveTrackToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                optimizer.optimizeMoveTrack = value;
                Save();
            });

            Target.RegisterHandler("OnOptimizeRecolorTrackToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                optimizer.optimizeRecolorTrack = value;
                Save();
            });

            Target.RegisterHandler("OnSkipEventIfPausedToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                optimizer.skipEventIfPaused = value;
                Save();
            });

            Target.RegisterHandler("OnOptimizeEventIconsToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                optimizer.optimizeEventIcons = value;
                Save();
            });

            Target.RegisterHandler("OnOptimizeScnGameUpdateToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                optimizer.optimizeScnGameUpdate = value;
                Save();
            });

            Target.RegisterHandler("OnOptimizePlayerInputAllocationsToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                optimizer.optimizePlayerInputAllocations = value;
                AsyncPatchManager.UpdateOptimizerPatchesAsync();
                Save();
            });

            Target.RegisterHandler("OnOptimizeRDInputAllocationsToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                optimizer.optimizeRDInputAllocations = value;
                AsyncPatchManager.UpdateOptimizerPatchesAsync();
                Save();
            });

            Target.RegisterHandler("OnOptimizeMoveDecorationsToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                optimizer.optimizeMoveDecorations = value;
                Save();
            });

            Target.RegisterHandler("OnOptimizeFfxDecorationsToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                optimizer.optimizeFfxDecorations = value;
                AsyncPatchManager.UpdateOptimizerPatchesAsync();
                Save();
            });

            Target.RegisterHandler("OnDecorationShaderCacheToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                optimizer.optimizeDecorationShaderCache = value;
                AsyncPatchManager.UpdateOptimizerPatchesAsync();
                Save();
            });

            Target.RegisterHandler("OnStaticDecorationBatchingToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                optimizer.enableStaticDecorationBatching = value;
                if (value && !optimizer.enableCustomEasingEngine)
                {
                    optimizer.enableCustomEasingEngine = true; // 合批的 tween 感知依赖缓速引擎
                }
                Patches.Optimizer.StaticDecorationBatcher.SetEnabled(
                    optimizer.enableStaticDecorationBatching && optimizer.enableCustomEasingEngine);
                Save();
            });

            Target.RegisterHandler("OnOptimizeFloorMeshToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                optimizer.optimizeFloorMesh = value;
                Save();
            });

            Target.RegisterHandler("OnOptimizeFiltersToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                optimizer.optimizeFilters = value;
                AsyncPatchManager.UpdateOptimizerPatchesAsync();
                Save();
            });

            Target.RegisterHandler("OnFastLoadingToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                optimizer.fastLoading = value;
                Save();
            });

            Target.RegisterHandler("OnCustomEasingEngineToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                optimizer.enableCustomEasingEngine = value;
                ApplyCustomEasingMutualExclusion(optimizer);
                // 合批渲染依赖缓速引擎的 tween 感知，引擎关闭时联动关闭
                if (!value && optimizer.enableStaticDecorationBatching)
                {
                    optimizer.enableStaticDecorationBatching = false;
                    Patches.Optimizer.StaticDecorationBatcher.SetEnabled(false);
                }
                AsyncPatchManager.UpdateOptimizerPatchesAsync();
                Save();
            });

            Target.RegisterHandler("OnOptimizeParticleToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                optimizer.optimizeParticle = value;
                Save();
            });

            Target.RegisterHandler("OnOptimizeParticleInactiveToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                optimizer.optimizeParticleInactive = value;
                Save();
            });

            Target.RegisterHandler("OnOptimizeParticleCullingToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                optimizer.optimizeParticleCulling = value;
                Save();
            });

            Target.RegisterHandler("OnOptimizeParticleLodToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                optimizer.optimizeParticleLod = value;
                Save();
            });

            Target.RegisterHandler("OnOptimizeEventProcessingToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                optimizer.optimizeEventProcessing = value;
                Save();
            });

            Target.RegisterHandler("OnOptimizeEditorMouseDetectionToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                optimizer.optimizeEditorMouseDetection = value;
                Save();
            });

            Target.RegisterHandler("OnOptimizeEditorEventIndicatorsToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                optimizer.optimizeEditorEventIndicators = value;
                Save();
            });

            Target.RegisterHandler("OnOptimizeMoveTrackTweensToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                optimizer.optimizeMoveTrackTweens = value;
                Save();
            });

            Target.RegisterHandler("OnBatchMoveDecorationsToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                optimizer.batchMoveDecorations = value;
                Save();
            });

            Target.RegisterHandler("OnCustomLevelReadOptimizationToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                optimizer.customLevelReadOptimization = value;
                AsyncPatchManager.UpdatePatchByTypeAsync(typeof(PatchGetCustomLevelName));
                Save();
            });

            Target.RegisterHandler("OnFrameSpreadDecorationLoadingToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                optimizer.frameSpreadDecorationLoading = value;
                AsyncPatchManager.UpdateOptimizerPatchesAsync();
                Save();
            });

            Target.RegisterHandler("OnLargeLevelLoadingToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                // 关闭前先把未激活砖块补完，避免 A 的 Awake 替换卸载后与 FastInit 重复初始化
                Iridium.Patches.Optimizer.ChunkedFloorSpawnPatch.FlushAll();
                optimizer.optimizeLargeLevelLoading = value;
                LargeLoadPatches.Update();
                Save();
            });

            Target.RegisterHandler("OnChunkedFloorSpawnToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                Iridium.Patches.Optimizer.ChunkedFloorSpawnPatch.FlushAll();
                optimizer.chunkedFloorSpawn = value;
                LargeLoadPatches.Update();
                Save();
            });

            Target.RegisterHandler("OnOptimizeEditorInteractionsToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                optimizer.optimizeEditorInteractions = value;
                AsyncPatchManager.UpdateOptimizerPatchesAsync();
                Save();
            });

            Target.RegisterHandler("OnOptimizeDeathResetToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                optimizer.optimizeDeathReset = value;
                AsyncPatchManager.UpdateOptimizerPatchesAsync();
                Save();
            });

            Target.RegisterHandler("OnOptimizeDecorationResetToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                optimizer.optimizeDecorationReset = value;
                AsyncPatchManager.UpdateOptimizerPatchesAsync();
                Save();
            });

            Target.RegisterHandler("OnOptimizeHoldRendererToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                optimizer.optimizeHoldRenderer = value;
                AsyncPatchManager.UpdateOptimizerPatchesAsync();
                Save();
            });

            Target.RegisterHandler("OnOptimizeOnBeatToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                optimizer.optimizeOnBeat = value;
                AsyncPatchManager.UpdateOptimizerPatchesAsync();
                Save();
            });

            Target.RegisterHandler("OnOptimizeSpectrumToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                optimizer.optimizeSpectrum = value;
                AsyncPatchManager.UpdateOptimizerPatchesAsync();
                Save();
            });

            Target.RegisterHandler("OnOptimizeHitTextPoolToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                optimizer.optimizeHitTextPool = value;
                AsyncPatchManager.UpdateOptimizerPatchesAsync();
                Save();
            });

            Target.RegisterHandler("OnDecorationsPerFrameChanged", (obj) =>
            {
                if (obj is float f)
                {
                    optimizer.decorationsPerFrame = Mathf.Clamp((int)f, 10, 500);
                    Save();
                }
            });

            Target.RegisterHandler("OnDOTweenGlobalToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                optimizer.optimizeDOTweenGlobal = value;
                if (value)
                    DOTweenOptimizationPatches.ApplyRuntimeSettings();
                else
                    DOTweenOptimizationPatches.ResetRuntimeSettings();
                Save();
            });

            Target.RegisterHandler("OnTweenerCapacityChanged", (obj) =>
            {
                if (obj is float f)
                {
                    optimizer.dotweenTweenerCapacity = Mathf.Clamp((int)f, 200, 2000);
                    DOTweenOptimizationPatches.ApplyRuntimeSettings();
                    Save();
                }
            });

            Target.RegisterHandler("OnSequenceCapacityChanged", (obj) =>
            {
                if (obj is float f)
                {
                    optimizer.dotweenSequenceCapacity = Mathf.Clamp((int)f, 50, 500);
                    DOTweenOptimizationPatches.ApplyRuntimeSettings();
                    Save();
                }
            });

            Target.RegisterHandler("OnDOTweenDefaultRecyclableToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                optimizer.dotweenDefaultRecyclable = value;
                DOTweenOptimizationPatches.ApplyRuntimeSettings();
                AsyncPatchManager.UpdatePatchByTypeAsync(typeof(TweenSafetyPatches));
                Save();
            });

            Target.RegisterHandler("OnDOTweenDisableSafeModeToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                optimizer.dotweenDisableSafeMode = value;
                DOTweenOptimizationPatches.ApplyRuntimeSettings();
                Save();
            });

            Target.RegisterHandler("OnExtremeOptimizationToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                optimizer.enableExtremeOptimization = value;
                AsyncPatchManager.UpdateOptimizerPatchesAsync();
                Save();
            });

            Target.RegisterHandler("OnMaxTweensPerFrameChanged", (obj) =>
            {
                if (obj is float f)
                {
                    optimizer.maxTweensPerFrame = Mathf.Clamp((int)f, 50, 500);
                    Save();
                }
            });

            Target.RegisterHandler("OnBasicMemoryOptimizationToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                memory.enableBasicOptimization = value;
                if (value)
                    Modules.FerriteCore.FerriteCoreModule.Enable();
                else
                    Modules.FerriteCore.FerriteCoreModule.Disable();
                Save();
            });

            Target.RegisterHandler("OnVirtualMemoryOptimizationToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                memory.enableVirtualMemoryOptimization = value;
                Modules.FerriteCore.VirtualMemoryOptimizer.SetEnabled(value);
                Save();
            });

            Target.RegisterHandler("OnVmTrimOnLevelLoadToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                memory.vmTrimOnLevelLoad = value;
                Save();
            });

            Target.RegisterHandler("OnVmTrimOnEditorEnterToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                memory.vmTrimOnEditorEnter = value;
                Save();
            });

            Target.RegisterHandler("OnOptimizeGameplayAllocationsToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                optimizer.optimizeGameplayAllocations = value;
                AsyncPatchManager.UpdateOptimizerPatchesAsync();
                Save();
            });

            Target.RegisterHandler("OnEditorFloorOptimizationToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                optimizer.enableEditorFloorOptimization = value;
                AsyncPatchManager.UpdateOptimizerPatchesAsync();
                Save();
            });

            Target.RegisterHandler("OnIncrementalFloorInsertToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                optimizer.incrementalFloorInsert = value;
                AsyncPatchManager.UpdateOptimizerPatchesAsync();
                Save();
            });

            Target.RegisterHandler("OnRangeBasedRedrawToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                optimizer.rangeBasedRedraw = value;
                AsyncPatchManager.UpdateOptimizerPatchesAsync();
                Save();
            });

            Target.RegisterHandler("OnSkipRedundantRemakePathToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                optimizer.skipRedundantRemakePath = value;
                AsyncPatchManager.UpdateOptimizerPatchesAsync();
                Save();
            });

            Target.RegisterHandler("OnOptimizeOffsetFloorEventsToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                optimizer.optimizeOffsetFloorEvents = value;
                AsyncPatchManager.UpdateOptimizerPatchesAsync();
                Save();
            });

            Target.RegisterHandler("OnSkipApplyEventsOnInsertToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                optimizer.skipApplyEventsOnInsert = value;
                AsyncPatchManager.UpdateOptimizerPatchesAsync();
                Save();
            });
        }

        private void RegisterUIHandlers()
        {
            // UI implementation (panel vs UMM page). The change is only persisted here;
            // the surface currently on screen keeps its renderer until it is reopened
            // (see LatchUiImplementation / Settings.OnGUI).
            RegisterTargetHandler<string>("OnUiImplementationClick", key =>
            {
                if (key == UiImplementationUmm && !UmmAvailable) return;
                if (key != UiImplementationPanel && key != UiImplementationUmm) return;
                if (ui.uiImplementation == key) return;
                ui.uiImplementation = key;
                Save();
                // UGUI does not redraw by itself; refresh the option row's highlight.
                _renderer?.MarkDirty();
            });
            Target.RegisterFunction("uiImplClass", args =>
            {
                if (args.Length < 1 || args[0] is not string key) return "element";
                if (key == UiImplementationUmm && !UmmAvailable) return "disabledBtn";
                return EffectiveUiImplementation == key ? "primary" : "element";
            });

            Target.RegisterHandler("OnRemoveNewsToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                ui.removeNews = value;
                AsyncPatchManager.UpdatePatchByTypeAsync(typeof(RemoveNewsPatch));
                RemoveNewsPatch.UpdateNews();
                Save();
            });

            Target.RegisterHandler("OnHideBetaWatermarkToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                ui.hideBetaWatermark = value;
                AsyncPatchManager.UpdatePatchByTypeAsync(typeof(HideBetaWatermarkPatch));
                HideBetaWatermarkPatch.RefreshBetaWatermark();
                Save();
            });

            Target.RegisterHandler("OnForceDifficultyUIToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                ui.forceDifficultyUI = value;
                AsyncPatchManager.UpdatePatchByTypeAsync(typeof(ForceDifficultyUIPatch));
                Save();
            });

            Target.RegisterHandler("OnAlwaysCountdownToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                ui.alwaysCountdown = value;
                AsyncPatchManager.UpdatePatchByTypeAsync(typeof(AlwaysCountdownPatch));
                Save();
            });

            Target.RegisterHandler("OnEnablePausePlanetTrailToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                ui.enablePausePlanetTrail = value;
                AsyncPatchManager.UpdatePatchByTypeAsync(typeof(PausePlanetTrailPatch));
                Save();
            });

            Target.RegisterHandler("OnMoveAutoplayTextToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                ui.moveAutoplayText = value;
                AsyncPatchManager.UpdatePatchByTypeAsync(typeof(AutoplayTextPositionPatch));
                AutoplayTextPositionPatch.RefreshAutoplayTextPosition();
                Save();
            });

            Target.RegisterHandler("OnAutoplayTextXChanged", (obj) =>
            {
                if (obj is float f)
                {
                    ui.autoplayTextX = f;
                    AutoplayTextPositionPatch.RefreshAutoplayTextPosition();
                    Save();
                }
            });

            Target.RegisterHandler("OnAutoplayTextYChanged", (obj) =>
            {
                if (obj is float f)
                {
                    ui.autoplayTextY = f;
                    AutoplayTextPositionPatch.RefreshAutoplayTextPosition();
                    Save();
                }
            });

            Target.RegisterHandler("OnShowAutoplayHintUIToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                ui.showAutoplayHintUI = value;
                AsyncPatchManager.UpdatePatchByTypeAsync(typeof(AutoplayHintUIPatch));
                Save();
            });

            Target.RegisterHandler("OnCustomAutoplayHintToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                ui.customAutoplayHint = value;
                Save();
            });

            Target.RegisterHandler("OnAutoplayHintTemplateChanged", (obj) =>
            {
                if (obj is string s)
                {
                    ui.autoplayHintTemplate = s;
                    Save();
                }
            });

            Target.RegisterHandler("OnEnableCircleArcToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                ui.enableCircleArc = value;
                // CircleArcPatch + AllAngleArcCornersPatch form one feature; apply
                // synchronously so the mesh rebuild below sees the new state.
                PatchManager.UpdatePatchByType(typeof(CircleArcPatch));
                PatchManager.UpdatePatchByType(typeof(AllAngleArcCornersPatch));
                RefreshFloorMeshCache();
                Save();
            });

            Target.RegisterHandler("OnCircleArcMinAngleChanged", (obj) =>
            {
                if (obj is float f)
                {
                    ui.circleArcMinAngle = Mathf.Clamp(f, 0f, 180f);
                    if (ui.circleArcMaxAngle < ui.circleArcMinAngle)
                        ui.circleArcMaxAngle = ui.circleArcMinAngle;
                    RefreshFloorMeshCache();
                    Save();
                }
            });

            Target.RegisterHandler("OnCircleArcMaxAngleChanged", (obj) =>
            {
                if (obj is float f)
                {
                    ui.circleArcMaxAngle = Mathf.Clamp(f, 0f, 180f);
                    if (ui.circleArcMaxAngle < ui.circleArcMinAngle)
                        ui.circleArcMinAngle = ui.circleArcMaxAngle;
                    RefreshFloorMeshCache();
                    Save();
                }
            });
        }

        // FloorMesh caches by angle pair only, so drop every entry and rebuild to make
        // a circle-arc toggle take effect on existing tiles.
        private static void RefreshFloorMeshCache()
        {
            try
            {
                foreach (var floorMesh in UnityEngine.Object.FindObjectsOfType<FloorMesh>())
                {
                    if (floorMesh == null || string.IsNullOrEmpty(floorMesh.cacheKey)) continue;
                    FloorMesh.cache.Remove(floorMesh.cacheKey);
                    floorMesh.UpdateMesh();
                }
            }
            catch (Exception ex)
            {
                Main.Logger?.Error($"[Settings] RefreshFloorMeshCache failed: {ex}");
            }
        }

        private void RegisterLevelSelectHandlers()
        {
            Target.RegisterHandler("OnLobbyMusicPatchToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                lobbyMusic.enableLobbyMusicPatch = value;
                AsyncPatchManager.UpdatePatchByTypeAsync(typeof(LobbyMusicPatch));
                if (value) LobbyMusicPatch.ReloadFromSettings();
                Save();
            });

            Target.RegisterHandler("OnEnableCustomBpmToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                lobbyMusic.enableCustomBpm = value;
                Save();
            });

            Target.RegisterHandler("OnCustomBpmChanged", (obj) =>
            {
                if (obj is float f)
                {
                    lobbyMusic.customBpm = f;
                    Save();
                }
            });

            Target.RegisterHandler("OnLobbyFastMusicToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                lobbyMusic.fastMusic = value;
                Save();
            });

            Target.RegisterHandler("OnLobbyCustomMusicToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                lobbyMusic.customMusic = value;
                LobbyMusicPatch.ReloadFromSettings();
                Save();
            });

            Target.RegisterHandler("OnDefaultMusicPathChanged", (obj) =>
            {
                if (obj is string s)
                {
                    _defaultLobbyMusicPathCache = s;
                }
            });

            Target.RegisterHandler("OnApplyDefaultMusic", () =>
            {
                lobbyMusic.defaultMusicPath = (_defaultLobbyMusicPathCache ?? string.Empty).Trim();
                LobbyMusicPatch.StartLoad(true, lobbyMusic.defaultMusicPath);
                Save();
            });

            Target.RegisterHandler("OnFastMusicPathChanged", (obj) =>
            {
                if (obj is string s)
                {
                    _fastLobbyMusicPathCache = s;
                }
            });

            Target.RegisterHandler("OnApplyFastMusic", () =>
            {
                lobbyMusic.fastMusicPath = (_fastLobbyMusicPathCache ?? string.Empty).Trim();
                LobbyMusicPatch.StartLoad(false, lobbyMusic.fastMusicPath);
                Save();
            });

            Target.RegisterHandler("OnLobbyReloadMusic", () =>
            {
                LobbyMusicPatch.ReloadFromSettings();
            });
        }

        private void RegisterCompatibilityHandlers()
        {
            if (_compatFlashMode < 0) _compatFlashMode = (int)compatibility.legacyFlashMode;
            if (_compatCamRelMode < 0) _compatCamRelMode = (int)compatibility.legacyCamRelativeToMode;

            Target.RegisterHandler("OnEnableLegacyPauseFixToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                compatibility.enableLegacyPauseFix = value;
                AsyncPatchManager.UpdatePatchByTypeAsync(typeof(LegacyPauseFixPatch_Play));
                Save();
            });

            Target.RegisterHandler("OnEnableNoFailTooEarlyToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                compatibility.enableNoFailTooEarly = value;
                AsyncPatchManager.UpdatePatchByTypeAsync(typeof(NoFailTooEarlyPatch));
                Save();
            });

            Target.RegisterHandler("OnScaleFilterSpeedWithPitchToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                compatibility.scaleFilterSpeedWithPitch = value;
                AsyncPatchManager.UpdatePatchByTypeAsync(typeof(ScaleFilterSpeedWithPitchPatch));
                Save();
            });

            Target.RegisterHandler("OnEditorPauseAllowedToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                compatibility.editorPauseAllowed = value;
                Save();
            });

            Target.RegisterHandler("OnEditorPauseEnabledToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                compatibility.editorPauseEnabled = value;
                Save();
            });

            Target.RegisterHandler("OnFixCameraRelativeDragToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                compatibility.fixCameraRelativeDrag = value;
                AsyncPatchManager.UpdatePatchByTypeAsync(typeof(CameraRelativeDragPatches));
                Save();
            });

            Target.RegisterHandler("OnPortalTravelFixToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                compatibility.portalTravelFix = value;
                AsyncPatchManager.UpdatePatchByTypeAsync(typeof(PortalTravelFixPatch));
                Save();
            });

            Target.RegisterHandler("OnFixEditorPlayResetMistakesToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                compatibility.fixEditorPlayResetMistakes = value;
                AsyncPatchManager.UpdatePatchByTypeAsync(typeof(EditorPlayResetMistakesPatch));
                Save();
            });

            Target.RegisterHandler("OnFixTurnaroundConditionToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                compatibility.fixTurnaroundCondition = value;
                AsyncPatchManager.UpdatePatchByTypeAsync(typeof(TurnaroundConditionFix));
                Save();
            });

            Target.RegisterHandler("OnFixJudgeRotationToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                compatibility.fixJudgeRotation = value;
                AsyncPatchManager.UpdatePatchByTypeAsync(typeof(JudgeTextPatches.HitTextMeshShowRotationFixPatch));
                Save();
            });

            Target.RegisterHandler("OnFixCoopPauseLockToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                compatibility.fixCoopPauseLock = value;
                AsyncPatchManager.UpdatePatchByTypeAsync(typeof(CoopPauseLockFix));
                AsyncPatchManager.UpdatePatchByTypeAsync(typeof(CoopPauseHandleLockFix));
                AsyncPatchManager.UpdatePatchByTypeAsync(typeof(CoopPlayerHitFix));
                Save();
            });

            Target.RegisterHandler("OnForceAngleDataToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                compatibility.forceAngleData = value;
                AsyncPatchManager.UpdatePatchByTypeAsync(typeof(ForceAngleDataPatch));
                Save();
            });

            Target.RegisterHandler("OnUseILPatchToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                patchMode.useILPatch = value;
                // Reapply through the backend so Prefix/Postfix and Transpiler impls switch together.
                Iridium.Patches.PatchManager.ReapplyAllPatches();
                Save();
            });

            Target.RegisterHandler("OnIgnoreRequiredModsToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                compatibility.ignoreRequiredMods = value;
                AsyncPatchManager.UpdatePatchByTypeAsync(typeof(RequiredModsClearPatches.LevelDataClearPatch));
                AsyncPatchManager.UpdatePatchByTypeAsync(typeof(RequiredModsClearPatches.LevelDataCLSClearPatch));
                AsyncPatchManager.UpdatePatchByTypeAsync(typeof(RequiredModsClearPatches.EncodeRestorePatch));
                AsyncPatchManager.UpdatePatchByTypeAsync(typeof(RequiredModsClearPatches.LevelLoadNotifyPatch));
                AsyncPatchManager.UpdatePatchByTypeAsync(typeof(CustomEventsPatches.ScanRegisterPatch));
                AsyncPatchManager.UpdatePatchByTypeAsync(typeof(CustomEventsPatches.ScanRegisterCLSPatch));
                AsyncPatchManager.UpdatePatchByTypeAsync(typeof(CustomEventsPatches.FakeEventDecodePatch));
                AsyncPatchManager.UpdatePatchByTypeAsync(typeof(CustomEventsPatches.FakeEventEncodePatch));
                AsyncPatchManager.UpdatePatchByTypeAsync(typeof(CustomEventsPatches.ReadOnlyPanelPatch));
                AsyncPatchManager.UpdatePatchByTypeAsync(typeof(CustomEventsPatches.ListItemEventPatch));
                AsyncPatchManager.UpdatePatchByTypeAsync(typeof(CustomEventsPatches.EventIndicatorPatch));
                AsyncPatchManager.UpdatePatchByTypeAsync(typeof(CustomEventsPatches.ShowPanelFakeEventPatch));
                AsyncPatchManager.UpdatePatchByTypeAsync(typeof(CustomEventsPatches.ShowTabsForFloorPatch));
                AsyncPatchManager.UpdatePatchByTypeAsync(typeof(CustomEventsPatches.FakeTabSetSelectedPatch));
                AsyncPatchManager.UpdatePatchByTypeAsync(typeof(CustomEventsPatches.FakeTabClickPatch));
                AsyncPatchManager.UpdatePatchByTypeAsync(typeof(CustomEventsPatches.RemoveEventAtSelectedPatch));
                Save();
            });
        }

        private void RegisterHitSoundHandlers()
        {
            Target.RegisterHandler("OnEnableHitSoundPitchToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                hitSound.enableHitSoundPitch = value;
                AsyncPatchManager.UpdatePatchByTypeAsync(typeof(HitSoundPatch));
                Save();
            });

            Target.RegisterHandler("OnJudgeTextCustomizationToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                judgeText.enableJudgeTextCustomization = value;
                AsyncPatchManager.UpdatePatchByTypeAsync(typeof(JudgeTextPatches.HitTextMeshInitPatch));
                AsyncPatchManager.UpdatePatchByTypeAsync(typeof(JudgeTextPatches.HitTextMeshShowPatch));
                Save();
            });

            Target.RegisterHandler("OnJudgeTextChanged", (obj) =>
            {
                Save();
            });

            Target.RegisterHandler("OnResetJudgeText", () =>
            {
                judgeText.ResetToDefault();
                Save();
            });

            Target.RegisterHandler("OnConvertJudgeTextToOffset", () =>
            {
                judgeText.ConvertAllToOffset();
                Save();
            });
        }

        private void RegisterEditorShortcutsHandlers()
        {
            Target.RegisterHandler("OnEnableEditorShortcutsToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                editorShortcuts.enableEditorShortcuts = value;
                AsyncPatchManager.UpdatePatchByTypeAsync(typeof(EditorShortcutPatches.EditorShortcutUpdatePatch));
                Save();
            });

            Target.RegisterHandler("OnCameraFollowOnFloorSelectToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                editorShortcuts.cameraFollowOnFloorSelect = value;
                Save();
            });
        }

        private void RegisterAsyncInputHandlers()
        {
            Target.RegisterHandler("OnAsyncInputToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                asyncInput.enableAIO = value;
                if (value)
                    Modules.AsyncInputOptimize.Main.Enable();
                else
                    Modules.AsyncInputOptimize.Main.Disable();
                AsyncPatchManager.UpdatePatchByTypeAsync(typeof(Modules.AsyncInputOptimize.Patch.UnityEngine__SceneManagement__SceneManager));
                AsyncPatchManager.UpdatePatchByTypeAsync(typeof(Modules.AsyncInputOptimize.Patch.__scnGame));
                AsyncPatchManager.UpdatePatchByTypeAsync(typeof(Modules.AsyncInputOptimize.Patch.__scrConductor));
                AsyncPatchManager.UpdatePatchByTypeAsync(typeof(Modules.AsyncInputOptimize.Patch.__scrCountdown));
                Save();
            });
        }

        private void RegisterShortcutHandlers()
        {
            var keys = new[] {
                ("selectAll",       (Action)(() => ToggleBinding("selectAll"))),
                ("deselectAll",     () => ToggleBinding("deselectAll")),
                ("toggleVisibility",() => ToggleBinding("toggleVisibility")),
                ("focusDecoration", () => ToggleBinding("focusDecoration")),
                ("goToFloor",       () => ToggleBinding("goToFloor")),
                ("selectAllFloors", () => ToggleBinding("selectAllFloors")),
                ("popupSave",       () => ToggleBinding("popupSave")),
                ("popupDiscard",    () => ToggleBinding("popupDiscard")),
            };
            foreach (var (name, handler) in keys)
            {
                var cap = char.ToUpper(name[0]) + name.Substring(1);
                Target.RegisterHandler($"OnBind{cap}Key", (obj) => handler());
            }
            Target.RegisterHandler("OnBindEditorPauseKey", (obj) => ToggleBinding("editorPause"));
        }

        /// <summary>
        /// True when the (key, mods) passed by a shortcut row belong to the row currently
        /// being recorded, so it can display the pending combination.
        /// </summary>
        private bool IsRecordingRow(int key, int mods)
        {
            if (!_isBindingPauseKey || string.IsNullOrEmpty(_bindingTarget)) return false;
            if (string.IsNullOrEmpty(_bindingPreview)) return false;

            GetBinding(_bindingTarget, out int currentKey, out int currentMods);
            return currentKey == key && currentMods == mods;
        }

        private void StartBinding(string target)
        {
            GetBinding(target, out _bindingOldKey, out _bindingOldMods);
            _bindingTarget = target;
            _bindingPreview = null;
            _pendingKey = 0;
            _pendingMods = 0;
            _isBindingPauseKey = true;
            _bindKeyStartFrame = Time.frameCount;

            // Drop text-field focus, otherwise keys typed during recording go into the
            // field instead of being captured as a shortcut.
            var eventSystem = UnityEngine.EventSystems.EventSystem.current;
            if (eventSystem != null) eventSystem.SetSelectedGameObject(null);
        }

        private void GetBinding(string target, out int key, out int mods)
        {
            key = 0; mods = 0;
            switch (target)
            {
                case "selectAll":          key = editorShortcuts.selectAllKey; mods = editorShortcuts.selectAllModifiers; break;
                case "deselectAll":        key = editorShortcuts.deselectAllKey; mods = editorShortcuts.deselectAllModifiers; break;
                case "toggleVisibility":   key = editorShortcuts.toggleVisibilityKey; mods = editorShortcuts.toggleVisibilityModifiers; break;
                case "focusDecoration":    key = editorShortcuts.focusDecorationKey; mods = editorShortcuts.focusDecorationModifiers; break;
                case "goToFloor":          key = editorShortcuts.goToFloorKey; mods = editorShortcuts.goToFloorModifiers; break;
                case "selectAllFloors":    key = editorShortcuts.selectAllFloorsKey; mods = editorShortcuts.selectAllFloorsModifiers; break;
                case "popupSave":          key = editorShortcuts.popupSaveKey; mods = editorShortcuts.popupSaveModifiers; break;
                case "popupDiscard":       key = editorShortcuts.popupDiscardKey; mods = editorShortcuts.popupDiscardModifiers; break;
                case "editorPause":        key = compatibility.editorPauseKey; mods = compatibility.editorPauseModifiers; break;
                case "panelToggle":        ParseHotkey(panelToggleHotkey, out key, out mods); break;
            }
        }

        /// <summary>
        /// Parses "Ctrl+F9" style strings into the same key/modifier encoding the editor
        /// shortcuts use (1=Ctrl, 2=Alt, 4=Shift, 8=Win).
        /// </summary>
        private static void ParseHotkey(string hotkey, out int key, out int mods)
        {
            key = 0;
            mods = 0;
            if (string.IsNullOrEmpty(hotkey)) return;

            foreach (var raw in hotkey.Split('+'))
            {
                var part = raw.Trim();
                if (part.Length == 0) continue;

                switch (part.ToLowerInvariant())
                {
                    case "ctrl": mods |= 1; break;
                    case "alt": mods |= 2; break;
                    case "shift": mods |= 4; break;
                    case "win": mods |= 8; break;
                    default:
                        if (Enum.TryParse<KeyCode>(part, ignoreCase: true, out var code)) key = (int)code;
                        break;
                }
            }
        }

        /// <summary>
        /// Formats a binding for Settings.panelToggleHotkey. Uses KeyCode names (so digits
        /// become "Alpha1"), which is what PanelShell.CheckHotkey parses back.
        /// </summary>
        private static string FormatHotkey(int key, int mods)
        {
            var text = "";
            if ((mods & 1) != 0) text += "Ctrl+";
            if ((mods & 2) != 0) text += "Alt+";
            if ((mods & 4) != 0) text += "Shift+";
            if ((mods & 8) != 0) text += "Win+";
            if (key == 0) return text.TrimEnd('+');
            return text + ((KeyCode)key).ToString();
        }

        private void ApplyBinding(string target, int key, int mods)
        {
            switch (target)
            {
                case "selectAll":          editorShortcuts.selectAllKey = key; editorShortcuts.selectAllModifiers = mods; break;
                case "deselectAll":        editorShortcuts.deselectAllKey = key; editorShortcuts.deselectAllModifiers = mods; break;
                case "toggleVisibility":   editorShortcuts.toggleVisibilityKey = key; editorShortcuts.toggleVisibilityModifiers = mods; break;
                case "focusDecoration":    editorShortcuts.focusDecorationKey = key; editorShortcuts.focusDecorationModifiers = mods; break;
                case "goToFloor":          editorShortcuts.goToFloorKey = key; editorShortcuts.goToFloorModifiers = mods; break;
                case "selectAllFloors":    editorShortcuts.selectAllFloorsKey = key; editorShortcuts.selectAllFloorsModifiers = mods; break;
                case "popupSave":          editorShortcuts.popupSaveKey = key; editorShortcuts.popupSaveModifiers = mods; break;
                case "popupDiscard":       editorShortcuts.popupDiscardKey = key; editorShortcuts.popupDiscardModifiers = mods; break;
                case "editorPause":        compatibility.editorPauseKey = key; compatibility.editorPauseModifiers = mods; break;
                case "panelToggle":        panelToggleHotkey = FormatHotkey(key, mods); break;
            }
            Save();
        }

        private static string ShortcutDisplay(int key, int modifiers)
        {
            // Modifier bit layout MUST match EditorShortcutPatches.MOD_*:
            // 1=Ctrl, 2=Alt, 4=Shift, 8=Win (also used by the pause-key check).
            var modStr = "";
            if ((modifiers & 1) != 0) modStr += "Ctrl+";
            if ((modifiers & 2) != 0) modStr += "Alt+";
            if ((modifiers & 4) != 0) modStr += "Shift+";
            if ((modifiers & 8) != 0) modStr += "Win+";
            if (key == 0 && modStr != "") return modStr.TrimEnd('+');
            if (key == 0) return "…";
            var keyName = key >= 32 && key <= 126 ? ((char)key).ToString() :
                          Enum.IsDefined(typeof(KeyCode), key) ? ((KeyCode)key).ToString() : "?";
            return modStr + keyName;
        }

        /// <summary>
        /// IMGUI hook, kept for loaders whose settings page is IMGUI (UMM, MiniModLoader).
        /// In the default "panel" mode the real settings tree is the standalone UGUI panel
        /// and this page only explains how to open it. In "umm" mode the very same IML is
        /// painted into this page through <see cref="IrisGuiRenderer"/> instead.
        /// </summary>
        public void OnGUI()
        {
            // UMM calls this only while its settings window is open, so a frame gap means
            // the page was just reopened: that is when a pending implementation change
            // takes effect (switching never tears down the surface currently in use).
            if (!IsUmmPageVisible) LatchUiImplementation();
            _ummPageLastFrame = Time.frameCount;

            if (ActiveUiImplementation == UiImplementationUmm)
            {
                DrawUmmSettingsPage();
                return;
            }

            DrawPanelModeHint();
        }

        /// <summary>Default mode: UMM's page just points at the standalone panel.</summary>
        private void DrawPanelModeHint()
        {
            var hotkey = string.IsNullOrEmpty(panelToggleHotkey) ? "Ctrl+F9" : panelToggleHotkey;
            var highlighted = $"[<color=#D973A5><b>{hotkey}</b></color>]";
            // Get(key, args) is the formatting overload: pass the hotkey as {0} (passing
            // the language id here would format the template with "zh"/"en").
            var hint = Localization.Get("PanelHotkeyHint", highlighted);

            var textStyle = new GUIStyle(IridiumLayout.SecondaryText) { richText = true, wordWrap = true };
            GUILayout.Label(hint, textStyle);

            if (LinkButton(Localization.Get("PanelHotkeyOpenLink", language)))
                Iridium.UI.PanelShell.Show();
        }

        /// <summary>
        /// "umm" mode: paint the settings IML into UMM's own page. UMM is drawn below the
        /// UGUI panel canvas, so the two cannot coexist; while the standalone panel is
        /// still open this page only asks the user to close it first.
        /// </summary>
        private void DrawUmmSettingsPage()
        {
            if (Iridium.UI.PanelShell.IsVisible)
            {
                var noticeStyle = new GUIStyle(IridiumLayout.SecondaryText) { richText = true, wordWrap = true };
                GUILayout.Label(Localization.Get("UiUmmClosePanelFirst"), noticeStyle);
                if (GUILayout.Button(Localization.Get("UiUmmClosePanelButton"),
                        IridiumLayout.PrimaryButton, GUILayout.ExpandWidth(false)))
                {
                    Iridium.UI.PanelShell.SetVisible(false);
                }
                return;
            }

            try
            {
                IridiumLayout.EnsureTexturesAlive();
                InitializeGuiRenderer();
                _guiRenderer!.Render(System.IO.Path.Combine(
                    Main.Handler?.ModPath ?? "",
                    "Resources", "ui", "Settings.iml"));
            }
            catch (Exception ex)
            {
                Main.Logger?.Error($"[Settings] Failed to render UMM settings page: {ex}");
            }
        }

        private static GUIStyle? _linkStyle;

        /// <summary>Underlined accent-coloured text that behaves like a link.</summary>
        private static bool LinkButton(string text)
        {
            var accent = new Color(0xD9 / 255f, 0x73 / 255f, 0xA5 / 255f, 1f);

            _linkStyle ??= new GUIStyle(IridiumLayout.SecondaryText)
            {
                richText = false,
                wordWrap = false,
                alignment = TextAnchor.MiddleLeft,
            };
            _linkStyle.normal.textColor = accent;
            _linkStyle.hover.textColor = Color.white;
            _linkStyle.active.textColor = Color.white;

            bool clicked = GUILayout.Button(text, _linkStyle, GUILayout.ExpandWidth(false));

            if (Event.current != null && Event.current.type == EventType.Repaint)
            {
                var rect = GUILayoutUtility.GetLastRect();
                if (rect.height > 0f)
                {
                    var previous = GUI.color;
                    GUI.color = accent;
                    GUI.DrawTexture(new Rect(rect.x, rect.yMax - 1f, rect.width, 1f), Texture2D.whiteTexture);
                    GUI.color = previous;
                }
            }

            return clicked;
        }

        /// <summary>True while the user is recording a new shortcut in the settings UI.</summary>
        public bool IsCapturingHotkey => _isBindingPauseKey;

        /// <summary>
        /// Per-frame UI tick: key-binding capture plus the UGUI renderer's dirty rebuild.
        /// </summary>
        public void TickUI()
        {
            try
            {
            ApplyPendingCaret();
            _defaultLobbyMusicPathCache ??= lobbyMusic.defaultMusicPath;
            _fastLobbyMusicPathCache ??= lobbyMusic.fastMusicPath;

            // While recording a shortcut the tree must stay untouched: a rebuild would
            // destroy the very button the user just clicked and drop keyboard focus, so the
            // key press was never captured. Refresh once recording ends instead.
            if (_isBindingPauseKey)
            {
                PollBindingCapture();
                // Text-only refresh: the recorder button shows the pending combination,
                // but the tree must not be rebuilt while recording.
                _renderer?.RefreshTexts();
                return;
            }

            if (!_rendererInitialized || _renderer == null) return;

            // A rebuild throws the tree away, so remember which field had focus (and where
            // the caret was) and put it back afterwards — otherwise typing one character
            // in the search box would drop focus and the results would never update.
            string focusName = null;
            string focusId = null;
            int caret = 0;
            // Unity's fake-null means `?.` is not enough: a destroyed EventSystem or
            // GameObject still compares unequal to null.
            var eventSystem = UnityEngine.EventSystems.EventSystem.current;
            if (eventSystem != null)
            {
                var selected = eventSystem.currentSelectedGameObject;
                if (selected != null)
                {
                    var field = selected.GetComponent<UnityEngine.UI.InputField>();
                    if (field != null)
                    {
                        focusName = field.gameObject.name;
                        focusId = selected.GetComponent<Iris.Iml.IrisGoRenderer.SuppressOnDestroy>()?.FieldId;
                        caret = field.caretPosition;
                    }
                }
            }

            _renderer.Tick();

            if (focusName == null) return;
            foreach (var candidate in Resources.FindObjectsOfTypeAll<UnityEngine.UI.InputField>())
            {
                if (candidate == null) continue;           // destroyed entries are reported
                if (candidate.gameObject == null) continue;

                // Prefer the stable field id: every slider value box is called
                // "ValueField", so matching by name focused an arbitrary one.
                bool match;
                if (!string.IsNullOrEmpty(focusId))
                {
                    var marker = candidate.GetComponent<Iris.Iml.IrisGoRenderer.SuppressOnDestroy>();
                    match = marker != null && marker.FieldId == focusId;
                }
                else
                {
                    match = candidate.gameObject.name == focusName;
                }
                if (!match) continue;
                if (!candidate.gameObject.activeInHierarchy) continue;
                candidate.ActivateInputField();

                int length = candidate.text != null ? candidate.text.Length : 0;
                int clamped = Mathf.Clamp(caret, 0, length);
                candidate.caretPosition = clamped;
                candidate.selectionAnchorPosition = clamped;
                candidate.selectionFocusPosition = clamped;

                // Activation happens in the next LateUpdate and resets the caret, so keep
                // re-applying it for a couple of frames (see ApplyPendingCaret).
                _caretField = candidate;
                _caretIndex = clamped;
                _caretFramesLeft = 3;
                break;
            }
            }
            catch (Exception ex)
            {
                // Never let the settings UI take down the whole frame: Main.OnUpdate does
                // patch maintenance after this call.
                if (!_tickUiErrorLogged)
                {
                    _tickUiErrorLogged = true;
                    Main.Logger?.Error($"[Settings] TickUI failed (further errors suppressed): {ex}");
                }
            }
        }

        private bool _tickUiErrorLogged;

        // Caret restoration across a rebuild. InputField activation is deferred to the
        // next LateUpdate and resets the caret, so a single assignment right after
        // ActivateInputField() is lost — which made every new character insert at
        // position 0 ("1234" arrived as "4321"). Re-apply for a couple of frames.
        private UnityEngine.UI.InputField? _caretField;
        private int _caretIndex;
        private int _caretFramesLeft;

        private void ApplyPendingCaret()
        {
            if (_caretField == null || _caretFramesLeft <= 0) return;

            // The user is placing the caret by hand (or selecting text) — their click wins.
            // Without this, the frames of caret re-application that follow a rebuild would
            // snap the caret back to the pre-rebuild position (often 0).
            if (Input.GetMouseButtonDown(0) || Input.GetMouseButton(0))
            {
                _caretField = null;
                return;
            }

            var eventSystem = UnityEngine.EventSystems.EventSystem.current;
            if (eventSystem == null || eventSystem.currentSelectedGameObject != _caretField.gameObject)
            {
                _caretField = null;
                return;
            }

            int length = _caretField.text != null ? _caretField.text.Length : 0;
            int caret = Mathf.Clamp(_caretIndex, 0, length);
            _caretField.caretPosition = caret;
            _caretField.selectionAnchorPosition = caret;
            _caretField.selectionFocusPosition = caret;

            if (--_caretFramesLeft <= 0) _caretField = null;
        }

        /// <summary>
        /// Key binding capture. The IMGUI version read <c>Event.current</c>; UGUI has no
        /// equivalent, so poll the keyboard directly while capture is armed.
        /// </summary>
        private void PollBindingCapture()
        {
            // Ignore the frame the capture started on: that click's own key press would
            // immediately be recorded as the new binding.
            if (Time.frameCount <= _bindKeyStartFrame) return;

            // Any click ends recording and commits the pending combination (clicking the
            // recorder button again does the same, see ToggleBinding).
            if (Input.GetMouseButtonDown(0))
            {
                FinishBinding();
                return;
            }

            int mods = 0;
            if (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)) mods |= 1;
            if (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)) mods |= 4;
            if (Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt)) mods |= 2;
            // Use the Windows keys on Windows; Command/Apple only exists on macOS
            // (KeyCode.LeftCommand aliases a different virtual key there and was producing
            // a spurious "Win" modifier).
            bool win = Input.GetKey(KeyCode.LeftWindows) || Input.GetKey(KeyCode.RightWindows);
            if (Application.platform == RuntimePlatform.OSXPlayer)
                win |= Input.GetKey(KeyCode.LeftCommand) || Input.GetKey(KeyCode.RightCommand);
            if (win) mods |= 8;

            // Scan every KeyCode, not 0..255: function keys (F1..F15 = 282..296) live above
            // that range, so F9 could never be recorded.
            foreach (var key in AllKeys)
            {
                if (key == KeyCode.None) continue;
                if (IsModifierKey(key)) continue;
                if (IsMouseKey(key)) continue;
                if (!Input.GetKeyDown(key)) continue;

                if (key == KeyCode.Escape)
                {
                    CancelBinding();
                    return;
                }

                // Arm the combination; it is committed when focus is lost. Format it like the row
                // that owns it so the button text doesn't change shape while recording.
                _pendingKey = (int)key;
                _pendingMods = mods;
                _bindingPreview = _bindingTarget == "panelToggle"
                    ? FormatHotkey(_pendingKey, _pendingMods)
                    : ShortcutDisplay(_pendingKey, _pendingMods);
                return;
            }

            // Nothing pressed yet: preview the held modifiers.
            if (mods != 0)
            {
                _bindingPreview = _bindingTarget == "panelToggle"
                    ? FormatHotkey(0, mods)
                    : ShortcutDisplay(0, mods);
            }
        }

        private int _pendingKey;
        private int _pendingMods;

        /// <summary>Click on a recorder button: arm, or commit if that row is already armed.</summary>
        private void ToggleBinding(string target)
        {
            if (_isBindingPauseKey)
            {
                FinishBinding();
                return;
            }

            // The click that ended the previous recording must not immediately re-arm it.
            if (Time.unscaledTime - _bindingFinishedTime < 0.25f) return;

            StartBinding(target);
        }

        /// <summary>Commit the pending combination and leave recording mode.</summary>
        private void FinishBinding()
        {
            var target = _bindingTarget;
            if (!string.IsNullOrEmpty(target) && _pendingKey != 0)
                ApplyBinding(target, _pendingKey, _pendingMods);

            ResetBindingState();
            _bindingFinishedTime = Time.unscaledTime;
            _renderer?.RequestRebuild();
        }

        /// <summary>Esc: drop the pending combination and restore the previous one.</summary>
        private void CancelBinding()
        {
            if (!string.IsNullOrEmpty(_bindingTarget))
                ApplyBinding(_bindingTarget, _bindingOldKey, _bindingOldMods);

            ResetBindingState();
            _bindingFinishedTime = Time.unscaledTime;
            _renderer?.RequestRebuild();
        }

        private void ResetBindingState()
        {
            _isBindingPauseKey = false;
            _bindingTarget = null;
            _bindingPreview = null;
            _pendingKey = 0;
            _pendingMods = 0;
        }

        private float _bindingFinishedTime = -99f;

        private static readonly KeyCode[] AllKeys = (KeyCode[])Enum.GetValues(typeof(KeyCode));

        private static bool IsMouseKey(KeyCode key)
            => key >= KeyCode.Mouse0 && key <= KeyCode.Mouse6;

        private static bool IsModifierKey(KeyCode key) => key switch
        {
            KeyCode.LeftControl or KeyCode.RightControl => true,
            KeyCode.LeftShift or KeyCode.RightShift => true,
            KeyCode.LeftAlt or KeyCode.RightAlt => true,
            KeyCode.LeftCommand or KeyCode.RightCommand => true,
            KeyCode.LeftWindows or KeyCode.RightWindows => true,
            _ => false,
        };

        public void Save()
        {
            Main.Handler?.SaveSettings(this);
        }

        // ---- Window geometry persistence (used by Iridium.UI.PanelShell) ----

        public bool HasWindowRect => ui.windowRectSaved && ui.windowWidth > 0f && ui.windowHeight > 0f;

        public void GetWindowRect(out float x, out float y, out float width, out float height)
        {
            x = ui.windowX;
            y = ui.windowY;
            width = ui.windowWidth;
            height = ui.windowHeight;
        }

        public void SetWindowRect(float x, float y, float width, float height)
        {
            ui.windowRectSaved = true;
            ui.windowX = x;
            ui.windowY = y;
            ui.windowWidth = width;
            ui.windowHeight = height;
        }

        /// <summary>
        /// 3.4.0 把 Perfect 拆分为 PerfectMinus/XPerfect/PerfectPlus。旧配置的
        /// perfect 文案在 4.0 下不再被任何判定命中，这里一次性继承到三个新字段
        /// （仅当新字段仍是默认值时，幂等）。空串同样继承：按 Iridium 约定，
        /// 空模板 = 隐藏该判定文本。
        /// </summary>
        public static void MigrateJudgeText(Settings settings)
        {
            var jt = settings.judgeText;
            if (!jt.extendedHitMargins) return;
            if (jt.perfect == "Perfect") return;

            if (jt.perfectMinus == "PerfectMinus") jt.perfectMinus = jt.perfect;
            if (jt.xPerfect == "XPerfect") jt.xPerfect = jt.perfect;
            if (jt.perfectPlus == "PerfectPlus") jt.perfectPlus = jt.perfect;
        }

        /// <summary>
        /// A hotkey without a key part (e.g. "Win" or "") leaves the panel impossible to
        /// open, so restore the default when the stored value cannot trigger anything.
        /// </summary>
        public static void ValidatePanelHotkey(Settings settings)
        {
            ParseHotkey(settings.panelToggleHotkey, out int key, out _);
            if (key != 0) return;
            settings.panelToggleHotkey = "Ctrl+F9";
            Main.Handler?.SaveSettings(settings);
        }

        /// <summary>
        /// A stored "umm" implementation is meaningless when the mod was not loaded by
        /// UnityModManager: there is no IMGUI page to paint into. Fall back at runtime
        /// (the stored value is deliberately left untouched so the preference survives a
        /// later run under UMM).
        /// </summary>
        public static void ValidateUiImplementation(Settings settings)
        {
            // Missing/corrupt values (older saves, hand-edited XML) fall back to the panel.
            if (settings.ui.uiImplementation != UiImplementationUmm
                && settings.ui.uiImplementation != UiImplementationPanel)
            {
                settings.ui.uiImplementation = UiImplementationPanel;
                Main.Handler?.SaveSettings(settings);
            }

            settings.LatchUiImplementation();
            if (settings.ui.uiImplementation == UiImplementationUmm && !UmmAvailable)
            {
                Main.Logger?.Warning(
                    "[Settings] UI implementation is set to UnityModManager, but the mod was not " +
                    "loaded by UMM; falling back to the standalone panel for this session.");
            }
        }

        public static void ValidateCustomEasingConflict(Settings settings)
        {
            if (!settings.optimizer.enableCustomEasingEngine) return;

            bool hasConflict = settings.optimizer.optimizeMoveTrack
                            || settings.optimizer.optimizeRecolorTrack
                            || settings.optimizer.optimizeMoveDecorations;

            if (hasConflict)
            {
                settings.optimizer.enableCustomEasingEngine = false;
                Main.Handler?.SaveSettings(settings);
                Main.Logger?.Warning(Localization.Get("CustomEasingEngineConflictDetected"));
            }
        }

        private static void ApplyCustomEasingMutualExclusion(OptimizerSettings opt)
        {
            if (opt.enableCustomEasingEngine)
            {
                bool changed = false;
                if (opt.optimizeMoveTrack) { opt.optimizeMoveTrack = false; changed = true; }
                if (opt.optimizeRecolorTrack) { opt.optimizeRecolorTrack = false; changed = true; }
                if (opt.optimizeMoveDecorations) { opt.optimizeMoveDecorations = false; changed = true; }
                if (changed) AsyncPatchManager.UpdateOptimizerPatchesAsync();
            }
        }
    }

    /// <summary>
    /// Bridges <see cref="Iris.Iml.IIrrLayout"/> onto the shared IMGUI layout engine in
    /// <see cref="Iridium.UI.IridiumLayout"/> so the UMM page uses the same theme tokens,
    /// metrics and textures as every other IMGUI surface.
    /// </summary>
    public class IridiumLayoutAdapter : Iris.Iml.IIrrLayout
    {
        public void BeginHorizontal(Iris.Iml.IrrContStyle style, GUILayoutOption[] options)
            => IridiumLayout.Engine.Begin(ContainerDirection.Horizontal, (ContainerStyle)(int)style, null, options);

        public void BeginVertical(Iris.Iml.IrrContStyle style, GUILayoutOption[] options)
            => IridiumLayout.Engine.Begin(ContainerDirection.Vertical, (ContainerStyle)(int)style, null, options);

        public void End() => IridiumLayout.Engine.End();

        public bool Button(string text, Iris.Iml.IrrButStyle style)
            => IridiumLayout.Engine.Button(text, (ButtonStyle)(int)style);

        public void Text(string text, Iris.Iml.IrrTextStyle style)
            => IridiumLayout.Engine.Text(text, (TextStyle)(int)style);

        public bool? Switch(bool on) => IridiumLayout.Engine.Switch(on);
        public bool? Checkbox(bool on) => IridiumLayout.Engine.Checkbox(on);
        public void Separator() => IridiumLayout.Engine.Separator();
        public void Space(double size) => IridiumLayout.Engine.Space(size);
        public void Fill() => IridiumLayout.Engine.Fill();

        public string? TextField(string content) => IridiumLayout.Engine.TextField(content);

        public string? TextField(string content, string? hint, int? width)
            => IridiumLayout.Engine.TextField(content, hint, width);

        public bool Icon(Iris.Iml.IrrIconStyle style)
            => IridiumLayout.Engine.Icon((IconStyle)(int)style);

        public void Link(string text, string url)
        {
            if (IridiumLayout.Engine.Link(text))
                Application.OpenURL(url);
        }
    }
}
