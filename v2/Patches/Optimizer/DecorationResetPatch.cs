using ADOFAI;
using HarmonyLib;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Iridium.Patches
{
	/// <summary>
/// On death-restart, dirty decorations still get the full original Setup; clean
/// ones only reset hitOnce and re-register tag indices. Note scrDecoration.tags is
/// string[] on 2.9.8 (HashSet on 3.x).
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
						// tags 为 string[]；空数组按 Setup 语义归入 NO TAG
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
