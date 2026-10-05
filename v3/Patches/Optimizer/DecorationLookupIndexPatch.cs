using ADOFAI;
using HarmonyLib;
using Iridium.Config;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace Iridium.Patches.Optimizer
{
	/// <summary>
	/// 装饰物按 LevelEvent 查找的 O(D²) 消除。
	///
	/// 原版 scrDecorationManager.GetDecoration(LevelEvent) 与
	/// GetDecorationIndex(LevelEvent) 都是对 allDecorations 的 O(D) 线性扫描
	/// （Find / foreach），而装饰列表搜索
	/// （PropertyControl_DecorationsList.FilterSearchResults）对**每个装饰**
	/// 各调一次 GetDecoration，整体 O(D²)——10 万装饰时搜索框每敲一个字符
	/// 可冻结数十秒。
	///
	/// 实现：按实例的帧级缓存索引（CWT 挂在 scrDecorationManager 上）。
	/// - 首次调用或新的一帧（Time.frameCount 变化）或列表计数变化时重建
	///   Dictionary（O(D) 一次），同帧后续调用 O(1)。
	/// - 语义对齐原版：保持"首个匹配优先"（后出现的重复事件不覆盖首项）、
	///   Index 找不到返回 -1、GetDecoration 找不到返回 null。
	/// - 同帧内修改列表属于极端情况；Count 不一致会触发立即重建兜底，
	///   同 Count 的重排极罕见且只在当前帧内短暂过时，下一帧自动纠正。
	///
	/// 与现有 EditorEventIndexPatch（floorID→events）互不重叠。
	/// 3.3.0 与 3.4.0 的方法与 sourceLevelEvent 字段一致（已核对两版源码）。
	/// </summary>
	public static class DecorationLookupIndexPatch
	{
		private sealed class LookupState
		{
			public int Frame = -1;
			public int Count = -1;
			public readonly Dictionary<LevelEvent, scrDecoration> Map = new();
			public readonly Dictionary<LevelEvent, int> Positions = new();
		}

		private static readonly ConditionalWeakTable<scrDecorationManager, LookupState> _states = new();

		private static LookupState GetState(scrDecorationManager manager, List<scrDecoration> list)
		{
			var state = _states.GetOrCreateValue(manager);
			int frame = Time.frameCount;
			if (state.Frame != frame || state.Count != list.Count)
			{
				state.Map.Clear();
				state.Positions.Clear();
				for (int i = 0; i < list.Count; i++)
				{
					var dec = list[i];
					if (dec == null || dec.sourceLevelEvent == null)
						continue;
					// 原版 Find/IndexOf 语义：首次出现优先
					if (!state.Map.ContainsKey(dec.sourceLevelEvent))
					{
						state.Map[dec.sourceLevelEvent] = dec;
						state.Positions[dec.sourceLevelEvent] = i;
					}
				}
				state.Frame = frame;
				state.Count = list.Count;
			}
			return state;
		}

		[IriPatch(Path = "optimizer/editorPerf", Pre = typeof(OptimizerSettings), Condition = "optimizeEditorInteractions")]
		[HarmonyPatch(typeof(scrDecorationManager), nameof(scrDecorationManager.GetDecoration),
			new[] { typeof(LevelEvent) })]
		public static class GetDecorationPatch
		{
			[HarmonyPrefix]
			public static bool Prefix(scrDecorationManager __instance, LevelEvent source,
				ref scrDecoration __result)
			{
				try
				{
					var list = __instance.allDecorations;
					if (list == null)
						return true;

					if (source == null)
					{
						__result = null; // 原版 Find 对 null source 也返回 null（无装饰的 sourceLevelEvent 为 null 之外都跳过）
						return false;
					}

					__result = GetState(__instance, list).Map.TryGetValue(source, out var dec) ? dec : null;
					return false;
				}
				catch (Exception)
				{
					return true;
				}
			}
		}

		[IriPatch(Path = "optimizer/editorPerf", Pre = typeof(OptimizerSettings), Condition = "optimizeEditorInteractions")]
		[HarmonyPatch(typeof(scrDecorationManager), nameof(scrDecorationManager.GetDecorationIndex))]
		public static class GetDecorationIndexPatch
		{
			[HarmonyPrefix]
			public static bool Prefix(scrDecorationManager __instance, LevelEvent dec, ref int __result)
			{
				try
				{
					var list = __instance.allDecorations;
					if (list == null)
						return true;

					if (dec == null)
					{
						__result = -1;
						return false;
					}

					__result = GetState(__instance, list).Positions.TryGetValue(dec, out int pos) ? pos : -1;
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
