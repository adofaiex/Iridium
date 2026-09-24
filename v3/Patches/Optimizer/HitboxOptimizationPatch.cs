using ADOFAI;
using HarmonyLib;
using Iridium.Config;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Iridium.Patches.Optimizer
{
	/// <summary>
	/// 命中框（Hitbox）检测热路径优化。
	///
	/// 原版每帧对<b>全部</b>装饰物调用 CheckHitboxHit（scrDecorationManager.Update），
	/// 而真正参与"装饰物-装饰物"判定的只是 hitboxDetectTarget == Decoration 的少数；
	/// 命中判定本身还有两处开销：
	///   1. `hitboxDecoTags.Any(t => deco.tags.Contains(t))` —— LINQ + 闭包，
	///      每个命中碰撞体每帧分配一次；
	///   2. 在"不可能触发"的情况（无匹配标签、Once 已触发）下仍执行物理 Overlap。
	///
	/// 本补丁严格保持原版语义：
	///   - 管理器循环先按 useHitbox &amp;&amp; target==Decoration 过滤，省去对其余
	///     装饰物（绝大多数）的方法调用与 Harmony 分支；
	///   - CheckHitboxHit 改为无分配的手写标签匹配，并在不可能触发时跳过物理查询。
	///
	/// 注意：v3 引用的是 2.9.8 的 UnityEngine，Collider2D 只有旧版 OverlapCollider；
	/// 而 3.3.x 改名为 Overlap。这里在运行时解析可用的 API，两个版本通用。
	/// </summary>
	public static class HitboxOptimizationPatch
	{
		// 复用结果列表，替代原版每次 CollectionPool.Get/Release
		private static readonly List<Collider2D> _overlapResults = new List<Collider2D>(16);

		private static ContactFilter2D _contactFilter;
		private static bool _contactFilterReady;

		private static Func<Collider2D, ContactFilter2D, List<Collider2D>, int>? _overlap;
		private static bool _overlapResolved;

		private static ContactFilter2D ContactFilter
		{
			get
			{
				if (!_contactFilterReady)
				{
					// 与 scrDecoration.hitboxContactFilter 一致：只查询命中框层
					_contactFilter = new ContactFilter2D
					{
						layerMask = 1 << scrDecoration.HitboxLayer,
						useLayerMask = true
					};
					_contactFilterReady = true;
				}
				return _contactFilter;
			}
		}

		/// <summary>
		/// 解析 Collider2D 的 overlap API：
		/// 3.3.x 的实例 Overlap &gt; 2.9.8 的实例 OverlapCollider &gt; 静态 Physics2D.OverlapCollider。
		/// </summary>
		private static bool EnsureOverlap()
		{
			if (_overlapResolved) return _overlap != null;
			_overlapResolved = true;

			// 每一级独立 try：某一级解析/委托生成失败不应影响后续回退。
			_overlap = TryInstance("Overlap") ?? TryInstance("OverlapCollider") ?? TryStatic();

			if (_overlap == null)
				Main.Logger?.Warning("[HitboxOptimization] no Collider2D overlap API found; falling back to vanilla.");

			return _overlap != null;

			Func<Collider2D, ContactFilter2D, List<Collider2D>, int>? TryInstance(string name)
			{
				try
				{
					var m = AccessTools.Method(typeof(Collider2D), name,
						new[] { typeof(ContactFilter2D), typeof(List<Collider2D>) });
					if (m == null || m.ReturnType != typeof(int)) return null;
					return AccessTools.MethodDelegate<Func<Collider2D, ContactFilter2D, List<Collider2D>, int>>(m);
				}
				catch (Exception e)
				{
					Main.Logger?.Error($"[HitboxOptimization] instance Collider2D.{name} unavailable: {e.Message}");
					return null;
				}
			}

			Func<Collider2D, ContactFilter2D, List<Collider2D>, int>? TryStatic()
			{
				try
				{
					var m = AccessTools.Method(typeof(Physics2D), "OverlapCollider",
						new[] { typeof(Collider2D), typeof(ContactFilter2D), typeof(List<Collider2D>) });
					if (m == null || m.ReturnType != typeof(int)) return null;
					var d = AccessTools.MethodDelegate<Func<Collider2D, ContactFilter2D, List<Collider2D>, int>>(m);
					return (c, f, l) => d(c, f, l);
				}
				catch (Exception e)
				{
					Main.Logger?.Error($"[HitboxOptimization] static Physics2D.OverlapCollider unavailable: {e.Message}");
					return null;
				}
			}
		}

		[IriPatch(Path = "optimizer/decor", Pre = typeof(OptimizerSettings), Condition = "enableOptimizer,optimizeHitboxDetection")]
		[HarmonyPatch(typeof(scrDecorationManager), "Update")]
		public static class ManagerUpdateHitboxFilterPatch
		{
			[HarmonyPrefix]
			public static bool Prefix(scrDecorationManager __instance)
			{
				if (!Main.Settings.optimizer.optimizeHitboxDetection) return true;
				try
				{
					if (!ADOBase.isEditingLevel)
					{
						List<scrDecoration> all = __instance.allDecorations;
						int count = all.Count;
						for (int i = 0; i < count; i++)
						{
							scrDecoration dec = all[i];
							if (dec != null && dec.useHitbox && dec.hitboxDetectTarget == HitboxDetectTarget.Decoration)
								dec.CheckHitboxHit();
						}
					}

					// 原版 Update 末尾的副作用
					scrCamera.instance.lockCustomFrameUpdate = true;
					return false;
				}
				catch (Exception ex)
				{
					Main.Logger?.Error($"[HitboxOptimization] Manager.Update failed, falling back to vanilla: {ex}");
					return true;
				}
			}
		}

		[IriPatch(Path = "optimizer/decor", Pre = typeof(OptimizerSettings), Condition = "enableOptimizer,optimizeHitboxDetection")]
		[HarmonyPatch(typeof(scrDecoration), nameof(scrDecoration.CheckHitboxHit))]
		public static class CheckHitboxHitPatch
		{
			[HarmonyPrefix]
			public static bool Prefix(scrDecoration __instance)
			{
				if (!Main.Settings.optimizer.optimizeHitboxDetection) return true;

				// 与原版一致的前置条件
				if (!__instance.useHitbox || __instance.hitboxDetectTarget != HitboxDetectTarget.Decoration)
					return false;

				HashSet<string> tags = __instance.hitboxDecoTags;
				if (tags == null || tags.Count == 0)
					return false; // 无标签可匹配，原版 Any 必为 false

				// Once 触发后 UpdateHitboxState 不再重置，命中判定不可能再次成功
				if (__instance.hitOnce && __instance.hitboxTriggerType == HitboxTriggerType.Once)
					return false;

				Collider2D collider = __instance.activeCollider;
				scrDecorationManager manager = __instance.manager;
				if (collider == null || manager == null)
					return false;

				Dictionary<Collider2D, scrDecoration> dict = manager.hitboxCollidersToDecorations;
				if (dict == null || !EnsureOverlap())
					return true; // 无法安全重写时交还原版

				try
				{
					_overlapResults.Clear();
					_overlap!(collider, ContactFilter, _overlapResults);

					for (int i = 0; i < _overlapResults.Count; i++)
					{
						if (!dict.TryGetValue(_overlapResults[i], out scrDecoration other) || other == null)
							continue;

						HashSet<string> otherTags = other.tags;
						if (otherTags == null)
							continue;

						bool matched = false;
						foreach (string tag in tags)
						{
							if (tag != null && otherTags.Contains(tag))
							{
								matched = true;
								break;
							}
						}

						if (matched)
						{
							// 首次触发后 hitOnce 生效，后续匹配不会再有动作
							__instance.HitboxTriggerAction();
							break;
						}
					}
				}
				catch (Exception ex)
				{
					Main.Logger?.Error($"[HitboxOptimization] CheckHitboxHit failed, falling back to vanilla: {ex}");
					return true;
				}

				return false;
			}
		}
	}
}
