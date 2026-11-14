using System;
using ADOFAI;
using HarmonyLib;
using Iridium.Config;
using UnityEngine;

namespace Iridium.Patches.Optimizer
{
	/// <summary>Strips the scrLetterPress Num object off the meshFloor prefab for huge levels and clones it back per floor only when the number becomes visible.</summary>
	public static class LazyFloorNumberPatch
	{
		/// <summary>Only strip above this floor count; small levels keep vanilla behaviour to ease debugging.</summary>
		private const int FloorThreshold = 20000;

		private static AccessTools.FieldRef<scrLevelMaker, GameObject>? _meshFloorRef;

		private static GameObject? _strippedPrefab;
		private static GameObject? _numTemplate;
		private static scrLetterPress? _numDummy;
		private static bool _templateFailed;

		private static bool Enabled =>
			Main.Settings.optimizer.optimizeLargeLevelLoading;

		private static bool InitRefs()
		{
			if (_meshFloorRef != null) return true;
			try
			{
				_meshFloorRef = AccessTools.FieldRefAccess<scrLevelMaker, GameObject>("meshFloor");
			}
			catch (Exception e)
			{
				Main.Logger?.Log($"[LazyFloorNumber] meshFloor ref failed: {e.Message}");
			}
			return _meshFloorRef != null;
		}

		private static void PrepareTemplate(scrLevelMaker lm)
		{
			if (_strippedPrefab != null || _templateFailed) return;
			try
			{
				var prefab = _meshFloorRef!(lm);
				if (prefab == null) { _templateFailed = true; return; }

				// 模板一：用于批量实例化（剥掉 Num）
				var stripped = UnityEngine.Object.Instantiate(prefab);
				stripped.name = "Iridium_StrippedFloor";
				// Instantiate copies hideFlags; HideAndDontSave here would leak DontSave onto every cloned floor.
				stripped.hideFlags = HideFlags.None;
				UnityEngine.Object.DontDestroyOnLoad(stripped);

				var strippedFloor = stripped.GetComponent<scrFloor>();
				var numOnTemplate = strippedFloor != null ? strippedFloor.editorNumText : null;
				if (numOnTemplate == null)
				{
					// prefab 结构不符合预期：放弃剥离
					UnityEngine.Object.Destroy(stripped);
					_templateFailed = true;
					return;
				}

				// 把 Num 从模板上摘下来，保留为按需克隆的原型
				var numGo = numOnTemplate.gameObject;
				numGo.transform.SetParent(null, false);
				numGo.SetActive(false);
				numGo.hideFlags = HideFlags.None;
				UnityEngine.Object.DontDestroyOnLoad(numGo);
				_numTemplate = numGo;

				strippedFloor.editorNumText = null!;
				_strippedPrefab = stripped;

				// Park it out of view, or it renders as an extra floor at the prefab origin.
				stripped.transform.position = new Vector3(0f, -100000f, 0f);
			}
			catch (Exception e)
			{
				Main.Logger?.Log($"[LazyFloorNumber] template prepare failed: {e}");
				_templateFailed = true;
			}
		}

		private static bool IsDummy(scrLetterPress? text) => text != null && ReferenceEquals(text, _numDummy);

		/// <summary>Give this floor its own number text; only call when it is actually visible.</summary>
		internal static void EnsureNumText(scrFloor floor)
		{
			if (floor == null || floor.editorNumText != null) return;
			if (_numTemplate == null) return;
			try
			{
				var go = UnityEngine.Object.Instantiate(_numTemplate, floor.transform);
				go.name = _numTemplate.name;
				go.hideFlags = HideFlags.None;
				var press = go.GetComponent<scrLetterPress>();
				floor.editorNumText = press;
				if (press != null && press.letterText != null)
					press.letterText.text = floor.seqID.ToString();
			}
			catch (Exception e)
			{
				Main.Logger?.Log($"[LazyFloorNumber] create num failed: {e.Message}");
			}
		}

		/// <summary>Called by the chunked spawner: ensure the stripped template exists and return it.</summary>
		internal static GameObject? EnsureStrippedTemplate(scrLevelMaker lm)
		{
			if (!InitRefs()) return null;
			PrepareTemplate(lm);
			return _strippedPrefab;
		}

		/// <summary>True when the chunked spawner owns template swapping.</summary>
		internal static bool HandoverToChunkedSpawn => ChunkedFloorSpawnPatch.Enabled;

		#region 批量实例化模板替换

		[IriPatch(Path = "loading/largeLevel", Pre = typeof(OptimizerSettings), Condition = "optimizeLargeLevelLoading")]
		[HarmonyPatch(typeof(scrLevelMaker), nameof(scrLevelMaker.InstantiateFloatFloors))]
		public static class StrippedPrefabPatch
		{
			[HarmonyPrefix]
			public static void Prefix(scrLevelMaker __instance, out GameObject? __state)
			{
				__state = null;
				if (!Enabled || !InitRefs()) return;
				// B（分帧激活）接管时由它统一替换模板并处理未激活克隆
				if (HandoverToChunkedSpawn) return;
				if (__instance.floorAngles == null || __instance.floorAngles.Length < FloorThreshold) return;

				PrepareTemplate(__instance);
				if (_strippedPrefab == null) return;

				var original = _meshFloorRef!(__instance);
				if (original == null || original == _strippedPrefab) return;
				__state = original;
				_meshFloorRef(__instance) = _strippedPrefab;
			}

