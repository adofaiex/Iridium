using System;
using System.Collections.Generic;
using System.Diagnostics;
using ADOFAI;
using HarmonyLib;
using UnityEngine;

namespace Iridium.Patches
{
    /// <summary>
    /// 大物量谱面加载耗时诊断（v2 / 2.9.8）。
    /// 每次加载只多几行 [LoadProfiler] 日志，始终启用。
    /// </summary>
    public static class LoadProfilerPatch
    {
        private static readonly Stopwatch TotalWatch = new();
        private static readonly Stopwatch DecodeWatch = new();
        private static readonly Stopwatch FloorWatch = new();
        private static readonly Stopwatch EventsWatch = new();

        /// <summary>是否正处于 LevelData.LoadLevel 过程中（用于只记录关卡文件的 JSON 解析）。</summary>
        private static bool _inLevelLoad;

        [HarmonyPatch(typeof(GDMiniJSON.Json), nameof(GDMiniJSON.Json.Deserialize), new[] { typeof(string) })]
        public static class JsonParseProfiler
        {
            private static readonly Stopwatch ParseWatch = new();

            [HarmonyPrefix]
            public static void Prefix(string __0)
            {
                if (!_inLevelLoad || __0 == null || __0.Length < 500_000) return;
                ParseWatch.Restart();
            }

            [HarmonyPostfix]
            public static void Postfix(string __0)
            {
                if (!_inLevelLoad || __0 == null || __0.Length < 500_000) return;
                Main.Logger?.Log($"[LoadProfiler]   JSON parse done: {ParseWatch.ElapsedMilliseconds} ms | {MemorySnapshot()}");
            }
        }

        /// <summary>托管/Unity 内存快照（MB），用于观察大谱面加载的内存峰值。</summary>
        internal static string MemorySnapshot()
        {
            try
            {
                long managed = GC.GetTotalMemory(false) / 1048576;
                long monoUsed = UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong() / 1048576;
                long unityAlloc = UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong() / 1048576;
                long unityReserved = UnityEngine.Profiling.Profiler.GetTotalReservedMemoryLong() / 1048576;
                return $"managed={managed}MB, monoUsed={monoUsed}MB, unity={unityAlloc}/{unityReserved}MB";
            }
            catch (Exception e)
            {
                return $"mem unavailable ({e.GetType().Name})";
            }
        }

        [HarmonyPatch(typeof(LevelData), nameof(LevelData.LoadLevel))]
        public static class LoadLevelProfiler
        {
            [HarmonyPrefix]
            public static void Prefix(string __0)
            {
                _inLevelLoad = true;
                TotalWatch.Restart();
                try
                {
                    long size = 0;
                    if (!string.IsNullOrEmpty(__0) && System.IO.File.Exists(__0))
                        size = new System.IO.FileInfo(__0).Length;
                    Main.Logger?.Log(
                        $"[LoadProfiler] load level start: {__0} ({size / 1048576}MB) | {MemorySnapshot()}");
                }
                catch (Exception e)
                {
                    Main.Logger?.Log($"[LoadProfiler] load level start log failed: {e.Message}");
                }
            }

            [HarmonyPostfix]
            public static void Postfix(LevelData __instance, bool __result)
            {
                _inLevelLoad = false;
                if (!__result) return;
                int floors = scrLevelMaker.instance != null && scrLevelMaker.instance.listFloors != null
                    ? scrLevelMaker.instance.listFloors.Count : 0;
                int events = __instance != null && __instance.levelEvents != null
                    ? __instance.levelEvents.Count : 0;
                Main.Logger?.Log(
                    $"[LoadProfiler] load level: {TotalWatch.ElapsedMilliseconds} ms (floors={floors}, events={events}) | {MemorySnapshot()}");
            }
        }

        [HarmonyPatch(typeof(LevelData), nameof(LevelData.Decode))]
        public static class DecodeProfiler
        {
            [HarmonyPrefix]
            public static void Prefix()
            {
                DecodeWatch.Restart();
            }

            [HarmonyPostfix]
            public static void Postfix(LevelData __instance)
            {
                int events = __instance != null && __instance.levelEvents != null
                    ? __instance.levelEvents.Count : 0;
                Main.Logger?.Log($"[LoadProfiler]   JSON+event decode: {DecodeWatch.ElapsedMilliseconds} ms (events={events})");
            }
        }

        [HarmonyPatch(typeof(scrLevelMaker), nameof(scrLevelMaker.InstantiateFloatFloors))]
        public static class FloatFloorsProfiler
        {
            [HarmonyPrefix]
            public static void Prefix()
            {
                FloorWatch.Restart();
            }

            [HarmonyPostfix]
            public static void Postfix(scrLevelMaker __instance)
            {
                int floors = __instance != null && __instance.listFloors != null
                    ? __instance.listFloors.Count : 0;
                Main.Logger?.Log($"[LoadProfiler]   instantiate floors: {FloorWatch.ElapsedMilliseconds} ms (floors={floors})");
            }
        }

        [HarmonyPatch(typeof(scnGame), nameof(scnGame.ApplyEventsToFloors),
            new[] { typeof(List<scrFloor>), typeof(LevelData), typeof(scrLevelMaker), typeof(List<LevelEvent>) })]
        public static class ApplyEventsProfiler
        {
            [HarmonyPrefix]
            public static void Prefix()
            {
                EventsWatch.Restart();
            }

            [HarmonyPostfix]
            public static void Postfix()
            {
                Main.Logger?.Log($"[LoadProfiler]   apply events to floors: {EventsWatch.ElapsedMilliseconds} ms");
            }
        }
    }
}
