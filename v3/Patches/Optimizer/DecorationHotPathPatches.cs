using Iridium.Config;
using ADOFAI;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace Iridium.Patches.Optimizer
{
    /// <summary>
    /// 装饰物热路径纯性能重写（行为语义与原版逐一对齐）：
    ///
    /// 1. GetTaggedDecorations：原版是延迟 LINQ 链（Where+SelectMany+Distinct），
    ///    每次 StartEffect 重新求值并分配枚举器 —— MoveDecorations 密集的谱面
    ///    （单谱可达 9 万+ 事件）每秒触发数百次。改为直接字典查找 + 物化 List。
    /// 2. SetRotation：UpdatePosition 每帧无条件重写 pivotTrans.rotation。
    ///    输入（angle/startRot）未变且不依赖外部状态（stickToFloor/lockRotation）
    ///    时跳过 —— 消除每帧无效 transform 写入。
    /// 3. SetScale：同上，另需 camScaleMultiplier 未变（相机缩放会改变结果）。
    ///
    /// 注意：每个嵌套补丁类各自带 [IriPatch]，由 PatchManager 逐个注册。
    /// （只在外层类标注的话 Harmony 容器不会递归到这里，整组补丁会 NotFound。）
    /// </summary>
    public static class DecorationHotPathPatches
    {
        // ---- 1. GetTaggedDecorations ----

        private static readonly HashSet<scrDecoration> _seen = new HashSet<scrDecoration>();

        [IriPatch(Path = "optimizer/decor", Pre = typeof(OptimizerSettings), Condition = "enableOptimizer")]
        [HarmonyPatch]
        public static class GetTaggedDecorationsPatch
        {
            // GetTaggedDecorations<T>(IEnumerable<string>) 与同参的非泛型重载会让
            // AccessTools.Method 产生歧义，这里显式排除泛型版本。
            [HarmonyTargetMethod]
            public static MethodBase ResolveTarget()
            {
                foreach (var method in typeof(scrDecorationManager).GetMethods(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (method.Name != "GetTaggedDecorations" || method.IsGenericMethod) continue;
                    var parameters = method.GetParameters();
                    if (parameters.Length == 1 && parameters[0].ParameterType == typeof(IEnumerable<string>))
                        return method;
                }
                return null!;
            }

            [HarmonyPrefix]
            public static bool Prefix(scrDecorationManager __instance, IEnumerable<string> tags,
                ref IEnumerable<scrDecoration> __result)
            {
                var dict = __instance.taggedDecorations;
                if (dict == null)
                {
                    __result = new List<scrDecoration>();
                    return false;
                }

                var result = new List<scrDecoration>(16);
                _seen.Clear();
                foreach (var tag in tags)
                {
                    if (tag != null && dict.TryGetValue(tag, out var list))
                    {
                        foreach (var dec in list)
                        {
                            if (_seen.Add(dec))
                                result.Add(dec);
                        }
                    }
                }
                __result = result;
                return false;
            }
        }

        // ---- 2. SetRotation 脏检查 ----

        private sealed class RotationState
        {
            public bool Applied;
            public float Angle = float.NaN;
            public float StartRot = float.NaN;
        }

        private static readonly ConditionalWeakTable<scrDecoration, RotationState> _rotStates = new();

        [IriPatch(Path = "optimizer/decor", Pre = typeof(OptimizerSettings), Condition = "enableOptimizer")]
        [HarmonyPatch(typeof(scrDecoration), nameof(scrDecoration.SetRotation))]
        public static class SetRotationSkipPatch
        {
            [HarmonyPrefix]
            public static bool Prefix(scrDecoration __instance, float angle)
            {
                // stickToFloor/lockRotation 的结果依赖每帧的外部状态，不能跳过
                if (__instance.stickToFloor || __instance.lockRotation)
                    return true;

                var state = _rotStates.GetOrCreateValue(__instance);
                if (state.Applied && state.Angle == angle && state.StartRot == __instance.startRot)
                    return false; // 输入未变，transform 写入是无效功

                state.Applied = true;
                state.Angle = angle;
                state.StartRot = __instance.startRot;
                return true;
            }
        }

        // ---- 3. SetScale 脏检查 ----

        private sealed class ScaleState
        {
            public bool Applied;
            public Vector2 Scale = Vector2.positiveInfinity;
            public Vector2 CamScale = Vector2.positiveInfinity;
        }

        private static readonly ConditionalWeakTable<scrDecoration, ScaleState> _scaleStates = new();

        [IriPatch(Path = "optimizer/decor", Pre = typeof(OptimizerSettings), Condition = "enableOptimizer")]
        [HarmonyPatch(typeof(scrDecoration), nameof(scrDecoration.SetScale))]
        public static class SetScaleSkipPatch
        {
            [HarmonyPrefix]
            public static bool Prefix(scrDecoration __instance, Vector2 scale)
            {
                if (__instance.stickToFloor)
                    return true; // 结果依赖 parentFloor 每帧缩放

                var state = _scaleStates.GetOrCreateValue(__instance);
                Vector2 camScale = __instance.camScaleMultiplier;
                if (state.Applied && state.Scale == scale && state.CamScale == camScale)
                    return false;

                state.Applied = true;
                state.Scale = scale;
                state.CamScale = camScale;
                return true;
            }
        }
    }
}
