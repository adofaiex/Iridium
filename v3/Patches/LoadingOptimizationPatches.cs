using Iridium.Config;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;
using ADOFAI;
using DG.Tweening;
using Iridium.UI;

namespace Iridium.Patches
{
	public static class LoadingOptimizationPatches
	{
		#region Shared State

		private static bool _isBatchCreating = false;

		#endregion

		#region Frame-Spread Decoration Loading

		[IriPatch(Path = "optimizer/loading", Pre = typeof(OptimizerSettings), Condition = "enableOptimizer,frameSpreadDecorationLoading")]
		[HarmonyPatch(typeof(scnGame), nameof(scnGame.UpdateDecorationObjects))]
		public static class FrameSpreadDecorationLoadingPatch
		{
			private static readonly Queue<LevelEvent> _pendingDecorations = new();
			private static bool _isLoading = false;
			private static readonly List<GraphicRaycaster> _disabledRaycasters = new();
			private static bool _cancelled = false;
			private static scnGame? _pendingGame;
			private static bool _playWasBlocked;
			private static bool _uiCompleted;
			// Set by ReloadAssets when a full decoration rebuild is about to happen
			// (level load). Editor-driven incremental updates call
			// UpdateDecorationObjects directly without ReloadAssets, so they must
			// NOT be intercepted or every tile edit re-triggers a frame-spread load.
			private static bool _shouldFrameSpread;

			public static bool IsLoading => _isLoading;

			private const float TIME_BUDGET_PER_FRAME = 0.012f;

			[IriPatch(Path = "optimizer/loading", Pre = typeof(OptimizerSettings), Condition = "enableOptimizer,frameSpreadDecorationLoading")]
			[HarmonyPatch(typeof(scnGame), nameof(scnGame.ReloadAssets))]
			public static class ReloadAssets_Patch
			{
				[HarmonyPostfix]
				public static void Postfix(bool reloadDecorations)
				{
					// ReloadAssets(reloadDecorations: false) means the caller rebuilds
					// decorations manually right after — that is the level-load path.
					if (!reloadDecorations)
						_shouldFrameSpread = true;
				}
			}

			[HarmonyPrefix]
			public static bool Prefix(scnGame __instance, bool reloadDecorations)
			{
				if (!Main.Settings.optimizer.enableOptimizer || !Main.Settings.optimizer.frameSpreadDecorationLoading)
					return true;

				if (_isLoading) return false;

				if (ADOBase.isOfficialLevel) return true;

				if (!reloadDecorations) return true;

				// Only intercept when ReloadAssets flagged this as a level load; direct
				// editor calls (tile edits, undo/redo) are left untouched.
				if (!_shouldFrameSpread)
					return true;
				_shouldFrameSpread = false;

				try
				{
					var decorations = __instance.decorations;
					if (decorations == null || decorations.Count == 0)
						return true;

					int totalActive = 0;
					foreach (var dec in decorations)
					{
						if (dec.active) totalActive++;
					}

					if (totalActive < 100)
						return true;

					Main.Logger?.Log($"[LoadingOptimization] Frame-spread loading {totalActive} decorations ({decorations.Count} total)");

					_isLoading = true;
					_pendingDecorations.Clear();
					_disabledRaycasters.Clear();

					foreach (var dec in decorations)
					{
						if (dec.active)
							_pendingDecorations.Enqueue(dec);
					}

					BlockUIInput();

					_pendingGame = __instance;
					__instance.StartCoroutine(FrameSpreadLoadCoroutine(__instance));
					return false;
				}
				catch (System.Exception ex)
				{
					Main.Logger?.Error($"[LoadingOptimization] FrameSpreadDecorationLoading failed: {ex}");
					CleanupState();
					return true;
				}
			}

			private static void BlockUIInput()
			{
				try
				{
					var canvases = Resources.FindObjectsOfTypeAll<Canvas>();
					foreach (var canvas in canvases)
					{
						var raycaster = canvas.GetComponent<GraphicRaycaster>();
						// Keep the VRAM notification's raycaster alive so the Stop
						// button stays clickable during frame-spread loading.
						if (raycaster == null || !raycaster.enabled) continue;
						if (raycaster == Iridium.UI.VRAMNotificationUI.InstanceRaycaster) continue;
						raycaster.enabled = false;
						_disabledRaycasters.Add(raycaster);
					}
					Main.Logger?.Log($"[LoadingOptimization] Blocked UI input: disabled {_disabledRaycasters.Count} raycaster(s)");
				}
				catch (Exception ex)
				{
					Main.Logger?.Error($"[LoadingOptimization] Failed to block UI input: {ex}");
				}
			}

