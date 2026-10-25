using Iridium.Config;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;
using ADOFAI;
using DG.Tweening;
using DG.Tweening.Core;
using DG.Tweening.Plugins.Options;

namespace Iridium.Patches
{
	public static class TrackOptimizationPatches
	{
		internal static ConditionalWeakTable<scrFloor, Transform> _floorTransformCache = new ConditionalWeakTable<scrFloor, Transform>();

		private static Transform GetTransform(scrFloor floor)
		{
			if (!_floorTransformCache.TryGetValue(floor, out var t))
			{
				t = floor.transform;
				_floorTransformCache.Add(floor, t);
			}
			return t;
		}

		[IriPatch(Path = "optimizer/track", Pre = typeof(OptimizerSettings), Condition = "enableOptimizer,optimizeMoveTrack,!enableCustomEasingEngine")]
		[HarmonyPatch(typeof(ffxMoveFloorPlus), nameof(ffxMoveFloorPlus.StartEffect))]
		public static class MoveFloorStartEffectPatch
		{
			public static bool Prefix(ffxMoveFloorPlus __instance)
			{
				if (!Main.Settings.optimizer.optimizeMoveTrack) return true;
				if (Main.Settings.optimizer.enableCustomEasingEngine) return true;

				__instance.AdjustDurationForHardbake();
				
				int startIdx = __instance.start;
				int endIdx = __instance.end;
				if (endIdx < startIdx)
				{
					int temp = startIdx;
					startIdx = endIdx;
					endIdx = temp;
				}

				Vector3 posOffset = new Vector3(__instance.targetPos.x, __instance.targetPos.y, 0f);
				float rotZ = __instance.targetRot;
				Vector3 scaleTarget = new Vector3(__instance.targetScaleV2.x, __instance.targetScaleV2.y, 1f);
				
				var floors = ADOBase.lm.listFloors;
				int count = floors.Count;
				int step = 1 + __instance.gapLength;

				void ApplyToFloor(scrFloor target)
				{
					Transform targetTransform = GetTransform(target);
					var moveTweens = target.moveTweens;

					if (__instance.positionUsed)
					{
						Vector3 targetPos = target.startPos + posOffset;
						if (!float.IsNaN(targetPos.x))
						{
							if (moveTweens.TryGetValue(TweenType.PositionX, out var oldT)) oldT.Kill(true);
							if (!Mathf.Approximately(targetTransform.position.x, targetPos.x))
								moveTweens[TweenType.PositionX] = DOTween.To((DOGetter<float>)(() => targetTransform.position.x), (DOSetter<float>)(x => targetTransform.MoveX(x)), targetPos.x, __instance.duration).SetEase(__instance.ease);
						}
						if (!float.IsNaN(targetPos.y))
						{
							if (moveTweens.TryGetValue(TweenType.PositionY, out var oldT)) oldT.Kill(true);
							if (!Mathf.Approximately(targetTransform.position.y, targetPos.y))
								moveTweens[TweenType.PositionY] = DOTween.To((DOGetter<float>)(() => targetTransform.position.y), (DOSetter<float>)(y => targetTransform.MoveY(y)), targetPos.y, __instance.duration).SetEase(__instance.ease);
						}
					}

					if (__instance.rotationUsed)
					{
						if (moveTweens.TryGetValue(TweenType.Rotation, out var oldT)) oldT.Kill(true);
						float finalRotZ = (target.startRot + new Vector3(0, 0, rotZ)).z;
						if (!Mathf.Approximately(targetTransform.eulerAngles.z, finalRotZ))
						{
							moveTweens[TweenType.Rotation] = DOTween.To(
								(DOGetter<float>)(() => target.tweenRot.z), 
								(DOSetter<float>)(r => {
									target.tweenRot.z = r;
									targetTransform.eulerAngles = target.tweenRot;
								}), 
								finalRotZ, __instance.duration).SetEase(__instance.ease);
						}
					}

					if (__instance.scaleUsed)
					{
						if (!float.IsNaN(scaleTarget.x))
						{
							if (moveTweens.TryGetValue(TweenType.ScaleX, out var oldT)) oldT.Kill(true);
							moveTweens[TweenType.ScaleX] = targetTransform.DOScaleX(scaleTarget.x, __instance.duration).SetEase(__instance.ease);
						}
						if (!float.IsNaN(scaleTarget.y))
						{
							if (moveTweens.TryGetValue(TweenType.ScaleY, out var oldT)) oldT.Kill(true);
							moveTweens[TweenType.ScaleY] = targetTransform.DOScaleY(scaleTarget.y, __instance.duration).SetEase(__instance.ease);
						}
					}

					if (__instance.opacityUsed)
					{
						if (moveTweens.TryGetValue(TweenType.Opacity, out var oldT)) oldT.Kill(true);
						if (!Mathf.Approximately(target.opacity, __instance.targetOpacity))
						{
							var t = target.TweenOpacity(__instance.targetOpacity, __instance.duration, __instance.ease);
							if (t != null) moveTweens[TweenType.Opacity] = t;
						}
					}
				}

				for (int i = startIdx; i <= endIdx && i < count; i += step)
				{
					scrFloor target = floors[i];
					ApplyToFloor(target);
					// Mirror the game: MoveTrack also moves landable floors in freeroam areas
					if (target.freeroamArea == null) continue;
					foreach (scrFloor listFloor in target.freeroamArea.listFloors)
					{
						if (listFloor.isLandable)
							ApplyToFloor(listFloor);
					}
				}
				return false; 
			}
		}

