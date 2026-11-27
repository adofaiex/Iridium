using ADOFAI;
using DG.Tweening;
using HarmonyLib;
using Iridium.Config;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Iridium.Patches.Optimizer
{
	[IriPatch(Path = "optimizer/decor", Pre = typeof(OptimizerSettings), Condition = "enableOptimizer")]
	[HarmonyPatch(typeof(ffxMoveDecorationsPlus), nameof(ffxMoveDecorationsPlus.StartEffect))]
	public static class MoveDecorationsOptimizationPatch
	{
		[HarmonyPrefix]
		public static bool Prefix(ffxMoveDecorationsPlus __instance)
		{
			if (!Main.Settings.optimizer.optimizeMoveDecorations)
				return true;
			if (Main.Settings.optimizer.enableCustomEasingEngine)
				return true;

			if (ADOBase.controller.visualQuality == VisualQuality.Low && (ADOBase.isOfficialLevel || Persistence.forceVisualSettings) && !ADOBase.levelIsMikoSkip)
			{
				return false;
			}

			if (!float.IsNaN(__instance.targetScale))
			{
				__instance.targetScaleV2 = new Vector2(__instance.targetScale, __instance.targetScale);
			}

			__instance.AdjustDurationForHardbake();

			HashSet<scrDecoration> processedDecs = new HashSet<scrDecoration>();
			Vector2 endScale = new Vector2(__instance.targetScaleV2.x, __instance.targetScaleV2.y);
			float duration = __instance.duration;
			bool isZeroDuration = duration <= 0f;

			foreach (string targetTag in __instance.targetTags)
			{
				if (!__instance.decManager.taggedDecorations.TryGetValue(targetTag, out var decList))
				{
					continue;
				}

				foreach (scrDecoration dec in decList)
				{
					if (!processedDecs.Add(dec))
					{
						continue;
					}

					Dictionary<TweenType, Tween> tweens = dec.eventTweens;
					bool isVisual = dec is scrVisualDecoration;
					scrVisualDecoration? visualDec = isVisual ? (scrVisualDecoration)dec : null;

					if ((bool)ADOBase.customLevel && __instance.movementTypeUsed && __instance.movementType != DecPlacementType.LastPosition)
					{
						dec.SetPlacementType(__instance.movementType);
					}

					if (!__instance.forceDontTweenMovement)
					{
						if (__instance.positionUsed)
						{
							Vector2 startPos = (__instance.movementType == DecPlacementType.LastPosition) ? dec.pivotPosVec : dec.startPos;
							if (!float.IsNaN(__instance.targetPos.x))
							{
								float targetX = startPos.x + __instance.targetPos.x;
								if (isZeroDuration)
								{
									if (tweens.TryGetValue(TweenType.PositionX, out var t)) t.Kill(true);
									dec.SetPositionX(targetX, dec.pivotOffsetVec);
								}
								else
								{
									if (tweens.TryGetValue(TweenType.PositionX, out var t)) t.Kill(true);
									Vector2 newPos = dec.pivotPosVec;
									tweens[TweenType.PositionX] = DOTween.To(() => newPos.x, x => newPos.x = x, targetX, duration)
										.SetEase(__instance.ease)
										.OnUpdate(() => dec.SetPositionX(newPos.x, dec.pivotOffsetVec))
										.OnComplete(() => dec.SetPositionX(targetX, dec.pivotOffsetVec))
										.Done();
								}
							}

							if (!float.IsNaN(__instance.targetPos.y))
							{
								float targetY = startPos.y + __instance.targetPos.y;
								if (isZeroDuration)
								{
									if (tweens.TryGetValue(TweenType.PositionY, out var t)) t.Kill(true);
									dec.SetPositionY(targetY, dec.pivotOffsetVec);
								}
								else
								{
									if (tweens.TryGetValue(TweenType.PositionY, out var t)) t.Kill(true);
									Vector2 newPos = dec.pivotPosVec;
									tweens[TweenType.PositionY] = DOTween.To(() => newPos.y, y => newPos.y = y, targetY, duration)
										.SetEase(__instance.ease)
										.OnUpdate(() => dec.SetPositionY(newPos.y, dec.pivotOffsetVec))
										.OnComplete(() => dec.SetPositionY(targetY, dec.pivotOffsetVec))
										.Done();
								}
							}
						}

						if (__instance.parallaxOffsetUsed)
						{
							if (!float.IsNaN(__instance.targetParallaxOffset.x))
							{
								if (isZeroDuration)
								{
									if (tweens.TryGetValue(TweenType.ParallaxOffsetX, out var t)) t.Kill(true);
									dec.SetParallaxOffsetX(__instance.targetParallaxOffset.x);
								}
								else
								{
									if (tweens.TryGetValue(TweenType.ParallaxOffsetX, out var t)) t.Kill(true);
									Vector2 newPos = dec.parallaxOffset;
									tweens[TweenType.ParallaxOffsetX] = DOTween.To(() => newPos.x, x => newPos.x = x, __instance.targetParallaxOffset.x, duration)
										.SetEase(__instance.ease)
										.OnUpdate(() => dec.SetParallaxOffsetX(newPos.x))
										.OnComplete(() => dec.SetParallaxOffsetX(__instance.targetParallaxOffset.x))
										.Done();
								}
							}

							if (!float.IsNaN(__instance.targetParallaxOffset.y))
							{
								if (isZeroDuration)
								{
									if (tweens.TryGetValue(TweenType.ParallaxOffsetY, out var t)) t.Kill(true);
									dec.SetParallaxOffsetY(__instance.targetParallaxOffset.y);
								}
								else
								{
									if (tweens.TryGetValue(TweenType.ParallaxOffsetY, out var t)) t.Kill(true);
									Vector2 newPos = dec.parallaxOffset;
									tweens[TweenType.ParallaxOffsetY] = DOTween.To(() => newPos.y, y => newPos.y = y, __instance.targetParallaxOffset.y, duration)
										.SetEase(__instance.ease)
										.OnUpdate(() => dec.SetParallaxOffsetY(newPos.y))
										.OnComplete(() => dec.SetParallaxOffsetY(__instance.targetParallaxOffset.y))
										.Done();
								}
							}
						}

						if (__instance.pivotUsed)
						{
							if (!float.IsNaN(__instance.targetPivot.x))
							{
								if (isZeroDuration)
								{
									if (tweens.TryGetValue(TweenType.PivotX, out var t)) t.Kill(true);
									dec.SetPivotX(__instance.targetPivot.x);
								}
								else
								{
									if (tweens.TryGetValue(TweenType.PivotX, out var t)) t.Kill(true);
									Vector2 newPivot = dec.pivotOffsetVec;
									tweens[TweenType.PivotX] = DOTween.To(() => newPivot.x, x => newPivot.x = x, __instance.targetPivot.x, duration)
										.SetEase(__instance.ease)
										.OnUpdate(() => dec.SetPivotX(newPivot.x))
										.OnComplete(() => dec.SetPivotX(__instance.targetPivot.x))
										.Done();
								}
							}

							if (!float.IsNaN(__instance.targetPivot.y))
							{
								if (isZeroDuration)
								{
									if (tweens.TryGetValue(TweenType.PivotY, out var t)) t.Kill(true);
									dec.SetPivotY(__instance.targetPivot.y);
								}
								else
								{
									if (tweens.TryGetValue(TweenType.PivotY, out var t)) t.Kill(true);
									Vector2 newPivot = dec.pivotOffsetVec;
									tweens[TweenType.PivotY] = DOTween.To(() => newPivot.y, y => newPivot.y = y, __instance.targetPivot.y, duration)
										.SetEase(__instance.ease)
										.OnUpdate(() => dec.SetPivotY(newPivot.y))
										.OnComplete(() => dec.SetPivotY(__instance.targetPivot.y))
										.Done();
								}
							}
						}

						if (__instance.rotationUsed)
						{
							if (isZeroDuration)
							{
								if (tweens.TryGetValue(TweenType.Rotation, out var t)) t.Kill(true);
								dec.SetRotation(__instance.targetRot);
							}
							else
							{
								if (tweens.TryGetValue(TweenType.Rotation, out var t)) t.Kill(true);
								float newRot = dec.rotAngle;
								tweens[TweenType.Rotation] = DOTween.To(() => newRot, r => newRot = r, __instance.targetRot, duration)
									.SetEase(__instance.ease)
									.OnUpdate(() => dec.SetRotation(newRot))
									.OnComplete(() => dec.SetRotation(__instance.targetRot))
									.Done();
							}
						}

						if (__instance.scaleUsed)
						{
							if (!float.IsNaN(endScale.x))
							{
								if (isZeroDuration)
								{
									if (tweens.TryGetValue(TweenType.ScaleX, out var t)) t.Kill(true);
									Vector2 currentScale = dec.scaleVec;
									currentScale.x = endScale.x;
									dec.SetScale(currentScale);
								}
								else
								{
									if (tweens.TryGetValue(TweenType.ScaleX, out var t)) t.Kill(true);
									tweens[TweenType.ScaleX] = DOTween.To(() => dec.scaleVec, v => dec.SetScale(v), endScale, duration)
										.SetEase(__instance.ease)
										.SetOptions(AxisConstraint.X)
										.Done();
								}
							}

							if (!float.IsNaN(endScale.y))
							{
								if (isZeroDuration)
								{
									if (tweens.TryGetValue(TweenType.ScaleY, out var t)) t.Kill(true);
									Vector2 currentScale = dec.scaleVec;
									currentScale.y = endScale.y;
									dec.SetScale(currentScale);
								}
								else
								{
									if (tweens.TryGetValue(TweenType.ScaleY, out var t)) t.Kill(true);
									tweens[TweenType.ScaleY] = DOTween.To(() => dec.scaleVec, v => dec.SetScale(v), endScale, duration)
										.SetEase(__instance.ease)
										.SetOptions(AxisConstraint.Y)
										.Done();
								}
							}
						}
					}

					if (__instance.colorUsed)
					{
						if (isZeroDuration)
						{
							if (tweens.TryGetValue(TweenType.Color, out var t)) t.Kill(true);
							dec.SetColor(__instance.targetColor);
						}
						else
						{
							if (tweens.TryGetValue(TweenType.Color, out var t)) t.Kill(true);
							Color newColor = dec.color;
							tweens[TweenType.Color] = DOTween.To(() => newColor, c => newColor = c, __instance.targetColor, duration)
								.SetEase(__instance.ease)
								.OnUpdate(() => dec.SetColor(newColor))
								.OnComplete(() => dec.SetColor(__instance.targetColor))
								.Done();
						}
					}

					if (__instance.opacityUsed)
					{
						if (isZeroDuration)
						{
							if (tweens.TryGetValue(TweenType.Opacity, out var t)) t.Kill(true);
							dec.SetOpacity(__instance.targetOpacity);
						}
						else
						{
							if (tweens.TryGetValue(TweenType.Opacity, out var t)) t.Kill(true);
							float newOpacity = dec.opacity;
							tweens[TweenType.Opacity] = DOTween.To(() => newOpacity, a => newOpacity = a, __instance.targetOpacity, duration)
								.SetEase(__instance.ease)
								.OnUpdate(() => dec.SetOpacity(newOpacity))
								.OnComplete(() => dec.SetOpacity(__instance.targetOpacity))
								.Done();
						}
					}

					if (__instance.parallaxUsed)
					{
						if (isZeroDuration)
						{
							if (tweens.TryGetValue(TweenType.Parallax, out var t)) t.Kill(true);
							dec.parallax.multiplier = __instance.targetParallax / 100f;
						}
						else
						{
							if (tweens.TryGetValue(TweenType.Parallax, out var t)) t.Kill(true);
							Vector2 newParallax = dec.parallax.multiplier;
							tweens[TweenType.Parallax] = DOTween.To(() => newParallax, p => dec.parallax.multiplier = p, __instance.targetParallax / 100f, duration)
								.SetEase(__instance.ease)
								.Done();
						}
					}

					if (__instance.visibleUsed)
					{
						dec.SetVisible(__instance.visible && !dec.forceHide);
					}

					if (__instance.depthUsed)
					{
						dec.SetDepth(__instance.targetDepth);
					}

					// 粒子装饰物的贴图（原版有，旧补丁漏了）
					if (dec is scrParticleDecoration particleDec && __instance.imageFilenameUsed)
					{
						bool hasImg = !string.IsNullOrEmpty(__instance.targetImageFilename);
						var particleSprites = scrDecorationManager.instance.imageHolder.customSprites;
						particleDec.SetSprite(hasImg ? particleSprites[__instance.targetImageFilename] : null);
					}
					if (isVisual && visualDec != null)
					{
						if (__instance.imageFilenameUsed)
						{
							var customSprites = scrDecorationManager.instance.imageHolder.customSprites;
							customSprites.TryGetValue(__instance.targetImageFilename ?? string.Empty, out var s);
							OptimizerShared.RuntimeSetSpriteOrClear(visualDec, s);
						}
						// 3.3.x 新增：原始尺寸 / 平滑
						OptimizerShared.MoveDecorationsApplyVisualProps(__instance, visualDec);

						if (__instance.maskingTypeUsed)
						{
							visualDec.SetMaskingType(__instance.targetMaskingType);
						}

						if (__instance.maskingTargetUsed)
						{
							visualDec.SetMaskingTarget(__instance.targetmaskingTarget);
						}

						if (__instance.useMaskingDepthUsed)
						{
							visualDec.SetMaskingDepth(__instance.targetUseMaskingDepth);
						}

						if (__instance.maskingFrontDepthUsed || __instance.maskingBackDepthUsed)
						{
							visualDec.SetMaskingDepth(__instance.maskingFrontDepthUsed ? new int?(__instance.targetMaskingFrontDepth) : null, __instance.maskingBackDepthUsed ? new int?(__instance.targetMaskingBackDepth) : null);
						}
					}
				}
			}
			return false;
		}
	}
}
