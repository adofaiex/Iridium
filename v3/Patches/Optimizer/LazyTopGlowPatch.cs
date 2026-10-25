using System;
using System.Collections.Generic;
using ADOFAI;
using HarmonyLib;
using Iridium.Config;
using UnityEngine;

namespace Iridium.Patches.Optimizer
{
	/// <summary>Spawns scrFloor.topGlow on demand: floors that never glow share one hidden dummy SpriteRenderer and get a real one on first use.</summary>
	public static class LazyTopGlowPatch
	{
		private static AccessTools.FieldRef<scrFloor, scrController>? _controllerRef;
		private static AccessTools.FieldRef<scrFloor, scrConductor>? _conductorRef;
		private static AccessTools.FieldRef<scrFloor, RDConstants>? _gcRef;
		private static AccessTools.FieldRef<scrFloor, scrVfx>? _vfxRef;
		private static AccessTools.FieldRef<scrFloor, scrVfxPlus>? _vfxPlusRef;
		private static bool _refsInitialized;

		private static SpriteRenderer? _dummy;

		private static bool Enabled =>
			Main.Settings.optimizer.optimizeLargeLevelLoading;

		private static bool InitRefs()
		{
			if (_refsInitialized) return _controllerRef != null;
			_refsInitialized = true;
			try
			{
				_controllerRef = AccessTools.FieldRefAccess<scrFloor, scrController>("controller");
				_conductorRef = AccessTools.FieldRefAccess<scrFloor, scrConductor>("conductor");
				_gcRef = AccessTools.FieldRefAccess<scrFloor, RDConstants>("gc");
				_vfxRef = AccessTools.FieldRefAccess<scrFloor, scrVfx>("vfx");
				_vfxPlusRef = AccessTools.FieldRefAccess<scrFloor, scrVfxPlus>("vfxPlus");
			}
			catch (Exception e)
			{
				Main.Logger?.Log($"[LazyTopGlow] field refs failed: {e.Message}");
			}
			return _controllerRef != null && _conductorRef != null && _gcRef != null && _vfxRef != null;
		}

		/// <summary>Shared stand-in: hidden and disabled, so stray SetActive/color writes stay harmless.</summary>
		internal static SpriteRenderer Dummy
		{
			get
			{
				if (_dummy == null)
				{
					var go = new GameObject("Iridium_TopGlowDummy");
					go.hideFlags = HideFlags.HideAndDontSave;
					go.SetActive(false);
					_dummy = go.AddComponent<SpriteRenderer>();
					_dummy.enabled = false; // belt and braces: renders nothing even if something activates the object
				}
				return _dummy;
			}
		}

		internal static bool IsDummy(SpriteRenderer? sr) => sr != null && ReferenceEquals(sr, _dummy);

		/// <summary>Materialize this floor's own topGlow, mirroring scrFloor.Awake.</summary>
		internal static SpriteRenderer Create(scrFloor floor)
		{
			var prefab = RDConstants.data.prefab_topGlow;
			var go = UnityEngine.Object.Instantiate(prefab, floor.transform);
			go.transform.SetParent(floor.transform);
			go.name = "topGlow";
			var sr = go.GetComponent<SpriteRenderer>();
			sr.transform.ScaleXY(floor.dontChangeMySprite ? 0.32f : 1.28f);
			var toggle = go.GetComponent<ToggleVisible>();
			if (toggle != null) toggle.scriptToToggle = floor;
			floor.topGlow = sr;
			return sr;
		}

		/// <summary>Return the real topGlow, replacing a dummy or a null one.</summary>
		internal static SpriteRenderer Ensure(scrFloor floor)
		{
			if (floor.topGlow != null && !IsDummy(floor.topGlow)) return floor.topGlow;
			floor.topGlow = null;
			return Create(floor);
		}

