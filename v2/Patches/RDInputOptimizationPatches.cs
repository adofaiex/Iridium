using HarmonyLib;
using System;
using System.Collections.Generic;

namespace Iridium.Patches
{
    /// <summary>
/// RDInput.GetStateKeys allocated a fresh List per call. Each ButtonState now reuses a
/// [ThreadStatic] list; callers consume it within the same call, so nothing holds it
/// across frames.
/// </summary>
    public static class RDInputOptimizationPatches
    {
        [ThreadStatic] private static List<AnyKeyCode>? _cachedWentDown;
        [ThreadStatic] private static List<AnyKeyCode>? _cachedWentUp;
        [ThreadStatic] private static List<AnyKeyCode>? _cachedIsDown;
        [ThreadStatic] private static List<AnyKeyCode>? _cachedIsUp;

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

        [HarmonyPatch(typeof(RDInput), "GetStateKeys")]
        public static class GetStateKeysPatch
        {
            [HarmonyPrefix]
            public static bool Prefix(ButtonState state, ref List<AnyKeyCode> __result)
            {
                // Opt-in — when disabled, fall through to the original.
                if (Main.Settings?.optimizer.optimizeRDInputAllocations != true)
                {
                    return true;
                }

                __result = GetStateKeys(state);
                return false;
            }
        }
    }
}
