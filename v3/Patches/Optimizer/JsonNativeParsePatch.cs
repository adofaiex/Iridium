using GDMiniJSON;
using HarmonyLib;
using Iridium.Config;
using Iridium.Native;

namespace Iridium.Patches.Optimizer
{
	/// <summary>Routes GDMiniJSON through iridium_core; the object graph must match it exactly, and any failure or missing library falls back to the managed parser.</summary>
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
