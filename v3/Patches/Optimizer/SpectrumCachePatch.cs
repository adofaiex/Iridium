using HarmonyLib;
using Iridium.Config;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using UnityEngine;

namespace Iridium.Patches.Optimizer
{
	/// <summary>Computes GetSpectrumData once per (source, channel, window, length) per frame and hands the same values to all consumers.</summary>
	public static class SpectrumCachePatch
	{
		/// <summary>GetSpectrumData 的每帧缓存。固定 8 条 + 线性查找，避免每帧字典分配。</summary>
		public static class SpectrumCache
		{
			private sealed class Entry
			{
				public AudioSource Source;
				public int Channel;
				public FFTWindow Window;
				public int Length;
				public int Frame = -1;
				public float[] Data = Array.Empty<float>();
			}

			private const int MaxEntries = 8;
			private static readonly Entry[] _entries = new Entry[MaxEntries];
			private static int _next;

			/// <summary>Stands in for AudioSource.GetSpectrumData with source hoisted to a leading parameter, matching the IL argument order.</summary>
			public static void Get(AudioSource source, float[] samples, int channel, FFTWindow window)
			{
				if (source == null || samples == null)
					return;

				int frame = Time.frameCount;
				int length = samples.Length;

				for (int i = 0; i < MaxEntries; i++)
				{
					var e = _entries[i];
					if (e == null) break;
					if (e.Frame == frame && e.Source == source && e.Channel == channel
						&& e.Window == window && e.Length == length)
					{
						// 同帧命中：拷贝缓存结果
						Array.Copy(e.Data, samples, length);
						return;
					}
				}

				// 未命中：真实计算一次，先填入调用方数组再缓存副本
				source.GetSpectrumData(samples, channel, window);

				Entry slot = null;
				for (int i = 0; i < MaxEntries; i++)
				{
					var e = _entries[i];
					if (e == null || e.Frame != frame)
					{
						if (e == null) e = _entries[i] = new Entry();
						slot = e;
						break;
					}
				}
				if (slot == null)
				{
					// 全部被本帧占用（理论上 ≤4 个消费者，不会到这）：轮转覆写最旧
					slot = _entries[_next];
					_next = (_next + 1) % MaxEntries;
				}

				slot.Source = source;
				slot.Channel = channel;
				slot.Window = window;
				slot.Length = length;
				slot.Frame = frame;
				if (slot.Data.Length != length)
					slot.Data = new float[length];
				Array.Copy(samples, slot.Data, length);
			}
		}

		[IriPatch(Path = "optimizer/spectrum", Pre = typeof(OptimizerSettings), Condition = "optimizeSpectrum")]
		[HarmonyPatch]
		public static class SpectrumCallReplacePatch
		{
			private static MethodInfo _targetGetSpectrumData;

			[HarmonyTargetMethods]
			public static System.Collections.Generic.IEnumerable<MethodBase> TargetMethods()
			{
				_targetGetSpectrumData = AccessTools.Method(typeof(AudioSource), nameof(AudioSource.GetSpectrumData),
					new[] { typeof(float[]), typeof(int), typeof(FFTWindow) }) as MethodInfo;
				if (_targetGetSpectrumData == null) yield break;

				// 4 个每帧消费者；任何一个缺失都安全跳过
				yield return AccessTools.Method(typeof(scrConductor), "Update");
				yield return AccessTools.Method(typeof(scrVolumeTrackerFloat), "Update");
				yield return AccessTools.Method(typeof(scrVolumeTrackerFade), "Update");
				yield return AccessTools.Method(typeof(scrBarMaker), "Update");
			}

			private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
			{
				var replacement = AccessTools.Method(typeof(SpectrumCache), nameof(SpectrumCache.Get));
				if (_targetGetSpectrumData == null || replacement == null)
				{
					foreach (var i in instructions) yield return i;
					yield break;
				}

				foreach (var instruction in instructions)
				{
					if (instruction.Calls(_targetGetSpectrumData))
					{
						// 实例方法 → 静态 helper：source 在栈上就是第一个参数，参数顺序不变
						instruction.opcode = OpCodes.Call;
						instruction.operand = replacement;
					}
					yield return instruction;
				}
			}
		}
	}
}
