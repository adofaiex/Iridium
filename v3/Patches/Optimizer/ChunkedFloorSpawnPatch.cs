using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using ADOFAI;
using HarmonyLib;
using Iridium.Config;
using UnityEngine;

namespace Iridium.Patches.Optimizer
{
	/// <summary>Spawns huge levels with the meshFloor prefab deactivated, fast-initialises each clone, then activates them spread over frames under a time budget.</summary>
	public static class ChunkedFloorSpawnPatch
	{
		private const int FloorThreshold = 20000;
		private const float FrameBudgetMs = 25f;

		private static readonly ConditionalWeakTable<scrFloor, object> _initialized = new();
		private static readonly Queue<scrFloor> _pending = new();
		private static readonly List<ffxPlusBase> _effectBuffer = new();
		private static readonly List<bool[]?> _condBuffer = new();
		private static readonly Stopwatch _watch = new();

		private static AccessTools.FieldRef<scrLevelMaker, GameObject>? _meshFloorRef;
		private static bool _meshFloorRefFailed;

		private static bool _bulkActive;
		private static bool _savedPrefabActive;
		private static GameObject? _savedPrefab;
		private static GameObject? _savedFieldPrefab;
		private static bool _bulkQueued;
		private static int _bulkTotal;
		private static int _bulkActivated;
		private static readonly Stopwatch _progressWatch = new();
		private static readonly Stopwatch _fastInitWatch = new();

		internal static bool Enabled =>
			Main.Settings.optimizer.optimizeLargeLevelLoading
			&& Main.Settings.optimizer.chunkedFloorSpawn;

		internal static bool IsInitialized(scrFloor floor)
			=> floor != null && _initialized.TryGetValue(floor, out _);

		internal static void MarkInitialized(scrFloor floor)
		{
			if (floor == null) return;
			_initialized.Remove(floor);
			_initialized.Add(floor, _initialized);
		}

		/// <summary>原版 scrFloor.Reset 会直接再调 Awake()，必须先清标记让初始化重跑。</summary>
		internal static void UnmarkInitialized(scrFloor floor)
		{
			if (floor == null) return;
			_initialized.Remove(floor);
		}

		#region InstantiateFloatFloors hook

		[IriPatch(Path = "loading/largeLevel", Pre = typeof(OptimizerSettings), Condition = "optimizeLargeLevelLoading,chunkedFloorSpawn")]
		[HarmonyPatch(typeof(scrLevelMaker), nameof(scrLevelMaker.InstantiateFloatFloors))]
		public static class InstantiateFloorsChunkedPatch
		{
			[HarmonyPrefix]
			public static bool Prefix(scrLevelMaker __instance)
			{
				// Always drain the previous batch first: after this, every inactive floor is a clone made by this call.
				if (_pending.Count > 0) FlushAll();

				if (!Enabled) return true;
				if (!Application.isPlaying) return true;
				if (!ADOBase.isLevelEditor && !ADOBase.isScnGame) return true;
				if (__instance.isOldLevel) return true;
				if (EditorFloorOptimizationPatches.IncrementalActive) return true;
				if (__instance.floorAngles == null || __instance.floorAngles.Length < FloorThreshold) return true;

				// 精灵砖块路径使用别的 prefab，不走本优化
				var existing = __instance.listFloors;
				if (existing != null && existing.Count > 0 && existing[0] != null
					&& existing[0].GetComponent<FloorSpriteRenderer>() != null)
					return true;

				if (_meshFloorRef == null && !_meshFloorRefFailed)
				{
					try { _meshFloorRef = AccessTools.FieldRefAccess<scrLevelMaker, GameObject>("meshFloor"); }
					catch (Exception e)
					{
						_meshFloorRefFailed = true;
						Main.Logger?.Log($"[ChunkedSpawn] meshFloor ref failed: {e.Message}");
					}
				}
				if (_meshFloorRef == null) return true;

				try
				{
					var originalPrefab = _meshFloorRef(__instance);
					if (originalPrefab == null) return true;

					// Prefer the number-stripped template; deactivating it keeps clones inactive so Awake/OnEnable/Start are deferred.
					var template = LazyFloorNumberPatch.EnsureStrippedTemplate(__instance) ?? originalPrefab;

					_savedPrefab = template;
					_savedFieldPrefab = originalPrefab;
					_savedPrefabActive = template.activeSelf;
					_bulkActive = true;
					if (!ReferenceEquals(originalPrefab, template))
						_meshFloorRef(__instance) = template;
					template.SetActive(false);
					return true;
				}
				catch (Exception e)
				{
					Main.Logger?.Log($"[ChunkedSpawn] prefix failed: {e}");
					RestorePrefab(__instance);
					return true;
				}
			}