			private static void RestoreUIInput()
			{
				try
				{
					foreach (var raycaster in _disabledRaycasters)
					{
						if (raycaster != null)
							raycaster.enabled = true;
					}
					_disabledRaycasters.Clear();
				}
				catch (Exception ex)
				{
					Main.Logger?.Error($"[LoadingOptimization] Failed to restore UI input: {ex}");
				}
			}

			public static void Cancel()
			{
				_cancelled = true;
				Iridium.UI.VRAMNotificationUI.Complete(forceImmediate: true);
			}

			private static System.Collections.IEnumerator FrameSpreadLoadCoroutine(scnGame instance)
			{
				int maxPerFrame = Main.Settings.optimizer.decorationsPerFrame;
				if (maxPerFrame < 1) maxPerFrame = 50;

				if (instance == null || instance.decManager == null)
				{
					CleanupState();
					yield break;
				}

				instance.decManager.ClearDecorations();

				int processed = 0;
				int total = _pendingDecorations.Count;

				Iridium.UI.VRAMNotificationUI.ShowPersistent(Localization.Get("LoadingDecorationsProgress", 0, total));
				Main.Logger?.Log($"[LoadingOptimization] Starting frame-spread loading: {total} decorations");

				while (_pendingDecorations.Count > 0 && !_cancelled)
				{
					if (instance == null || instance.decManager == null)
					{
						Main.Logger?.Log($"[LoadingOptimization] scnGame destroyed during loading, aborting");
						CleanupState();
						yield break;
					}

					float frameStart = Time.realtimeSinceStartup;
					int batchLimit = Mathf.Min(maxPerFrame, _pendingDecorations.Count);

					for (int i = 0; i < batchLimit && _pendingDecorations.Count > 0 && !_cancelled; i++)
					{
						var ev = _pendingDecorations.Dequeue();
						try
						{
							bool spritesLoaded = false;
							instance.decManager.CreateDecoration(ev, out spritesLoaded);
						}
						catch (System.Exception ex)
						{
							Main.Logger?.Error($"[LoadingOptimization] Failed to create decoration: {ex}");
						}
						processed++;

						if (Time.realtimeSinceStartup - frameStart > TIME_BUDGET_PER_FRAME)
							break;
					}

					if (_pendingDecorations.Count > 0 && !_cancelled)
					{
						Iridium.UI.VRAMNotificationUI.UpdateProgress(Localization.Get("LoadingDecorationsProgress", processed, total));
						yield return null;
					}
				}

				if (_cancelled)
				{
					Main.Logger?.Log($"[LoadingOptimization] Loading cancelled by user");
					CleanupState();
					yield break;
				}

				var moveDecImages = new List<(string name, string path)>();
				foreach (var evt in instance.events)
				{
					if (evt.eventType != LevelEventType.MoveDecorations) continue;
					try
					{
						string? output2 = null;
						if (evt.TryGetAndSet("decorationImage", ref output2, onlyIfEnabled: true) && !output2.IsNullOrEmpty())
						{
							string filePath2 = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(instance.levelPath), output2!);
							moveDecImages.Add((output2!, filePath2));
						}
					}
					catch (Exception ex)
					{
						Main.Logger?.Error($"[LoadingOptimization] Failed to collect MoveDecoration image: {ex}");
					}
				}

				if (_cancelled)
				{
					Main.Logger?.Log($"[LoadingOptimization] Loading cancelled by user");
					CleanupState();
					yield break;
				}

