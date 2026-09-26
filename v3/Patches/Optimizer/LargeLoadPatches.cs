using System;
using System.Reflection;
using HarmonyLib;

namespace Iridium.Patches.Optimizer
{
	/// <summary>
	/// 「大谱面加载优化」相关补丁的刷新入口。
	/// 这些补丁挂在 loading/largeLevel 路径下，不属于 optimizer 子树，
	/// 因此不能走 UpdateOptimizerPatches，需要单独枚举刷新。
	/// </summary>
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
