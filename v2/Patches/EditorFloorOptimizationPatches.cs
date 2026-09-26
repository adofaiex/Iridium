using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;
using ADOFAI;

namespace Iridium.Patches
{
    public static class EditorFloorOptimizationPatches
    {
        #region Static State for Incremental Mode

        private static bool _incrementalMode;
        private static bool _incrementalIsInsert;
        private static int _incrementalSeqID;
        private static int _rebuildTick;

        internal static bool IncrementalActive => _incrementalMode;

        #endregion

        #region Reflection Targets

        // FieldRefAccess delegates — direct memory access, zero allocation per call
        private static AccessTools.FieldRef<scnEditor, bool>? _refreshDecSpritesRef;
        private static AccessTools.FieldRef<scrLevelMaker, GameObject>? _meshFloorRef;
        private static AccessTools.FieldRef<scrLevelMaker, GameObject>? _spriteFloorRef;
        private static AccessTools.FieldRef<scrLevelMaker, List<GameObject>>? _holdGOsRef;

        // Cached open delegate — created once, reused every call
        private static Action<scnEditor>? _drawFloorNumsAction;

        private static void DrawFloorNums(scnEditor editor)
        {
            (_drawFloorNumsAction ??= AccessTools.MethodDelegate<Action<scnEditor>>(
                AccessTools.Method(typeof(scnEditor), "DrawFloorNums"), null))?.Invoke(editor);
        }

        #endregion

        #region Helpers

        private static bool AnyFloorsHaveHolds(List<scrFloor> floors)
        {
            for (int i = 0; i < floors.Count; i++)
                if (floors[i].holdLength >= 0) return true;
            return false;
        }

        private static bool AnyEventsHavePositionTrack(List<LevelEvent> events)
        {
            for (int i = 0; i < events.Count; i++)
                if (events[i].eventType == LevelEventType.PositionTrack) return true;
            return false;
        }

        #endregion

        #region Patch: InsertCharFloor - Incremental

        [HarmonyPatch(typeof(scnEditor), "InsertCharFloor")]
        public static class InsertCharFloorOptimizationPatch
        {
            [HarmonyPrefix]
            public static bool Prefix(scnEditor __instance, int sequenceID, char floorType)
            {
                if (!Main.Settings.optimizer.enableEditorFloorOptimization) return true;
                if (!Main.Settings.optimizer.incrementalFloorInsert) return true;

                var lm = scrLevelMaker.instance;
                var floors = lm.listFloors;
                if (floors == null || floors.Count < 2) return true;

                try
                {
                    // Convert char to angle — midspin fallback to original
                    float floorAngle = scrLevelMaker.GetAngleFromFloorCharDirection(floorType);
                    if (floorAngle == 999f) return true;

                    // Inject data before RemakePath runs
                    __instance.levelData.pathData = __instance.levelData.pathData.Insert(sequenceID, floorType.ToString());
                    __instance.levelData.angleData.Insert(sequenceID, floorAngle);

                    // Let RemakePath() run the full chain (MakeLevel → InstantiateFloatFloors → post-process → draws).
                    // Our InstantiateFloatFloors patch intercepts to reuse existing floors.
                    _incrementalMode = true;
                    _incrementalIsInsert = true;
                    _incrementalSeqID = sequenceID;

                    __instance.RemakePath();

                    _incrementalMode = false;
                    return false; // data already inserted, skip original
                }
                catch (Exception e)
                {
                    Main.Logger?.Error($"[EditorFloorOptimization] InsertCharPrefix failed: {e}");
                    _incrementalMode = false;
                    return true; // fallback to original
                }
            }
        }

        #endregion

        #region Patch: InsertFloatFloor - Incremental

        [HarmonyPatch(typeof(scnEditor), "InsertFloatFloor")]
        public static class InsertFloatFloorOptimizationPatch
        {
            [HarmonyPrefix]
            public static bool Prefix(scnEditor __instance, int sequenceID, float floorAngle)
            {
                if (!Main.Settings.optimizer.enableEditorFloorOptimization) return true;
                if (!Main.Settings.optimizer.incrementalFloorInsert) return true;

                var lm = scrLevelMaker.instance;
                var floors = lm.listFloors;
                if (floors == null || floors.Count < 2) return true;

                try
                {
                    __instance.levelData.angleData.Insert(sequenceID, floorAngle);

                    _incrementalMode = true;
                    _incrementalIsInsert = true;
                    _incrementalSeqID = sequenceID;

                    __instance.RemakePath();

                    _incrementalMode = false;
                    return false;
                }
                catch (Exception e)
                {
                    Main.Logger?.Error($"[EditorFloorOptimization] InsertFloatPrefix failed: {e}");
                    _incrementalMode = false;
                    return true;
                }
            }
        }

