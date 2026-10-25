using HarmonyLib;
using Iridium.Config;
using System;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace Iridium.Patches.Optimizer
{
	/// <summary>Skips scrHoldRenderer.UpdateMesh/UpdateColor while the geometry inputs are unchanged; _Completion and _StartAlpha still update every frame.</summary>
	[IriPatch(Path = "optimizer/hold", Pre = typeof(OptimizerSettings), Condition = "optimizeHoldRenderer")]
	[HarmonyPatch(typeof(scrHoldRenderer), "Update")]
	public static class HoldRendererDirtyCheckPatch
	{
		private sealed class Snapshot
		{
			public bool Valid;
			public Vector3 HeadPos;
			public Vector3 TailPos;
			public float HeadScaleX;
			public float TailScaleX;
			public float TileSize;
			public float StartRadiusScale;
			public scrFloor TailFloor;
			public float ExtendAnim;
			public double ExitAngle;
		}

		private static readonly ConditionalWeakTable<scrHoldRenderer, Snapshot> _snapshots = new();

		// 3.3.0/3.4.0 中这些成员是 private：缓存 FieldRef 访问器
		private static readonly AccessTools.FieldRef<scrHoldRenderer, scrFloor> _getHead =
			AccessTools.FieldRefAccess<scrHoldRenderer, scrFloor>("headFloor");
		private static readonly AccessTools.FieldRef<scrHoldRenderer, scrFloor> _getTail =
			AccessTools.FieldRefAccess<scrHoldRenderer, scrFloor>("tailFloor");
		private static readonly AccessTools.FieldRef<scrHoldRenderer, float> _getExtendAlp =
			AccessTools.FieldRefAccess<scrHoldRenderer, float>("extendAlp");

		private static scrFloor GetHead(scrHoldRenderer hr) => _getHead?.Invoke(hr);
		private static scrFloor GetTail(scrHoldRenderer hr) => _getTail?.Invoke(hr);
		private static float GetExtendAlp(scrHoldRenderer hr) => _getExtendAlp != null ? _getExtendAlp(hr) : 1f;

		private static bool SnapshotMatches(scrHoldRenderer hr, Snapshot s)
		{
			var start = hr.startFloor;
			var tail = GetTail(hr);
			if (start == null || tail == null)
				return false;

			float tileSize = scrController.instance != null ? scrController.instance.tileSize : 0f;
			return s.HeadPos == start.transform.position
				&& s.TailPos == tail.transform.position
				&& s.HeadScaleX == start.floorRenderer.transform.localScale.x
				&& s.TailScaleX == tail.floorRenderer.transform.localScale.x
				&& s.TileSize == tileSize
				&& s.StartRadiusScale == start.radiusScale
				&& ReferenceEquals(s.TailFloor, tail)
				&& s.ExtendAnim == start.extendAnim
				&& s.ExitAngle == start.exitangle;
		}

		private static void UpdateSnapshot(scrHoldRenderer hr, Snapshot s)
		{
			var start = hr.startFloor;
			var tail = GetTail(hr);
			if (start == null || tail == null)
			{
				s.Valid = false;
				return;
			}
			s.HeadPos = start.transform.position;
			s.TailPos = tail.transform.position;
			s.HeadScaleX = start.floorRenderer.transform.localScale.x;
			s.TailScaleX = tail.floorRenderer.transform.localScale.x;
			s.TileSize = scrController.instance != null ? scrController.instance.tileSize : 0f;
			s.StartRadiusScale = start.radiusScale;
			s.TailFloor = tail;
			s.ExtendAnim = start.extendAnim;
			s.ExitAngle = start.exitangle;
			s.Valid = true;
		}

		[HarmonyPrefix]
		public static bool Prefix(scrHoldRenderer __instance)
		{
			try
			{
				// unfill/hit 动画期间颜色与圆圈每帧变化，放行原版
				if (__instance.unfilling || __instance.hit)
					return true;

				var start = __instance.startFloor;
				if (start == null || GetTail(__instance) == null || GetHead(__instance) == null)
					return true;

				var snap = _snapshots.GetOrCreateValue(__instance);
				if (snap.Valid && SnapshotMatches(__instance, snap))
				{
					// Geometry unchanged: only the per-frame fill/alpha writes, replicating UpdateColor's dynamic branch.
					if (__instance.m_meshRenderer != null)
					{
						Color holdColor = __instance.touchColor;
						if (holdColor != Color.clear)
							__instance.m_meshRenderer.material.SetColor("_FillColor", holdColor);
						__instance.m_meshRenderer.material.SetFloat("_Completion", start.holdCompletionEased);
						__instance.m_meshRenderer.material.SetFloat("_StartAlpha",
							start.opacity * start.holdOpacity * GetExtendAlp(__instance));
					}
					return false;
				}
				return true; // 几何有变化：放行原版完整更新
			}
			catch (Exception)
			{
				return true;
			}
		}

		[HarmonyPostfix]
		public static void Postfix(scrHoldRenderer __instance)
		{
			try
			{
				if (__instance.unfilling || __instance.hit)
					return; // 动画结束后下一帧再进入脏检查
				var snap = _snapshots.GetOrCreateValue(__instance);
				UpdateSnapshot(__instance, snap);
			}
			catch
			{
				// 快照失败只影响下一帧跳过判定
			}
		}
	}
}