			[HarmonyPostfix]
			public static void Postfix(scrLevelMaker __instance)
			{
				if (!_bulkActive) return;
				try
				{
					var floors = __instance.listFloors;
					if (floors != null)
					{
						int enqueuedCount = 0;
						_fastInitWatch.Restart();
						// 前缀已 FlushAll：这里所有「非激活」砖块一定是本次新建的克隆。
						for (int i = 0; i < floors.Count; i++)
						{
							var floor = floors[i];
							if (floor == null || floor.gameObject.activeSelf) continue;
							if (LazyTopGlowPatch.TryInitialize(floor, callComponentAwake: true))
							{
								MarkInitialized(floor);
								_pending.Enqueue(floor);
								enqueuedCount++;
							}
							else
							{
								// FastInit 失败：立即激活走原版生命周期，别把砖块卡在未激活状态
								ActivateFloor(floor, restoreConditionalInfo: false);
							}
						}
						if (enqueuedCount > 0)
						{
							_bulkQueued = true;
							_bulkTotal = enqueuedCount;
							_bulkActivated = 0;
							_progressWatch.Restart();
							Main.Logger?.Log($"[ChunkedSpawn] fastinit: {_fastInitWatch.ElapsedMilliseconds} ms (floors={enqueuedCount})");
						}
					}
				}
				catch (Exception e)
				{
					Main.Logger?.Log($"[ChunkedSpawn] postfix failed: {e}");
					FlushAll();
				}
				finally
				{
					RestorePrefab(__instance);
				}
			}

			[HarmonyFinalizer]
			public static Exception? Finalizer(scrLevelMaker __instance, Exception? __exception)
			{
				RestorePrefab(__instance);
				return __exception;
			}

			private static void RestorePrefab(scrLevelMaker __instance)
			{
				if (!_bulkActive) return;
				_bulkActive = false;
				try
				{
					// 先还原字段，再恢复被我们反激活的 prefab 引用本身。
					if (_savedFieldPrefab != null && _meshFloorRef != null)
						_meshFloorRef(__instance) = _savedFieldPrefab;
					if (_savedPrefab != null)
						_savedPrefab.SetActive(_savedPrefabActive);
				}
				catch (Exception e)
				{
					Main.Logger?.Log($"[ChunkedSpawn] restore prefab failed: {e.Message}");
				}
				finally
				{
					_savedPrefab = null;
					_savedFieldPrefab = null;
				}
			}
		}

		#endregion

		#region Activation driver

		/// <summary>Called every frame from Main.OnUpdate; activates as many queued floors as the frame budget allows.</summary>
		public static void Tick()
		{
			if (_pending.Count == 0)
			{
				FinishBulkIfDone();
				return;
			}
			if (!Enabled)
			{
				FlushAll();
				FinishBulkIfDone();
				return;
			}

			_watch.Restart();
			while (_pending.Count > 0)
			{
				var floor = _pending.Dequeue();
				if (floor == null) continue;
				_bulkActivated++;
				ActivateFloor(floor, restoreConditionalInfo: true);
				if (_watch.Elapsed.TotalMilliseconds >= FrameBudgetMs) break;
			}
			_watch.Stop();
			if (_progressWatch.ElapsedMilliseconds >= 2000 && _pending.Count > 0)
			{
				_progressWatch.Restart();
				int pct = _bulkTotal > 0 ? (int)(_bulkActivated * 100L / _bulkTotal) : 0;
				Main.Logger?.Log(
					$"[ChunkedSpawn] activating {_bulkActivated}/{_bulkTotal} ({pct}%) | {LoadProfilerPatch.MemorySnapshot()}");
			}
			FinishBulkIfDone();
		}

