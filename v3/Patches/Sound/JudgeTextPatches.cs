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
				if (string.IsNullOrEmpty(template)) return; // Auto/Midspin 等回落游戏原生文本
				double timing = CalculateTimingFromAngle(_capturedMissAngle);
				___text.text = JudgeTextSettings.ReplaceOffset(template, timing);
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
