using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using ADOFAI;
using HarmonyLib;
using Iridium.Config;
using UnityEngine;

namespace Iridium.Patches.Optimizer
{
	/// <summary>A floor with an empty plusEffects registry cannot own ffxPlusBase components, so the per-floor GetComponents scan is skipped.</summary>
	public static class FfxGetComponentsFastPatch
	{
		internal static ffxPlusBase[] GetFast(Component component)
		{
			if (component is scrFloor floor
				&& floor.plusEffects != null
				&& floor.plusEffects.Count == 0)
			{
				return Array.Empty<ffxPlusBase>();
			}
			return component.GetComponents<ffxPlusBase>();
		}

		[IriPatch(Path = "loading/largeLevel", Pre = typeof(OptimizerSettings), Condition = "optimizeLargeLevelLoading")]
		[HarmonyPatch]
		public static class ApplyEventsFfxScanPatch
		{
			private static IEnumerable<MethodBase> TargetMethods()
			{
				// 加载 / 编辑器应用事件的主路径
				var staticApply = AccessTools.Method(typeof(scnGame), "ApplyEventsToFloors",
					new[]
					{
						typeof(List<scrFloor>), typeof(LevelData), typeof(scrLevelMaker), typeof(List<LevelEvent>)
					});
				if (staticApply != null) yield return staticApply;

				// 编辑器里点播放时也会整表重置一次 triggered
				var finishLoading = AccessTools.Method(typeof(scnGame), "FinishCustomLevelLoading",
					new[] { typeof(int), typeof(bool) });
				if (finishLoading != null) yield return finishLoading;

				// 组件写法谱面 / 场景重置时的整表扫描
				var setFxPlus = AccessTools.Method(typeof(scnGame), "SetFxPlusFromComponents",
					new[] { typeof(List<scrFloor>), typeof(bool) });
				if (setFxPlus != null) yield return setFxPlus;

				var resetScene = AccessTools.Method(typeof(scnGame), "ResetScene", new[] { typeof(bool) });
				if (resetScene != null) yield return resetScene;
			}

			private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
			{
				var original = AccessTools
					.Method(typeof(Component), "GetComponents", Type.EmptyTypes)
					?.MakeGenericMethod(typeof(ffxPlusBase));
				var replacement = AccessTools.Method(typeof(FfxGetComponentsFastPatch), nameof(GetFast));

				if (original == null || replacement == null)
				{
					foreach (var instruction in instructions) yield return instruction;
					yield break;
				}

				foreach (var instruction in instructions)
				{
					if (instruction.Calls(original))
					{
						instruction.opcode = OpCodes.Call;
						instruction.operand = replacement;
					}
					yield return instruction;
				}
			}
		}
	}
}
