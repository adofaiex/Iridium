using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using ADOFAI.Editor.Actions;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;
using ADOFAI;

namespace Iridium.Patches
{
    /// <summary>
    /// 专门针对 scnGame 和 scnEditor 的性能优化 Patch
    /// </summary>
    public static class SceneOptimizationPatches
    {
        #region Shared Caches

        // GameObject.Find 缓存
        private static GameObject? _cachedFlash;
        private static scrDecorationManager? _cachedDecorationManager;
        private static scrCamera? _cachedCameraComponent;

        // 组件缓存
        private static readonly ConditionalWeakTable<scrFloor, FloorMesh> _floorMeshCache = new();

        // 对象池 - 重用 List 避免 GC
        private static readonly List<Collider2D> _reusableColliderList = new(100);
        private static readonly List<GameObject> _reusableGameObjectList = new(100);
        private static readonly List<ffxPlusBase> _reusableEffectList = new(50);

        // 事件分类缓存
        private static readonly Dictionary<LevelEventType, List<LevelEvent>> _eventsByType = new();

        #endregion

        #region scnGame Optimizations

        /// <summary>
        /// 缓存 scnGame 中频繁使用的 GameObject.Find 结果
        /// </summary>
        [HarmonyPatch(typeof(scnGame), "Awake")]
        public static class ScnGameCacheReferencesPatch
        {
            [HarmonyPostfix]
            public static void Postfix()
            {
                if (!Main.Settings.optimizer.cacheGameObjectReferences) return;

                try
                {
                    _cachedFlash = GameObject.Find("Flash");

                    var decorContainer = GameObject.Find("Decoration Container");
                    if (decorContainer != null)
                        _cachedDecorationManager = decorContainer.GetComponent<scrDecorationManager>();

                    var cameraObj = GameObject.Find("Camera");
                    if (cameraObj != null)
                        _cachedCameraComponent = cameraObj.GetComponent<scrCamera>();

                    Main.Logger?.Log("[SceneOptimization] Cached GameObject references");
                }
                catch (Exception e)
                {
                    Main.Logger?.Error($"[SceneOptimization] Failed to cache references: {e}");
                }
            }
        }

        /// <summary>
        /// 优化 scnGame.Update - 只在相机参数变化时更新
        /// </summary>
        [HarmonyPatch(typeof(scnGame), "Update")]
        public static class ScnGameUpdateOptimizationPatch
        {
            private static float _lastOrthoSize;
            private static float _lastAspect;
            private static Vector2 _cachedScreenSize;
            private static AccessTools.FieldRef<scnGame, int>? _startFrameAccessor;
            private static AccessTools.FieldRef<scnGame, Camera>? _cameraAccessor;
            private static AccessTools.FieldRef<scnGame, GameObject>? _flashAccessor;
            private static bool _initialized;

            private static void Initialize()
            {
                try
                {
                    _startFrameAccessor = AccessTools.FieldRefAccess<scnGame, int>("startFrame");
                    _cameraAccessor = AccessTools.FieldRefAccess<scnGame, Camera>("camera");
                    _flashAccessor = AccessTools.FieldRefAccess<scnGame, GameObject>("flash");
                }
                catch (Exception e)
                {
                    Main.Logger?.Error($"[SceneOptimization] Failed to create accessors: {e}");
                }
                _initialized = true;
            }

            [HarmonyPrefix]
            public static bool Prefix(scnGame __instance)
            {
                if (!Main.Settings.optimizer.optimizeScnGameUpdate) return true;

                if (!_initialized) Initialize();
                if (_startFrameAccessor == null || _cameraAccessor == null || _flashAccessor == null)
                    return true;

                int startFrame = _startFrameAccessor(__instance);

                // 特殊情况：第3帧必须执行
                if ((GCS.customLevelPaths != null || ADOBase.isInternalLevel) &&
                    !ADOBase.isLevelEditor &&
                    Time.frameCount - startFrame == 3)
                    return true;

                Camera cam = _cameraAccessor(__instance);
                if (cam == null) return true;

                float orthoSize = cam.orthographicSize;
                float aspect = cam.aspect;

                // 相机参数没变化，跳过更新
                if (Mathf.Approximately(orthoSize, _lastOrthoSize) &&
                    Mathf.Approximately(aspect, _lastAspect))
                {
                    return false;
                }

                // 更新缓存
                _lastOrthoSize = orthoSize;
                _lastAspect = aspect;

                float height = 2f * orthoSize;
                float width = height * aspect;
                _cachedScreenSize = new Vector2(width, height);

                // 手动执行更新逻辑，避免重复计算
                var camInstance = scrCamera.instance;
                if (camInstance != null)
                {
                    camInstance.flashPlusRendererBg.transform.ScaleXY(_cachedScreenSize.x, _cachedScreenSize.y);
                    camInstance.flashPlusRendererFg.transform.ScaleXY(_cachedScreenSize.x, _cachedScreenSize.y);
                }

                GameObject flash = _flashAccessor(__instance);
                if (flash != null)
                {
                    flash.transform.ScaleXY(_cachedScreenSize.x, _cachedScreenSize.y);
                }

                return false;
            }
        }

