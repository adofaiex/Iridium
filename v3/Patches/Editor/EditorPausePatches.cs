using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using HarmonyLib;
using ADOFAI;
using UnityEngine;

namespace Iridium.Patches.Editor
{
	[HarmonyPatch(typeof(scnEditor), "Update")]
	[IriPatch(Path = "editor/pause", AlwaysOn = true)]
	public static class EditorPausePatches
	{
		public static bool CheckPauseKey()
		{
			// Master switch: completely disable pause in editor auto-play
			if (!Main.Settings.compatibility.editorPauseAllowed)
				return false;

			// Custom key disabled: fall back to default Space behavior
			if (!Main.Settings.compatibility.editorPauseEnabled)
				return Input.GetKeyDown(KeyCode.Space);

			int mods = Main.Settings.compatibility.editorPauseModifiers;
			int key = Main.Settings.compatibility.editorPauseKey;

			if ((mods & 1) != 0 && !Input.GetKey(KeyCode.LeftControl) && !Input.GetKey(KeyCode.RightControl))
				return false;
			if ((mods & 2) != 0 && !Input.GetKey(KeyCode.LeftAlt) && !Input.GetKey(KeyCode.RightAlt))
				return false;
			if ((mods & 4) != 0 && !Input.GetKey(KeyCode.LeftShift) && !Input.GetKey(KeyCode.RightShift))
				return false;
			if ((mods & 8) != 0 && !Input.GetKey(KeyCode.LeftWindows) && !Input.GetKey(KeyCode.RightWindows))
				return false;

			return Input.GetKeyDown((KeyCode)key);
		}

		/// <summary>
		/// Same signature as the Input.GetKeyDown(KeyCode) call it replaces, so the transpiler only swaps the
		/// call target: the vanilla key stays on the stack as this argument (and is ignored), the stack effect is
		/// unchanged and branch labels on either instruction stay valid.
		/// </summary>
		public static bool CheckPauseKey(KeyCode vanillaKey) => CheckPauseKey();

		[HarmonyTranspiler]
		public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
		{
			var codes = instructions.ToList();
			var getKeyDown = AccessTools.Method(typeof(Input), "GetKeyDown", new[] { typeof(KeyCode) });
			var checkPauseKey = AccessTools.Method(typeof(EditorPausePatches), nameof(CheckPauseKey), new[] { typeof(KeyCode) });

			for (int i = 1; i < codes.Count; i++)
			{
				if (codes[i].Calls(getKeyDown))
				{
					var prev = codes[i - 1];
					if ((prev.opcode == OpCodes.Ldc_I4_S && prev.operand is sbyte s && s == 32)
						|| (prev.opcode == OpCodes.Ldc_I4 && prev.operand is int n && n == 32))
					{
						// Only the call target changes (KeyCode -> bool both before and after), so whichever of the
						// two instructions a branch lands on, the stack it finds is the one vanilla had.
						codes[i].operand = checkPauseKey;
						Main.Logger?.Log("[EditorPausePatch] Replaced pause key check");
						break;
					}
				}
			}

			return codes;
		}
	}
}
