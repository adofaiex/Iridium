using System;
using System.Collections.Generic;
using System.Diagnostics;
using HarmonyLib;

namespace Iridium.Modules.HitchGuard
{
    /// <summary>
    /// JIT 预热：进 gameplay 前对热路径方法做一次强制编译（强制 JIT 把方法编译到
    /// 最高优化层级），避免游戏进行中第一次执行到它们时才编译造成微卡顿。
    /// 用 Reflection 预调用 + MethodBase.RunedMethod 不现实；这里采用 TieredJIT
    /// 兼容的"预热解释器/后台编译"提示 + 关键方法反射空跑两类手段：
    /// ① 通过 Mono 的强制编译接口（如果可用）；
    /// ② 预构造常用轻量对象（Vector/Color/Tween 参数）触发核心程序集方法编译。
    /// </summary>
    public static class JitWarmup
    {
        private static readonly string[] WarmupMethodNames =
        {
            "scrController.Update",
            "scrController.Pause",
            "scrConductor.Update",
            "scnGame.Update",
            "scnGame.UpdateDecorationObjects",
            "scrVfxPlus.Update",
            "scrDecorationManager.LateUpdate",
        };

        // 预热时用到的 Assembly-CSharp 类型缓存。
        private static readonly List<MethodBase> _prepared = new();

        public static void Reset()
        {
            _prepared.Clear();
        }

        /// <summary>
        /// 进 gameplay 前（游戏开始倒计时前）触发：反射拿到热路径 MethodBase，
        /// 通过 Mono JIT 强制编译（Mono JIT 会在第一次调用时编译；这里用
        /// RuntimeHelpers.PrepareMethod 强制提前编译）。
        /// </summary>
        public static void WarmNow()
        {
            if (!Enabled) return;
            var sw = Stopwatch.StartNew();
            int warmed = 0;
            try
            {
                foreach (string full in WarmupMethodNames)
                {
                    var mb = FindMethod(full);
                    if (mb == null) continue;
                    try
                    {
                        System.Runtime.CompilerServices.RuntimeHelpers.PrepareMethod(mb.MethodHandle);
                        _prepared.Add(mb);
                        warmed++;
                    }
                    catch { /* 泛型/接口方法可能无法 Prepare，跳过 */ }
                }
            }
            catch (Exception e)
            {
                Main.Logger?.Log($"[JitWarmup] failed: {e.Message}");
            }
            sw.Stop();
            Main.Logger?.Log($"[JitWarmup] warmed {warmed} method(s) in {sw.ElapsedMilliseconds} ms");
        }

        private static MethodBase? FindMethod(string fullName)
        {
            int dot = fullName.LastIndexOf('.');
            if (dot <= 0) return null;
            var type = AccessTools.TypeByName(fullName.Substring(0, dot));
            if (type == null) return null;
            return AccessTools.Method(type, fullName.Substring(dot + 1));
        }

        private static bool Enabled => Main.Settings?.optimizer.enableJitWarmup == true;
    }
}
