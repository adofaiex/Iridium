using System;
using ADOFAI;
using HarmonyLib;
using UnityEngine;

namespace Iridium.Patches
{
    /// <summary>
/// scrFloor.Awake instantiates a topGlow per floor even when most editor floors never
/// glow. In gameplay it is still created eagerly (the game logic touches it directly);
/// in the editor it points at a shared hidden placeholder until something lights it.
/// </summary>
    public static class LazyTopGlowPatch
    {
        private static AccessTools.FieldRef<scrFloor, scrController>? _controllerRef;
        private static AccessTools.FieldRef<scrFloor, scrConductor>? _conductorRef;
        private static AccessTools.FieldRef<scrFloor, RDConstants>? _gcRef;
        private static AccessTools.FieldRef<scrFloor, scrVfx>? _vfxRef;
        private static AccessTools.FieldRef<scrFloor, scrVfxPlus>? _vfxPlusRef;
        private static bool _refsInitialized;

        private static SpriteRenderer? _dummy;

        private static bool Enabled =>
            Main.Settings.optimizer.optimizeLargeLevelLoading;

        private static bool InitRefs()
        {
            if (_refsInitialized) return _controllerRef != null;
            _refsInitialized = true;
            try
            {
                _controllerRef = AccessTools.FieldRefAccess<scrFloor, scrController>("controller");
                _conductorRef = AccessTools.FieldRefAccess<scrFloor, scrConductor>("conductor");
                _gcRef = AccessTools.FieldRefAccess<scrFloor, RDConstants>("gc");
                _vfxRef = AccessTools.FieldRefAccess<scrFloor, scrVfx>("vfx");
                _vfxPlusRef = AccessTools.FieldRefAccess<scrFloor, scrVfxPlus>("vfxPlus");
            }
            catch (Exception e)
            {
                Main.Logger?.Log($"[LazyTopGlow] field refs failed: {e.Message}");
            }
            return _controllerRef != null && _conductorRef != null && _gcRef != null && _vfxRef != null;
        }

        /// <summary>共享占位：隐藏、不参与渲染，可安全接收 SetActive/color 等写入。</summary>
        internal static SpriteRenderer Dummy
        {
            get
            {
                if (_dummy == null)
                {
                    var go = new GameObject("Iridium_TopGlowDummy");
                    go.hideFlags = HideFlags.HideAndDontSave;
                    go.SetActive(false);
                    _dummy = go.AddComponent<SpriteRenderer>();
                    // 兜底：即使占位对象被 SetActive 也不渲染任何东西
                    _dummy.enabled = false;
                }
                return _dummy;
            }
        }

        internal static bool IsDummy(SpriteRenderer? sr) => sr != null && ReferenceEquals(sr, _dummy);

        /// <summary>实体化一块砖自己的 topGlow（逻辑对齐 scrFloor.Awake 原版）。</summary>
        internal static SpriteRenderer Create(scrFloor floor)
        {
            var prefab = RDConstants.data.prefab_topGlow;
            var go = UnityEngine.Object.Instantiate(prefab, floor.transform);
            go.transform.SetParent(floor.transform);
            go.name = "topGlow";
            var sr = go.GetComponent<SpriteRenderer>();
            sr.transform.ScaleXY(floor.dontChangeMySprite ? 0.32f : 1.28f);
            var toggle = go.GetComponent<ToggleVisible>();
            if (toggle != null) toggle.scriptToToggle = floor;
            floor.topGlow = sr;
            return sr;
        }

        /// <summary>需要真实 topGlow 时调用：已有真实对象直接返回，占位/空则创建。</summary>
        internal static SpriteRenderer Ensure(scrFloor floor)
        {
            if (floor.topGlow != null && !IsDummy(floor.topGlow)) return floor.topGlow;
            floor.topGlow = null;
            return Create(floor);
        }

        /// <summary>复刻 scrFloor.Awake 的必要初始化；callComponentAwake=true 时额外手动调 FloorRenderer.Awake（幂等）。</summary>
        internal static bool TryInitialize(scrFloor floor, bool callComponentAwake)
        {
            if (floor == null || !InitRefs()) return false;
            try
            {
                var controller = scrController.instance;
                var conductor = scrConductor.instance;
                var gc = RDConstants.data;
                var vfx = scrVfx.instance;
                var vfxPlus = scrVfxPlus.instance;

                _controllerRef!(floor) = controller;
                _conductorRef!(floor) = conductor;
                _gcRef!(floor) = gc;
                _vfxRef!(floor) = vfx;
                if (_vfxPlusRef != null) _vfxPlusRef(floor) = vfxPlus;

                var t = floor.transform;
                floor.thisTransform = t;
                if (!callComponentAwake)
                {
                    floor.startPos = t.position;
                    floor.startRot = t.rotation.eulerAngles;
                    floor.tweenRot = floor.startRot;
                }
                // B 路径不写 startPos/startRot/tweenRot：MakeLevel 收尾会统一重写这三项

                if (floor.floorRenderer == null)
                {
                    floor.floorRenderer = floor.GetComponent<FloorRenderer>();
                    if (floor.floorRenderer == null)
                    {
                        floor.floorRenderer = floor.gameObject.AddComponent<FloorSpriteRenderer>();
                        floor.floorRenderer.renderer = floor.GetComponent<SpriteRenderer>();
                        floor.legacyFloorSpriteRenderer = floor.GetComponent<SpriteRenderer>();
                    }
                }
                if (callComponentAwake && floor.floorRenderer != null)
                {
                    // 虚拟派发到 FloorMeshRenderer.Awake / FloorSpriteRenderer.Awake（幂等）
                    floor.floorRenderer.Awake();
                }
                if (floor.setHitsound == null)
                {
                    floor.setHitsound = floor.GetComponent<ffxSetHitsound>();
                }
                if (controller.gameworld && floor.bottomGlow != null)
                    floor.bottomGlow.gameObject.SetActive(false);

                bool needsGlow = controller.gameworld
                    || vfx.tileFlashStyle == TileFlashStyle.AlwaysOn
                    || floor.hasLit;
                if (needsGlow)
                    Create(floor);
                else
                    floor.topGlow = Dummy;

                if (controller.gameworld && !ADOBase.isScnGame)
                    floor.stickToFloor = controller.stickToFloor;
                return true;
            }
            catch (Exception e)
            {
                Main.Logger?.Log($"[LazyTopGlow] init failed: {e}");
                return false;
            }
        }

