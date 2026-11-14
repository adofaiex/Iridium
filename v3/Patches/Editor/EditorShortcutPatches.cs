using Iridium.Config;
using System;
using DG.Tweening;
using HarmonyLib;
using UnityEngine;
using Iridium;

namespace Iridium.Patches.Editor
{
	/// <summary>Editor keyboard shortcuts, ported from ADOFAI.EditorTweaks.</summary>
	public static class EditorShortcutPatches
	{
		[HarmonyPatch(typeof(scnEditor), "Update")]
		[IriPatch(Path = "editor/shortcuts", Pre = typeof(EditorShortcutSettings), Condition = "enableEditorShortcuts")]
		public static class EditorShortcutUpdatePatch
		{
			private static bool _attachLogged;

			[HarmonyPostfix]
			public static void Postfix(scnEditor __instance)
			{
				var s = Main.Settings.editorShortcuts;

				if (!_attachLogged)
				{
					_attachLogged = true;
					Main.Logger?.Log(
						$"[EditorShortcuts] postfix attached. enabled={s.enableEditorShortcuts}, " +
						$"selectAll={s.selectAllKey}/{s.selectAllModifiers}, deselect={s.deselectAllKey}/{s.deselectAllModifiers}, " +
						 $"toggle={s.toggleVisibilityKey}/{s.toggleVisibilityModifiers}, focus={s.focusDecorationKey}/{s.focusDecorationModifiers}, " +
						$"goto={s.goToFloorKey}/{s.goToFloorModifiers}, allFloors={s.selectAllFloorsKey}/{s.selectAllFloorsModifiers}");
				}

				// Popup shortcuts (highest priority)
				if (__instance.popupPanel != null && __instance.popupPanel.activeSelf)
				{
					HandlePopupShortcuts(__instance, s);
					return;
				}

				// Don't fire shortcuts while editing an input field
				if (__instance.userIsEditingAnInputField) return;

				ProbeConfiguredKeys(s);
				HandleEditorShortcuts(__instance, s);
			}

			// Dump raw key + modifier state on every configured press, to tell an unbound key from a gated one.
			private static void ProbeConfiguredKeys(Config.EditorShortcutSettings s)
			{
				ProbeKey("selectAll", s.selectAllKey, s.selectAllModifiers);
				ProbeKey("deselect", s.deselectAllKey, s.deselectAllModifiers);
				ProbeKey("toggleVis", s.toggleVisibilityKey, s.toggleVisibilityModifiers);
				ProbeKey("focus", s.focusDecorationKey, s.focusDecorationModifiers);
				ProbeKey("goto", s.goToFloorKey, s.goToFloorModifiers);
				ProbeKey("allFloors", s.selectAllFloorsKey, s.selectAllFloorsModifiers);
			}

			private static void ProbeKey(string name, int key, int modifiers)
			{
				if (!Input.GetKeyDown((KeyCode)key)) return;
				Main.Logger?.Log(
					$"[EditorShortcuts] {name}: {(KeyCode)key}({key}) down; mods=0x{modifiers:X} " +
					$"ctrl={Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)} " +
					$"shift={Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)} " +
					$"alt={Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt)}");
			}
		}

		private static void HandlePopupShortcuts(scnEditor editor, Config.EditorShortcutSettings s)
		{
			if (editor.unsavedChangesPopupContainer == null ||
				!editor.unsavedChangesPopupContainer.activeSelf) return;

			if (CheckKey(s.popupSaveKey, s.popupSaveModifiers))
				editor.popupUnsavedChangesSave?.onClick?.Invoke();
			if (CheckKey(s.popupDiscardKey, s.popupDiscardModifiers))
				editor.popupUnsavedChangesDiscard?.onClick?.Invoke();
		}

		private static void HandleEditorShortcuts(scnEditor editor, Config.EditorShortcutSettings s)
		{
			if (CheckKey(s.selectAllKey, s.selectAllModifiers))
				SelectAllDecorations(editor);
			if (CheckKey(s.deselectAllKey, s.deselectAllModifiers))
				DeselectAllDecorations(editor);
			if (CheckKey(s.toggleVisibilityKey, s.toggleVisibilityModifiers))
				ToggleSelectedDecorationVisibility(editor);
			if (CheckKey(s.focusDecorationKey, s.focusDecorationModifiers))
				FocusSelectedDecoration(editor);
			if (CheckKey(s.goToFloorKey, s.goToFloorModifiers))
				GoToSelectedFloor(editor);
			if (CheckKey(s.selectAllFloorsKey, s.selectAllFloorsModifiers))
				SelectAllFloors(editor);
		}

