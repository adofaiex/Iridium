using ADOFAI;
using DG.Tweening;
using HarmonyLib;
using Iridium.Core;
using System.Collections.Generic;
using UnityEngine;

#pragma warning disable CS8625 // 将 null 字面量转换为可能为空的引用类型

namespace Iridium.Patches
{
    /// <summary>
/// When enableCustomEasingEngine is on, MoveTrack/RecolorTrack/MoveDecoration StartEffect
/// use CustomEasingEngine instead of DOTween. Mutually exclusive with the
/// optimizeMoveTrack / optimizeRecolorTrack / optimizeMoveDecorations patches.
/// </summary>
    public static class CustomEasingPatches
    {
        [HarmonyPatch(typeof(ffxMoveFloorPlus), "StartEffect")]
        public static class MoveFloorPatch
        {
            [HarmonyPrefix]
            public static bool Prefix(ffxMoveFloorPlus __instance, scrPlanet planet)
            {
                if (!Main.Settings.optimizer.enableCustomEasingEngine)
                    return true; // 未启用，走原逻辑

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

                return false; // 跳过原始方法

                void TweenFloor(scrFloor target)
                {
                    Transform tform = ((Component)target).transform;
                    Dictionary<TweenType, Tween> moveTweens = target.moveTweens;
                    Vector3 posTarget = target.startPos + targetPosV3;
                    float rotZ = (target.startRot + targetRotVec).z;
                    float dur = __instance.duration;
                    Ease ease = __instance.ease;

                    if (__instance.positionUsed && !float.IsNaN(posTarget.x))
                    {
                        if (moveTweens.TryGetValue(TweenType.PositionX, out var old) && old != null) old.Kill(true);
                        if (!Mathf.Approximately(tform.position.x, posTarget.x))
                        {
                            float startX = tform.position.x;
                            moveTweens[TweenType.PositionX] = null; // 占位：不存 DOTween tween
                            CustomEasingEngine.ToFrom(startX, x => tform.MoveX(x), posTarget.x, dur, ease,
                                () => tform.MoveX(posTarget.x));
                        }
                    }

                    if (__instance.positionUsed && !float.IsNaN(posTarget.y))
                    {
                        if (moveTweens.TryGetValue(TweenType.PositionY, out var old) && old != null) old.Kill(true);
                        if (!Mathf.Approximately(tform.position.y, posTarget.y))
                        {
                            float startY = tform.position.y;
                            moveTweens[TweenType.PositionY] = null;
                            CustomEasingEngine.ToFrom(startY, y => tform.MoveY(y), posTarget.y, dur, ease,
                                () => tform.MoveY(posTarget.y));
                        }
                    }

                    // 每帧同步 tweenRot → transform（同原始 DOTween OnUpdate）
                    if (__instance.rotationUsed)
                    {
                        if (moveTweens.TryGetValue(TweenType.Rotation, out var old) && old != null) old.Kill(true);
                        if (!Mathf.Approximately(tform.eulerAngles.z, rotZ))
                        {
                            float startRot = target.tweenRot.z;
                            moveTweens[TweenType.Rotation] = null;
                            CustomEasingEngine.ToFrom(startRot,
                                r => { target.tweenRot.z = r; tform.eulerAngles = target.tweenRot; },
                                rotZ, dur, ease);
                        }
                    }

                    // 只改 X 轴，Y/Z 从 localScale 实时读取（同原始 AxisConstraint.X）
                    if (__instance.scaleUsed && !float.IsNaN(targetScaleVec.x))
                    {
                        if (moveTweens.TryGetValue(TweenType.ScaleX, out var oldSx) && oldSx != null) oldSx.Kill(true);
                        float sx = tform.localScale.x;
                        if (!Mathf.Approximately(sx, targetScaleVec.x))
                        {
                            moveTweens[TweenType.ScaleX] = null;
                            CustomEasingEngine.ToFrom(sx,
                                x => tform.localScale = new Vector3(x, tform.localScale.y, tform.localScale.z),
                                targetScaleVec.x, dur, ease);
                        }
                    }

                    // 只改 Y 轴
                    if (__instance.scaleUsed && !float.IsNaN(targetScaleVec.y))
                    {
                        if (moveTweens.TryGetValue(TweenType.ScaleY, out var oldSy) && oldSy != null) oldSy.Kill(true);
                        float sy = tform.localScale.y;
                        if (!Mathf.Approximately(sy, targetScaleVec.y))
                        {
                            moveTweens[TweenType.ScaleY] = null;
                            CustomEasingEngine.ToFrom(sy,
                                y => tform.localScale = new Vector3(tform.localScale.x, y, tform.localScale.z),
                                targetScaleVec.y, dur, ease);
                        }
                    }

                    if (__instance.opacityUsed)
                    {
                        if (moveTweens.TryGetValue(TweenType.Opacity, out var old) && old != null) old.Kill(true);
                        if (!Mathf.Approximately(target.opacity, __instance.targetOpacity))
                        {
                            var opacityTween = target.TweenOpacity(__instance.targetOpacity, dur, ease);
                            if (opacityTween != null)
                                moveTweens[TweenType.Opacity] = opacityTween;
                        }
                    }
                }
            }
        }

