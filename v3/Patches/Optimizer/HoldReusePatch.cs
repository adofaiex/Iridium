using HarmonyLib;
using Iridium.Config;
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace Iridium.Patches.Optimizer
{
	/// <summary>On restart, DrawHolds(true) reuses the existing hold GameObjects, meshes and materials instead of destroying and recreating them, provided the hold count still matches.</summary>
	[IriPatch(Path = "optimizer/deathReset", Pre = typeof(OptimizerSettings), Condition = "optimizeDeathReset")]
	[HarmonyPatch(typeof(scrLevelMaker), "DrawHolds", new[] { typeof(bool) })]
	public static class HoldReusePatch
	{
		private static readonly AccessTools.FieldRef<scrLevelMaker, List<GameObject>> _getHoldGOs =
			AccessTools.FieldRefAccess<scrLevelMaker, List<GameObject>>("holdGOs");

		[HarmonyPrefix]
		public static bool Prefix(scrLevelMaker __instance, bool unfillHolds)
		{
			try
			{
				if (!Application.isPlaying || ADOBase.isLevelEditor)
					return true;
				if (!unfillHolds)
					return true;

				List<GameObject> holdGOs = _getHoldGOs?.Invoke(__instance);
				if (holdGOs == null || holdGOs.Count == 0)
					return true;

				var floors = __instance.listFloors;
				if (floors == null)
					return true;

				// 几何未变校验：hold 数量必须与现有对象一一对应
				int holdCount = 0;
				for (int i = 0; i < floors.Count; i++)
					if (floors[i] != null && floors[i].holdLength >= 0)
						holdCount++;
				if (holdCount != holdGOs.Count)
					return true;

				// 复刻原版头部的容器/材质准备（holdContainer 公有字段）
				GameObject container = GameObject.Find("Hold Container");
				if (container == null)
				{
					// 原版此时会新建容器；对象都还挂在旧容器下，找父级兜底
					if (holdGOs[0] != null)
						container = holdGOs[0].transform.parent != null
							? holdGOs[0].transform.parent.gameObject
							: new GameObject("Hold Container");
					else
						return true; // 对象异常，交还原版
				}
				__instance.holdContainer = container;

				int goIndex = 0;
				for (int i = 0; i < floors.Count; i++)
				{
					var floor = floors[i];
					if (floor == null || floor.holdLength < 0)
						continue;

					GameObject go = holdGOs[goIndex++];
					if (go == null)
						return true; // 中途发现对象被销毁：交还原版全量重建兜底

					var renderer = go.GetComponent<scrHoldRenderer>();
					var meshFilter = go.GetComponent<MeshFilter>();
					var meshRenderer = go.GetComponent<MeshRenderer>();
					if (renderer == null || meshFilter == null || meshRenderer == null)
						return true;

					go.transform.SetParent(container.transform, false);

					Mesh mesh = meshFilter.mesh;
					mesh.Clear();
					renderer.m_mesh = mesh;
					renderer.m_meshRenderer = meshRenderer;
					floor.holdRenderer = renderer;
					renderer.startFloor = floor;
					renderer.CreateMesh();
					renderer.Unfill();
				}
				return false; // 全部复用成功
			}
			catch (Exception)
			{
				return true; // 任何异常回退原版（此时对象未被销毁，原版会重建）
			}
		}
	}
}
