using ADOFAI;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace Iridium.Patches
{
	/// <summary>
/// Replaces the O(D) linear GetDecoration(LevelEvent) scan with a per-frame index,
/// turning decoration list search from O(D²) into one O(D) rebuild. First match
/// wins; the Index variant still returns -1 when nothing matches.
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

		[HarmonyPatch(typeof(scrDecorationManager), "GetDecoration", new[] { typeof(LevelEvent) })]
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
						__result = null;
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

		[HarmonyPatch(typeof(scrDecorationManager), "GetDecorationIndex")]
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
