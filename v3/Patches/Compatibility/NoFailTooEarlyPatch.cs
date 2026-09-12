using Iridium.Config;
using HarmonyLib;
using ADOFAI;
using System;
using System.Reflection;

namespace Iridium.Patches.Compatibility
{
	[HarmonyPatch(typeof(scrDecoration), nameof(scrDecoration.HitboxTriggerAction))]
	[IriPatch(Path = "compatibility/noFail", Pre = typeof(CompatibilitySettings), Condition = "enableNoFailTooEarly")]
	public static class NoFailTooEarlyPatch
	{
		// 3.4.0 重排了 HitMargin 枚举，按名字解析保证新旧版本都取到 FailOverload。
		private static readonly HitMargin FailOverload =
			(HitMargin)Enum.Parse(typeof(HitMargin), "FailOverload");

		// 3.4.0 起 scrHitErrorMeter.AddHit 多了 HitMargin 参数，按签名在运行时选重载。
		private static MethodInfo? _errorMeterAddHit;
		private static bool _errorMeterAddHitResolved;

		private static void AddErrorMeterHit(scrHitErrorMeter? meter)
		{
			if (meter == null) return;

			if (!_errorMeterAddHitResolved)
			{
				_errorMeterAddHitResolved = true;
				foreach (var m in typeof(scrHitErrorMeter).GetMethods(BindingFlags.Public | BindingFlags.Instance))
				{
					if (m.Name != "AddHit") continue;
					var ps = m.GetParameters();
					if (ps.Length >= 5 && ps[1].ParameterType == typeof(HitMargin))
					{
						_errorMeterAddHit = m;
						break;
					}
					if (ps.Length == 4 && ps[0].ParameterType == typeof(float))
					{
						_errorMeterAddHit = m;
					}
				}
			}

			if (_errorMeterAddHit == null) return;
			var parameters = _errorMeterAddHit.GetParameters();
			if (parameters.Length >= 5)
				_errorMeterAddHit.Invoke(meter, new object[] { float.NegativeInfinity, FailOverload, 1f, null!, null! });
			else
				_errorMeterAddHit.Invoke(meter, new object[] { float.NegativeInfinity, 1f, null!, null! });
		}

		public static void Prefix(scrDecoration __instance, out HitboxType __state, scrPlanet planet)
		{
			__state = __instance.hitbox;
			if (!ADOBase.controller.gameworld || !ADOBase.controller.noFail || __instance.hitbox != HitboxType.Kill)
			{
				return;
			}

			if (RDC.auto)
			{
				return;
			}

			__instance.hitbox = HitboxType.None;
			if ((planet != null && planet.iFrames > 0) || __instance.hitOnce)
			{
				return;
			}

			ADOBase.controller.playerOne.marginTracker.AddHit(FailOverload);
			AddErrorMeterHit(ADOBase.controller.errorMeter);
			ADOBase.controller.chosenPlanet.MarkFail()?.BlinkForSeconds(3);
		}

		public static void Postfix(scrDecoration __instance, HitboxType __state)
		{
			__instance.hitbox = __state;
		}
	}
}
