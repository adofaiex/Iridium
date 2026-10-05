using ADOFAI;
using HarmonyLib;
using Iridium.Config;
using System;
using System.Reflection;

namespace Iridium.Patches.Optimizer
{
	/// <summary>
	/// 编辑器 SaveState 冗余全谱深拷贝消除。
	///
	/// 原版 scnEditor.SaveState 无条件执行 levelData.Copy()（全谱深拷贝，
	/// 60 万事件 ≈ 每事件 2 个字典拷贝 ≈ 120 万次分配），随后把拷贝传给
	/// LevelState 构造函数——而构造函数在 dataHasChanged == false 时
	/// 直接丢弃这份拷贝（this.data = dataHasChanged ? data : null）。
	///
	/// 编辑器里大量高频交互路径都以 dataHasChanged:false 调用 SaveState：
	/// 选中砖块（SelectFloor/MultiSelectFloors）、选中/取消选中装饰物、
	/// 打开属性面板（SaveStateScope(..., dataHasChanged: false)）。
	/// 即每次点击都在做一次全谱深拷贝后当场丢弃——大谱面"点一下卡一下"的
	/// 主要来源之一。现有 UndoMemoryCapPatch 只压 undo 条数，未消除此开销。
	///
	/// 实现：ThreadStatic 抑制标志。
	/// - SaveState 的 Prefix 在 dataHasChanged == false 时置位抑制标志；
	/// - LevelData.Copy 的 Prefix 在标志置位时返回 null 并跳过原方法
	///   （返回 null 与原版语义一致：构造函数该分支本来就存 null）；
	/// - Finalizer 保证提前 return / 抛异常时标志复位。
	/// 不使用计数器：Harmony 补丁方法体内不会重入 SaveState（构造 LevelState
	/// 期间不会再触发 SaveState），bool 足够；Finalizer 兜底复位覆盖所有路径。
	///
	/// 其他代码路径（真正需要拷贝的 undo/redo、保存等）不受影响。
	/// 3.3.0（scnEditor.cs:6918 / ADOFAI/LevelData.cs:849）与 3.4.0 签名一致。
	/// </summary>
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
