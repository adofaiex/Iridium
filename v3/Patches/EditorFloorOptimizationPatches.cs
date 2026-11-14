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

		// 增量重建只对"现代表示"成立：几何由 angleData → InstantiateFloatFloors 构建，
		// floorAngles.Length + 1 == listFloors.Count，angles[j] 是第 j+1 块的朝向。
		// 旧谱面（isOldLevel == true）的 angleData 为空，几何由 InstantiateStringFloors(pathData)
		// 构建，本文件并未接管 —— 所有增量入口都必须对 isOldLevel 直接放行给原版。
		// 开启 compatibility/forceAngleData 后 Decode 会把 pathData 转成 angleData 并移除
		// pathData，isOldLevel 最终为 false，二者叠加而非冲突；但 Decode / isOldLevel 的判定
		// 一旦改动，这里的哨兵就是防线。
		private static bool _incrementalMode;
		private static bool _incrementalIsInsert;
		private static int _incrementalSeqID;
		private static int _rebuildTick;

		internal static bool IncrementalActive => _incrementalMode;

		#endregion

		#region Reflection Targets

		// FieldRefAccess delegates — direct memory access, zero allocation per call
		private static AccessTools.FieldRef<scnEditor, bool>? _refreshDecSpritesRef;
		private static AccessTools.FieldRef<scrLevelMaker, GameObject>? _meshFloorRef;
		private static AccessTools.FieldRef<scrLevelMaker, GameObject>? _spriteFloorRef;
		private static AccessTools.FieldRef<scrLevelMaker, List<GameObject>>? _holdGOsRef;

		// Cached open delegate — created once, reused every call
		private static Action<scnEditor>? _drawFloorNumsAction;
		private static Action<scnEditor>? _drawFloorOffsetLinesAction;
		private static Action<scnEditor, bool>? _drawHoldsAction;
		private static Action<scnEditor>? _drawMultiPlanetAction;

		private static void DrawFloorNums(scnEditor editor)
		{
			(_drawFloorNumsAction ??= FastDelegate.Open<Action<scnEditor>>(
				AccessTools.Method(typeof(scnEditor), "DrawFloorNums"), null))?.Invoke(editor);
		}

		private static void DrawFloorOffsetLines(scnEditor editor)
		{
			(_drawFloorOffsetLinesAction ??= FastDelegate.Open<Action<scnEditor>>(
				AccessTools.Method(typeof(scnEditor), "DrawFloorOffsetLines"), null))?.Invoke(editor);
		}

		private static void DrawHolds(scnEditor editor, bool unfillHolds)
		{
			(_drawHoldsAction ??= FastDelegate.Open<Action<scnEditor, bool>>(
				AccessTools.Method(typeof(scnEditor), "DrawHolds", new[] { typeof(bool) }), null))?.Invoke(editor, unfillHolds);
		}

		private static void DrawMultiPlanet(scnEditor editor)
		{
			(_drawMultiPlanetAction ??= FastDelegate.Open<Action<scnEditor>>(
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
					// Convert char to angle; 999 = midspin, fall back to the original
					float floorAngle = FloorCharToAngle(floorType);
					if (floorAngle == 999f) return true;

					__instance.levelData.pathData = __instance.levelData.pathData.Insert(sequenceID, floorType.ToString());
					__instance.levelData.angleData.Insert(sequenceID, floorAngle);

					// Let RemakePath() run the full chain; the InstantiateFloatFloors
					// patch below intercepts it to reuse existing floors.
					_incrementalMode = true;
					_incrementalIsInsert = true;
					_incrementalSeqID = sequenceID;

					__instance.RemakePath();

					_incrementalMode = false;
					return false;
				}
				catch (Exception e)
				{
					Main.Logger?.Error($"[EditorFloorOptimization] InsertCharPrefix failed: {e}");
					_incrementalMode = false;
					return true;
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
				if (!_incrementalMode) return true;

				// 哨兵：旧谱面由 InstantiateStringFloors 构建，角度/楼层数约定不同
				// （angleData 为空），不允许走增量重建。
				if (__instance.isOldLevel)
				{
					_incrementalMode = false;
					return true;
				}

				// 与原版 scrLevelMaker.InstantiateFloatFloors 的选择保持一致：非播放中、或精灵砖块
				// （FloorSpriteRenderer）时原版会整批销毁重建，增量复用只对网格砖块安全。
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

						_meshFloorRef ??= AccessTools.FieldRefAccess<scrLevelMaker, GameObject>("meshFloor");
						_spriteFloorRef ??= AccessTools.FieldRefAccess<scrLevelMaker, GameObject>("spriteFloor");
						GameObject prefab = _meshFloorRef(__instance);
						if (prefab == null) prefab = _spriteFloorRef(__instance);
						if (prefab == null) return true;

						GameObject container = GameObject.Find("Floors");
						if (container == null) container = new GameObject("Floors");

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

						floors.Insert(seqID + 1, newFloor);

						while (floors.Count > targetCount)
						{
							var extra = floors[floors.Count - 1];
							if (extra != null) UnityEngine.Object.DestroyImmediate(extra.gameObject);
							floors.RemoveAt(floors.Count - 1);
						}

						RebuildPositionsAndAngles(__instance, seqID);
					}
					else
					{
						// Delete mode - floor was already removed from list by our prefix
						while (floors.Count > targetCount)
						{
							var extra = floors[floors.Count - 1];
							if (extra != null) UnityEngine.Object.DestroyImmediate(extra.gameObject);
							floors.RemoveAt(floors.Count - 1);
						}

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

					return false;
				}
				catch (Exception e)
				{
					Main.Logger?.Error($"[EditorFloorOptimization] InstFloatPrefix failed: {e}");
					_incrementalMode = false;
					return true;
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

			// Native fast path: iridium_core computes all angles/positions in one call, C# only
			// writes them back to Unity objects. Hard binding — returns false when the
			// library is absent, and the managed loop below is the only fallback.
			if (NativeRebuild(lm, Math.Max(0, fromSeqID))) return;

			int start = Math.Max(0, fromSeqID);

			// Floors in [0, start) are not cleaned here: without skipApplyEventsOnInsert the
			// following ApplyEventsToFloors rebuilds all ffx anyway, and with it this
			// operation adds no ffx in that range.

			if (start == 0)
			{
				var floor0 = floors[0];
				ResetFloorState(floor0, Vector3.zero);
				floor0.entryangle = Pi1_5;
				floor0.seqID = 0;
				floor0.hasLit = true;
				floor0.prevfloor = null;
			}

			// Floors before the insertion point are untouched, so reuse their stored position /
			// entry angle; every 64th rebuild recomputes from 0 to stop drift accumulating.
			Vector3 cumulativePos;
			double prevEntryAngle;
			bool fullRecompute = start <= 0 || (++_rebuildTick & 63) == 0;
			if (start > 0 && !fullRecompute)
			{
				cumulativePos = floors[start].transform.position;
				prevEntryAngle = floors[start].entryangle;
			}
			else
			{
				cumulativePos = Vector3.zero;
				double entryAngle = Pi1_5;
				double tileRadius = scrController.instance.tileSize;
				for (int i = 0; i < start && i < angles.Length; i++)
				{
					float ang = angles[i];
					double exitAngle = ang == SentinelNoAngle ? entryAngle : (-ang + 90f) * (Math.PI / 180.0);
					cumulativePos += scrMisc.getVectorFromAngle(exitAngle, tileRadius);
					entryAngle = (exitAngle + Math.PI) % (2.0 * Math.PI);
				}
				prevEntryAngle = entryAngle;
			}

			// Reset the anchor floor unless doing a full rebuild (floor 0 was already
			// reset above); every other floor is reset once as nextFloor below.
			if (start > 0)
			{
				if (float.IsNaN(cumulativePos.x) || float.IsNaN(cumulativePos.y) || double.IsNaN(prevEntryAngle))
				{
					cumulativePos = floors[start].transform.position;
					prevEntryAngle = floors[start].entryangle;
				}
				ResetFloorState(floors[start], cumulativePos);
			}

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

		/// <summary>Replicate scrLevelMaker.ResetFloor for incremental reuse: destroy stray ffxPlusBase, reset transform, call floor.Reset().</summary>
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

		/// <summary>Native rebuild via iridium_core, same anchor/periodic-recompute policy as the managed loop; false when native is unavailable.</summary>
		private static bool NativeRebuild(scrLevelMaker lm, int start)
		{
			if (Iridium.Native.IridiumNative.RemakePath == null) return false;

			var floors = lm.listFloors;
			var angles = lm.floorAngles;
			if (floors == null || angles == null || floors.Count == 0) return false;

			int total = Math.Min(angles.Length, floors.Count - 1);
			if (total < 0) return false;
			if (start > total) start = total;

			float tileSize = scrController.instance != null ? (float)scrController.instance.tileSize : 0.631f;

			// Incremental rebuilds reuse the stored state of floors[start]; every 64th rebuild
			// (or an invalid anchor) recomputes from floor 0 to stop drift.
			bool fromAnchor = false;
			float originX = 0f, originY = 0f;
			double startAngle = Pi1_5;
			if (start > 0)
			{
				if (Iridium.Native.IridiumNative.RemakePathFrom == null) return false;
				bool fullRecompute = (++_rebuildTick & 63) == 0;
				Vector3 anchorPos = floors[start].transform.position;
				double anchorEntry = floors[start].entryangle;
				if (fullRecompute || float.IsNaN(anchorPos.x) || float.IsNaN(anchorPos.y) || double.IsNaN(anchorEntry))
				{
					// Prefix pass: compute the anchor from floor 0 natively.
					var pe = new double[start + 1];
					var pxs = new double[start + 1];
					var ppx = new float[start + 1];
					var ppy = new float[start + 1];
					if (!Iridium.Native.IridiumNative.RemakePathPinned(
						angles, 0, Ones(start), start, Pi1_5, tileSize, false, 0f, 0f, pe, pxs, ppx, ppy))
						return false;
					anchorPos = new Vector3(ppx[start], ppy[start], 0f);
					anchorEntry = pe[start];
				}
				fromAnchor = true;
				originX = anchorPos.x;
				originY = anchorPos.y;
				startAngle = anchorEntry;
			}

			int n = total - start;
			var entry = new double[n + 1];
			var exit = new double[n + 1];
			var px = new float[n + 1];
			var py = new float[n + 1];
			if (!Iridium.Native.IridiumNative.RemakePathPinned(
				angles, start, Ones(n), n, startAngle, tileSize, fromAnchor, originX, originY,
				entry, exit, px, py))
				return false;

			var anchor = floors[start];
			ResetFloorState(anchor, new Vector3(px[0], py[0], 0f));
			anchor.entryangle = entry[0];
			if (start == 0)
			{
				anchor.seqID = 0;
				anchor.hasLit = true;
				anchor.prevfloor = null;
			}

			for (int k = 0; k < n; k++)
			{
				int i = start + k;
				var floor = floors[i];
				var nextFloor = floors[i + 1];
				float ang = angles[i];

				floor.entryangle = entry[k];
				floor.exitangle = exit[k];
				floor.midSpin = ang == SentinelNoAngle;
				floor.seqID = i;
				floor.prevfloor = i > 0 ? floors[i - 1] : null;
				floor.nextfloor = nextFloor;
				floor.speed = 1f;
				floor.isCCW = false;

				var pos = new Vector3(px[k + 1], py[k + 1], 0f);
				ResetFloorState(nextFloor, pos);
				nextFloor.entryangle = entry[k + 1];
				nextFloor.seqID = i + 1;
				nextFloor.transform.position = pos;
				nextFloor.floatDirection = ang;
				nextFloor.prevfloor = floor;
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
			return true;
		}

		private static float[] _ones = new float[0];

		/// <summary>Shared all-ones length-multiplier buffer (no tile scaling).</summary>
		private static float[] Ones(int n)
		{
			if (_ones.Length < n)
			{
				_ones = new float[Math.Max(n, _ones.Length * 2)];
				for (int i = 0; i < _ones.Length; i++) _ones[i] = 1f;
			}
			return _ones;
		}

		#endregion

		#region Patch: DeleteFloor - Incremental (via Transpiler replacing RemakePath)

		[IriPatch(Path = "optimizer/editorFloor/insert", Pre = typeof(OptimizerSettings), Condition = "enableEditorFloorOptimization,incrementalFloorInsert")]
		[HarmonyPatch(typeof(scnEditor), "DeleteFloor",
			new Type[] { typeof(int), typeof(bool) })]
		public static class DeleteFloorOptimizationPatch
		{
			/// <summary>记录本次删除的砖块序号，供 LightweightDeleteRemakePath / DrawFloorNums 使用。</summary>
			[HarmonyPrefix]
			public static void Prefix(int sequenceIndex)
			{
				_incrementalSeqID = Math.Max(0, sequenceIndex);
			}

			[HarmonyTranspiler]
			public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
			{
				// Always replace the RemakePath() call; the settings check happens at
				// runtime in LightweightDeleteRemakePath.
				var list = instructions.ToList();

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

			// Signature must match RemakePath(bool applyEventsToFloors, bool remakeLevel)
			// or the IL stack is unbalanced
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

					// Data already removed by DeleteFloor; the deleted index is recorded by
					// DeleteFloorOptimizationPatch.Prefix and must not be overwritten.
					_incrementalMode = true;
					_incrementalIsInsert = false;

					editor.RemakePath(applyEventsToFloors, remakeLevel);

					_incrementalMode = false;
				}
				catch (Exception e)
				{
					Main.Logger?.Error($"[EditorFloorOptimization] LightweightDeleteRemakePath failed: {e}");
					_incrementalMode = false;
					editor.RemakePath();
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

				// Visual-only refresh (e.g. ToggleFloorNums calls RemakePath(false, false)): skip
				// the duplicate scnGame Holds/MultiPlanet pass, but still do the conductor
				// setup and every editor-level draw so offset lines/holds/planets stay clean.
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
				// Incremental insert/delete only renumbers floors after the insertion point
				int first = _incrementalMode ? Math.Max(0, _incrementalSeqID - 1) : 0;
				for (int i = first; i < floors.Count; i++)
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
					return false;

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

		/// <summary>Skip the full ApplyEventsToFloors on incremental insert; OffsetFloorIDsInEvents already offset the event floor IDs.</summary>
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

				return false;
			}
		}

		#endregion

		#region Patch: Skip duplicated draws during incremental rebuild

		[IriPatch(Path = "optimizer/editorFloor/insert", Pre = typeof(OptimizerSettings), Condition = "enableEditorFloorOptimization,incrementalFloorInsert")]
		[HarmonyPatch(typeof(scnEditor), "DrawHolds", new[] { typeof(bool) })]
		public static class EditorDrawHoldsDuplicateSkipPatch
		{
			[HarmonyPrefix]
			public static bool Prefix()
			{
				if (!Main.Settings.optimizer.enableEditorFloorOptimization) return true;
				if (!Main.Settings.optimizer.incrementalFloorInsert) return true;
				// scnGame.RemakePath already called levelMaker.DrawHolds(false); this
				// second pass is a redundant full rebuild
				return !_incrementalMode;
			}
		}

		[IriPatch(Path = "optimizer/editorFloor/insert", Pre = typeof(OptimizerSettings), Condition = "enableEditorFloorOptimization,incrementalFloorInsert")]
		[HarmonyPatch(typeof(scnEditor), "DrawMultiPlanet", new Type[0])]
		public static class EditorDrawMultiPlanetDuplicateSkipPatch
		{
			[HarmonyPrefix]
			public static bool Prefix()
			{
				if (!Main.Settings.optimizer.enableEditorFloorOptimization) return true;
				if (!Main.Settings.optimizer.incrementalFloorInsert) return true;
				// Same: DrawMultiPlanet already scanned everything in scnGame.RemakePath
				return !_incrementalMode;
			}
		}

		[IriPatch(Path = "optimizer/editorFloor/insert", Pre = typeof(OptimizerSettings), Condition = "enableEditorFloorOptimization,incrementalFloorInsert")]
		[HarmonyPatch(typeof(scrLevelMaker), "DrawHolds", new[] { typeof(bool) })]
		public static class LevelMakerDrawHoldsNoHoldsSkipPatch
		{
			[HarmonyPrefix]
			public static bool Prefix(scrLevelMaker __instance)
			{
				if (!Main.Settings.optimizer.enableEditorFloorOptimization) return true;
				if (!Main.Settings.optimizer.incrementalFloorInsert) return true;
				if (!_incrementalMode) return true;

				var floors = __instance.listFloors;
				if (floors == null) return true;

				// Old hold objects are still present: the original cleanup must run
				_holdGOsRef ??= AccessTools.FieldRefAccess<scrLevelMaker, List<GameObject>>("holdGOs");
				var holdGOs = _holdGOsRef(__instance);
				if (holdGOs != null && holdGOs.Count > 0) return true;

				// Rebuilding the Hold container is a no-op when no floor has a hold
				if (AnyFloorsHaveHolds(floors)) return true;
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
