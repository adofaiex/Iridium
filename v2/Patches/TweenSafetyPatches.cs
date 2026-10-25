using System.Collections;
using System.Reflection;
using DG.Tweening;
using HarmonyLib;

namespace Iridium.Patches
{
    /// <summary>
/// With DOTween.defaultRecyclable = true, completed tweens go back to the pool, and
/// ADOFAI keeps stored Tween references without checking IsActive(). Every patch here
/// guards with IsActive() + null checks.
/// </summary>
    public static class TweenSafetyPatches
    {
        /// <summary>
        /// scrVfxPlus.Reset() 会清理 filterTween，但未清理 pausedTweens。
        /// 导致过期的 Tween 引用跨关卡累积，在下一次恢复(Play)时对已回收的 Tween 进行操作。
        /// </summary>
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

        /// <summary>
        /// scrDecoration.OnDestroy() 遍历 eventTweens 直接调用 Kill(false)，
        /// 未对可能已回收/无效的 Tween 做安全检查。
        /// </summary>
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

        /// <summary>
        /// ffxPlusBase.Kill() 遍历 eventTweens 直接调用 Kill(false)，
        /// 没有 null 检查也没有 IsActive() 检查。
        /// eventTweens 是 protected 属性，通过 AccessTools 反射读取。
        /// </summary>
        [HarmonyPatch(typeof(ffxPlusBase), "Kill")]
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

        /// <summary>ScrubToTime 在 Goto()/Kill() 前不查 IsActive()，也未检查活跃性就加入 pausedTweens。</summary>
        [HarmonyPatch(typeof(ffxPlusBase), "ScrubToTime")]
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