		[IriPatch(Path = "optimizer/track", Pre = typeof(OptimizerSettings), Condition = "enableOptimizer,optimizeRecolorTrack,!enableCustomEasingEngine")]
		[HarmonyPatch(typeof(ffxRecolorFloorPlus), nameof(ffxRecolorFloorPlus.StartEffect))]
		public static class RecolorFloorStartEffectPatch
		{
			public static bool Prefix(ffxRecolorFloorPlus __instance)
			{
				if (!Main.Settings.optimizer.optimizeRecolorTrack) return true;
				if (Main.Settings.optimizer.enableCustomEasingEngine) return true;

				__instance.AdjustDurationForHardbake();
				
				int startIdx = __instance.start;
				int endIdx = __instance.end;
				if (endIdx < startIdx)
				{
					int temp = startIdx;
					startIdx = endIdx;
					endIdx = temp;
				}

				var floors = ADOBase.lm.listFloors;
				int count = floors.Count;
				int step = 1 + __instance.gapLength;
				float pitch = __instance.cond.song.pitch;

				for (int i = startIdx; i <= endIdx && i < count; i += step)
				{
					scrFloor target = floors[i];
					target.styleNum = (int)__instance.style;
					target.UpdateAngle(false);
					target.SetTrackStyle(__instance.style);

					var mt = target.moveTweens;
					if (mt.TryGetValue(TweenType.Color, out var t1)) t1.Kill(true);
					if (mt.TryGetValue(TweenType.Glow, out var t2)) t2.Kill(true);

					target.ColorFloor(__instance.colorType, __instance.color1, __instance.color2, __instance.colorAnimDuration / pitch, __instance.pulseType, (float)__instance.pulseLength, __instance.start, __instance.duration, __instance.ease);
					
					mt[TweenType.Glow] = DOTween.To((DOGetter<float>)(() => target.glowMultiplier), (DOSetter<float>)(x => target.glowMultiplier = x), __instance.glowMult, __instance.duration)
						.SetEase(__instance.ease);
				}

				__instance.enabled = false;
				return false;
			}
		}

		
		[HarmonyPatch(typeof(scnGame), "Awake")]
		public static class CleanupPatch
		{
			public static void Postfix()
			{
				_floorTransformCache = new ConditionalWeakTable<scrFloor, Transform>();
			}
		}
	}
}