using System;
using ADOFAI;
using DG.Tweening;
using HarmonyLib;
using UnityEngine;

namespace Iridium.Patches
{
 /// <summary>
  /// DOTween 性能设置 — 只走配置，不做运行时反射或 Transpiler。
  /// </summary>
 public static class DOTweenOptimizationPatches
 {
 private static bool _isInitialized = false;
 private static bool _capacityHasBeenSet = false; // DOTween 容量只能增不能减

 #region Runtime Settings

 /// <summary>应用 DOTween 运行时设置。容量只能设一次；其余（可复用、安全模式）可动态改变。</summary>
 public static void ApplyRuntimeSettings()
 {
 if (!Main.Settings.optimizer.enableOptimizer || !Main.Settings.optimizer.optimizeDOTweenGlobal)
 {
 return;
 }

 try
 {
 // 容量只在第一次应用
 if (!_capacityHasBeenSet)
 {
 int tweeners = Math.Max(200, Main.Settings.optimizer.dotweenTweenerCapacity);
 int sequences = Math.Max(50, Main.Settings.optimizer.dotweenSequenceCapacity);

 DOTween.SetTweensCapacity(tweeners, sequences);
 _capacityHasBeenSet = true;

 Main.Logger?.Log($"[DOTweenOptimization] Capacity set: {tweeners}/{sequences}");
 }

 DOTween.defaultRecyclable = Main.Settings.optimizer.dotweenDefaultRecyclable;

 // 保守：只在生产环境禁用安全模式
 if (!Debug.isDebugBuild && Main.Settings.optimizer.dotweenDisableSafeMode)
 {
 DOTween.useSafeMode = false;
 DOTween.logBehaviour = LogBehaviour.ErrorsOnly;
 }
 else
 {
 DOTween.useSafeMode = true;
 DOTween.logBehaviour = LogBehaviour.Default;
 }

 _isInitialized = true;
 }
 catch (Exception e)
 {
 Main.Logger?.Error($"[DOTweenOptimization] Failed to apply settings: {e}");
 _isInitialized = false;
 }
 }

 /// <summary>重置 DOTween 设置。容量不会恢复（DOTween 只能增不能减），其余恢复默认。</summary>
 public static void ResetRuntimeSettings()
 {
 try
 {
 DOTween.defaultRecyclable = false;
 DOTween.useSafeMode = true;
 DOTween.logBehaviour = LogBehaviour.Default;

 _isInitialized = false;

 Main.Logger?.Log("[DOTweenOptimization] Settings reset (capacity preserved due to DOTween limitation)");
 }
 catch (Exception e)
 {
 Main.Logger?.Error($"[DOTweenOptimization] Failed to reset settings: {e}");
 }
 }

 #endregion

 #region Performance Monitoring - 性能监控（仅统计，不修改行为）

 private static int _lastReportTime = -1;

 /// <summary>定期报告 DOTween 状态（仅统计，不修改行为）。</summary>
 public static void MonitorDOTweenStatus()
 {
 if (!Main.Settings.optimizer.enableOptimizer || !Main.Settings.optimizer.optimizeDOTweenGlobal)
 {
 return;
 }

 if (!_isInitialized) return;

 // 用时间而不是帧数，避免在 UI 线程外执行
 int currentTime = Environment.TickCount;
 if (currentTime - _lastReportTime < 30000) return;

 _lastReportTime = currentTime;

 try
 {
 int activeTweens = DOTween.TotalPlayingTweens();
 Main.Logger?.Log($"[DOTweenOptimization] Status: Active={activeTweens}");
 }
 catch (Exception e)
 {
 Main.Logger?.Error($"[DOTweenOptimization] Monitor failed: {e}");
 }
 }

 #endregion

 #region Safe Tween Helper - 安全的Tween辅助方法

 public static Tween? SetTweenProperties(Tween tween)
 {
 if (tween == null) return null;

 try
 {
 if (Main.Settings.optimizer.enableOptimizer && Main.Settings.optimizer.optimizeDOTweenGlobal)
 {
 tween.SetRecyclable(true);
 }
 }
 catch (Exception e)
 {
 Main.Logger?.Error($"[DOTweenOptimization] Failed to set tween properties: {e}");
 }

 return tween;
 }

 #endregion
 }
}