        // RecolorTrack

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

                for (int i = __instance.start; i <= __instance.end; i += 1 + __instance.gapLength)
                {
                    scrFloor target = ADOBase.lm.listFloors[i];
                    ((Behaviour)__instance).enabled = false;
                    target.styleNum = (int)__instance.style;
                    target.UpdateAngle(false);
                    target.SetTrackStyle(__instance.style);

                    var managedTypes = new[] { TweenType.Glow };
                    foreach (var tt in managedTypes)
                    {
                        if (target.moveTweens.TryGetValue(tt, out var v))
                            TweenExtensions.Kill(v, true);
                    }

                    // ColorFloor 含脉冲动画等复杂逻辑，保持原样
                    target.ColorFloor(__instance.colorType, __instance.color1, __instance.color2,
                        __instance.colorAnimDuration / __instance.cond.song.pitch,
                        __instance.pulseType, __instance.pulseLength, __instance.start,
                        __instance.duration, __instance.ease);

                    // 只替换 glowMultiplier 的 DOTween tween
                    float startGlow = target.glowMultiplier;
                    target.moveTweens[TweenType.Glow] = null;
                    CustomEasingEngine.ToFrom(startGlow, x => target.glowMultiplier = x,
                        __instance.glowMult, __instance.duration, __instance.ease);
                }