        #endregion

        #region Patch: InstantiateFloatFloors - Reuse floors in editor

        [HarmonyPatch(typeof(scrLevelMaker), "InstantiateFloatFloors")]
        public static class InstantiateFloatFloorsOptimizationPatch
        {
            [HarmonyPrefix]
            public static bool Prefix(scrLevelMaker __instance)
            {
                if (!_incrementalMode) return true; // normal mode - let original run

                try
                {
                    var floors = __instance.listFloors;
                    int targetCount = __instance.floorAngles.Length + 1;

                    if (_incrementalIsInsert)
                    {
                        int seqID = _incrementalSeqID;

                        // Create ONE new floor
                        _meshFloorRef ??= AccessTools.FieldRefAccess<scrLevelMaker, GameObject>("meshFloor");
                        _spriteFloorRef ??= AccessTools.FieldRefAccess<scrLevelMaker, GameObject>("spriteFloor");
                        GameObject prefab = _meshFloorRef(__instance);
                        if (prefab == null) prefab = _spriteFloorRef(__instance);
                        if (prefab == null) return true; // fallback

                        GameObject container = GameObject.Find("Floors");
                        if (container == null) container = new GameObject("Floors");

                        // Calculate position for new floor
                        var prevFloor = floors[Math.Max(0, seqID)];
                        double radius = scrController.instance.tileSize;
                        float ang = __instance.floorAngles[Math.Min(seqID, __instance.floorAngles.Length - 1)];
                        double exitAngle = ang == 999f
                            ? prevFloor.entryangle
                            : (-ang + 90f) * (Math.PI / 180.0);
                        Vector3 offset = scrMisc.getVectorFromAngle(exitAngle, radius);
                        Vector3 insertPos = floors[seqID].transform.position + offset;

                        GameObject newObj = UnityEngine.Object.Instantiate(prefab, insertPos, Quaternion.identity);
                        newObj.transform.parent = container.transform;
                        var newFloor = newObj.GetComponent<scrFloor>();

                        // Insert into list at correct position
                        floors.Insert(seqID + 1, newFloor);

                        // Remove excess floors if we have too many
                        while (floors.Count > targetCount)
                        {
                            var extra = floors[floors.Count - 1];
                            if (extra != null) UnityEngine.Object.DestroyImmediate(extra.gameObject);
                            floors.RemoveAt(floors.Count - 1);
                        }

                        // Now rebuild geometry for all floors from insertion point
                        // Reuse existing floor 0 setup
                        RebuildPositionsAndAngles(__instance, seqID);
                    }
                    else
                    {
                        // Delete mode - floor was already removed from list by our prefix
                        // Remove excess floors
                        while (floors.Count > targetCount)
                        {
                            var extra = floors[floors.Count - 1];
                            if (extra != null) UnityEngine.Object.DestroyImmediate(extra.gameObject);
                            floors.RemoveAt(floors.Count - 1);
                        }

                        // Add missing floors if needed
                        while (floors.Count < targetCount)
                        {
                            _meshFloorRef ??= AccessTools.FieldRefAccess<scrLevelMaker, GameObject>("meshFloor");
                            _spriteFloorRef ??= AccessTools.FieldRefAccess<scrLevelMaker, GameObject>("spriteFloor");
                            GameObject prefab = _meshFloorRef(__instance);
                            if (prefab == null) prefab = _spriteFloorRef(__instance);
                            if (prefab == null) break;

                            GameObject container = GameObject.Find("Floors");
                            if (container == null) container = new GameObject("Floors");

                            var newObj = UnityEngine.Object.Instantiate(prefab, Vector3.zero, Quaternion.identity);
                            newObj.transform.parent = container.transform;
                            floors.Add(newObj.GetComponent<scrFloor>());
                        }

                        RebuildPositionsAndAngles(__instance, Math.Max(0, _incrementalSeqID - 1));
                    }

                    return false; // skip original InstantiateFloatFloors
                }
                catch (Exception e)
                {
                    Main.Logger?.Error($"[EditorFloorOptimization] InstFloatPrefix failed: {e}");
                    _incrementalMode = false;
                    return true; // fallback
                }
            }
        }

