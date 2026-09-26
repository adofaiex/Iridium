using System;
using ADOFAI;
using HarmonyLib;
using UnityEngine;

namespace Iridium.Patches
{
    /// <summary>
    /// 大谱面加载优化 A2：线号文本（Num）按需生成（v2 / 2.9.8）。
    ///
    /// prefab 里串着一个 scrLetterPress（GameObject + RectTransform + Text）做砖块编号，
    /// 几十万砖时白克隆、编辑器里也根本看不全。
    ///
    /// 方案：大谱面批量实例化时临时把 meshFloor 换成「剥掉 Num 子物件」的模板，
    /// 编号文本改成砖块可见（OnBecameVisible / DrawFloorNums）时才从保留的模板克隆。
    /// 纯视觉对象，逻辑/存档不受影响。
    /// </summary>
    public static class LazyFloorNumberPatch
    {
        private const int FloorThreshold = 20000;

        private static AccessTools.FieldRef<scrLevelMaker, GameObject>? _meshFloorRef;

        private static GameObject? _strippedPrefab;
        private static GameObject? _numTemplate;
        private static scrLetterPress? _numDummy;
        private static bool _templateFailed;

        private static bool Enabled =>
            Main.Settings.optimizer.optimizeLargeLevelLoading;

        private static bool InitRefs()
        {
            if (_meshFloorRef != null) return true;
            try
            {
                _meshFloorRef = AccessTools.FieldRefAccess<scrLevelMaker, GameObject>("meshFloor");
            }
            catch (Exception e)
            {
                Main.Logger?.Log($"[LazyFloorNumber] meshFloor ref failed: {e.Message}");
            }
            return _meshFloorRef != null;
        }

        private static void PrepareTemplate(scrLevelMaker lm)
        {
            if (_strippedPrefab != null || _templateFailed) return;
            try
            {
                var prefab = _meshFloorRef!(lm);
                if (prefab == null) { _templateFailed = true; return; }

                var stripped = UnityEngine.Object.Instantiate(prefab);
                stripped.name = "Iridium_StrippedFloor";
                // 注意：Instantiate 会复制 hideFlags，不能给模板设 HideAndDontSave，
                // 否则克隆出的砖块会带着 DontSave 标志跨场景泄漏。
                stripped.hideFlags = HideFlags.None;
                UnityEngine.Object.DontDestroyOnLoad(stripped);

                var strippedFloor = stripped.GetComponent<scrFloor>();
                var numOnTemplate = strippedFloor != null ? strippedFloor.editorNumText : null;
                if (numOnTemplate == null)
                {
                    UnityEngine.Object.Destroy(stripped);
                    _templateFailed = true;
                    return;
                }

                var numGo = numOnTemplate.gameObject;
                numGo.transform.SetParent(null, false);
                numGo.SetActive(false);
                numGo.hideFlags = HideFlags.None;
                UnityEngine.Object.DontDestroyOnLoad(numGo);
                _numTemplate = numGo;

                strippedFloor.editorNumText = null!;
                _strippedPrefab = stripped;

                // 模板本身不是场景内容：挪到视野外。否则它会以默认材质停在 prefab 原位置
                // （谱面起点）渲染出一块额外的砖。克隆时游戏都显式传入 position/rotation，
                // 不受模板位置影响。
                stripped.transform.position = new Vector3(0f, -100000f, 0f);
            }
            catch (Exception e)
            {
                Main.Logger?.Log($"[LazyFloorNumber] template prepare failed: {e}");
                _templateFailed = true;
            }
        }

        private static bool IsDummy(scrLetterPress? text) => text != null && ReferenceEquals(text, _numDummy);

        internal static void EnsureNumText(scrFloor floor)
        {
            if (floor == null || floor.editorNumText != null) return;
            if (_numTemplate == null) return;
            try
            {
                var go = UnityEngine.Object.Instantiate(_numTemplate, floor.transform);
                go.name = _numTemplate.name;
                go.hideFlags = HideFlags.None;
                var press = go.GetComponent<scrLetterPress>();
                floor.editorNumText = press;
                if (press != null && press.letterText != null)
                    press.letterText.text = floor.seqID.ToString();
            }
            catch (Exception e)
            {
                Main.Logger?.Log($"[LazyFloorNumber] create num failed: {e.Message}");
            }
        }

        /// <summary>B（分帧激活）调用：确保剥离模板已就绪并返回它。</summary>
        internal static GameObject? EnsureStrippedTemplate(scrLevelMaker lm)
        {
            if (!InitRefs()) return null;
            PrepareTemplate(lm);
            return _strippedPrefab;
        }

        /// <summary>B 接管批量实例化时，A2 不再自己替换模板。</summary>
        internal static bool HandoverToChunkedSpawn => ChunkedFloorSpawnPatch.Enabled;

        #region 批量实例化模板替换

