using Iridium.Config;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using ADOFAI;
using HarmonyLib;
using UnityEngine;

namespace Iridium.Patches.UI
{
	[HarmonyPatch(typeof(FloorMesh), "SmallestAngleBetweenTwoAngles")]
	[IriPatch(Path = "ui/circleArc", Pre = typeof(UISettings), Condition = "enableCircleArc")]
	public static class CircleArcPatch
	{
		private static readonly MethodInfo ApplyOverride = AccessTools.Method(typeof(CircleArcPatch), nameof(ApplyCircleArcOverride));

		[HarmonyTranspiler]
		public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
		{
			var codes = instructions.ToList();
			var resultLocal = generator.DeclareLocal(typeof(float));
			for (int i = 0; i < codes.Count; i++)
			{
				if (codes[i].opcode != OpCodes.Ret) continue;

				codes.Insert(i, new CodeInstruction(OpCodes.Stloc, resultLocal));
				codes.Insert(i + 1, new CodeInstruction(OpCodes.Ldloc, resultLocal));
				codes.Insert(i + 2, new CodeInstruction(OpCodes.Ldarg_1));
				codes.Insert(i + 3, new CodeInstruction(OpCodes.Ldarg_2));
				codes.Insert(i + 4, new CodeInstruction(OpCodes.Call, ApplyOverride));
				break;
			}
			return codes;
		}

		private static float ApplyCircleArcOverride(float original, float angleA, float angleB)
		{
			float minDiff = Mathf.Abs(Mathf.DeltaAngle(angleA * Mathf.Rad2Deg, angleB * Mathf.Rad2Deg)) * Mathf.Deg2Rad;
			float minDiffDeg = minDiff * Mathf.Rad2Deg;
			// num6 drives both the arc centre and the radius, so inflating it is what turns the corner into the rounded OUTER corner; vanilla only does that in the 89.9-105.1 band. The interval is half-open [min, max) and max is capped by MaxAppliedAngleDeg so 180 stays unreachable.
			var ui = Main.Settings?.ui;
			float min = Mathf.Clamp(ui != null ? ui.circleArcMinAngle : 90f, 0f, 180f);
			float max = Mathf.Clamp(ui != null ? ui.circleArcMaxAngle : 105f, min, 180f);
			if (minDiffDeg >= min && minDiffDeg < Mathf.Min(max, MaxAppliedAngleDeg))
				return minDiff * 5f / 180f * Mathf.PI;
			return original;
		}

		// Ceiling just under 180: vanilla's piAngle epsilon is 1e-4 rad, so this keeps a float round trip from reaching a reversal while staying visually identical to 180.
		public const float MaxAppliedAngleDeg = 179.99f;
	}

	// Widens GetPositions' 120-degree corner-arc gate to PI; the inputs are normalized up front so angleDifference is already <= PI.
	// num6 is deliberately untouched: flooring it only adds an invisible inner fillet and would hollow out straight piAngle tiles.
	[HarmonyPatch(typeof(FloorMesh), "GetPositions")]
	[IriPatch(Path = "ui/circleArc", Pre = typeof(UISettings), Condition = "enableCircleArc", RequireMethod = "FloorMesh.GetPositions")]
	public static class AllAngleArcCornersPatch
	{
		[HarmonyTranspiler]
		public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
		{
			var codes = instructions.ToList();

			// IL anchors, verified against Assembly-CSharp 3.3.0: (A) "ldarg.0, ldarg.0, ldarg.2, ldarg.1, sub, call ModAngle360, stfld angleDifference" — angleDifference is a field, and the earlier ModAngle360 calls use starg, so call+stfld is unique; (C) the "ldfld angleDifference, ldc.r4 2.0942953, bge.un" 120-degree gate, where bge.un jumps past the arc body.

			// Anchor A.
			int anchorIdx = -1;
			for (int i = 0; i < codes.Count - 1; i++)
			{
				if (!IsCallTo(codes[i], "ModAngle360")) continue;
				if (codes[i + 1].opcode == OpCodes.Stfld &&
					codes[i + 1].operand is FieldInfo stored && stored.Name == "angleDifference")
				{
					anchorIdx = i;
					break;
				}
			}

			// Anchor C: the first "ldfld angleDifference; ldc.r4 <not PI>" past the anchor is the gate constant; the later PI ternary is excluded by the check.
			int gateIdx = -1;
			for (int i = anchorIdx + 2; anchorIdx >= 0 && i < codes.Count - 2; i++)
			{
				if (codes[i].opcode != OpCodes.Ldfld ||
					codes[i].operand is not FieldInfo loaded || loaded.Name != "angleDifference")
					continue;
				if (codes[i + 1].opcode != OpCodes.Ldc_R4 || codes[i + 1].operand is not float threshold)
					continue;
				if (Mathf.Approximately(threshold, Mathf.PI)) continue;
				gateIdx = i + 1;
				break;
			}

			if (anchorIdx < 0 || gateIdx < 0)
			{
				return instructions;
			}

			var result = new List<CodeInstruction>(codes.Count);
			for (int i = 0; i < codes.Count; i++)
			{
				if (i == gateIdx)
				{
					var widened = new CodeInstruction(codes[i]) { operand = Mathf.PI };
					result.Add(widened);
				}
				else
				{
					result.Add(codes[i]);
				}
			}
			return result;
		}

		// Match by name: Harmony resolves operands through the runtime module, so ReferenceEquals against an AccessTools MethodInfo is not reliable.
		private static bool IsCallTo(CodeInstruction instruction, string methodName)
		{
			if (instruction.opcode != OpCodes.Call && instruction.opcode != OpCodes.Callvirt) return false;
			return instruction.operand is MethodInfo m &&
				   m.DeclaringType == typeof(FloorMesh) &&
				   m.Name == methodName;
		}
	}
}
