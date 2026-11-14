using Iridium.Config;
using HarmonyLib;
using Iridium.Runtime;
using System.Reflection;
using UnityEngine;

namespace Iridium.Patches
{
    /// <summary>Harmony targets resolved at runtime, so no compile-time coupling to a game version.</summary>
    internal static class AdaptivePatches
    {
        [IriPatch(Path = "optimizer/texture", Pre = typeof(OptimizerSettings), Condition = "enableOptimizer")]
        public sealed class TextureNameCleanup : MonoAdaptivePatch
        {
            public override string Id => "TextureNameCleanup";

            protected override MethodBase? GetTargetMethod()
            {
                var method = AccessTools.Method(typeof(TextureManager), "ApplyOptionsToTexture");
                if (method != null) return method;

                return AccessTools.Method(typeof(TextureManager), "LoadTexture");
            }

            protected override MethodInfo Postfix =>
                AccessTools.Method(typeof(TextureNameCleanup), nameof(OnTextureLoaded))!;

            private static void OnTextureLoaded(Texture2D __result)
            {
                if (__result != null && __result.name.EndsWith("(Clone)"))
                    __result.name = __result.name.Substring(0, __result.name.Length - 7);
            }
        }

        [IriPatch(Path = "optimizer/decor", Pre = typeof(OptimizerSettings), Condition = "enableOptimizer")]
        public sealed class DecorationScalingCustomSprite : MonoAdaptivePatch
        {
            public override string Id => "DecorationScalingCustomSprite";

            protected override MethodBase? GetTargetMethod()
            {
                return FindSetSprite(typeof(TextureManager.CustomSprite));
            }

            protected override MethodInfo Postfix =>
                AccessTools.Method(typeof(DecorationScalingCustomSprite), nameof(OnSetSprite))!;

            private static void OnSetSprite(scrVisualDecoration __instance)
            {
                if (GCS.internalLevelName != null) return;
                var sprite = __instance.spriteRenderer?.sprite;
                if (sprite?.texture == null) return;
                if (Iridium.Patches.Optimizer.OptimizerShared.TryGetDecorRatioForTexture(sprite.texture, out Vector3 ratio))
                    ApplyRatioInvoke(__instance, ratio);
            }
        }

        [IriPatch(Path = "optimizer/decor", Pre = typeof(OptimizerSettings), Condition = "enableOptimizer")]
        public sealed class DecorationScalingSprite : MonoAdaptivePatch
        {
            public override string Id => "DecorationScalingSprite";

            protected override MethodBase? GetTargetMethod()
            {
                return FindSetSprite(typeof(Sprite));
            }

            protected override MethodInfo Postfix =>
                AccessTools.Method(typeof(DecorationScalingSprite), nameof(OnSetSprite))!;

            private static void OnSetSprite(scrVisualDecoration __instance)
            {
                if (GCS.internalLevelName != null) return;
                var sprite = __instance.spriteRenderer?.sprite;
                if (sprite?.texture == null) return;
                if (Iridium.Patches.Optimizer.OptimizerShared.TryGetDecorRatioForTexture(sprite.texture, out Vector3 ratio))
                    ApplyRatioInvoke(__instance, ratio);
            }
        }

        private static MethodBase? FindSetSprite(System.Type firstParamType)
        {
            foreach (var m in typeof(scrVisualDecoration).GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (m.Name != "SetSprite") continue;
                var p = m.GetParameters();
                if (p.Length >= 1 && p[0].ParameterType == firstParamType)
                    return m;
            }
            return null;
        }

        private static void ApplyRatioInvoke(scrVisualDecoration instance, Vector3 ratio)
        {
            var method = AccessTools.Method(typeof(Iridium.Patches.Optimizer.OptimizerShared), "ApplyDecorRatioScaling");
            method?.Invoke(null, new object[] { instance, ratio });
        }
    }
}
