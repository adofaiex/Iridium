using ADOFAI;
using HarmonyLib;
using System;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace Iridium.Patches
{
	/// <summary>
/// Skips ResetToLevelStart on death-restart, but only when every write it would
/// make is an idempotent repeat: the transform/opacity/taps values already match
/// and the SetTrackStyle inputs are unchanged since the last full reset.
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

		/// <summary>
		/// RecolorTrack 会在运行中把样式改成 initialTrackStyle 以外的值（material、_length/_width），
		/// 快照里只有"输入"，看不到这个已发生的改写；不让快照失效的话，死亡复位会跳过这块砖，
		/// 样式就停留在上一轮的状态。
		/// </summary>
		[HarmonyPatch(typeof(scrFloor), nameof(scrFloor.SetTrackStyle))]
		public static class StyleInvalidatePatch
		{
			[HarmonyPostfix]
			public static void Postfix(scrFloor __instance, object[] __args)
			{
				if (__args == null || __args.Length == 0 || !(__args[0] is TrackStyle style)) return;
				if (style == __instance.initialTrackStyle) return;
				if (_snapshots.TryGetValue(__instance, out var snap)) snap.Valid = false;
			}
		}

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