			[HarmonyPostfix]
			public static void Postfix(scrLevelMaker __instance, GameObject? __state)
			{
				if (__state != null) _meshFloorRef!(__instance) = __state;
			}

			[HarmonyFinalizer]
			public static Exception? Finalizer(scrLevelMaker __instance, GameObject? __state, Exception? __exception)
			{
				if (__state != null) _meshFloorRef!(__instance) = __state;
				return __exception;
			}
		}

		#endregion

		#region 按需创建 / 空引用防护

		/// <summary>砖块进入视野时创建编号文本（原版此处已判空，这里只负责补建）。</summary>
		[IriPatch(Path = "loading/largeLevel", Pre = typeof(OptimizerSettings), Condition = "optimizeLargeLevelLoading")]
		[HarmonyPatch(typeof(scrFloor), "OnBecameVisible")]
		public static class OnBecameVisiblePatch
		{
			[HarmonyPrefix]
			public static void Prefix(scrFloor __instance)
			{
				if (!Enabled || !ADOBase.isLevelEditor) return;
				if (__instance == null || __instance.isFake) return;
				if (__instance.editorNumText == null || IsDummy(__instance.editorNumText))
					EnsureNumText(__instance);
			}
		}

		/// <summary>DrawFloorNums 原版对 editorNumText 不判空；改为只处理已存在的编号对象。</summary>
		[IriPatch(Path = "loading/largeLevel", Pre = typeof(OptimizerSettings), Condition = "optimizeLargeLevelLoading")]
		[HarmonyPatch(typeof(scnEditor), "DrawFloorNums")]
		public static class DrawFloorNumsPatch
		{
			[HarmonyPrefix]
			public static bool Prefix(scnEditor __instance)
			{
				if (!Enabled) return true;

				try
				{
					var floors = __instance.floors;
					if (floors == null) return false;

					bool showNums = __instance.showFloorNums && !__instance.playMode;
					for (int i = 0; i < floors.Count; i++)
					{
						var floor = floors[i];
						if (floor == null || !floor.enabled) continue;
						// B（分帧激活）期间未激活的砖块不创建编号，等激活后由 OnBecameVisible 补建
						if (!floor.gameObject.activeInHierarchy) continue;

						if (showNums && !floor.isFake)
						{
							if (floor.editorNumText == null || IsDummy(floor.editorNumText))
								EnsureNumText(floor);
							if (floor.editorNumText != null)
							{
								if (floor.editorNumText.letterText != null)
									floor.editorNumText.letterText.text = floor.seqID.ToString();
								floor.editorNumText.gameObject.SetActive(true);
							}
						}
						else if (floor.editorNumText != null)
						{
							floor.editorNumText.gameObject.SetActive(false);
						}
					}
					return false;
				}
				catch (Exception e)
				{
					Main.Logger?.Log($"[LazyFloorNumber] DrawFloorNums failed: {e.Message}");
					return true;
				}
			}
		}

		/// <summary>Vanilla Play hides every number without a null check; borrow the shared dummy so no per-floor object is created.</summary>
		[IriPatch(Path = "loading/largeLevel", Pre = typeof(OptimizerSettings), Condition = "optimizeLargeLevelLoading")]
		[HarmonyPatch(typeof(scnEditor), nameof(scnEditor.Play))]
		public static class PlayPatch
		{
			[HarmonyPrefix]
			public static void Prefix(scnEditor __instance)
			{
				if (!Enabled) return;
				try
				{
					_numDummy = GetOrCreateDummy();
					var floors = __instance.floors;
					if (floors == null) return;
					for (int i = 0; i < floors.Count; i++)
					{
						var floor = floors[i];
						if (floor != null && floor.editorNumText == null)
							floor.editorNumText = _numDummy;
					}
				}
				catch (Exception e)
				{
					Main.Logger?.Log($"[LazyFloorNumber] Play prefix failed: {e.Message}");
				}
			}

			[HarmonyPostfix]
			public static void Postfix(scnEditor __instance)
			{
				if (!Enabled || _numDummy == null) return;
				try
				{
					var floors = __instance.floors;
					if (floors == null) return;
					for (int i = 0; i < floors.Count; i++)
					{
						var floor = floors[i];
						if (floor != null && ReferenceEquals(floor.editorNumText, _numDummy))
							floor.editorNumText = null!;
					}
				}
				catch (Exception e)
				{
					Main.Logger?.Log($"[LazyFloorNumber] Play postfix failed: {e.Message}");
				}
			}

			private static scrLetterPress GetOrCreateDummy()
			{
				if (_numDummy != null) return _numDummy;
				var go = new GameObject("Iridium_NumDummy");
				go.hideFlags = HideFlags.HideAndDontSave;
				go.SetActive(false);
				var press = go.AddComponent<scrLetterPress>();
				var text = go.AddComponent<UnityEngine.UI.Text>();
				press.letterText = text;
				_numDummy = press;
				return press;
			}
		}

		#endregion
	}
}
