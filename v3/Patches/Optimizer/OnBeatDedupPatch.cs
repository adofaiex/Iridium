using HarmonyLib;
using Iridium.Config;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Iridium.Patches.Optimizer
{
	/// <summary>Runs scrFloor.OnBeat once per beat in gameworld; other onBeats listeners are untouched, so only floor/listener ordering changes.</summary>
	[IriPatch(Path = "optimizer/onBeat", Pre = typeof(OptimizerSettings), Condition = "optimizeOnBeat")]
	[HarmonyPatch(typeof(scrConductor), nameof(scrConductor.PropagateOnBeat))]
	public static class OnBeatDedupPatch
	{
		/// <summary>该砖块是否就是 listFloors[seqID]（freeroam 子砖块的 seqID 指向母砖块，不算）。</summary>
		private static bool IsInList(List<scrFloor> list, scrFloor f)
		{
			int id = f.seqID;
			return id >= 0 && id < list.Count && ReferenceEquals(list[id], f);
		}

		[HarmonyPrefix]
		public static bool Prefix(scrConductor __instance)
		{
			try
			{
				bool gameworld = ADOBase.controller != null && ADOBase.controller.gameworld;

				List<ADOBase> onBeats = __instance.onBeats;
				List<scrFloor>? beatFloors = gameworld ? ADOBase.lm.listFloors : null;
				int count = onBeats.Count;
				int i = 0;
				while (i < count)
				{
					if (onBeats[i] == null)
					{
						onBeats.RemoveAt(i);
						count--;
						continue;
					}

					// gameworld 时 listFloors 里的砖块交给下面的循环，只跑一次。
					// freeroam 生成的子砖块也在 onBeats 里，但不在 listFloors 里（seqID 与母砖块相同），
					// 必须仍由这里调用，否则它们永远收不到 OnBeat。
					if (!(beatFloors != null && onBeats[i] is scrFloor f && IsInList(beatFloors, f)))
					{
						onBeats[i].OnBeat();
					}
					i++;
				}

				if (gameworld)
				{
					List<scrFloor> listFloors = ADOBase.lm.listFloors;
					int floorCount = listFloors.Count;
					for (int j = 0; j < floorCount; j++)
					{
						listFloors[j].OnBeat();
					}
				}

				__instance.onBeatFrame = Time.frameCount;
				return false;
			}
			catch (Exception)
			{
				// 任何异常都回退原版，保证 OnBeat 传播不丢
				return true;
			}
		}
	}
}
