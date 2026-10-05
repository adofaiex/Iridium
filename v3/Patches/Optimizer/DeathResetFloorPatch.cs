using HarmonyLib;
using Iridium.Config;
using System;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace Iridium.Patches.Optimizer
{
	/// <summary>
	/// 死亡重开时的砖块复位跳过。
	///
	/// 每次死亡 scrGame.ResetScene(false) 对**全部砖块**调用
	/// scrFloor.ResetToLevelStart()：写 transform.position/eulerAngles/
	/// localScale、opacity、tapsSoFar=0，并 SetTrackStyle(initialTrackStyle)
	/// （约 4 次 SetTexture + 3 次 SetFloat + SetColor + mesh 置脏）。
	/// 15 万砖 ≈ 每次重开百万级原生调用，表现为重开卡顿数百 ms~秒级。
	///
	/// 实现原则：**只在所有写入都是幂等重复时跳过**（跳过与否不改变任何
	/// 可见状态）。两层判定：
	/// 1. 幂等写入检查（覆盖 ResetToLevelStart 的全部直接写入目标）：
	///    position == startPos、eulerAngles/tweenRot == startRot+z 偏移、
	///    localScale == startScale（freeroamGenerated 时原版不写，跳过比较）、
	///    opacity == opacityVal、tapsSoFar == 0。
	/// 2. 样式输入快照：SetTrackStyle 的副作用链复杂（材质/贴图/边缘宽度由
	///    initialTrackStyle、customTexture、customTextureScale、lengthMult、
	///    widthMult、tapsNeeded、tileSize 等输入推导），无法廉价穷举。因此用
	///    Postfix 在每次**完整**复位后记录这些输入的快照（CWT 挂在砖块上），
	///    Prefix 仅当当前输入与快照完全一致时才允许跳过——样式相关输入被
	///    事件改过的砖块一律交还原版复位。
	///
	/// freeroam 且未生成（递归复位子砖）直接放行。
	/// 回退方式：关闭 optimizeDeathReset 开关。
	/// 3.3.0 与 3.4.0 的 ResetToLevelStart / SetTrackStyle 结构一致（已核对两版反编译源）。
	/// </summary>
	[IriPatch(Path = "optimizer/deathReset", Pre = typeof(OptimizerSettings), Condition = "optimizeDeathReset")]
	[HarmonyPatch(typeof(scrFloor), nameof(scrFloor.ResetToLevelStart))]
	public static class DeathResetFloorPatch
	{
		/// <summary>SetTrackStyle 输出所依赖的输入快照。</summary>
		private sealed class StyleSnapshot
		{
			public bool Valid;
			public TrackStyle InitialStyle;
			public Texture2D CustomTexture;
			public float CustomTextureScale;
			public float LengthMult;
			public float WidthMult;
			public int TapsNeeded;
			public Vector2 BaseFloorDimensions;
		}

		private static readonly ConditionalWeakTable<scrFloor, StyleSnapshot> _snapshots = new();

		[HarmonyPrefix]
		public static bool Prefix(scrFloor __instance)
		{
			try
			{
				// freeroam 未生成：原版会递归复位子砖并改写其状态，直接放行
				if (__instance.freeroam && !__instance.freeroamGenerated)
					return true;

				Transform t = __instance.transform;
				Vector3 targetEuler = __instance.startRot.WithZ(__instance.startRot.z + __instance.rotationOffset);

				// 幂等写入检查：任一目标值与期望不符则交还原版
				if (t.position != __instance.startPos)
					return true;
				if (t.eulerAngles != targetEuler)
					return true;
				if (__instance.tweenRot != targetEuler)
					return true;
				if (!__instance.freeroamGenerated && t.localScale != __instance.startScale)
					return true;
				if (__instance.opacity != __instance.opacityVal)
					return true;
				if (__instance.tapsSoFar != 0)
					return true;

				// 样式输入快照检查：无快照（首次）或输入有变化 → 交还原版
				if (!_snapshots.TryGetValue(__instance, out var snap) || !snap.Valid)
					return true;

				var controller = ADOBase.controller;
				Vector2 dims = controller != null ? controller.baseFloorDimensions : Vector2.zero;
				if (snap.InitialStyle != __instance.initialTrackStyle
					|| snap.CustomTexture != __instance.customTexture
					|| snap.CustomTextureScale != __instance.customTextureScale
					|| snap.LengthMult != __instance.lengthMult
					|| snap.WidthMult != __instance.widthMult
					|| snap.TapsNeeded != __instance.tapsNeeded
					|| snap.BaseFloorDimensions != dims)
					return true;

				return false; // 全部写入均为幂等重复，安全跳过
			}
			catch (Exception)
			{
				return true; // 任何异常回退原版
			}
		}

		[HarmonyPostfix]
		public static void Postfix(scrFloor __instance)
		{
			try
			{
				var snap = _snapshots.GetOrCreateValue(__instance);
				snap.InitialStyle = __instance.initialTrackStyle;
				snap.CustomTexture = __instance.customTexture;
				snap.CustomTextureScale = __instance.customTextureScale;
				snap.LengthMult = __instance.lengthMult;
				snap.WidthMult = __instance.widthMult;
				snap.TapsNeeded = __instance.tapsNeeded;
				var controller = ADOBase.controller;
				snap.BaseFloorDimensions = controller != null ? controller.baseFloorDimensions : Vector2.zero;
				snap.Valid = true;
			}
			catch
			{
				// 快照失败只影响下一次跳过判定，不影响游戏
			}
		}
	}
}
