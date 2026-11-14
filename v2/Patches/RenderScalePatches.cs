using System;
using HarmonyLib;
using UnityEngine;

namespace Iridium.Patches
{
	/// <summary>
/// Reuses the game's own RT camera pipeline and only shrinks camRT to Screen * scale,
/// so the world renders at lower resolution and the quad upscales it. Screen-pixel
/// dependent APIs (pixelWidth, ScreenToWorldPoint) are corrected in CameraCoordPatch.
/// </summary>
	public static class RenderScaleRuntime
	{
		public const int MinPercent = 30;
		public const int MaxPercent = 100;

		private static readonly AccessTools.FieldRef<scrCamera, RenderTexture> CamRTRef =
			AccessTools.FieldRefAccess<scrCamera, RenderTexture>("camRT");
		private static readonly AccessTools.FieldRef<scrCamera, bool> UseRTCamRef =
			AccessTools.FieldRefAccess<scrCamera, bool>("useRTCam");
		private static readonly AccessTools.FieldRef<scrCamera, MeshRenderer> CamQuadMeshRef =
			AccessTools.FieldRefAccess<scrCamera, MeshRenderer>("camQuadMesh");

		/// <summary>最近一次创建的缩放 RT；坐标补丁据此识别「缩放相机」。</summary>
		internal static RenderTexture CurrentRT = null!;

		/// <summary>功能已开启（设置层面：总开关 + 自身开关）。</summary>
		public static bool Configured
		{
			get
			{
				var opt = Main.Settings?.optimizer;
				return opt != null && opt.enableOptimizer && opt.enableRenderScale;
			}
		}

		public static float Scale
		{
			get
			{
				int p = Mathf.Clamp(Main.Settings?.optimizer?.renderScalePercent ?? MaxPercent, MinPercent, MaxPercent);
				return p / 100f;
			}
		}

		/// <summary>该相机是否正在渲染到我们的缩放 RT（需要坐标修正）。</summary>
		public static bool IsScaledCamera(Camera cam)
		{
			if (cam == null || !Configured) return false;
			var rt = CurrentRT;
			if (rt == null || Screen.width <= 0) return false;
			return ReferenceEquals(cam.targetTexture, rt) && rt.width < Screen.width;
		}

		private static bool _errorLogged;

		/// <summary>仅在游戏已启用 RT 管线（useRTCam）时介入；前缀异常会跳过整个 Update，故整体兜底。</summary>
		public static void EnsureScaledRT(scrCamera cam)
		{
			if (cam == null || !Configured) return;
			try
			{
				EnsureScaledRTInternal(cam);
			}
			catch (Exception e)
			{
				if (!_errorLogged)
				{
					_errorLogged = true;
					Main.Logger?.Error($"[RenderScale] unexpected error, falling back to vanilla frames: {e}");
				}
			}
		}

		private static void EnsureScaledRTInternal(scrCamera cam)
		{
			if (!UseRTCamRef(cam)) return;

			int screenW = Screen.width, screenH = Screen.height;
			if (screenW <= 0 || screenH <= 0) return;

			float scale = Scale;
			int w = Mathf.Max(16, Mathf.RoundToInt(screenW * scale));
			int h = Mathf.Max(16, Mathf.RoundToInt(screenH * scale));

			var oldRT = CamRTRef(cam);
			bool sized = oldRT != null && oldRT.width == w && oldRT.height == h;
			// sized 但接线不完整（例如上一次尝试中途异常）时，仍走下面的幂等重接流程。
			bool wired = sized && cam.camobj != null && ReferenceEquals(cam.camobj.targetTexture, oldRT);
			if (wired)
			{
				CurrentRT = oldRT!;
				return;
			}

			var target = oldRT;
			if (!sized)
			{
				// 与原版一致：默认格式、24 位深度；额外指定双线性放大
				target = new RenderTexture(w, h, 24)
				{
					name = "IridiumRenderScaleRT",
					filterMode = FilterMode.Bilinear,
					anisoLevel = 0,
					useMipMap = false,
					autoGenerateMips = false,
				};
				target.Create();
				CamRTRef(cam) = target;
			}

			// 重新指定三台世界相机的 targetTexture，并确保 Overlaycam / quad 激活
			cam.SetupRTCam(true);

			// 自定义帧率模式下 quad 纹理由该功能自行管理，这里不接管
			if (!cam.enableCustomFPS)
			{
				var quadMesh = CamQuadMeshRef(cam);
				if (quadMesh != null) quadMesh.material.mainTexture = target;
			}

			// 复刻原版对 quad 的尺寸设置，保证铺满 Overlaycam 视野
			if (cam.quad != null && cam.Overlaycam != null)
			{
				float num = cam.Overlaycam.orthographicSize * 2f;
				float x = num * (float)screenW / (float)screenH;
				cam.quad.transform.localScale = new Vector3(x, num, 1f);
			}

			CurrentRT = target!;

			if (!sized && oldRT != null)
			{
				try { oldRT.Release(); UnityEngine.Object.Destroy(oldRT); }
				catch (Exception) { }
			}
			else if (oldRT == null)
			{
				Main.Logger?.Log($"[RenderScale] RT {w}x{h} (scale {scale:P0})");
			}
		}
	}

