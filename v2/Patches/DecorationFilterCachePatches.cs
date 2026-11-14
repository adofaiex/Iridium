using Iridium.Config;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using DG.Tweening;
using UnityEngine;

namespace Iridium.Patches
{
    /// <summary>
/// Dirty-checks scrVisualDecoration.UpdateShader and caches filter output. Only
/// filters whose result depends solely on the source texture and public fields
/// are cached; time-animated ones (VHS, noise, rain, glitch) always redraw.
/// </summary>
    public static class DecorationFilterCachePatches
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

        private static readonly FieldInfo? _maskingTypeField =
            AccessTools.Field(typeof(scrVisualDecoration), "maskingType");

        private static readonly AccessTools.FieldRef<scrVisualDecoration, MaskingType>? _maskingTypeRef =
            _maskingTypeField == null ? null : AccessTools.FieldRefAccess<scrVisualDecoration, MaskingType>(_maskingTypeField);

        // 输出不依赖时间的 18 个 legacy 滤镜（GLSL 里不引用 _Time/_TimeX/_SinTime 等）。
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

        [HarmonyPatch(typeof(scrVisualDecoration), "UpdateShader")]
        public static class UpdateShaderCachePatch
        {
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

                var meshObj = __instance.meshRendererObj;
                bool meshEnabled = meshObj != null && meshObj.activeSelf;

                var state = _cache.GetOrCreateValue(__instance);
                bool visible = __instance.GetVisible();
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
        }

        [HarmonyPatch(typeof(ffxSetFilterAdvancedPlus), "StartEffect")]
        public static class FilterDirtyPatch
        {
            private static readonly FieldInfo? _targetsField =
                AccessTools.Field(typeof(ffxSetFilterAdvancedPlus), "targetObjects");

            private static readonly AccessTools.FieldRef<ffxSetFilterAdvancedPlus, List<GameObject>>? _targetsRef =
                _targetsField == null ? null : AccessTools.FieldRefAccess<ffxSetFilterAdvancedPlus, List<GameObject>>(_targetsField);

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
                    if (deco != null) MarkDirty(deco);
                }
            }
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

        internal static void MarkDirty(scrVisualDecoration deco)
        {
            _cache.GetOrCreateValue(deco).PendingDirty = true;
        }

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
}
