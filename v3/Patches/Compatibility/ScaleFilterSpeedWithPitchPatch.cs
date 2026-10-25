using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using ADOFAI;
using HarmonyLib;
using UnityEngine;
using Iridium.Config;

namespace Iridium.Patches.Compatibility
{
	/// <summary>
	/// 缩放 CameraFilterPack 着色器内部动画速度以适应音高变化：所有 OnRenderImage 里的
	/// TimeX += Time.deltaTime 改为 TimeX += Time.deltaTime * pitch。
	/// 只调 tween duration（AdjustDurationForHardbake）是不够的 —— 着色器内的 TimeX
	/// 仍用未缩放的 deltaTime，降 pitch 录制后期加速后滤镜动画会比原速快。
	/// </summary>
	[HarmonyPatch]
	[IriPatch(Path = "compatibility/scaleFilter", Pre = typeof(CompatibilitySettings), Condition = "scaleFilterSpeedWithPitch")]
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
