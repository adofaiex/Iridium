using ADOFAI;
using HarmonyLib;
using Iridium.Config;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;

namespace Iridium.Patches.Optimizer
{
	/// <summary>Hitbox hot path: filter decorations in the manager loop and skip the physics query when no trigger is possible, keeping vanilla semantics otherwise.</summary>
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

		/// <summary>2.9.8 only has the older Collider2D.OverlapCollider; 3.3.x renamed it to Overlap, so resolve whatever this build has.</summary>
		private static bool EnsureOverlap()
		{
			if (_overlapResolved) return _overlap != null;
			_overlapResolved = true;

			// Independent try per level: one failure must not block the next fallback.
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
					return FastDelegate.Open<Func<Collider2D, ContactFilter2D, List<Collider2D>, int>>(m);
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
					var d = FastDelegate.Open<Func<Collider2D, ContactFilter2D, List<Collider2D>, int>>(m);
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
			/// <summary>Below this many checkers the per-checker Overlap beats building an index.</summary>
			private const int MinIndexedCheckers = 2;

			/// <summary>Give up the index when targets exceed checkers × this; rebuild reads every target's collider.bounds.</summary>
			private const int TargetToCheckerRatio = 8;

			private static readonly List<scrDecoration> _spatialTargets = new List<scrDecoration>(256);
			private static float[] _spatialRects = new float[256 * 4];
			private static int[] _spatialHits = new int[256];
			private static IntPtr _hitBuffer;
			private static int _hitCapacity;

			[HarmonyPrefix]
			public static bool Prefix(scrDecorationManager __instance)
			{
				if (!Main.Settings.optimizer.optimizeHitboxDetection) return true;
				try
				{
					if (!ADOBase.isEditingLevel)
					{
						if (!TrySpatialFilteredChecks(__instance))
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

			/// <summary>AABB broad phase is sound: AABB overlap and tag overlap are both necessary for a trigger, so an empty candidate set cannot miss; any candidate still runs vanilla physics.</summary>
			private static bool TrySpatialFilteredChecks(scrDecorationManager manager)
			{
				if (!Iridium.Native.IridiumNative.HasSpatial) return false;

				List<scrDecoration> all = manager.allDecorations;
				if (all == null || all.Count == 0) return true;

				// 第一遍（廉价）：统计检查者与目标数量，决定是否值得建索引。
				int checkerCount = 0;
				int targetCount = 0;
				int listCount = all.Count;
				for (int i = 0; i < listCount; i++)
				{
					scrDecoration dec = all[i];
					if (dec == null) continue;
					if (IsChecker(dec)) checkerCount++;
					if (IsIndexTarget(dec)) targetCount++;
				}

				if (checkerCount == 0) return true; // 没有可触发者
				if (targetCount == 0) return true;  // 没有带标签的可命中目标
				if (checkerCount < MinIndexedCheckers) return false;
				if ((long)targetCount > (long)checkerCount * TargetToCheckerRatio) return false;

				// 第二遍：收集目标 AABB（世界包围盒），重建索引。
				_spatialTargets.Clear();
				if (_spatialRects.Length < targetCount * 4)
					_spatialRects = new float[targetCount * 4];
				int n = 0;
				for (int i = 0; i < listCount; i++)
				{
					scrDecoration dec = all[i];
					if (dec == null || !IsIndexTarget(dec)) continue;
					Bounds b = dec.activeCollider.bounds;
					int o = n * 4;
					_spatialRects[o] = b.min.x;
					_spatialRects[o + 1] = b.min.y;
					_spatialRects[o + 2] = b.max.x;
					_spatialRects[o + 3] = b.max.y;
					_spatialTargets.Add(dec);
					n++;
				}

				if (!Iridium.Native.IridiumNative.SpatialRebuild(_spatialRects, n))
					return false;

				EnsureHitBuffer(n);
				if (_spatialHits.Length < n) _spatialHits = new int[n];

				// 第三遍：逐个检查者查询候选，标签相交才调用精确物理判定。
				for (int i = 0; i < listCount; i++)
				{
					scrDecoration dec = all[i];
					if (dec == null || !IsChecker(dec)) continue;

					Bounds b = dec.activeCollider.bounds;
					int hits = Iridium.Native.IridiumNative.SpatialQueryRaw(
						b.min.x, b.min.y, b.max.x, b.max.y, _hitBuffer, n);
					if (hits <= 0) continue;
					if (hits > n) hits = n;

					Marshal.Copy(_hitBuffer, _spatialHits, 0, hits);

					HashSet<string> tags = dec.hitboxDecoTags;
					bool possible = false;
					for (int k = 0; k < hits; k++)
					{
						scrDecoration other = _spatialTargets[_spatialHits[k]];
						if (other == null || ReferenceEquals(other, dec)) continue;
						HashSet<string> otherTags = other.tags;
						if (otherTags == null) continue;
						foreach (string t in tags)
						{
							if (t != null && otherTags.Contains(t))
							{
								possible = true;
								break;
							}
						}
						if (possible) break;
					}

					if (possible)
						dec.CheckHitboxHit();
				}
				return true;
			}

			private static bool IsChecker(scrDecoration dec)
			{
				if (!dec.useHitbox || dec.hitboxDetectTarget != HitboxDetectTarget.Decoration) return false;
				HashSet<string> tags = dec.hitboxDecoTags;
				if (tags == null || tags.Count == 0) return false;
				if (dec.hitOnce && dec.hitboxTriggerType == HitboxTriggerType.Once) return false;
				return dec.activeCollider != null;
			}

			private static bool IsIndexTarget(scrDecoration dec)
			{
				if (dec.activeCollider == null) return false;
				HashSet<string> tags = dec.tags;
				return tags != null && tags.Count > 0;
			}

			private static void EnsureHitBuffer(int count)
			{
				if (_hitCapacity >= count && _hitBuffer != IntPtr.Zero) return;
				if (_hitBuffer != IntPtr.Zero) Marshal.FreeHGlobal(_hitBuffer);
				_hitBuffer = Marshal.AllocHGlobal(count * sizeof(int));
				_hitCapacity = count;
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
