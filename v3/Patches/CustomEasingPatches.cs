using Iridium.Config;
using ADOFAI;
using DG.Tweening;
using HarmonyLib;
using Iridium.Core;
using System.Collections.Generic;
using UnityEngine;

namespace Iridium.Patches
{
	/// <summary>
	/// 自定义缓速引擎：enableCustomEasingEngine 打开后拦截 MoveTrack / RecolorTrack /
	/// MoveDecorations 的 StartEffect，用 CustomEasingEngine 替代 DOTween。
	/// 注意不向 moveTweens/eventTweens 写 null 占位 —— 原版 scrDecoration.OnDestroy 遍历
	/// eventTweens.Values 不判 null，会 NRE；旧的 DOTween tween 直接 Remove。
	/// </summary>
	public static class CustomEasingPatches
	{
		/// <summary>杀掉原版存在 moveTweens/eventTweens 里的 DOTween tween 并移除条目。</summary>
		private static void KillVanillaTween(Dictionary<TweenType, Tween> tweens, TweenType type)
		{
			if (tweens.TryGetValue(type, out var old))
			{
				old?.Kill(true);
				tweens.Remove(type);
			}
		}

		[IriPatch(Path = "optimizer/customEasing", Pre = typeof(OptimizerSettings), Condition = "enableCustomEasingEngine")]
		[HarmonyPatch(typeof(ffxMoveFloorPlus), "StartEffect")]
		public static class MoveFloorPatch
		{
			[HarmonyPrefix]
			public static bool Prefix(ffxMoveFloorPlus __instance, scrPlanet planet)
			{
				if (!Main.Settings.optimizer.enableCustomEasingEngine)
					return true;

				__instance.AdjustDurationForHardbake();

				if (__instance.end < __instance.start)
				{
					int tmp = __instance.end;
					__instance.end = __instance.start;
					__instance.start = tmp;
				}

				Vector3 targetPosV3 = new Vector3(__instance.targetPos.x, __instance.targetPos.y, 0f);
				Vector3 targetRotVec = new Vector3(0f, 0f, __instance.targetRot);
				Vector3 targetScaleVec = new Vector3(__instance.targetScaleV2.x, __instance.targetScaleV2.y, 1f);

				List<scrFloor> listFloors = ADOBase.lm.listFloors;
				object owner = __instance;

				for (int i = __instance.start; i <= __instance.end; i += 1 + __instance.gapLength)
				{
					scrFloor floor = listFloors[i];
					TweenFloor(floor);

					if (floor.freeroamArea == null) continue;
					foreach (scrFloor sub in floor.freeroamArea.listFloors)
					{
						if (sub.isLandable)
							TweenFloor(sub);
					}
				}

				return false;

				void TweenFloor(scrFloor target)
				{
					Dictionary<TweenType, Tween> moveTweens = target.moveTweens;
					float dur = __instance.duration;
					Ease ease = __instance.ease;

					if (__instance.positionUsed)
					{
						float endX = target.startPos.x + targetPosV3.x;
						float endY = target.startPos.y + targetPosV3.y;
						KillVanillaTween(moveTweens, TweenType.PositionX);
						if (!float.IsNaN(endX))
							CustomEasingEngine.Start(target, TweenType.PositionX, endX, dur, ease, owner);
						KillVanillaTween(moveTweens, TweenType.PositionY);
						if (!float.IsNaN(endY))
							CustomEasingEngine.Start(target, TweenType.PositionY, endY, dur, ease, owner);
					}

					if (__instance.rotationUsed)
					{
						KillVanillaTween(moveTweens, TweenType.Rotation);
						CustomEasingEngine.Start(target, TweenType.Rotation, (target.startRot + targetRotVec).z, dur, ease, owner);
					}

					if (__instance.scaleUsed)
					{
						KillVanillaTween(moveTweens, TweenType.ScaleX);
						if (!float.IsNaN(targetScaleVec.x))
							CustomEasingEngine.Start(target, TweenType.ScaleX, targetScaleVec.x, dur, ease, owner);
						KillVanillaTween(moveTweens, TweenType.ScaleY);
						if (!float.IsNaN(targetScaleVec.y))
							CustomEasingEngine.Start(target, TweenType.ScaleY, targetScaleVec.y, dur, ease, owner);
					}

					if (__instance.opacityUsed)
					{
						KillVanillaTween(moveTweens, TweenType.Opacity);
						CustomEasingEngine.Start(target, TweenType.Opacity, __instance.targetOpacity, dur, ease, owner);
					}
				}
			}
		}