        #endregion

        #region Geometry Rebuild

        private static void RebuildPositionsAndAngles(scrLevelMaker lm, int fromSeqID)
        {
            var floors = lm.listFloors;
            var angles = lm.floorAngles;
            if (floors == null || floors.Count == 0 || angles == null) return;

            int start = Math.Max(0, fromSeqID);

            // 旧实现在每次增量重建时都会对 [0, start) 的每块砖做
            // GetComponents<ffxPlusBase>() + DestroyImmediate 清理。150k 砖时这是
            // 15 万次原生查询，是「新建一块就卡一下」的主要来源，且没有必要：
            //   - 未开启 skipApplyEventsOnInsert：随后的 scnGame.ApplyEventsToFloors
            //     会自己清空并重建所有砖块的 ffx；
            //   - 开启时：本次操作不会向该范围重新添加 ffx，不存在累积。
            // 重建范围内的砖块由下面的 ResetFloorState 负责清理。

            // Full rebuild from floor 0
            if (start == 0)
            {
                var floor0 = floors[0];
                ResetFloorState(floor0, Vector3.zero);
                floor0.entryangle = 4.71238899230957; // MathF.PI * 1.5
                floor0.seqID = 0;
                floor0.hasLit = true;
                floor0.prevfloor = null;
            }

            // 插入点之前的砖块本次不会被改动，锚点直接用砖块上已保存的位置/入射角，
            // 避免每次插入都从 0 号砖重算一遍（O(start) 三角函数）。
            // 为防止增量累加的浮点误差无限累积，每 64 次重建做一次完整重算。
            Vector3 cumulativePos;
            double prevEntryAngle;
            bool fullRecompute = start <= 0 || (++_rebuildTick & 63) == 0;
            if (start > 0 && !fullRecompute)
            {
                cumulativePos = floors[start].transform.position;
                prevEntryAngle = floors[start].entryangle;
            }
            else
            {
                cumulativePos = Vector3.zero;
                double entryAngle = 4.71238899230957; // MathF.PI * 1.5
                double tileRadius = scrController.instance.tileSize;
                for (int i = 0; i < start && i < angles.Length; i++)
                {
                    float ang = angles[i];
                    double exitAngle = ang == 999f ? entryAngle : (-ang + 90f) * (Math.PI / 180.0);
                    cumulativePos += scrMisc.getVectorFromAngle(exitAngle, tileRadius);
                    entryAngle = (exitAngle + Math.PI) % (2.0 * Math.PI);
                }
                prevEntryAngle = entryAngle;
            }

            // Reset the anchor floor if we're not doing a full rebuild (floor 0 was
            // already reset above). All other floors are reset once as nextFloor below.
            if (start > 0)
            {
                if (float.IsNaN(cumulativePos.x) || float.IsNaN(cumulativePos.y) || double.IsNaN(prevEntryAngle))
                {
                    cumulativePos = floors[start].transform.position;
                    prevEntryAngle = floors[start].entryangle;
                }
                ResetFloorState(floors[start], cumulativePos);
            }

            for (int i = start; i < floors.Count - 1 && i < angles.Length; i++)
            {
                var floor = floors[i];
                var nextFloor = floors[i + 1];

                double radius = scrController.instance.tileSize;
                float ang = angles[i];

                floor.entryangle = prevEntryAngle;

                double exitAngle;
                if (ang == 999f)
                {
                    exitAngle = prevEntryAngle;
                    floor.midSpin = true;
                }
                else
                {
                    exitAngle = (-ang + 90f) * (Math.PI / 180.0);
                    floor.midSpin = false;
                }

                floor.exitangle = exitAngle;
                floor.seqID = i;
                floor.prevfloor = i > 0 ? floors[i - 1] : null;
                floor.nextfloor = nextFloor;
                floor.speed = 1f;
                floor.isCCW = false;

                Vector3 offset = scrMisc.getVectorFromAngle(exitAngle, radius);
                cumulativePos += offset;

                ResetFloorState(nextFloor, cumulativePos);
                nextFloor.entryangle = (exitAngle + Math.PI) % (2.0 * Math.PI);
                nextFloor.seqID = i + 1;
                nextFloor.transform.position = cumulativePos;
                nextFloor.floatDirection = ang;
                nextFloor.prevfloor = floor;

                prevEntryAngle = nextFloor.entryangle;
            }

            if (floors.Count > 1)
            {
                var lastFloor = floors[floors.Count - 1];
                lastFloor.exitangle = lastFloor.entryangle + Math.PI;
                lastFloor.nextfloor = null;

                if (scrController.instance?.gameworld == true)
                {
                    lastFloor.isportal = true;
                    lastFloor.levelnumber = Portal.EndOfLevel;
                }
            }
        }

