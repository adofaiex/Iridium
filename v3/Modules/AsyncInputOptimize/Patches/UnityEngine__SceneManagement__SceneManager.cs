using Iridium.Patches;
using Iridium.Config;
﻿using HarmonyLib;
using System.Collections.Generic;
using System.Reflection.Emit;
using Iridium;

namespace Iridium.Modules.AsyncInputOptimize.Patch
{
    [HarmonyPatch]
    [IriPatch(Path = "asyncInput", Pre = typeof(AsyncInputSettings), Condition = "enableAIO")]
    public static class UnityEngine__SceneManagement__SceneManager
    {
        [HarmonyPatch(typeof(UnityEngine.SceneManagement.SceneManager), "LoadSceneAsyncNameIndexInternal")]
        [HarmonyTranspiler]
        public static IEnumerable<CodeInstruction> Transpiler_LoadSceneAsyncNameIndexInternal(IEnumerable<CodeInstruction> instructions)
        {
            // Reset 放在方法开头：追加在末尾时它排在 ret 之后，永远执行不到
            yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(PatchMidLayer), nameof(PatchMidLayer.Reset)));
            foreach (CodeInstruction ci in instructions)
            {
                yield return ci;
            }
            yield break;
        }
    }
}
