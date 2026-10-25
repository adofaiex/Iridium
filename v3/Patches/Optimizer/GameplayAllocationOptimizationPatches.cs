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
    /// <summary>Kills the per-frame allocations in the gameplay hot paths (planet overlap, dead tween sequence, reflection filter invoke).</summary>
    public static class GameplayAllocationOptimizationPatches
    {
        // Each nested patch class carries its own [IriPatch]: annotating only the outer class makes the
        // Harmony container skip them and the whole group reports NotFound.

        private static readonly Collider2D[] _overlapBuffer = new Collider2D[64];
        private static readonly Collider2D[] _emptyColliders = new Collider2D[0];

        /// <summary>Zero-allocation OverlapCircleAll: shared empty array when there is no hit, exact-size array otherwise, vanilla call on buffer overflow.</summary>
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

        /// <summary>pulse == None means vanilla creates the Sequence and immediately breaks without using it, so null is equivalent.</summary>
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

        private static readonly Dictionary<MethodInfo, Action<object, RenderTexture, RenderTexture>?> _invokeCache
            = new Dictionary<MethodInfo, Action<object, RenderTexture, RenderTexture>?>();

        /// <summary>Delegate-cached stand-in for MethodInfo.Invoke; wrong signatures fall back to the reflective call.</summary>
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
