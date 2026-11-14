using ADOFAI;
using HarmonyLib;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Iridium.Patches
{
	/// <summary>
/// On death-restart, reuses existing hold GameObjects when the geometry has not
/// changed instead of destroying and rebuilding them (CreateMesh + Unfill only).
/// </summary>
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

				int holdCount = 0;
				for (int i = 0; i < floors.Count; i++)
					if (floors[i] != null && floors[i].holdLength >= 0)
						holdCount++;
				if (holdCount != holdGOs.Count)
					return true;

				GameObject container = GameObject.Find("Hold Container");
				if (container == null)
				{
					if (holdGOs[0] != null)
						container = holdGOs[0].transform.parent != null
							? holdGOs[0].transform.parent.gameObject
							: new GameObject("Hold Container");
					else
						return true;
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
						return true;

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
				return false;
			}
			catch (Exception)
			{
				return true;
			}
		}
	}
}
