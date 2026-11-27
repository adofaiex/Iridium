using ADOFAI;
using HarmonyLib;
using Iridium.Config;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace Iridium.Patches.Optimizer
{
	/// <summary>Turns the decoration-list search from O(D²) into a per-frame LevelEvent→decoration lookup; first match wins, missing lookups still give null / -1.</summary>
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
			// GetDecoration 是 static 方法（读 scrDecorationManager.instance），Harmony 给 static 方法的
			// __instance 恒为 null，旧写法每次调用都抛 NRE 再回退原版，既没加速还多一次异常开销。
			public static bool Prefix(LevelEvent source, ref scrDecoration __result)
			{
				try
				{
					var manager = scrDecorationManager.instance;
					var list = manager != null ? manager.allDecorations : null;
					if (list == null || source == null)
						return true;

					// 只接受"校验过的命中"：缓存按 (帧, Count) 失效，同帧内 Remove+Insert 重排不会改 Count，
					// 所以命中后核对 list[pos] 仍是该装饰物；未命中或校验失败一律交还原版线性查找。
					var state = GetState(manager!, list);
					if (state.Map.TryGetValue(source, out var dec)
						&& state.Positions.TryGetValue(source, out int pos)
						&& pos < list.Count && ReferenceEquals(list[pos], dec))
					{
						__result = dec;
						return false;
					}
					return true;
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
			// static 方法，见 GetDecorationPatch 的说明：用 scrDecorationManager.instance，不能用 __instance。
			public static bool Prefix(LevelEvent dec, ref int __result)
			{
				try
				{
					var manager = scrDecorationManager.instance;
					var list = manager != null ? manager.allDecorations : null;
					if (list == null || dec == null)
						return true;

					var state = GetState(manager!, list);
					if (state.Positions.TryGetValue(dec, out int pos)
						&& pos < list.Count && list[pos] != null && list[pos].sourceLevelEvent == dec)
					{
						__result = pos;
						return false;
					}
					return true;
				}
				catch (Exception)
				{
					return true;
				}
			}
		}
	}
}
