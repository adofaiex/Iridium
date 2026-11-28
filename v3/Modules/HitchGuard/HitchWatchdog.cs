using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace Iridium.Modules.HitchGuard
{
    /// <summary>
    /// 游玩期卡顿 watchdog：由 Main.OnUpdate 每帧驱动，帧耗时超过阈值时记录
    /// 一次 hitch（帧长、GC 增量、托管内存快照），可附带短暂窗口内的
    /// 汇总报告，帮助定位突刺来源。只观察、不干预游戏。
    /// 非游玩态（菜单/设置/加载）只推进基线，不计 hitch。
    /// </summary>
    public static class HitchWatchdog
    {
        private static readonly Stopwatch _frameWatch = Stopwatch.StartNew();
        private static long _lastFrameElapsed;

        // hitch 统计（本会话累计）
        private static int _hitchCount;
        private static long _worstHitchMs;
        private static readonly List<string> _recent = new();

        // GC/heap 基线：每帧推进，hitch 报告的增量只覆盖最近一帧的活动
        private static bool _hasGcBaseline;
        private static int _lastGcGen0, _lastGcGen1, _lastGcGen2;
        private static long _lastHeapBytes;

        // 帧基线跨度上限：超过视为基线过期（mod 停用期间等），只重置不计 hitch
        private const long GapMs = 1000;

        internal static bool Enabled => Main.Settings?.optimizer.enableHitchWatchdog == true;
        private static long ThresholdMs => Main.Settings?.optimizer.hitchThresholdMs is int v ? Math.Clamp(v, 17, 200) : 33;

        public static void Reset()
        {
            _hitchCount = 0;
            _worstHitchMs = 0;
            _recent.Clear();
            _hasGcBaseline = false;
            _lastFrameElapsed = _frameWatch.ElapsedMilliseconds;
        }

        /// <summary>由 Main.OnUpdate 驱动：测量上一帧耗时，超阈值则记录。
        /// inGameplay=false 时只刷新帧/GC 基线，不计 hitch。</summary>
        public static void Tick(bool inGameplay)
        {
            long now = _frameWatch.ElapsedMilliseconds;
            long frameMs = now - _lastFrameElapsed;
            _lastFrameElapsed = now;

            if (frameMs > GapMs)
            {
                // 基线已过期（长时间未 tick）：只重置基线，不计 hitch
                SampleGcBaseline();
                return;
            }

            if (inGameplay && frameMs > ThresholdMs)
            {
                _hitchCount++;
                if (frameMs > _worstHitchMs) _worstHitchMs = frameMs;

                string snapshot = Describe(frameMs);
                _recent.Add(snapshot);
                if (_recent.Count > MaxRecent) _recent.RemoveAt(0);

                Main.Logger?.Log(snapshot);
            }

            // 每帧推进 GC/heap 基线（含非游玩态）：hitch 增量始终只反映最近一帧
            SampleGcBaseline();
        }

        private const int MaxRecent = 32;

        /// <summary>构造一条 hitch 记录：帧长 + 最近一帧的 GC 压力变化 + 内存快照。</summary>
        private static string Describe(long frameMs)
        {
            var sb = new StringBuilder(256);
            sb.Append("[HitchWatchdog] ").Append(frameMs).Append(" ms");

            try
            {
                if (_hasGcBaseline)
                {
                    long heap = GC.GetTotalMemory(false);
                    int dg0 = GC.CollectionCount(0) - _lastGcGen0;
                    int dg1 = GC.CollectionCount(1) - _lastGcGen1;
                    int dg2 = GC.CollectionCount(2) - _lastGcGen2;
                    long dHeap = heap - _lastHeapBytes;
                    sb.Append($" | GC d(0:{dg0},1:{dg1},2:{dg2}) heap {(heap / 1024.0 / 1024.0):F1}MB ({dHeap / 1024.0:+0.0;-0.0}MB)");
                }
            }
            catch { /* GC 统计失败不影响 watchdog */ }

            string? mem = Patches.Optimizer.LoadProfilerPatch.MemorySnapshot();
            if (!string.IsNullOrEmpty(mem)) sb.Append(" | ").Append(mem);
            return sb.ToString();
        }

        private static void SampleGcBaseline()
        {
            try
            {
                _lastGcGen0 = GC.CollectionCount(0);
                _lastGcGen1 = GC.CollectionCount(1);
                _lastGcGen2 = GC.CollectionCount(2);
                _lastHeapBytes = GC.GetTotalMemory(false);
                _hasGcBaseline = true;
            }
            catch { /* GC 统计失败不影响 watchdog */ }
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
