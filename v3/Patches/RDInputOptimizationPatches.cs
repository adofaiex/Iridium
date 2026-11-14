using Iridium.Config;
using HarmonyLib;
using System;
using System.Collections.Generic;

namespace Iridium.Patches
{
    /// <summary>
    /// Eliminates the per-frame List allocation in the RDInput query path.
    ///
    /// Vanilla RDInput.GetStateKeys builds a fresh List&lt;AnyKeyCode&gt; on every call,
    /// and scrPlayer.ValidInputWasReleased calls GetMainHeldKeys every frame per player.
    /// Each ButtonState gets a reusable [ThreadStatic] List instead: all call sites
    /// consume the result immediately and never hold it across frames.
    /// </summary>
    public static class RDInputOptimizationPatches
    {
        [ThreadStatic]
        private static List<AnyKeyCode>? _cachedWentDown;
        [ThreadStatic]
        private static List<AnyKeyCode>? _cachedWentUp;
        [ThreadStatic]
        private static List<AnyKeyCode>? _cachedIsDown;
        [ThreadStatic]
        private static List<AnyKeyCode>? _cachedIsUp;

        private static List<AnyKeyCode> GetPooled(ButtonState state)
        {
            switch (state)
            {
                case ButtonState.WentDown:
                    if (_cachedWentDown == null) _cachedWentDown = new List<AnyKeyCode>();
                    _cachedWentDown.Clear();
                    return _cachedWentDown;
                case ButtonState.WentUp:
                    if (_cachedWentUp == null) _cachedWentUp = new List<AnyKeyCode>();
                    _cachedWentUp.Clear();
                    return _cachedWentUp;
                case ButtonState.IsDown:
                    if (_cachedIsDown == null) _cachedIsDown = new List<AnyKeyCode>();
                    _cachedIsDown.Clear();
                    return _cachedIsDown;
                default:
                    if (_cachedIsUp == null) _cachedIsUp = new List<AnyKeyCode>();
                    _cachedIsUp.Clear();
                    return _cachedIsUp;
            }
        }

        private static List<AnyKeyCode> GetStateKeys(ButtonState state)
        {
            RDInput.GetMain(state);
            var list = GetPooled(state);
            foreach (RDInputType input in RDInput.inputs)
            {
                if (!input.isActive) continue;
                switch (state)
                {
                    case ButtonState.WentDown: list.AddRange(input.pressCount.keys); break;
                    case ButtonState.IsDown: list.AddRange(input.heldCount.keys); break;
                    case ButtonState.WentUp: list.AddRange(input.releaseCount.keys); break;
                    case ButtonState.IsUp: list.AddRange(input.isReleaseCount.keys); break;
                }
            }
            return list;
        }

        [IriPatch(Path = "optimizer/rdInput",
            Pre = typeof(OptimizerSettings),
            Condition = "optimizeRDInputAllocations")]
        [HarmonyPatch(typeof(RDInput), "GetStateKeys")]
        public static class GetStateKeysPatch
        {
            [HarmonyPrefix]
            public static bool Prefix(ButtonState state, ref List<AnyKeyCode> __result)
            {
                if (!Main.Settings.optimizer.optimizeRDInputAllocations)
                {
                    return true;
                }

                __result = GetStateKeys(state);
                return false;
            }
        }
    }
}
