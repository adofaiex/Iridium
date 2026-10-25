using System;
using ADOFAI;
using HarmonyLib;

namespace Iridium.Patches
{
    /// <summary>
/// Caps the editor undo depth on large charts: each SaveState deep-copies the whole
/// level (~200MB at 300k events) and the original cap is 100. Thresholds:
/// >=300k events -> 3, >=100k -> 5, >=30k -> 15; smaller charts keep the original 100.
/// </summary>
    public static class UndoMemoryCapPatch
    {
        private static bool _logged;

        [HarmonyPatch(typeof(scnEditor), nameof(scnEditor.SaveState))]
        public static class SaveStateCapPatch
        {
            [HarmonyPostfix]
            public static void Postfix(scnEditor __instance)
            {
                try
                {
                    if (__instance == null) return;
                    var data = __instance.levelData;
                    int events = data != null && data.levelEvents != null ? data.levelEvents.Count : 0;
                    int cap = events >= 300000 ? 3
                        : events >= 100000 ? 5
                        : events >= 30000 ? 15
                        : 100;
                    if (cap >= 100) return;

                    var undo = __instance.undoStates;
                    if (undo != null)
                    {
                        while (undo.Count > cap) undo.RemoveAt(0);
                    }
                    var redo = __instance.redoStates;
                    if (redo != null)
                    {
                        while (redo.Count > cap) redo.RemoveAt(0);
                    }

                    if (!_logged)
                    {
                        _logged = true;
                        Main.Logger?.Log(
                            $"[LargeLoad] undo history capped at {cap} snapshots (events={events}, ~{events * 700L / 1048576}MB per snapshot)");
                    }
                }
                catch (Exception e)
                {
                    Main.Logger?.Log($"[LargeLoad] undo cap failed: {e.Message}");
                }
            }
        }
    }
}