		/// <summary>The parts of scrFloor.Awake that must run; FloorRenderer.Awake is called manually and re-invoked idempotently on activation.</summary>
		internal static bool TryInitialize(scrFloor floor, bool callComponentAwake)
		{
			if (floor == null || !InitRefs()) return false;
			try
			{
				var controller = scrController.instance;
				var conductor = scrConductor.instance;
				var gc = RDConstants.data;
				var vfx = scrVfx.instance;
				var vfxPlus = scrVfxPlus.instance;

				_controllerRef!(floor) = controller;
				_conductorRef!(floor) = conductor;
				_gcRef!(floor) = gc;
				_vfxRef!(floor) = vfx;
				if (_vfxPlusRef != null) _vfxPlusRef(floor) = vfxPlus;

				var t = floor.transform;
				floor.thisTransform = t;
				if (!callComponentAwake)
				{
					floor.startPos = t.position;
					floor.startRot = t.rotation.eulerAngles;
					floor.tweenRot = floor.startRot;
				}
				// The chunked path skips these; MakeLevel's finishing loop rewrites them for every floor anyway.

				if (floor.floorRenderer == null)
				{
					floor.floorRenderer = floor.GetComponent<FloorRenderer>();
					if (floor.floorRenderer == null)
					{
						floor.floorRenderer = floor.gameObject.AddComponent<FloorSpriteRenderer>();
						floor.floorRenderer.renderer = floor.GetComponent<SpriteRenderer>();
						floor.legacyFloorSpriteRenderer = floor.GetComponent<SpriteRenderer>();
					}
				}
				if (callComponentAwake && floor.floorRenderer != null)
				{
					// Virtual dispatch to FloorMeshRenderer/FloorSpriteRenderer.Awake; Unity repeats it on activation, both are idempotent.
					floor.floorRenderer.Awake();
				}
				if (floor.setHitsound == null)
				{
					floor.setHitsound = floor.GetComponent<ffxSetHitsound>();
				}
				if (controller.gameworld && floor.bottomGlow != null)
					floor.bottomGlow.gameObject.SetActive(false);

				// topGlow：播放世界/需要常亮时立即创建，否则先挂共享占位。
				bool needsGlow = controller.gameworld
					|| vfx.tileFlashStyle == TileFlashStyle.AlwaysOn
					|| floor.hasLit;
				if (needsGlow)
					Create(floor);
				else
					floor.topGlow = Dummy;

				if (controller.gameworld && !ADOBase.isScnGame)
					floor.stickToFloor = controller.stickToFloor;
				conductor.onBeats.Add(floor);
				return true;
			}
			catch (Exception e)
			{
				Main.Logger?.Log($"[LazyTopGlow] init failed: {e}");
				return false;
			}
		}

		#region scrFloor.Awake replacement

		[IriPatch(Path = "loading/largeLevel", Pre = typeof(OptimizerSettings), Condition = "optimizeLargeLevelLoading")]
		[HarmonyPatch(typeof(scrFloor), nameof(scrFloor.Awake))]
		public static class AwakePatch
		{
			[HarmonyPrefix]
			public static bool Prefix(scrFloor __instance)
			{
				if (!Enabled || !InitRefs()) return true;
				// B 的未激活克隆已经 FastInit 过：激活时跳过，避免重复 topGlow/onBeats。
				if (ChunkedFloorSpawnPatch.IsInitialized(__instance)) return false;

				if (!TryInitialize(__instance, callComponentAwake: false)) return true;
				ChunkedFloorSpawnPatch.MarkInitialized(__instance);
				return false;
			}
		}

		#endregion

		#region Ensure at access / activation sites

		[IriPatch(Path = "loading/largeLevel", Pre = typeof(OptimizerSettings), Condition = "optimizeLargeLevelLoading")]
		[HarmonyPatch(typeof(scrFloor), nameof(scrFloor.Reset))]
		public static class ResetPatch
		{
			[HarmonyPrefix]
			public static void Prefix(scrFloor __instance)
			{
				if (!Enabled) return;
				// Vanilla Reset destroys topGlow then calls Awake again, so the initialized marker must go first.
				ChunkedFloorSpawnPatch.UnmarkInitialized(__instance);
				if (IsDummy(__instance.topGlow)) __instance.topGlow = null; // shared dummy, vanilla would Destroy it
			}
		}

