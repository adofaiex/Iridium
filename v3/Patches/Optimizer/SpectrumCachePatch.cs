using HarmonyLib;
using Iridium.Config;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using UnityEngine;

namespace Iridium.Patches.Optimizer
{
	/// <summary>
	/// 音频频谱（FFT）每帧缓存。
	///
	/// 原版有 4 个组件每帧各自对同一个 AudioSource 调 GetSpectrumData(1024, BlackmanHarris)：
	/// scrConductor.Update、scrVolumeTrackerFloat.Update、scrVolumeTrackerFade.Update、
	/// scrBarMaker.Update。每次调用都是一次真实的 FFT 计算 + 填充 4KB 数组，
	/// 且结果几乎相同（同源同窗口）——纯重复计算。
	///
	/// 本补丁用 Transpiler 把这 4 处 GetSpectrumData 调用替换为
	/// SpectrumCache.Get：同一帧内相同 (source, channel, window, 长度) 只真正
	/// 计算一次，其余调用复用缓存结果拷贝到调用方数组。频谱数值语义不变。
	///
	/// 3.3.0 与 3.4.0 的这 4 处调用完全一致（已核对两版反编译源）。
	/// </summary>
	public static class SpectrumCachePatch
	{
		/// <summary>
		/// GetSpectrumData 的每帧缓存。固定 8 条 + 线性查找，避免每帧字典分配。
		/// </summary>
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

			/// <summary>
			/// 替换 AudioSource.GetSpectrumData 的静态入口。签名与实例方法对齐
			/// （source 相当于 this），参数顺序与 IL 栈上一致，可直接替换调用点。
			/// </summary>
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