        [HarmonyPatch(typeof(scrLevelMaker), nameof(scrLevelMaker.InstantiateFloatFloors))]
        public static class StrippedPrefabPatch
        {
            [HarmonyPrefix]
            public static void Prefix(scrLevelMaker __instance, out GameObject? __state)
            {
                __state = null;
                if (!Enabled || !InitRefs()) return;
                // B（分帧激活）接管时由它统一替换模板并处理未激活克隆
                if (HandoverToChunkedSpawn) return;
                if (__instance.floorAngles == null || __instance.floorAngles.Length < FloorThreshold) return;

                PrepareTemplate(__instance);
                if (_strippedPrefab == null) return;

                var original = _meshFloorRef!(__instance);
                if (original == null || original == _strippedPrefab) return;
                __state = original;
                _meshFloorRef(__instance) = _strippedPrefab;
            }

            [HarmonyPostfix]
            public static void Postfix(scrLevelMaker __instance, GameObject? __state)
            {
                if (__state != null) _meshFloorRef!(__instance) = __state;
            }

            [HarmonyFinalizer]
            public static Exception? Finalizer(scrLevelMaker __instance, GameObject? __state, Exception? __exception)
            {
                if (__state != null) _meshFloorRef!(__instance) = __state;
                return __exception;
            }
        }

        #endregion

        #region 按需创建 / 空引用防护

        [HarmonyPatch(typeof(scrFloor), "OnBecameVisible")]
        public static class OnBecameVisiblePatch
        {
            [HarmonyPrefix]
            public static void Prefix(scrFloor __instance)
            {
                if (!Enabled || !ADOBase.isLevelEditor) return;
                if (__instance == null || __instance.isFake) return;
                if (__instance.editorNumText == null || IsDummy(__instance.editorNumText))
                    EnsureNumText(__instance);
            }
        }

        [HarmonyPatch(typeof(scnEditor), "DrawFloorNums")]
        public static class DrawFloorNumsPatch
        {
            [HarmonyPrefix]
            public static bool Prefix(scnEditor __instance)
            {
                if (!Enabled) return true;

                try
                {
                    var floors = __instance.floors;
                    if (floors == null) return false;

                    bool showNums = __instance.showFloorNums && !__instance.playMode;
                    for (int i = 0; i < floors.Count; i++)
                    {
                        var floor = floors[i];
                        if (floor == null || !floor.enabled) continue;
                        // B（分帧激活）期间未激活的砖块不创建编号，等激活后由 OnBecameVisible 补建
                        if (!floor.gameObject.activeInHierarchy) continue;

                        if (showNums && !floor.isFake)
                        {
                            if (floor.editorNumText == null || IsDummy(floor.editorNumText))
                                EnsureNumText(floor);
                            if (floor.editorNumText != null)
                            {
                                if (floor.editorNumText.letterText != null)
                                    floor.editorNumText.letterText.text = floor.seqID.ToString();
                                floor.editorNumText.gameObject.SetActive(true);
                            }
                        }
                        else if (floor.editorNumText != null)
                        {
                            floor.editorNumText.gameObject.SetActive(false);
                        }
                    }
                    return false;
                }
                catch (Exception e)
                {
                    Main.Logger?.Log($"[LazyFloorNumber] DrawFloorNums failed: {e.Message}");
                    return true;
                }
            }
        }

        [HarmonyPatch(typeof(scnEditor), nameof(scnEditor.Play))]
        public static class PlayPatch
        {
            [HarmonyPrefix]
            public static void Prefix(scnEditor __instance)
            {
                if (!Enabled) return;
                try
                {
                    _numDummy = GetOrCreateDummy();
                    var floors = __instance.floors;
                    if (floors == null) return;
                    for (int i = 0; i < floors.Count; i++)
                    {
                        var floor = floors[i];
                        if (floor != null && floor.editorNumText == null)
                            floor.editorNumText = _numDummy;
                    }
                }
                catch (Exception e)
                {
                    Main.Logger?.Log($"[LazyFloorNumber] Play prefix failed: {e.Message}");
                }
            }

            [HarmonyPostfix]
            public static void Postfix(scnEditor __instance)
            {
                if (!Enabled || _numDummy == null) return;
                try
                {
                    var floors = __instance.floors;
                    if (floors == null) return;
                    for (int i = 0; i < floors.Count; i++)
                    {
                        var floor = floors[i];
                        if (floor != null && ReferenceEquals(floor.editorNumText, _numDummy))
                            floor.editorNumText = null!;
                    }
                }
                catch (Exception e)
                {
                    Main.Logger?.Log($"[LazyFloorNumber] Play postfix failed: {e.Message}");
                }
            }

            private static scrLetterPress GetOrCreateDummy()
            {
                if (_numDummy != null) return _numDummy;
                var go = new GameObject("Iridium_NumDummy");
                go.hideFlags = HideFlags.HideAndDontSave;
                go.SetActive(false);
                var press = go.AddComponent<scrLetterPress>();
                var text = go.AddComponent<UnityEngine.UI.Text>();
                press.letterText = text;
                _numDummy = press;
                return press;
            }
        }

        #endregion
    }
}
