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

        private string? _defaultLobbyMusicPathCache;
        private string? _fastLobbyMusicPathCache;

        private string _currentTab = "general";
        public string currentTab => _currentTab;

        // Collapsible section states (default collapsed). Used by the settings IML
        // to show/hide option groups so the panel stays readable.
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
        private int _bindingOldKey, _bindingOldMods;

        private string[] _cachedTabDisplayNames = System.Array.Empty<string>();
        private string _cachedLanguage = "";

        private IrisGuiRenderer _renderer;
        private bool _rendererInitialized = false;

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

        private void InitializeRenderer()
        {
            if (_rendererInitialized) return;

            _renderer = new IrisGuiRenderer();
            if (Main.Logger != null)
                _renderer.LogDelegate = msg => Main.Logger.Log(msg);  // 适配签名
            _renderer.SetHotReload(false);

            // DataContext wraps 'this' as 'settings' - expressions use settings.currentTab etc.
            // This is set once; no need to update every frame since 'this' is a reference.
            _renderer.SetDataContext(new { settings = this });

            _renderer.RegisterFunction("localize", args =>
            {
                if (args.Length > 0 && args[0] is string key)
                    return Localization.Get(key);
                return "";
            });

            _renderer.RegisterFunction("getVersion", args => VersionManager.GetFullVersionString());
            _renderer.RegisterFunction("getAsyncStatus", args =>
                AsyncPatchManager.IsProcessing ? "⏳ " + Localization.Get("AsyncPatchProcessing") : "");
            _renderer.RegisterFunction("getLanguages", args =>
            {
                var langs = Localization.AvailableLanguages;
                var result = new List<object>();
                foreach (var l in langs)
                    result.Add(new { key = l, displayName = Localization.GetDisplayName(l) });
                return result;
            });

            _renderer.RegisterFunction("getShortcutDisplay", args =>
            {
                if (args.Length >= 2 && args[0] is int key && args[1] is int mods)
                    return ShortcutDisplay(key, mods);
                return "";
            });

            _renderer.RegisterHandler<string>("OnTabClick", key => { _currentTab = key; });
            _renderer.RegisterHandler<string>("OnSectionToggle", key => ToggleSection(key));
            _renderer.RegisterHandler<string>("OnLanguageClick", lang => { language = lang; Save(); });
            _renderer.RegisterHandler<string>("OnInfoToggle", key => ToggleInfo(key));
            _renderer.RegisterFunction("isInfoExpanded", args =>
                args.Length > 0 && args[0] is string key && IsInfoExpanded(key));

            RegisterShortcutHandlers();

            _renderer.SetLayout(new IridiumLayoutAdapter());

            RegisterOptimizerHandlers();
            RegisterUIHandlers();
            RegisterLevelSelectHandlers();
            RegisterCompatibilityHandlers();
            RegisterHitSoundHandlers();
            RegisterEditorShortcutsHandlers();
            RegisterAsyncInputHandlers();

            _rendererInitialized = true;
        }

        private void RegisterOptimizerHandlers()
        {
            _renderer.RegisterHandler("OnOptimizerToggled", (obj) =>
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

            _renderer.RegisterHandler("OnCompressToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                optimizer.dontCompress = !value;
                Iridium.Patches.Optimizer.OptimizerShared.ResetTextureOptimizationState();
                Save();
            });

            _renderer.RegisterHandler("OnShowSavedMemoryToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                optimizer.dontShowSavedMemory = !value;
                Save();
            });

            _renderer.RegisterHandler("OnLossyCompressionToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                optimizer.useLossyCompression = value;
                Iridium.Patches.Optimizer.OptimizerShared.ResetTextureOptimizationState();
                Save();
            });

            _renderer.RegisterHandler("OnLossyQualityChanged", (obj) =>
            {
                if (obj is float f)
                {
                    optimizer.lossyQuality = Mathf.Clamp((int)f, 10, 100);
                    Iridium.Patches.Optimizer.OptimizerShared.ResetTextureOptimizationState();
                    Save();
                }
            });

            _renderer.RegisterHandler("OnMultipleOf4Toggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                optimizer.dontResizeMultipleOf4 = !value;
                Iridium.Patches.Optimizer.OptimizerShared.ResetTextureOptimizationState();
                Save();
            });

            _renderer.RegisterHandler("OnDivideByChanged", (obj) =>
            {
                if (obj is float f)
                {
                    optimizer.divideBy = Mathf.Clamp((int)f, 1, 4);
                    Iridium.Patches.Optimizer.OptimizerShared.ResetTextureOptimizationState();
                    Save();
                }
            });

            _renderer.RegisterHandler("OnDontResizeColliderToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                optimizer.dontResizeCollider = !value;
                Save();
            });

            _renderer.RegisterHandler("OnDisableShadowsToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                optimizer.disableShadows = value;
                if (optimizer.enableOptimizer && optimizer.disableShadows)
                    QualitySettings.shadows = ShadowQuality.Disable;
                else
                    QualitySettings.shadows = ShadowQuality.All;
                Save();
            });

            _renderer.RegisterHandler("OnOptimizeDecorationUpdateToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                optimizer.optimizeDecorationUpdate = value;
                Save();
            });

            _renderer.RegisterHandler("OnOptimizeTileUpdateToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                optimizer.optimizeTileUpdate = value;
                Save();
            });

            _renderer.RegisterHandler("OnOptimizeMoveTrackToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                optimizer.optimizeMoveTrack = value;
                Save();
            });

            _renderer.RegisterHandler("OnOptimizeRecolorTrackToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                optimizer.optimizeRecolorTrack = value;
                Save();
            });

            _renderer.RegisterHandler("OnSkipEventIfPausedToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                optimizer.skipEventIfPaused = value;
                Save();
            });

            _renderer.RegisterHandler("OnOptimizeEventIconsToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                optimizer.optimizeEventIcons = value;
                Save();
            });

            _renderer.RegisterHandler("OnOptimizeScnGameUpdateToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                optimizer.optimizeScnGameUpdate = value;
                Save();
            });

            _renderer.RegisterHandler("OnOptimizePlayerInputAllocationsToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                optimizer.optimizePlayerInputAllocations = value;
                AsyncPatchManager.UpdateOptimizerPatchesAsync();
                Save();
            });

            _renderer.RegisterHandler("OnOptimizeRDInputAllocationsToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                optimizer.optimizeRDInputAllocations = value;
                AsyncPatchManager.UpdateOptimizerPatchesAsync();
                Save();
            });

            _renderer.RegisterHandler("OnOptimizeMoveDecorationsToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                optimizer.optimizeMoveDecorations = value;
                Save();
            });

            _renderer.RegisterHandler("OnOptimizeFfxDecorationsToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                optimizer.optimizeFfxDecorations = value;
                AsyncPatchManager.UpdateOptimizerPatchesAsync();
                Save();
            });

            _renderer.RegisterHandler("OnDecorationShaderCacheToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                optimizer.optimizeDecorationShaderCache = value;
                AsyncPatchManager.UpdateOptimizerPatchesAsync();
                Save();
            });

            _renderer.RegisterHandler("OnStaticDecorationBatchingToggled", (obj) =>
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

            _renderer.RegisterHandler("OnOptimizeFloorMeshToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                optimizer.optimizeFloorMesh = value;
                Save();
            });

            _renderer.RegisterHandler("OnOptimizeFiltersToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                optimizer.optimizeFilters = value;
                AsyncPatchManager.UpdateOptimizerPatchesAsync();
                Save();
            });

            _renderer.RegisterHandler("OnFastLoadingToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                optimizer.fastLoading = value;
                Save();
            });

            _renderer.RegisterHandler("OnCustomEasingEngineToggled", (obj) =>
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

            _renderer.RegisterHandler("OnOptimizeParticleToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                optimizer.optimizeParticle = value;
                Save();
            });

            _renderer.RegisterHandler("OnOptimizeParticleInactiveToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                optimizer.optimizeParticleInactive = value;
                Save();
            });

            _renderer.RegisterHandler("OnOptimizeParticleCullingToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                optimizer.optimizeParticleCulling = value;
                Save();
            });

            _renderer.RegisterHandler("OnOptimizeParticleLodToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                optimizer.optimizeParticleLod = value;
                Save();
            });

            _renderer.RegisterHandler("OnOptimizeEventProcessingToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                optimizer.optimizeEventProcessing = value;
                Save();
            });

            _renderer.RegisterHandler("OnOptimizeEditorMouseDetectionToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                optimizer.optimizeEditorMouseDetection = value;
                Save();
            });

            _renderer.RegisterHandler("OnOptimizeEditorEventIndicatorsToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                optimizer.optimizeEditorEventIndicators = value;
                Save();
            });

            _renderer.RegisterHandler("OnOptimizeMoveTrackTweensToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                optimizer.optimizeMoveTrackTweens = value;
                Save();
            });

            _renderer.RegisterHandler("OnBatchMoveDecorationsToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                optimizer.batchMoveDecorations = value;
                Save();
            });

            _renderer.RegisterHandler("OnCustomLevelReadOptimizationToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                optimizer.customLevelReadOptimization = value;
                AsyncPatchManager.UpdatePatchByTypeAsync(typeof(PatchGetCustomLevelName));
                Save();
            });

            _renderer.RegisterHandler("OnFrameSpreadDecorationLoadingToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                optimizer.frameSpreadDecorationLoading = value;
                AsyncPatchManager.UpdateOptimizerPatchesAsync();
                Save();
            });

            _renderer.RegisterHandler("OnDecorationsPerFrameChanged", (obj) =>
            {
                if (obj is float f)
                {
                    optimizer.decorationsPerFrame = Mathf.Clamp((int)f, 10, 500);
                    Save();
                }
            });

            _renderer.RegisterHandler("OnDOTweenGlobalToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                optimizer.optimizeDOTweenGlobal = value;
                if (value)
                    DOTweenOptimizationPatches.ApplyRuntimeSettings();
                else
                    DOTweenOptimizationPatches.ResetRuntimeSettings();
                Save();
            });

            _renderer.RegisterHandler("OnTweenerCapacityChanged", (obj) =>
            {
                if (obj is float f)
                {
                    optimizer.dotweenTweenerCapacity = Mathf.Clamp((int)f, 200, 2000);
                    DOTweenOptimizationPatches.ApplyRuntimeSettings();
                    Save();
                }
            });

            _renderer.RegisterHandler("OnSequenceCapacityChanged", (obj) =>
            {
                if (obj is float f)
                {
                    optimizer.dotweenSequenceCapacity = Mathf.Clamp((int)f, 50, 500);
                    DOTweenOptimizationPatches.ApplyRuntimeSettings();
                    Save();
                }
            });

            _renderer.RegisterHandler("OnDOTweenDefaultRecyclableToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                optimizer.dotweenDefaultRecyclable = value;
                DOTweenOptimizationPatches.ApplyRuntimeSettings();
                AsyncPatchManager.UpdatePatchByTypeAsync(typeof(TweenSafetyPatches));
                Save();
            });

            _renderer.RegisterHandler("OnDOTweenDisableSafeModeToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                optimizer.dotweenDisableSafeMode = value;
                DOTweenOptimizationPatches.ApplyRuntimeSettings();
                Save();
            });

            _renderer.RegisterHandler("OnExtremeOptimizationToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                optimizer.enableExtremeOptimization = value;
                AsyncPatchManager.UpdateOptimizerPatchesAsync();
                Save();
            });

            _renderer.RegisterHandler("OnMaxTweensPerFrameChanged", (obj) =>
            {
                if (obj is float f)
                {
                    optimizer.maxTweensPerFrame = Mathf.Clamp((int)f, 50, 500);
                    Save();
                }
            });

            _renderer.RegisterHandler("OnBasicMemoryOptimizationToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                memory.enableBasicOptimization = value;
                if (value)
                    Modules.FerriteCore.FerriteCoreModule.Enable();
                else
                    Modules.FerriteCore.FerriteCoreModule.Disable();
                Save();
            });

            _renderer.RegisterHandler("OnVirtualMemoryOptimizationToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                memory.enableVirtualMemoryOptimization = value;
                Modules.FerriteCore.VirtualMemoryOptimizer.SetEnabled(value);
                Save();
            });

            _renderer.RegisterHandler("OnVmTrimOnLevelLoadToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                memory.vmTrimOnLevelLoad = value;
                Save();
            });

            _renderer.RegisterHandler("OnVmTrimOnEditorEnterToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                memory.vmTrimOnEditorEnter = value;
                Save();
            });

            _renderer.RegisterHandler("OnOptimizeGameplayAllocationsToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                optimizer.optimizeGameplayAllocations = value;
                AsyncPatchManager.UpdateOptimizerPatchesAsync();
                Save();
            });

            _renderer.RegisterHandler("OnEditorFloorOptimizationToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                optimizer.enableEditorFloorOptimization = value;
                AsyncPatchManager.UpdateOptimizerPatchesAsync();
                Save();
            });

            _renderer.RegisterHandler("OnIncrementalFloorInsertToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                optimizer.incrementalFloorInsert = value;
                Save();
            });

            _renderer.RegisterHandler("OnRangeBasedRedrawToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                optimizer.rangeBasedRedraw = value;
                Save();
            });

            _renderer.RegisterHandler("OnSkipRedundantRemakePathToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                optimizer.skipRedundantRemakePath = value;
                Save();
            });

            _renderer.RegisterHandler("OnOptimizeOffsetFloorEventsToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                optimizer.optimizeOffsetFloorEvents = value;
                Save();
            });
        }

        private void RegisterUIHandlers()
        {
            _renderer.RegisterHandler("OnRemoveNewsToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                ui.removeNews = value;
                AsyncPatchManager.UpdatePatchByTypeAsync(typeof(RemoveNewsPatch));
                RemoveNewsPatch.UpdateNews();
                Save();
            });

            _renderer.RegisterHandler("OnHideBetaWatermarkToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                ui.hideBetaWatermark = value;
                AsyncPatchManager.UpdatePatchByTypeAsync(typeof(HideBetaWatermarkPatch));
                HideBetaWatermarkPatch.RefreshBetaWatermark();
                Save();
            });

            _renderer.RegisterHandler("OnForceDifficultyUIToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                ui.forceDifficultyUI = value;
                AsyncPatchManager.UpdatePatchByTypeAsync(typeof(ForceDifficultyUIPatch));
                Save();
            });

            _renderer.RegisterHandler("OnAlwaysCountdownToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                ui.alwaysCountdown = value;
                AsyncPatchManager.UpdatePatchByTypeAsync(typeof(AlwaysCountdownPatch));
                Save();
            });

            _renderer.RegisterHandler("OnEnablePausePlanetTrailToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                ui.enablePausePlanetTrail = value;
                AsyncPatchManager.UpdatePatchByTypeAsync(typeof(PausePlanetTrailPatch));
                Save();
            });

            _renderer.RegisterHandler("OnMoveAutoplayTextToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                ui.moveAutoplayText = value;
                AsyncPatchManager.UpdatePatchByTypeAsync(typeof(AutoplayTextPositionPatch));
                AutoplayTextPositionPatch.RefreshAutoplayTextPosition();
                Save();
            });

            _renderer.RegisterHandler("OnAutoplayTextXChanged", (obj) =>
            {
                if (obj is float f)
                {
                    ui.autoplayTextX = f;
                    AutoplayTextPositionPatch.RefreshAutoplayTextPosition();
                    Save();
                }
            });

            _renderer.RegisterHandler("OnAutoplayTextYChanged", (obj) =>
            {
                if (obj is float f)
                {
                    ui.autoplayTextY = f;
                    AutoplayTextPositionPatch.RefreshAutoplayTextPosition();
                    Save();
                }
            });

            _renderer.RegisterHandler("OnShowAutoplayHintUIToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                ui.showAutoplayHintUI = value;
                AsyncPatchManager.UpdatePatchByTypeAsync(typeof(AutoplayHintUIPatch));
                Save();
            });

            _renderer.RegisterHandler("OnCustomAutoplayHintToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                ui.customAutoplayHint = value;
                Save();
            });

            _renderer.RegisterHandler("OnAutoplayHintTemplateChanged", (obj) =>
            {
                if (obj is string s)
                {
                    ui.autoplayHintTemplate = s;
                    Save();
                }
            });

            _renderer.RegisterHandler("OnEnableCircleArcToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                ui.enableCircleArc = value;
                // CircleArcPatch + AllAngleArcCornersPatch form one feature.
                // Apply synchronously (handler already runs on the main thread)
                // so the mesh rebuild below sees the new patch state.
                PatchManager.UpdatePatchByType(typeof(CircleArcPatch));
                PatchManager.UpdatePatchByType(typeof(AllAngleArcCornersPatch));
                RefreshFloorMeshCache();
                Save();
            });

            _renderer.RegisterHandler("OnCircleArcMinAngleChanged", (obj) =>
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

            _renderer.RegisterHandler("OnCircleArcMaxAngleChanged", (obj) =>
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

        // FloorMesh caches built meshes by angle pair, which does not include
        // mod settings. Drop every cached entry and rebuild so a circle-arc
        // toggle takes effect immediately on existing tiles.
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
            _renderer.RegisterHandler("OnLobbyMusicPatchToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                lobbyMusic.enableLobbyMusicPatch = value;
                AsyncPatchManager.UpdatePatchByTypeAsync(typeof(LobbyMusicPatch));
                if (value) LobbyMusicPatch.ReloadFromSettings();
                Save();
            });

            _renderer.RegisterHandler("OnEnableCustomBpmToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                lobbyMusic.enableCustomBpm = value;
                Save();
            });

            _renderer.RegisterHandler("OnCustomBpmChanged", (obj) =>
            {
                if (obj is float f)
                {
                    lobbyMusic.customBpm = f;
                    Save();
                }
            });

            _renderer.RegisterHandler("OnLobbyFastMusicToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                lobbyMusic.fastMusic = value;
                Save();
            });

            _renderer.RegisterHandler("OnLobbyCustomMusicToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                lobbyMusic.customMusic = value;
                LobbyMusicPatch.ReloadFromSettings();
                Save();
            });

            _renderer.RegisterHandler("OnDefaultMusicPathChanged", (obj) =>
            {
                if (obj is string s)
                {
                    _defaultLobbyMusicPathCache = s;
                }
            });

            _renderer.RegisterHandler("OnApplyDefaultMusic", () =>
            {
                lobbyMusic.defaultMusicPath = (_defaultLobbyMusicPathCache ?? string.Empty).Trim();
                LobbyMusicPatch.StartLoad(true, lobbyMusic.defaultMusicPath);
                Save();
            });

            _renderer.RegisterHandler("OnFastMusicPathChanged", (obj) =>
            {
                if (obj is string s)
                {
                    _fastLobbyMusicPathCache = s;
                }
            });

            _renderer.RegisterHandler("OnApplyFastMusic", () =>
            {
                lobbyMusic.fastMusicPath = (_fastLobbyMusicPathCache ?? string.Empty).Trim();
                LobbyMusicPatch.StartLoad(false, lobbyMusic.fastMusicPath);
                Save();
            });

            _renderer.RegisterHandler("OnLobbyReloadMusic", () =>
            {
                LobbyMusicPatch.ReloadFromSettings();
            });
        }

        private void RegisterCompatibilityHandlers()
        {
            if (_compatFlashMode < 0) _compatFlashMode = (int)compatibility.legacyFlashMode;
            if (_compatCamRelMode < 0) _compatCamRelMode = (int)compatibility.legacyCamRelativeToMode;

            _renderer.RegisterHandler("OnEnableLegacyPauseFixToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                compatibility.enableLegacyPauseFix = value;
                AsyncPatchManager.UpdatePatchByTypeAsync(typeof(LegacyPauseFixPatch_Play));
                Save();
            });

            _renderer.RegisterHandler("OnEnableNoFailTooEarlyToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                compatibility.enableNoFailTooEarly = value;
                AsyncPatchManager.UpdatePatchByTypeAsync(typeof(NoFailTooEarlyPatch));
                Save();
            });

            _renderer.RegisterHandler("OnScaleFilterSpeedWithPitchToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                compatibility.scaleFilterSpeedWithPitch = value;
                AsyncPatchManager.UpdatePatchByTypeAsync(typeof(ScaleFilterSpeedWithPitchPatch));
                Save();
            });

            _renderer.RegisterHandler("OnEditorPauseAllowedToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                compatibility.editorPauseAllowed = value;
                Save();
            });

            _renderer.RegisterHandler("OnEditorPauseEnabledToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                compatibility.editorPauseEnabled = value;
                Save();
            });

            _renderer.RegisterHandler("OnFixCameraRelativeDragToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                compatibility.fixCameraRelativeDrag = value;
                AsyncPatchManager.UpdatePatchByTypeAsync(typeof(CameraRelativeDragPatches));
                Save();
            });

            _renderer.RegisterHandler("OnPortalTravelFixToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                compatibility.portalTravelFix = value;
                AsyncPatchManager.UpdatePatchByTypeAsync(typeof(PortalTravelFixPatch));
                Save();
            });

            _renderer.RegisterHandler("OnFixEditorPlayResetMistakesToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                compatibility.fixEditorPlayResetMistakes = value;
                AsyncPatchManager.UpdatePatchByTypeAsync(typeof(EditorPlayResetMistakesPatch));
                Save();
            });

            _renderer.RegisterHandler("OnFixTurnaroundConditionToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                compatibility.fixTurnaroundCondition = value;
                AsyncPatchManager.UpdatePatchByTypeAsync(typeof(TurnaroundConditionFix));
                Save();
            });

            _renderer.RegisterHandler("OnFixJudgeRotationToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                compatibility.fixJudgeRotation = value;
                AsyncPatchManager.UpdatePatchByTypeAsync(typeof(JudgeTextPatches.HitTextMeshShowRotationFixPatch));
                Save();
            });

            _renderer.RegisterHandler("OnFixCoopPauseLockToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                compatibility.fixCoopPauseLock = value;
                AsyncPatchManager.UpdatePatchByTypeAsync(typeof(CoopPauseLockFix));
                AsyncPatchManager.UpdatePatchByTypeAsync(typeof(CoopPauseHandleLockFix));
                AsyncPatchManager.UpdatePatchByTypeAsync(typeof(CoopPlayerHitFix));
                Save();
            });

            _renderer.RegisterHandler("OnForceAngleDataToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                compatibility.forceAngleData = value;
                AsyncPatchManager.UpdatePatchByTypeAsync(typeof(ForceAngleDataPatch));
                Save();
            });

            _renderer.RegisterHandler("OnUseILPatchToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                patchMode.useILPatch = value;
                // Reapply adaptive Mono patches through the backend so their
                // Prefix/Postfix and Transpiler implementations switch together.
                Iridium.Patches.PatchManager.ReapplyAllPatches();
                Save();
            });

            _renderer.RegisterHandler("OnIgnoreRequiredModsToggled", (obj) =>
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
            _renderer.RegisterHandler("OnEnableHitSoundPitchToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                hitSound.enableHitSoundPitch = value;
                AsyncPatchManager.UpdatePatchByTypeAsync(typeof(HitSoundPatch));
                Save();
            });

            _renderer.RegisterHandler("OnJudgeTextCustomizationToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                judgeText.enableJudgeTextCustomization = value;
                AsyncPatchManager.UpdatePatchByTypeAsync(typeof(JudgeTextPatches.HitTextMeshInitPatch));
                AsyncPatchManager.UpdatePatchByTypeAsync(typeof(JudgeTextPatches.HitTextMeshShowPatch));
                Save();
            });

            _renderer.RegisterHandler("OnJudgeTextChanged", (obj) =>
            {
                Save();
            });

            _renderer.RegisterHandler("OnResetJudgeText", () =>
            {
                judgeText.ResetToDefault();
                Save();
            });

            _renderer.RegisterHandler("OnConvertJudgeTextToOffset", () =>
            {
                judgeText.ConvertAllToOffset();
                Save();
            });
        }

        private void RegisterEditorShortcutsHandlers()
        {
            _renderer.RegisterHandler("OnEnableEditorShortcutsToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                editorShortcuts.enableEditorShortcuts = value;
                AsyncPatchManager.UpdatePatchByTypeAsync(typeof(EditorShortcutPatches.EditorShortcutUpdatePatch));
                Save();
            });

            _renderer.RegisterHandler("OnCameraFollowOnFloorSelectToggled", (obj) =>
            {
                bool value = obj is bool b ? b : false;
                editorShortcuts.cameraFollowOnFloorSelect = value;
                Save();
            });
        }

        private void RegisterAsyncInputHandlers()
        {
            _renderer.RegisterHandler("OnAsyncInputToggled", (obj) =>
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
                ("selectAll",       (Action)(() => StartBinding("selectAll"))),
                ("deselectAll",     () => StartBinding("deselectAll")),
                ("toggleVisibility",() => StartBinding("toggleVisibility")),
                ("focusDecoration", () => StartBinding("focusDecoration")),
                ("goToFloor",       () => StartBinding("goToFloor")),
                ("selectAllFloors", () => StartBinding("selectAllFloors")),
                ("popupSave",       () => StartBinding("popupSave")),
                ("popupDiscard",    () => StartBinding("popupDiscard")),
            };
            foreach (var (name, handler) in keys)
            {
                var cap = char.ToUpper(name[0]) + name.Substring(1);
                _renderer.RegisterHandler($"OnBind{cap}Key", (obj) => handler());
            }
            _renderer.RegisterHandler("OnBindEditorPauseKey", (obj) => StartBinding("editorPause"));
        }

        private void StartBinding(string target)
        {
            // Save old values for cancel
            GetBinding(target, out _bindingOldKey, out _bindingOldMods);
            _bindingTarget = target;
            _isBindingPauseKey = true;
            _bindKeyStartFrame = Time.frameCount;
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
            }
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

        public void OnGUI()
        {
            int initialStackDepth = IridiumLayout.Engine.ContainerStack.Count;

            try
            {
                EnsureTexturesAlive();

                _defaultLobbyMusicPathCache ??= lobbyMusic.defaultMusicPath;
                _fastLobbyMusicPathCache ??= lobbyMusic.fastMusicPath;

                InitializeRenderer();

                string imlPath = System.IO.Path.Combine(
                    Main.Handler?.ModPath ?? "",
                    "Resources", "ui", "Settings.iml");

                if (System.IO.File.Exists(imlPath))
                {
                    _renderer.Render(imlPath);
                }
                else
                {
                    // fallback: hardcoded minimal UI if IML file missing
                    IridiumLayout.Render(
                        IridiumLayout.VBox(
                            IridiumLayout.ContainerStyle.Padding,
                            null,
                            IridiumLayout.Text("IML file not found: Settings.iml", IridiumLayout.TextStyle.Secondary)
                        )
                    );
                }

                // Key binding capture — real-time display
                if (_isBindingPauseKey)
                {
                    var ev = Event.current;
                    if (ev != null && ev.type == EventType.KeyDown)
                    {
                        var kc = ev.keyCode;
                        bool isMod = kc == KeyCode.LeftControl || kc == KeyCode.RightControl ||
                            kc == KeyCode.LeftShift || kc == KeyCode.RightShift ||
                            kc == KeyCode.LeftAlt || kc == KeyCode.RightAlt ||
                            kc == KeyCode.LeftCommand || kc == KeyCode.RightCommand;

                        int mods = 0;
                        if (ev.control) mods |= 1;
                        // Bit layout must match EditorShortcutPatches.MOD_*:
                        // 2=Alt, 4=Shift (NOT Unity's Event field order).
                        if (ev.shift) mods |= 4;
                        if (ev.alt) mods |= 2;
                        if (ev.command) mods |= 8;

                        if (isMod)
                        {
                            // Real-time modifier display: show "Ctrl+..." etc.
                            ApplyBinding(_bindingTarget, 0, mods);
                            ev.Use();
                        }
                        else if (kc != KeyCode.None && kc != KeyCode.Escape)
                        {
                            // Final key pressed — save binding
                            ApplyBinding(_bindingTarget, (int)kc, mods);
                            _isBindingPauseKey = false;
                            _bindingTarget = null;
                            ev.Use();
                        }
                        else if (kc == KeyCode.Escape)
                        {
                            // Cancel — restore old binding
                            ApplyBinding(_bindingTarget, _bindingOldKey, _bindingOldMods);
                            _isBindingPauseKey = false;
                            _bindingTarget = null;
                            ev.Use();
                        }
                    }
                }

                if (GUI.changed) Save();
            }
            catch (Exception ex)
            {
                Main.Logger?.Error($"[OnGUI] Settings.OnGUI failed: {ex}");
                throw;
            }
            finally
            {
                while (IridiumLayout.Engine.ContainerStack.Count > initialStackDepth)
                {
                    try { IridiumLayout.Engine.End(); }
                    catch { break; }
                }
            }
        }

        public void Save()
        {
            Main.Handler?.SaveSettings(this);
        }

        /// <summary>
        /// 3.4.0 把 Perfect 拆分为 PerfectMinus/XPerfect/PerfectPlus。旧配置的
        /// perfect 自定义文案在 4.0 下不再被任何判定命中，这里一次性继承到三个
        /// 新字段（仅当新字段仍是默认值时，幂等）。
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

        public bool Icon(Iris.Iml.IrrIconStyle style)
            => IridiumLayout.Engine.Icon((IconStyle)(int)style);

        public void Link(string text, string url)
        {
            if (IridiumLayout.Engine.Link(text))
                Application.OpenURL(url);
        }
    }
}
