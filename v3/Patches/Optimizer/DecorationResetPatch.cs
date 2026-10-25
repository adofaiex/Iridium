using HarmonyLib;
using Iridium.Config;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace Iridium.Patches.Optimizer
{
	/// <summary>On reset, only re-runs scrDecoration.Setup for decorations an event actually touched (eventTweens/hitboxEvents left over); clean ones are re-registered into the tag indices only.</summary>
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
