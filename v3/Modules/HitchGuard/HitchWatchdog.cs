using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace Iridium.Modules.HitchGuard
{
    /// <summary>
    /// 游玩期卡顿 watchdog：由 Main.OnUpdate 每帧驱动，帧耗时超过阈值时记录
    /// 一次 hitch（帧长、GC 前后堆、GC 计数、托管内存快照），可附带短暂窗口内的
    /// 汇总报告，帮助定位突刺来源。只观察、不干预游戏。
    /// </summary>
    public static class HitchWatchdog
    {
        private static readonly Stopwatch _frameWatch = Stopwatch.StartNew();
        private static long _lastFrameElapsed;

        // hitch 统计（本会话累计）
        private static int _hitchCount;
        private static long _worstHitchMs;
        private static readonly List<string> _recent = new();

        // 突刺瞬时窗口内 GC 状态
        private static int _lastGcGen0 = -1, _lastGcGen1 = -1, _lastGcGen2 = -1;
        private static long _lastHeapBytes = -1;

        internal static bool Enabled => Main.Settings?.optimizer.enableHitchWatchdog == true;
        private static long ThresholdMs => Main.Settings?.optimizer.hitchThresholdMs is int v && v > 0 ? v : 33;

        public static void Reset()
        {
            _hitchCount = 0;
            _worstHitchMs = 0;
            _recent.Clear();
            _lastFrameElapsed = _frameWatch.ElapsedMilliseconds;
        }

        /// <summary>由 Main.OnUpdate 驱动：测量上一帧耗时，超阈值则记录。</summary>
        public static void Tick()
        {
            long now = _frameWatch.ElapsedMilliseconds;
            long frameMs = now - _lastFrameElapsed;
            _lastFrameElapsed = now;

            if (frameMs <= ThresholdMs) return;

            _hitchCount++;
            if (frameMs > _worstHitchMs) _worstHitchMs = frameMs;

            string snapshot = Describe(frameMs);
            _recent.Add(snapshot);
            if (_recent.Count > MaxRecent) _recent.RemoveAt(0);

            Main.Logger?.Log(snapshot);
        }

        private const int MaxRecent = 32;

        /// <summary>构造一条 hitch 记录：帧长 + GC 压力变化 + 内存快照。</summary>
        private static string Describe(long frameMs)
        {
            var sb = new StringBuilder(256);
            sb.Append("[HitchWatchdog] ").Append(frameMs).Append(" ms");

            try
            {
                int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
                long heap = GC.GetTotalMemory(false);
                if (_lastGcGen0 >= 0)
                {
                    int dg0 = g0 - _lastGcGen0, dg1 = g1 - _lastGcGen1, dg2 = g2 - _lastGcGen2;
                    long dHeap = heap - _lastHeapBytes;
                    sb.Append($" | GC d(0:{dg0},1:{dg1},2:{dg2}) heap {(heap / 1024.0 / 1024.0):F1}MB ({dHeap / 1024.0:+0.0;-0.0}MB)");
                }
                _lastGcGen0 = g0; _lastGcGen1 = g1; _lastGcGen2 = g2; _lastHeapBytes = heap;
            }
            catch { /* GC 统计失败不影响 watchdog */ }

            string? mem = Patches.Optimizer.LoadProfilerPatch.MemorySnapshot();
            if (!string.IsNullOrEmpty(mem)) sb.Append(" | ").Append(mem);
            return sb.ToString();
        }

        /// <summary>会话累计摘要（供 UI/日志使用）。</summary>
        public static string Summary()
        {
            return $"[HitchWatchdog] session: hitches={_hitchCount}, worst={_worstHitchMs} ms" +
                   (_recent.Count > 0 ? $", recent:\n  " + string.Join("\n  ", _recent) : "");
        }

        public static int HitchCount => _hitchCount;
        public static long WorstHitchMs => _worstHitchMs;
    }
}
