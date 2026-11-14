using System;
using System.Reflection;
using HarmonyLib;
using Iridium.Config;
using TMPro;
using UnityEngine;

namespace Iridium.Patches
{
    /// <summary>Judge text display with offset support, using SuperStrictJudge's timing approach.</summary>
    public static class JudgeTextPatches
    {
        private static JudgeTextSettings Settings => Main.Settings.judgeText;

        /// <summary>
        /// Timing offset from angular offset (targetExitAngle - actualAngle). Sign convention: early hit = +, late hit = -.
        /// </summary>
        private static double CalculateTimingFromAngle(float angularOffset)
        {
            var controller = scrController.instance;
            var conductor = scrConductor.instance;
            if (controller == null || conductor == null) return 0;

            double bpm = conductor.bpm;
            double speed = controller.speed;
            double pitch = conductor.song.pitch;

            // Standard timing is early = negative, late = positive.
            double standardTiming = angularOffset * (controller.isCW ? 1.0 : -1.0) * 60000.0 / (Math.PI * bpm * speed * pitch);
            // Inverse of standard timing, per the requested sign convention
            return -standardTiming;
        }

        /// <summary>Get display text for offset mode</summary>
        private static string GetOffsetText(double timing)
        {
            if (double.IsNaN(timing) || double.IsInfinity(timing))
                return "0ms";
            
            // Display as integer (F0) as per user example "5ms"
            // Use Math.Round to ensure 0.5ms becomes 1ms
            long ms = (long)Math.Round(timing);
            return $"{(ms >= 0 ? "+" : "-")}{Math.Abs(ms)}ms";
        }

        /// <summary>scrHitTextMesh.Init：自定义文本模式（非 offset 模式）。</summary>
        [HarmonyPatch(typeof(scrHitTextMesh), "Init")]
        public static class HitTextMeshInitPatch
        {
            public static void Postfix(scrHitTextMesh __instance, HitMargin hitMargin, TextMesh ___text)
            {
                if (!Settings.enableJudgeTextCustomization) return;
                if (Settings.showAsOffset) return; // Offset mode handled by Show patch

                ___text.text = Settings.GetTextForHitMargin((int)hitMargin);
            }
        }

        /// <summary>scrHitTextMesh.Show：offset 模式显示，angle 参数是弧度角偏移。</summary>
        [HarmonyPatch(typeof(scrHitTextMesh), "Show")]
        public static class HitTextMeshShowPatch
        {
            public static void Prefix(scrHitTextMesh __instance, float angle, TextMesh ___text)
            {
                if (!Settings.enableJudgeTextCustomization) return;
                if (___text == null) return;

                double timing = CalculateTimingFromAngle(angle);

                if (Settings.showAsOffset)
                {
                    ___text.text = GetOffsetText(timing);
                    return;
                }

                // 模板模式支持 {offset} / {offset:x} 占位符（v3 移植）
                string template = Settings.GetTextForHitMargin((int)__instance.hitMargin);
                ___text.text = JudgeTextSettings.ReplaceOffset(template, timing);
            }
        }

        /// <summary>scrController.Awake_Rewind：倒带时重置状态。</summary>
        [HarmonyPatch(typeof(scrController), "Awake_Rewind")]
        public static class ResetTimingOnRewindPatch
        {
            public static void Postfix()
            {
                // 目前没有全局状态，保留以备后用
            }
        }
    }
}
