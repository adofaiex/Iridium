using ADOFAI;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace Iridium.Patches
{
	/// <summary>
	/// 编辑器事件查找 / 搜索的全表扫描消除（自 v3 移植，2.9.8 适配）。
	///
	/// 与 v3 版差异：
	/// - 2.9.8 的 SearchByComment 用 e.data["comment"]（3.x 是 e["comment"]）；
	/// - ShowTabsForFloor / ShowPanelOfEvent 结构与 3.x 一致（已核对反编译源）。
	///
	/// 实现：
	/// - ShowTabsForFloor/ShowPanelOfEvent 用帧级 floor→events 索引；
	/// - SearchByComment 重写为手写循环 + 按 LevelEvent 缓存小写 comment
	///   （CWT，评论字符串引用变化时重算），Distinct + orderby 语义等价复刻。
	/// </summary>
	public static class EditorSearchOptimizationPatch
	{
		// ---- 帧级 floor→events 索引 ----

		private static int _indexFrame = -1;
		private static int _indexCount = -1;
		private static scnEditor _indexEditor;
		private static readonly Dictionary<int, List<LevelEvent>> _byFloor = new();

		private static Dictionary<int, List<LevelEvent>> EnsureIndex(scnEditor editor)
		{
			var events = editor != null ? editor.events : null;
			int frame = Time.frameCount;
			int count = events != null ? events.Count : -1;
			if (_indexFrame != frame || _indexCount != count || !ReferenceEquals(_indexEditor, editor))
			{
				_byFloor.Clear();
				if (events != null)
				{
					for (int i = 0; i < events.Count; i++)
					{
						var e = events[i];
						if (e == null) continue;
						if (!_byFloor.TryGetValue(e.floor, out var list))
							_byFloor[e.floor] = list = new List<LevelEvent>();
						list.Add(e); // 保持原数组顺序
					}
				}
				_indexFrame = frame;
				_indexCount = count;
				_indexEditor = editor;
			}
			return _byFloor;
		}

		// ShowTabsForFloor 复刻需要的私有成员：缓存反射访问器
		private static readonly Func<InspectorPanel, string, bool, bool> _modifyMessageText =
			AccessTools.MethodDelegate<Func<InspectorPanel, string, bool, bool>>(
				AccessTools.Method(typeof(InspectorPanel), "ModifyMessageText",
					new[] { typeof(string), typeof(bool) }));
		private static readonly Func<InspectorPanel, string, float, bool, bool> _modifyMessageTextY =
			AccessTools.MethodDelegate<Func<InspectorPanel, string, float, bool, bool>>(
				AccessTools.Method(typeof(InspectorPanel), "ModifyMessageText",
					new[] { typeof(string), typeof(float), typeof(bool) }));

		private static LevelEventType GetCacheSelected()
		{
			// cacheSelectedEventType 是 private static 字段，直接反射读取
			var f = _cacheSelectedField ??= AccessTools.Field(typeof(InspectorPanel), "cacheSelectedEventType");
			return (LevelEventType)f.GetValue(null);
		}

		private static FieldInfo _cacheSelectedField;

		[HarmonyPatch(typeof(InspectorPanel), "ShowTabsForFloor")]
		public static class ShowTabsForFloorPatch
		{
			[HarmonyPrefix]
			public static bool Prefix(InspectorPanel __instance, int floorID)
			{
				try
				{
					var byFloor = EnsureIndex(ADOBase.editor);
					if (!_byFloor.TryGetValue(floorID, out var floorEvents))
						floorEvents = null;

					var list = new List<LevelEventType>();
					if (floorEvents != null)
						for (int i = 0; i < floorEvents.Count; i++)
							list.Add(floorEvents[i].eventType);

					// ── 以下为原版逻辑的忠实复刻（仅事件收集走索引） ──
					__instance.titleCanvas.SetActive(list.Count > 0);
					_modifyMessageText(__instance, "", false);
					if (list.Count == 0)
					{
						__instance.ShowPanel(LevelEventType.None);
						_modifyMessageTextY(__instance, RDString.Get("editor.dialog.noEventsOnTile"), 0f, true);
						ADOBase.editor.DeselectAllDecorations();
					}
					else
					{
						LevelEventType levelEventType = LevelEventType.None;
						bool found = false;
						LevelEventType cached = GetCacheSelected();
						foreach (var t in list)
						{
							if (t == cached)
							{
								levelEventType = t;
								found = true;
								break;
							}
						}
						if (!found)
						{
							// 原版按枚举定义顺序取 list 中出现的第一个类型
							Array values = Enum.GetValues(typeof(LevelEventType));
							foreach (LevelEventType v in values)
							{
								foreach (var t in list)
								{
									if (t == v)
									{
										levelEventType = t;
										break;
									}
								}
								if (levelEventType != LevelEventType.None)
									break;
							}
						}
						__instance.selectedEventType = LevelEventType.None;
						if (levelEventType != LevelEventType.AddDecoration && levelEventType != LevelEventType.AddText)
						{
							ADOBase.editor.DeselectAllDecorations();
						}
						__instance.ShowPanel(levelEventType);
						__instance.ShowInspector(show: true);
					}

					// ── tab 可见性与布局（原版尾部逻辑） ──
					var names = new List<string>();
					foreach (var t in list)
					{
						string name = t.ToString();
						if (!names.Contains(name))
							names.Add(name);
					}
					int count = names.Count;
					float height = __instance.tabs.rect.height;
					float spacing = 68f;
					if ((float)count * 68f >= height)
					{
						float adjust = (height - 68f * (float)count) / (float)(count * count);
						spacing = height / (float)count + adjust;
					}
					int shown = -1;
					foreach (Transform tab in __instance.tabs)
					{
						bool visible = names.Contains(tab.name);
						tab.gameObject.SetActive(visible);
						if (visible)
						{
							shown++;
							names.Remove(tab.name);
						}
						float y = (0f - spacing) * (float)shown;
						tab.GetComponent<RectTransform>().SetAnchorPosY(y);
					}
					return false;
				}
				catch (Exception)
				{
					return true;
				}
			}
		}

		[HarmonyPatch(typeof(InspectorPanel), "ShowPanelOfEvent")]
		public static class ShowPanelOfEventPatch
		{
			[HarmonyPrefix]
			public static bool Prefix(InspectorPanel __instance, LevelEvent evnt)
			{
				try
				{
					var floors = ADOBase.editor.selectedFloors;
					if (floors == null || floors.Count == 0 || evnt == null)
						return true;

					int seqID = floors[0].seqID;
					var byFloor = EnsureIndex(ADOBase.editor);
					int num = 0;
					if (byFloor.TryGetValue(seqID, out var floorEvents))
					{
						for (int i = 0; i < floorEvents.Count; i++)
						{
							var e = floorEvents[i];
							if (e.eventType == evnt.eventType)
							{
								if (ReferenceEquals(e, evnt))
									break;
								num++;
							}
						}
					}
					__instance.ShowPanel(evnt.eventType, num);
					return false;
				}
				catch (Exception)
				{
					return true;
				}
			}
		}

		// ---- SearchByComment ----

		private sealed class CommentLowerState
		{
			public string RawRef;
			public string Lower;
		}

		private static readonly ConditionalWeakTable<LevelEvent, CommentLowerState> _commentCache = new();

		// 2.9.8 的 comment 存在 e.data["comment"]（3.x 是 e["comment"]）
		private static string GetLowerComment(LevelEvent e)
		{
			string raw = e.data != null ? e.data["comment"] as string : null;
			if (raw == null)
				return null;
			var state = _commentCache.GetOrCreateValue(e);
			if (!ReferenceEquals(state.RawRef, raw))
			{
				state.RawRef = raw;
				state.Lower = raw.ToLowerInvariant();
			}
			return state.Lower;
		}

		[HarmonyPatch(typeof(scnEditor), nameof(scnEditor.SearchByComment))]
		public static class SearchByCommentPatch
		{
			[HarmonyPrefix]
			public static bool Prefix(scnEditor __instance, string text, ref int[] __result)
			{
				try
				{
					var events = __instance.levelData != null ? __instance.levelData.levelEvents : null;
					if (events == null)
						return true;

					if (text == null)
						text = "";
					text = text.ToLowerInvariant();

					var seen = new HashSet<int>();
					var floors = new List<int>();
					for (int i = 0; i < events.Count; i++)
					{
						var e = events[i];
						if (e == null || e.eventType != LevelEventType.EditorComment || !e.active)
							continue;
						string lower = GetLowerComment(e);
						if (lower != null && lower.Contains(text) && seen.Add(e.floor))
							floors.Add(e.floor);
					}

					floors.Sort(); // orderby i 升序
					__result = floors.ToArray();
					return false;
				}
				catch (Exception)
				{
					return true;
				}
			}
		}
	}
}