				if (moveDecImages.Count > 0)
				{
					total += moveDecImages.Count;
					for (int i = 0; i < moveDecImages.Count; i++)
					{
						if (_cancelled)
						{
							Main.Logger?.Log($"[LoadingOptimization] Loading cancelled by user");
							Iridium.UI.VRAMNotificationUI.Show(Localization.Get("LoadingDecorationsProgress", processed, total));
							_uiCompleted = true;
							CleanupState();
							yield break;
						}
						var (name, path) = moveDecImages[i];
						Iridium.UI.VRAMNotificationUI.UpdateProgress(Localization.Get("LoadingDecorationsProgress", processed, total));
						try
						{
							LoadResult status;
							instance.imgHolder.GetOrAddSprite(name, path, out status);
							if (ADOBase.editor != null)
								ADOBase.editor.UpdateImageLoadResult(name, status);
						}
						catch (System.Exception ex)
						{
							Main.Logger?.Error($"[LoadingOptimization] Failed to load MoveDecoration image: {ex}");
						}
						processed++;
						yield return null;
					}
				}

				Main.Logger?.Log($"[LoadingOptimization] Finished loading {processed} decorations across multiple frames");

				if (!Main.Settings.optimizer.dontShowSavedMemory)
				{
					if (Iridium.Patches.Optimizer.OptimizerShared.savedVRAM_MB > 0.1f)
					{
						Iridium.UI.VRAMNotificationUI.Show(Localization.Get("SavedMemoryMsg", Iridium.Patches.Optimizer.OptimizerShared.savedVRAM_MB.ToString("F2")));
						Main.Logger?.Log(Localization.Get("SavedMemoryLog", Iridium.Patches.Optimizer.OptimizerShared.savedVRAM_MB.ToString("F2")));
					}
					else
					{
						Iridium.UI.VRAMNotificationUI.Show(Localization.Get("LoadingDecorationsProgress", processed, total));
					}
					_uiCompleted = true;
					Iridium.Patches.Optimizer.VRAMNotificationPatch.isFinished = true;
				}
				else
				{
					Iridium.UI.VRAMNotificationUI.Show(Localization.Get("LoadingDecorationsProgress", processed, total));
					_uiCompleted = true;
				}

				var gameToPlay = (_pendingGame != null && _playWasBlocked) ? _pendingGame : null;
				CleanupState();
				if (instance != null && instance.decManager != null)
					instance.decManager.ResetDecorations();
				gameToPlay?.Play();
			}

			[IriPatch(Path = "optimizer/loading", Pre = typeof(OptimizerSettings), Condition = "enableOptimizer,frameSpreadDecorationLoading")]
			[HarmonyPatch(typeof(scrDecorationManager), nameof(scrDecorationManager.ResetDecorations))]
			public static class ResetDecorations_Patch
			{
				[HarmonyPrefix]
				public static bool Prefix()
				{
					if (_isLoading) return false;
					return true;
				}
			}

			[IriPatch(Path = "optimizer/loading", Pre = typeof(OptimizerSettings), Condition = "enableOptimizer,frameSpreadDecorationLoading")]
			[HarmonyPatch(typeof(scnGame), nameof(scnGame.Play),
				new Type[] { typeof(int), typeof(bool) })]
			public static class Play_Patch
			{
				[HarmonyPrefix]
				public static bool Prefix()
				{
					if (_isLoading)
					{
						_playWasBlocked = true;
						return false;
					}
					return true;
				}
			}

			private static void CleanupState()
			{
				_isLoading = false;
				_cancelled = false;
				_pendingGame = null;
				_playWasBlocked = false;
				_shouldFrameSpread = false;
				_pendingDecorations.Clear();
				RestoreUIInput();
				if (!_uiCompleted)
					Iridium.UI.VRAMNotificationUI.Complete();
				_uiCompleted = false;
			}
		}

		#endregion

		#region Cleanup

		[IriPatch(Path = "optimizer/loading", Pre = typeof(OptimizerSettings), Condition = "enableOptimizer")]
		[HarmonyPatch(typeof(scnGame), "OnDestroy")]
		public static class LoadingOptimizationCleanupPatch
		{
			[HarmonyPostfix]
			public static void Postfix()
			{
				if (!Main.Settings.optimizer.enableOptimizer) return;

				_isBatchCreating = false;

				Main.Logger?.Log("[LoadingOptimization] Cleaned up caches and pools");
			}
		}

		#endregion

		#region Utility Methods

		public static bool IsBatchCreating => _isBatchCreating;

		#endregion
	}
}
