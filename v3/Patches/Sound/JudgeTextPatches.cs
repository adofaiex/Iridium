using System;
using HarmonyLib;
using Iridium;
using Iridium.Config;
using TMPro;
using DG.Tweening;
using UnityEngine;

namespace Iridium.Patches.Sound
{
	public static class JudgeTextPatches
	{
		private static JudgeTextSettings Settings => Main.Settings.judgeText;

		// Captured missAngle from scrHitTextManager.ShowHitText (game doesn't forward it to Show in non-coop)
		private static float _capturedMissAngle;

		// 3.4.0 的 XPerfect 边框文字（旧版本无此字段，ref 为 null）。
		// 隐藏判定时需一并清空，否则边框仍显示原生 XPerfect 文本。
		private static readonly System.Reflection.FieldInfo? _xPerfectBorderField =
			AccessTools.Field(typeof(scrHitTextMesh), "xPerfectBorder");

		private static readonly AccessTools.FieldRef<scrHitTextMesh, TextMeshPro>? _xPerfectBorderRef =
			_xPerfectBorderField == null
				? null
				: AccessTools.FieldRefAccess<scrHitTextMesh, TextMeshPro>(_xPerfectBorderField);

		private static double CalculateTimingFromAngle(float angularOffset)
		{
			var controller = scrController.instance;
			var conductor = scrConductor.instance;
			if (controller == null || conductor == null) return 0;

			double bpm = conductor.bpm;
			double speed = controller.d_speed;
			double pitch = conductor.song.pitch;

			double standardTiming = angularOffset * (controller.playerOne.planetarySystem.isCW ? 1.0 : -1.0) * 60000.0 / (Math.PI * bpm * speed * pitch);

			return -standardTiming;
		}

		[HarmonyPatch(typeof(scrHitTextManager), "ShowHitText")]
		[IriPatch(Path = "sound/judgeTextRotation", AlwaysOn = true)]
		public static class HitTextManagerShowPatch
		{
			public static void Prefix(float missAngle)
			{
				_capturedMissAngle = missAngle;
			}
		}

		[HarmonyPatch(typeof(scrHitTextMesh), "Init")]
		[IriPatch(Path = "sound/judgeText", Pre = typeof(JudgeTextSettings), Condition = "enableJudgeTextCustomization")]
		public static class HitTextMeshInitPatch
		{
			public static void Postfix(scrHitTextMesh __instance, HitMargin hitMargin, TextMeshPro ___text)
			{
				if (!Settings.enableJudgeTextCustomization) return;
				if (___text == null) return;

				string template = Settings.GetTextForHitMarginName(hitMargin.ToString());
				if (!string.IsNullOrEmpty(template))
					___text.text = template;
			}
		}

		// 3.4.0 起 scrHitTextMesh.Show 会自己写 text（customText/ToLocalized），
		// 因此必须用 Postfix 在游戏赋值之后覆盖；3.3.x 的 Show 不写 text，
		// Postfix 覆盖也与旧 Prefix 行为等价。
		[HarmonyPatch(typeof(scrHitTextMesh), "Show")]
		[IriPatch(Path = "sound/judgeText", Pre = typeof(JudgeTextSettings), Condition = "enableJudgeTextCustomization")]
		public static class HitTextMeshShowPatch
		{
			public static void Postfix(scrHitTextMesh __instance, TextMeshPro ___text)
			{
				if (!Settings.enableJudgeTextCustomization) return;
				if (___text == null) return;

				string template = Settings.GetTextForHitMarginName(__instance.hitMargin.ToString());

				// 空模板 = 隐藏该判定文本（与 3.3.x 的 Prefix 行为一致）。
				// 3.4.0 的 Show 会先写入原生文本，所以这里要主动清空。
				if (string.IsNullOrEmpty(template))
				{
					___text.text = "";
					var border = _xPerfectBorderRef?.Invoke(__instance);
					if (border != null) border.text = "";
					return;
				}

				double timing = CalculateTimingFromAngle(_capturedMissAngle);
				string newText = JudgeTextSettings.ReplaceOffset(template, timing);
				___text.text = newText;

				// XPerfect 的描边文字要与主文字同步（否则描边仍是原生文本）
				var borderText = _xPerfectBorderRef?.Invoke(__instance);
				if (borderText != null) borderText.text = newText;
			}
		}

		[HarmonyPatch(typeof(scrHitTextMesh), "Show")]
		[IriPatch(Path = "sound/judgeTextRotation", Pre = typeof(CompatibilitySettings), Condition = "fixJudgeRotation")]
		public static class HitTextMeshShowRotationFixPatch
		{
			public static void Postfix(scrHitTextMesh __instance)
			{
				if (scrController.coopMode) return;

				// 3.4.0 的 Perfect 拆成三个枚举值，按名字判断兼容新旧
				string marginName = __instance.hitMargin.ToString();
				if (marginName is "Perfect" or "PerfectMinus" or "XPerfect" or "PerfectPlus") return;

				__instance.transform.DOLocalRotate(
					new Vector3(0f, 0f, _capturedMissAngle * 20f),
					2f,
					RotateMode.LocalAxisAdd
				);
			}
		}
	}
}
