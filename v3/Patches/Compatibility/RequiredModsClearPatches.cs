using Iridium.Config;
using System;
using System.Collections.Generic;
using HarmonyLib;
using ADOFAI;

namespace Iridium.Patches.Compatibility
{
	/// <summary>
	/// Third-party required mods handling ("Ignore required third-party mods").
	///
	/// Instead of patching the dependency check, the chart's settings.requiredMods is
	/// emptied to [] right before the game decodes the level, so every reader sees an
	/// empty list. The original is kept per LevelData instance and restored on encode,
	/// so requiredMods survives a save.
	/// </summary>
	public static class RequiredModsClearPatches
	{
		private static readonly Dictionary<LevelData, object> OriginalRequiredMods = new();
		private static readonly List<string> CurrentRequiredMods = new();

		private static object ClearRequiredMods(Dictionary<string, object> settings)
		{
			if (settings == null || !settings.TryGetValue("requiredMods", out var raw)) return null;

			CurrentRequiredMods.Clear();
			if (raw is object[] arr)
			{
				foreach (var item in arr)
					if (item is string s && !string.IsNullOrEmpty(s)) CurrentRequiredMods.Add(s);
			}
			else if (raw is List<object> list)
			{
				foreach (var item in list)
					if (item is string s && !string.IsNullOrEmpty(s)) CurrentRequiredMods.Add(s);
			}

			settings["requiredMods"] = new List<object>();
			return raw;
		}

		/// <summary>清空解码路径的 requiredMods，并暂存原值供 encode 还原。</summary>
		[HarmonyPatch(typeof(LevelData), nameof(LevelData.Decode))]
		[IriPatch(Path = "compatibility/requiredMods", Pre = typeof(CompatibilitySettings), Condition = "ignoreRequiredMods")]
		public static class LevelDataClearPatch
		{
			[HarmonyPrefix]
			public static void Prefix(LevelData __instance, Dictionary<string, object> dict)
			{
				if (!Main.Settings.compatibility.ignoreRequiredMods) return;
				if (dict != null && dict.TryGetValue("settings", out var s) && s is Dictionary<string, object> settings)
					OriginalRequiredMods[__instance] = ClearRequiredMods(settings);
			}
		}

		/// <summary>Level select 解码路径（LevelDataCLS.Decode）。</summary>
		[HarmonyPatch(typeof(LevelDataCLS), nameof(LevelDataCLS.Decode))]
		[IriPatch(Path = "compatibility/requiredMods", Pre = typeof(CompatibilitySettings), Condition = "ignoreRequiredMods")]
		public static class LevelDataCLSClearPatch
		{
			[HarmonyPrefix]
			public static void Prefix(Dictionary<string, object> rootDict)
			{
				if (!Main.Settings.compatibility.ignoreRequiredMods) return;
				if (rootDict != null && rootDict.TryGetValue("settings", out var s) && s is Dictionary<string, object> settings)
					ClearRequiredMods(settings);
			}
		}

		/// <summary>Encode 前还原原 requiredMods，保存/导出时不丢。</summary>
		[HarmonyPatch(typeof(LevelData), nameof(LevelData.EncodeToDictionary))]
		[IriPatch(Path = "compatibility/requiredMods", Pre = typeof(CompatibilitySettings), Condition = "ignoreRequiredMods")]
		public static class EncodeRestorePatch
		{
			[HarmonyPrefix]
			public static void Prefix(LevelData __instance)
			{
				if (!Main.Settings.compatibility.ignoreRequiredMods) return;
				if (__instance == null) return;
				if (!OriginalRequiredMods.TryGetValue(__instance, out var raw) || raw == null) return;

				__instance.levelSettings["requiredMods"] = raw;
				OriginalRequiredMods.Remove(__instance);
			}
		}

		/// <summary>关卡加载完成后（编辑器打开或开始游玩）提示被忽略的缺失 mod。</summary>
		[HarmonyPatch(typeof(scnGame), nameof(scnGame.LoadLevel))]
		[IriPatch(Path = "compatibility/requiredMods", Pre = typeof(CompatibilitySettings), Condition = "ignoreRequiredMods")]
		public static class LevelLoadNotifyPatch
		{
			[HarmonyPostfix]
			public static void Postfix(LoadResult status)
			{
				try
				{
					if (!Main.Settings.compatibility.ignoreRequiredMods) return;
					if (status != LoadResult.Successful) return;
					if (CurrentRequiredMods.Count == 0) return;

					var mods = CurrentRequiredMods.ToArray();
					CurrentRequiredMods.Clear();

					if (mods.Length == 1)
						Iridium.UI.VRAMNotificationUI.Show(Localization.Get("MissingModsNoticeSingle", mods[0]));
					else
						Iridium.UI.VRAMNotificationUI.Show(Localization.Get("MissingModsNotice", mods[0], mods.Length - 1));
				}
				catch (Exception e)
				{
					Main.Logger?.Log($"[RequiredMods] notify error: {e}");
				}
			}
		}
	}
}
