using ADOFAI;
using HarmonyLib;
using Iridium.Config;
using System;
using System.Reflection;

namespace Iridium.Patches.Optimizer
{
	/// <summary>Suppresses the full level deep copy inside SaveState when dataHasChanged is false, since LevelState would discard it anyway.</summary>
	public static class EditorSaveStateCopyPatch
	{
		[ThreadStatic]
		private static bool _suppressCopy;

		[IriPatch(Path = "optimizer/editorPerf", Pre = typeof(OptimizerSettings), Condition = "optimizeEditorInteractions")]
		[HarmonyPatch(typeof(scnEditor), nameof(scnEditor.SaveState))]
		public static class SaveStateSuppressPatch
		{
			[HarmonyPrefix]
			public static bool Prefix(bool dataHasChanged)
			{
				// 只在"拷贝会被丢弃"的分支抑制 Copy；真正要存档的路径照常
				if (!dataHasChanged)
					_suppressCopy = true;
				return true;
			}

			[HarmonyFinalizer]
			public static Exception Finalizer()
			{
				_suppressCopy = false;
				return null; // 不吞异常，仅保证复位
			}
		}

		[IriPatch(Path = "optimizer/editorPerf", Pre = typeof(OptimizerSettings), Condition = "optimizeEditorInteractions")]
		[HarmonyPatch(typeof(LevelData), nameof(LevelData.Copy))]
		public static class LevelDataCopySuppressPatch
		{
			[HarmonyPrefix]
			public static bool Prefix(ref LevelData __result)
			{
				if (!_suppressCopy)
					return true;

				__result = null; // LevelState 在 dataHasChanged:false 时本来就存 null
				return false;
			}
		}
	}
}
