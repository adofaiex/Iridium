using ADOFAI;
using HarmonyLib;
using Iridium.Config;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEngine;
using UnityEngine.Profiling;

namespace Iridium.Patches.Optimizer
{
	public static class OptimizerShared
	{
		public static ConcurrentDictionary<string, Vector3> decorRatios = new();
		public static ConditionalWeakTable<Texture2D, string> textureNameMap = new();
		private static ConcurrentDictionary<int, byte> _prefixCompletedTextures = new();
		public static float savedVRAM_MB = 0f;
		private static int processedTextureCount = 0;
		private const int GC_INTERVAL = 50;

		private static Func<scrVisualDecoration, Vector2>? _getSpriteUnscaledSize;
		private static Action<scrVisualDecoration, Vector2>? _setSpriteUnscaledSize;
		private static bool _decorScaleInit;

		public static void ResetDecorOptimization(bool fullReset)
		{
			decorRatios.Clear();
			_prefixCompletedTextures.Clear();
			textureNameMap = new ConditionalWeakTable<Texture2D, string>();
			savedVRAM_MB = 0f;
			processedTextureCount = 0;
			VRAMNotificationPatch.isFinished = false;
			if (fullReset)
			{
				GC.Collect();
				GC.WaitForPendingFinalizers();
				Resources.UnloadUnusedAssets();
			}
		}

		public static void ResetTextureOptimizationState()
		{
			decorRatios.Clear();
			_prefixCompletedTextures.Clear();
			textureNameMap = new ConditionalWeakTable<Texture2D, string>();
			savedVRAM_MB = 0f;
			processedTextureCount = 0;
		}

		private static bool TryGetDecorRatio(string id, out Vector3 scale)
		{
			if (!decorRatios.TryGetValue(id, out scale))
			{
				scale = Vector3.one;
				return false;
			}
			return true;
		}

		internal static bool TryGetDecorRatioForTexture(Texture2D tex, out Vector3 scale)
		{
			if (tex == null)
			{
				scale = Vector3.one;
				return false;
			}

			if (!string.IsNullOrEmpty(tex.name) && decorRatios.TryGetValue(tex.name, out scale))
			{
				return true;
			}

			if (textureNameMap.TryGetValue(tex, out string originalName) && !string.IsNullOrEmpty(originalName))
			{
				return TryGetDecorRatio(originalName, out scale);
			}

			scale = Vector3.one;
			return false;
		}

		public static Texture2D? CreateProcessedTexture(Texture2D source, int targetW, int targetH)
		{
			if (targetW <= 0 || targetH <= 0)
			{
				Main.Logger?.Log($"[Optimizer] Invalid target dimensions: {targetW}x{targetH}");
				return null;
			}

			if (!Main.IsMainThread)
			{
				return ResizeTextureCPU(source, targetW, targetH);
			}

			RenderTexture? rt = null;
			try
			{
				rt = RenderTexture.GetTemporary(targetW, targetH, 0, RenderTextureFormat.ARGB32);
				if (rt == null)
				{
					Main.Logger?.Log($"[Optimizer] Failed to create RenderTexture for {targetW}x{targetH}");
					return null;
				}

				rt.filterMode = FilterMode.Bilinear;
				Graphics.Blit(source, rt);

				Texture2D result = new(targetW, targetH, TextureFormat.RGBA32, false);
				RenderTexture.active = rt;
				result.ReadPixels(new Rect(0, 0, targetW, targetH), 0, 0);
				result.Apply(false);
				RenderTexture.active = null;
				result.name = source.name;
				return result;
			}
			catch (Exception e)
			{
				Main.Logger?.Log($"[Optimizer] Error in CreateProcessedTexture: {e.Message}");
				return null;
			}
			finally
			{
				if (rt != null) RenderTexture.ReleaseTemporary(rt);
			}
		}

