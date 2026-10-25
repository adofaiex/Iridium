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
    /// <summary>Rewrites the decoration hot paths (tag lookup, SetRotation, SetScale) into allocation-light, dirty-checked equivalents.</summary>
    public static class DecorationHotPathPatches
    {
        // Each nested patch class carries its own [IriPatch]: annotating only the outer class makes the
        // Harmony container skip them and the whole group reports NotFound.

        private static readonly HashSet<scrDecoration> _seen = new HashSet<scrDecoration>();

        [IriPatch(Path = "optimizer/decor", Pre = typeof(OptimizerSettings), Condition = "enableOptimizer")]
        [HarmonyPatch]
        public static class GetTaggedDecorationsPatch
        {
            // The generic overload makes AccessTools.Method ambiguous; pick the non-generic one.
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

        // SetRotation 脏检查

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

        // SetScale 脏检查

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
