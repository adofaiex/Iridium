using GDMiniJSON;
using HarmonyLib;
using Iridium.Config;
using Iridium.Native;

namespace Iridium.Patches.Optimizer
{
	/// <summary>
	/// GDMiniJSON 解析走 iridium_core（Rust 批量解析）。
	///
	/// 覆盖两个入口：
	///  - <see cref="Json.Deserialize(string)"/>：LevelData.LoadLevel 的完整谱面解析；
	///  - <see cref="Json.DeserializePartially(string, string)"/>：选关列表 /
	///    GetCustomLevelName 的“只解析到 actions 之前”的部分解析。
	///
	/// 语义：物化后的对象图与 GDMiniJSON 完全一致（Dictionary/List/string/
	/// int/float/bool/null；重复键后者覆盖；数字解析失败为 0）。任何解析失败
	/// 或库缺失都原样回退到托管解析器，不改变行为。
	/// </summary>
	public static class JsonNativeParsePatch
	{
		[IriPatch(Path = "loading/largeLevel", Pre = typeof(OptimizerSettings), Condition = "optimizeLargeLevelLoading")]
		[HarmonyPatch(typeof(Json), nameof(Json.Deserialize), new[] { typeof(string) })]
		public static class DeserializePatch
		{
			[HarmonyPrefix]
			public static bool Prefix(string json, ref object __result)
			{
				if (NativeJson.TryParse(json, null, out object? result))
				{
					__result = result!;
					return false;
				}
				return true;
			}
		}

		[IriPatch(Path = "loading/largeLevel", Pre = typeof(OptimizerSettings), Condition = "optimizeLargeLevelLoading")]
		[HarmonyPatch(typeof(Json), nameof(Json.DeserializePartially), new[] { typeof(string), typeof(string) })]
		public static class DeserializePartiallyPatch
		{
			[HarmonyPrefix]
			public static bool Prefix(string json, string upToSection, ref object __result)
			{
				if (NativeJson.TryParse(json, upToSection, out object? result))
				{
					__result = result!;
					return false;
				}
				return true;
			}
		}
	}
}
