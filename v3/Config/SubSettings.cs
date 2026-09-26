using System;
using UnityEngine;

namespace Iridium.Config
{
    public class OptimizerSettings
    {
        public bool enableOptimizer = false;
        public bool enableRenderScale = false;   // 帧缓冲降分辨率（世界画面以较低分辨率渲染后放大）
        public int renderScalePercent = 75;      // 30-100，越低越流畅
        public bool optimizeMoveTrack = false;
        public bool optimizeRecolorTrack = false;
        public bool optimizeFilters = false; // 装饰物滤镜 RT 缓存（参数/事件变化自动失效）
		public int divideBy = 1;
        public bool dontShowSavedMemory = false;
        public bool dontCompress = false;
        public bool dontResizeMultipleOf4 = false;
        public bool dontResizeCollider = false;
        public bool useLossyCompression = false;
        public int lossyQuality = 90;
        public bool disableShadows = false;
        public bool optimizeDecorationUpdate = false;
        public bool optimizeHitboxDetection = false; // 命中框判定热路径优化
        public bool optimizeTileUpdate = false;
        public bool fastLoading = false;
        public bool skipEventIfPaused = false;
        public bool optimizeEventIcons = false;
        public bool optimizeScnGameUpdate = false;
        public bool optimizePlayerInputAllocations = false;
        public bool optimizeRDInputAllocations = false;
        public bool optimizeGameplayAllocations = false;
        public bool optimizeMoveDecorations = false;
        public bool optimizeFloorMesh = false;
        public bool optimizeFfxDecorations = false; // 新增：优化 ffx 装饰物更新
        public bool optimizeDecorationShaderCache = false; // 装饰物渲染脏检查缓存
        public bool enableStaticDecorationBatching = false; // 静态装饰物合批渲染（实验性，需缓速引擎）

        // Particle Optimization Patches (粒子优化)
        public bool optimizeParticle = false;             // 主开关
        public bool optimizeParticleInactive = false;     // 跳过非活跃粒子 + 对象池
        public bool optimizeParticleCulling = false;      // 离屏暂停模拟
        public bool optimizeParticleLod = false;          // LOD 跳过

        // Scene Optimization Patches
        public bool optimizeEventProcessing = false;
        public bool optimizeEditorMouseDetection = false;
        public bool optimizeEditorEventIndicators = false;

        // Loading Optimization Patches
        public bool optimizeMoveTrackTweens = false;
        public bool batchMoveDecorations = false;

        // DOTween Optimization Patches
        public bool optimizeDOTweenGlobal = false;
        public int dotweenTweenerCapacity = 500;
        public int dotweenSequenceCapacity = 100;
        public bool dotweenDefaultRecyclable = true;
        public bool dotweenDisableSafeMode = false;

        // Extreme Optimization Patches (极端情况优化)
        public bool enableExtremeOptimization = false; // 启用极端优化（分帧处理）
        public int maxTweensPerFrame = 100; // 每帧最多创建的Tween数量

        // Frame-Spread Loading (分帧加载)
        public bool frameSpreadDecorationLoading = false; // 启用装饰物分帧加载
        public int decorationsPerFrame = 50; // 每帧加载的装饰物数量

        // JSON Deserialize Optimization
        public bool customLevelReadOptimization = false; // 自定义关卡谱面读取优化

        public bool enableCustomEasingEngine = false; // 自定义缓速引擎（替代 DOTween）

        // --- Editor Floor Performance Optimizations ---
        public bool enableEditorFloorOptimization = false; // 主开关
        public bool incrementalFloorInsert = false;        // 增量式砖块插入/删除
        public bool rangeBasedRedraw = false;              // 范围式重绘(Holds/Planets/Nums)
        public bool skipRedundantRemakePath = false;       // 跳过重复的RemakePath调用
        public bool optimizeOffsetFloorEvents = false;     // 优化 OffsetFloorIDsInEvents
        public bool skipApplyEventsOnInsert = false;       // 增量插入时跳过 ApplyEventsToFloors

