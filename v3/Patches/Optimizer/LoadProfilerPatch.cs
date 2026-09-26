using System;
using System.Collections.Generic;
using System.Diagnostics;
using ADOFAI;
using HarmonyLib;
using Iridium.Config;
using UnityEngine;

namespace Iridium.Patches.Optimizer
{
	/// <summary>
	/// 大物量谱面加载耗时诊断。
	/// 每次加载只多几行 [LoadProfiler] 日志，始终启用，方便定位
	/// JSON 解析 / 砖块实例化 / 事件应用 / 装饰物 各阶段耗时。
	/// 需要上传日志时按这些行报数即可。
	/// </summary>
	public static class LoadProfilerPatch
	{
		private static readonly Stopwatch TotalWatch = new();
		private static readonly Stopwatch DecodeWatch = new();
		private static readonly Stopwatch FloorWatch = new();
		private static readonly Stopwatch EventsWatch = new();

		/// <summary>托管/Unity 内存快照（MB），用于观察大谱面加载的内存峰值。</summary>
		internal static string MemorySnapshot()
		{
			try
			{
				long managed = GC.GetTotalMemory(false) / 1048576;
				long monoUsed = UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong() / 1048576;
				long unityAlloc = UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong() / 1048576;
				long unityReserved = UnityEngine.Profiling.Profiler.GetTotalReservedMemoryLong() / 1048576;
				return $"managed={managed}MB, monoUsed={monoUsed}MB, unity={unityAlloc}/{unityReserved}MB";
			}
			catch (Exception e)
			{
				return $"mem unavailable ({e.GetType().Name})";
			}
		}

		[IriPatch(Path = "loading/largeLevel", Pre = typeof(OptimizerSettings), Condition = "optimizeLargeLevelLoading")]
		[HarmonyPatch(typeof(LevelData), nameof(LevelData.LoadLevel))]
		public static class LoadLevelProfiler
		{
			[HarmonyPrefix]
			public static void Prefix(string __0)
			{
				_inLevelLoad = true;
				TotalWatch.Restart();
				try
				{
					long size = 0;
					if (!string.IsNullOrEmpty(__0) && System.IO.File.Exists(__0))
						size = new System.IO.FileInfo(__0).Length;
					Main.Logger?.Log(
						$"[LoadProfiler] load level start: {__0} ({size / 1048576}MB) | {MemorySnapshot()}");
				}
				catch (Exception e)
				{
					Main.Logger?.Log($"[LoadProfiler] load level start log failed: {e.Message}");
				}
			}

			[HarmonyPostfix]
			public static void Postfix(LevelData __instance, bool __result)
			{
				_inLevelLoad = false;
				if (!__result) return;
				int floors = scrLevelMaker.instance != null && scrLevelMaker.instance.listFloors != null
					? scrLevelMaker.instance.listFloors.Count : 0;
				int events = __instance != null && __instance.levelEvents != null
					? __instance.levelEvents.Count : 0;
				Main.Logger?.Log(
					$"[LoadProfiler] load level: {TotalWatch.ElapsedMilliseconds} ms (floors={floors}, events={events}) | {MemorySnapshot()}");
			}
		}

		[IriPatch(Path = "loading/largeLevel", Pre = typeof(OptimizerSettings), Condition = "optimizeLargeLevelLoading")]
		[HarmonyPatch(typeof(LevelData), nameof(LevelData.Decode))]
		public static class DecodeProfiler
		{
			[HarmonyPrefix]
			public static void Prefix()
			{
				DecodeWatch.Restart();
			}

			[HarmonyPostfix]
			public static void Postfix(LevelData __instance)
			{
				int events = __instance != null && __instance.levelEvents != null
					? __instance.levelEvents.Count : 0;
				Main.Logger?.Log($"[LoadProfiler]   JSON+event decode: {DecodeWatch.ElapsedMilliseconds} ms (events={events})");
			}
		}