        /// <summary>
        /// 优化 ApplyEventsToFloors - 预先分类事件避免多次 FindAll
        /// </summary>
        [HarmonyPatch(typeof(scnGame), "ApplyEventsToFloors",
            typeof(List<scrFloor>), typeof(LevelData), typeof(scrLevelMaker), typeof(List<LevelEvent>))]
        public static class ApplyEventsOptimizationPatch
        {
            [HarmonyPrefix]
            public static void Prefix(List<LevelEvent> events)
            {
                if (!Main.Settings.optimizer.optimizeEventProcessing) return;

                try
                {
                    // 清空并重新分类事件
                    _eventsByType.Clear();

                    foreach (var evt in events)
                    {
                        if (!_eventsByType.TryGetValue(evt.eventType, out var list))
                        {
                            list = new List<LevelEvent>();
                            _eventsByType[evt.eventType] = list;
                        }
                        list.Add(evt);
                    }
                }
                catch (Exception e)
                {
                    Main.Logger?.Error($"[SceneOptimization] Event classification failed: {e}");
                }
            }

            // 提供静态方法供其他代码使用分类后的事件
            public static List<LevelEvent>? GetEventsByType(LevelEventType type)
            {
                return _eventsByType.TryGetValue(type, out var list) ? list : null;
            }
        }

        /// <summary>
        /// 清理缓存 - 在场景销毁时
        /// </summary>
        [HarmonyPatch(typeof(scnGame), "OnDestroy")]
        public static class ScnGameCleanupPatch
        {
            [HarmonyPostfix]
            public static void Postfix()
            {
                // 只在启用优化时才清理
                if (!Main.Settings.optimizer.enableOptimizer) return;

                _cachedFlash = null;
                _cachedDecorationManager = null;
                _cachedCameraComponent = null;
                _eventsByType.Clear();

                Main.Logger?.Log("[SceneOptimization] Cleaned up scnGame caches");
            }
        }

        #endregion

        #region scnEditor Optimizations

        /// <summary>
        /// 优化 scnEditor.ObjectsAtMouse - 重用 List 避免 GC
        /// </summary>
        [HarmonyPatch(typeof(scnEditor), "ObjectsAtMouse")]
        public static class ObjectsAtMouseOptimizationPatch
        {
            private static int _lastFrameUpdated = -1;
            private static GameObject[] _cachedResult = Array.Empty<GameObject>();

            [HarmonyPrefix]
            public static bool Prefix(scnEditor __instance, ref GameObject[] __result)
            {
                if (!Main.Settings.optimizer.optimizeEditorMouseDetection) return true;

                try
                {
                    // 同一帧内重复调用直接返回缓存
                    if (Time.frameCount == _lastFrameUpdated)
                    {
                        __result = _cachedResult;
                        return false;
                    }

                    _lastFrameUpdated = Time.frameCount;

                    // 重用 List
                    _reusableColliderList.Clear();
                    _reusableGameObjectList.Clear();

                    // 获取鼠标位置
                    Vector2 mousePos = __instance.camera.ScreenToWorldPoint(Input.mousePosition);
                    float magnitude = __instance.camera.orthographicSize * 2f;

                    // 遍历 floors
                    var floors = __instance.floors;
                    if (floors == null)
                    {
                        __result = Array.Empty<GameObject>();
                        _cachedResult = __result;
                        return false;
                    }

                    foreach (var floor in floors)
                    {
                        if (floor == null) continue;

                        Vector2 floorPos = floor.transform.position;
                        if (Vector2.Distance(floorPos, mousePos) > magnitude) continue;

                        // 使用缓存的 FloorMesh
                        if (!_floorMeshCache.TryGetValue(floor, out var floorMesh))
                        {
                            floorMesh = floor.GetComponent<FloorMesh>();
                            if (floorMesh != null)
                                _floorMeshCache.Add(floor, floorMesh);
                        }

                        if (floorMesh != null)
                        {
                            floorMesh.GenerateCollider();
                            if (floorMesh.polygonCollider != null)
                            {
                                floorMesh.polygonCollider.enabled = true;
                                _reusableColliderList.Add(floorMesh.polygonCollider);
                            }
                        }
                    }

                    // 执行物理检测
                    var hits = Physics2D.OverlapPointAll(mousePos);
                    var resultList = new List<GameObject>();

                    foreach (var hit in hits)
                    {
                        if (hit != null && hit.gameObject != null)
                            resultList.Add(hit.gameObject);
                    }

                    // Match original: return null when nothing hit so caller can
                    // DeselectFloors() + DeselectAllDecorations() on empty clicks
                    __result = resultList.Count > 0 ? resultList.ToArray() : null!;
                    _cachedResult = __result;

                    return false;
                }
                catch (Exception e)
                {
                    Main.Logger?.Error($"[SceneOptimization] ObjectsAtMouse failed: {e}");
                    return true;
                }
            }
        }

