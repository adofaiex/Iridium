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
