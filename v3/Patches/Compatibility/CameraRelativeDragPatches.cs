using Iridium.Config;
using System.Collections.Generic;
using ADOFAI;
using HarmonyLib;
using UnityEngine;
using Iridium;

namespace Iridium.Patches.Compatibility
{
	/// <summary>
	/// 修复 DragDecorations 对 Camera/CameraAspect 类型装饰物的拖拽偏移计算 ——
	/// 原版 GetDecorationDragDelta 只处理了 parallax，没有适配镜头相对坐标系。
	/// </summary>
	[HarmonyPatch(typeof(scnEditor),nameof(scnEditor.DragDecorations))]
	[IriPatch(Path = "compatibility/cameraDrag", Pre = typeof(CompatibilitySettings), Condition = "fixCameraRelativeDrag")]
	public static class CameraRelativeDragPatches
	{
		private static AccessTools.FieldRef<scnEditor, Dictionary<scrDecoration, Vector2>>? _dragStartPositions;
		private static AccessTools.FieldRef<scnEditor, float>? _addXDragCache;
		private static AccessTools.FieldRef<scnEditor, float>? _addYDragCache;
		private static AccessTools.FieldRef<LevelEvent, Dictionary<string, object>>? _eventData;
		private static bool _initialized;

		private static void Initialize()
		{
			if (_initialized) return;
			_dragStartPositions = AccessTools.FieldRefAccess<scnEditor, Dictionary<scrDecoration, Vector2>>("decorationPositionsAtDragStart");
			_addXDragCache = AccessTools.FieldRefAccess<scnEditor, float>("addXDragCache");
			_addYDragCache = AccessTools.FieldRefAccess<scnEditor, float>("addYDragCache");
			_eventData = AccessTools.FieldRefAccess<LevelEvent, Dictionary<string, object>>("data");
			_initialized = true;
		}

		[HarmonyPrefix]
		public static bool Prefix(scnEditor __instance, Vector3 translation, bool ignoreModifiers = false)
		{
			if (!Main.Settings.compatibility.fixCameraRelativeDrag) return true;

			Initialize();

			// 检查是否有 camera-relative 装饰物
			if (!HasCameraRelativeSelection(__instance)) return true;

			bool shiftLock = RDInput.holdingShift && !ignoreModifiers;

			var dragStart = _dragStartPositions!(__instance);
			ref var addX = ref _addXDragCache!(__instance);
			ref var addY = ref _addYDragCache!(__instance);

			float absX = Mathf.Abs(translation.x) + addX;
			float absY = Mathf.Abs(translation.y) + addY;
			bool preferX = absX > absY;
			addX = preferX ? 1f : 0f;
			addY = preferX ? 0f : 1f;

			foreach (var levelEvent in __instance.selectedDecorations)
			{
				var decoration = scrDecorationManager.GetDecoration(levelEvent);
				if (decoration == null || levelEvent.locked || decoration.forceLock || !dragStart.ContainsKey(decoration))
					continue;

				var placementType = (DecPlacementType)levelEvent["relativeTo"];
				bool isCamRel = placementType == DecPlacementType.Camera || placementType == DecPlacementType.CameraAspect;

				Vector2 startPos = dragStart[decoration];
				Vector2 worldPos;

				if (isCamRel)
				{
					worldPos = DragCameraRelative(__instance, levelEvent, decoration, startPos, translation, preferX, shiftLock);
				}
				else
				{
					worldPos = DragNormal(levelEvent, decoration, startPos, translation, preferX, shiftLock);
				}

				Vector2 storedPos = worldPos;
				if (placementType == DecPlacementType.Tile)
				{
					int idx = Mathf.Clamp(levelEvent.floor, 0, __instance.floors.Count - 1);
					storedPos -= scrLevelMaker.instance.listFloors[idx].transform.position.xy();
				}
				storedPos /= ADOBase.controller.tileSize;

				levelEvent["position"] = storedPos;
				decoration.SetPosition(worldPos, decoration.pivotOffsetVec);
			}

			if (__instance.SelectionDecorationIsSingle())
			{
				__instance.levelEventsPanel.UpdatePropertyText(__instance.selectedDecorations[0], "position");
			}

			return false;
		}

		private static Vector2 DragCameraRelative(scnEditor editor, LevelEvent ev, scrDecoration dec,
			Vector2 startPos, Vector3 translation, bool preferX, bool shiftLock)
		{
			DecPlacementType type = (DecPlacementType)ev["relativeTo"];
			float scale = 20f / (editor.camera.orthographicSize * 2f);
			Vector2 delta = translation.xy() * scale;

			if (type == DecPlacementType.Camera)
				delta.x /= editor.camera.aspect;

			Vector2 freePos = startPos + delta;
			return ApplyAxisLock(startPos, freePos, preferX, shiftLock);
		}

		private static Vector2 DragNormal(LevelEvent ev, scrDecoration dec,
			Vector2 startPos, Vector3 translation, bool preferX, bool shiftLock)
		{
			Vector2 delta = GetDecorationDragDelta(translation.xy(), dec);
			var data = _eventData!(ev);
			if (data.TryGetValue("parallax", out var pObj) && pObj is Vector2 pVec)
			{
				if (pVec.x == 100f) delta.x = 0f;
				if (pVec.y == 100f) delta.y = 0f;
			}

			Vector2 freePos = startPos + delta;
			return ApplyAxisLock(startPos, freePos, preferX, shiftLock);
		}

		private static Vector2 ApplyAxisLock(Vector2 start, Vector2 free, bool preferX, bool locked)
		{
			if (!locked) return free;
			return new Vector2(preferX ? free.x : start.x, preferX ? start.y : free.y);
		}

		private static Vector2 GetDecorationDragDelta(Vector2 translation, scrDecoration dec)
		{
			Vector2 mult = Vector2.one - dec.parallax.multiplier;
			if (mult.x == 0f) mult.x = 1f;
			if (mult.y == 0f) mult.y = 1f;
			return translation / mult;
		}

		private static bool HasCameraRelativeSelection(scnEditor editor)
		{
			foreach (var ev in editor.selectedDecorations)
			{
				var type = (DecPlacementType)ev["relativeTo"];
				if (type == DecPlacementType.Camera || type == DecPlacementType.CameraAspect)
					return true;
			}
			return false;
		}
	}
}
