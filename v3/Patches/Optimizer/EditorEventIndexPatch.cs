using System;
using System.Collections.Generic;
using ADOFAI;
using HarmonyLib;
using Iridium.Config;
using UnityEngine;

namespace Iridium.Patches.Optimizer
{
	/// <summary>
	/// 大物量谱面「选中/切换砖块」卡顿修复。
	///
	/// 根因（3.x）：
	///  - scnEditor.GetFloorEvents(floorID, type) 每次都全表扫描 levelData.levelEvents；
	///    而 inspector 几乎每个 Tab 的 SetSelected 都会调用一次 GetSelectedFloorEvents
	///    （→ GetFloorEvents），60 万事件 × 40+ Tab = 每次点砖块几十次全表扫描。
	///  - ShowEventIndicators 里还有一次 events.FindAll(...) 全表扫描，
	///    DestroyEventIndicators 则用 GameObject.FindGameObjectsWithTag 全场景扫描。
	///
	/// 修复：维护 floorID → events 的惰性索引（ApplyEventsToFloors / 关卡加载后标脏重建），
	/// 并用自跟踪的 indicator 列表替换 FindGameObjectsWithTag。
	/// </summary>
	public static class EditorEventIndexPatch
	{
		private static readonly Dictionary<int, List<LevelEvent>> _byFloor = new();
		private static readonly List<GameObject> _trackedIndicators = new();
		private static bool _dirty = true;
		private static bool _initialTagScanDone;

		/// <summary>事件数据发生变化（编辑、偏移、加载新谱）后调用，下次查询时重建索引。</summary>
		public static void MarkDirty()
		{
			_dirty = true;
		}

		private static void EnsureIndex(scnEditor editor)
		{
			if (!_dirty) return;
			_dirty = false;
			_byFloor.Clear();

			var events = editor != null ? editor.events : null;
			if (events == null) return;

			for (int i = 0; i < events.Count; i++)
			{
				var e = events[i];
				if (e == null) continue;
				if (!_byFloor.TryGetValue(e.floor, out var list))
					_byFloor[e.floor] = list = new List<LevelEvent>();
				list.Add(e);
			}
		}

		private static void DestroyTrackedIndicators(scnEditor editor)
		{
			// 选项中途开启时可能还有旧版本创建的 indicator，只做一次全场景兜底清理。
			if (!_initialTagScanDone)
			{
				_initialTagScanDone = true;
				try
				{
					var leftovers = GameObject.FindGameObjectsWithTag("EventIndicator");
					for (int i = 0; i < leftovers.Length; i++)
						UnityEngine.Object.Destroy(leftovers[i]);
				}
				catch (Exception e)
				{
					Main.Logger?.Log($"[EditorEventIndex] tag scan failed: {e.Message}");
				}
			}

			for (int i = 0; i < _trackedIndicators.Count; i++)
			{
				var go = _trackedIndicators[i];
				if (go != null) UnityEngine.Object.Destroy(go);
			}
			_trackedIndicators.Clear();

			if (editor != null && editor.EventCircle != null)
				editor.EventCircle.gameObject.SetActive(false);
		}

		#region GetFloorEvents / GetSelectedFloorEvents

		[IriPatch(Path = "optimizer/editorFloor", Pre = typeof(OptimizerSettings), Condition = "enableEditorFloorOptimization")]
		[HarmonyPatch(typeof(scnEditor), nameof(scnEditor.GetFloorEvents))]
		public static class GetFloorEventsIndexPatch
		{
			[HarmonyPrefix]
			public static bool Prefix(scnEditor __instance, int floorID, LevelEventType eventType, ref List<LevelEvent> __result)
			{
				if (!Main.Settings.optimizer.enableEditorFloorOptimization) return true;

				if (eventType.IsSetting())
				{
					__result = null!;
					return false;
				}

				try
				{
					EnsureIndex(__instance);
					var result = new List<LevelEvent>();
					if (_byFloor.TryGetValue(floorID, out var bucket))
					{
						for (int i = 0; i < bucket.Count; i++)
							if (bucket[i].eventType == eventType)
								result.Add(bucket[i]);
					}
					__result = result;
					return false;
				}
				catch (Exception e)
				{
					Main.Logger?.Log($"[EditorEventIndex] GetFloorEvents failed: {e.Message}");
					return true;
				}
			}
		}

		#endregion

		#region ShowEventIndicators / DestroyEventIndicators