		[IriPatch(Path = "optimizer/customEasing", Pre = typeof(OptimizerSettings), Condition = "enableCustomEasingEngine")]
		[HarmonyPatch(typeof(ffxRecolorFloorPlus), "StartEffect")]
		public static class RecolorFloorPatch
		{
			[HarmonyPrefix]
			public static bool Prefix(ffxRecolorFloorPlus __instance, scrPlanet planet)
			{
				if (!Main.Settings.optimizer.enableCustomEasingEngine)
					return true;

				__instance.AdjustDurationForHardbake();

				if (__instance.end < __instance.start)
				{
					int tmp = __instance.end;
					__instance.end = __instance.start;
					__instance.start = tmp;
				}

				object owner = __instance;
				for (int i = __instance.start; i <= __instance.end; i += 1 + __instance.gapLength)
				{
					scrFloor target = ADOBase.lm.listFloors[i];
					((Behaviour)__instance).enabled = false;
					target.styleNum = (int)__instance.style;
					target.UpdateAngle(false);
					target.SetTrackStyle(__instance.style);

					KillVanillaTween(target.moveTweens, TweenType.Glow);

					// ColorFloor 是游戏内部复杂逻辑（含脉冲动画等），保持原样
					target.ColorFloor(__instance.colorType, __instance.color1, __instance.color2,
						__instance.colorAnimDuration / __instance.cond.song.pitch,
						__instance.pulseType, __instance.pulseLength, __instance.start,
						__instance.duration, __instance.ease);

					// 仅替换 glowMultiplier 的 DOTween tween
					CustomEasingEngine.Start(target, TweenType.Glow, __instance.glowMult, __instance.duration, __instance.ease, owner);
				}

				return false;
			}
		}

