using DG.Tweening;
using HarmonyLib;
using Iridium.Config;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using UnityEngine;

namespace Iridium.Patches.Optimizer
{
    /// <summary>
    /// 消除原版游戏逻辑热路径中的每帧堆分配 —— 游戏内最大的持续性 GC 压力源。
    ///
    /// 1. scrPlanet.Update: 每帧每星球调用 Physics2D.OverlapCircleAll，
    ///    分配一个新的 Collider2D[]。改为复用静态缓冲区做 NonAlloc 检测，
    ///    仅有命中结果时才分配精确大小的结果数组。
    /// 2. scrFloor.Update (Volume 轨迹颜色): pulse 为 None 时原版仍每帧
    ///    创建一个立即废弃的空 DOTween.Sequence。改为返回 null（该路径
    ///    创建后从未使用，语义完全不变）；pulse 不为 None 时保持原版行为。
    /// 3. scrVisualDecoration.UpdateShader: 对 CameraFilterPack 滤镜每帧
    ///    走 MethodInfo.Invoke 反射调用（参数校验 + 装箱）。改为按
    ///    MethodInfo 缓存编译委托后直接调用，反射失败时回退原版。
    ///
    /// 注意：每个嵌套补丁类各自带 [IriPatch]，由 PatchManager 逐个注册。
    /// （只在外层类标注的话 Harmony 容器不会递归到这里，整组补丁会 NotFound。）
    /// </summary>
    public static class GameplayAllocationOptimizationPatches
    {
        // ── 1. scrPlanet.Update: non-alloc hitbox overlap ──────────────────

        private static readonly Collider2D[] _overlapBuffer = new Collider2D[64];
        private static readonly Collider2D[] _emptyColliders = new Collider2D[0];

        /// <summary>
        /// OverlapCircleAll 的零分配等价物：常态（无命中）返回共享空数组，
        /// 命中时才分配精确长度的结果。缓冲区溢出时回退原版调用。
        /// </summary>
        public static Collider2D[] OverlapCircleAllTrimmed(Vector2 pos, float radius, int layerMask)
        {
            // 双保险：补丁应用状态下设置被外部改动时仍回退原版
            if (Main.Settings?.optimizer.optimizeGameplayAllocations != true)
                return Physics2D.OverlapCircleAll(pos, radius, layerMask);
            int count;
            try
            {
                count = Physics2D.OverlapCircleNonAlloc(pos, radius, _overlapBuffer, layerMask);
            }
            catch (Exception)
            {
                return Physics2D.OverlapCircleAll(pos, radius, layerMask);
            }
            if (count == 0) return _emptyColliders;
            if (count > _overlapBuffer.Length)
                return Physics2D.OverlapCircleAll(pos, radius, layerMask);
            var result = new Collider2D[count];
            Array.Copy(_overlapBuffer, result, count);
            return result;
        }

        [IriPatch(Path = "optimizer/gameplayAlloc", Pre = typeof(OptimizerSettings), Condition = "optimizeGameplayAllocations")]
        [HarmonyPatch(typeof(scrPlanet), "Update")]
        private static class PlanetOverlapAllocPatch
        {
            private static readonly MethodInfo? _vanillaOverlap = AccessTools.Method(
                typeof(Physics2D), nameof(Physics2D.OverlapCircleAll),
                new[] { typeof(Vector2), typeof(float), typeof(int) });
            private static readonly MethodInfo? _trimmedOverlap = AccessTools.Method(
                typeof(GameplayAllocationOptimizationPatches), nameof(OverlapCircleAllTrimmed));

            [HarmonyTranspiler]
            public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                if (_vanillaOverlap == null || _trimmedOverlap == null)
                {
                    Main.Logger?.Error("[GameplayAlloc] OverlapCircleAll method not found, skipping transpile");
                    return instructions;
                }
                var replaced = 0;
                var codes = instructions.ToList();
                for (int i = 0; i < codes.Count; i++)
                {
                    if (codes[i].operand is MethodInfo mi && mi.MethodSignatureEquals(_vanillaOverlap))
                    {
                        codes[i].operand = _trimmedOverlap;
                        replaced++;
                    }
                }
                if (replaced == 0)
                    Main.Logger?.Error("[GameplayAlloc] OverlapCircleAll call site not found in scrPlanet.Update");
                else
                    Main.Logger?.Log($"[GameplayAlloc] replaced {replaced} OverlapCircleAll call(s) in scrPlanet.Update");
                return codes;
            }
        }

        // ── 2. scrFloor.Update: abandon the dead Sequence for Volume+None ──

        /// <summary>
        /// 替换 Volume 分支开头的 DOTween.Sequence()。pulse == None 时原版
        /// 创建后从未使用该 Sequence（下一行直接 break），返回 null 完全等价；
        /// 其他 pulse 仍返回真实的 Sequence 以保持原版调度语义。
        /// </summary>
        public static Sequence? VolumeSequenceOrNull(scrFloor floor)
        {
            if (Main.Settings?.optimizer.optimizeGameplayAllocations != true)
                return DOTween.Sequence();
            return floor.specialColorPulse == TrackColorPulse.None ? null : DOTween.Sequence();
        }