		/// <summary>
		/// 与原版逻辑一致，但用索引取本砖块事件，并把新建的 indicator 记入跟踪列表，
		/// 避免 FindAll 全表扫描与 FindGameObjectsWithTag 全场景扫描。
		/// </summary>
		[IriPatch(Path = "optimizer/editorFloor", Pre = typeof(OptimizerSettings), Condition = "enableEditorFloorOptimization")]
		[HarmonyPatch(typeof(scnEditor), nameof(scnEditor.ShowEventIndicators))]
		public static class ShowEventIndicatorsIndexPatch
		{
			[HarmonyPrefix]
			public static bool Prefix(scnEditor __instance, scrFloor floor)
			{
				if (!Main.Settings.optimizer.enableEditorFloorOptimization) return true;
				if (floor == null) return true;

				try
				{
					DestroyTrackedIndicators(__instance);
					EnsureIndex(__instance);

					bool flag = false;
					int num = 0;
					if (_byFloor.TryGetValue(floor.seqID, out var bucket))
					{
						for (int i = 0; i < bucket.Count; i++)
						{
							var item = bucket[i];
							if (item == null || !item.ContainsKey("angleOffset")) continue;

							var go = UnityEngine.Object.Instantiate(ADOBase.gc.prefab_eventIndicator, floor.transform);
							go.GetComponent<EventIndicator>().Init(item, floor, num);
							_trackedIndicators.Add(go);
							flag = true;
							num++;
						}
					}

					if (flag && __instance.EventCircle != null)
					{
						var circle = __instance.EventCircle;
						circle.gameObject.SetActive(true);
						circle.fillClockwise = !floor.isCCW;
						circle.transform.rotation = Quaternion.Euler(0f, 0f, (0f - (float)floor.entryangle) * 57.29578f);
						double angleMoved = scrMisc.GetAngleMoved((float)floor.entryangle, (float)floor.exitangle, !floor.isCCW);
						if (Mathf.Abs((float)angleMoved) <= Math.Pow(10f, -6f))
							circle.fillAmount = 1f;
						else
							circle.fillAmount = (float)angleMoved / (float)(Math.PI * 2.0);
					}

					return false;
				}
				catch (Exception e)
				{
					Main.Logger?.Log($"[EditorEventIndex] ShowEventIndicators failed: {e.Message}");
					return true;
				}
			}
		}

		[IriPatch(Path = "optimizer/editorFloor", Pre = typeof(OptimizerSettings), Condition = "enableEditorFloorOptimization")]
		[HarmonyPatch(typeof(scnEditor), "DestroyEventIndicators")]
		public static class DestroyEventIndicatorsFastPatch
		{
			[HarmonyPrefix]
			public static bool Prefix(scnEditor __instance)
			{
				if (!Main.Settings.optimizer.enableEditorFloorOptimization) return true;

				try
				{
					DestroyTrackedIndicators(__instance);
					return false;
				}
				catch (Exception e)
				{
					Main.Logger?.Log($"[EditorEventIndex] DestroyEventIndicators failed: {e.Message}");
					return true;
				}
			}
		}

		#endregion

		#region Dirty Hooks

		[IriPatch(Path = "optimizer/editorFloor", Pre = typeof(OptimizerSettings), Condition = "enableEditorFloorOptimization")]
		[HarmonyPatch(typeof(scnEditor), nameof(scnEditor.ApplyEventsToFloors))]
		public static class EditorApplyDirtyPatch
		{
			[HarmonyPostfix]
			public static void Postfix() => MarkDirty();
		}

		[IriPatch(Path = "optimizer/editorFloor", Pre = typeof(OptimizerSettings), Condition = "enableEditorFloorOptimization")]
		[HarmonyPatch(typeof(scnGame), "ApplyEventsToFloors", new[] { typeof(List<scrFloor>) })]
		public static class GameApplyDirtyPatch
		{
			[HarmonyPostfix]
			public static void Postfix() => MarkDirty();
		}

		[IriPatch(Path = "optimizer/editorFloor", Pre = typeof(OptimizerSettings), Condition = "enableEditorFloorOptimization")]
		[HarmonyPatch(typeof(LevelData), nameof(LevelData.Decode))]
		public static class LevelDecodeDirtyPatch
		{
			[HarmonyPostfix]
			public static void Postfix() => MarkDirty();
		}

		[IriPatch(Path = "optimizer/editorFloor", Pre = typeof(OptimizerSettings), Condition = "enableEditorFloorOptimization")]
		[HarmonyPatch(typeof(scnEditor), "OffsetFloorIDsInEvents")]
		public static class OffsetFloorIdsDirtyPatch
		{
			[HarmonyPostfix]
			public static void Postfix() => MarkDirty();
		}

		#endregion
	}
}
