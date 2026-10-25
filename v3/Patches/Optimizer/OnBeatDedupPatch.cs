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
		[HarmonyPrefix]
		public static bool Prefix(scrConductor __instance)
		{
			try
			{
				bool gameworld = ADOBase.controller != null && ADOBase.controller.gameworld;

				List<ADOBase> onBeats = __instance.onBeats;
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

					// gameworld 时砖块的 OnBeat 交给下面的 listFloors 循环，只跑一次
					if (!(gameworld && onBeats[i] is scrFloor))
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
