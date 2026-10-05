using HarmonyLib;
using Iridium.Config;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Iridium.Patches.Optimizer
{
	/// <summary>
	/// 每拍 OnBeat 去重。
	///
	/// 原版 scrConductor.PropagateOnBeat 会把所有砖块执行**两遍** OnBeat：
	/// scrFloor.Awake 把自己注册进 conductor.onBeats（第一循环遍历 onBeats 调一遍），
	/// 随后 gameworld 时又对 ADOBase.lm.listFloors 整表调第二遍。
	/// 15 万砖 × 200+ BPM 下，这是每秒上百万次冗余的视觉/材质写入。
	///
	/// 本补丁在 gameworld 分支里让第一循环跳过 scrFloor 类型的监听者
	/// （由第二循环统一执行一次），其他监听者（scrConductor、scrACGear、
	/// scrPulseOnBeat 等场景对象）保持原行为。同时保留第一循环的 null 清理，
	/// 否则 freeroam 中销毁的砖会在 onBeats 里越积越多。
	///
	/// 语义差异：仅砖块 OnBeat 与其他监听者 OnBeat 的相对执行顺序改变
	/// （砖块统一移到第二循环）；双方都只做视觉/随机类工作，无 gameplay 影响。
	/// 3.3.0 与 3.4.0 的该方法与字段完全一致（已核对两版反编译源）。
	/// </summary>
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