		private static bool CheckKey(int key, int modifiers)
		{
			if (!Input.GetKeyDown((KeyCode)key)) return false;

			if ((modifiers & MOD_CTRL) != 0 &&
				!Input.GetKey(KeyCode.LeftControl) && !Input.GetKey(KeyCode.RightControl))
				return false;
			if ((modifiers & MOD_SHIFT) != 0 &&
				!Input.GetKey(KeyCode.LeftShift) && !Input.GetKey(KeyCode.RightShift))
				return false;
			if ((modifiers & MOD_ALT) != 0 &&
				!Input.GetKey(KeyCode.LeftAlt) && !Input.GetKey(KeyCode.RightAlt))
				return false;

			return true;
		}

		//  Actions

		private static void SelectAllDecorations(scnEditor editor)
		{
			try
			{
				foreach (var decoration in editor.decorations)
				{
					if (!editor.selectedDecorations.Contains(decoration))
					{
						editor.SelectDecoration(decoration,
							jumpToDecoration: false, showPanel: false, ignoreDeselection: true);
					}
				}
			}
			catch (Exception e)
			{
				Main.Logger?.Error($"[EditorShortcuts] SelectAll failed: {e.Message}");
			}
		}

		private static void DeselectAllDecorations(scnEditor editor)
		{
			try
			{
				editor.DeselectAllDecorations();
			}
			catch (Exception e)
			{
				Main.Logger?.Error($"[EditorShortcuts] DeselectAll failed: {e.Message}");
			}
		}

		private static void ToggleSelectedDecorationVisibility(scnEditor editor)
		{
			try
			{
				if (editor.selectedDecorations.Count == 0) return;
				foreach (var levelEvent in editor.selectedDecorations)
				{
					editor.ShowEvent(levelEvent, !levelEvent.visible);
				}
			}
			catch (Exception e)
			{
				Main.Logger?.Error($"[EditorShortcuts] ToggleVisibility failed: {e.Message}");
			}
		}

		private static void FocusSelectedDecoration(scnEditor editor)
		{
			try
			{
				if (editor.selectedDecorations.Count == 0) return;
				editor.GoToDecoration(editor.selectedDecorations[0]);
			}
			catch (Exception e)
			{
				Main.Logger?.Error($"[EditorShortcuts] FocusDecoration failed: {e.Message}");
			}
		}

		private static void GoToSelectedFloor(scnEditor editor)
		{
			try
			{
				if (editor.selectedFloors.Count == 0) return;
				var floor = editor.selectedFloors[0];
				if (floor == null) return;

				var cam = editor.camera;
				if (cam == null) return;

				cam.transform.DOKill();
				cam.transform.DOMove(floor.transform.position.WithZ(-10f), 0.4f)
					.SetUpdate(isIndependentUpdate: true)
					.SetEase(Ease.OutCubic);
			}
			catch (Exception e)
			{
				Main.Logger?.Error($"[EditorShortcuts] GoToFloor failed: {e.Message}");
			}
		}

		private static void SelectAllFloors(scnEditor editor)
		{
			try
			{
				var floors = editor.floors;
				if (floors == null || floors.Count == 0) return;

				// MultiSelectFloors selects a range from start to end
				editor.MultiSelectFloors(floors[0], floors[floors.Count - 1]);
			}
			catch (Exception e)
			{
				Main.Logger?.Error($"[EditorShortcuts] SelectAllFloors failed: {e.Message}");
			}
		}

		/// <summary>阻止 SelectFloor 的附带镜头跟随。不影响 GoToDecoration 等其他路径。</summary>
		[HarmonyPatch(typeof(scnEditor), "SelectFloor")]
		[IriPatch(Path = "editor/shortcuts", Pre = typeof(EditorShortcutSettings), Condition = "enableEditorShortcuts,cameraFollowOnFloorSelect")]
		public static class FloorSelectCameraJumpPatch
		{
			private static bool _reEntering;

			[HarmonyPrefix]
			public static bool Prefix(scnEditor __instance, scrFloor floorToSelect, bool cameraJump)
			{
				if (!Main.Settings.editorShortcuts.cameraFollowOnFloorSelect && cameraJump)
				{
					if (_reEntering) return true;
					_reEntering = true;
					__instance.SelectFloor(floorToSelect, false);
					_reEntering = false;
					return false;
				}
				return true;
			}
		}

		//  Modifier helpers

		public const int MOD_CTRL = 1;
		public const int MOD_SHIFT = 4;
		public const int MOD_ALT = 2;

		public static string GetKeyDisplay(int key, int mods)
		{
			string result = "";
			if ((mods & MOD_CTRL) != 0) result += "Ctrl+";
			if ((mods & MOD_ALT) != 0) result += "Alt+";
			if ((mods & MOD_SHIFT) != 0) result += "Shift+";
			result += ((KeyCode)key).ToString();
			return result;
		}

		public static int CycleModifier(int mods)
		{
			int[] sequence = { 0, 1, 5, 3, 7, 4, 2, 6 };
			int idx = Array.IndexOf(sequence, mods);
			if (idx < 0) idx = 0;
			return sequence[(idx + 1) % sequence.Length];
		}
	}
}