        // 大谱面加载优化（几十万砖）：跳过无特效砖块的 GetComponents 扫描 + 加载阶段计时日志
        public bool optimizeLargeLevelLoading = false;
        // 大谱面加载优化 B：分帧激活砖块（实验性；关闭则退回一次性激活）
        public bool chunkedFloorSpawn = true;
    }

    public class UISettings
    {
        public bool removeNews = false;
        public bool hideBetaWatermark = false;
        public bool moveAutoplayText = false;
        public float autoplayTextX = 0f;
        public float autoplayTextY = 0f;
        public bool forceDifficultyUI = false;
        public bool enableCircleArc = false;
        // Turn-angle band (degrees) where CircleArcPatch applies the big arc.
        // Defaults keep old configs (missing these fields) behaving as a
        // right-angle-only feature.
        public float circleArcMinAngle = 90f;
        public float circleArcMaxAngle = 105f;
        public bool alwaysCountdown = false;
        public bool enablePausePlanetTrail = false;
        public bool showAutoplayHintUI = true;
        public bool customAutoplayHint = false;
        public string autoplayHintTemplate = "{autoplay}: {status}, press {key} to toggle";
    }

    public class LobbyMusicSettings
    {
        public bool enableLobbyMusicPatch = false;
        public bool enableCustomBpm = false;
        public float customBpm = 120f;
        public bool fastMusic = false;
        public bool customMusic = false;
        public string defaultMusicPath = string.Empty;
        public string fastMusicPath = string.Empty;
    }

    public class MemorySettings
    {
        /// <summary>
        /// 基础优化：整合旧“内存优化”系列与 FerriteCore 引擎调优的单一开关。
        /// 具体调优项在 FerriteCore 的 Config/FerriteCore.json 中微调。
        /// </summary>
        public bool enableBasicOptimization = false;

        /// <summary>
        /// 进阶优化：虚拟内存优化（原理同 PCL2）。仅在 Windows 上执行
        /// 工作集裁剪；其他平台自动降级为仅托管侧清理。
        /// </summary>
        public bool enableVirtualMemoryOptimization = false;
        public bool vmTrimOnLevelLoad = false;
        public bool vmTrimOnEditorEnter = false;

        // Legacy: pre-rework master switch, kept only to migrate old
        // Settings.xml files. No longer shown in the UI.
        public bool enableFerriteCore = false;
    }

    public class CompatibilitySettings
    {
        public bool enableLegacyPauseFix = false;
        public bool enableNoFailTooEarly = false;
        public bool forceAngleData = false;
        public bool scaleFilterSpeedWithPitch = false;
        public bool fixCameraRelativeDrag = false;
        public bool portalTravelFix = false;
        public bool fixEditorPlayResetMistakes = true;
        public bool fixTurnaroundCondition = true;
        public bool editorPauseEnabled = false;
        public bool editorPauseAllowed = true; // Master switch: allow pause in editor auto-play
        public int editorPauseKey = 32; // KeyCode.Space
        public int editorPauseModifiers = 0; // bit: 1=Ctrl 2=Alt 4=Shift 8=Win
        public bool fixCoopPauseLock = true;
        public bool fixJudgeRotation = true;
        public bool ignoreRequiredMods = false;
        public LegacyBehaviorMode legacyFlashMode = LegacyBehaviorMode.Default;
        public LegacyBehaviorMode legacyCamRelativeToMode = LegacyBehaviorMode.Default;
    }

    public enum LegacyBehaviorMode
    {
        Default,
        AlwaysOff,
        AlwaysOn
    }

    public class HitSoundSettings
    {
        public bool enableHitSoundPitch = false;
    }

    public class JudgeTextSettings
    {
        public bool enableJudgeTextCustomization = false;