        #region scrFloor.Awake replacement

        [HarmonyPatch(typeof(scrFloor), nameof(scrFloor.Awake))]
        public static class AwakePatch
        {
            [HarmonyPrefix]
            public static bool Prefix(scrFloor __instance)
            {
                if (!Enabled || !InitRefs()) return true;
                // B 的未激活克隆已 FastInit 过，激活时跳过
                if (ChunkedFloorSpawnPatch.IsInitialized(__instance)) return false;

                if (!TryInitialize(__instance, callComponentAwake: false)) return true;
                ChunkedFloorSpawnPatch.MarkInitialized(__instance);
                return false;
            }
        }

        #endregion

        #region Ensure at access / activation sites

        [HarmonyPatch(typeof(scrFloor), nameof(scrFloor.Reset))]
        public static class ResetPatch
        {
            [HarmonyPrefix]
            public static void Prefix(scrFloor __instance)
            {
                if (!Enabled) return;
                // 原版 Reset 会 Destroy(topGlow) 再调 Awake()，必须先清「已初始化」标记，
                // 否则那次 Awake 被跳过、砖块不再重建 topGlow
                ChunkedFloorSpawnPatch.UnmarkInitialized(__instance);
                // 占位对象是共享的，先摘掉引用（原版会 Destroy 它）
                if (IsDummy(__instance.topGlow)) __instance.topGlow = null;
            }
        }

        [HarmonyPatch(typeof(scrFloor), nameof(scrFloor.Start))]
        public static class StartPatch
        {
            [HarmonyPrefix]
            public static void Prefix(scrFloor __instance)
            {
                if (!Enabled) return;
                if (!IsDummy(__instance.topGlow)) return;

                var vfx = scrVfx.instance;
                bool active = vfx.tileFlashStyle == TileFlashStyle.AlwaysOn
                    || (__instance.hasLit && vfx.tileFlashStyle != TileFlashStyle.AlwaysBlack);
                if (active) Ensure(__instance);
            }
        }

        [HarmonyPatch(typeof(scrFloor), nameof(scrFloor.LightUp))]
        public static class LightUpPatch
        {
            [HarmonyPrefix]
            public static void Prefix(scrFloor __instance)
            {
                if (!Enabled) return;
                if (IsDummy(__instance.topGlow)) Ensure(__instance);
            }
        }

        [HarmonyPatch(typeof(scrFloor), nameof(scrFloor.SetToRandomColor))]
        public static class RandomColorPatch
        {
            [HarmonyPrefix]
            public static void Prefix(scrFloor __instance)
            {
                if (!Enabled) return;
                if (IsDummy(__instance.topGlow)) Ensure(__instance);
            }
        }

        /// <summary>原版会把 seqID 之前的 topGlow 全部点亮，先实体化以免点亮共享占位。</summary>
        [HarmonyPatch(typeof(scnGame), "FinishCustomLevelLoading")]
        public static class FinishLoadingPatch
        {
            [HarmonyPrefix]
            public static void Prefix(int seqID)
            {
                if (!Enabled) return;
                try
                {
                    var lm = scrLevelMaker.instance;
                    var floors = lm != null ? lm.listFloors : null;
                    if (floors == null) return;
                    for (int i = 0; i < floors.Count; i++)
                    {
                        var floor = floors[i];
                        if (floor == null || floor.seqID > seqID) break;
                        if (IsDummy(floor.topGlow)) Ensure(floor);
                    }
                }
                catch (Exception e)
                {
                    Main.Logger?.Log($"[LazyTopGlow] FinishCustomLevelLoading failed: {e.Message}");
                }
            }
        }

        /// <summary>对象装饰物的地板开启 glow 时同样要实体化。</summary>
        [HarmonyPatch(typeof(scrObjectDecoration), nameof(scrObjectDecoration.SetFloorGlowEnabled))]
        public static class ObjectDecorationGlowPatch
        {
            [HarmonyPrefix]
            public static void Prefix(scrObjectDecoration __instance, bool enable)
            {
                if (!Enabled || !enable) return;
                try
                {
                    if (__instance.floor != null && IsDummy(__instance.floor.topGlow))
                        Ensure(__instance.floor);
                }
                catch (Exception e)
                {
                    Main.Logger?.Log($"[LazyTopGlow] SetFloorGlowEnabled failed: {e.Message}");
                }
            }
        }

        #endregion
    }
}