        [IriPatch(Path = "optimizer/gameplayAlloc", Pre = typeof(OptimizerSettings), Condition = "optimizeGameplayAllocations")]
        [HarmonyPatch(typeof(scrFloor), "Update")]
        private static class FloorVolumeSequencePatch
        {
            private static readonly MethodInfo? _vanillaSequence = AccessTools.Method(
                typeof(DOTween), nameof(DOTween.Sequence), Type.EmptyTypes);
            private static readonly MethodInfo? _volumeSequence = AccessTools.Method(
                typeof(GameplayAllocationOptimizationPatches), nameof(VolumeSequenceOrNull));

            [HarmonyTranspiler]
            public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                if (_vanillaSequence == null || _volumeSequence == null)
                {
                    Main.Logger?.Error("[GameplayAlloc] DOTween.Sequence method not found, skipping transpile");
                    return instructions;
                }
                var codes = instructions.ToList();
                for (int i = 0; i < codes.Count; i++)
                {
                    if (codes[i].operand is MethodInfo mi && mi.MethodSignatureEquals(_vanillaSequence))
                    {
                        // 静态调用改为实例调用：先压入 this，再调 helper
                        codes[i] = new CodeInstruction(OpCodes.Ldarg_0);
                        codes.Insert(i + 1, new CodeInstruction(OpCodes.Call, _volumeSequence));
                        Main.Logger?.Log("[GameplayAlloc] patched dead Volume Sequence allocation in scrFloor.Update");
                        return codes;
                    }
                }
                Main.Logger?.Error("[GameplayAlloc] DOTween.Sequence call site not found in scrFloor.Update");
                return codes;
            }
        }

        // ── 3. scrVisualDecoration.UpdateShader: reflection-free invoke ────

        private static readonly Dictionary<MethodInfo, Action<object, RenderTexture, RenderTexture>?> _invokeCache
            = new Dictionary<MethodInfo, Action<object, RenderTexture, RenderTexture>?>();

        /// <summary>
        /// MethodInfo.Invoke 的委托化替代。OnRenderImage(RenderTexture, RenderTexture)
        /// 的开实例委托按 MethodInfo 缓存一次，之后零反射开销；
        /// 签名不符或委托创建失败的滤镜自动回退原版 Invoke。
        /// </summary>
        public static void FastInvoke(MethodInfo? mi, object target, object[] args)
        {
            if (mi == null) return;
            if (Main.Settings?.optimizer.optimizeGameplayAllocations != true
                || args == null || args.Length != 2
                || !(args[0] is RenderTexture) || !(args[1] is RenderTexture))
            {
                mi.Invoke(target, args);
                return;
            }
            if (!_invokeCache.TryGetValue(mi, out var dlg))
            {
                try
                {
                    dlg = AccessTools.MethodDelegate<Action<object, RenderTexture, RenderTexture>>(mi);
                }
                catch (Exception)
                {
                    dlg = null;
                }
                _invokeCache[mi] = dlg;
            }
            if (dlg != null)
                dlg(target, (RenderTexture)args[0], (RenderTexture)args[1]);
            else
                mi.Invoke(target, args);
        }

        [IriPatch(Path = "optimizer/gameplayAlloc", Pre = typeof(OptimizerSettings), Condition = "optimizeGameplayAllocations")]
        [HarmonyPatch(typeof(scrVisualDecoration), "UpdateShader")]
        private static class DecorationFilterInvokePatch
        {
            private static readonly MethodInfo? _vanillaInvoke = AccessTools.Method(
                typeof(MethodInfo), nameof(MethodInfo.Invoke),
                new[] { typeof(object), typeof(object[]) });
            private static readonly MethodInfo? _fastInvoke = AccessTools.Method(
                typeof(GameplayAllocationOptimizationPatches), nameof(FastInvoke));

            [HarmonyTranspiler]
            public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                if (_vanillaInvoke == null || _fastInvoke == null)
                {
                    Main.Logger?.Error("[GameplayAlloc] MethodInfo.Invoke not found, skipping transpile");
                    return instructions;
                }
                var replaced = 0;
                var codes = instructions.ToList();
                for (int i = 0; i < codes.Count; i++)
                {
                    if (codes[i].operand is MethodInfo mi && mi.MethodSignatureEquals(_vanillaInvoke))
                    {
                        codes[i].operand = _fastInvoke;
                        replaced++;
                    }
                }
                if (replaced == 0)
                    Main.Logger?.Error("[GameplayAlloc] MethodInfo.Invoke call site not found in scrVisualDecoration.UpdateShader");
                else
                    Main.Logger?.Log($"[GameplayAlloc] replaced {replaced} reflection Invoke call(s) in scrVisualDecoration.UpdateShader");
                return codes;
            }
        }
    }

    internal static class MethodInfoSignatureExtensions
    {
        /// <summary>参数类型逐一比较的签名匹配（重载区分用）。</summary>
        public static bool MethodSignatureEquals(this MethodInfo a, MethodInfo b)
        {
            if (a.DeclaringType != b.DeclaringType || a.Name != b.Name) return false;
            var pa = a.GetParameters();
            var pb = b.GetParameters();
            if (pa.Length != pb.Length) return false;
            for (int i = 0; i < pa.Length; i++)
                if (pa[i].ParameterType != pb[i].ParameterType) return false;
            return true;
        }
    }
}