        // 自定义判定文本（支持 {offset} / {offset:x} 占位符）
        public string tooEarly = "TooEarly";
        public string veryEarly = "VeryEarly";
        public string earlyPerfect = "EarlyPerfect";
        public string perfect = "Perfect";
        public string latePerfect = "LatePerfect";
        public string veryLate = "VeryLate";
        public string tooLate = "TooLate";
        public string multipress = "Multipress";
        public string failMiss = "FailMiss";
        public string failOverload = "FailOverload";

        // 3.4.0 新增/拆分的判定（旧版本不显示、不生效）
        public string perfectMinus = "PerfectMinus";
        public string xPerfect = "XPerfect";
        public string perfectPlus = "PerfectPlus";
        public string midspin = "Midspin";
        public string failedFloor = "FailedFloor";

        private static readonly bool _extendedHitMargins = DetectExtendedHitMargins();

        /// <summary>运行中的游戏是否具有 3.4.0 的扩展判定类型（XPerfect 等）。</summary>
        public bool extendedHitMargins => _extendedHitMargins;

        private static bool DetectExtendedHitMargins()
        {
            try { return Enum.IsDefined(typeof(HitMargin), "XPerfect"); }
            catch { return false; }
        }

        // 按枚举名映射判定文本。禁止按数值/索引映射：3.4.0 重排了 HitMargin
        // 的值（Perfect=3 在 4.0 变成 PerfectMinus），编译期常量与 (int) 转换
        // 会在跨版本运行时静默指向错误的判定类型。
        public string GetTextForHitMarginName(string name)
        {
            return name switch
            {
                "TooEarly" => tooEarly,
                "VeryEarly" => veryEarly,
                "EarlyPerfect" => earlyPerfect,
                "Perfect" => perfect,
                "PerfectMinus" => perfectMinus,
                "XPerfect" => xPerfect,
                "PerfectPlus" => perfectPlus,
                "LatePerfect" => latePerfect,
                "VeryLate" => veryLate,
                "TooLate" => tooLate,
                "Multipress" => multipress,
                "FailMiss" => failMiss,
                "FailOverload" => failOverload,
                "Midspin" => midspin,
                "FailedFloor" => failedFloor,
                _ => ""
            };
        }

        public static string ReplaceOffset(string template, double offsetMs)
        {
            if (double.IsNaN(offsetMs) || double.IsInfinity(offsetMs))
                offsetMs = 0;

            return System.Text.RegularExpressions.Regex.Replace(template, @"\{offset(?::(\d+))?\}", match =>
            {
                double abs = Math.Abs(offsetMs);
                bool isZero;
                string formatted;
                if (match.Groups[1].Success)
                {
                    int decimals = int.Parse(match.Groups[1].Value);
                    formatted = abs.ToString("F" + decimals);
                    isZero = Math.Round(abs, decimals) == 0;
                }
                else
                {
                    formatted = Math.Round(abs).ToString();
                    isZero = Math.Round(abs) == 0;
                }
                string sign = offsetMs < 0 && !isZero ? "-" : "";
                return sign + formatted;
            });
        }

        public void ConvertAllToOffset()
        {
            tooEarly = "{offset}ms";
            veryEarly = "{offset}ms";
            earlyPerfect = "{offset}ms";
            perfect = "{offset}ms";
            latePerfect = "{offset}ms";
            veryLate = "{offset}ms";
            tooLate = "{offset}ms";
            multipress = "{offset}ms";
            failMiss = "{offset}ms";
            failOverload = "{offset}ms";
            perfectMinus = "{offset}ms";
            xPerfect = "{offset}ms";
            perfectPlus = "{offset}ms";
            midspin = "{offset}ms";
            failedFloor = "{offset}ms";
        }