		[IriPatch(Path = "loading/largeLevel", Pre = typeof(OptimizerSettings), Condition = "optimizeLargeLevelLoading")]
		[HarmonyPatch(typeof(scrFloor), nameof(scrFloor.Start))]
		public static class StartPatch
		{
			[HarmonyPrefix]
			public static void Prefix(scrFloor __instance)
			{
				if (!Enabled) return;
				if (!IsDummy(__instance.topGlow)) return;

				// 对齐 Start 里的 active 判定；只有真的要显示发光才实体化。
				var vfx = scrVfx.instance;
				bool active = vfx.tileFlashStyle == TileFlashStyle.AlwaysOn
					|| (__instance.hasLit && vfx.tileFlashStyle != TileFlashStyle.AlwaysBlack);
				if (active) Ensure(__instance);
			}
		}

		[IriPatch(Path = "loading/largeLevel", Pre = typeof(OptimizerSettings), Condition = "optimizeLargeLevelLoading")]
		[HarmonyPatch(typeof(scrFloor), nameof(scrFloor.LightUp))]
		public static class LightUpPatch
		{
			[HarmonyPrefix]
			public static void Prefix(scrFloor __instance)
			{
				if (!Enabled) return;
				if (IsDummy(__instance.topGlow)) Ensure(__instance);
			}
		}

		[IriPatch(Path = "loading/largeLevel", Pre = typeof(OptimizerSettings), Condition = "optimizeLargeLevelLoading")]
		[HarmonyPatch(typeof(scrFloor), nameof(scrFloor.SetToRandomColor))]
		public static class RandomColorPatch
		{
			[HarmonyPrefix]
			public static void Prefix(scrFloor __instance)
			{
				if (!Enabled) return;
				if (IsDummy(__instance.topGlow)) Ensure(__instance);
			}
		}

		/// <summary>Materialize topGlows for the lit prefix, which vanilla SetActive(true)s on the shared dummy otherwise.</summary>
		[IriPatch(Path = "loading/largeLevel", Pre = typeof(OptimizerSettings), Condition = "optimizeLargeLevelLoading")]
		[HarmonyPatch(typeof(scnGame), nameof(scnGame.FinishCustomLevelLoading))]
		public static class FinishLoadingPatch
		{
			[HarmonyPrefix]
			public static void Prefix(scnGame __instance, int seqID)
			{
				if (!Enabled) return;
				try
				{
					var lm = scrLevelMaker.instance;
					var floors = lm != null ? lm.listFloors : null;
					if (floors == null) return;
					for (int i = 0; i < floors.Count; i++)
					{
						var floor = floors[i];
						if (floor == null || floor.seqID > seqID) break;
						if (IsDummy(floor.topGlow)) Ensure(floor);
					}
				}
				catch (Exception e)
				{
					Main.Logger?.Log($"[LazyTopGlow] FinishCustomLevelLoading failed: {e.Message}");
				}
			}
		}

		/// <summary>Object decorations enabling their floor glow need a real topGlow too.</summary>
		[IriPatch(Path = "loading/largeLevel", Pre = typeof(OptimizerSettings), Condition = "optimizeLargeLevelLoading")]
		[HarmonyPatch(typeof(scrObjectDecoration), nameof(scrObjectDecoration.SetFloorGlowEnabled))]
		public static class ObjectDecorationGlowPatch
		{
			[HarmonyPrefix]
			public static void Prefix(scrObjectDecoration __instance, bool enable)
			{
				if (!Enabled || !enable) return;
				try
				{
					if (__instance.floor != null && IsDummy(__instance.floor.topGlow))
						Ensure(__instance.floor);
				}
				catch (Exception e)
				{
					Main.Logger?.Log($"[LazyTopGlow] SetFloorGlowEnabled failed: {e.Message}");
				}
			}
		}

		#endregion
	}
}
