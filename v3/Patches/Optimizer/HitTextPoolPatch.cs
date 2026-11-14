using HarmonyLib;
using Iridium.Config;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using UnityEngine;

namespace Iridium.Patches.Optimizer
{
	/// <summary>Shrinks the per-HitMargin hit text pool from 100 to 24 on construction and grows it on demand up to vanilla's cap of 100.</summary>
	public static class HitTextPoolPatch
	{
		private const int InitialPoolSize = 24;
		private const int MaxPoolSize = 100; // 原版每类上限，保持一致

		// cachedHitTexts 是私有字段：缓存 FieldRef 访问器
		private static readonly AccessTools.FieldRef<scrHitTextManager, Dictionary<HitMargin, scrHitTextMesh[]>> _getCached =
			AccessTools.FieldRefAccess<scrHitTextManager, Dictionary<HitMargin, scrHitTextMesh[]>>("cachedHitTexts");

		[IriPatch(Path = "optimizer/hitText", Pre = typeof(OptimizerSettings), Condition = "optimizeHitTextPool")]
		[HarmonyPatch(typeof(scrHitTextManager), MethodType.Constructor)]
		public static class HitTextCtorShrinkPatch
		{
			private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
			{
				foreach (var instruction in instructions)
				{
					// ldc.i4.s 100 appears exactly twice in the ctor (newarr + loop bound) → 初始池大小
					if (instruction.opcode == OpCodes.Ldc_I4_S && (sbyte)instruction.operand == 100)
					{
						instruction.opcode = OpCodes.Ldc_I4_S;
						instruction.operand = (sbyte)InitialPoolSize;
					}
					yield return instruction;
				}
			}
		}

		[IriPatch(Path = "optimizer/hitText", Pre = typeof(OptimizerSettings), Condition = "optimizeHitTextPool")]
		[HarmonyPatch(typeof(scrHitTextManager), "ShowHitText")]
		public static class HitTextLazyGrowPatch
		{
			[HarmonyPrefix]
			public static bool Prefix(scrHitTextManager __instance, HitMargin hitMargin)
			{
				try
				{
					var cached = _getCached?.Invoke(__instance);
					if (cached == null || !cached.TryGetValue(hitMargin, out var pool) || pool == null || pool.Length == 0)
						return true;

					// 池里还有 dead 实例 → 原版能正常找到，无需扩容
					for (int i = 0; i < pool.Length; i++)
						if (pool[i] != null && pool[i].dead)
							return true;

					// 已达原版上限：与原版行为一致（原版 FindFirstOrDefault 也找不到，直接不显示）
					if (pool.Length >= MaxPoolSize)
						return true;

					// 倍增扩容（上限 100），补建实例
					int newSize = Math.Min(pool.Length * 2, MaxPoolSize);
					var newPool = new scrHitTextMesh[newSize];
					Array.Copy(pool, newPool, pool.Length);

					GameObject prefab = ADOBase.gc != null ? ADOBase.gc.hitTextPrefab : null;
					Transform container = __instance.hitTextContainer != null
						? __instance.hitTextContainer.transform
						: null;
					if (prefab == null || container == null)
						return true;

					for (int i = pool.Length; i < newSize; i++)
					{
						var mesh = UnityEngine.Object.Instantiate(prefab, container)
							.GetComponentInChildren<scrHitTextMesh>();
						mesh.Init(hitMargin); // Init 后处于 dead 状态，等待被 Show 激活
						newPool[i] = mesh;
					}

					// 写回私有字段（字典条目是数组引用，直接替换数组即可）
					cached[hitMargin] = newPool;
					return true; // 原版会用刚创建的 dead 实例显示
				}
				catch (Exception)
				{
					return true; // 任何异常回退原版
				}
			}
		}
	}
}
