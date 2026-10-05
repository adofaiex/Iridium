using ADOFAI;
using HarmonyLib;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Iridium.Patches
{
	/// <summary>
	/// 死亡重开时的装饰物复位优化（自 v3 移植，2.9.8 适配）。
	///
	/// ResetDecorations 对全部装饰物重跑 Setup（JSON 解析、Split、
	/// Enum.Parse、多次原生调用）。实现在播放场景下：
	/// - 脏装饰物（eventTweens/hitboxEvents 有残留）交还原版完整 Setup；
	/// - 干净装饰物只重置 hitOnce，并按 Setup 同款路径重建标签索引登记。
	/// 2.9.8 差异：scrDecoration.tags 是 string[]（3.x 是 HashSet）。
	/// 2.9.8 与 3.x 的 ResetDecorations/Setup 结构一致（已核对反编译源）。
	/// </summary>
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
					return true;

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
						dec.Setup(dec.sourceLevelEvent, out _);
						dec.hitOnce = false;
						continue;
					}

					dec.hitOnce = false;

					if (tagged != null)
					{
						// 2.9.8：tags 为 string[]；空数组按 Setup 语义归入 NO TAG
						string[] tags = dec.tags;
						if (tags == null || tags.Length == 0)
							tags = new[] { "NO TAG" };
						foreach (string tag in tags)
						{
							if (string.IsNullOrEmpty(tag))
								continue;
							if (!tagged.TryGetValue(tag, out var list))
								tagged[tag] = list = new List<scrDecoration>();
							list.Add(dec);
						}
					}

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
				return true;
			}
		}
	}
}