        /// <summary>
        /// 优化 scnEditor.DestroyEventIndicators - 维护列表而不是每次 Find
        /// </summary>
        [HarmonyPatch(typeof(scnEditor), "DestroyEventIndicators")]
        public static class DestroyEventIndicatorsPatch
        {
            [HarmonyPrefix]
            public static bool Prefix(scnEditor __instance)
            {
                if (!Main.Settings.optimizer.optimizeEditorEventIndicators) return true;

                try
                {
                    // 禁用 EventCircle
                    if (__instance.EventCircle != null)
                        __instance.EventCircle.gameObject.SetActive(false);

                    // 使用 FindGameObjectsWithTag 但只调用一次
                    var indicators = GameObject.FindGameObjectsWithTag("EventIndicator");
                    foreach (var indicator in indicators)
                    {
                        if (indicator != null)
                            UnityEngine.Object.Destroy(indicator);
                    }

                    return false;
                }
                catch (Exception e)
                {
                    Main.Logger?.Error($"[SceneOptimization] DestroyEventIndicators failed: {e}");
                    return true;
                }
            }
        }

        /// <summary>
        /// 优化 scnEditor.Update 中的 ScrollRect 查找
        /// </summary>
        [HarmonyPatch(typeof(scnEditor), "Update")]
        public static class EditorUpdateScrollRectCachePatch
        {
            private static readonly ConditionalWeakTable<GameObject, ScrollRect> _scrollRectCache = new();

            // 提供缓存查找方法
            public static ScrollRect? FindScrollRect(GameObject obj)
            {
                if (obj == null) return null;

                if (_scrollRectCache.TryGetValue(obj, out var cached))
                    return cached;

                Transform current = obj.transform;
                while (current != null)
                {
                    if (current.TryGetComponent<ScrollRect>(out var scrollRect))
                    {
                        _scrollRectCache.Add(obj, scrollRect);
                        return scrollRect;
                    }
                    current = current.parent;
                }

                return null;
            }
        }

        /// <summary>
        /// ToggleFloorNumsEditorAction 不再调用 RemakePath()（重建整个路径很慢），
        /// 改为只调 DrawFloorNums() 刷新编号标签的可见性。
        /// </summary>
        [HarmonyPatch(typeof(ToggleFloorNumsEditorAction), "Execute")]
        public static class ToggleFloorNumsActionPatch
        {
            private static Action<scnEditor>? _drawFloorNums;

            [HarmonyPrefix]
            public static bool Prefix(ToggleFloorNumsEditorAction __instance, scnEditor editor)
            {
                if (!Main.Settings.optimizer.enableOptimizer) return true;

                int num = editor.SelectionIsSingle() ? editor.selectedFloors[0].seqID : -1;
                editor.showFloorNums = !editor.showFloorNums;

                // Skip RemakePath() — just refresh floor number labels
                _drawFloorNums ??= FastDelegate.Open<Action<scnEditor>>(
                    AccessTools.Method(typeof(scnEditor), "DrawFloorNums"), null);
                _drawFloorNums?.Invoke(editor);

                if (num != -1)
                    editor.SelectFloor(editor.floors[num], false);

                return false;
            }
        }

        #endregion

        #region Utility Methods

        /// <summary>
        /// 获取缓存的 FloorMesh 组件
        /// </summary>
        public static FloorMesh? GetCachedFloorMesh(scrFloor floor)
        {
            if (floor == null) return null;

            if (!_floorMeshCache.TryGetValue(floor, out var mesh))
            {
                mesh = floor.GetComponent<FloorMesh>();
                if (mesh != null)
                    _floorMeshCache.Add(floor, mesh);
            }

            return mesh;
        }

        /// <summary>
        /// 清空所有缓存（用于测试或重置）
        /// </summary>
        public static void ClearAllCaches()
        {
            _cachedFlash = null;
            _cachedDecorationManager = null;
            _cachedCameraComponent = null;
            _eventsByType.Clear();
            _reusableColliderList.Clear();
            _reusableGameObjectList.Clear();
            _reusableEffectList.Clear();
            // ConditionalWeakTable 不需要手动清理

            Main.Logger?.Log("[SceneOptimization] All caches cleared");
        }

        #endregion
    }
}
