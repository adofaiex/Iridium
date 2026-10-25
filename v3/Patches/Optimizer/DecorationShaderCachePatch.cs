using Iridium.Config;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using DG.Tweening;
using UnityEngine;

namespace Iridium.Patches.Optimizer
{
    /// <summary>Skips scrVisualDecoration.UpdateShader when nothing it depends on changed; only filters proven time-independent are cacheable.</summary>
    [IriPatch(Path = "optimizer/decor", Pre = typeof(OptimizerSettings), Condition = "enableOptimizer")]
    [HarmonyPatch(typeof(scrVisualDecoration), "UpdateShader")]
    public static class DecorationShaderCachePatch
    {
        private sealed class CacheState
        {
            public bool Valid;
            public bool PendingDirty;
            public Color Color;
            public float Opacity;
            public float RepeatX, RepeatY;
            public Sprite? Sprite;
            public bool Visible;
            public bool MeshEnabled;
            public int FilterSig;
        }

        private static readonly ConditionalWeakTable<scrVisualDecoration, CacheState> _cache = new();

        private static readonly AccessTools.FieldRef<scrVisualDecoration, bool>? _meshEnabledRef =
            AccessTools.FieldRefAccess<scrVisualDecoration, bool>("meshRendererEnabled");

        private static readonly AccessTools.FieldRef<scrVisualDecoration, MaskingType>? _maskingTypeRef =
            AccessTools.FieldRefAccess<scrVisualDecoration, MaskingType>("maskingType");

        // The 18 legacy filters whose GLSL never references _Time/_TimeX/_SinTime; anything else animates on its own and must stay uncached.
        private static readonly HashSet<string> _parameterDrivenFilters = new()
        {
            "CameraFilterPackLegacy_Blur_Blurry",
            "CameraFilterPackLegacy_Blur_Focus",
            "CameraFilterPackLegacy_Blur_GaussianBlur",
            "CameraFilterPackLegacy_Color_Chromatic_Aberration",
            "CameraFilterPackLegacy_Color_Contrast",
            "CameraFilterPackLegacy_Color_GrayScale",
            "CameraFilterPackLegacy_Color_Invert",
            "CameraFilterPackLegacy_Color_Sepia",
            "CameraFilterPackLegacy_Distortion_FishEye",
            "CameraFilterPackLegacy_Edge_BlackLine",
            "CameraFilterPackLegacy_Edge_Neon",
            "CameraFilterPackLegacy_FX_Hexagon_Black",
            "CameraFilterPackLegacy_FX_superDot",
            "CameraFilterPackLegacy_Pixel_Pixelisation",
            "CameraFilterPackLegacy_Pixelisation_OilPaint",
            "CameraFilterPackLegacy_Sharpen_Sharpen",
            "CameraFilterPackLegacy_TV_Posterize",
            "CameraFilterPackLegacy_TV_Video3D",
        };

        private static Dictionary<GameObject, Dictionary<string, Dictionary<string, Tween>>>? _filterFieldTweens;
        private static bool _filterFieldTweensResolved;

        [HarmonyPrefix]
        public static bool Prefix(scrVisualDecoration __instance, bool disable = false)
        {
            var opt = Main.Settings.optimizer;
            bool shaderCache = opt.optimizeDecorationShaderCache;
            bool filterCache = opt.optimizeFilters;
            if (!shaderCache && !filterCache) return true;
            if (disable || __instance.isMask()) return true;
            if (_maskingTypeRef == null || _maskingTypeRef(__instance) != MaskingType.None) return true;
            if (ADOBase.isEditingLevel) return true;

            var cfp = __instance.cfpCache;
            bool hasFilters = cfp != null && cfp.Length > 0;
            if (hasFilters)
            {
                if (!filterCache) return true;
                if (!AllFiltersParameterDriven(cfp!)) return true;
            }
            else if (!shaderCache)
            {
                return true;
            }

            var sprite = __instance.spriteRenderer != null ? __instance.spriteRenderer.sprite : null;
            if (sprite == null) return true;
            if (_meshEnabledRef == null) return true;

            var state = _cache.GetOrCreateValue(__instance);
            bool visible = __instance.GetVisible();
            bool meshEnabled = _meshEnabledRef(__instance);
            int filterSig = hasFilters ? ComputeFilterSignature(cfp!) : 0;
            bool tweening = hasFilters && HasActiveFilterTween(__instance.gameObject);

            bool changed = !state.Valid
                || state.PendingDirty
                || tweening
                || state.Color != __instance.color
                || state.Opacity != __instance.opacity
                || state.RepeatX != __instance.repeatX
                || state.RepeatY != __instance.repeatY
                || state.Sprite != sprite
                || state.Visible != visible
                || state.MeshEnabled != meshEnabled
                || state.FilterSig != filterSig;

            if (!changed) return false; // 本帧无任何输入变化，跳过 UpdateShader（含滤镜 blit 链）

            state.Valid = true;
            state.PendingDirty = false;
            state.Color = __instance.color;
            state.Opacity = __instance.opacity;
            state.RepeatX = __instance.repeatX;
            state.RepeatY = __instance.repeatY;
            state.Sprite = sprite;
            state.Visible = visible;
            state.MeshEnabled = meshEnabled;
            state.FilterSig = filterSig;
            return true;
        }