		[IriPatch(Path = "optimizer/customEasing", Pre = typeof(OptimizerSettings), Condition = "enableCustomEasingEngine")]
		[HarmonyPatch(typeof(ffxMoveDecorationsPlus), "StartEffect")]
		public static class MoveDecorationPatch
		{
			[HarmonyPrefix]
			public static bool Prefix(ffxMoveDecorationsPlus __instance, scrPlanet planet)
			{
				if (!Main.Settings.optimizer.enableCustomEasingEngine)
					return true;

				// 低画质官方关卡跳过（与原逻辑一致）
				if (ADOBase.controller.visualQuality == VisualQuality.Low
					&& ADOBase.isOfficialLevel && !ADOBase.levelIsMikoSkip)
					return false;

				if (!float.IsNaN(__instance.targetScale))
					__instance.targetScaleV2 = new Vector2(__instance.targetScale, __instance.targetScale);

				__instance.AdjustDurationForHardbake();
				float dur = __instance.duration;
				Ease ease = __instance.ease;
				object owner = __instance;

				foreach (scrDecoration dec in __instance.decManager.GetTaggedDecorations(__instance.targetTags))
				{
					Dictionary<TweenType, Tween> tweens = dec.eventTweens;
					bool isVisual = dec is scrVisualDecoration;
					bool isParticle = dec is scrParticleDecoration;

					if ((bool)ADOBase.customLevel && __instance.movementTypeUsed
						&& __instance.movementType != DecPlacementType.LastPosition)
						dec.SetPlacementType(__instance.movementType);

					Vector2 endScale = new Vector2(__instance.targetScaleV2.x, __instance.targetScaleV2.y);

					if (!__instance.forceDontTweenMovement)
					{
						if (__instance.positionUsed)
						{
							Vector2 startPos = (__instance.movementType == DecPlacementType.LastPosition)
								? dec.pivotPosVec : dec.startPos;

							if (!float.IsNaN(__instance.targetPos.x))
							{
								KillVanillaTween(tweens, TweenType.PositionX);
								CustomEasingEngine.Start(dec, TweenType.PositionX, startPos.x + __instance.targetPos.x, dur, ease, owner);
							}

							if (!float.IsNaN(__instance.targetPos.y))
							{
								KillVanillaTween(tweens, TweenType.PositionY);
								CustomEasingEngine.Start(dec, TweenType.PositionY, startPos.y + __instance.targetPos.y, dur, ease, owner);
							}
						}

						if (__instance.parallaxOffsetUsed)
						{
							if (!float.IsNaN(__instance.targetParallaxOffset.x))
							{
								KillVanillaTween(tweens, TweenType.ParallaxOffsetX);
								CustomEasingEngine.Start(dec, TweenType.ParallaxOffsetX, __instance.targetParallaxOffset.x, dur, ease, owner);
							}

							if (!float.IsNaN(__instance.targetParallaxOffset.y))
							{
								KillVanillaTween(tweens, TweenType.ParallaxOffsetY);
								CustomEasingEngine.Start(dec, TweenType.ParallaxOffsetY, __instance.targetParallaxOffset.y, dur, ease, owner);
							}
						}

						if (__instance.pivotUsed)
						{
							if (!float.IsNaN(__instance.targetPivot.x))
							{
								KillVanillaTween(tweens, TweenType.PivotX);
								CustomEasingEngine.Start(dec, TweenType.PivotX, __instance.targetPivot.x, dur, ease, owner);
							}

							if (!float.IsNaN(__instance.targetPivot.y))
							{
								KillVanillaTween(tweens, TweenType.PivotY);
								CustomEasingEngine.Start(dec, TweenType.PivotY, __instance.targetPivot.y, dur, ease, owner);
							}
						}

						if (__instance.rotationUsed)
						{
							KillVanillaTween(tweens, TweenType.Rotation);
							CustomEasingEngine.Start(dec, TweenType.Rotation, __instance.targetRot, dur, ease, owner);
						}

						// X/Y 各自独立，回调实时读取另一轴
						if (__instance.scaleUsed)
						{
							if (!float.IsNaN(endScale.x))
							{
								KillVanillaTween(tweens, TweenType.ScaleX);
								CustomEasingEngine.Start(dec, TweenType.ScaleX, endScale.x, dur, ease, owner);
							}

							if (!float.IsNaN(endScale.y))
							{
								KillVanillaTween(tweens, TweenType.ScaleY);
								CustomEasingEngine.Start(dec, TweenType.ScaleY, endScale.y, dur, ease, owner);
							}
						}
					}

					if (__instance.colorUsed)
					{
						KillVanillaTween(tweens, TweenType.Color);
						CustomEasingEngine.StartColor(dec, TweenType.Color, __instance.targetColor, dur, ease, owner);
					}

					if (__instance.opacityUsed)
					{
						KillVanillaTween(tweens, TweenType.Opacity);
						CustomEasingEngine.Start(dec, TweenType.Opacity, __instance.targetOpacity, dur, ease, owner);
					}

					// 原版为 Vector2 插值
					if (__instance.parallaxUsed)
					{
						KillVanillaTween(tweens, TweenType.Parallax);
						CustomEasingEngine.StartVec2(dec, TweenType.Parallax, __instance.targetParallax / 100f, dur, ease, owner);
					}

					// 非动画属性直接设置（与原逻辑一致）
					if (__instance.visibleUsed)
						dec.SetVisible(__instance.visible && !dec.forceHide);
					if (__instance.depthUsed)
						dec.SetDepth(__instance.targetDepth);
					if (isParticle && __instance.imageFilenameUsed)
					{
						bool hasImg = !string.IsNullOrEmpty(__instance.targetImageFilename);
						var sprites = scrDecorationManager.instance.imageHolder.customSprites;
						((scrParticleDecoration)dec).SetSprite(hasImg ? sprites[__instance.targetImageFilename] : null);
					}
					if (isVisual && __instance.imageFilenameUsed)
					{
						bool hasImg = !string.IsNullOrEmpty(__instance.targetImageFilename);
						var sprites = scrDecorationManager.instance.imageHolder.customSprites;
						var cs = hasImg ? sprites[__instance.targetImageFilename] : null;
						((scrVisualDecoration)dec).SetSprite(cs?.sprite);
					}
					// Masking 属性 (仅 scrVisualDecoration)
					if (isVisual)
					{
						var visDec = (scrVisualDecoration)dec;
						if (__instance.maskingTypeUsed)
							visDec.SetMaskingType(__instance.targetMaskingType);
						if (__instance.maskingTargetUsed)
							visDec.SetMaskingTarget(__instance.targetmaskingTarget);
						if (__instance.useMaskingDepthUsed)
							visDec.SetMaskingDepth(__instance.targetUseMaskingDepth);
						if (__instance.maskingFrontDepthUsed || __instance.maskingBackDepthUsed)
							visDec.SetMaskingDepth(
								__instance.maskingFrontDepthUsed ? new int?(__instance.targetMaskingFrontDepth) : null,
								__instance.maskingBackDepthUsed ? new int?(__instance.targetMaskingBackDepth) : null);
					}
				}

				return false;
			}
		}

