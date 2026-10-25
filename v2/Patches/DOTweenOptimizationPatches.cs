using System;
using ADOFAI;
using DG.Tweening;
using HarmonyLib;
using UnityEngine;

namespace Iridium.Patches
{
 /// <summary>
 /// DOTween tuning via runtime settings only (no reflection, no transpilers).
 /// </summary>
 public static class DOTweenOptimizationPatches
 {
 private static bool _isInitialized = false;
 private static bool _capacityHasBeenSet = false; // 容量是否已设置（只能设置一次）

 #region Runtime Settings

 /// <summary>
 /// DOTween capacity can only grow, so it is applied once; other settings are dynamic.
 /// </summary>
 public static void ApplyRuntimeSettings()
 {
 if (!Main.Settings.optimizer.enableOptimizer || !Main.Settings.optimizer.optimizeDOTweenGlobal)
 {
 return;
 }

 try
 {
 // 容量设置：只在第一次时应用（DOTween限制）
 if (!_capacityHasBeenSet)
 {
 int tweeners = Math.Max(200, Main.Settings.optimizer.dotweenTweenerCapacity);
 int sequences = Math.Max(50, Main.Settings.optimizer.dotweenSequenceCapacity);

 DOTween.SetTweensCapacity(tweeners, sequences);
 _capacityHasBeenSet = true;

 Main.Logger?.Log($"[DOTweenOptimization] Capacity set: {tweeners}/{sequences}");
 }

 // 可以动态改变的设置
 DOTween.defaultRecyclable = Main.Settings.optimizer.dotweenDefaultRecyclable;

 // 保守设置：只在生产环境禁用安全模式
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

 /// <summary>
 /// Resets the dynamic settings; capacity stays raised (DOTween cannot shrink it).
 /// </summary>
 public static void ResetRuntimeSettings()
 {
 try
 {
 // 恢复可动态改变的设置
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

 /// <summary>
 /// 定期报告DOTween状态（仅用于监控，在主线程安全执行）
 /// </summary>
 public static void MonitorDOTweenStatus()
 {
 if (!Main.Settings.optimizer.enableOptimizer || !Main.Settings.optimizer.optimizeDOTweenGlobal)
 {
 return;
 }

 if (!_isInitialized) return;

 // 使用时间而不是帧数，避免在UI线程外执行
 int currentTime = Environment.TickCount;
 if (currentTime - _lastReportTime < 30000) return; // 每30秒报告一次

 _lastReportTime = currentTime;

 try
 {
 // 仅报告状态，不做任何修改
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

 /// <summary>
 /// 安全地设置Tween的属性
 /// </summary>
 public static Tween? SetTweenProperties(Tween tween)
 {
 if (tween == null) return null;

 try
 {
 if (Main.Settings.optimizer.enableOptimizer && Main.Settings.optimizer.optimizeDOTweenGlobal)
 {
 // 只设置可复用，不修改其他关键属性
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
