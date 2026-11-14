using Iridium.Config;
using System.Collections;
using System.Reflection;
using DG.Tweening;
using HarmonyLib;

namespace Iridium.Patches
{
	/// <summary>
	/// DOTween 回收安全补丁：defaultRecyclable = true 时已完成的 Tween 会回到对象池，
	/// 而 ADOFAI 多处存储/遍历 Tween 引用时未检查 IsActive() —— 本文件所有补丁都补上
	/// IsActive() + null 检查。
	/// </summary>
	public static class TweenSafetyPatches
	{
		/// <summary>scrVfxPlus.Reset() 清了 filterTween 却漏了 pausedTweens，stale 引用跨关卡累积。</summary>
		[IriPatch(Path = "optimizer/tweenSafety", Pre = typeof(OptimizerSettings), Condition = "enableOptimizer,dotweenDefaultRecyclable")]
		[HarmonyPatch(typeof(scrVfxPlus), "Reset")]
		public static class ClearPausedTweensOnReset
		{
			[HarmonyPostfix]
			public static void Postfix(scrVfxPlus __instance)
			{
				if (__instance.pausedTweens is { Count: > 0 })
					__instance.pausedTweens.Clear();
			}
		}

		/// <summary>原版 scrDecoration.OnDestroy 遍历 eventTweens 直接 Kill(false)，不做安全检查。</summary>
		[IriPatch(Path = "optimizer/tweenSafety", Pre = typeof(OptimizerSettings), Condition = "enableOptimizer,dotweenDefaultRecyclable")]
		[HarmonyPatch(typeof(scrDecoration), "OnDestroy")]
		public static class SafeDecorationOnDestroy
		{
			[HarmonyPrefix]
			public static bool Prefix(scrDecoration __instance)
			{
				if (__instance.eventTweens != null)
				{
					foreach (var tween in __instance.eventTweens.Values)
					{
						if (tween != null && tween.IsActive())
							tween.Kill(false);
					}
				}

				ffxSetFilterAdvancedPlus.CleanVariables(__instance.gameObject);
				return false;
			}
		}

		/// <summary>原版 ffxPlusBase.Kill 遍历 eventTweens 直接 Kill(false)，null/IsActive 都不检查。</summary>
		[IriPatch(Path = "optimizer/tweenSafety", Pre = typeof(OptimizerSettings), Condition = "enableOptimizer,dotweenDefaultRecyclable")]
		[HarmonyPatch(typeof(ffxPlusBase),nameof(ffxPlusBase.Kill))]
		public static class SafeFfxPlusBaseKill
		{
			private static readonly PropertyInfo EventTweensProp =
				AccessTools.Property(typeof(ffxPlusBase), "eventTweens");

			[HarmonyPrefix]
			public static bool Prefix(ffxPlusBase __instance)
			{
				var tweens = EventTweensProp?.GetValue(__instance, null) as IEnumerable;
				if (tweens != null)
				{
					foreach (Tween t in tweens)
					{
						if (t != null && t.IsActive())
							t.Kill(false);
					}
				}

				return false;
			}
		}

		/// <summary>完整替换原 ScrubToTime，补上 Goto/Kill 前的 IsActive() 检查与 pausedTweens 守卫。</summary>
		[IriPatch(Path = "optimizer/tweenSafety", Pre = typeof(OptimizerSettings), Condition = "enableOptimizer,dotweenDefaultRecyclable")]
		[HarmonyPatch(typeof(ffxPlusBase), nameof(ffxPlusBase.ScrubToTime))]
		public static class SafeFfxPlusBaseScrubToTime
		{
			private static readonly PropertyInfo EventTweensProp =
				AccessTools.Property(typeof(ffxPlusBase), "eventTweens");

			[HarmonyPrefix]
			public static bool Prefix(ffxPlusBase __instance, float t)
			{
				if ((double)t < __instance.startTime)
					return false;

				__instance.StartEffect();
				__instance.triggered = true;

				double endTime = __instance.startTime + __instance.duration;
				bool isAtEnd = UnityEngine.Mathf.Approximately(t, (float)endTime);

				if (ADOBase.currentLevel == "XO-X" && __instance is ffxCameraPlus)
					isAtEnd = true;

				if ((double)t >= endTime || isAtEnd)
				{
					// 超出持续时间：Kill 所有活跃的 Tween
					var tweens = EventTweensProp?.GetValue(__instance, null) as IEnumerable;
					if (tweens != null)
					{
						foreach (Tween tw in tweens)
						{
							if (tw != null && tw.IsActive())
								tw.Kill(true);
						}
					}

					return false;
				}

				// 在持续时间内：Goto 到指定时间 + 加入暂停列表
				float elapsed = (float)((double)t - __instance.startTime);
				var allTweens = EventTweensProp?.GetValue(__instance, null) as IEnumerable;
				if (allTweens != null)
				{
					foreach (Tween tw in allTweens)
					{
						bool isAtZero = UnityEngine.Mathf.Approximately(elapsed, 0f);
						if (tw != null && tw.IsActive())
						{
							tw.Goto(elapsed, true);
						}

						// 只在 tween 活跃时加入暂停列表，防止 stale 引用累积
						if (!isAtZero && tw != null && tw.IsActive())
						{
							if (__instance.vfx is { pausedTweens: not null })
								__instance.vfx.pausedTweens.Add(tw);
						}
					}
				}

				return false;
			}
		}
	}
}