		/// <summary>Log totals once the batch drains; deliberately no forced GC (long pause on big heaps).</summary>
		private static void FinishBulkIfDone()
		{
			if (!_bulkQueued || _pending.Count > 0) return;
			_bulkQueued = false;
			try
			{
				Main.Logger?.Log(
					$"[ChunkedSpawn] activation done ({_bulkActivated}/{_bulkTotal}) | {LoadProfilerPatch.MemorySnapshot()}");
			}
			catch (Exception e)
			{
				Main.Logger?.Log($"[ChunkedSpawn] activation done log failed: {e.Message}");
			}
		}

		/// <summary>Activate every queued floor now; called before gameplay so no floor stays inactive.</summary>
		public static void FlushAll()
		{
			while (_pending.Count > 0)
			{
				var floor = _pending.Dequeue();
				if (floor == null) continue;
				ActivateFloor(floor, restoreConditionalInfo: true);
			}
		}

		private static void ActivateFloor(scrFloor floor, bool restoreConditionalInfo)
		{
			try
			{
				if (floor == null) return;
				if (floor.gameObject.activeSelf)
				{
					MarkInitialized(floor);
					return;
				}

				if (restoreConditionalInfo)
				{
					_effectBuffer.Clear();
					_condBuffer.Clear();
					var effects = floor.plusEffects;
					if (effects != null)
					{
						for (int i = 0; i < effects.Count; i++)
						{
							var effect = effects[i];
							if (effect == null) continue;
							_effectBuffer.Add(effect);
							_condBuffer.Add(effect.conditionalInfo);
						}
					}

					floor.gameObject.SetActive(true);

					// ffxPlusBase.Awake normalizes conditionalInfo; the pipeline-written value must win.
					for (int i = 0; i < _effectBuffer.Count && i < _condBuffer.Count; i++)
					{
						var snapshot = _condBuffer[i];
						if (snapshot != null) _effectBuffer[i].conditionalInfo = snapshot;
					}
				}
				else
				{
					floor.gameObject.SetActive(true);
				}
				MarkInitialized(floor);
			}
			catch (Exception e)
			{
				Main.Logger?.Log($"[ChunkedSpawn] activate failed: {e.Message}");
			}
		}

		#endregion

		#region Deferred-Awake compensations

		/// <summary>ffxCheckpoint.Decode 读取基类 floor（Awake 才设置）。</summary>
		[IriPatch(Path = "loading/largeLevel", Pre = typeof(OptimizerSettings), Condition = "optimizeLargeLevelLoading,chunkedFloorSpawn")]
		[HarmonyPatch(typeof(ffxCheckpoint), nameof(ffxCheckpoint.Decode))]
		public static class CheckpointDecodePatch
		{
			[HarmonyPrefix]
			public static void Prefix(ffxCheckpoint __instance)
			{
				if (!Enabled || __instance == null || __instance.floor != null) return;
				__instance.floor = __instance.GetComponent<scrFloor>();
			}
		}

		/// <summary>ffxSetHitsound.Decode 写入 floor.setHitsound。</summary>
		[IriPatch(Path = "loading/largeLevel", Pre = typeof(OptimizerSettings), Condition = "optimizeLargeLevelLoading,chunkedFloorSpawn")]
		[HarmonyPatch(typeof(ffxSetHitsound), nameof(ffxSetHitsound.Decode))]
		public static class SetHitsoundDecodePatch
		{
			[HarmonyPrefix]
			public static void Prefix(ffxSetHitsound __instance)
			{
				if (!Enabled || __instance == null || __instance.floor != null) return;
				__instance.floor = __instance.GetComponent<scrFloor>();
			}
		}

		#endregion

		#region Flush before gameplay

		[IriPatch(Path = "loading/largeLevel", Pre = typeof(OptimizerSettings), Condition = "optimizeLargeLevelLoading,chunkedFloorSpawn")]
		[HarmonyPatch(typeof(scnEditor), nameof(scnEditor.Play))]
		public static class EditorPlayFlushPatch
		{
			[HarmonyPrefix]
			public static void Prefix() => FlushAll();
		}

		[IriPatch(Path = "loading/largeLevel", Pre = typeof(OptimizerSettings), Condition = "optimizeLargeLevelLoading,chunkedFloorSpawn")]
		[HarmonyPatch(typeof(scnGame), nameof(scnGame.FinishCustomLevelLoading))]
		public static class FinishLoadingFlushPatch
		{
			[HarmonyPrefix]
			public static void Prefix() => FlushAll();
		}

		#endregion
	}
}
