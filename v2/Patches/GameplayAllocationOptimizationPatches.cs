using DG.Tweening;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using UnityEngine;

namespace Iridium.Patches
{
    /// <summary>
    /// v3 移植：消除游戏逻辑热路径中的每帧堆分配（适配 ADOFAI 2.9.8）。
    ///
    /// 1. scrPlanet.Update: 每帧每星球 Physics2D.OverlapCircleAll(pos, radius)
    ///    分配新的 Collider2D[]。改为 NonAlloc 缓冲，常态（无命中）返回共享
    ///    空数组，命中时才分配精确长度结果。
    /// 2. scrVisualDecoration.UpdateShader: 滤镜 OnRenderImage 的 MethodInfo.Invoke
    ///    反射调用（参数校验 + 装箱 + 数组分配）改为按 MethodInfo 缓存编译委托。
    ///
    /// （3.3.x 的 scrFloor.Update 空 DOTween.Sequence 问题在 2.9.8 不存在，未移植。）
    /// </summary>
    public static class GameplayAllocationOptimizationPatches
    {
        // ── 1. scrPlanet.Update: non-alloc hitbox overlap ──────────────────

        private static readonly Collider2D[] _overlapBuffer = new Collider2D[64];
        private static readonly Collider2D[] _emptyColliders = new Collider2D[0];

        public static Collider2D[] OverlapCircleAllTrimmed(Vector2 pos, float radius)
        {
            if (Main.Settings?.optimizer.optimizeGameplayAllocations != true)
                return Physics2D.OverlapCircleAll(pos, radius);
            int count;
            try
            {
                count = Physics2D.OverlapCircleNonAlloc(pos, radius, _overlapBuffer);
            }
            catch (Exception)
            {
                return Physics2D.OverlapCircleAll(pos, radius);
            }
            if (count == 0) return _emptyColliders;
            if (count > _overlapBuffer.Length)
                return Physics2D.OverlapCircleAll(pos, radius);
            var result = new Collider2D[count];
            Array.Copy(_overlapBuffer, result, count);
            return result;
        }

        [HarmonyPatch(typeof(scrPlanet), "Update")]
        public static class PlanetOverlapAllocPatch
        {
            private static readonly MethodInfo? _vanillaOverlap = AccessTools.Method(
                typeof(Physics2D), nameof(Physics2D.OverlapCircleAll),
                new[] { typeof(Vector2), typeof(float) });
            private static readonly MethodInfo? _trimmedOverlap = AccessTools.Method(
                typeof(GameplayAllocationOptimizationPatches), nameof(OverlapCircleAllTrimmed));

            [HarmonyTranspiler]
            public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                if (_vanillaOverlap == null || _trimmedOverlap == null)
                {
                    Main.Logger?.Error("[GameplayAlloc] OverlapCircleAll overload not found, skipping");
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
                return codes;
            }
        }

        // ── 2. scrVisualDecoration.UpdateShader: reflection-free invoke ────

        private static readonly Dictionary<MethodInfo, Action<object, RenderTexture, RenderTexture>?> _invokeCache
            = new Dictionary<MethodInfo, Action<object, RenderTexture, RenderTexture>?>();

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

        [HarmonyPatch(typeof(scrVisualDecoration), "UpdateShader")]
        public static class DecorationFilterInvokePatch
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
                    Main.Logger?.Error("[GameplayAlloc] MethodInfo.Invoke not found, skipping");
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
                    Main.Logger?.Error("[GameplayAlloc] Invoke call site not found in UpdateShader");
                return codes;
            }
        }
    }

    internal static class MethodInfoSignatureExtensions
    {
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
