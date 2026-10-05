using System.Collections.Generic;

namespace Iridium
{
	/// <summary>
	/// 优化器子 Tab 的选项键清单（供搜索过滤使用）。
	/// 键为各开关的本地化 Label 键名；新增开关时把 Label 键加进对应分组即可。
	/// </summary>
	public static class OptimizerLayout
	{
		private static readonly Dictionary<string, string[]> _options = new()
		{
			// ── 通用：用户直接感知的画质/资源项 ──
			["basics"] = new[]
			{
				"EnableOptimizer", "RenderScale", "RenderScalePercent",
				"CompressImage", "ShowSavedMemory", "UseLossyCompression", "LossyQuality",
				"MultipleOf4", "DivideImageBy", "DontResizeCollider", "DisableShadows",
				"ShowVRAMNotification",
			},
			// ── 游戏性能：Update 热路径与玩法相关 ──
			["gameplay"] = new[]
			{
				"OptimizeDecorationUpdate", "OptimizeHitboxDetection", "OptimizeTileUpdate",
				"OptimizeMoveDecorations", "OptimizeFfxDecorations", "OptimizeDecorationShaderCache",
				"OptimizeHoldRenderer", "OptimizeOnBeat", "OptimizeSpectrum",
				"OptimizeDeathReset", "OptimizeDecorationReset",
				"OptimizeScnGameUpdate", "OptimizePlayerInputAllocations", "OptimizeRDInputAllocations",
				"OptimizeGameplayAllocations", "SkipEventIfPaused",
			},
			// ── 特效与动画：缓动/DOTween/粒子 ──
			["fx"] = new[]
			{
				"EnableCustomEasingEngine", "OptimizeFilters",
				"EnableDOTweenOptimization", "TweenerCapacity", "SequenceCapacity",
				"DOTweenDefaultRecyclable", "DOTweenDisableSafeMode",
				"OptimizeParticle", "OptimizeParticleInactive", "OptimizeParticleCulling", "OptimizeParticleLod",
			},
			// ── 高级 / 实验性 ──
			["advanced"] = new[]
			{
				"OptimizeLargeLevelLoading", "ChunkedFloorSpawn",
				"FrameSpreadDecorationLoading", "DecorationsPerFrame",
				"CustomLevelReadOptimization", "OptimizeHitTextPool",
				"OptimizeEventProcessing", "OptimizeEditorMouseDetection", "OptimizeEditorEventIndicators",
				"OptimizeEditorInteractions",
				"EnableExtremeOptimization", "MaxTweensPerFrame",
				"OptimizeMoveTrackTweens", "BatchMoveDecorations",
				"EnableStaticDecorationBatching",
			},
		};

		public static IEnumerable<string> GetOptionKeys(string subTab)
		{
			return _options.TryGetValue(subTab, out var keys) ? keys : System.Array.Empty<string>();
		}

		/// <summary>全部子 Tab 的键（含未知分组兜底），供需要遍历的场景使用。</summary>
		public static IEnumerable<string> AllSubTabs => _options.Keys;
	}
}
