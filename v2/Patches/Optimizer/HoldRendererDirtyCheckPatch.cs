using ADOFAI;
using HarmonyLib;
using System;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace Iridium.Patches
{
	/// <summary>
	/// Hold 渲染器每帧脏检查（自 v3 移植，2.9.8 适配）。
	///
	/// 每个 hold 每帧无条件执行 Update → UpdateMesh + UpdateColor（约 10 次
	/// 材质 Set + bounds 重建），而几何输入极少变化。实现在 unfill/hit 动画
	/// 之外，用 CWT 快照比较几何输入，未变时只更新 _Completion/_StartAlpha。
	/// 2.9.8 的 headFloor/tailFloor/extendAlp 同为 private（FieldRef 访问），
	/// Update/UpdateMesh/UpdateColor 结构与 3.x 一致（已核对反编译源）。
	/// </summary>
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

		// 2.9.8 中这些成员是 private：缓存 FieldRef 访问器
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
				if (__instance.unfilling || __instance.hit)
					return true;

				var start = __instance.startFloor;
				if (start == null || GetTail(__instance) == null || GetHead(__instance) == null)
					return true;

				var snap = _snapshots.GetOrCreateValue(__instance);
				if (snap.Valid && SnapshotMatches(__instance, snap))
				{
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
				return true;
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
					return;
				var snap = _snapshots.GetOrCreateValue(__instance);
				UpdateSnapshot(__instance, snap);
			}
			catch
			{
			}
		}
	}
}