        private static bool AllFiltersParameterDriven(MonoBehaviour[] cfp)
        {
            for (int i = 0; i < cfp.Length; i++)
            {
                var mb = cfp[i];
                if (mb == null || !mb.enabled) continue;
                if (!_parameterDrivenFilters.Contains(mb.GetType().Name)) return false;
            }
            return true;
        }

        /// <summary>事件应用后标记该装饰物下一帧必须重绘。</summary>
        internal static void MarkDirty(scrVisualDecoration deco)
        {
            _cache.GetOrCreateValue(deco).PendingDirty = true;
        }

        /// <summary>该装饰物的滤镜参数是否仍有 tween 在写（暂停中的也算）。</summary>
        private static bool HasActiveFilterTween(GameObject go)
        {
            if (!_filterFieldTweensResolved)
            {
                _filterFieldTweensResolved = true;
                var field = AccessTools.Field(typeof(ffxSetFilterAdvancedPlus), "filterFieldTweens");
                _filterFieldTweens = field?.GetValue(null)
                    as Dictionary<GameObject, Dictionary<string, Dictionary<string, Tween>>>;
            }
            if (_filterFieldTweens == null || !_filterFieldTweens.TryGetValue(go, out var byName) || byName == null)
                return false;

            foreach (var byField in byName.Values)
            {
                if (byField == null) continue;
                foreach (var tween in byField.Values)
                {
                    if (tween != null && tween.IsActive() && !tween.IsComplete()) return true;
                }
            }
            return false;
        }

        /// <summary>滤镜签名：类型名 + 启用位；字段变化由打脏/tween 检测覆盖。</summary>
        private static int ComputeFilterSignature(MonoBehaviour[] cfpCache)
        {
            unchecked
            {
                int hash = 17;
                hash = hash * 31 + cfpCache.Length;
                foreach (var mb in cfpCache)
                {
                    hash = hash * 31 + (mb == null ? 0 : mb.GetType().Name.GetHashCode());
                    hash = hash * 31 + (mb != null && mb.enabled ? 1 : 0);
                }
                return hash;
            }
        }
    }

    /// <summary>Marks a decoration's filter cache dirty when its filter fields or enabled flags change.</summary>
    [HarmonyPatch(typeof(ffxSetFilterAdvancedPlus), "StartEffect")]
    [IriPatch(Path = "optimizer/decor", Pre = typeof(OptimizerSettings), Condition = "enableOptimizer,optimizeFilters")]
    public static class DecorationFilterDirtyPatch
    {
        private static readonly AccessTools.FieldRef<ffxSetFilterAdvancedPlus, List<GameObject>>? _targetsRef =
            AccessTools.FieldRefAccess<ffxSetFilterAdvancedPlus, List<GameObject>>("targetObjects");

        [HarmonyPostfix]
        public static void Postfix(ffxSetFilterAdvancedPlus __instance)
        {
            if (!Main.Settings.optimizer.optimizeFilters) return;
            var targets = _targetsRef?.Invoke(__instance);
            if (targets == null) return;

            for (int i = 0; i < targets.Count; i++)
            {
                var go = targets[i];
                if (go == null) continue;
                var deco = go.GetComponent<scrVisualDecoration>();
                if (deco != null) DecorationShaderCachePatch.MarkDirty(deco);
            }
        }
    }
}
