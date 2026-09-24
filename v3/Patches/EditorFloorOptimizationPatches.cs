using Iridium.Config;
using ADOFAI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace Iridium.Patches
{
	public static class EditorFloorOptimizationPatches
	{
		private const double Pi1_5 = 4.71238899230957;
		private const float SentinelNoAngle = 999f;

		#region Static State for Incremental Mode

		// NOTE(isOldLevel 与 compatibility/forceAngleData 的交互，改代码前必读):
		//
		// 增量重建只对"现代表示"成立：几何由 angleData → InstantiateFloatFloors
		// 构建，floorAngles.Length + 1 == listFloors.Count，angles[j] 是第 j+1 块
		// 的朝向。旧谱面（isOldLevel == true）的 LevelData.angleData 是空的，
		// 几何由 InstantiateStringFloors(pathData) 构建，本文件并未接管；此时
		// 任何基于 floorAngles 的重建都会算错。所以所有增量入口都必须对
		// isOldLevel 直接放行给原版。
		//
		// 开启 compatibility/forceAngleData 时，LevelData.Decode 会把 pathData
		// 转成角度制 angleData 并移除 pathData（见 ForceAngleDataPatch），
		// isOldLevel 最终为 false —— 旧谱面也因此走 float 路径，增量重建才合法。
		// 也就是说 ForceAngleData 与本优化是叠加关系，不是冲突；但一旦将来
		// Decode / isOldLevel 的判定逻辑改动，这里的哨兵就是防线。
		private static bool _incrementalMode;
		private static bool _incrementalIsInsert;
		private static int _incrementalSeqID;

		#endregion

		#region Reflection Targets

		// FieldRefAccess delegates — direct memory access, zero allocation per call
		private static AccessTools.FieldRef<scnEditor, bool>? _refreshDecSpritesRef;
		private static AccessTools.FieldRef<scrLevelMaker, GameObject>? _meshFloorRef;
		private static AccessTools.FieldRef<scrLevelMaker, GameObject>? _spriteFloorRef;

		// Cached open delegate — created once, reused every call
		private static Action<scnEditor>? _drawFloorNumsAction;
		private static Action<scnEditor>? _drawFloorOffsetLinesAction;
		private static Action<scnEditor, bool>? _drawHoldsAction;
		private static Action<scnEditor>? _drawMultiPlanetAction;

		private static void DrawFloorNums(scnEditor editor)
		{
			(_drawFloorNumsAction ??= AccessTools.MethodDelegate<Action<scnEditor>>(
				AccessTools.Method(typeof(scnEditor), "DrawFloorNums"), null))?.Invoke(editor);
		}

		private static void DrawFloorOffsetLines(scnEditor editor)
		{
			(_drawFloorOffsetLinesAction ??= AccessTools.MethodDelegate<Action<scnEditor>>(
				AccessTools.Method(typeof(scnEditor), "DrawFloorOffsetLines"), null))?.Invoke(editor);
		}

		private static void DrawHolds(scnEditor editor, bool unfillHolds)
		{
			(_drawHoldsAction ??= AccessTools.MethodDelegate<Action<scnEditor, bool>>(
				AccessTools.Method(typeof(scnEditor), "DrawHolds", new[] { typeof(bool) }), null))?.Invoke(editor, unfillHolds);
		}

		private static void DrawMultiPlanet(scnEditor editor)
		{
			(_drawMultiPlanetAction ??= AccessTools.MethodDelegate<Action<scnEditor>>(
				AccessTools.Method(typeof(scnEditor), "DrawMultiPlanet"), null))?.Invoke(editor);
		}

		#endregion

		#region Helpers

		private static float FloorCharToAngle(char floorType)
		{
			var method = AccessTools.Method(typeof(scrLevelMaker), "GetAngleFromFloorCharDirection");
			if (method != null)
				return (float)method.Invoke(null, new object[] { floorType })!;

			var partial = AccessTools.Method(typeof(FloorHelper), "PathIdToRadiansPartial");
			if (partial != null)
			{
				double? result = (double?)partial.Invoke(null, new object[] { floorType });
				return result.HasValue ? (float)result.Value : 999f;
			}

			return 999f;
		}

		private static bool AnyFloorsHaveHolds(List<scrFloor> floors)
		{
			for (int i = 0; i < floors.Count; i++)
				if (floors[i].holdLength >= 0) return true;
			return false;
		}

		private static bool AnyEventsHavePositionTrack(List<LevelEvent> events)
		{
			for (int i = 0; i < events.Count; i++)
				if (events[i].eventType == LevelEventType.PositionTrack) return true;
			return false;
		}

		#endregion

		#region Patch: InsertCharFloor - Incremental

		[IriPatch(Path = "optimizer/editorFloor/insert", Pre = typeof(OptimizerSettings), Condition = "enableEditorFloorOptimization,incrementalFloorInsert")]
		[HarmonyPatch(typeof(scnEditor), "InsertCharFloor")]
		public static class InsertCharFloorOptimizationPatch
		{
			[HarmonyPrefix]
			public static bool Prefix(scnEditor __instance, int sequenceID, char floorType)
			{
				if (!Main.Settings.optimizer.enableEditorFloorOptimization) return true;
				if (!Main.Settings.optimizer.incrementalFloorInsert) return true;

				// 哨兵：旧谱面走 InstantiateStringFloors(pathData)，增量重建不适用
				// （见文件顶部 NOTE / forceAngleData 交互）。
				if (__instance.levelData.isOldLevel) return true;

				var lm = scrLevelMaker.instance;
				var floors = lm.listFloors;
				if (floors == null || floors.Count < 2) return true;

				try
				{
					// Convert char to angle — midspin fallback to original
					float floorAngle = FloorCharToAngle(floorType);
					if (floorAngle == 999f) return true;

					// Inject data before RemakePath runs
					__instance.levelData.pathData = __instance.levelData.pathData.Insert(sequenceID, floorType.ToString());
					__instance.levelData.angleData.Insert(sequenceID, floorAngle);

					// Let RemakePath() run the full chain (MakeLevel → InstantiateFloatFloors → post-process → draws).
					// Our InstantiateFloatFloors patch intercepts to reuse existing floors.
					_incrementalMode = true;
					_incrementalIsInsert = true;
					_incrementalSeqID = sequenceID;

					__instance.RemakePath();

					_incrementalMode = false;
					return false; // data already inserted, skip original
				}
				catch (Exception e)
				{
					Main.Logger?.Error($"[EditorFloorOptimization] InsertCharPrefix failed: {e}");
					_incrementalMode = false;
					return true; // fallback to original
				}
			}
		}

		#endregion

		#region Patch: InsertFloatFloor - Incremental

		[IriPatch(Path = "optimizer/editorFloor/insert", Pre = typeof(OptimizerSettings), Condition = "enableEditorFloorOptimization,incrementalFloorInsert")]
		[HarmonyPatch(typeof(scnEditor), "InsertFloatFloor")]
		public static class InsertFloatFloorOptimizationPatch
		{
			[HarmonyPrefix]
			public static bool Prefix(scnEditor __instance, int sequenceID, float floorAngle)
			{
				if (!Main.Settings.optimizer.enableEditorFloorOptimization) return true;
				if (!Main.Settings.optimizer.incrementalFloorInsert) return true;

				// 哨兵：同上，旧谱面一律交还原版
				if (__instance.levelData.isOldLevel) return true;

				var lm = scrLevelMaker.instance;
				var floors = lm.listFloors;
				if (floors == null || floors.Count < 2) return true;

				try
				{
					__instance.levelData.angleData.Insert(sequenceID, floorAngle);

					_incrementalMode = true;
					_incrementalIsInsert = true;
					_incrementalSeqID = sequenceID;

					__instance.RemakePath();

					_incrementalMode = false;
					return false;
				}
				catch (Exception e)
				{
					Main.Logger?.Error($"[EditorFloorOptimization] InsertFloatPrefix failed: {e}");
					_incrementalMode = false;
					return true;
				}
			}
		}

		#endregion

		#region Patch: InstantiateFloatFloors - Reuse floors in editor

		[IriPatch(Path = "optimizer/editorFloor/insert", Pre = typeof(OptimizerSettings), Condition = "enableEditorFloorOptimization,incrementalFloorInsert")]
		[HarmonyPatch(typeof(scrLevelMaker), "InstantiateFloatFloors")]
		public static class InstantiateFloatFloorsOptimizationPatch
		{
			[HarmonyPrefix]
			public static bool Prefix(scrLevelMaker __instance)
			{
				if (!_incrementalMode) return true; // normal mode - let original run

				// 哨兵：旧谱面由 InstantiateStringFloors 构建，角度/楼层数约定不同
				// （angleData 为空），不允许走增量重建。
				if (__instance.isOldLevel)
				{
					_incrementalMode = false;
					return true;
				}

				// 与原版 scrLevelMaker.InstantiateFloatFloors 的选择保持一致：
				// 非播放中、或精灵砖块（FloorSpriteRenderer）时，原版会整批销毁重建，
				// 增量复用只对网格砖块安全 —— 这些情况一律交还原版。
				var initialFloors = __instance.listFloors;
				if (!Application.isPlaying ||
					(initialFloors.Count > 0 && initialFloors[0].GetComponent<FloorSpriteRenderer>() != null))
				{
					_incrementalMode = false;
					return true;
				}

				try
				{
					var floors = __instance.listFloors;
					int targetCount = __instance.floorAngles.Length + 1;

					if (_incrementalIsInsert)
					{
						int seqID = _incrementalSeqID;

						// Create ONE new floor
						_meshFloorRef ??= AccessTools.FieldRefAccess<scrLevelMaker, GameObject>("meshFloor");
						_spriteFloorRef ??= AccessTools.FieldRefAccess<scrLevelMaker, GameObject>("spriteFloor");
						GameObject prefab = _meshFloorRef(__instance);
						if (prefab == null) prefab = _spriteFloorRef(__instance);
						if (prefab == null) return true; // fallback

						GameObject container = GameObject.Find("Floors");
						if (container == null) container = new GameObject("Floors");

						// Calculate position for new floor
						var prevFloor = floors[Math.Max(0, seqID)];
						double radius = scrController.instance.tileSize;
						float ang = __instance.floorAngles[Math.Min(seqID, __instance.floorAngles.Length - 1)];
						double exitAngle = ang == 999f
							? prevFloor.entryangle
							: (-ang + 90f) * (Math.PI / 180.0);
						Vector3 offset = scrMisc.getVectorFromAngle(exitAngle, radius);
						Vector3 insertPos = floors[seqID].transform.position + offset;

						GameObject newObj = UnityEngine.Object.Instantiate(prefab, insertPos, Quaternion.identity);
						newObj.transform.parent = container.transform;
						var newFloor = newObj.GetComponent<scrFloor>();

						// Insert into list at correct position
						floors.Insert(seqID + 1, newFloor);

						// Remove excess floors if we have too many
						while (floors.Count > targetCount)
						{
							var extra = floors[floors.Count - 1];
							if (extra != null) UnityEngine.Object.DestroyImmediate(extra.gameObject);
							floors.RemoveAt(floors.Count - 1);
						}

						// Now rebuild geometry for all floors from insertion point
						// Reuse existing floor 0 setup
						RebuildPositionsAndAngles(__instance, seqID);
					}
					else
					{
						// Delete mode - floor was already removed from list by our prefix
						// Remove excess floors
						while (floors.Count > targetCount)
						{
							var extra = floors[floors.Count - 1];
							if (extra != null) UnityEngine.Object.DestroyImmediate(extra.gameObject);
							floors.RemoveAt(floors.Count - 1);
						}

						// Add missing floors if needed
						while (floors.Count < targetCount)
						{
							_meshFloorRef ??= AccessTools.FieldRefAccess<scrLevelMaker, GameObject>("meshFloor");
							_spriteFloorRef ??= AccessTools.FieldRefAccess<scrLevelMaker, GameObject>("spriteFloor");
							GameObject prefab = _meshFloorRef(__instance);
							if (prefab == null) prefab = _spriteFloorRef(__instance);
							if (prefab == null) break;

							GameObject container = GameObject.Find("Floors");
							if (container == null) container = new GameObject("Floors");

							var newObj = UnityEngine.Object.Instantiate(prefab, Vector3.zero, Quaternion.identity);
							newObj.transform.parent = container.transform;
							floors.Add(newObj.GetComponent<scrFloor>());
						}

						RebuildPositionsAndAngles(__instance, Math.Max(0, _incrementalSeqID - 1));
					}

					return false; // skip original InstantiateFloatFloors
				}
				catch (Exception e)
				{
					Main.Logger?.Error($"[EditorFloorOptimization] InstFloatPrefix failed: {e}");
					_incrementalMode = false;
					return true; // fallback
				}
			}
		}

		#endregion

		#region Geometry Rebuild

		private static void RebuildPositionsAndAngles(scrLevelMaker lm, int fromSeqID)
		{
			var floors = lm.listFloors;
			var angles = lm.floorAngles;
			if (floors == null || floors.Count == 0 || angles == null) return;

			// Destroy all ffxPlusBase on floors before the rebuild range.
			// These floors are not touched by ResetFloorState, so their event
			// components (holds, twirls, etc.) would persist and accumulate.
			int start = Math.Max(0, fromSeqID);
			for (int i = 0; i < start && i < floors.Count; i++)
			{
				var ffx = floors[i].GetComponents<ffxPlusBase>();
				for (int j = 0; j < ffx.Length; j++)
					UnityEngine.Object.DestroyImmediate(ffx[j]);
			}

			// Full rebuild from floor 0
			if (start == 0)
			{
				var floor0 = floors[0];
				ResetFloorState(floor0, Vector3.zero);
				floor0.entryangle = Pi1_5;
				floor0.seqID = 0;
				floor0.hasLit = true;
				floor0.prevfloor = null;
			}

			// Recompute cumulative position from floor 0 to the anchor.
			// Using floors[start].transform.position directly would compound
			// floating-point error across multiple incremental edits.
			Vector3 cumulativePos = Vector3.zero;
			double entryAngle = Pi1_5;
			double tileRadius = scrController.instance.tileSize;
			for (int i = 0; i < start && i < angles.Length; i++)
			{
				float ang = angles[i];
				double exitAngle = ang == SentinelNoAngle ? entryAngle : (-ang + 90f) * (Math.PI / 180.0);
				cumulativePos += scrMisc.getVectorFromAngle(exitAngle, tileRadius);
				entryAngle = (exitAngle + Math.PI) % (2.0 * Math.PI);
			}
			double prevEntryAngle = entryAngle;

			// Reset the anchor floor if we're not doing a full rebuild (floor 0 was
			// already reset above). All other floors are reset once as nextFloor below.
			if (start > 0)
				ResetFloorState(floors[start], cumulativePos);

			for (int i = start; i < floors.Count - 1 && i < angles.Length; i++)
			{
				var floor = floors[i];
				var nextFloor = floors[i + 1];

				double radius = scrController.instance.tileSize;
				float ang = angles[i];

				floor.entryangle = prevEntryAngle;

				double exitAngle;
				if (ang == 999f)
				{
					exitAngle = prevEntryAngle;
					floor.midSpin = true;
				}
				else
				{
					exitAngle = (-ang + 90f) * (Math.PI / 180.0);
					floor.midSpin = false;
				}

				floor.exitangle = exitAngle;
				floor.seqID = i;
				floor.prevfloor = i > 0 ? floors[i - 1] : null;
				floor.nextfloor = nextFloor;
				floor.speed = 1f;
				floor.isCCW = false;

				Vector3 offset = scrMisc.getVectorFromAngle(exitAngle, radius);
				cumulativePos += offset;

				ResetFloorState(nextFloor, cumulativePos);
				nextFloor.entryangle = (exitAngle + Math.PI) % (2.0 * Math.PI);
				nextFloor.seqID = i + 1;
				nextFloor.transform.position = cumulativePos;
				nextFloor.floatDirection = ang;
				nextFloor.prevfloor = floor;

				prevEntryAngle = nextFloor.entryangle;
			}

			if (floors.Count > 1)
			{
				var lastFloor = floors[floors.Count - 1];
				lastFloor.exitangle = lastFloor.entryangle + Math.PI;
				lastFloor.nextfloor = null;

				if (scrController.instance?.gameworld == true)
				{
					lastFloor.isportal = true;
					lastFloor.levelnumber = Portal.EndOfLevel;
				}
			}
		}

		/// <summary>
		/// Replicate scrLevelMaker.ResetFloor logic for incremental reuse:
		/// destroy stray ffxPlusBase components, reset transform, call floor.Reset().
		/// </summary>
		private static void ResetFloorState(scrFloor floor, Vector3 position)
		{
			if (floor == null) return;

			var ffxComponents = floor.GetComponents<ffxPlusBase>();
			for (int i = 0; i < ffxComponents.Length; i++)
				UnityEngine.Object.DestroyImmediate(ffxComponents[i]);

			floor.transform.position = position;
			floor.transform.rotation = Quaternion.identity;
			floor.transform.localScale = Vector3.one;

			// 对齐原版 scrLevelMaker.ResetFloor：播放中把砖块材质重置为默认，
			// 否则复用砖块会残留上一次编辑造成的材质改动。
			if (Application.isPlaying && floor.floorRenderer != null &&
				RDConstants.data != null && RDConstants.data.floorMeshDefault != null)
			{
				floor.floorRenderer.material.CopyPropertiesFromMaterial(RDConstants.data.floorMeshDefault);
			}

			floor.Reset();
		}

		#endregion

		#region Patch: DeleteFloor - Incremental (via Transpiler replacing RemakePath)

		[IriPatch(Path = "optimizer/editorFloor/insert", Pre = typeof(OptimizerSettings), Condition = "enableEditorFloorOptimization,incrementalFloorInsert")]
		[HarmonyPatch(typeof(scnEditor), "DeleteFloor",
			new Type[] { typeof(int), typeof(bool) })]
		public static class DeleteFloorOptimizationPatch
		{
			[HarmonyTranspiler]
			public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
			{
				// Always replace RemakePath() call with our version.
				// Settings check happens at runtime in LightweightDeleteRemakePath.
				var list = instructions.ToList();

				// Find pattern: call instance void scnEditor::RemakePath(bool, bool)
				// and replace with our method that has the same signature (editor, bool, bool)
				for (int i = 0; i < list.Count; i++)
				{
					if (list[i].opcode == OpCodes.Call &&
						list[i].operand is MethodInfo method &&
						method.Name == "RemakePath" &&
						method.DeclaringType == typeof(scnEditor))
					{
						list[i] = new CodeInstruction(OpCodes.Call,
							AccessTools.Method(typeof(DeleteFloorOptimizationPatch),
								nameof(LightweightDeleteRemakePath)));
					}
				}
				return list;
			}

			// Lightweight replacement for RemakePath(bool, bool) in DeleteFloor
			// Signature matches RemakePath(bool applyEventsToFloors, bool remakeLevel)
			// so the stack is balanced (editor, bool, bool -> void)
			public static void LightweightDeleteRemakePath(scnEditor editor, bool applyEventsToFloors, bool remakeLevel)
			{
				if (!Main.Settings.optimizer.enableEditorFloorOptimization ||
					!Main.Settings.optimizer.incrementalFloorInsert)
				{
					editor.RemakePath();
					return;
				}

				try
				{
					var lm = scrLevelMaker.instance;
					var floors = lm.listFloors;
					if (floors == null || floors.Count < 2)
					{
						editor.RemakePath();
						return;
					}

					// Data already removed by DeleteFloor. Let RemakePath run the full
					// chain (scnGame.RemakePath → MakeLevel → post-process → draws).
					// InstantiateFloatFloors is intercepted to reuse existing floors.
					_incrementalMode = true;
					_incrementalIsInsert = false;
					_incrementalSeqID = 0;

					editor.RemakePath(applyEventsToFloors, remakeLevel);

					_incrementalMode = false;
				}
				catch (Exception e)
				{
					Main.Logger?.Error($"[EditorFloorOptimization] LightweightDeleteRemakePath failed: {e}");
					_incrementalMode = false;
					editor.RemakePath(); // Fallback
				}
			}
		}

		#endregion

		#region Patch: scnEditor.RemakePath - Skip redundant calls

		[IriPatch(Path = "optimizer/editorFloor/insert", Pre = typeof(OptimizerSettings), Condition = "enableEditorFloorOptimization,incrementalFloorInsert,skipRedundantRemakePath")]
		[HarmonyPatch(typeof(scnEditor), "RemakePath",
			new Type[] { typeof(bool), typeof(bool) })]
		public static class RemakePathRedundancyPatch
		{
			[HarmonyPrefix]
			public static bool Prefix(scnEditor __instance, bool applyEventsToFloors, bool remakeLevel)
			{
				if (!Main.Settings.optimizer.enableEditorFloorOptimization) return true;
				if (!Main.Settings.optimizer.skipRedundantRemakePath) return true;

				// Visual-only refresh (e.g. ToggleFloorNums calls RemakePath(false, false)).
				// 原版 scnEditor.RemakePath 会先调 customLevel.RemakePath(false,false)
				// （内部做 SetupConductor + DrawHolds + DrawMultiPlanet），再执行
				// DrawFloorOffsetLines / DrawHolds / DrawFloorNums / DrawMultiPlanet。
				// 我们跳过 scnGame 那一份重复的 Holds/MultiPlanet，但仍按原版补齐
				// 导体设置与全部编辑器级绘制，避免悬浮线/长条/多星球残留旧状态。
				if (!applyEventsToFloors && !remakeLevel)
				{
					ADOBase.conductor.SetupConductorWithLevelData(__instance.levelData);
					DrawFloorOffsetLines(__instance);
					DrawHolds(__instance, true);
					DrawFloorNums(__instance);
					DrawMultiPlanet(__instance);
					return false;
				}

				return true;
			}
		}

		#endregion

		#region Patch: scnGame.RemakePath - Optimize visual-only calls

		[IriPatch(Path = "optimizer/editorFloor/insert", Pre = typeof(OptimizerSettings), Condition = "enableEditorFloorOptimization,incrementalFloorInsert,skipRedundantRemakePath")]
		[HarmonyPatch(typeof(scnGame), "RemakePath",
			new Type[] { typeof(bool), typeof(bool) })]
		public static class GameRemakePathOptimizationPatch
		{
			[HarmonyPrefix]
			public static bool Prefix(scnGame __instance, bool applyEventsToFloors, bool remakeLevel)
			{
				if (!Main.Settings.optimizer.enableEditorFloorOptimization) return true;
				var editor = ADOBase.editor;
				if (editor == null) return true;

				if (!remakeLevel && !applyEventsToFloors)
				{
					// Visual-only: just setup conductor
					ADOBase.conductor.SetupConductorWithLevelData(__instance.levelData);
					return false;
				}

				return true;
			}
		}

		#endregion

		#region Patch: DrawFloorNums

		[IriPatch(Path = "optimizer/editorFloor/insert", Pre = typeof(OptimizerSettings), Condition = "enableEditorFloorOptimization,incrementalFloorInsert,rangeBasedRedraw")]
		[HarmonyPatch(typeof(scnEditor), "DrawFloorNums")]
		public static class DrawFloorNumsOptimizationPatch
		{
			[HarmonyPrefix]
			public static bool Prefix(scnEditor __instance)
			{
				if (!Main.Settings.optimizer.enableEditorFloorOptimization) return true;

				var floors = __instance.floors;
				if (floors == null) return false;

				bool showNums = __instance.showFloorNums && !__instance.playMode;
				for (int i = 0; i < floors.Count; i++)
				{
					var floor = floors[i];
					if (floor != null && floor.enabled && floor.editorNumText != null)
						floor.editorNumText.gameObject.SetActive(showNums && !floor.isFake);
				}
				return false;
			}
		}

		#endregion

		#region Patch: DrawFloorOffsetLines - Skip if no PositionTrack events

		[IriPatch(Path = "optimizer/editorFloor/insert", Pre = typeof(OptimizerSettings), Condition = "enableEditorFloorOptimization,incrementalFloorInsert,skipRedundantRemakePath")]
		[HarmonyPatch(typeof(scnEditor), "DrawFloorOffsetLines")]
		public static class DrawFloorOffsetLinesOptimizationPatch
		{
			[HarmonyPrefix]
			public static bool Prefix(scnEditor __instance)
			{
				if (!Main.Settings.optimizer.enableEditorFloorOptimization) return true;
				if (!Main.Settings.optimizer.skipRedundantRemakePath) return true;

				if (!AnyEventsHavePositionTrack(__instance.events))
					return false; // no offset lines to draw

				return true;
			}
		}

		#endregion

		#region Patch: OffsetFloorIDsInEvents

		[IriPatch(Path = "optimizer/editorFloor/insert", Pre = typeof(OptimizerSettings), Condition = "enableEditorFloorOptimization,incrementalFloorInsert,optimizeOffsetFloorEvents")]
		[HarmonyPatch(typeof(scnEditor), "OffsetFloorIDsInEvents")]
		public static class OffsetFloorIDsOptimizationPatch
		{
			[HarmonyPrefix]
			public static bool Prefix(scnEditor __instance, int startFloorID, int offset)
			{
				if (!Main.Settings.optimizer.enableEditorFloorOptimization) return true;
				if (!Main.Settings.optimizer.optimizeOffsetFloorEvents) return true;
				if (offset == 0) return false;

				var events = __instance.events;
				for (int i = 0; i < events.Count; i++)
					if (events[i].floor > startFloorID)
						events[i].floor += offset;

				var decorations = __instance.decorations;
				for (int i = 0; i < decorations.Count; i++)
					if (decorations[i].floor > startFloorID)
						decorations[i].floor += offset;

				_refreshDecSpritesRef ??= AccessTools.FieldRefAccess<scnEditor, bool>("refreshDecSprites");
				_refreshDecSpritesRef(__instance) = true;
				return false;
			}
		}

		#endregion

		#region Patch: Skip ApplyEventsToFloors during incremental insert

		/// <summary>
		/// 增量插入砖块时跳过全量 ApplyEventsToFloors。
		/// OffsetFloorIDsInEvents 已经处理了事件 floor ID 偏移，
		/// 全量重新应用事件对百万砖块是灾难性的。
		/// </summary>
		[IriPatch(Path = "optimizer/editorFloor/insert", Pre = typeof(OptimizerSettings), Condition = "enableEditorFloorOptimization,incrementalFloorInsert,skipApplyEventsOnInsert")]
		[HarmonyPatch(typeof(scnGame), "ApplyEventsToFloors",
			new[] { typeof(List<scrFloor>) })]
		public static class SkipApplyEventsOnInsertPatch
		{
			[HarmonyPrefix]
			public static bool Prefix()
			{
				if (!Main.Settings.optimizer.enableEditorFloorOptimization) return true;
				if (!_incrementalMode) return true;
				if (!Main.Settings.optimizer.skipApplyEventsOnInsert) return true;

				// Events already offset by OffsetFloorIDsInEvents / FloorWasCreatedOrDeleted.
				// Skip the full re-application for massive level performance.
				return false;
			}
		}

		#endregion

		#region Utility

		public static void ResetState()
		{
			_incrementalMode = false;
		}

		#endregion
	}
}
