using System;
using System.Collections.Generic;
using UnityEngine;
using Iridium.UI;
using Iridium.Config;
using Iridium.Patches;
using System.Linq;
using static Iridium.UI.IridiumLayout;

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

        public string panelToggleHotkey = "Ctrl+F9";

        private string? _defaultLobbyMusicPathCache;
        private string? _fastLobbyMusicPathCache;

        private int _currentTabIndex;
        private Vector2 _contentScrollPosition = Vector2.zero;
        private SizesGroup.Holder _sizesHolder = new();

        private static readonly string[] TabNames = new string[]
        {
            "EnableOptimizer",
            "UISettings",
            "LevelSelectSettings",
            "CompatibilitySettings",
            "HitSoundSettings",
            "AsyncInputSettings",
            "EditorShortcuts"
        };

        private int _compatFlashMode = -1;
        private int _compatCamRelMode = -1;

        private string[] _cachedTabDisplayNames = System.Array.Empty<string>();
        private string _cachedLanguage = "";

        private string[] GetTabDisplayNames()
        {
            if (_cachedLanguage != language || _cachedTabDisplayNames.Length == 0)
            {
                _cachedTabDisplayNames = TabNames.Select(n => Localization.Get(n)).ToArray();
                _cachedLanguage = language;
            }
            return _cachedTabDisplayNames;
        }

        private static object[] WithWidthMax(params Element[] children)
        {
            var content = new object[children.Length + 1];
            content[0] = WidthMax;
            Array.Copy(children, 0, content, 1, children.Length);
            return content;
        }

        public void OnGUI()
        {
            try
            {
                EnsureTexturesAlive();

                _defaultLobbyMusicPathCache ??= lobbyMusic.defaultMusicPath;
                _fastLobbyMusicPathCache ??= lobbyMusic.fastMusicPath;

                Element[] tabContent = _currentTabIndex switch
                {
                    0 => DrawOptimizerTab(),
                    1 => DrawUISettingsTab(),
                    2 => DrawLevelSelectTab(),
                    3 => DrawCompatibilityTab(),
                    4 => DrawHitSoundAndJudgeTextTab(),
                    5 => DrawAsyncInputTab(),
                    _ => DrawEditorShortcutsTab(),
                };

                var langButtons = new List<object> { Fill() };
                foreach (var lang in Localization.AvailableLanguages)
                {
                    var isCurrent = language == lang;
                    var displayName = Localization.GetDisplayName(lang);
                    langButtons.Add(Button(displayName.ToUpper(), isCurrent ? ButtonStyle.Primary : ButtonStyle.Element, () => language = lang, Height(28)));
                    langButtons.Add(Space(2));
                }

                string asyncStatus = AsyncPatchManager.IsProcessing ? "⏳ " + Localization.Get("AsyncPatchProcessing") : "";

                IridiumLayout.Render(
                    VBox(
                        ContainerStyle.Padding,
                        null,
                        HBox(
                            ContainerStyle.None,
                            null,
                            Space(4),
                            Selector(_currentTabIndex, GetTabDisplayNames(), i => _currentTabIndex = i, ButtonStyle.Element, ButtonStyle.Primary, WidthMin),
                            Fill(),
                            Space(4),
                            Text($"Iridium {VersionManager.GetFullVersionString()}", TextStyle.Secondary)
                        ),
                        VBox(ContainerStyle.Background, null, WithWidthMax(tabContent)),
                        Space(2),
                        Text(asyncStatus, TextStyle.Secondary, WidthMax),
                        Space(2),
                        HBox(ContainerStyle.None, null, langButtons.ToArray())
                    )
                );

                HandleShortcutKeyCapture();

                if (GUI.changed) Save();
            }
            catch (Exception ex)
            {
                Main.Logger?.Error($"Settings.OnGUI failed: {ex}");
                throw;
            }
        }

        #region Optimizer Tab
        private Element[] DrawOptimizerTab()
        {
            var sizes = _sizesHolder.Begin();

            var elements = new List<Element>
            {
                Text(Localization.Get("EnableOptimizer"), TextStyle.Title),
                Separator(),
                IridiumPreset.SwitchOption(sizes, optimizer.enableOptimizer, v =>
                {
                    optimizer.enableOptimizer = v;
                    if (v)
                    {
                        if (optimizer.disableShadows) QualitySettings.shadows = ShadowQuality.Disable;
                    }
                    else
                    {
                        QualitySettings.shadows = ShadowQuality.All;
                    }
                    AsyncPatchManager.UpdateOptimizerPatchesAsync();
                }, "EnableOptimizer")
            };

            var body = new List<Element>();
            body.Add(Separator());

            if (OptimizerPatches.savedVRAM_MB > 0.1f)
            {
                body.Add(IridiumPreset.IconTextFormatted(sizes, IconStyle.Success, "SavedMemoryMsg", OptimizerPatches.savedVRAM_MB.ToString("F2")));
                body.Add(Separator());
            }

            body.Add(Text(Localization.Get("ImageOptimizations"), TextStyle.Subtitle));
            body.Add(Separator());

            var image = new List<Element>();
            image.Add(InvertedSwitchOption(sizes, optimizer.dontCompress, v =>
            {
                optimizer.dontCompress = v;
                OptimizerPatches.ResetTextureOptimizationState();
            }, "CompressImage"));

            image.Add(Separator());
            image.Add(IridiumPreset.SwitchOption(sizes, optimizer.enableRenderScale, v =>
            {
                optimizer.enableRenderScale = v;
                AsyncPatchManager.UpdateOptimizerPatchesAsync();
            }, "RenderScale"));
            image.Add(IridiumPreset.IconText(sizes, IconStyle.Information, "RenderScaleHint"));
            if (optimizer.enableRenderScale)
            {
                image.Add(Separator());
                image.Add(IridiumPreset.IntOption(sizes, optimizer.renderScalePercent, v =>
                {
                    var clamped = Mathf.Clamp(v, 30, 100);
                    if (clamped != optimizer.renderScalePercent) optimizer.renderScalePercent = clamped;
                }, "RenderScalePercent", IntFormat(30, 100)));
            }

            bool compressEnabled = !optimizer.dontCompress;
            if (compressEnabled)
            {
                image.Add(Separator());
                image.Add(InvertedSwitchOption(sizes, optimizer.dontShowSavedMemory, v => optimizer.dontShowSavedMemory = v, "ShowSavedMemory"));

                image.Add(Separator());
                image.Add(IridiumPreset.SwitchOption(sizes, optimizer.useLossyCompression, v =>
                {
                    optimizer.useLossyCompression = v;
                    OptimizerPatches.ResetTextureOptimizationState();
                }, "UseLossyCompression"));

                if (optimizer.useLossyCompression)
                {
                    image.Add(Separator());
                    image.Add(IridiumPreset.IntOption(sizes, optimizer.lossyQuality, v =>
                    {
                        var clamped = Mathf.Clamp(v, 10, 100);
                        if (clamped != optimizer.lossyQuality)
                        {
                            optimizer.lossyQuality = clamped;
                            OptimizerPatches.ResetTextureOptimizationState();
                        }
                    }, "LossyQuality", IntFormat(10, 100)));
                }

                image.Add(Separator());
                image.Add(InvertedSwitchOption(sizes, optimizer.dontResizeMultipleOf4, v =>
                {
                    optimizer.dontResizeMultipleOf4 = v;
                    OptimizerPatches.ResetTextureOptimizationState();
                }, "MultipleOf4"));
                if (optimizer.dontCompress) optimizer.dontResizeMultipleOf4 = true;

                image.Add(Separator());
                image.Add(IridiumPreset.DoubleOption(sizes, optimizer.divideBy, v =>
                {
                    if (v != optimizer.divideBy)
                    {
                        optimizer.divideBy = v;
                        OptimizerPatches.ResetTextureOptimizationState();
                    }
                }, "DivideImageBy", DoubleFormat(precision: 1)));
                image.Add(Separator());
                image.Add(InvertedSwitchOption(sizes, optimizer.dontResizeCollider, v => optimizer.dontResizeCollider = v, "DontResizeCollider"));
            }

            body.Add(VBox(ContainerStyle.Background, null, WithWidthMax(image.ToArray())));
            body.Add(Separator());

            body.Add(Text(Localization.Get("CustomEasingEngine"), TextStyle.Subtitle));
            body.Add(Separator());

            var easing = new List<Element>
            {
                IridiumPreset.SwitchOption(sizes, optimizer.enableCustomEasingEngine, v =>
                {
                    optimizer.enableCustomEasingEngine = v;
                    ApplyCustomEasingMutualExclusion(optimizer);
                    AsyncPatchManager.UpdateOptimizerPatchesAsync();
                }, "EnableCustomEasingEngine")
            };
            if (optimizer.enableCustomEasingEngine)
            {
                easing.Add(Separator());
                easing.Add(IridiumPreset.IconText(sizes, IconStyle.Information, "CustomEasingEngineHint"));
            }
            body.Add(VBox(ContainerStyle.Background, null, WithWidthMax(easing.ToArray())));
            body.Add(Separator());

            body.Add(Text(Localization.Get("RenderingOptimizations"), TextStyle.Subtitle));
            body.Add(Separator());

            var rendering = new List<Element>();
            rendering.Add(IridiumPreset.SwitchOption(sizes, optimizer.disableShadows, v =>
            {
                optimizer.disableShadows = v;
                if (optimizer.enableOptimizer && optimizer.disableShadows) QualitySettings.shadows = ShadowQuality.Disable;
                else QualitySettings.shadows = ShadowQuality.All;
            }, "DisableShadows"));
            rendering.Add(Separator());
            rendering.Add(IridiumPreset.SwitchOption(sizes, optimizer.optimizeDecorationUpdate, v => optimizer.optimizeDecorationUpdate = v, "OptimizeDecorationUpdate"));
            rendering.Add(Separator());
            rendering.Add(IridiumPreset.SwitchOption(sizes, optimizer.optimizeDecorationShaderCache, v => optimizer.optimizeDecorationShaderCache = v, "OptimizeDecorationShaderCache"));
            rendering.Add(Separator());
            rendering.Add(IridiumPreset.SwitchOption(sizes, optimizer.optimizeTileUpdate, v => optimizer.optimizeTileUpdate = v, "OptimizeTileUpdate"));
            rendering.Add(Separator());
            rendering.Add(Enabled(
                () => optimizer.enableOptimizer && !optimizer.enableCustomEasingEngine,
                IridiumPreset.SwitchOption(sizes, optimizer.optimizeMoveTrack, v => optimizer.optimizeMoveTrack = v, "OptimizeMoveTrack")
            ));
            rendering.Add(Separator());
            rendering.Add(Enabled(
                () => optimizer.enableOptimizer && !optimizer.enableCustomEasingEngine,
                IridiumPreset.SwitchOption(sizes, optimizer.optimizeRecolorTrack, v => optimizer.optimizeRecolorTrack = v, "OptimizeRecolorTrack")
            ));
            rendering.Add(Separator());
            rendering.Add(Enabled(
                () => optimizer.enableOptimizer && !optimizer.enableCustomEasingEngine,
                IridiumPreset.SwitchOption(sizes, optimizer.skipEventIfPaused, v => optimizer.skipEventIfPaused = v, "SkipEventIfPaused")
            ));
            if (optimizer.skipEventIfPaused)
            {
                rendering.Add(Separator());
                rendering.Add(IridiumPreset.IconText(sizes, IconStyle.Warning, "SkipEventIfPausedWarning"));
                rendering.Add(IridiumPreset.IconText(sizes, IconStyle.Information, "SkipEventIfPausedWarningDetail"));
            }
            rendering.Add(Separator());
            rendering.Add(IridiumPreset.SwitchOption(sizes, optimizer.optimizeEventIcons, v => optimizer.optimizeEventIcons = v, "OptimizeEventIcons"));
            rendering.Add(Separator());
            rendering.Add(IridiumPreset.SwitchOption(sizes, optimizer.optimizeScnGameUpdate, v => optimizer.optimizeScnGameUpdate = v, "OptimizeScnGameUpdate"));
            rendering.Add(Separator());
            rendering.Add(IridiumPreset.SwitchOption(sizes, optimizer.optimizeMoveDecorations, v => optimizer.optimizeMoveDecorations = v, "OptimizeMoveDecorations"));
            rendering.Add(Separator());
            rendering.Add(IridiumPreset.SwitchOption(sizes, optimizer.optimizeFfxDecorations, v => optimizer.optimizeFfxDecorations = v, "OptimizeFfxDecorations"));
            rendering.Add(IridiumPreset.IconText(sizes, IconStyle.Warning, "DOTweenOptimizationWarning"));
            rendering.Add(Separator());
            rendering.Add(IridiumPreset.SwitchOption(sizes, optimizer.optimizeFloorMesh, v => optimizer.optimizeFloorMesh = v, "OptimizeFloorMesh"));
            rendering.Add(Separator());
            rendering.Add(IridiumPreset.SwitchOption(sizes, optimizer.optimizeFilters, v =>
            {
                optimizer.optimizeFilters = v;
                AsyncPatchManager.UpdateOptimizerPatchesAsync();
            }, "OptimizeFilters"));
            rendering.Add(Separator());
            rendering.Add(IridiumPreset.SwitchOption(sizes, optimizer.optimizeGameplayAllocations, v =>
            {
                optimizer.optimizeGameplayAllocations = v;
                AsyncPatchManager.UpdateOptimizerPatchesAsync();
            }, "OptimizeGameplayAllocations"));
            rendering.Add(Separator());
            rendering.Add(IridiumPreset.SwitchOption(sizes, optimizer.optimizeRDInputAllocations, v =>
            {
                optimizer.optimizeRDInputAllocations = v;
                AsyncPatchManager.UpdateOptimizerPatchesAsync();
            }, "OptimizeRDInputAllocations"));
            rendering.Add(Separator());
            rendering.Add(IridiumPreset.SwitchOption(sizes, optimizer.optimizePlayerInputAllocations, v =>
            {
                optimizer.optimizePlayerInputAllocations = v;
                AsyncPatchManager.UpdateOptimizerPatchesAsync();
            }, "OptimizePlayerInputAllocations"));
            rendering.Add(Separator());
            rendering.Add(IridiumPreset.SwitchOption(sizes, optimizer.fastLoading, v => optimizer.fastLoading = v, "FastLoading"));

            body.Add(VBox(ContainerStyle.Background, null, WithWidthMax(rendering.ToArray())));
            body.Add(Separator());

            body.Add(Text(Localization.Get("ParticleOptimizations"), TextStyle.Subtitle));
            body.Add(Separator());

            var particle = new List<Element>
            {
                IridiumPreset.SwitchOption(sizes, optimizer.optimizeParticle, v => optimizer.optimizeParticle = v, "OptimizeParticle")
            };
            if (optimizer.optimizeParticle)
            {
                particle.Add(Separator());
                particle.Add(IridiumPreset.SwitchOption(sizes, optimizer.optimizeParticleInactive, v => optimizer.optimizeParticleInactive = v, "OptimizeParticleInactive"));
                particle.Add(Separator());
                particle.Add(IridiumPreset.SwitchOption(sizes, optimizer.optimizeParticleCulling, v => optimizer.optimizeParticleCulling = v, "OptimizeParticleCulling"));
                particle.Add(Separator());
                particle.Add(IridiumPreset.SwitchOption(sizes, optimizer.optimizeParticleLod, v => optimizer.optimizeParticleLod = v, "OptimizeParticleLod"));
            }
            body.Add(VBox(ContainerStyle.Background, null, WithWidthMax(particle.ToArray())));
            body.Add(Separator());

            body.Add(Text(Localization.Get("SceneOptimizations"), TextStyle.Subtitle));
            body.Add(Separator());

            body.Add(VBox(
                ContainerStyle.Background,
                null,
                WithWidthMax(
                    IridiumPreset.SwitchOption(sizes, optimizer.cacheGameObjectReferences, v => optimizer.cacheGameObjectReferences = v, "CacheGameObjectReferences"),
                    Separator(),
                    IridiumPreset.SwitchOption(sizes, optimizer.optimizeEventProcessing, v => optimizer.optimizeEventProcessing = v, "OptimizeEventProcessing"),
                    Separator(),
                    IridiumPreset.SwitchOption(sizes, optimizer.optimizeEditorMouseDetection, v => optimizer.optimizeEditorMouseDetection = v, "OptimizeEditorMouseDetection"),
                    Separator(),
                    IridiumPreset.SwitchOption(sizes, optimizer.optimizeEditorEventIndicators, v => optimizer.optimizeEditorEventIndicators = v, "OptimizeEditorEventIndicators")
                )
            ));
            body.Add(Separator());

            body.Add(Text(Localization.Get("LoadingOptimizations"), TextStyle.Subtitle));
            body.Add(Separator());

            var loading = new List<Element>
            {
                IridiumPreset.SwitchOption(sizes, optimizer.cacheFloorEvents, v => optimizer.cacheFloorEvents = v, "CacheFloorEvents"),
                Separator(),
                IridiumPreset.SwitchOption(sizes, optimizer.optimizeMoveTrackTweens, v => optimizer.optimizeMoveTrackTweens = v, "OptimizeMoveTrackTweens"),
                Separator(),
                IridiumPreset.SwitchOption(sizes, optimizer.batchMoveDecorations, v => optimizer.batchMoveDecorations = v, "BatchMoveDecorations"),
                Separator(),
                IridiumPreset.SwitchOption(sizes, optimizer.customLevelReadOptimization, v =>
                {
                    optimizer.customLevelReadOptimization = v;
                    AsyncPatchManager.UpdatePatchByTypeAsync(typeof(JsonPatches.PatchLevelDataCLSLoadLevel));
                }, "CustomLevelReadOptimization"),
                Separator(),
                IridiumPreset.SwitchOption(sizes, optimizer.frameSpreadDecorationLoading, v =>
                {
                    optimizer.frameSpreadDecorationLoading = v;
                    AsyncPatchManager.UpdateOptimizerPatchesAsync();
                }, "FrameSpreadDecorationLoading")
            };
            if (optimizer.frameSpreadDecorationLoading)
            {
                loading.Add(Separator());
                loading.Add(IridiumPreset.IntOption(sizes, optimizer.decorationsPerFrame, v =>
                {
                    var clamped = Mathf.Clamp(v, 10, 500);
                    if (clamped != optimizer.decorationsPerFrame)
                    {
                        optimizer.decorationsPerFrame = clamped;
                    }
                }, "DecorationsPerFrame", IntFormat(10, 500)));
                loading.Add(Separator());
                loading.Add(IridiumPreset.IconText(sizes, IconStyle.Information, "FrameSpreadLoadingHint"));
            }
            body.Add(VBox(ContainerStyle.Background, null, WithWidthMax(loading.ToArray())));
            body.Add(Separator());

            body.Add(Text(Localization.Get("DOTweenOptimizations"), TextStyle.Subtitle));
            body.Add(Separator());

            var dotween = new List<Element>();
            dotween.Add(IridiumPreset.SwitchOption(sizes, optimizer.optimizeDOTweenGlobal, v =>
            {
                optimizer.optimizeDOTweenGlobal = v;
                if (optimizer.optimizeDOTweenGlobal)
                    DOTweenOptimizationPatches.ApplyRuntimeSettings();
                else
                    DOTweenOptimizationPatches.ResetRuntimeSettings();
            }, "EnableDOTweenOptimization"));

            if (optimizer.optimizeDOTweenGlobal)
            {
                dotween.Add(Separator());
                dotween.Add(IridiumPreset.IntOption(sizes, optimizer.dotweenTweenerCapacity, v =>
                {
                    var clamped = Mathf.Clamp(v, 200, 2000);
                    if (clamped != optimizer.dotweenTweenerCapacity)
                    {
                        optimizer.dotweenTweenerCapacity = clamped;
                        DOTweenOptimizationPatches.ApplyRuntimeSettings();
                    }
                }, "TweenerCapacity", IntFormat(200, 2000)));
                dotween.Add(Separator());
                dotween.Add(IridiumPreset.IntOption(sizes, optimizer.dotweenSequenceCapacity, v =>
                {
                    var clamped = Mathf.Clamp(v, 50, 500);
                    if (clamped != optimizer.dotweenSequenceCapacity)
                    {
                        optimizer.dotweenSequenceCapacity = clamped;
                        DOTweenOptimizationPatches.ApplyRuntimeSettings();
                    }
                }, "SequenceCapacity", IntFormat(50, 500)));
                dotween.Add(Separator());
                dotween.Add(IridiumPreset.SwitchOption(sizes, optimizer.dotweenDefaultRecyclable, v =>
                {
                    optimizer.dotweenDefaultRecyclable = v;
                    DOTweenOptimizationPatches.ApplyRuntimeSettings();
                    AsyncPatchManager.UpdatePatchByTypeAsync(typeof(TweenSafetyPatches));
                }, "DOTweenDefaultRecyclable"));
                dotween.Add(Separator());
                dotween.Add(IridiumPreset.SwitchOption(sizes, optimizer.dotweenDisableSafeMode, v =>
                {
                    optimizer.dotweenDisableSafeMode = v;
                    DOTweenOptimizationPatches.ApplyRuntimeSettings();
                }, "DOTweenDisableSafeMode"));
                dotween.Add(Separator());
                dotween.Add(IridiumPreset.IconText(sizes, IconStyle.Warning, "DOTweenOptimizationRestartRequired"));
            }
            else
            {
                dotween.Add(Separator());
                dotween.Add(IridiumPreset.IconText(sizes, IconStyle.Warning, "DOTweenOptimizationWarning"));
            }

            body.Add(VBox(ContainerStyle.Background, null, WithWidthMax(dotween.ToArray())));
            body.Add(Separator());

            body.Add(Text(Localization.Get("ExtremeOptimizations"), TextStyle.Subtitle));
            body.Add(Separator());

            var extreme = new List<Element>
            {
                IridiumPreset.SwitchOption(sizes, optimizer.enableExtremeOptimization, v =>
                {
                    optimizer.enableExtremeOptimization = v;
                    AsyncPatchManager.UpdateOptimizerPatchesAsync();
                }, "EnableExtremeOptimization")
            };
            if (optimizer.enableExtremeOptimization)
            {
                extreme.Add(Separator());
                extreme.Add(IridiumPreset.IntOption(sizes, optimizer.maxTweensPerFrame, v =>
                {
                    optimizer.maxTweensPerFrame = Mathf.Clamp(v, 50, 500);
                }, "MaxTweensPerFrame", IntFormat(50, 500)));
                extreme.Add(Separator());
                extreme.Add(IridiumPreset.IconText(sizes, IconStyle.Information, "ExtremeOptimizationHint"));
            }
            body.Add(VBox(ContainerStyle.Background, null, WithWidthMax(extreme.ToArray())));
            body.Add(Separator());

            if (typeof(Notification).GetMethod("SetupNotification", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic) == null)
            {
                body.Add(IridiumPreset.IconText(sizes, IconStyle.Error, "MethodNotFound"));
                optimizer.dontShowSavedMemory = true;
                body.Add(Separator());
            }
            if (typeof(scrVisualDecoration).GetProperty("spriteUnscaledSize", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public) == null)
            {
                body.Add(IridiumPreset.IconText(sizes, IconStyle.Error, "PropertyNotFound"));
                optimizer.dontResizeCollider = true;
                body.Add(Separator());
            }

            body.Add(Text(Localization.Get("MemorySettings"), TextStyle.Subtitle));
            body.Add(Separator());

            var memoryContent = new List<Element>
            {
                IridiumPreset.SwitchOption(sizes, memory.enableMemoryOptimization, v =>
                {
                    memory.enableMemoryOptimization = v;
                    AsyncPatchManager.UpdatePatchByTypeAsync(typeof(MiscPatches.SmartGCPatch));
                }, "MemorySettings")
            };
            if (memory.enableMemoryOptimization)
            {
                memoryContent.Add(Separator());
                memoryContent.Add(IridiumPreset.SwitchOption(sizes, memory.enableSmartGC, v =>
                {
                    memory.enableSmartGC = v;
                    AsyncPatchManager.UpdatePatchByTypeAsync(typeof(MiscPatches.SmartGCPatch));
                }, "EnableSmartGC"));

                if (memory.enableSmartGC)
                {
                    memoryContent.Add(Separator());
                    memoryContent.Add(IridiumPreset.DoubleOption(sizes, memory.gcInterval, v => memory.gcInterval = (float)v, "GCInterval", DoubleFormat(precision: 0)));
                    memoryContent.Add(Separator());
                    memoryContent.Add(IridiumPreset.SwitchOption(sizes, memory.gcInGame, v => memory.gcInGame = v, "GCInGame"));
                }
            }
            body.Add(VBox(ContainerStyle.Background, null, WithWidthMax(memoryContent.ToArray())));
            body.Add(Separator());

            body.Add(Text(Localization.Get("EditorFloorOptimizations"), TextStyle.Subtitle));
            body.Add(Separator());

            var editorFloor = new List<Element>
            {
                IridiumPreset.SwitchOption(sizes, optimizer.enableEditorFloorOptimization, v =>
                {
                    optimizer.enableEditorFloorOptimization = v;
                    AsyncPatchManager.UpdateOptimizerPatchesAsync();
                }, "EnableEditorFloorOptimization")
            };
            if (optimizer.enableEditorFloorOptimization)
            {
                editorFloor.Add(Separator());
                editorFloor.Add(IridiumPreset.SwitchOption(sizes, optimizer.incrementalFloorInsert, v =>
                {
                    optimizer.incrementalFloorInsert = v;
                    AsyncPatchManager.UpdateOptimizerPatchesAsync();
                }, "IncrementalFloorInsert"));
                if (optimizer.incrementalFloorInsert)
                {
                    editorFloor.Add(Separator());
                    editorFloor.Add(IridiumPreset.SwitchOption(sizes, optimizer.skipApplyEventsOnInsert, v =>
                    {
                        optimizer.skipApplyEventsOnInsert = v;
                        AsyncPatchManager.UpdateOptimizerPatchesAsync();
                    }, "SkipApplyEventsOnInsert"));
                    editorFloor.Add(Separator());
                    editorFloor.Add(IridiumPreset.SwitchOption(sizes, optimizer.rangeBasedRedraw, v =>
                    {
                        optimizer.rangeBasedRedraw = v;
                        AsyncPatchManager.UpdateOptimizerPatchesAsync();
                    }, "RangeBasedRedraw"));
                    editorFloor.Add(Separator());
                    editorFloor.Add(IridiumPreset.SwitchOption(sizes, optimizer.skipRedundantRemakePath, v =>
                    {
                        optimizer.skipRedundantRemakePath = v;
                        AsyncPatchManager.UpdateOptimizerPatchesAsync();
                    }, "SkipRedundantRemakePath"));
                    editorFloor.Add(Separator());
                    editorFloor.Add(IridiumPreset.SwitchOption(sizes, optimizer.optimizeOffsetFloorEvents, v =>
                    {
                        optimizer.optimizeOffsetFloorEvents = v;
                        AsyncPatchManager.UpdateOptimizerPatchesAsync();
                    }, "OptimizeOffsetFloorEvents"));
                }
                editorFloor.Add(Separator());
                editorFloor.Add(IridiumPreset.IconText(sizes, IconStyle.Warning, "EditorFloorOptimizationWarning"));
            }
            body.Add(VBox(ContainerStyle.Background, null, WithWidthMax(editorFloor.ToArray())));

            // 大谱面加载优化：独立于优化器总开关的功能（不放在 Enabled 里）
            elements.Add(Separator());
            elements.Add(IridiumPreset.SwitchOption(sizes, optimizer.optimizeLargeLevelLoading, v =>
            {
                // 关闭前先把未激活砖块补完，避免 A 的 Awake 替换卸载后与 FastInit 重复初始化
                Iridium.Patches.ChunkedFloorSpawnPatch.FlushAll();
                optimizer.optimizeLargeLevelLoading = v;
                AsyncPatchManager.UpdateOptimizerPatchesAsync();
            }, "OptimizeLargeLevelLoading"));
            elements.Add(Separator());
            elements.Add(IridiumPreset.IconText(sizes, IconStyle.Information, "OptimizeLargeLevelLoadingInfo"));

            // B：分帧激活砖块（依赖大谱面加载优化开启）
            var chunkedSpawnElements = new List<Element>
            {
                IridiumPreset.SwitchOption(sizes, optimizer.chunkedFloorSpawn, v =>
                {
                    Iridium.Patches.ChunkedFloorSpawnPatch.FlushAll();
                    optimizer.chunkedFloorSpawn = v;
                    AsyncPatchManager.UpdateOptimizerPatchesAsync();
                }, "ChunkedFloorSpawn"),
                IridiumPreset.IconText(sizes, IconStyle.Information, "ChunkedFloorSpawnInfo")
            };
            elements.Add(Enabled(() => optimizer.optimizeLargeLevelLoading, chunkedSpawnElements.ToArray()));

            elements.Add(Enabled(() => optimizer.enableOptimizer, body.ToArray()));
            return elements.ToArray();
        }
        #endregion

        #region UI Settings Tab
        private Element[] DrawUISettingsTab()
        {
            var sizes = _sizesHolder.Begin();

            var elements = new List<Element>
            {
                Text(Localization.Get("UISettings"), TextStyle.Title),
                Separator()
            };

            var body = new List<Element>();
            body.Add(IridiumPreset.SwitchOption(sizes, ui.removeNews, v =>
            {
                ui.removeNews = v;
                AsyncPatchManager.UpdatePatchByTypeAsync(typeof(MiscPatches.RemoveNewsPatch));
                MiscPatches.RemoveNewsPatch.UpdateNews();
            }, "RemoveNews"));
            body.Add(Separator());

            body.Add(IridiumPreset.SwitchOption(sizes, ui.hideBetaWatermark, v =>
            {
                ui.hideBetaWatermark = v;
                AsyncPatchManager.UpdatePatchByTypeAsync(typeof(MiscPatches.HideBetaWatermarkPatch));
                MiscPatches.RefreshBetaWatermark();
            }, "HideBetaWatermark"));
            body.Add(Separator());

            body.Add(IridiumPreset.SwitchOption(sizes, ui.forceDifficultyUI, v =>
            {
                ui.forceDifficultyUI = v;
                AsyncPatchManager.UpdatePatchByTypeAsync(typeof(MiscPatches.ForceDifficultyUIPatch));
            }, "ForceDifficultyUI"));
            body.Add(Separator());

            body.Add(IridiumPreset.SwitchOption(sizes, ui.alwaysCountdown, v =>
            {
                ui.alwaysCountdown = v;
                AsyncPatchManager.UpdatePatchByTypeAsync(typeof(MiscPatches.AlwaysCountdownPatch));
            }, "AlwaysCountdown"));
            body.Add(Separator());

            body.Add(IridiumPreset.SwitchOption(sizes, ui.enablePausePlanetTrail, v =>
            {
                ui.enablePausePlanetTrail = v;
                AsyncPatchManager.UpdatePatchByTypeAsync(typeof(MiscPatches.PausePlanetTrailPatch));
            }, "EnablePausePlanetTrail"));
            body.Add(Separator());

            body.Add(IridiumPreset.SwitchOption(sizes, ui.moveAutoplayText, v =>
            {
                ui.moveAutoplayText = v;
                AsyncPatchManager.UpdatePatchByTypeAsync(typeof(MiscPatches.AutoplayTextPositionPatch));
                MiscPatches.RefreshAutoplayTextPosition();
            }, "MoveAutoplayText"));

            if (ui.moveAutoplayText)
            {
                body.Add(Separator());
                body.Add(HBox(
                    ContainerStyle.None,
                    sizes,
                    WidthMax,
                    Align(0.5, 0,
                        Text("X:", TextStyle.Normal, WidthMin),
                        Slider(ui.autoplayTextX, -Screen.width / 2f, Screen.width / 2f, v =>
                        {
                            ui.autoplayTextX = v;
                            MiscPatches.RefreshAutoplayTextPosition();
                        }),
                        Text(ui.autoplayTextX.ToString("F0"), TextStyle.Secondary, Width(40))
                    )
                ));

                body.Add(HBox(
                    ContainerStyle.None,
                    sizes,
                    WidthMax,
                    Align(0.5, 0,
                        Text("Y:", TextStyle.Normal, WidthMin),
                        Slider(ui.autoplayTextY, -Screen.height / 2f, Screen.height / 2f, v =>
                        {
                            ui.autoplayTextY = v;
                            MiscPatches.RefreshAutoplayTextPosition();
                        }),
                        Text(ui.autoplayTextY.ToString("F0"), TextStyle.Secondary, Width(40))
                    )
                ));
            }
            body.Add(Separator());

            body.Add(IridiumPreset.SwitchOption(sizes, ui.enableCircleArc, v =>
            {
                ui.enableCircleArc = v;
                // CircleArcPatch + AllAngleArcCornersPatch form one feature.
                // Apply synchronously (handler already runs on the main thread)
                // so the mesh rebuild below sees the new patch state.
                PatchManager.UpdatePatchByType(typeof(MiscPatches.CircleArcPatch));
                PatchManager.UpdatePatchByType(typeof(MiscPatches.AllAngleArcCornersPatch));
                RefreshFloorMeshCache();
            }, "EnableCircleArc"));

            if (ui.enableCircleArc)
            {
                body.Add(HBox(
                    ContainerStyle.None,
                    sizes,
                    WidthMax,
                    Align(0.5, 0,
                        Text(Localization.Get("CircleArcMinAngle"), TextStyle.Normal, WidthMin),
                        Slider(ui.circleArcMinAngle, 0f, 180f, v =>
                        {
                            ui.circleArcMinAngle = Mathf.Clamp(v, 0f, 180f);
                            if (ui.circleArcMaxAngle < ui.circleArcMinAngle)
                                ui.circleArcMaxAngle = ui.circleArcMinAngle;
                            RefreshFloorMeshCache();
                        }),
                        Text(ui.circleArcMinAngle.ToString("F0"), TextStyle.Secondary, Width(40))
                    )
                ));

                body.Add(HBox(
                    ContainerStyle.None,
                    sizes,
                    WidthMax,
                    Align(0.5, 0,
                        Text(Localization.Get("CircleArcMaxAngle"), TextStyle.Normal, WidthMin),
                        Slider(ui.circleArcMaxAngle, 0f, 180f, v =>
                        {
                            ui.circleArcMaxAngle = Mathf.Clamp(v, 0f, 180f);
                            if (ui.circleArcMaxAngle < ui.circleArcMinAngle)
                                ui.circleArcMinAngle = ui.circleArcMaxAngle;
                            RefreshFloorMeshCache();
                        }),
                        Text(ui.circleArcMaxAngle.ToString("F0"), TextStyle.Secondary, Width(40))
                    )
                ));
            }

            elements.Add(VBox(ContainerStyle.Background, null, WithWidthMax(body.ToArray())));
            return elements.ToArray();
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
        #endregion

        #region Level Select Tab
        private Element[] DrawLevelSelectTab()
        {
            var sizes = _sizesHolder.Begin();

            var elements = new List<Element>
            {
                Text(Localization.Get("LevelSelectSettings"), TextStyle.Title),
                Separator(),
                IridiumPreset.SwitchOption(sizes, lobbyMusic.enableLobbyMusicPatch, v =>
                {
                    lobbyMusic.enableLobbyMusicPatch = v;
                    AsyncPatchManager.UpdatePatchByTypeAsync(typeof(MiscPatches.LobbyMusicPatch));
                    if (lobbyMusic.enableLobbyMusicPatch) MiscPatches.LobbyMusicPatch.ReloadFromSettings();
                }, "LevelSelectSettings")
            };

            var body = new List<Element>();
            body.Add(Separator());

            var inner = new List<Element>();
            inner.Add(IridiumPreset.SwitchOption(sizes, lobbyMusic.enableCustomBpm, v => lobbyMusic.enableCustomBpm = v, "EnableCustomBpm"));

            if (lobbyMusic.enableCustomBpm)
            {
                inner.Add(Separator());
                inner.Add(IridiumPreset.DoubleOption(sizes, lobbyMusic.customBpm, v => lobbyMusic.customBpm = (float)v, "CustomBpm", DoubleFormat(precision: 1)));
            }

            inner.Add(Separator());
            inner.Add(IridiumPreset.SwitchOption(sizes, lobbyMusic.fastMusic, v => lobbyMusic.fastMusic = v, "LobbyFastMusic"));
            inner.Add(Separator());

            inner.Add(IridiumPreset.SwitchOption(sizes, lobbyMusic.customMusic, v =>
            {
                lobbyMusic.customMusic = v;
                MiscPatches.LobbyMusicPatch.ReloadFromSettings();
            }, "LobbyCustomMusic"));

            if (lobbyMusic.customMusic)
            {
                inner.Add(Separator());
                inner.Add(HBox(
                    ContainerStyle.None,
                    sizes,
                    WidthMax,
                    Align(0.5, 0,
                        Text(Localization.Get("LobbyDefaultMusicPath"), TextStyle.Normal, WidthMin),
                        Fill(),
                        TextField(_defaultLobbyMusicPathCache ?? string.Empty, v => _defaultLobbyMusicPathCache = v, null, Width(200)),
                        Space(4),
                        Button(Localization.Get("Apply"), ButtonStyle.Element, () =>
                        {
                            lobbyMusic.defaultMusicPath = (_defaultLobbyMusicPathCache ?? string.Empty).Trim();
                            MiscPatches.LobbyMusicPatch.StartLoad(true, lobbyMusic.defaultMusicPath);
                        }, Width(60))
                    )
                ));

                inner.Add(Separator());
                inner.Add(HBox(
                    ContainerStyle.None,
                    sizes,
                    WidthMax,
                    Align(0.5, 0,
                        Text(Localization.Get("LobbyFastMusicPath"), TextStyle.Normal, WidthMin),
                        Fill(),
                        TextField(_fastLobbyMusicPathCache ?? string.Empty, v => _fastLobbyMusicPathCache = v, null, Width(200)),
                        Space(4),
                        Button(Localization.Get("Apply"), ButtonStyle.Element, () =>
                        {
                            lobbyMusic.fastMusicPath = (_fastLobbyMusicPathCache ?? string.Empty).Trim();
                            MiscPatches.LobbyMusicPatch.StartLoad(false, lobbyMusic.fastMusicPath);
                        }, Width(60))
                    )
                ));

                inner.Add(Separator());
                inner.Add(HBox(
                    ContainerStyle.None,
                    sizes,
                    WidthMax,
                    Fill(),
                    Button(Localization.Get("LobbyReloadMusic"), ButtonStyle.Element, () => MiscPatches.LobbyMusicPatch.ReloadFromSettings(), Width(140))
                ));

                inner.Add(Separator());
                inner.Add(IridiumPreset.IconText(sizes, IconStyle.Information, "LobbyMusicHint"));
            }

            body.Add(VBox(ContainerStyle.Background, null, WithWidthMax(inner.ToArray())));
            elements.Add(Enabled(() => lobbyMusic.enableLobbyMusicPatch, body.ToArray()));
            return elements.ToArray();
        }
        #endregion

        #region Compatibility Tab
        private Element[] DrawCompatibilityTab()
        {
            if (_compatFlashMode < 0) _compatFlashMode = (int)compatibility.legacyFlashMode;
            if (_compatCamRelMode < 0) _compatCamRelMode = (int)compatibility.legacyCamRelativeToMode;

            var sizes = _sizesHolder.Begin();

            var elements = new List<Element>
            {
                Text(Localization.Get("CompatibilitySettings"), TextStyle.Title),
                Separator()
            };

            var top = new List<Element>();
            top.Add(IridiumPreset.SwitchOption(sizes, compatibility.enableLegacyPauseFix, v =>
            {
                compatibility.enableLegacyPauseFix = v;
                AsyncPatchManager.UpdatePatchByTypeAsync(typeof(CompatibilityPatches.LegacyPauseFixPatch_Play));
                AsyncPatchManager.UpdatePatchByTypeAsync(typeof(CompatibilityPatches.LegacyPauseFixPatch_Apply));
            }, "EnableLegacyPauseFix"));
            top.Add(Separator());

            top.Add(IridiumPreset.SwitchOption(sizes, compatibility.enableNoFailTooEarly, v =>
            {
                compatibility.enableNoFailTooEarly = v;
                AsyncPatchManager.UpdatePatchByTypeAsync(typeof(CompatibilityPatches.NoFailTooEarlyPatch));
            }, "EnableNoFailTooEarly"));
            top.Add(Separator());

            top.Add(IridiumPreset.SwitchOption(sizes, compatibility.scaleFilterSpeedWithPitch, v =>
            {
                compatibility.scaleFilterSpeedWithPitch = v;
                AsyncPatchManager.UpdatePatchByTypeAsync(typeof(CompatibilityPatches.ScaleFilterSpeedWithPitchPatch));
            }, "ScaleFilterSpeedWithPitch"));
            top.Add(Separator());

            top.Add(IridiumPreset.SwitchOption(sizes, compatibility.fixCameraRelativeDrag, v =>
            {
                compatibility.fixCameraRelativeDrag = v;
                AsyncPatchManager.UpdatePatchByTypeAsync(typeof(CameraRelativeDragPatches));
            }, "FixCameraRelativeDrag"));

            elements.Add(VBox(ContainerStyle.Background, null, WithWidthMax(top.ToArray())));
            elements.Add(Separator());

            elements.Add(Text(Localization.Get("LegacyLevelBehavior"), TextStyle.Subtitle));
            elements.Add(Separator());

            var legacy = new List<Element>();
            legacy.Add(IridiumPreset.SwitchOption(sizes, compatibility.forceAngleData, v =>
            {
                compatibility.forceAngleData = v;
                AsyncPatchManager.UpdatePatchByTypeAsync(typeof(JsonPatches.ForceAngleDataPatch));
            }, "ForceAngleData"));
            legacy.Add(Separator());

            legacy.Add(IridiumPreset.SelectorOption(
                sizes,
                _compatFlashMode,
                v =>
                {
                    _compatFlashMode = v;
                    ApplyLegacyBehavior();
                },
                new string[] { Localization.Get("ModeDefault"), Localization.Get("ModeAlwaysOff"), Localization.Get("ModeAlwaysOn") },
                "LegacyFlashMode"));
            legacy.Add(Separator());

            legacy.Add(IridiumPreset.SelectorOption(
                sizes,
                _compatCamRelMode,
                v =>
                {
                    _compatCamRelMode = v;
                    ApplyLegacyBehavior();
                },
                new string[] { Localization.Get("ModeDefault"), Localization.Get("ModeAlwaysOff"), Localization.Get("ModeAlwaysOn") },
                "LegacyCamRelativeToMode"));

            elements.Add(VBox(ContainerStyle.Background, null, WithWidthMax(legacy.ToArray())));
            elements.Add(Separator());

            elements.Add(Text(Localization.Get("PatchMode"), TextStyle.Subtitle));
            elements.Add(Separator());

            var patchModeContent = new List<Element>();
            patchModeContent.Add(IridiumPreset.SwitchOption(sizes, patchMode.useILPatch, v =>
            {
                patchMode.useILPatch = v;
                Core.BasePatchMethod.SyncILModeFromSettings();
            }, "UseILPatch"));
            patchModeContent.Add(Separator());

            if (patchMode.useILPatch)
                patchModeContent.Add(IridiumPreset.IconText(sizes, IconStyle.Information, "UseILPatchHint"));
            else
                patchModeContent.Add(IridiumPreset.IconText(sizes, IconStyle.Information, "UsePrefixPostfixHint"));

            elements.Add(VBox(ContainerStyle.Background, null, WithWidthMax(patchModeContent.ToArray())));

            elements.Add(Text(Localization.Get("ThirdPartyMods"), TextStyle.Subtitle));
            elements.Add(Separator());

            var thirdParty = new List<Element>();
            thirdParty.Add(HBox(
                ContainerStyle.None,
                sizes,
                WidthMax,
                Align(
                    0.5,
                    0,
                    IridiumPreset.OptionNameDescription("IgnoreRequiredMods", false),
                    Fill(),
                    Checkbox(compatibility.ignoreRequiredMods, v =>
                    {
                        compatibility.ignoreRequiredMods = v;
                        Patches.PatchManager.UpdatePatchByType(typeof(RequiredModsClearPatches.LevelDataClearPatch));
                        Patches.PatchManager.UpdatePatchByType(typeof(RequiredModsClearPatches.LevelDataCLSClearPatch));
                        Patches.PatchManager.UpdatePatchByType(typeof(RequiredModsClearPatches.EncodeRestorePatch));
                        Patches.PatchManager.UpdatePatchByType(typeof(RequiredModsClearPatches.LevelLoadNotifyPatch));
                        Patches.PatchManager.UpdatePatchByType(typeof(CustomEventsPatches.ScanRegisterPatch));
                        Patches.PatchManager.UpdatePatchByType(typeof(CustomEventsPatches.ScanRegisterCLSPatch));
                        Patches.PatchManager.UpdatePatchByType(typeof(CustomEventsPatches.FakeEventDecodePatch));
                        Patches.PatchManager.UpdatePatchByType(typeof(CustomEventsPatches.FakeEventEncodePatch));
                        Patches.PatchManager.UpdatePatchByType(typeof(CustomEventsPatches.ReadOnlyPanelPatch));
                        Patches.PatchManager.UpdatePatchByType(typeof(CustomEventsPatches.ListItemEventPatch));
                        Patches.PatchManager.UpdatePatchByType(typeof(CustomEventsPatches.EventIndicatorPatch));
                        Patches.PatchManager.UpdatePatchByType(typeof(CustomEventsPatches.ShowPanelFakeEventPatch));
                        Patches.PatchManager.UpdatePatchByType(typeof(CustomEventsPatches.ShowTabsForFloorPatch));
                        Patches.PatchManager.UpdatePatchByType(typeof(CustomEventsPatches.FakeTabSetSelectedPatch));
                        Patches.PatchManager.UpdatePatchByType(typeof(CustomEventsPatches.FakeTabClickPatch));
                        Patches.PatchManager.UpdatePatchByType(typeof(CustomEventsPatches.RemoveEventAtSelectedPatch));
                    }, WidthMin)
                )
            ));
            thirdParty.Add(Separator());

            if (compatibility.ignoreRequiredMods)
                thirdParty.Add(IridiumPreset.IconText(sizes, IconStyle.Warning, "IgnoreRequiredModsWarning"));

            elements.Add(VBox(ContainerStyle.Background, null, WithWidthMax(thirdParty.ToArray())));
            elements.Add(Separator());

            elements.Add(Text(Localization.Get("V3LevelCompatSection"), TextStyle.Subtitle));
            elements.Add(Separator());

            var v3compat = new List<Element>
            {
                IridiumPreset.SwitchOption(sizes, compatibility.enableV3LevelCompat, v =>
                {
                    compatibility.enableV3LevelCompat = v;
                    V3LevelCompatPatches.UpdatePatches();
                    CustomEventsPatches.UpdatePatches();
                }, "EnableV3LevelCompat")
            };
            v3compat.Add(Separator());
            v3compat.Add(IridiumPreset.IconText(sizes, IconStyle.Information, "V3LevelCompatHint"));
            if (compatibility.enableV3LevelCompat)
            {
                v3compat.Add(Separator());
                v3compat.Add(IridiumPreset.IconText(sizes, IconStyle.Warning, "V3LevelCompatWarning"));
            }
            elements.Add(VBox(ContainerStyle.Background, null, WithWidthMax(v3compat.ToArray())));
            return elements.ToArray();
        }

        private void ApplyLegacyBehavior()
        {
            var newFlashMode = (LegacyBehaviorMode)_compatFlashMode;
            var newCamRelMode = (LegacyBehaviorMode)_compatCamRelMode;

            if (newFlashMode != compatibility.legacyFlashMode || newCamRelMode != compatibility.legacyCamRelativeToMode)
            {
                compatibility.legacyFlashMode = newFlashMode;
                compatibility.legacyCamRelativeToMode = newCamRelMode;
                AsyncPatchManager.UpdatePatchByTypeAsync(typeof(JsonPatches.LegacyBehaviorPatch));
            }
        }
        #endregion

        #region HitSound & JudgeText Tab
        private Element[] DrawHitSoundAndJudgeTextTab()
        {
            var sizes = _sizesHolder.Begin();

            var elements = new List<Element>
            {
                Text(Localization.Get("HitSoundSettings"), TextStyle.Title),
                Separator()
            };

            var hitSoundContent = new List<Element>
            {
                IridiumPreset.SwitchOption(sizes, hitSound.enableHitSoundPitch, v =>
                {
                    hitSound.enableHitSoundPitch = v;
                    AsyncPatchManager.UpdatePatchByTypeAsync(typeof(HitSoundPatch));
                }, "EnableHitSoundPitch")
            };
            elements.Add(VBox(ContainerStyle.Background, null, WithWidthMax(hitSoundContent.ToArray())));
            elements.Add(Separator());

            elements.Add(Text(Localization.Get("JudgeTextSettings"), TextStyle.Subtitle));
            elements.Add(Separator());

            var judgeContent = new List<Element>();
            judgeContent.Add(IridiumPreset.SwitchOption(sizes, judgeText.enableJudgeTextCustomization, v =>
            {
                judgeText.enableJudgeTextCustomization = v;
                AsyncPatchManager.UpdatePatchByTypeAsync(typeof(JudgeTextPatches.HitTextMeshInitPatch));
                AsyncPatchManager.UpdatePatchByTypeAsync(typeof(JudgeTextPatches.HitTextMeshShowPatch));
                AsyncPatchManager.UpdatePatchByTypeAsync(typeof(JudgeTextPatches.ResetTimingOnRewindPatch));
            }, "JudgeTextSettings"));
            judgeContent.Add(Separator());
            judgeContent.Add(IridiumPreset.SwitchOption(sizes, judgeText.showAsOffset, v =>
            {
                judgeText.showAsOffset = v;
                AsyncPatchManager.UpdatePatchByTypeAsync(typeof(JudgeTextPatches.HitTextMeshShowPatch));
            }, "ShowAsOffset"));
            judgeContent.Add(Separator());
            judgeContent.Add(Enabled(
                () => !judgeText.showAsOffset && judgeText.enableJudgeTextCustomization,
                Text(Localization.Get("CustomJudgeText"), TextStyle.Normal),
                Separator(),
                DrawJudgeTextInput(sizes, "TooEarly", judgeText.tooEarly, v => judgeText.tooEarly = v),
                Separator(),
                DrawJudgeTextInput(sizes, "VeryEarly", judgeText.veryEarly, v => judgeText.veryEarly = v),
                Separator(),
                DrawJudgeTextInput(sizes, "EarlyPerfect", judgeText.earlyPerfect, v => judgeText.earlyPerfect = v),
                Separator(),
                DrawJudgeTextInput(sizes, "Perfect", judgeText.perfect, v => judgeText.perfect = v),
                Separator(),
                DrawJudgeTextInput(sizes, "LatePerfect", judgeText.latePerfect, v => judgeText.latePerfect = v),
                Separator(),
                DrawJudgeTextInput(sizes, "VeryLate", judgeText.veryLate, v => judgeText.veryLate = v),
                Separator(),
                DrawJudgeTextInput(sizes, "TooLate", judgeText.tooLate, v => judgeText.tooLate = v),
                Separator(),
                DrawJudgeTextInput(sizes, "Multipress", judgeText.multipress, v => judgeText.multipress = v),
                Separator(),
                DrawJudgeTextInput(sizes, "FailMiss", judgeText.failMiss, v => judgeText.failMiss = v),
                Separator(),
                DrawJudgeTextInput(sizes, "FailOverload", judgeText.failOverload, v => judgeText.failOverload = v)
            ));
            judgeContent.Add(Separator());
            judgeContent.Add(HBox(
                ContainerStyle.None,
                sizes,
                WidthMax,
                Fill(),
                Button(Localization.Get("ResetJudgeText"), ButtonStyle.Element, judgeText.ResetToDefault, Width(120)),
                Button(Localization.Get("ConvertJudgeTextToOffset"), ButtonStyle.Element, judgeText.ConvertAllToOffset, Width(180))
            ));

            elements.Add(Enabled(() => judgeText.enableJudgeTextCustomization,
                VBox(ContainerStyle.Background, null, WithWidthMax(judgeContent.ToArray()))
            ));
            return elements.ToArray();
        }

        private Element DrawJudgeTextInput(Sizes sizes, string key, string value, Action<string> onChanged)
        {
            return HBox(
                ContainerStyle.None,
                sizes,
                WidthMax,
                Align(0.5, 0,
                    Text(Localization.Get($"JudgeText_{key}"), TextStyle.Normal, WidthMin),
                    Fill(),
                    TextField(value, onChanged, 128, Width(120))
                )
            );
        }
        #endregion

        #region Helpers
        private Element InvertedSwitchOption(Sizes sizes, bool invertedOption, Action<bool> onChanged, string name)
        {
            return HBox(
                ContainerStyle.None,
                sizes,
                WidthMax,
                Align(0.5, 0,
                    Text(Localization.Get(name), TextStyle.Normal, WidthMin),
                    Fill(),
                    Switch(!invertedOption, v => onChanged(!v), WidthMin)
                )
            );
        }
        #endregion

        #region Editor Shortcuts Tab
        private bool _isBindingShortcutKey = false;
        private int _bindingShortcutIndex = -1;
        private int _bindKeyStartFrame = -1;
        private Action<int>? _bindingKeySetter;

        private static readonly string[] _shortcutSettingNames = new[]
        {
            "ShortcutSelectAll",
            "ShortcutDeselectAll",
            "ShortcutToggleVisibility",
            "ShortcutFocusDecoration",
            "ShortcutPopupSave",
            "ShortcutPopupDiscard"
        };

        private Element[] DrawEditorShortcutsTab()
        {
            var sizes = _sizesHolder.Begin();
            var s = editorShortcuts;

            var elements = new List<Element>
            {
                Text(Localization.Get("EditorShortcuts"), TextStyle.Title),
                Separator(),
                IridiumPreset.SwitchOption(sizes, s.enableEditorShortcuts, v =>
                {
                    s.enableEditorShortcuts = v;
                    AsyncPatchManager.UpdatePatchByTypeAsync(typeof(EditorShortcutPatches.EditorShortcutUpdatePatch));
                }, "EnableEditorShortcuts")
            };

            if (s.enableEditorShortcuts)
            {
                int idx = 0;

                elements.Add(Separator());
                elements.Add(Text(Localization.Get("EditorShortcutsDecoration"), TextStyle.Subtitle));
                elements.Add(Separator());
                elements.Add(VBox(
                    ContainerStyle.Background,
                    null,
                    WithWidthMax(
                        DrawShortcutKeyBinding(sizes, idx++, "ShortcutSelectAll",
                            s.selectAllKey, v => s.selectAllKey = v,
                            s.selectAllModifiers, v => s.selectAllModifiers = v),
                        Separator(),
                        DrawShortcutKeyBinding(sizes, idx++, "ShortcutDeselectAll",
                            s.deselectAllKey, v => s.deselectAllKey = v,
                            s.deselectAllModifiers, v => s.deselectAllModifiers = v),
                        Separator(),
                        DrawShortcutKeyBinding(sizes, idx++, "ShortcutToggleVisibility",
                            s.toggleVisibilityKey, v => s.toggleVisibilityKey = v,
                            s.toggleVisibilityModifiers, v => s.toggleVisibilityModifiers = v),
                        Separator(),
                        DrawShortcutKeyBinding(sizes, idx++, "ShortcutFocusDecoration",
                            s.focusDecorationKey, v => s.focusDecorationKey = v,
                            s.focusDecorationModifiers, v => s.focusDecorationModifiers = v)
                    )
                ));

                elements.Add(Separator());
                elements.Add(Text(Localization.Get("EditorShortcutsNavigation"), TextStyle.Subtitle));
                elements.Add(Separator());
                elements.Add(VBox(
                    ContainerStyle.Background,
                    null,
                    WithWidthMax(
                        DrawShortcutKeyBinding(sizes, idx++, "ShortcutGoToFloor",
                            s.goToFloorKey, v => s.goToFloorKey = v,
                            s.goToFloorModifiers, v => s.goToFloorModifiers = v),
                        Separator(),
                        IridiumPreset.SwitchOption(sizes, s.cameraFollowOnFloorSelect, v => s.cameraFollowOnFloorSelect = v, "CameraFollowOnFloorSelect"),
                        Separator(),
                        DrawShortcutKeyBinding(sizes, idx++, "ShortcutSelectAllFloors",
                            s.selectAllFloorsKey, v => s.selectAllFloorsKey = v,
                            s.selectAllFloorsModifiers, v => s.selectAllFloorsModifiers = v)
                    )
                ));

                elements.Add(Separator());
                elements.Add(Text(Localization.Get("EditorShortcutsPopup"), TextStyle.Subtitle));
                elements.Add(Separator());
                elements.Add(VBox(
                    ContainerStyle.Background,
                    null,
                    WithWidthMax(
                        DrawShortcutKeyBinding(sizes, idx++, "ShortcutPopupSave",
                            s.popupSaveKey, v => s.popupSaveKey = v,
                            s.popupSaveModifiers, v => s.popupSaveModifiers = v),
                        Separator(),
                        DrawShortcutKeyBinding(sizes, idx++, "ShortcutPopupDiscard",
                            s.popupDiscardKey, v => s.popupDiscardKey = v,
                            s.popupDiscardModifiers, v => s.popupDiscardModifiers = v)
                    )
                ));

                elements.Add(Separator());
                elements.Add(IridiumPreset.IconText(sizes, IconStyle.Information, "EditorShortcutsHint"));
            }

            return elements.ToArray();
        }

        private Element DrawShortcutKeyBinding(
            Sizes sizes,
            int index,
            string name,
            int key,
            Action<int> onKeyChanged,
            int modifiers,
            Action<int> onModifiersChanged
        )
        {
            string display = (_isBindingShortcutKey && _bindingShortcutIndex == index)
                ? Localization.Get("EditorPauseKeyPress")
                : EditorShortcutPatches.GetKeyDisplay(key, modifiers);

            return HBox(
                ContainerStyle.None,
                sizes,
                WidthMax,
                Align(0.5, 0,
                    Text(Localization.Get(name), TextStyle.Normal, WidthMin),
                    Fill(),
                    Button(display, ButtonStyle.Element, () =>
                    {
                        _isBindingShortcutKey = true;
                        _bindingShortcutIndex = index;
                        _bindingKeySetter = onKeyChanged;
                        _bindKeyStartFrame = Time.frameCount;
                    }, Width(160)),
                    Button(GetModifierLabel(modifiers), ButtonStyle.Element, () =>
                    {
                        onModifiersChanged(EditorShortcutPatches.CycleModifier(modifiers));
                    }, Width(60))
                )
            );
        }

        private void HandleShortcutKeyCapture()
        {
            if (!_isBindingShortcutKey || _bindingKeySetter == null) return;

            var e = Event.current;
            if (e.type == EventType.KeyDown)
            {
                if (e.keyCode != KeyCode.None && e.keyCode != KeyCode.Escape)
                {
                    _bindingKeySetter((int)e.keyCode);
                    _isBindingShortcutKey = false;
                    _bindingShortcutIndex = -1;
                    _bindingKeySetter = null;
                    e.Use();
                }
                else if (e.keyCode == KeyCode.Escape)
                {
                    _isBindingShortcutKey = false;
                    _bindingShortcutIndex = -1;
                    _bindingKeySetter = null;
                    e.Use();
                }
            }
            else if (e.type == EventType.MouseDown && Time.frameCount != _bindKeyStartFrame)
            {
                _isBindingShortcutKey = false;
                _bindingShortcutIndex = -1;
                _bindingKeySetter = null;
            }
        }

        private static string GetModifierLabel(int mods)
        {
            if (mods == 0) return "---";
            string result = "";
            if ((mods & EditorShortcutPatches.MOD_CTRL) != 0) result += "C";
            if ((mods & EditorShortcutPatches.MOD_ALT) != 0) result += "A";
            if ((mods & EditorShortcutPatches.MOD_SHIFT) != 0) result += "S";
            return result;
        }

        #region AsyncInput Tab
        private Element[] DrawAsyncInputTab()
        {
            var sizes = _sizesHolder.Begin();

            var elements = new List<Element>
            {
                Text(Localization.Get("AsyncInputSettings"), TextStyle.Title),
                Separator(),
                IridiumPreset.SwitchOption(sizes, asyncInput.enableAIO, v =>
                {
                    asyncInput.enableAIO = v;
                    if (asyncInput.enableAIO)
                        Modules.AsyncInputOptimize.Main.Enable();
                    else
                        Modules.AsyncInputOptimize.Main.Disable();
                    AsyncPatchManager.UpdatePatchByTypeAsync(typeof(Modules.AsyncInputOptimize.Patch.UnityEngine__SceneManagement__SceneManager));
                    AsyncPatchManager.UpdatePatchByTypeAsync(typeof(Modules.AsyncInputOptimize.Patch.__scnGame));
                    AsyncPatchManager.UpdatePatchByTypeAsync(typeof(Modules.AsyncInputOptimize.Patch.__scrConductor));
                    AsyncPatchManager.UpdatePatchByTypeAsync(typeof(Modules.AsyncInputOptimize.Patch.__scrCountdown));
                }, "EnableAsyncInput"),
                Separator(),
                Text(Localization.Get("AsyncInputHint"), TextStyle.Secondary),
                Text(Localization.Get("AsyncInputWarning"), TextStyle.Secondary)
            };

            return elements.ToArray();
        }
        #endregion

        #endregion

        public void Save()
        {
            Main.Handler?.SaveSettings(this);
        }

        /// <summary>
        /// 启动时检测自定义缓速引擎与三个 Patch 的冲突。
        /// 如果引擎和任一 Patch 同时开启，强制关闭引擎并保存配置。
        /// </summary>
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
}
