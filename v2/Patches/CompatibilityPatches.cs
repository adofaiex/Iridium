using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using ADOFAI;
using HarmonyLib;
using UnityEngine;

namespace Iridium.Patches
{
    public static class CompatibilityPatches
    {
        [HarmonyPatch(typeof(scnEditor), nameof(scnEditor.Play))]
        public static class LegacyPauseFixPatch_Play
        {
            public static bool isPlayingFromEditor = false;
            public static void Prefix()
            {
                isPlayingFromEditor = true;
            }
            public static Exception Finalizer(Exception __exception)
            {
                isPlayingFromEditor = false;
                return __exception;
            }
        }

        [HarmonyPatch(typeof(scnGame), "ApplyCoreEventsToFloors", typeof(List<scrFloor>), typeof(LevelData), typeof(scrLevelMaker), typeof(List<LevelEvent>), typeof(List<LevelEvent>[]))]
        public static class LegacyPauseFixPatch_Apply
        {
            public static void Prefix(List<scrFloor> floors, LevelData levelData, scrLevelMaker lm, List<LevelEvent> events, List<LevelEvent>[] floorEvents)
            {
                if (!LegacyPauseFixPatch_Play.isPlayingFromEditor) return;
                if (floorEvents == null) return;

                bool isCCW = false;
                foreach (var floor in floors)
                {
                    var floorEventList = floorEvents[floor.seqID];
                    for (int i = 0; i < floorEventList.Count; i++)
                    {
                        if (floorEventList[i].eventType == LevelEventType.Twirl)
                            isCCW = !isCCW;
                    }
                    floor.isCCW = isCCW;
                }
            }
        }

        [HarmonyPatch(typeof(scrDecoration), "HitboxTriggerAction")]
        public static class NoFailTooEarlyPatch
        {
            public static void Prefix(scrDecoration __instance, out HitboxType __state, scrPlanet planet)
            {
                __state = __instance.hitbox;
                if (!ADOBase.controller.gameworld || !ADOBase.controller.noFail || __instance.hitbox != HitboxType.Kill)
                {
                    return;
                }

                if (RDC.auto)
                {
                    return;
                }

                __instance.hitbox = HitboxType.None;
                if ((planet != null && planet.iFrames > 0) || __instance.hitOnce)
                {
                    return;
                }

                ADOBase.controller.mistakesManager.AddHit(HitMargin.FailOverload);
                ADOBase.controller.errorMeter?.AddHit(float.NegativeInfinity);
                ADOBase.controller.chosenPlanet.MarkFail()?.BlinkForSeconds(3);
            }

            public static void Postfix(scrDecoration __instance, HitboxType __state)
            {
                __instance.hitbox = __state;
            }
        }

        /// <summary>
/// Scales CameraFilterPack's internal shader animation with pitch: their OnRenderImage
/// does TimeX += Time.deltaTime, so speeding the video up after a low-pitch recording
/// would otherwise run the shaders faster than the original.
/// </summary>
        [HarmonyPatch]
        public static class ScaleFilterSpeedWithPitchPatch
        {
            static IEnumerable<MethodBase> TargetMethods()
            {
                try { Assembly.Load("Assembly-CSharp-firstpass"); }
                catch { }

                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type[] types;
                    try { types = asm.GetTypes(); }
                    catch { continue; }

                    foreach (var type in types)
                    {
                        if (!type.Name.Contains("CameraFilterPack")) continue;
                        var method = type.GetMethod("OnRenderImage",
                            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        if (method != null) yield return method;
                    }
                }
            }

            [HarmonyTranspiler]
            static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                var get_deltaTime = AccessTools.PropertyGetter(typeof(Time), "deltaTime");
                var getMultiplier = AccessTools.Method(typeof(ScaleFilterSpeedWithPitchPatch), nameof(GetPitchMultiplier));

                foreach (var code in instructions)
                {
                    yield return code;
                    if (code.Calls(get_deltaTime))
                    {
                        yield return new CodeInstruction(OpCodes.Call, getMultiplier);
                        yield return new CodeInstruction(OpCodes.Mul);
                    }
                }
            }

            static float GetPitchMultiplier()
            {
                if (!Main.Settings.compatibility.scaleFilterSpeedWithPitch) return 1f;
                var cond = scrConductor.instance;
                if (cond == null || cond.song == null) return 1f;
                return cond.song.pitch;
            }
        }
    }
}
