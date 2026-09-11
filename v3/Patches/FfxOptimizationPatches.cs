using Iridium.Config;
using Iridium.Core;
using Iridium.Patches.Optimizer;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;
using DG.Tweening;

namespace Iridium.Patches
{
	public static class FfxOptimizationPatches
	{
		private sealed class DecoState
		{
			public bool WasVisible = true;
			public float LastRotAngle;
			public Vector2 LastScaleVec = Vector2.zero;
		}

		private static readonly ConditionalWeakTable<scrDecoration, DecoState> _states = new();

		/// <summary>
		/// 判定是否需要本帧 LogicUpdate。
		/// 跳过的只有"持续不可见且无判定盒"的装饰物 —— 原版对它们的 LogicUpdate
		/// 同样做不了有用功（UpdatePosition 仅可见时执行，UpdateShader 只做隐藏收尾）。
		/// 可见性过渡帧会更新一次，保证隐藏渲染器收尾被执行。
		/// </summary>
		private static bool NeedsLogicUpdate(scrDecoration dec, DecoState state)
		{
			bool visible = dec.GetVisible();
			bool needs = visible || state.WasVisible || dec.useHitbox;
			state.WasVisible = visible;
			return needs;
		}

		/// <summary>
		/// 位置静止判定：无任何活跃 tween（引擎 + 原版 DOTween）、无跟随/吸附、
		/// 视差乘数为 1（无相机位移）、旋转与缩放字段与上帧相同。
		/// 满足时跳过 UpdatePosition（其每帧的 transform 写入会持续弄脏
		/// 变换层级、破坏合批），改为直接执行 UpdateShader/UpdateHitboxState。
		/// </summary>
		private static bool IsPositionStatic(scrDecoration dec, DecoState state)
		{
			if (dec.stickToFloor || dec.followPlanet != null) return false;
			if (dec.parallax == null) return false;
			if (dec.parallax.multiplier.x != 1f || dec.parallax.multiplier.y != 1f) return false;
			if (dec.rotAngle != state.LastRotAngle || dec.scaleVec != state.LastScaleVec) return false;
			if (CustomEasingEngine.HasActiveTweens(dec)) return false;
			if (dec.eventTweens != null)
			{
				foreach (var tween in dec.eventTweens.Values)
				{
					if (tween != null && tween.IsActive() && tween.IsPlaying())
						return false;
				}
			}
			return true;
		}

		[IriPatch(Path = "optimizer/ffx", Pre = typeof(OptimizerSettings), Condition = "enableOptimizer,optimizeFfxDecorations")]
		[HarmonyPatch(typeof(scrDecorationManager), "LateUpdate")]
		public static class OptimizeDecorationManagerLateUpdate
		{
			static bool Prefix(scrDecorationManager __instance)
			{
				if (!Main.Settings.optimizer.enableOptimizer || !Main.Settings.optimizer.optimizeFfxDecorations)
					return true;

				try
				{
					bool disableV15Features = ADOBase.controller.disableV15Features;
					var allDecorations = __instance.allDecorations;
					int count = allDecorations.Count;

					for (int i = 0; i < count; i++)
					{
						scrDecoration dec = allDecorations[i];
						if (dec == null) continue;

						// 已被合批渲染器接管：位置/材质由顶点遍历负责，
						// 这里直接跳过（LogicUpdate 前缀亦会兜底）。
						if (StaticDecorationBatcher.IsBatched(dec))
						{
							if (dec.useHitbox) dec.UpdateHitboxState();
							continue;
						}

						var state = _states.GetOrCreateValue(dec);
						if (!NeedsLogicUpdate(dec, state)) continue;

						if (IsPositionStatic(dec, state))
						{
							// 位置静止：跳过 UpdatePosition 的 transform 重写，
							// 其余（材质/滤镜/判定盒）照常执行
							dec.UpdateShader(disableV15Features);
							if (dec.useHitbox)
							{
								dec.UpdateHitboxState();
							}
						}
						else
						{
							dec.LogicUpdate(disableV15Features);
						}
					}

					return false;
				}
				catch (Exception ex)
				{
					Main.Logger?.Error($"[FfxOptimization] Error in OptimizeDecorationManagerLateUpdate: {ex}");
					return true;
				}
			}
		}
	}
}
