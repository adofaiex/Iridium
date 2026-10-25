using GDMiniJSON;
using HarmonyLib;
using Iridium.Native;

namespace Iridium.Patches
{
    /// <summary>
/// Routes GDMiniJSON parsing through iridium_core. The materialized graph matches
/// GDMiniJSON exactly (duplicate keys resolve to last, unparsable numbers become 0);
/// any failure falls back to the managed parser unchanged.
/// </summary>
    public static class JsonNativeParsePatch
    {
        [HarmonyPatch(typeof(Json), nameof(Json.Deserialize), new[] { typeof(string) })]
        public static class DeserializePatch
        {
            [HarmonyPrefix]
            public static bool Prefix(string json, ref object __result)
            {
                if (NativeJson.TryParse(json, null, out object? result))
                {
                    __result = result!;
                    return false;
                }
                return true;
            }
        }

        [HarmonyPatch(typeof(Json), nameof(Json.DeserializePartially), new[] { typeof(string), typeof(string) })]
        public static class DeserializePartiallyPatch
        {
            [HarmonyPrefix]
            public static bool Prefix(string json, string upToSection, ref object __result)
            {
                if (NativeJson.TryParse(json, upToSection, out object? result))
                {
                    __result = result!;
                    return false;
                }
                return true;
            }
        }
    }
}
