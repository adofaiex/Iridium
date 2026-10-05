using ADOFAI;
using HarmonyLib;
using System;
using UnityEngine;

namespace Iridium.Patches
{
	/// <summary>
	/// 编辑器 SaveState 冗余全谱深拷贝消除（自 v3 移植，2.9.8 适配）。
	///
	/// 原版 scnEditor.SaveState 无条件执行 levelData.Copy()（全谱深拷贝），
	/// 而 LevelState 构造函数在 dataHasChanged == false 时直接丢弃这份拷贝。
	/// 选中砖块/装饰物、打开面板等高频路径都以 dataHasChanged:false 调用，
	/// 即每次点击都在做一次全谱深拷贝后当场丢弃。
	///
	/// 实现：ThreadStatic 抑制标志——SaveState 的 Prefix 在 dataHasChanged
	/// 为 false 时置位；LevelData.Copy 的 Prefix 在置位时返回 null（与原版
	/// 语义一致：构造函数该分支本来就存 null）；Finalizer 保证复位。
	/// 2.9.8 与 3.x 的 SaveState/Copy/LevelState 结构一致（已核对反编译源）。
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
			public static Exception Finalizer()
			{
				_suppressCopy = false;
				return null; // 不吞异常，仅保证复位
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

				__result = null; // LevelState 在 dataHasChanged:false 时本来就存 null
				return false;
			}
		}
	}
}