	/// <summary>嵌套补丁容器，条件为 enableOptimizer && enableRenderScale。</summary>
	public static class RenderScalePatches
	{
		[HarmonyPatch(typeof(scrCamera))]
		public static class ScrCameraRTPatch
		{
			/// <summary>在 Update 最前面维护缩放 RT（必须先于原版的重建判定）。</summary>
			[HarmonyPatch("Update"), HarmonyPrefix]
			public static void UpdatePrefix(scrCamera __instance)
			{
				RenderScaleRuntime.EnsureScaledRT(__instance);
			}

			/// <summary>阻止原版按 Screen 尺寸重建 camRT——缩放 RT 由我们管理。</summary>
			[HarmonyPatch("get_camRTNeedsRecreation"), HarmonyPrefix]
			public static bool NeedsRecreationPrefix(ref bool __result)
			{
				// 关闭时交还原版：它会发现 camRT 尺寸不等于 Screen 并重建为全分辨率，自动还原
				if (!RenderScaleRuntime.Configured) return true;
				__result = false;
				return false;
			}
		}

		/// <summary>缩放 RT 下 pixelWidth / ScreenToWorldPoint 等仍需表现得像全屏 RT，否则输入与拾取会整体偏移。</summary>
		[HarmonyPatch(typeof(Camera))]
		public static class CameraCoordPatch
		{
			[HarmonyPatch("get_pixelWidth"), HarmonyPrefix]
			public static bool PixelWidthPrefix(Camera __instance, ref int __result)
			{
				if (!RenderScaleRuntime.IsScaledCamera(__instance)) return true;
				__result = Screen.width;
				return false;
			}

			[HarmonyPatch("get_pixelHeight"), HarmonyPrefix]
			public static bool PixelHeightPrefix(Camera __instance, ref int __result)
			{
				if (!RenderScaleRuntime.IsScaledCamera(__instance)) return true;
				__result = Screen.height;
				return false;
			}

			[HarmonyPatch("get_pixelRect"), HarmonyPrefix]
			public static bool PixelRectPrefix(Camera __instance, ref Rect __result)
			{
				if (!RenderScaleRuntime.IsScaledCamera(__instance)) return true;
				__result = new Rect(0f, 0f, Screen.width, Screen.height);
				return false;
			}

			[HarmonyPatch("ScreenToWorldPoint", new Type[] { typeof(Vector3) }), HarmonyPrefix]
			public static bool ScreenToWorldPointPrefix(Camera __instance, Vector3 position, ref Vector3 __result)
			{
				if (!RenderScaleRuntime.IsScaledCamera(__instance) || Screen.width <= 0 || Screen.height <= 0) return true;
				var viewport = new Vector3(position.x / Screen.width, position.y / Screen.height, position.z);
				__result = __instance.ViewportToWorldPoint(viewport);
				return false;
			}

			[HarmonyPatch("WorldToScreenPoint", new Type[] { typeof(Vector3) }), HarmonyPrefix]
			public static bool WorldToScreenPointPrefix(Camera __instance, Vector3 position, ref Vector3 __result)
			{
				if (!RenderScaleRuntime.IsScaledCamera(__instance) || Screen.width <= 0 || Screen.height <= 0) return true;
				var viewport = __instance.WorldToViewportPoint(position);
				__result = new Vector3(viewport.x * Screen.width, viewport.y * Screen.height, viewport.z);
				return false;
			}

			[HarmonyPatch("ScreenToViewportPoint", new Type[] { typeof(Vector3) }), HarmonyPrefix]
			public static bool ScreenToViewportPointPrefix(Camera __instance, Vector3 position, ref Vector3 __result)
			{
				if (!RenderScaleRuntime.IsScaledCamera(__instance) || Screen.width <= 0 || Screen.height <= 0) return true;
				__result = new Vector3(position.x / Screen.width, position.y / Screen.height, position.z);
				return false;
			}
		}
	}
}
