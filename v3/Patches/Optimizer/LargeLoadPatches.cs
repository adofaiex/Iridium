using System;
using System.Reflection;
using HarmonyLib;

namespace Iridium.Patches.Optimizer
{
	/// <summary>Refresh entry point for the loading/largeLevel patches, which live outside the optimizer subtree and need their own enumeration.</summary>
	public static class LargeLoadPatches
	{
		private static readonly Type[] Parents =
		{
			typeof(FfxGetComponentsFastPatch),
			typeof(LoadProfilerPatch),
			typeof(LazyTopGlowPatch),
			typeof(LazyFloorNumberPatch),
			typeof(DecodeAllocationPatch),
			typeof(ChunkedFloorSpawnPatch),
			typeof(UndoMemoryCapPatch),
		};

		public static void Update()
		{
			try
			{
				foreach (var parent in Parents)
				{
					foreach (var type in parent.GetNestedTypes(
						         BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
					{
						if (type.GetCustomAttributes(typeof(HarmonyPatch), true).Length > 0)
							AsyncPatchManager.UpdatePatchByTypeAsync(type);
					}
				}
			}
			catch (Exception e)
			{
				Main.Logger?.Log($"[LargeLoad] Update failed: {e}");
			}
		}
	}
}
