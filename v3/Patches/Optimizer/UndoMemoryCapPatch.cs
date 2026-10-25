using System;
using ADOFAI;
using HarmonyLib;
using Iridium.Config;

namespace Iridium.Patches.Optimizer
{
	/// <summary>Scales the editor undo/redo cap down by event count: 3 snapshots at 300k events, 5 at 100k, 15 at 30k, vanilla 100 below that.</summary>
	public static class UndoMemoryCapPatch
	{
		private static bool _logged;

		[IriPatch(Path = "loading/largeLevel", Pre = typeof(OptimizerSettings), Condition = "optimizeLargeLevelLoading")]
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
