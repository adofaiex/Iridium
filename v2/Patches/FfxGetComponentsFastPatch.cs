using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using ADOFAI;
using HarmonyLib;
using UnityEngine;

namespace Iridium.Patches
{
    /// <summary>
    /// 大物量谱面加载 / 开局时，ApplyEventsToFloors 会对每一块砖做
    /// GetComponents&lt;ffxPlusBase&gt;() 清理。几十万砖时这是几十万次原生组件查询
    /// （每次还分配一个数组），是加载卡顿的一大来源。
    ///
    /// ffxPlusBase 创建时都会登记到 scrFloor.plusEffects（游戏自身的活跃特效注册表），
    /// 因此 plusEffects 为空的砖块必然没有 ffx 组件，可直接返回空数组；
    /// 对确有特效的砖块走原版 GetComponents，行为不变。
    /// </summary>
    public static class FfxGetComponentsFastPatch
    {
        internal static ffxPlusBase[] GetFast(Component component)
        {
            if (component is scrFloor floor
                && floor.plusEffects != null
                && floor.plusEffects.Count == 0)
            {
                return Array.Empty<ffxPlusBase>();
            }
            return component.GetComponents<ffxPlusBase>();
        }

        [HarmonyPatch]
        public static class ApplyEventsFfxScanPatch
        {
            private static IEnumerable<MethodBase> TargetMethods()
            {
                var staticApply = AccessTools.Method(typeof(scnGame), "ApplyEventsToFloors",
                    new[]
                    {
                        typeof(List<scrFloor>), typeof(LevelData), typeof(scrLevelMaker), typeof(List<LevelEvent>)
                    });
                if (staticApply != null) yield return staticApply;

                // 编辑器里点播放时也会整表重置一次 triggered
                var finishLoading = AccessTools.Method(typeof(scnGame), "FinishCustomLevelLoading",
                    new[] { typeof(int), typeof(bool) });
                if (finishLoading != null) yield return finishLoading;

                // 组件写法谱面 / 场景重置时的整表扫描
                var setFxPlus = AccessTools.Method(typeof(scnGame), "SetFxPlusFromComponents",
                    new[] { typeof(List<scrFloor>), typeof(bool) });
                if (setFxPlus != null) yield return setFxPlus;

                var resetScene = AccessTools.Method(typeof(scnGame), "ResetScene", new[] { typeof(bool) });
                if (resetScene != null) yield return resetScene;
            }

            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                var original = AccessTools
                    .Method(typeof(Component), "GetComponents", Type.EmptyTypes)
                    ?.MakeGenericMethod(typeof(ffxPlusBase));
                var replacement = AccessTools.Method(typeof(FfxGetComponentsFastPatch), nameof(GetFast));

                if (original == null || replacement == null)
                {
                    foreach (var instruction in instructions) yield return instruction;
                    yield break;
                }

                foreach (var instruction in instructions)
                {
                    if (instruction.Calls(original))
                    {
                        instruction.opcode = OpCodes.Call;
                        instruction.operand = replacement;
                    }
                    yield return instruction;
                }
            }
        }
    }
}
