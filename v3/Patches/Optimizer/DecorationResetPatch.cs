using HarmonyLib;
using Iridium.Config;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace Iridium.Patches.Optimizer
{
	/// <summary>
	/// 死亡重开时的装饰物复位优化。
	///
	/// 每次死亡 scrDecorationManager.ResetDecorations 对**全部装饰物**重跑
	/// scrDecoration.Setup(sourceLevelEvent, out _)：内部含组件写法的
	/// JSON 解析（Json.Deserialize）、字符串 Split/HashSet 分配、SetBlendMode
	/// 的 Enum.Parse、多次 SetSprite/SetTile/SetMasking 原生调用，以及
	/// GetComponents&lt;MonoBehaviour&gt;().Where().ToArray() 重建缓存。
	/// 上千装饰物的谱面每次重开都是一笔可观的固定开销，而其中绝大多数
	/// 装饰物在播放期间根本没有被事件改动过。
	///
	/// 实现：播放期脏标记。
	/// - Prefix 挂在 ResetDecorations：先复刻原版的三个字典 Clear
	///   （taggedDecorations / hitboxEventTags / hitboxEventTagDecorations）；
	/// - 对"脏"装饰物（播放期被事件改过）交还原版 Setup + hitOnce=false；
	/// - 对"干净"装饰物：跳过 Setup，仅重置 hitOnce=false，并按 Setup 内
	///   的注册逻辑把该装饰物重新登记进 taggedDecorations 与
	///   hitboxEventTags/hitboxEventTagDecorations（tags/hitboxEventTags
	///   均为 public 字段，可直接读取；此步保证跳过 Setup 后标签索引不失效）；
	///   同时刷新其在 taggedDecorations 列表中的位置（Remove 后 Add 到尾部，
	///   与原版 Setup 的登记路径一致）。
	/// - 脏标记的来源：scrDecoration 的 eventTweens 非空（被 StartEffect
	///   建 tween）或 hitboxEvents 非空。这两个字典/列表在 ResetScene 的
	///   tween 清理路径中会被清空，因此直接在 Reset 时刻检查它们是否残留
	///   即可判定"播放期被改过"——无需在各个事件入口埋点。
	///
	/// 风险说明：若某事件路径改动了装饰物但未留 eventTweens/hitboxEvents
	/// 痕迹，跳过 Setup 可能导致该装饰物不复位。因此本项做成独立开关，
	/// 出问题时关闭 optimizeDecorationReset 即可回退原版行为。
	/// 3.3.0 与 3.4.0 的 ResetDecorations / Setup 结构一致（已核对两版反编译源）。
	/// </summary>
	[IriPatch(Path = "optimizer/decorReset", Pre = typeof(OptimizerSettings), Condition = "optimizeDecorationReset")]
	[HarmonyPatch(typeof(scrDecorationManager), nameof(scrDecorationManager.ResetDecorations))]
	public static class DecorationResetPatch
	{
		[HarmonyPrefix]
		public static bool Prefix(scrDecorationManager __instance)
		{
			try
			{
				var decorations = __instance.allDecorations;
				if (decorations == null)
					return true;
				if (!Application.isPlaying || ADOBase.isLevelEditor)
					return true; // 编辑器/非播放场景保持原版行为

				// 复刻原版头部的索引清理
				__instance.taggedDecorations?.Clear();
				__instance.hitboxEventTags.Clear();
				__instance.hitboxEventTagDecorations?.Clear();

				var tagged = __instance.taggedDecorations;
				var hitboxTags = __instance.hitboxEventTags;
				var hitboxTagDecos = __instance.hitboxEventTagDecorations;

				foreach (var dec in decorations)
				{
					if (dec == null)
						continue;

					bool dirty = (dec.eventTweens != null && dec.eventTweens.Count > 0)
						|| (dec.hitboxEvents != null && dec.hitboxEvents.Count > 0);

					if (dirty)
					{
						// 被事件改过：完整 Setup 复位（Setup 内部会重新登记标签索引）
						dec.Setup(dec.sourceLevelEvent, out _);
						dec.hitOnce = false;
						continue;
					}

					// 干净装饰物：只重置 hit 标记，并重建标签索引登记
					dec.hitOnce = false;

					if (tagged != null)
					{
						var tags = dec.tags;
						if (tags == null || tags.Count == 0)
							tags = new HashSet<string> { "NO TAG" };
						foreach (string tag in tags)
						{
							if (!tagged.TryGetValue(tag, out var list))
								tagged[tag] = list = new List<scrDecoration>();
							// 该装饰物刚被 Clear 移除，重新登记（Setup 同款路径）
							list.Add(dec);
						}
					}

					// hitbox 事件标签登记（Setup 内 UnionWith + 逐 tag 登记的同款语义）
					var decHitboxTags = dec.hitboxEventTags;
					if (decHitboxTags != null && decHitboxTags.Count > 0)
					{
						hitboxTags.UnionWith(decHitboxTags);
						if (hitboxTagDecos != null)
						{
							foreach (string hbTag in decHitboxTags)
							{
								if (!hitboxTagDecos.TryGetValue(hbTag, out var list))
									hitboxTagDecos[hbTag] = list = new List<scrDecoration>();
								list.Add(dec);
							}
						}
					}
				}
				return false;
			}
			catch (Exception)
			{
				return true; // 任何异常回退原版全量 Setup
			}
		}
	}
}