                return false;
            }
        }

        // MoveDecoration

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

                // 2.9.8 兼容：按 targetTags 从 taggedDecorations 取装饰物
                foreach (string tag in __instance.targetTags)
                {
                    if (!__instance.decManager.taggedDecorations.TryGetValue(tag, out var taggedList))
                        continue;
                    foreach (scrDecoration dec in taggedList)
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
                        // Position
                        if (__instance.positionUsed)
                        {
                            Vector2 startPos = (__instance.movementType == DecPlacementType.LastPosition)
                                ? dec.pivotPosVec : dec.startPos;

                            if (!float.IsNaN(__instance.targetPos.x))
                            {
                                if (tweens.TryGetValue(TweenType.PositionX, out var old) && old != null) old.Kill(true);
                                float tx = startPos.x + __instance.targetPos.x;
                                float sx = dec.pivotPosVec.x;
                                tweens[TweenType.PositionX] = null;
                                CustomEasingEngine.ToFrom(sx, x => dec.SetPositionX(x, dec.pivotOffsetVec),
                                    tx, dur, ease);
                            }

                            if (!float.IsNaN(__instance.targetPos.y))
                            {
                                if (tweens.TryGetValue(TweenType.PositionY, out var old) && old != null) old.Kill(true);
                                float ty = startPos.y + __instance.targetPos.y;
                                float sy = dec.pivotPosVec.y;
                                tweens[TweenType.PositionY] = null;
                                CustomEasingEngine.ToFrom(sy, y => dec.SetPositionY(y, dec.pivotOffsetVec),
                                    ty, dur, ease);
                            }
                        }

                        // Parallax Offset
                        if (__instance.parallaxOffsetUsed)
                        {
                            if (!float.IsNaN(__instance.targetParallaxOffset.x))
                            {
                                if (tweens.TryGetValue(TweenType.ParallaxOffsetX, out var old) && old != null) old.Kill(true);
                                float sx = dec.parallaxOffset.x;
                                tweens[TweenType.ParallaxOffsetX] = null;
                                CustomEasingEngine.ToFrom(sx, x => dec.SetParallaxOffsetX(x),
                                    __instance.targetParallaxOffset.x, dur, ease);
                            }

                            if (!float.IsNaN(__instance.targetParallaxOffset.y))
                            {
                                if (tweens.TryGetValue(TweenType.ParallaxOffsetY, out var old) && old != null) old.Kill(true);
                                float sy = dec.parallaxOffset.y;
                                tweens[TweenType.ParallaxOffsetY] = null;
                                CustomEasingEngine.ToFrom(sy, y => dec.SetParallaxOffsetY(y),
                                    __instance.targetParallaxOffset.y, dur, ease);
                            }
                        }

                        // Pivot
                        if (__instance.pivotUsed)
                        {
                            if (!float.IsNaN(__instance.targetPivot.x))
                            {
                                if (tweens.TryGetValue(TweenType.PivotX, out var old) && old != null) old.Kill(true);
                                float sx = dec.pivotOffsetVec.x;
                                tweens[TweenType.PivotX] = null;
                                CustomEasingEngine.ToFrom(sx, x => dec.SetPivotX(x),
                                    __instance.targetPivot.x, dur, ease);
                            }

                            if (!float.IsNaN(__instance.targetPivot.y))
                            {
                                if (tweens.TryGetValue(TweenType.PivotY, out var old) && old != null) old.Kill(true);
                                float sy = dec.pivotOffsetVec.y;
                                tweens[TweenType.PivotY] = null;
                                CustomEasingEngine.ToFrom(sy, y => dec.SetPivotY(y),
                                    __instance.targetPivot.y, dur, ease);
                            }
                        }

                        // 每帧调 SetRotation 同步到 transform
                        if (__instance.rotationUsed)
                        {
                            if (tweens.TryGetValue(TweenType.Rotation, out var old) && old != null) old.Kill(true);
                            float sr = dec.rotAngle;
                            tweens[TweenType.Rotation] = null;
                            CustomEasingEngine.ToFrom(sr, r => dec.SetRotation(r),
                                __instance.targetRot, dur, ease);
                        }

                    // Scale：逐轴 float tween，每个回调从 dec.scaleVec 读另一轴当前值，
                    // 避免 X/Y 两条独立 tween 互相覆盖
                    if (__instance.scaleUsed)
                    {
                        if (!float.IsNaN(endScale.x))
                        {
                            if (tweens.TryGetValue(TweenType.ScaleX, out var old) && old != null) old.Kill(true);
                            float sx = dec.scaleVec.x;
                            tweens[TweenType.ScaleX] = null;
                            CustomEasingEngine.ToFrom(sx,
                                x => dec.SetScale(new Vector2(x, dec.scaleVec.y)),
                                endScale.x, dur, ease);
                        }

                        if (!float.IsNaN(endScale.y))
                        {
                            if (tweens.TryGetValue(TweenType.ScaleY, out var old) && old != null) old.Kill(true);
                            float sy = dec.scaleVec.y;
                            tweens[TweenType.ScaleY] = null;
                            CustomEasingEngine.ToFrom(sy,
                                y => dec.SetScale(new Vector2(dec.scaleVec.x, y)),
                                endScale.y, dur, ease);
                        }
                    }
                    }

                    // Color
                    if (__instance.colorUsed)
                    {
                        if (tweens.TryGetValue(TweenType.Color, out var old) && old != null) old.Kill(true);
                        Color sc = dec.color;
                        tweens[TweenType.Color] = null;
                        CustomEasingEngine.ToColor(
                            () => dec.color, c => dec.SetColor(c),
                            __instance.targetColor, dur, ease,
                            () => dec.SetColor(__instance.targetColor));
                    }

                    // Opacity
                    if (__instance.opacityUsed)
                    {
                        if (tweens.TryGetValue(TweenType.Opacity, out var old) && old != null) old.Kill(true);
                        float so = dec.opacity;
                        tweens[TweenType.Opacity] = null;
                        CustomEasingEngine.ToFrom(so, a => dec.SetOpacity(a),
                            __instance.targetOpacity, dur, ease,
                            () => dec.SetOpacity(__instance.targetOpacity));
                    }

                    // Parallax：原版 DOTween.To 插的是整个 Vector2，x/y 各自独立
                    if (__instance.parallaxUsed)
                    {
                        if (tweens.TryGetValue(TweenType.Parallax, out var old) && old != null) old.Kill(true);
                        Vector2 sp = dec.parallax.multiplier;
                        Vector2 tp = __instance.targetParallax / 100f;
                        tweens[TweenType.Parallax] = null;
                        CustomEasingEngine.ToVector3(
                            () => new Vector3(sp.x, sp.y, 0),
                            v => dec.parallax.multiplier = new Vector2(v.x, v.y),
                            new Vector3(tp.x, tp.y, 0), dur, ease);
                    }

                    // 非动画属性直接设置
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
                    } // end foreach dec
                } // end foreach tag

                return false;
            }
        }
    }
}
