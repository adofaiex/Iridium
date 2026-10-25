using System;
using System.Collections.Generic;
using HarmonyLib;
using ADOFAI;

namespace Iridium.Patches
{
    /// <summary>
/// Empties settings.requiredMods to [] before the game decodes the level, so every
/// reader of that list sees a normal chart. The original is kept per LevelData and
/// restored on Encode, so it survives a save.
/// </summary>
    public static class RequiredModsClearPatches
    {
        private static readonly Dictionary<LevelData, object> OriginalRequiredMods = new();
        private static readonly List<string> CurrentRequiredMods = new();

        private static object? ClearRequiredMods(Dictionary<string, object>? settings)
        {
            if (settings == null || !settings.TryGetValue("requiredMods", out var raw)) return null;

            CurrentRequiredMods.Clear();
            if (raw is object[] arr)
            {
                foreach (var item in arr)
                    if (item is string s && !string.IsNullOrEmpty(s)) CurrentRequiredMods.Add(s);
            }
            else if (raw is List<object> list)
            {
                foreach (var item in list)
                    if (item is string s && !string.IsNullOrEmpty(s)) CurrentRequiredMods.Add(s);
            }

            settings["requiredMods"] = new List<object>();
            return raw;
        }

        /// <summary>Editor/play decode path; stash the original for the encode restore.</summary>
        [HarmonyPatch(typeof(LevelData), nameof(LevelData.Decode))]
        public static class LevelDataClearPatch
        {
            [HarmonyPrefix]
            public static void Prefix(LevelData __instance, Dictionary<string, object> dict)
            {
                if (!Main.Settings.compatibility.ignoreRequiredMods) return;
                if (dict != null && dict.TryGetValue("settings", out var s) && s is Dictionary<string, object> settings)
                    OriginalRequiredMods[__instance] = ClearRequiredMods(settings);
            }
        }

        /// <summary>Level select decode path.</summary>
        [HarmonyPatch(typeof(LevelDataCLS), nameof(LevelDataCLS.Decode))]
        public static class LevelDataCLSClearPatch
        {
            [HarmonyPrefix]
            public static void Prefix(Dictionary<string, object> rootDict)
            {
                if (!Main.Settings.compatibility.ignoreRequiredMods) return;
                if (rootDict != null && rootDict.TryGetValue("settings", out var s) && s is Dictionary<string, object> settings)
                    ClearRequiredMods(settings);
            }
        }

        /// <summary>Restore the original requiredMods before Encode so saving keeps it.</summary>
        [HarmonyPatch(typeof(LevelData), nameof(LevelData.Encode))]
        public static class EncodeRestorePatch
        {
            [HarmonyPrefix]
            public static void Prefix(LevelData __instance)
            {
                if (!Main.Settings.compatibility.ignoreRequiredMods) return;
                if (__instance == null) return;
                if (!OriginalRequiredMods.TryGetValue(__instance, out var raw) || raw == null) return;

                __instance.levelSettings["requiredMods"] = raw;
                OriginalRequiredMods.Remove(__instance);
            }
        }

        /// <summary>After load, notify the player of the ignored missing mods.</summary>
        [HarmonyPatch(typeof(scnGame), "LoadLevel")]
        public static class LevelLoadNotifyPatch
        {
            [HarmonyPostfix]
            public static void Postfix(LoadResult status)
            {
                try
                {
                    if (!Main.Settings.compatibility.ignoreRequiredMods) return;
                    if (status != LoadResult.Successful) return;
                    if (CurrentRequiredMods.Count == 0) return;

                    var mods = CurrentRequiredMods.ToArray();
                    CurrentRequiredMods.Clear();

                    if (mods.Length == 1)
                        UI.VRAMNotificationUI.Show(Localization.Get("MissingModsNoticeSingle", mods[0]));
                    else
                        UI.VRAMNotificationUI.Show(Localization.Get("MissingModsNotice", mods[0], mods.Length - 1));
                }
                catch (Exception e)
                {
                    Main.Logger?.Log($"[RequiredMods] notify error: {e}");
                }
            }
        }
    }
}
