using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Iridium.Config;
using Iridium.Core;

namespace Iridium.Patches
{
    public static class PatchManager
    {
        private static Harmony _harmony => Main.Harmony!;

        private static readonly Dictionary<Type, bool> _activePatches = new();
        // Cache exact patch bindings per class to isolate unpatching
        private static readonly Dictionary<Type, List<(MethodBase Original, MethodInfo PatchMethod)>> _patchedBindings = new();

        private static readonly List<BasePatchMethod> _methodPatches = new();

        private class PatchDef
        {
            public Type Type;
            public Func<bool> Condition;
            public Type? Parent;
            public string Name;

            public PatchDef(Type type, Func<bool> condition, Type? parent = null)
            {
                Type = type;
                Condition = condition;
                Parent = parent;
                Name = type.Name;
            }
        }

        private static readonly List<PatchDef> _definitions = new();

        static PatchManager()
        {
            RegisterPatches();
        }

        /// <summary>
        /// 注册一个实例化补丁（BasePatchMethod 子类）
        /// </summary>
        public static void RegisterMethodPatch(BasePatchMethod patch)
        {
            lock (_methodPatches)
            {
                if (!_methodPatches.Contains(patch))
                    _methodPatches.Add(patch);
            }
        }

        /// <summary>
        /// 取消注册实例化补丁
        /// </summary>
        public static void UnregisterMethodPatch(BasePatchMethod patch)
        {
            lock (_methodPatches)
            {
                _methodPatches.Remove(patch);
            }
        }

