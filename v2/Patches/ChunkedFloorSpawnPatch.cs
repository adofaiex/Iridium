using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace Iridium.Patches
{
    /// <summary>
    /// 大谱面加载优化 B：分帧激活砖块（实验性，v2 / 2.9.8）。
    ///
    /// 目标：把「Instantiate 出来的砖块立即 Awake/OnEnable/Start」的成本从单帧挪到多帧，
    /// 消除打开几十万砖谱面时的单帧长冻结与内存尖峰。做法：
    ///  1. 批量实例化时临时把 meshFloor prefab 置为非激活（同时接管 A2 的剥离模板），
    ///     克隆出来的砖块保持非激活（Unity 不会立刻调用 Awake/OnEnable/Start）；
    ///  2. 立刻做一次 FastInit（复刻 scrFloor.Awake 的必要初始化 + FloorRenderer.Awake），
    ///     保证随后 MakeLevel / ApplyEventsToFloors / DrawXxx 能正常读写砖块；
    ///  3. 砖块进入待激活队列，由主线程每帧按时间预算分批 SetActive(true)；
    ///  4. 进播放（scnEditor.Play / FinishCustomLevelLoading）时强制一次性全部激活。
    ///
    /// 延迟 Awake 的补偿（已逐个核对 2.9.8 全库）：
    ///  - ffxCheckpoint / ffxSetHitsound 的 Decode 读取基类 floor（Awake 才设置）→ 缺失时补上。
    /// </summary>
    public static class ChunkedFloorSpawnPatch
    {
        private const int FloorThreshold = 20000;
        private const float FrameBudgetMs = 25f;

        private static readonly ConditionalWeakTable<scrFloor, object> _initialized = new();
        private static readonly Queue<scrFloor> _pending = new();
        private static readonly List<ffxPlusBase> _effectBuffer = new();
        private static readonly List<bool[]?> _condBuffer = new();
        private static readonly Stopwatch _watch = new();

        private static AccessTools.FieldRef<scrLevelMaker, GameObject>? _meshFloorRef;
        private static bool _meshFloorRefFailed;

        private static bool _bulkActive;
        private static bool _savedPrefabActive;
        private static GameObject? _savedPrefab;
        private static GameObject? _savedFieldPrefab;
        private static bool _bulkQueued;
        private static int _bulkTotal;
        private static int _bulkActivated;
        private static readonly Stopwatch _progressWatch = new();
        private static readonly Stopwatch _fastInitWatch = new();

        internal static bool Enabled =>
            Main.Settings.optimizer.optimizeLargeLevelLoading
            && Main.Settings.optimizer.chunkedFloorSpawn;

        internal static bool IsInitialized(scrFloor floor)
            => floor != null && _initialized.TryGetValue(floor, out _);

        internal static void MarkInitialized(scrFloor floor)
        {
            if (floor == null) return;
            _initialized.Remove(floor);
            _initialized.Add(floor, _initialized);
        }

        /// <summary>原版 scrFloor.Reset 会直接再调 Awake()，必须先清标记让初始化重跑。</summary>
        internal static void UnmarkInitialized(scrFloor floor)
        {
            if (floor == null) return;
            _initialized.Remove(floor);
        }

        #region InstantiateFloatFloors hook

        [HarmonyPatch(typeof(scrLevelMaker), nameof(scrLevelMaker.InstantiateFloatFloors))]
        public static class InstantiateFloorsChunkedPatch
        {
            [HarmonyPrefix]
            public static bool Prefix(scrLevelMaker __instance)
            {
                // 任何一次新加载都先把上一批未激活砖块补激活，避免它们被复用/销毁后
                // 留在未激活状态（FlushAll 后，下列所有非激活砖块一定是本次新建的克隆）。
                if (_pending.Count > 0) FlushAll();

                if (!Enabled) return true;
                if (!Application.isPlaying) return true;
                if (!ADOBase.isLevelEditor && !ADOBase.isScnGame) return true;
                if (__instance.isOldLevel) return true;
                if (EditorFloorOptimizationPatches.IncrementalActive) return true;
                if (__instance.floorAngles == null || __instance.floorAngles.Length < FloorThreshold) return true;

                // 精灵砖块路径使用别的 prefab，不走本优化
                var existing = __instance.listFloors;
                if (existing != null && existing.Count > 0 && existing[0] != null
                    && existing[0].GetComponent<FloorSpriteRenderer>() != null)
                    return true;

                if (_meshFloorRef == null && !_meshFloorRefFailed)
                {
                    try { _meshFloorRef = AccessTools.FieldRefAccess<scrLevelMaker, GameObject>("meshFloor"); }
                    catch (Exception e)
                    {
                        _meshFloorRefFailed = true;
                        Main.Logger?.Log($"[ChunkedSpawn] meshFloor ref failed: {e.Message}");
                    }
                }
                if (_meshFloorRef == null) return true;

                try
                {
                    var originalPrefab = _meshFloorRef(__instance);
                    if (originalPrefab == null) return true;

                    // B 统一接管模板：优先用 A2 的「剥掉 Num」模板，失败则退回原 prefab。
                    // 反激活模板 → 克隆出来的砖块保持未激活，Awake/OnEnable/Start 推迟。
                    var template = LazyFloorNumberPatch.EnsureStrippedTemplate(__instance) ?? originalPrefab;

                    _savedPrefab = template;
                    _savedFieldPrefab = originalPrefab;
                    _savedPrefabActive = template.activeSelf;
                    _bulkActive = true;
                    if (!ReferenceEquals(originalPrefab, template))
                        _meshFloorRef(__instance) = template;
                    template.SetActive(false);
                    return true;
                }
                catch (Exception e)
                {
                    Main.Logger?.Log($"[ChunkedSpawn] prefix failed: {e}");
                    RestorePrefab(__instance);
                    return true;
                }
            }

            [HarmonyPostfix]
            public static void Postfix(scrLevelMaker __instance)
            {
                if (!_bulkActive) return;
                try
                {
                    var floors = __instance.listFloors;
                    if (floors != null)
                    {
                        int enqueuedCount = 0;
                        _fastInitWatch.Restart();
                        // 前缀已 FlushAll：这里所有「非激活」砖块一定是本次新建的克隆。
                        for (int i = 0; i < floors.Count; i++)
                        {
                            var floor = floors[i];
                            if (floor == null || floor.gameObject.activeSelf) continue;
                            if (LazyTopGlowPatch.TryInitialize(floor, callComponentAwake: true))
                            {
                                MarkInitialized(floor);
                                _pending.Enqueue(floor);
                                enqueuedCount++;
                            }
                            else
                            {
                                // FastInit 失败：立即激活走原版生命周期，别把砖块卡在未激活状态
                                ActivateFloor(floor, restoreConditionalInfo: false);
                            }
                        }
                        if (enqueuedCount > 0)
                        {
                            _bulkQueued = true;
                            _bulkTotal = enqueuedCount;
                            _bulkActivated = 0;
                            _progressWatch.Restart();
                            Main.Logger?.Log($"[ChunkedSpawn] fastinit: {_fastInitWatch.ElapsedMilliseconds} ms (floors={enqueuedCount})");
                        }
                    }
                }
                catch (Exception e)
                {
                    Main.Logger?.Log($"[ChunkedSpawn] postfix failed: {e}");
                    FlushAll();
                }
                finally
                {
                    RestorePrefab(__instance);
                }
            }

            [HarmonyFinalizer]
            public static Exception? Finalizer(scrLevelMaker __instance, Exception? __exception)
            {
                RestorePrefab(__instance);
                return __exception;
            }

            private static void RestorePrefab(scrLevelMaker __instance)
            {
                if (!_bulkActive) return;
                _bulkActive = false;
                try
                {
                    // 先还原字段，再恢复被我们反激活的 prefab 引用本身。
                    if (_savedFieldPrefab != null && _meshFloorRef != null)
                        _meshFloorRef(__instance) = _savedFieldPrefab;
                    if (_savedPrefab != null)
                        _savedPrefab.SetActive(_savedPrefabActive);
                }
                catch (Exception e)
                {
                    Main.Logger?.Log($"[ChunkedSpawn] restore prefab failed: {e.Message}");
                }
                finally
                {
                    _savedPrefab = null;
                    _savedFieldPrefab = null;
                }
            }
        }

        #endregion

        #region Activation driver

        /// <summary>由 Main.OnUpdate 每帧调用，按时间预算分批激活。</summary>
        public static void Tick()
        {
            if (_pending.Count == 0)
            {
                FinishBulkIfDone();
                return;
            }
            if (!Enabled)
            {
                FlushAll();
                FinishBulkIfDone();
                return;
            }

            _watch.Restart();
            while (_pending.Count > 0)
            {
                var floor = _pending.Dequeue();
                if (floor == null) continue;
                _bulkActivated++;
                ActivateFloor(floor, restoreConditionalInfo: true);
                if (_watch.Elapsed.TotalMilliseconds >= FrameBudgetMs) break;
            }
            _watch.Stop();
            if (_progressWatch.ElapsedMilliseconds >= 2000 && _pending.Count > 0)
            {
                _progressWatch.Restart();
                int pct = _bulkTotal > 0 ? (int)(_bulkActivated * 100L / _bulkTotal) : 0;
                Main.Logger?.Log(
                    $"[ChunkedSpawn] activating {_bulkActivated}/{_bulkTotal} ({pct}%) | {LoadProfilerPatch.MemorySnapshot()}");
            }
            FinishBulkIfDone();
        }

        /// <summary>整批激活完成后收尾：打印总量与内存（不做强制 GC，避免大堆上长时间停顿）。</summary>
        private static void FinishBulkIfDone()
        {
            if (!_bulkQueued || _pending.Count > 0) return;
            _bulkQueued = false;
            try
            {
                Main.Logger?.Log(
                    $"[ChunkedSpawn] activation done ({_bulkActivated}/{_bulkTotal}) | {LoadProfilerPatch.MemorySnapshot()}");
            }
            catch (Exception e)
            {
                Main.Logger?.Log($"[ChunkedSpawn] activation done log failed: {e.Message}");
            }
        }

        /// <summary>立即激活全部剩余砖块（进入播放前调用，保证玩法不缺砖）。</summary>
        public static void FlushAll()
        {
            while (_pending.Count > 0)
            {
                var floor = _pending.Dequeue();
                if (floor == null) continue;
                ActivateFloor(floor, restoreConditionalInfo: true);
            }
        }

        private static void ActivateFloor(scrFloor floor, bool restoreConditionalInfo)
        {
            try
            {
                if (floor == null) return;
                if (floor.gameObject.activeSelf)
                {
                    MarkInitialized(floor);
                    return;
                }

                if (restoreConditionalInfo)
                {
                    _effectBuffer.Clear();
                    _condBuffer.Clear();
                    var effects = floor.plusEffects;
                    if (effects != null)
                    {
                        for (int i = 0; i < effects.Count; i++)
                        {
                            var effect = effects[i];
                            if (effect == null) continue;
                            _effectBuffer.Add(effect);
                            _condBuffer.Add(effect.conditionalInfo);
                        }
                    }

                    floor.gameObject.SetActive(true);

                    // ffxPlusBase.Awake 会把 conditionalInfo 归一化/重置；
                    // 管线写入的值（快照非空）按原版语义应当保留。
                    for (int i = 0; i < _effectBuffer.Count && i < _condBuffer.Count; i++)
                    {
                        var snapshot = _condBuffer[i];
                        if (snapshot != null) _effectBuffer[i].conditionalInfo = snapshot;
                    }
                }
                else
                {
                    floor.gameObject.SetActive(true);
                }
                MarkInitialized(floor);
            }
            catch (Exception e)
            {
                Main.Logger?.Log($"[ChunkedSpawn] activate failed: {e.Message}");
            }
        }

        #endregion

        #region Deferred-Awake compensations

        /// <summary>ffxCheckpoint.Decode 读取基类 floor（Awake 才设置）。</summary>
        [HarmonyPatch(typeof(ffxCheckpoint), nameof(ffxCheckpoint.Decode))]
        public static class CheckpointDecodePatch
        {
            [HarmonyPrefix]
            public static void Prefix(ffxCheckpoint __instance)
            {
                if (!Enabled || __instance == null || __instance.floor != null) return;
                __instance.floor = __instance.GetComponent<scrFloor>();
            }
        }

        /// <summary>ffxSetHitsound.Decode 写入 floor.setHitsound。</summary>
        [HarmonyPatch(typeof(ffxSetHitsound), nameof(ffxSetHitsound.Decode))]
        public static class SetHitsoundDecodePatch
        {
            [HarmonyPrefix]
            public static void Prefix(ffxSetHitsound __instance)
            {
                if (!Enabled || __instance == null || __instance.floor != null) return;
                __instance.floor = __instance.GetComponent<scrFloor>();
            }
        }

        #endregion

        #region Flush before gameplay

        [HarmonyPatch(typeof(scnEditor), nameof(scnEditor.Play))]
        public static class EditorPlayFlushPatch
        {
            [HarmonyPrefix]
            public static void Prefix() => FlushAll();
        }

        [HarmonyPatch(typeof(scnGame), nameof(scnGame.FinishCustomLevelLoading))]
        public static class FinishLoadingFlushPatch
        {
            [HarmonyPrefix]
            public static void Prefix() => FlushAll();
        }

        #endregion
    }
}