        /// <summary>
        /// Replicate scrLevelMaker.ResetFloor logic for incremental reuse:
        /// destroy stray ffxPlusBase components, reset transform, call floor.Reset().
        /// </summary>
        private static void ResetFloorState(scrFloor floor, Vector3 position)
        {
            if (floor == null) return;

            var ffxComponents = floor.GetComponents<ffxPlusBase>();
            for (int i = 0; i < ffxComponents.Length; i++)
                UnityEngine.Object.DestroyImmediate(ffxComponents[i]);

            floor.transform.position = position;
            floor.transform.rotation = Quaternion.identity;
            floor.transform.localScale = Vector3.one;
            floor.Reset();
        }

        #endregion

        #region Patch: DeleteFloor - Incremental (via Transpiler replacing RemakePath)

        [HarmonyPatch(typeof(scnEditor), "DeleteFloor",
            new Type[] { typeof(int), typeof(bool) })]
        public static class DeleteFloorOptimizationPatch
        {
            /// <summary>记录本次删除的砖块序号，供 LightweightDeleteRemakePath / DrawFloorNums 使用。</summary>
            [HarmonyPrefix]
            public static void Prefix(int sequenceIndex)
            {
                _incrementalSeqID = Math.Max(0, sequenceIndex);
            }

            [HarmonyTranspiler]
            public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                // Always replace RemakePath() call with our version.
                // Settings check happens at runtime in LightweightDeleteRemakePath.
                var list = instructions.ToList();

                // Find pattern: call instance void scnEditor::RemakePath(bool, bool)
                // and replace with our method that has the same signature (editor, bool, bool)
                for (int i = 0; i < list.Count; i++)
                {
                    if (list[i].opcode == OpCodes.Call &&
                        list[i].operand is MethodInfo method &&
                        method.Name == "RemakePath" &&
                        method.DeclaringType == typeof(scnEditor))
                    {
                        list[i] = new CodeInstruction(OpCodes.Call,
                            AccessTools.Method(typeof(DeleteFloorOptimizationPatch),
                                nameof(LightweightDeleteRemakePath)));
                    }
                }
                return list;
            }

            // Lightweight replacement for RemakePath(bool, bool) in DeleteFloor
            // Signature matches RemakePath(bool applyEventsToFloors, bool remakeLevel)
            // so the stack is balanced (editor, bool, bool -> void)
            public static void LightweightDeleteRemakePath(scnEditor editor, bool applyEventsToFloors, bool remakeLevel)
            {
                if (!Main.Settings.optimizer.enableEditorFloorOptimization ||
                    !Main.Settings.optimizer.incrementalFloorInsert)
                {
                    editor.RemakePath();
                    return;
                }

                try
                {
                    var lm = scrLevelMaker.instance;
                    var floors = lm.listFloors;
                    if (floors == null || floors.Count < 2)
                    {
                        editor.RemakePath();
                        return;
                    }

                    // Data already removed by DeleteFloor. Let RemakePath run the full
                    // chain (scnGame.RemakePath → MakeLevel → post-process → draws).
                    // InstantiateFloatFloors is intercepted to reuse existing floors.
                    // 删除序号由 DeleteFloorOptimizationPatch.Prefix 记录，这里不要覆盖。
                    _incrementalMode = true;
                    _incrementalIsInsert = false;

                    editor.RemakePath(applyEventsToFloors, remakeLevel);

                    _incrementalMode = false;
                }
                catch (Exception e)
                {
                    Main.Logger?.Error($"[EditorFloorOptimization] LightweightDeleteRemakePath failed: {e}");
                    _incrementalMode = false;
                    editor.RemakePath(); // Fallback
                }
            }
        }

