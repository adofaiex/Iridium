using System;
using ADOFAI;
using HarmonyLib;
using Iridium.Config;

namespace Iridium.Patches.Optimizer
{
	/// <summary>
	/// 大谱面内存保护：限制编辑器撤销历史深度。
	///
	/// 编辑器每个「会改数据」的编辑动作都会走 scnEditor.SaveState → LevelData.Copy()，
	/// 把整份关卡数据深拷贝一份（每个 LevelEvent 复制 data/disabled 两个字典；数值/字符串
	/// 共享引用）。几十万事件的谱面，一份快照就要 ~200MB+，而原版撤销历史上限是 100 份——
	/// 编辑十几次就能吃掉几个 GB，这在大物量谱面上是主要的内存爆炸源。
	///
	/// 这里按事件数下调上限（只影响大谱面，小谱面维持原版 100 份）：
	///   events ≥ 30 万 → 3 份；≥ 10 万 → 5 份；≥ 3 万 → 15 份。
	/// 换取的是编辑大谱面时内存不再线性膨胀。
	/// 挂 optimizer.optimizeLargeLevelLoading（与总开关无关）。
	/// </summary>
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