		/// <summary>
		/// 引擎 tick 紧跟 scrVfxPlus.Update（事件触发点）之后驱动。原版 DOTween 的驱动
		/// MonoBehaviour 创建于早期，其 Update 先于 scrPlanet.Update 执行，而 Main.OnUpdate
		/// 时序靠后 —— 会让行星读到滞后一帧的地板位置，是 MoveTrack 抖动的根源。
		/// </summary>
		[IriPatch(Path = "optimizer/customEasing", Pre = typeof(OptimizerSettings), Condition = "enableCustomEasingEngine")]
		[HarmonyPatch(typeof(scrVfxPlus), "Update")]
		public static class VfxUpdateTickPatch
		{
			[HarmonyPostfix]
			public static void Postfix()
			{
				if (Main.Settings.optimizer.enableCustomEasingEngine || CustomEasingEngine.ActiveCount > 0)
					CustomEasingEngine.Update(Time.deltaTime);
			}
		}

		/// <summary>
		/// ResetScene 用 DOTween.PlayingTweens() 收编所有 tween，引擎 tween 不在 DOTween 名册里，
		/// 必须在此同步杀死，否则随后的 ResetToLevelStart/ResetDecorations 会被继续写入覆盖。
		/// </summary>
		[IriPatch(Path = "optimizer/customEasing", Pre = typeof(OptimizerSettings), Condition = "enableCustomEasingEngine")]
		[HarmonyPatch(typeof(scnGame), "ResetScene")]
		public static class ResetSceneHook
		{
			[HarmonyPostfix]
			public static void Postfix()
			{
				CustomEasingEngine.KillAll(complete: false);
			}
		}

		/// <summary>
		/// ResetDecorations 会以 sourceLevelEvent 重置装饰物内部字段，引擎 tween 若继续写入
		/// 会覆盖复位结果（对应原版"tween 已死不再写入"）。
		/// </summary>
		[IriPatch(Path = "optimizer/customEasing", Pre = typeof(OptimizerSettings), Condition = "enableCustomEasingEngine")]
		[HarmonyPatch(typeof(scrDecorationManager), "ResetDecorations")]
		public static class ResetDecorationsHook
		{
			[HarmonyPostfix]
			public static void Postfix()
			{
				CustomEasingEngine.KillAllDecorations(complete: false);
			}
		}

		/// <summary>地板复位时机：杀掉该地板上仍在飞的引擎 tween。</summary>
		[IriPatch(Path = "optimizer/customEasing", Pre = typeof(OptimizerSettings), Condition = "enableCustomEasingEngine")]
		[HarmonyPatch(typeof(scrFloor), "ResetToLevelStart")]
		public static class ResetFloorHook
		{
			[HarmonyPostfix]
			public static void Postfix(scrFloor __instance)
			{
				CustomEasingEngine.KillTarget(__instance, complete: false);
			}
		}

		/// <summary>
		/// ffxFloorAppearPlus / ffxFloorDisappearPlus 不经我们补丁，直接用 DOTween 在 moveTweens
		/// 的 Position/Scale/Rotation/Opacity 键上建 tween。它们按原版语义 Kill 旧 tween 时看不到
		/// 引擎的键控 tween —— 引擎必须先自行让位，否则会与消失/出现动画互相覆写。
		/// </summary>
		private static void KillEngineTweenForVanillaKey(scrFloor floor, TweenType vanillaKey)
		{
			switch (vanillaKey)
			{
				case TweenType.Position: // 原版 Position 是向量，引擎拆成 X/Y
					CustomEasingEngine.KillByKey(floor, TweenType.PositionX, complete: true);
					CustomEasingEngine.KillByKey(floor, TweenType.PositionY, complete: true);
					break;
				case TweenType.Scale:
					CustomEasingEngine.KillByKey(floor, TweenType.ScaleX, complete: true);
					CustomEasingEngine.KillByKey(floor, TweenType.ScaleY, complete: true);
					break;
				default:
					CustomEasingEngine.KillByKey(floor, vanillaKey, complete: true);
					break;
			}
		}

		[IriPatch(Path = "optimizer/customEasing", Pre = typeof(OptimizerSettings), Condition = "enableCustomEasingEngine")]
		[HarmonyPatch(typeof(ffxFloorDisappearPlus), "StartEffect")]
		public static class FloorDisappearConflictPatch
		{
			[HarmonyPrefix]
			public static void Prefix(ffxFloorDisappearPlus __instance)
			{
				if (!Main.Settings.optimizer.enableCustomEasingEngine || __instance.floor == null) return;
				var floor = __instance.floor;
				switch (__instance.animType)
				{
					case TrackAnimationType2.Retract:
						KillEngineTweenForVanillaKey(floor, TweenType.Position);
						KillEngineTweenForVanillaKey(floor, TweenType.Scale);
						break;
					case TrackAnimationType2.Scatter:
					case TrackAnimationType2.Scatter_Far:
						KillEngineTweenForVanillaKey(floor, TweenType.Position);
						KillEngineTweenForVanillaKey(floor, TweenType.Rotation);
						break;
					case TrackAnimationType2.Shrink:
						KillEngineTweenForVanillaKey(floor, TweenType.Scale);
						break;
					case TrackAnimationType2.Shrink_Spin:
						KillEngineTweenForVanillaKey(floor, TweenType.Scale);
						KillEngineTweenForVanillaKey(floor, TweenType.Rotation);
						break;
					case TrackAnimationType2.Fade:
						KillEngineTweenForVanillaKey(floor, TweenType.Opacity);
						break;
				}
			}
		}

