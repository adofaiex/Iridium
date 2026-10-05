using System.Collections.Generic;

namespace Iridium
{
	/// <summary>
	/// 全局设置搜索索引。
	///
	/// 每个可搜索条目 = 一个「选项行」（开关/滑条/文本框）或一个「分组头」。
	/// 搜索时按 Label 键的本地化文本（及键名本身）匹配，结果按 Tab 分组展示；
	/// 点击结果跳转到对应 Tab 并自动展开所在区块与 Info 说明。
	///
	/// 新增开关时：在对应 Tab 的条目列表里加一条即可被搜索到。
	/// Optimizer 子 Tab 的条目沿用 OptimizerLayout 的分组键（其 Label 键同时
	/// 登记在这里，搜索行为一致，但跳转目标带子 Tab）。
	/// </summary>
	public static class SettingsSearchIndex
	{
		public sealed class Entry
		{
			/// <summary>本地化 Label 键（显示名与匹配文本来源）。</summary>
			public string LabelKey;
			/// <summary>顶层 Tab（general/optimizer/editor/compatibility/audio）。</summary>
			public string Tab;
			/// <summary>Optimizer 子 Tab（仅 optimizer 条目有值：basics/gameplay/fx/advanced）。</summary>
			public string? SubTab;
			/// <summary>跳转后要自动展开的折叠区块（section key，可空）。</summary>
			public string? Section;
			/// <summary>跳转后要自动展开的 Info 键（可空，默认 = LabelKey + "Info"）。</summary>
			public string? InfoKey;

			public Entry(string labelKey, string tab, string? subTab = null, string? section = null, string? infoKey = null)
			{
				LabelKey = labelKey;
				Tab = tab;
				SubTab = subTab;
				Section = section;
				InfoKey = infoKey;
			}
		}

		private static readonly List<Entry> _entries = Build();

		public static IReadOnlyList<Entry> Entries => _entries;

		private static List<Entry> Build()
		{
			var list = new List<Entry>();

			// ── Optimizer：沿用 OptimizerLayout 的四组键，标注子 Tab ──
			list.Add(new Entry("EnableOptimizer", "optimizer", "basics"));
			foreach (string key in OptimizerLayout.GetOptionKeys("basics"))
				if (key != "EnableOptimizer")
					list.Add(new Entry(key, "optimizer", "basics"));
			foreach (string key in OptimizerLayout.GetOptionKeys("gameplay"))
				list.Add(new Entry(key, "optimizer", "gameplay"));
			foreach (string key in OptimizerLayout.GetOptionKeys("fx"))
				list.Add(new Entry(key, "optimizer", "fx"));
			foreach (string key in OptimizerLayout.GetOptionKeys("advanced"))
				list.Add(new Entry(key, "optimizer", "advanced"));

			// ── General（UI / 判定文本 / 自动播放提示） ──
			string[] general = {
				"Language", "RemoveNews", "HideBetaWatermark", "MoveAutoplayText",
				"ForceDifficultyUI", "EnableCircleArc", "AlwaysCountdown",
				"EnablePausePlanetTrail", "ShowAutoplayHintUI", "CustomAutoplayHint",
				"UIBehavior",
				"EnableJudgeTextCustomization", "JudgeText_TooEarly", "JudgeText_VeryEarly",
				"JudgeText_EarlyPerfect", "JudgeText_Perfect", "JudgeText_LatePerfect",
				"JudgeText_VeryLate", "JudgeText_TooLate", "JudgeText_Multipress",
				"JudgeText_FailMiss", "JudgeText_FailOverload", "JudgeText_XPerfect",
			};
			foreach (string key in general)
				list.Add(new Entry(key, "general"));

			// ── Editor ──
			list.Add(new Entry("EnableEditorFloorOptimization", "editor", null, null, "EditorFloorOptimizations"));
			string[] editor = {
				"IncrementalFloorInsert", "RangeBasedRedraw", "SkipRedundantRemakePath",
				"OptimizeOffsetFloorEvents", "SkipApplyEventsOnInsert",
				"EnableEditorShortcuts", "ShortcutSelectAll", "ShortcutDeselectAll",
				"ShortcutToggleVisibility", "ShortcutFocusDecoration", "ShortcutGoToFloor",
				"ShortcutSelectAllFloors", "ShortcutPopupSave", "ShortcutPopupDiscard",
				"EditorPauseEnabled", "EditorPauseAllowed", "CameraFollowOnFloorSelect",
			};
			foreach (string key in editor)
				list.Add(new Entry(key, "editor"));

			// ── Compatibility ──
			string[] compat = {
				"EnableLegacyPauseFix", "EnableNoFailTooEarly", "PortalTravelFix",
				"FixTurnaroundCondition", "FixEditorPlayResetMistakes", "FixCoopPauseLock",
				"FixJudgeRotation", "FixCameraRelativeDrag", "ForceAngleData",
				"ScaleFilterSpeedWithPitch", "IgnoreRequiredMods", "UseILPatch",
			};
			foreach (string key in compat)
				list.Add(new Entry(key, "compatibility"));

			// ── Audio ──
			string[] audio = {
				"EnableHitSoundPitch", "EnableLobbyMusicPatch", "EnableCustomBpm",
				"EnableAsyncInput", "LobbyCustomMusic", "LobbyDefaultMusicPath",
				"LobbyFastMusicPath", "LobbyReloadMusic",
			};
			foreach (string key in audio)
				list.Add(new Entry(key, "audio"));

			return list;
		}
	}
}
