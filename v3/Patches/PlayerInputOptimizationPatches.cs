using Iridium.Config;
using Iridium.Runtime;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace Iridium.Patches
{
    /// <summary>
    /// Eliminates per-frame heap allocations in the player input hot path.
    ///
    /// Vanilla scrPlayer.Simulated_PlayerControl_Update (called every frame per
    /// player) wraps hold-check callbacks in AsyncInputUtils.WhileFloorNotChange
    /// using captured lambdas:
    ///
    ///     AsyncInputUtils.WhileFloorNotChange(this, delegate { CheckPostHoldFail(targetTick); });
    ///     ... (7 total)
    ///
    /// This patch replaces the method with an equivalent implementation that
    /// invokes the hold-check callbacks via compiled delegates (created once
    /// via AccessTools.MethodDelegate) instead of per-frame lambdas.
    ///
    /// 3.4.0 changed the tick parameter type from ulong? to long?. This patch
    /// resolves the target and its prefix at runtime (MonoAdaptivePatch) so a
    /// single implementation serves both 3.3.x and 3.4.0.
    /// </summary>
    public static class PlayerInputOptimizationPatches
    {
        private static readonly Dictionary<string, Delegate> _delegates = new();

        // Cached FieldRefs to private scrPlayer fields.
        private static AccessTools.FieldRef<scrPlayer, bool>? _nextTileIsHoldCachedRef;
        private static AccessTools.FieldRef<scrPlayer, bool>? _validInputWasReleasedThisFrameRef;
        private static AccessTools.FieldRef<scrPlayer, Vector2>? _cachedCamyToPosRef;

        private static Action<scrPlayer, T?> GetDelegate<T>(string methodName) where T : struct
        {
            string key = typeof(T).Name + ":" + methodName;
            if (_delegates.TryGetValue(key, out var cached)) return (Action<scrPlayer, T?>)cached;

            var method = AccessTools.Method(typeof(scrPlayer), methodName);
            var compiled = AccessTools.MethodDelegate<Action<scrPlayer, T?>>(method, null);
            _delegates[key] = compiled;
            return compiled;
        }

        [IriPatch(Path = "optimizer/playerInput", Pre = typeof(OptimizerSettings), Condition = "optimizePlayerInputAllocations")]
        public sealed class SimulatedPlayerControlUpdatePatch : MonoAdaptivePatch
        {
            private static bool _longTicks;

            public override string Id => "PlayerInputSimulatedUpdate";

            protected override MethodBase? GetTargetMethod()
            {
                var method = AccessTools.Method(typeof(scrPlayer), "Simulated_PlayerControl_Update");
                var parameters = method?.GetParameters();
                _longTicks = parameters is { Length: > 0 } && parameters[0].ParameterType == typeof(long?);
                return method;
            }

            protected override MethodInfo? Prefix =>
                AccessTools.Method(typeof(SimulatedPlayerControlUpdatePatch), _longTicks ? nameof(PrefixLong) : nameof(PrefixUlong));

            public static bool PrefixUlong(scrPlayer __instance, ulong? targetTick)
                => Apply(__instance, targetTick);

            public static bool PrefixLong(scrPlayer __instance, long? targetTick)
                => Apply(__instance, targetTick);

            // 3.4.0 给 HitInputEvent 追加了 handleAll 参数（可选参数是编译期的，
            // 旧签名直接调用在新版会 MissingMethod），运行时按参数表选委托。
            private static Func<scrPlayer, bool>? _hitInputEventUp;

            private static Func<scrPlayer, bool> GetHitInputEventUp()
            {
                if (_hitInputEventUp != null) return _hitInputEventUp;

                var method = AccessTools.Method(typeof(scrPlayer), "HitInputEvent");
                if (method == null) return _hitInputEventUp = _ => false;

                if (method.GetParameters().Length >= 3)
                {
                    var del = AccessTools.MethodDelegate<Func<scrPlayer, bool, InputEventState, bool, bool>>(method, null);
                    return _hitInputEventUp = p => del(p, false, InputEventState.Up, true);
                }

                var old = AccessTools.MethodDelegate<Func<scrPlayer, bool, InputEventState, bool>>(method, null);
                return _hitInputEventUp = p => old(p, false, InputEventState.Up);
            }

            private static bool Apply<T>(scrPlayer __instance, T? targetTick) where T : struct
            {
                // Opt-in optimization — when disabled, fall through to the original.
                if (!Main.Settings.optimizer.optimizePlayerInputAllocations)
                {
                    return true;
                }

                if (!__instance.alive || ADOBase.controller.paused || __instance.currFloor == null || ADOBase.controller.isCutscene)
                {
                    return false;
                }

                _nextTileIsHoldCachedRef ??= AccessTools.FieldRefAccess<scrPlayer, bool>("__nextTileIsHoldCached");
                _validInputWasReleasedThisFrameRef ??= AccessTools.FieldRefAccess<scrPlayer, bool>("validInputWasReleasedThisFrame");
                _cachedCamyToPosRef ??= AccessTools.FieldRefAccess<scrPlayer, Vector2>("cachedCamyToPos");

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

                RunWhileFloorUnchanged(__instance, GetDelegate<T>("CheckPostHoldFail"), targetTick);
                RunWhileFloorUnchanged(__instance, GetDelegate<T>("OttoHoldHit"), targetTick);

                GetDelegate<T>("HitAutoFloors")(__instance, targetTick);
                GetDelegate<T>("UpdateHoldBehavior")(__instance, targetTick);

                RunWhileFloorUnchanged(__instance, GetDelegate<T>("HitHoldFloorsIfStartedAtHold"), targetTick);
                RunWhileFloorUnchanged(__instance, GetDelegate<T>("CheckPreHoldFail"), targetTick);

                // 3.4.0 把 UpdateHoldKeys 的循环从 WhileFloorNotChange 改为按 keyTimes
                // 数量变化收敛；3.3.x 维持原语义。
                if (_longTicks)
                {
                    int count;
                    do
                    {
                        count = __instance.keyTimes.Count;
                        GetDelegate<T>("UpdateHoldKeys")(__instance, targetTick);
                    }
                    while (__instance.keyTimes.Count != count);
                }
                else
                {
                    RunWhileFloorUnchanged(__instance, GetDelegate<T>("UpdateHoldKeys"), targetTick);
                }

                if (RDInput.GetMain(ButtonState.WentUp) > 0)
                {
                    GetHitInputEventUp()(__instance);
                }

                Vector2 topos = ADOBase.controller.camy.topos;
                if (_cachedCamyToPosRef(__instance) != topos)
                {
                    scrPlayer.shouldReplaceCamyToPos = true;
                    scrPlayer.overrideCamyToPos = topos;
                }

                return false;
            }

            /// <summary>Mirror of AsyncInputUtils.WhileFloorNotChange, invoked without lambdas.</summary>
            private static void RunWhileFloorUnchanged<T>(scrPlayer player, Action<scrPlayer, T?> action, T? tick) where T : struct
            {
                int num = -1;
                while (num != player.currFloor.seqID)
                {
                    num = player.currFloor.seqID;
                    action(player, tick);
                }
            }
        }
    }
}
