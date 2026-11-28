using ADOFAI;
using HarmonyLib;
using Iridium.Config;
using Iridium.Modules.HitchGuard;

namespace Iridium.Patches.Optimizer
{
    /// <summary>
    /// 进 gameplay 前（scnGame.Play）触发 JIT 预热：把热路径方法提前编译，
    /// 避免游玩中第一次执行到它们时的编译微卡顿。
    /// </summary>
    [IriPatch(Path = "optimizer/hitchGuard", Pre = typeof(OptimizerSettings), Condition = "enableOptimizer,enableJitWarmup")]
    [HarmonyPatch(typeof(scnGame), nameof(scnGame.Play))]
    public static class JitWarmupTriggerPatch
    {
        [HarmonyPrefix]
        public static void Prefix()
        {
            JitWarmup.WarmNow();
        }
    }
}
