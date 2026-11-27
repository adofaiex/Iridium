using ADOFAI;
using HarmonyLib;
using System;
using UnityEngine;

namespace Iridium.Patches
{
	/// <summary>
/// Drops the full-chart deep copy that scnEditor.SaveState performs on every
/// dataHasChanged:false call (selecting tiles, opening panels) and LevelState
/// then throws away. Null matches the original semantics for that branch.
/// </summary>
	public static class EditorSaveStateCopyPatch
	{
		[ThreadStatic]
		private static bool _suppressCopy;

		[HarmonyPatch(typeof(scnEditor), nameof(scnEditor.SaveState))]
		public static class SaveStateSuppressPatch
		{
			[HarmonyPrefix]
			public static bool Prefix(bool dataHasChanged)
			{
				if (!dataHasChanged)
					_suppressCopy = true;
				return true;
			}

			[HarmonyFinalizer]
			public static Exception Finalizer(Exception __exception)
			{
				_suppressCopy = false;
				return __exception; // 原样透传：返回 null 会把 SaveState 里的异常整个吞掉
			}
		}

		[HarmonyPatch(typeof(LevelData), nameof(LevelData.Copy))]
		public static class LevelDataCopySuppressPatch
		{
			[HarmonyPrefix]
			public static bool Prefix(ref LevelData __result)
			{
				if (!_suppressCopy)
					return true;

				__result = null; // dataHasChanged:false 时 LevelState 本来就存 null
				return false;
			}
		}
	}
}