		[IriPatch(Path = "optimizer/customEasing", Pre = typeof(OptimizerSettings), Condition = "enableCustomEasingEngine")]
		[HarmonyPatch(typeof(ffxFloorAppearPlus), "StartEffect")]
		public static class FloorAppearConflictPatch
		{
			[HarmonyPrefix]
			public static void Prefix(ffxFloorAppearPlus __instance)
			{
				if (!Main.Settings.optimizer.enableCustomEasingEngine || __instance.floor == null) return;
				var floor = __instance.floor;
				// Appear 无条件创建 Position tween（DOMove 回 startPos）
				KillEngineTweenForVanillaKey(floor, TweenType.Position);
				switch (__instance.animType)
				{
					case TrackAnimationType.Extend:
					case TrackAnimationType.Grow:
					case TrackAnimationType.Grow_Spin:
					case TrackAnimationType.Drop:
					case TrackAnimationType.Rise:
						KillEngineTweenForVanillaKey(floor, TweenType.Scale);
						break;
				}
				if (__instance.animType == TrackAnimationType.Assemble
					|| __instance.animType == TrackAnimationType.Assemble_Far
					|| __instance.animType == TrackAnimationType.Grow_Spin)
					KillEngineTweenForVanillaKey(floor, TweenType.Rotation);
				if (__instance.animType == TrackAnimationType.Fade)
					KillEngineTweenForVanillaKey(floor, TweenType.Opacity);
			}
		}

		/// <summary>
		/// ScrubToTime 语义对齐：拖到事件末尾之后 → Kill(complete)，拖到中间 → Goto(已过时间)。
		/// 仅覆盖未 override ScrubToTime 的 ffx 类型（Move/Recolor/MoveDecorations 均未 override）。
		/// </summary>
		[IriPatch(Path = "optimizer/customEasing", Pre = typeof(OptimizerSettings), Condition = "enableCustomEasingEngine")]
		[HarmonyPatch(typeof(ffxPlusBase), nameof(ffxPlusBase.ScrubToTime))]
		public static class ScrubToTimePatch
		{
			[HarmonyPostfix]
			public static void Postfix(ffxPlusBase __instance, float t)
			{
				if (!Main.Settings.optimizer.enableCustomEasingEngine) return;
				if ((double)t < __instance.startTime) return;

				double end = __instance.startTime + (double)__instance.duration;
				if ((double)t >= end || Mathf.Approximately(t, (float)end))
				{
					CustomEasingEngine.KillOwned(__instance, complete: true);
					return;
				}
				float elapsed = (t - (float)__instance.startTime) / __instance.cond.song.pitch - scrConductor.calibration_i;
				CustomEasingEngine.GotoOwned(__instance, elapsed);
			}
		}

		/// <summary>ffxPlusBase.Kill（不 complete）→ 引擎里属于该事件的 tween 同步停住。</summary>
		[IriPatch(Path = "optimizer/customEasing", Pre = typeof(OptimizerSettings), Condition = "enableCustomEasingEngine")]
		[HarmonyPatch(typeof(ffxPlusBase), nameof(ffxPlusBase.Kill))]
		public static class FfxKillPatch
		{
			[HarmonyPostfix]
			public static void Postfix(ffxPlusBase __instance)
			{
				if (Main.Settings.optimizer.enableCustomEasingEngine)
					CustomEasingEngine.KillOwned(__instance, complete: false);
			}
		}

		/// <summary>游戏退出播放/倒带/切关时调 DOTween.KillAll —— 同步清理引擎全部 tween，complete 语义透传。</summary>
		[IriPatch(Path = "optimizer/customEasing", Pre = typeof(OptimizerSettings), Condition = "enableCustomEasingEngine")]
		[HarmonyPatch(typeof(DOTween), "KillAll", new[] { typeof(bool) })]
		public static class DotweenKillAllPatch
		{
			[HarmonyPostfix]
			public static void Postfix(bool complete)
			{
				if (Main.Settings.optimizer.enableCustomEasingEngine)
					CustomEasingEngine.KillAll(complete);
			}
		}
	}
}
