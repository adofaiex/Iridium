using HarmonyLib;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Iridium.Patches
{
    /// <summary>
    /// v3 移植（适配 2.9.8）：消除输入热路径的每帧 lambda 分配。
    ///
    /// 原版 scrController.Simulated_PlayerControl_Update 每帧为 7 个
    /// hold-check 回调创建捕获 this + targetTick 的匿名委托（AsyncInputUtils
    /// .WhileFloorNotChange）。本补丁用一次性编译的委托（AccessTools.MethodDelegate）
    /// 复刻同一逻辑，每帧零分配。字段经 FieldRef 访问。
    /// </summary>
    public static class PlayerInputOptimizationPatches
    {
        private static readonly Dictionary<string, Action<scrController, ulong?>> _delegates = new();

        private static AccessTools.FieldRef<scrController, bool>? _nextTileIsHoldCachedRef;
        private static AccessTools.FieldRef<scrController, bool>? _validInputWasReleasedThisFrameRef;
        private static AccessTools.FieldRef<scrController, Vector3>? _cachedCamyToPosRef;

        private static Action<scrController, ulong?> GetDelegate(string methodName)
        {
            if (_delegates.TryGetValue(methodName, out var cached)) return cached;

            var method = AccessTools.Method(typeof(scrController), methodName);
            var compiled = AccessTools.MethodDelegate<Action<scrController, ulong?>>(method, null);
            _delegates[methodName] = compiled;
            return compiled;
        }

        [HarmonyPatch(typeof(scrController), "Simulated_PlayerControl_Update")]
        public static class SimulatedPlayerControlUpdatePatch
        {
            [HarmonyPrefix]
            public static bool Prefix(scrController __instance, ulong? targetTick)
            {
                if (Main.Settings?.optimizer.optimizePlayerInputAllocations != true)
                {
                    return true;
                }

                if (__instance.paused || __instance.currFloor == null || __instance.isCutscene)
                {
                    return false;
                }

                _nextTileIsHoldCachedRef ??= AccessTools.FieldRefAccess<scrController, bool>("__nextTileIsHoldCached");
                _validInputWasReleasedThisFrameRef ??= AccessTools.FieldRefAccess<scrController, bool>("validInputWasReleasedThisFrame");
                _cachedCamyToPosRef ??= AccessTools.FieldRefAccess<scrController, Vector3>("cachedCamyToPos");

                _nextTileIsHoldCachedRef(__instance) = false;
                _validInputWasReleasedThisFrameRef(__instance) = __instance.ValidInputWasReleased();
                _cachedCamyToPosRef(__instance) = ADOBase.controller.camy.topos;

                if ((bool)__instance.currFloor.nextfloor)
                {
                    scrFloor nextfloor = __instance.currFloor.nextfloor;
                    while (nextfloor.midSpin && (bool)nextfloor.nextfloor)
                    {
                        nextfloor = nextfloor.nextfloor;
                    }
                    _nextTileIsHoldCachedRef(__instance) = nextfloor.holdLength > -1;
                }

                RunWhileFloorUnchanged(__instance, GetDelegate("CheckPostHoldFail"), targetTick);
                RunWhileFloorUnchanged(__instance, GetDelegate("OttoHoldHit"), targetTick);

                GetDelegate("HitAutoFloors")(__instance, targetTick);
                GetDelegate("UpdateHoldBehavior")(__instance, targetTick);

                RunWhileFloorUnchanged(__instance, GetDelegate("HitHoldFloorsIfStartedAtHold"), targetTick);
                RunWhileFloorUnchanged(__instance, GetDelegate("CheckPreHoldFail"), targetTick);
                RunWhileFloorUnchanged(__instance, GetDelegate("UpdateHoldKeys"), targetTick);

                if (RDInput.GetMain(ButtonState.WentUp) > 0)
                {
                    __instance.HitInputEvent(isAuto: false, InputEventState.Up);
                }

                Vector3 topos = ADOBase.controller.camy.topos;
                if (_cachedCamyToPosRef(__instance) != topos)
                {
                    scrController.shouldReplaceCamyToPos = true;
                    scrController.overrideCamyToPos = topos;
                }

                return false;
            }

            /// <summary>Mirror of AsyncInputUtils.WhileFloorNotChange, invoked without lambdas.</summary>
            private static void RunWhileFloorUnchanged(scrController controller, Action<scrController, ulong?> action, ulong? tick)
            {
                int num = -1;
                while (num != controller.currFloor.seqID)
                {
                    num = controller.currFloor.seqID;
                    action(controller, tick);
                }
            }
        }
    }
}