        #endregion

        #region Patch: scnEditor.RemakePath - Skip redundant calls

        [HarmonyPatch(typeof(scnEditor), "RemakePath",
            new Type[] { typeof(bool), typeof(bool) })]
        public static class RemakePathRedundancyPatch
        {
            [HarmonyPrefix]
            public static bool Prefix(scnEditor __instance, bool applyEventsToFloors, bool remakeLevel)
            {
                if (!Main.Settings.optimizer.enableEditorFloorOptimization) return true;
                if (!Main.Settings.optimizer.skipRedundantRemakePath) return true;

                // Visual-only refresh (e.g. ToggleFloorNums calls RemakePath(false, false))
                // scnGame.RemakePath(false, false) does nothing meaningful
                // scnEditor.RemakePath then calls DrawFloorOffsetLines + DrawHolds + DrawFloorNums + DrawMultiPlanet
                // We skip the scnGame call and do only the minimum editor-level draws.
                if (!applyEventsToFloors && !remakeLevel)
                {
                    DrawFloorNums(__instance);
                    return false;
                }

                return true;
            }
        }

        #endregion

        #region Patch: scnGame.RemakePath - Optimize visual-only calls

        [HarmonyPatch(typeof(scnGame), "RemakePath",
            new Type[] { typeof(bool), typeof(bool) })]
        public static class GameRemakePathOptimizationPatch
        {
            [HarmonyPrefix]
            public static bool Prefix(scnGame __instance, bool applyEventsToFloors, bool remakeLevel)
            {
                if (!Main.Settings.optimizer.enableEditorFloorOptimization) return true;
                var editor = ADOBase.editor;
                if (editor == null) return true;

                if (!remakeLevel && !applyEventsToFloors)
                {
                    // Visual-only: just setup conductor
                    ADOBase.conductor.SetupConductorWithLevelData(__instance.levelData);
                    return false;
                }

                return true;
            }
        }

        #endregion

        #region Patch: DrawFloorNums

        [HarmonyPatch(typeof(scnEditor), "DrawFloorNums")]
        public static class DrawFloorNumsOptimizationPatch
        {
            [HarmonyPrefix]
            public static bool Prefix(scnEditor __instance)
            {
                if (!Main.Settings.optimizer.enableEditorFloorOptimization) return true;

                var floors = __instance.floors;
                if (floors == null) return false;

                bool showNums = __instance.showFloorNums && !__instance.playMode;
                // 增量插入/删除只会改变插入点之后的编号；前缀砖块的显示状态没变，
                // 没必要每次编辑都对 15 万块砖调一遍 SetActive。
                int first = _incrementalMode ? Math.Max(0, _incrementalSeqID - 1) : 0;
                for (int i = first; i < floors.Count; i++)
                {
                    var floor = floors[i];
                    if (floor != null && floor.enabled && floor.editorNumText != null)
                        floor.editorNumText.gameObject.SetActive(showNums && !floor.isFake);
                }
                return false;
            }
        }

        #endregion

        #region Patch: DrawFloorOffsetLines - Skip if no PositionTrack events

        [HarmonyPatch(typeof(scnEditor), "DrawFloorOffsetLines")]
        public static class DrawFloorOffsetLinesOptimizationPatch
        {
            [HarmonyPrefix]
            public static bool Prefix(scnEditor __instance)
            {
                if (!Main.Settings.optimizer.enableEditorFloorOptimization) return true;
                if (!Main.Settings.optimizer.skipRedundantRemakePath) return true;

                if (!AnyEventsHavePositionTrack(__instance.events))
                    return false; // no offset lines to draw

                return true;
            }
        }

        #endregion

        #region Patch: OffsetFloorIDsInEvents