		private static Texture2D? ResizeTextureCPU(Texture2D source, int targetW, int targetH)
		{
			try
			{
				Color32[] sourcePixels = source.GetPixels32();
				Color32[] targetPixels = new Color32[targetW * targetH];
				int sourceW = source.width;
				int sourceH = source.height;

				if (sourceW < 2 || sourceH < 2 || targetW <= 0 || targetH <= 0)
				{
					// Note: the managed loop below indexes `y2 = (yFloor+1)*sourceW`
					// and `xFloor+1` unconditionally, so a source narrower or
					// shorter than 2 would read past the pixel array. Refusing
					// here is both safer and cheaper than cloning a 1xN texture
					// through a fallback path.
					Main.Logger?.Log($"[Optimizer] ResizeTextureCPU: bad size {sourceW}x{sourceH} -> {targetW}x{targetH}");
					return null;
				}

				// Native bilinear downscale. Bit-exact with the managed loop
				// below (same int-then-float delta, same truncating u8 cast,
				// same byte quantization between the two lerp stages), so both
				// paths produce identical pixels.
				if (Iridium.Native.IridiumNative.HasResizeBilinear &&
					Iridium.Native.IridiumNative.ResizeBilinearPinned(
						sourcePixels, sourceW, sourceH, targetPixels, targetW, targetH))
				{
					return FinishResize(source, targetPixels, targetW, targetH);
				}

				float xRatio = (float)(sourceW - 1) / targetW;
				float yRatio = (float)(sourceH - 1) / targetH;

				for (int y = 0; y < targetH; y++)
				{
					int yFloor = (int)(y * yRatio);
					float yLerp = (y * yRatio) - yFloor;
					int y1 = yFloor * sourceW;
					int y2 = (yFloor + 1) * sourceW;

					for (int x = 0; x < targetW; x++)
					{
						int xFloor = (int)(x * xRatio);
						float xLerp = (x * xRatio) - xFloor;

						int index = y * targetW + x;

						Color32 c1 = sourcePixels[y1 + xFloor];
						Color32 c2 = sourcePixels[y1 + xFloor + 1];
						Color32 c3 = sourcePixels[y2 + xFloor];
						Color32 c4 = sourcePixels[y2 + xFloor + 1];

						targetPixels[index] = Color32.Lerp(
							Color32.Lerp(c1, c2, xLerp),
							Color32.Lerp(c3, c4, xLerp),
							yLerp
						);
					}
				}

				Texture2D result = new(targetW, targetH, source.format, source.mipmapCount > 1);
				result.SetPixels32(targetPixels);
				result.Apply(false, false);
				result.name = source.name;
				return result;
			}
			catch (Exception e)
			{
				Main.Logger?.Log($"[Optimizer] CPU Resize Error: {e.Message}");
				return null;
			}
		}

		/// <summary>
		/// Wraps an already-resampled <see cref="Color32"/> buffer back into a
		/// Texture2D. Shared by the native and managed resize paths so texture
		/// creation stays identical regardless of which one ran.
		/// </summary>
		private static Texture2D? FinishResize(Texture2D source, Color32[] targetPixels, int targetW, int targetH)
		{
			try
			{
				Texture2D result = new(targetW, targetH, source.format, source.mipmapCount > 1);
				result.SetPixels32(targetPixels);
				result.Apply(false, false);
				result.name = source.name;
				return result;
			}
			catch (Exception e)
			{
				Main.Logger?.Log($"[Optimizer] CPU Resize Error: {e.Message}");
				return null;
			}
		}

		internal static void ApplyDecorRatioScaling(scrVisualDecoration __instance, Vector3 ratio)
		{
			if (__instance.spriteRenderer != null)
				__instance.spriteRenderer.transform.localScale = ratio;

			if (!Main.Settings.optimizer.dontResizeCollider)
			{
				__instance.editorCollider.size = Vector2.Scale(__instance.editorCollider.size, ratio);

				if (!_decorScaleInit) InitDecorScale();
				if (_getSpriteUnscaledSize != null && _setSpriteUnscaledSize != null && __instance.spriteRenderer.sprite != null)
				{
					var uncompressedSize = Vector2.Scale((Vector2)__instance.spriteRenderer.sprite.bounds.size, ratio);
					_setSpriteUnscaledSize(__instance, uncompressedSize);
				}
			}
		}

		private static void InitDecorScale()
		{
			var prop = AccessTools.Property(typeof(scrVisualDecoration), "spriteUnscaledSize");
			if (prop != null)
			{
				if (prop.GetMethod != null)
					_getSpriteUnscaledSize = AccessTools.MethodDelegate<Func<scrVisualDecoration, Vector2>>(prop.GetMethod);
				if (prop.SetMethod != null)
					_setSpriteUnscaledSize = AccessTools.MethodDelegate<Action<scrVisualDecoration, Vector2>>(prop.SetMethod);
			}
			_decorScaleInit = true;
		}

		internal static void RuntimeSetSprite(scrVisualDecoration dec, TextureManager.CustomSprite customSprite)
		{
			if (customSprite == null) return;

			var method2 = AccessTools.Method(typeof(scrVisualDecoration), "SetSprite",
				new Type[] { typeof(TextureManager.CustomSprite), typeof(bool) });
			if (method2 != null)
			{
				method2.Invoke(dec, new object[] { customSprite, false });
				return;
			}

			var method1 = AccessTools.Method(typeof(scrVisualDecoration), "SetSprite",
				new Type[] { typeof(Sprite) });
			method1?.Invoke(dec, new object[] { customSprite.sprite });
		}

		public static bool TryGetPrefixCompleted(Texture2D tex) => _prefixCompletedTextures.ContainsKey(tex.GetInstanceID());
		public static void MarkPrefixCompleted(Texture2D tex) => _prefixCompletedTextures[tex.GetInstanceID()] = 1;
		public static void IncrementProcessedTextureCount() => processedTextureCount++;
		public static int GetProcessedTextureCount() => processedTextureCount;
		public static int GetGcInterval() => GC_INTERVAL;
	}
}