        /// <summary>
        /// 注册一个包含 HarmonyPatch 嵌套类型的补丁类中的所有嵌套补丁
        /// </summary>
        private static void RegisterNestedPatches(Type parentType, Func<bool> condition, HashSet<Type>? exclude = null)
        {
            foreach (var type in parentType.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
            {
                if (type.GetCustomAttributes(typeof(HarmonyPatch), true).Length > 0)
                {
                    if (exclude != null && exclude.Contains(type))
                        continue;
                    _definitions.Add(new PatchDef(type, condition));
                }
            }
        }

        private static void RegisterPatches()
        {
            _definitions.Clear();

            var optCond = () => Main.Settings.optimizer.enableOptimizer;
            RegisterNestedPatches(typeof(OptimizerPatches), optCond);
            RegisterNestedPatches(typeof(TrackOptimizationPatches), optCond);

            // Render scale (framebuffer downscale)
            var renderScaleCond = () => Main.Settings.optimizer.enableOptimizer && Main.Settings.optimizer.enableRenderScale;
            RegisterNestedPatches(typeof(RenderScalePatches), renderScaleCond);

            RegisterNestedPatches(typeof(FfxOptimizationPatches), optCond);

            RegisterNestedPatches(typeof(DecorationFilterCachePatches), optCond);

            _definitions.Add(new PatchDef(
                typeof(GameplayAllocationOptimizationPatches.PlanetOverlapAllocPatch),
                () => Main.Settings.optimizer.enableOptimizer && Main.Settings.optimizer.optimizeGameplayAllocations));
            _definitions.Add(new PatchDef(
                typeof(GameplayAllocationOptimizationPatches.DecorationFilterInvokePatch),
                () => Main.Settings.optimizer.enableOptimizer && Main.Settings.optimizer.optimizeGameplayAllocations));
            _definitions.Add(new PatchDef(
                typeof(RDInputOptimizationPatches.GetStateKeysPatch),
                () => Main.Settings.optimizer.enableOptimizer && Main.Settings.optimizer.optimizeRDInputAllocations));
            _definitions.Add(new PatchDef(
                typeof(PlayerInputOptimizationPatches.SimulatedPlayerControlUpdatePatch),
                () => Main.Settings.optimizer.enableOptimizer && Main.Settings.optimizer.optimizePlayerInputAllocations));

            RegisterNestedPatches(typeof(SceneOptimizationPatches), optCond);

            // FrameSpreadDecorationLoadingPatch 有独立子开关，需排除后单独注册
            RegisterNestedPatches(typeof(LoadingOptimizationPatches), optCond,
                new HashSet<Type> { typeof(LoadingOptimizationPatches.FrameSpreadDecorationLoadingPatch) });
            _definitions.Add(new PatchDef(typeof(LoadingOptimizationPatches.FrameSpreadDecorationLoadingPatch),
                () => Main.Settings.optimizer.enableOptimizer && Main.Settings.optimizer.frameSpreadDecorationLoading));
            _definitions.Add(new PatchDef(typeof(LoadingOptimizationPatches.FrameSpreadDecorationLoadingPatch.ResetDecorations_Patch),
                () => Main.Settings.optimizer.enableOptimizer && Main.Settings.optimizer.frameSpreadDecorationLoading));
            _definitions.Add(new PatchDef(typeof(LoadingOptimizationPatches.FrameSpreadDecorationLoadingPatch.Play_Patch),
                () => Main.Settings.optimizer.enableOptimizer && Main.Settings.optimizer.frameSpreadDecorationLoading));

            // DOTween optimization uses no HarmonyPatch, only runtime config

            RegisterNestedPatches(typeof(ExtremeOptimizationPatches),
                () => Main.Settings.optimizer.enableOptimizer && Main.Settings.optimizer.enableExtremeOptimization);

            var tweenSafetyCond = () => Main.Settings.optimizer.enableOptimizer && Main.Settings.optimizer.dotweenDefaultRecyclable;
            RegisterNestedPatches(typeof(TweenSafetyPatches), tweenSafetyCond);

            var eventTweenCond = () => Main.Settings.optimizer.optimizeEventProcessing;
            _definitions.Add(new PatchDef(typeof(EventTweenOptimizationPatches.FfxMoveFloorPlusEventTweensPatch), eventTweenCond));
            _definitions.Add(new PatchDef(typeof(EventTweenOptimizationPatches.FfxMoveDecorationsPlusEventTweensPatch), eventTweenCond));
            _definitions.Add(new PatchDef(typeof(EventTweenOptimizationPatches.FfxRecolorFloorPlusEventTweensPatch), eventTweenCond));
            _definitions.Add(new PatchDef(typeof(EventTweenOptimizationPatches.FfxPlusBaseKillCacheInvalidationPatch), eventTweenCond));

            var jsonOptCond = () => Main.Settings.optimizer.customLevelReadOptimization;
            _definitions.Add(new PatchDef(typeof(JsonPatches.PatchLevelDataCLSLoadLevel), jsonOptCond));

            // Editor floor optimization, each patch with its own sub-condition
            var editorMaster = () => Main.Settings.optimizer.enableEditorFloorOptimization;
            _definitions.Add(new PatchDef(typeof(EditorFloorOptimizationPatches.InsertCharFloorOptimizationPatch),
                () => editorMaster() && Main.Settings.optimizer.incrementalFloorInsert));
            _definitions.Add(new PatchDef(typeof(EditorFloorOptimizationPatches.InsertFloatFloorOptimizationPatch),
                () => editorMaster() && Main.Settings.optimizer.incrementalFloorInsert));
            _definitions.Add(new PatchDef(typeof(EditorFloorOptimizationPatches.InstantiateFloatFloorsOptimizationPatch),
                () => editorMaster() && Main.Settings.optimizer.incrementalFloorInsert));
            _definitions.Add(new PatchDef(typeof(EditorFloorOptimizationPatches.DeleteFloorOptimizationPatch),
                () => editorMaster() && Main.Settings.optimizer.incrementalFloorInsert));
            _definitions.Add(new PatchDef(typeof(EditorFloorOptimizationPatches.RemakePathRedundancyPatch),
                () => editorMaster() && Main.Settings.optimizer.incrementalFloorInsert && Main.Settings.optimizer.skipRedundantRemakePath));
            _definitions.Add(new PatchDef(typeof(EditorFloorOptimizationPatches.GameRemakePathOptimizationPatch),
                () => editorMaster() && Main.Settings.optimizer.incrementalFloorInsert && Main.Settings.optimizer.skipRedundantRemakePath));
            _definitions.Add(new PatchDef(typeof(EditorFloorOptimizationPatches.DrawFloorNumsOptimizationPatch),
                () => editorMaster() && Main.Settings.optimizer.incrementalFloorInsert && Main.Settings.optimizer.rangeBasedRedraw));
            _definitions.Add(new PatchDef(typeof(EditorFloorOptimizationPatches.DrawFloorOffsetLinesOptimizationPatch),
                () => editorMaster() && Main.Settings.optimizer.incrementalFloorInsert && Main.Settings.optimizer.skipRedundantRemakePath));
            _definitions.Add(new PatchDef(typeof(EditorFloorOptimizationPatches.OffsetFloorIDsOptimizationPatch),
                () => editorMaster() && Main.Settings.optimizer.incrementalFloorInsert && Main.Settings.optimizer.optimizeOffsetFloorEvents));
            _definitions.Add(new PatchDef(typeof(EditorFloorOptimizationPatches.SkipApplyEventsOnInsertPatch),
                () => editorMaster() && Main.Settings.optimizer.incrementalFloorInsert && Main.Settings.optimizer.skipApplyEventsOnInsert));
            _definitions.Add(new PatchDef(typeof(EditorFloorOptimizationPatches.EditorDrawHoldsDuplicateSkipPatch),
                () => editorMaster() && Main.Settings.optimizer.incrementalFloorInsert));
            _definitions.Add(new PatchDef(typeof(EditorFloorOptimizationPatches.EditorDrawMultiPlanetDuplicateSkipPatch),
                () => editorMaster() && Main.Settings.optimizer.incrementalFloorInsert));
            _definitions.Add(new PatchDef(typeof(EditorFloorOptimizationPatches.LevelMakerDrawHoldsNoHoldsSkipPatch),
                () => editorMaster() && Main.Settings.optimizer.incrementalFloorInsert));

            // Event index (selecting/switching tiles is slow)
            _definitions.Add(new PatchDef(typeof(EditorEventIndexPatch.GetFloorEventsIndexPatch), editorMaster));
            _definitions.Add(new PatchDef(typeof(EditorEventIndexPatch.ShowEventIndicatorsIndexPatch), editorMaster));
            _definitions.Add(new PatchDef(typeof(EditorEventIndexPatch.DestroyEventIndicatorsFastPatch), editorMaster));
            _definitions.Add(new PatchDef(typeof(EditorEventIndexPatch.EditorApplyDirtyPatch), editorMaster));
            _definitions.Add(new PatchDef(typeof(EditorEventIndexPatch.GameApplyDirtyPatch), editorMaster));
            _definitions.Add(new PatchDef(typeof(EditorEventIndexPatch.LevelDecodeDirtyPatch), editorMaster));
            _definitions.Add(new PatchDef(typeof(EditorEventIndexPatch.OffsetFloorIdsDirtyPatch), editorMaster));

            // Gated on the large-level loading switch only, not the optimizer master
            var largeLoadCond = () => Main.Settings.optimizer.optimizeLargeLevelLoading;
            _definitions.Add(new PatchDef(typeof(FfxGetComponentsFastPatch.ApplyEventsFfxScanPatch), largeLoadCond));
            _definitions.Add(new PatchDef(typeof(LoadProfilerPatch.LoadLevelProfiler), largeLoadCond));
            _definitions.Add(new PatchDef(typeof(LoadProfilerPatch.DecodeProfiler), largeLoadCond));
            _definitions.Add(new PatchDef(typeof(LoadProfilerPatch.JsonParseProfiler), largeLoadCond));
            _definitions.Add(new PatchDef(typeof(LoadProfilerPatch.FloatFloorsProfiler), largeLoadCond));

            // Native JSON parsing (Rust, batched)
            _definitions.Add(new PatchDef(typeof(JsonNativeParsePatch.DeserializePatch), largeLoadCond));
            _definitions.Add(new PatchDef(typeof(JsonNativeParsePatch.DeserializePartiallyPatch), largeLoadCond));

            var editorInteractionsCond = () => Main.Settings.optimizer.enableOptimizer && Main.Settings.optimizer.optimizeEditorInteractions;
            _definitions.Add(new PatchDef(typeof(EditorSaveStateCopyPatch.SaveStateSuppressPatch), editorInteractionsCond));
            _definitions.Add(new PatchDef(typeof(EditorSaveStateCopyPatch.LevelDataCopySuppressPatch), editorInteractionsCond));
            _definitions.Add(new PatchDef(typeof(DecorationLookupIndexPatch.GetDecorationPatch), editorInteractionsCond));
            _definitions.Add(new PatchDef(typeof(DecorationLookupIndexPatch.GetDecorationIndexPatch), editorInteractionsCond));
            _definitions.Add(new PatchDef(typeof(EditorSearchOptimizationPatch.ShowTabsForFloorPatch), editorInteractionsCond));
            _definitions.Add(new PatchDef(typeof(EditorSearchOptimizationPatch.ShowPanelOfEventPatch), editorInteractionsCond));
            _definitions.Add(new PatchDef(typeof(EditorSearchOptimizationPatch.SearchByCommentPatch), editorInteractionsCond));

            var deathResetCond = () => Main.Settings.optimizer.enableOptimizer && Main.Settings.optimizer.optimizeDeathReset;
            _definitions.Add(new PatchDef(typeof(DeathResetFloorPatch), deathResetCond));
            _definitions.Add(new PatchDef(typeof(HoldReusePatch), deathResetCond));

            var decorResetCond = () => Main.Settings.optimizer.enableOptimizer && Main.Settings.optimizer.optimizeDecorationReset;
            _definitions.Add(new PatchDef(typeof(DecorationResetPatch), decorResetCond));

            var holdRendererCond = () => Main.Settings.optimizer.enableOptimizer && Main.Settings.optimizer.optimizeHoldRenderer;
            _definitions.Add(new PatchDef(typeof(HoldRendererDirtyCheckPatch), holdRendererCond));
            _definitions.Add(new PatchDef(typeof(LoadProfilerPatch.ApplyEventsProfiler), largeLoadCond));

            // Lazy top glow
            _definitions.Add(new PatchDef(typeof(LazyTopGlowPatch.AwakePatch), largeLoadCond));
            _definitions.Add(new PatchDef(typeof(LazyTopGlowPatch.ResetPatch), largeLoadCond));
            _definitions.Add(new PatchDef(typeof(LazyTopGlowPatch.StartPatch), largeLoadCond));
            _definitions.Add(new PatchDef(typeof(LazyTopGlowPatch.LightUpPatch), largeLoadCond));
            _definitions.Add(new PatchDef(typeof(LazyTopGlowPatch.RandomColorPatch), largeLoadCond));
            _definitions.Add(new PatchDef(typeof(LazyTopGlowPatch.FinishLoadingPatch), largeLoadCond));
            _definitions.Add(new PatchDef(typeof(LazyTopGlowPatch.ObjectDecorationGlowPatch), largeLoadCond));

            // Floor number text generated on demand
            _definitions.Add(new PatchDef(typeof(LazyFloorNumberPatch.StrippedPrefabPatch), largeLoadCond));
            _definitions.Add(new PatchDef(typeof(LazyFloorNumberPatch.OnBecameVisiblePatch), largeLoadCond));
            _definitions.Add(new PatchDef(typeof(LazyFloorNumberPatch.DrawFloorNumsPatch), largeLoadCond));
            _definitions.Add(new PatchDef(typeof(LazyFloorNumberPatch.PlayPatch), largeLoadCond));

            // Decode/event grouping allocation cuts
            _definitions.Add(new PatchDef(typeof(DecodeAllocationPatch.DecodeDictionaryCapacityPatch), largeLoadCond));
            _definitions.Add(new PatchDef(typeof(DecodeAllocationPatch.EventListCapacityPatch), largeLoadCond));

            // Chunked floor spawn (experimental)
            var chunkedSpawnCond = () => Main.Settings.optimizer.optimizeLargeLevelLoading
                && Main.Settings.optimizer.chunkedFloorSpawn;
            _definitions.Add(new PatchDef(typeof(ChunkedFloorSpawnPatch.InstantiateFloorsChunkedPatch), chunkedSpawnCond));
            _definitions.Add(new PatchDef(typeof(ChunkedFloorSpawnPatch.CheckpointDecodePatch), chunkedSpawnCond));
            _definitions.Add(new PatchDef(typeof(ChunkedFloorSpawnPatch.SetHitsoundDecodePatch), chunkedSpawnCond));
            _definitions.Add(new PatchDef(typeof(ChunkedFloorSpawnPatch.EditorPlayFlushPatch), chunkedSpawnCond));
            _definitions.Add(new PatchDef(typeof(ChunkedFloorSpawnPatch.FinishLoadingFlushPatch), chunkedSpawnCond));

            // Undo history depth cap (memory guard)
            _definitions.Add(new PatchDef(typeof(UndoMemoryCapPatch.SaveStateCapPatch), largeLoadCond));

            _definitions.Add(new PatchDef(typeof(MiscPatches.RemoveNewsPatch), () => Main.Settings.ui.removeNews));
            _definitions.Add(new PatchDef(typeof(MiscPatches.HideBetaWatermarkPatch), () => Main.Settings.ui.hideBetaWatermark));
            _definitions.Add(new PatchDef(typeof(MiscPatches.ForceDifficultyUIPatch), () => Main.Settings.ui.forceDifficultyUI));
            _definitions.Add(new PatchDef(typeof(MiscPatches.CircleArcPatch), () => Main.Settings.ui.enableCircleArc));
            _definitions.Add(new PatchDef(typeof(MiscPatches.AllAngleArcCornersPatch), () => Main.Settings.ui.enableCircleArc));
            _definitions.Add(new PatchDef(typeof(MiscPatches.AutoplayTextPositionPatch), () => Main.Settings.ui.moveAutoplayText));
            _definitions.Add(new PatchDef(typeof(MiscPatches.AlwaysCountdownPatch), () => Main.Settings.ui.alwaysCountdown));
            _definitions.Add(new PatchDef(typeof(MiscPatches.PausePlanetTrailPatch), () => Main.Settings.ui.enablePausePlanetTrail));

            _definitions.Add(new PatchDef(typeof(MiscPatches.LobbyMusicPatch), () => Main.Settings.lobbyMusic.enableLobbyMusicPatch));

            var memCond = () => Main.Settings.memory.enableMemoryOptimization;
            _definitions.Add(new PatchDef(typeof(MiscPatches.SmartGCPatch), () => memCond() && Main.Settings.memory.enableSmartGC));

            var pauseFixCond = () => Main.Settings.compatibility.enableLegacyPauseFix;
            _definitions.Add(new PatchDef(typeof(CompatibilityPatches.LegacyPauseFixPatch_Play), pauseFixCond));
_definitions.Add(new PatchDef(typeof(CompatibilityPatches.LegacyPauseFixPatch_Apply), pauseFixCond));
            _definitions.Add(new PatchDef(typeof(CompatibilityPatches.NoFailTooEarlyPatch), () => Main.Settings.compatibility.enableNoFailTooEarly));

            // v3 chart compat (load/play/edit polyfill for level versions 16~19)
            var v3LevelCompatCond = () => Main.Settings.compatibility.enableV3LevelCompat;
            RegisterNestedPatches(typeof(V3LevelCompatPatches), v3LevelCompatCond);
            _definitions.Add(new PatchDef(typeof(CompatibilityPatches.ScaleFilterSpeedWithPitchPatch), () => Main.Settings.compatibility.scaleFilterSpeedWithPitch));
            _definitions.Add(new PatchDef(typeof(CameraRelativeDragPatches), () => Main.Settings.compatibility.fixCameraRelativeDrag));
            _definitions.Add(new PatchDef(typeof(JsonPatches.ForceAngleDataPatch), () => Main.Settings.compatibility.forceAngleData));
            _definitions.Add(new PatchDef(typeof(JsonPatches.LegacyBehaviorPatch), () =>
                Main.Settings.compatibility.legacyFlashMode != LegacyBehaviorMode.Default ||
                Main.Settings.compatibility.legacyCamRelativeToMode != LegacyBehaviorMode.Default));

            // requiredMods clearing, mounted on demand via ignoreRequiredMods
            var requiredModsCond = () => Main.Settings.compatibility.ignoreRequiredMods;
            _definitions.Add(new PatchDef(typeof(RequiredModsClearPatches.LevelDataClearPatch), requiredModsCond));
            _definitions.Add(new PatchDef(typeof(RequiredModsClearPatches.LevelDataCLSClearPatch), requiredModsCond));
            _definitions.Add(new PatchDef(typeof(RequiredModsClearPatches.EncodeRestorePatch), requiredModsCond));
            _definitions.Add(new PatchDef(typeof(RequiredModsClearPatches.LevelLoadNotifyPatch), requiredModsCond));

            // Third-party custom events (fake event registration, read-only panels);
            // also covers unknown event types in v3 charts
            var customEventsCond = () => Main.Settings.compatibility.ignoreRequiredMods
                                         || Main.Settings.compatibility.enableV3LevelCompat;
_definitions.Add(new PatchDef(typeof(CustomEventsPatches.ScanRegisterPatch), customEventsCond));
            _definitions.Add(new PatchDef(typeof(CustomEventsPatches.ScanRegisterCLSPatch), customEventsCond));
            _definitions.Add(new PatchDef(typeof(CustomEventsPatches.FakeEventDecodePatch), customEventsCond));
            _definitions.Add(new PatchDef(typeof(CustomEventsPatches.FakeEventEncodePatch), customEventsCond));
            _definitions.Add(new PatchDef(typeof(CustomEventsPatches.ReadOnlyPanelPatch), customEventsCond));
            _definitions.Add(new PatchDef(typeof(CustomEventsPatches.ListItemEventPatch), customEventsCond));
            _definitions.Add(new PatchDef(typeof(CustomEventsPatches.EventIndicatorPatch), customEventsCond));
            _definitions.Add(new PatchDef(typeof(CustomEventsPatches.ShowPanelFakeEventPatch), customEventsCond));
            _definitions.Add(new PatchDef(typeof(CustomEventsPatches.RemoveEventAtSelectedPatch), customEventsCond));
            _definitions.Add(new PatchDef(typeof(CustomEventsPatches.ShowTabsForFloorPatch), customEventsCond));
            _definitions.Add(new PatchDef(typeof(CustomEventsPatches.FakeTabSetSelectedPatch), customEventsCond));
            _definitions.Add(new PatchDef(typeof(CustomEventsPatches.FakeTabClickPatch), customEventsCond));

            _definitions.Add(new PatchDef(typeof(HitSoundPatch), () => Main.Settings.hitSound.enableHitSoundPitch));

            _definitions.Add(new PatchDef(typeof(JudgeTextPatches.HitTextMeshInitPatch), () => Main.Settings.judgeText.enableJudgeTextCustomization));
            _definitions.Add(new PatchDef(typeof(JudgeTextPatches.HitTextMeshShowPatch), () => Main.Settings.judgeText.enableJudgeTextCustomization && Main.Settings.judgeText.showAsOffset));
            _definitions.Add(new PatchDef(typeof(JudgeTextPatches.ResetTimingOnRewindPatch), () => Main.Settings.judgeText.enableJudgeTextCustomization));

            _definitions.Add(new PatchDef(typeof(EditorShortcutPatches.EditorShortcutUpdatePatch), () => Main.Settings.editorShortcuts.enableEditorShortcuts));
            _definitions.Add(new PatchDef(typeof(EditorShortcutPatches.FloorSelectCameraJumpPatch), () => Main.Settings.editorShortcuts.enableEditorShortcuts));

            var easingCond = () => Main.Settings.optimizer.enableCustomEasingEngine;
            RegisterNestedPatches(typeof(CustomEasingPatches), easingCond);

            var aioCond = () => Main.Settings.asyncInput.enableAIO;
            _definitions.Add(new PatchDef(typeof(Modules.AsyncInputOptimize.Patch.UnityEngine__SceneManagement__SceneManager), aioCond));
            _definitions.Add(new PatchDef(typeof(Modules.AsyncInputOptimize.Patch.__scnGame), aioCond));
            _definitions.Add(new PatchDef(typeof(Modules.AsyncInputOptimize.Patch.__scrConductor), aioCond));
            _definitions.Add(new PatchDef(typeof(Modules.AsyncInputOptimize.Patch.__scrCountdown), aioCond));
        }

        /// <summary>
        /// 更新所有patch（仅用于初始化或全量更新）
        /// </summary>
        public static void UpdateAllPatches()
        {
            if (_harmony == null) return;

            foreach (var def in _definitions)
            {
                UpdateSinglePatch(def);
            }

            BasePatchMethod.SyncILModeFromSettings();
        }

        /// <summary>
        /// 按类型更新单个patch - 用于增量更新
        /// </summary>
        public static void UpdatePatchByType(Type patchType)
        {
            if (_harmony == null) return;

            var def = _definitions.Find(d => d.Type == patchType);
            if (def != null)
            {
                UpdateSinglePatch(def);
            }
        }

        /// <summary>
        /// 更新所有优化器相关的patch（当 enableOptimizer 改变时调用）
        /// </summary>
        public static void UpdateOptimizerPatches()
        {
            if (_harmony == null) return;

            var optimizerParentTypes = new HashSet<Type>
            {
                typeof(OptimizerPatches),
                typeof(RenderScalePatches),
                typeof(TrackOptimizationPatches),
                typeof(SceneOptimizationPatches),
                typeof(LoadingOptimizationPatches),
                typeof(ExtremeOptimizationPatches),
                typeof(EditorFloorOptimizationPatches),
                typeof(EditorEventIndexPatch),
                typeof(FfxGetComponentsFastPatch),
                typeof(LoadProfilerPatch),
                typeof(LazyTopGlowPatch),
                typeof(LazyFloorNumberPatch),
                typeof(DecodeAllocationPatch),
                typeof(ChunkedFloorSpawnPatch),
                typeof(UndoMemoryCapPatch)
            };

            foreach (var def in _definitions)
            {
                bool isOptimizerPatch = optimizerParentTypes.Contains(def.Type) ||
                    (def.Type.DeclaringType != null && optimizerParentTypes.Contains(def.Type.DeclaringType));

                if (isOptimizerPatch)
                {
                    UpdateSinglePatch(def);
                }
            }
        }

        /// <summary>
        /// 更新满足条件的patch - 用于批量增量更新
        /// </summary>
        public static void UpdatePatchesByCondition(Func<Type, bool> predicate)
        {
            if (_harmony == null) return;

            foreach (var def in _definitions)
            {
                if (predicate(def.Type))
                {
                    UpdateSinglePatch(def);
                }
            }
        }

        /// <summary>
        /// 更新单个patch定义
        /// </summary>
        private static void UpdateSinglePatch(PatchDef def)
        {
            bool shouldBeActive = CalculateEffectiveStatus(def);
            bool trackedActive = _activePatches.TryGetValue(def.Type, out bool currentActive) && currentActive;

            if (trackedActive != shouldBeActive)
            {
                Main.Logger?.Log(Localization.Get("PatchManagerStatusChanged", def.Name, trackedActive.ToString(), shouldBeActive.ToString()));
                if (shouldBeActive) ApplyPatch(def.Type);
                else RemovePatch(def.Type);

                _activePatches[def.Type] = shouldBeActive;
            }
        }

        private static bool CalculateEffectiveStatus(PatchDef def)
        {
            if (!def.Condition()) return false;

            if (def.Parent != null)
            {
                _activePatches.TryGetValue(def.Parent, out bool parentActive);
                if (!parentActive) return false;
            }

            return true;
        }

        private static void ApplyPatch(Type type)
        {
            try
            {
                Main.Logger?.Log(Localization.Get("PatchManagerAttemptApply", type.Name));
                var processor = _harmony.CreateClassProcessor(type);
                var originals = processor.Patch();

                if (originals != null && originals.Count > 0)
                {
                    var bindings = new List<(MethodBase Original, MethodInfo PatchMethod)>();
                    foreach (var original in originals)
                    {
                        var info = Harmony.GetPatchInfo(original);
                        if (info == null) continue;

                        foreach (var p in info.Prefixes)
                        {
                            if (p.owner == _harmony.Id && p.PatchMethod.DeclaringType == type)
                                bindings.Add((original, p.PatchMethod));
                        }
                        foreach (var p in info.Postfixes)
                        {
                            if (p.owner == _harmony.Id && p.PatchMethod.DeclaringType == type)
                                bindings.Add((original, p.PatchMethod));
                        }
                        foreach (var p in info.Transpilers)
                        {
                            if (p.owner == _harmony.Id && p.PatchMethod.DeclaringType == type)
                                bindings.Add((original, p.PatchMethod));
                        }
                        foreach (var p in info.Finalizers)
                        {
                            if (p.owner == _harmony.Id && p.PatchMethod.DeclaringType == type)
                                bindings.Add((original, p.PatchMethod));
                        }
                    }

                    _patchedBindings[type] = bindings;
                    _activePatches[type] = true;
                    Main.Logger?.Log(Localization.Get("PatchManagerSuccessApply", type.Name, bindings.Count.ToString()));
                }
                else
                {
                    Main.Logger?.Log(Localization.Get("PatchManagerNoMethods", type.Name));
                }
            }
            catch (Exception e)
            {
                Main.Logger?.Error(Localization.Get("PatchManagerFailedApply", type.Name, e.ToString()));
            }
        }

        private static void RemovePatch(Type type)
        {
            try
            {
                Main.Logger?.Log(Localization.Get("PatchManagerAttemptRemove", type.Name));
                if (_patchedBindings.TryGetValue(type, out var bindings) && bindings.Count > 0)
                {
                    Main.Logger?.Log(Localization.Get("PatchManagerUsingCache", type.Name, bindings.Count.ToString()));
                    foreach (var (original, patchMethod) in bindings)
                    {
                        _harmony.Unpatch(original, patchMethod);
                    }
                    _patchedBindings.Remove(type);
                }
                else
                {
                    // Fallback to slow method if cache is missing or empty
                    Main.Logger?.Log(Localization.Get("PatchManagerUsingFallback", type.Name));
                    UnpatchMethod(type);
                    _patchedBindings.Remove(type);
                }

                _activePatches[type] = false;
                Main.Logger?.Log(Localization.Get("PatchManagerSuccessRemove", type.Name));
            }
            catch (Exception e)
            {
                Main.Logger?.Error(Localization.Get("PatchManagerFailedRemove", type.Name, e.ToString()));
            }
        }

        private static void UnpatchMethod(Type patchClass)
        {
            // Slow fallback: search all patched methods in the game
            var allPatchedMethods = _harmony.GetPatchedMethods();
            foreach (var original in allPatchedMethods)
            {
                var info = Harmony.GetPatchInfo(original);
                if (info == null) continue;

                foreach (var p in info.Prefixes)
                {
                    if (p.owner == _harmony.Id && p.PatchMethod.DeclaringType == patchClass)
                        _harmony.Unpatch(original, p.PatchMethod);
                }
                foreach (var p in info.Postfixes)
                {
                    if (p.owner == _harmony.Id && p.PatchMethod.DeclaringType == patchClass)
                        _harmony.Unpatch(original, p.PatchMethod);
                }
                foreach (var p in info.Transpilers)
                {
                    if (p.owner == _harmony.Id && p.PatchMethod.DeclaringType == patchClass)
                        _harmony.Unpatch(original, p.PatchMethod);
                }
                foreach (var p in info.Finalizers)
                {
                    if (p.owner == _harmony.Id && p.PatchMethod.DeclaringType == patchClass)
                        _harmony.Unpatch(original, p.PatchMethod);
                }
            }
        }

        public static void UnpatchAll()
        {
            _harmony?.UnpatchAll(_harmony.Id);
            _activePatches.Clear();
            _patchedBindings.Clear();

            lock (_methodPatches)
            {
                foreach (var mp in _methodPatches)
                {
                    if (mp.IsPatched)
                        mp.StopPatch();
                }
            }

            Main.Logger?.Log(Localization.Get("PatchManagerUnpatchedAll"));
        }
    }
}