		[IriPatch(Path = "loading/largeLevel", Pre = typeof(OptimizerSettings), Condition = "optimizeLargeLevelLoading")]
		[HarmonyPatch(typeof(scrLevelMaker), nameof(scrLevelMaker.InstantiateFloatFloors))]
		public static class FloatFloorsProfiler
		{
			[HarmonyPrefix]
			public static void Prefix()
			{
				FloorWatch.Restart();
			}

			[HarmonyPostfix]
			public static void Postfix(scrLevelMaker __instance)
			{
				int floors = __instance != null && __instance.listFloors != null
					? __instance.listFloors.Count : 0;
				Main.Logger?.Log($"[LoadProfiler]   instantiate floors: {FloorWatch.ElapsedMilliseconds} ms (floors={floors})");
			}
		}

		[IriPatch(Path = "loading/largeLevel", Pre = typeof(OptimizerSettings), Condition = "optimizeLargeLevelLoading")]
		[HarmonyPatch(typeof(scnGame), nameof(scnGame.ApplyEventsToFloors),
			new[] { typeof(List<scrFloor>), typeof(LevelData), typeof(scrLevelMaker), typeof(List<LevelEvent>) })]
		public static class ApplyEventsProfiler
		{
			[HarmonyPrefix]
			public static void Prefix()
			{
				EventsWatch.Restart();
			}

			[HarmonyPostfix]
			public static void Postfix()
			{
				Main.Logger?.Log($"[LoadProfiler]   apply events to floors: {EventsWatch.ElapsedMilliseconds} ms");
			}
		}

		private static readonly Stopwatch MakeLevelWatch = new();
		private static readonly Stopwatch RemakePathWatch = new();

		/// <summary>是否正处于 LevelData.LoadLevel 过程中（用于只记录关卡文件的 JSON 解析）。</summary>
		private static bool _inLevelLoad;

		[IriPatch(Path = "loading/largeLevel", Pre = typeof(OptimizerSettings), Condition = "optimizeLargeLevelLoading")]
		[HarmonyPatch(typeof(GDMiniJSON.Json), nameof(GDMiniJSON.Json.Deserialize), new[] { typeof(string) })]
		public static class JsonParseProfiler
		{
			private static readonly Stopwatch ParseWatch = new();

			[HarmonyPrefix]
			public static void Prefix(string __0)
			{
				if (!_inLevelLoad || __0 == null || __0.Length < 500_000) return;
				ParseWatch.Restart();
			}

			[HarmonyPostfix]
			public static void Postfix(string __0)
			{
				if (!_inLevelLoad || __0 == null || __0.Length < 500_000) return;
				Main.Logger?.Log($"[LoadProfiler]   JSON parse done: {ParseWatch.ElapsedMilliseconds} ms | {MemorySnapshot()}");
			}
		}

		[IriPatch(Path = "loading/largeLevel", Pre = typeof(OptimizerSettings), Condition = "optimizeLargeLevelLoading")]
		[HarmonyPatch(typeof(scrLevelMaker), nameof(scrLevelMaker.MakeLevel))]
		public static class MakeLevelProfiler
		{
			[HarmonyPrefix]
			public static void Prefix()
			{
				MakeLevelWatch.Restart();
			}

			[HarmonyPostfix]
			public static void Postfix()
			{
				Main.Logger?.Log($"[LoadProfiler]   make level post-work: {MakeLevelWatch.ElapsedMilliseconds} ms");
			}
		}

		[IriPatch(Path = "loading/largeLevel", Pre = typeof(OptimizerSettings), Condition = "optimizeLargeLevelLoading")]
		[HarmonyPatch(typeof(scnEditor), nameof(scnEditor.RemakePath), new[] { typeof(bool), typeof(bool) })]
		public static class EditorRemakePathProfiler
		{
			[HarmonyPrefix]
			public static void Prefix()
			{
				RemakePathWatch.Restart();
			}

			[HarmonyPostfix]
			public static void Postfix(scnEditor __instance)
			{
				int floors = __instance != null && __instance.floors != null ? __instance.floors.Count : 0;
				Main.Logger?.Log($"[LoadProfiler] editor RemakePath total: {RemakePathWatch.ElapsedMilliseconds} ms (floors={floors}) | {MemorySnapshot()}");
			}
		}
	}
}
