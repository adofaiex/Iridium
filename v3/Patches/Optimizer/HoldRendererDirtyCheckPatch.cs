using HarmonyLib;
using Iridium.Config;
using System;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace Iridium.Patches.Optimizer
{
	/// <summary>
	/// Hold 渲染器每帧脏检查。
	///
	/// 原版每个 hold（scrLevelMaker.DrawHolds 为每块 hold 砖创建一个常驻
	/// 激活的 GameObject）每帧无条件执行 Update → UpdateMesh + UpdateColor：
	/// 每次 5+ 次材质 SetFloat/SetColor/SetVector、mesh.bounds 写入、若干
	/// transform 读取。1 万 hold 的谱面每帧约 10 万次原生调用，是持续帧开销。
	/// 实际上几何输入（砖块位置/缩放/宽度/角度）极少变化，每帧必须更新的
	/// 只有 _Completion 与 alpha（填充进度/透明度）。
	///
	/// 实现：
	/// - Prefix 挂在 scrHoldRenderer.Update：unfilling/hit 动画期间放行
	///   （颜色/圆圈每帧变化）；否则用 CWT 快照比较几何输入
	///   （startFloor.position、head/tail 的 localScale.x、tileSize、
	///   startFloor.radiusScale、tailFloor 引用、extendAnim、exitangle），
	///   全部未变时只更新每帧必须的 _Completion/_StartAlpha 后返回 false；
	///   有变化时放行原版（随后 Postfix 刷新快照）。
	/// - 快照在 Update 后记录，保证"放行原版后下一帧即可进入跳过状态"。
	///
	/// 行为差异：静止 hold 的 mesh.bounds 不再每帧重写（内容相同，无视觉
	/// 影响）；_Completion/_StartAlpha 仍每帧更新，填充动画不受影响。
	/// 回退方式：关闭 optimizeHoldRenderer。
	/// 3.3.0 与 3.4.0 的 Update/UpdateMesh/UpdateColor 结构一致（已核对两版反编译源）。
	/// </summary>
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
					// 几何未变：只更新每帧必须的填充进度/透明度，跳过 UpdateMesh
					// 与 UpdateColor 的静态部分（等价复刻 UpdateColor 的动态分支）
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
