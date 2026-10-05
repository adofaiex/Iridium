using ADOFAI;
using HarmonyLib;
using Iridium.Config;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace Iridium.Patches.Optimizer
{
	/// <summary>
	/// 编辑器事件查找 / 搜索的全表扫描消除。
	///
	/// 原版三个高频路径都对全谱事件（可达 60 万）做线性扫描：
	/// 1. InspectorPanel.ShowTabsForFloor：选砖时 foreach 全部事件收集
	///    floor==floorID 的类型列表；
	/// 2. InspectorPanel.ShowPanelOfEvent：打开事件面板时再扫一遍统计序号；
	/// 3. scnEditor.SearchByComment：评论搜索对全部事件做
	///    ToLowerInvariant()（每个事件分配一个小写串）+ Contains。
	///
	/// 实现：
	/// - 1/2 用帧级 floor→events 索引（与现有 EditorEventIndexPatch 独立，
	///   因为开关不同；帧级缓存对"每次选中才调用"的面板路径足够安全，
	///   同帧内编辑事件属极端情况，下一帧自动纠正）；
	/// - 3 重写为手写循环：只对 EditorComment 事件（通常极少）做小写化，
	///   小写结果按事件缓存（CWT，评论字符串引用变化时重算），
	///   Distinct + orderby 语义用 HashSet + Array.Sort 等价复刻。
	///
	/// 3.3.0 与 3.4.0 的三个方法结构一致（已核对两版反编译源）。
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
						list.Add(e); // 保持原数组顺序，ShowTabsForFloor 依赖顺序
					}
				}
				_indexFrame = frame;
				_indexCount = count;
				_indexEditor = editor;
			}
			return _byFloor;
		}

		// ---- 1. ShowTabsForFloor ----

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
			// cacheSelectedEventType 是 private static 字段，直接反射读取（值低频变化，可接受）
			var f = _cacheSelectedField ??= AccessTools.Field(typeof(InspectorPanel), "cacheSelectedEventType");
			return (LevelEventType)f.GetValue(null);
		}

		private static FieldInfo _cacheSelectedField;

		[IriPatch(Path = "optimizer/editorPerf", Pre = typeof(OptimizerSettings), Condition = "optimizeEditorInteractions")]
		[HarmonyPatch(typeof(InspectorPanel), nameof(InspectorPanel.ShowTabsForFloor))]
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

					// ── tab 可见性与布局（原版尾部逻辑，成员均为 public） ──
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

		// ---- 2. ShowPanelOfEvent ----

		[IriPatch(Path = "optimizer/editorPerf", Pre = typeof(OptimizerSettings), Condition = "optimizeEditorInteractions")]
		[HarmonyPatch(typeof(InspectorPanel), nameof(InspectorPanel.ShowPanelOfEvent))]
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

		// ---- 3. SearchByComment ----

		private sealed class CommentLowerState
		{
			public string RawRef;
			public string Lower;
		}

		// 按事件缓存小写 comment；评论被编辑时是新的字符串实例（引用不同）→ 重算
		private static readonly ConditionalWeakTable<LevelEvent, CommentLowerState> _commentCache = new();

		private static string GetLowerComment(LevelEvent e)
		{
			string raw = e["comment"] as string;
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

		[IriPatch(Path = "optimizer/editorPerf", Pre = typeof(OptimizerSettings), Condition = "optimizeEditorInteractions")]
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

					// 收集匹配事件的 floor（保持原版 Distinct 语义：首次出现优先）
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

					// orderby i 升序
					floors.Sort();
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