        [HarmonyPatch(typeof(scnEditor), "OffsetFloorIDsInEvents")]
        public static class OffsetFloorIDsOptimizationPatch
        {
            [HarmonyPrefix]
            public static bool Prefix(scnEditor __instance, int startFloorID, int offset)
            {
                if (!Main.Settings.optimizer.enableEditorFloorOptimization) return true;
                if (!Main.Settings.optimizer.optimizeOffsetFloorEvents) return true;
                if (offset == 0) return false;

                var events = __instance.events;
                for (int i = 0; i < events.Count; i++)
                    if (events[i].floor > startFloorID)
                        events[i].floor += offset;

                var decorations = __instance.decorations;
                for (int i = 0; i < decorations.Count; i++)
                    if (decorations[i].floor > startFloorID)
                        decorations[i].floor += offset;

                _refreshDecSpritesRef ??= AccessTools.FieldRefAccess<scnEditor, bool>("refreshDecSprites");
                _refreshDecSpritesRef(__instance) = true;
                return false;
            }
        }

        #endregion

        #region Patch: Skip ApplyEventsToFloors during incremental insert

        /// <summary>
        /// 增量插入砖块时跳过全量 ApplyEventsToFloors。
        /// OffsetFloorIDsInEvents 已经处理了事件 floor ID 偏移，
        /// 全量重新应用事件对百万砖块是灾难性的。
        /// </summary>
        [HarmonyPatch(typeof(scnGame), "ApplyEventsToFloors",
            new[] { typeof(List<scrFloor>) })]
        public static class SkipApplyEventsOnInsertPatch
        {
            [HarmonyPrefix]
            public static bool Prefix()
            {
                if (!Main.Settings.optimizer.enableEditorFloorOptimization) return true;
                if (!_incrementalMode) return true;
                if (!Main.Settings.optimizer.skipApplyEventsOnInsert) return true;

                // Events already offset by OffsetFloorIDsInEvents / FloorWasCreatedOrDeleted.
                // Skip the full re-application for massive level performance.
                return false;
            }
        }

        #endregion

        #region Patch: Skip duplicated draws during incremental rebuild

        [HarmonyPatch(typeof(scnEditor), "DrawHolds", new[] { typeof(bool) })]
        public static class EditorDrawHoldsDuplicateSkipPatch
        {
            [HarmonyPrefix]
            public static bool Prefix()
            {
                if (!Main.Settings.optimizer.enableEditorFloorOptimization) return true;
                if (!Main.Settings.optimizer.incrementalFloorInsert) return true;
                // scnGame.RemakePath 里已经调用过 levelMaker.DrawHolds(false)，
                // scnEditor.RemakePath 紧接着的这一遍是重复的整表重建。
                return !_incrementalMode;
            }
        }

        [HarmonyPatch(typeof(scnEditor), "DrawMultiPlanet", new Type[0])]
        public static class EditorDrawMultiPlanetDuplicateSkipPatch
        {
            [HarmonyPrefix]
            public static bool Prefix()
            {
                if (!Main.Settings.optimizer.enableEditorFloorOptimization) return true;
                if (!Main.Settings.optimizer.incrementalFloorInsert) return true;
                // 同上：DrawMultiPlanet 在 scnGame.RemakePath 里已经整表扫描过一次。
                return !_incrementalMode;
            }
        }

        [HarmonyPatch(typeof(scrLevelMaker), "DrawHolds", new[] { typeof(bool) })]
        public static class LevelMakerDrawHoldsNoHoldsSkipPatch
        {
            [HarmonyPrefix]
            public static bool Prefix(scrLevelMaker __instance)
            {
                if (!Main.Settings.optimizer.enableEditorFloorOptimization) return true;
                if (!Main.Settings.optimizer.incrementalFloorInsert) return true;
                if (!_incrementalMode) return true;

                var floors = __instance.listFloors;
                if (floors == null) return true;

                // 还有旧的 hold 对象时必须走原版清理流程
                _holdGOsRef ??= AccessTools.FieldRefAccess<scrLevelMaker, List<GameObject>>("holdGOs");
                var holdGOs = _holdGOsRef(__instance);
                if (holdGOs != null && holdGOs.Count > 0) return true;

                // 没有任何 hold 砖块时重建 Hold 容器是空操作，跳过整表扫描。
                if (AnyFloorsHaveHolds(floors)) return true;
                return false;
            }
        }

        #endregion

        #region Utility

        public static void ResetState()
        {
            _incrementalMode = false;
        }

        #endregion
    }
}