        public void ResetToDefault()
        {
            tooEarly = "TooEarly";
            veryEarly = "VeryEarly";
            earlyPerfect = "EarlyPerfect";
            perfect = "Perfect";
            latePerfect = "LatePerfect";
            veryLate = "VeryLate";
            tooLate = "TooLate";
            multipress = "Multipress";
            failMiss = "FailMiss";
            failOverload = "FailOverload";
            perfectMinus = "PerfectMinus";
            xPerfect = "XPerfect";
            perfectPlus = "PerfectPlus";
            midspin = "Midspin";
            failedFloor = "FailedFloor";
        }
    }

    /// <summary>
    /// 补丁模式设置 — 控制 Transpiler / PrefixPostfix 切换
    /// </summary>
    public class PatchModeSettings
    {
        /// <summary>
        /// true = 使用 Transpiler（IL注入，性能优先）
        /// false = 使用 Prefix/Postfix（兼容性优先）
        /// </summary>
        public bool useILPatch = false;
    }

	public class EditorShortcutSettings
	{
		public bool enableEditorShortcuts = false;

		// --- Decoration shortcuts ---
		// NOTE: key codes must be UnityEngine.KeyCode values, NOT ASCII.
		// Unity letter keys start at A=97 (lowercase ASCII); 65..90 are
		// undefined holes that Input.GetKeyDown can never match.

		// Select All Decorations (default: Ctrl+Shift+A)
		public int selectAllKey = 97; // KeyCode.A
		public int selectAllModifiers = 5; // Ctrl+Shift

		// Deselect All (default: Ctrl+Shift+D)
		public int deselectAllKey = 100; // KeyCode.D
		public int deselectAllModifiers = 5; // Ctrl+Shift

		// Toggle Visibility (default: Ctrl+E)
		public int toggleVisibilityKey = 101; // KeyCode.E
		public int toggleVisibilityModifiers = 1; // Ctrl

		// Focus Decoration (default: Ctrl+G)
		public int focusDecorationKey = 103; // KeyCode.G
		public int focusDecorationModifiers = 1; // Ctrl

		// --- Navigation shortcuts ---

		// Go To Selected Floor (default: Ctrl+Shift+N)
		public int goToFloorKey = 110; // KeyCode.N
		public int goToFloorModifiers = 5; // Ctrl+Shift
		public bool cameraFollowOnFloorSelect = true; // SelectFloor 附带镜头跟随

		// Select All Floors (default: Ctrl+Shift+W)
		public int selectAllFloorsKey = 119; // KeyCode.W
		public int selectAllFloorsModifiers = 5; // Ctrl+Shift

		// --- Popup shortcuts ---

		// Popup Save (default: Return)
		public int popupSaveKey = 13; // KeyCode.Return
		public int popupSaveModifiers = 0;

		// Popup Discard (default: D)
		public int popupDiscardKey = 100; // KeyCode.D
		public int popupDiscardModifiers = 0;

		/// <summary>
		/// 早期版本的默认键码误用 ASCII 大写字母（'A'=65 等）。这些值不是有效的
		/// Unity KeyCode，永远不会触发，且会随设置一起被序列化进用户配置。
		/// 加载时把已知的无效遗留值迁移为正确的键码；由于这些数值不对应任何
		/// 物理键，用户不可能有意绑定它们，无条件迁移是安全的。
		/// </summary>
		public void MigrateLegacyAsciiKeyCodes()
		{
			if (selectAllKey == 65) selectAllKey = 97;          // 'A' -> A
			if (deselectAllKey == 68) deselectAllKey = 100;     // 'D' -> D
			if (toggleVisibilityKey == 69) toggleVisibilityKey = 101; // 'E' -> E
			if (focusDecorationKey == 71) focusDecorationKey = 103;   // 'G' -> G
			if (goToFloorKey == 78) goToFloorKey = 110;         // 'N' -> N
			if (selectAllFloorsKey == 87) selectAllFloorsKey = 119;   // 'W' -> W
			if (popupDiscardKey == 68) popupDiscardKey = 100;   // 'D' -> D
		}
	}

	public class AsyncInputSettings
	{
		public bool enableAIO = false;
	}
}
