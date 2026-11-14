using HarmonyLib;
using Iridium.Config;
using System;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace Iridium.Patches.Optimizer
{
	/// <summary>Skips scrFloor.ResetToLevelStart on death reset only when every write it would perform is already in effect, including an unchanged SetTrackStyle input snapshot.</summary>
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
