using ADOFAI;
using HarmonyLib;
using System;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace Iridium.Patches
{
	/// <summary>
	/// 死亡重开时的砖块复位跳过（自 v3 移植，2.9.8 适配）。
	///
	/// 每次死亡 ResetScene 对全部砖块调用 ResetToLevelStart：写 transform、
	/// opacity、tapsSoFar=0 并 SetTrackStyle(initialTrackStyle)（多次材质
	/// 写入 + mesh 置脏）。15 万砖 ≈ 每次重开百万级原生调用。
	///
	/// 实现原则：只在所有写入都是幂等重复时跳过。两层判定：
	/// 1. 幂等写入检查（position/euler/tweenRot/localScale/opacity/taps）；
	/// 2. 样式输入快照（Postfix 在完整复位后记录 SetTrackStyle 的全部输入，
	///    输入一致才允许跳过）。
	/// freeroam 且未生成（2.9.8 会递归复位子砖）直接放行。
	/// 2.9.8 与 3.x 的 ResetToLevelStart 结构一致（已核对反编译源）。
	/// </summary>
	[HarmonyPatch(typeof(scrFloor), nameof(scrFloor.ResetToLevelStart))]
	public static class DeathResetFloorPatch
	{
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
				if (__instance.freeroam && !__instance.freeroamGenerated)
					return true;

				Transform t = __instance.transform;
				Vector3 targetEuler = __instance.startRot.WithZ(__instance.startRot.z + __instance.rotationOffset);

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

				return false;
			}
			catch (Exception)
			{
				return true;
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
			}
		}
	}
}
