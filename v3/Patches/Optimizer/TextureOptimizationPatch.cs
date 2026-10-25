using ADOFAI;
using HarmonyLib;
using Iridium.Config;
using Iridium.UI;
using System;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Profiling;

namespace Iridium.Patches.Optimizer
{
	[IriPatch(Path = "optimizer/texture", Pre = typeof(OptimizerSettings), Condition = "enableOptimizer,!dontCompress")]
	[HarmonyPatch(typeof(TextureManager), nameof(TextureManager.LoadTexture))]
	public static class TextureOptimizationPatch
	{
		private static int AlignTo4(int val) => Math.Max(4, (val + 2) & ~3);

		private static long EstimateTextureSize(Texture2D tex)
		{
			try
			{
				var size = Profiler.GetRuntimeMemorySizeLong(tex);
				if (size > 0) return size;
			}
			catch (Exception _ex)
			{
				Main.Logger?.Error($"[Optimizer] Failed to get memory size: {_ex}");
			}

			var format = tex.format;
			long pixels = (long)tex.width * tex.height;

			if (format == TextureFormat.DXT1 || format == TextureFormat.DXT1Crunched ||
				format == TextureFormat.ETC_RGB4 || format == TextureFormat.ETC2_RGB)
				return Math.Max(4L, pixels / 2);

			if (format == TextureFormat.DXT5 || format == TextureFormat.DXT5Crunched ||
				format == TextureFormat.ETC2_RGBA8)
				return pixels;

			int fmtVal = (int)format;
			if (fmtVal >= (int)TextureFormat.ASTC_4x4 && fmtVal <= (int)TextureFormat.ASTC_12x12)
				return Math.Max(16L, pixels / 4);

			if (format == TextureFormat.RGBA32 || format == TextureFormat.ARGB32 ||
				format == TextureFormat.RGBAFloat || format == TextureFormat.BGRA32)
				return pixels * 4;

			if (format == TextureFormat.RGB24)
				return pixels * 3;

			if (format == TextureFormat.Alpha8 || format == TextureFormat.R8)
				return pixels;

			if (format == TextureFormat.RG16 || format == TextureFormat.R16 ||
				format == TextureFormat.RGBAHalf || format == TextureFormat.RGHalf || format == TextureFormat.RHalf)
				return pixels * 2;

			return pixels * 4;
		}

		private static void ApplyCompression(Texture2D tex)
		{
			if (!tex.isReadable) return;

			var settings = Main.Settings.optimizer;
			if (settings.useLossyCompression)
			{
				try
				{
					int w = tex.width, h = tex.height;
					Color32[] original = tex.GetPixels32();

					var rgbTex = new Texture2D(w, h, TextureFormat.RGB24, false);
					var rgbOnly = new Color32[original.Length];
					for (int i = 0; i < original.Length; i++)
						rgbOnly[i] = new Color32(original[i].r, original[i].g, original[i].b, 255);
					rgbTex.SetPixels32(rgbOnly);
					rgbTex.Apply(false);

					byte[] jpgBytes = ImageConversion.EncodeToJPG(rgbTex, Mathf.Clamp(settings.lossyQuality, 10, 100));
					UnityEngine.Object.DestroyImmediate(rgbTex);

					if (jpgBytes != null && jpgBytes.Length > 0)
					{
						var decoded = new Texture2D(w, h, TextureFormat.RGB24, false);
						ImageConversion.LoadImage(decoded, jpgBytes, false);
						Color32[] decodedRgb = decoded.GetPixels32();
						UnityEngine.Object.DestroyImmediate(decoded);

						for (int i = 0; i < original.Length; i++)
							original[i] = new Color32(decodedRgb[i].r, decodedRgb[i].g, decodedRgb[i].b, original[i].a);

						tex.SetPixels32(original);
					}
				}
				catch (Exception _ex)
				{
					Main.Logger?.Error($"[Optimizer] ApplyCompression inline failed: {_ex}");
				}
			}

			tex.Compress(false);
			tex.Apply(false, true);
		}

		private static void TryFastCompress(ref Texture2D tex)
		{
			if (Main.Settings.optimizer.dontCompress) return;

			try
			{
				if (tex.isReadable)
				{
					ApplyCompression(tex);
				}
			}
			catch (Exception _ex)
			{
				Main.Logger?.Error($"[Optimizer] TryFastCompress failed: {_ex}");
			}
		}

		private static byte[] ResizeImageBytes(byte[] imageData, double scaleFactor, bool alignTo4, out int originalW, out int originalH, out int newW, out int newH)
		{
			using (var ms = new MemoryStream(imageData))
			using (var bitmap = new System.Drawing.Bitmap(ms))
			{
				originalW = bitmap.Width;
				originalH = bitmap.Height;

				newW = (int)Math.Round(originalW / scaleFactor);
				newH = (int)Math.Round(originalH / scaleFactor);

				if (newW < 4) newW = 4;
				if (newH < 4) newH = 4;

				const int maxTexSize = 2048;
				if (newW > maxTexSize || newH > maxTexSize)
				{
					double ratio = Math.Min((double)maxTexSize / newW, (double)maxTexSize / newH);
					newW = (int)Math.Round(newW * ratio);
					newH = (int)Math.Round(newH * ratio);
				}

				if (alignTo4)
				{
					newW = ((newW + 3) / 4) * 4;
					newH = ((newH + 3) / 4) * 4;
				}

				bool needsResize = (newW != originalW || newH != originalH);

				if (!needsResize)
				{
					return imageData;
				}

				using (var resized = new System.Drawing.Bitmap(newW, newH))
				{
					using (var g = System.Drawing.Graphics.FromImage(resized))
					{
						g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
						g.DrawImage(bitmap, 0, 0, newW, newH);
					}

					using (var outMs = new MemoryStream())
					{
						bool hasAlpha = HasAlphaPixels(resized);
						if (hasAlpha || imageData.Length <= 5 * 1024 * 1024)
						{
							resized.Save(outMs, System.Drawing.Imaging.ImageFormat.Png);
						}
						else
						{
							var jpegParams = new System.Drawing.Imaging.EncoderParameters(1);
							jpegParams.Param[0] = new System.Drawing.Imaging.EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 92L);
							resized.Save(outMs, GetJpegCodecInfo(), jpegParams);
						}
						return outMs.ToArray();
					}
				}
			}
		}

		private static bool HasAlphaPixels(System.Drawing.Bitmap bitmap)
		{
			var rect = new System.Drawing.Rectangle(0, 0, bitmap.Width, bitmap.Height);
			var data = bitmap.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
			try
			{
				int stride = data.Stride;
				byte[] rowData = new byte[stride];
				for (int y = 0; y < bitmap.Height; y++)
				{
					System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * stride, rowData, 0, stride);
					for (int x = 0; x < bitmap.Width; x++)
					{
						if (rowData[x * 4 + 3] < 255) return true;
					}
				}
				return false;
			}
			finally
			{
				bitmap.UnlockBits(data);
			}
		}

		private static System.Drawing.Imaging.ImageCodecInfo? GetJpegCodecInfo()
		{
			foreach (var codec in System.Drawing.Imaging.ImageCodecInfo.GetImageEncoders())
			{
				if (codec.MimeType == "image/jpeg")
					return codec;
			}
			return null;
		}

		[HarmonyPrefix]
		public static bool Prefix(string filePath, ref LoadResult status, int maxSideSize, ref Texture2D __result)
		{
			byte[]? fileData = null;
			byte[]? resizedData = null;
			try
			{
				if (GCS.internalLevelName != null || ADOBase.isBundleLevel)
					return true;

				if (!RDFile.Exists(filePath))
					return true;

				if (!Main.Settings.optimizer.enableOptimizer)
					return true;

				double scaleFactor = Main.Settings.optimizer.divideBy;
				bool dontCompress = Main.Settings.optimizer.dontCompress;
				bool alignTo4 = !Main.Settings.optimizer.dontResizeMultipleOf4;

				if (scaleFactor <= 1.01 && dontCompress && !alignTo4)
					return true;

				fileData = RDFile.ReadAllBytes(filePath, out status);
				if (fileData == null || fileData.Length == 0)
					return true;

				long fileSize = fileData.Length;
				if (fileSize < 512 * 1024 && scaleFactor <= 1.01 && !alignTo4)
					return true;

				long totalAlloc = Profiler.GetTotalAllocatedMemoryLong();
				int sysMem = SystemInfo.systemMemorySize;
				long estimatedMem = fileSize * 8L;

				if (sysMem > 0 && totalAlloc + estimatedMem > sysMem * 1024L * 1024L * 2L / 3L)
				{
					Main.Logger?.Log($"[TextureManager] Memory pressure high before {filePath}, forcing GC");
					GC.Collect();
					GC.WaitForPendingFinalizers();
					GC.Collect();
					Resources.UnloadUnusedAssets();
					totalAlloc = Profiler.GetTotalAllocatedMemoryLong();
					if (totalAlloc + estimatedMem > sysMem * 1024L * 1024L * 3L / 4L)
					{
						Main.Logger?.Log($"[TextureManager] Memory still critical, creating placeholder for {filePath}");
						var placeholder = new Texture2D(64, 64, TextureFormat.RGBA32, false);
						placeholder.name = filePath;
						placeholder.wrapMode = TextureWrapMode.Repeat;
						placeholder.Apply(false, true);
						status = LoadResult.Successful;
						__result = placeholder;
						fileData = null;
						return false;
					}
				}

				int origW = 0, origH = 0, newW = 0, newH = 0;
				bool taskSuccess = false;

				var task = Task.Run(() =>
				{
					try
					{
						resizedData = ResizeImageBytes(fileData, scaleFactor, alignTo4, out origW, out origH, out newW, out newH);
						taskSuccess = resizedData != null && resizedData.Length > 0;
					}
					catch (Exception ex)
					{
						Main.Logger?.Log($"[TextureManager] Background resize error for {filePath}: {ex.Message}");
						taskSuccess = false;
					}
				});

				if (!task.Wait(30000))
				{
					Main.Logger?.Log($"[TextureManager] Background resize timed out for {filePath}, creating placeholder");
					var placeholder = new Texture2D(64, 64, TextureFormat.RGBA32, false);
					placeholder.name = filePath;
					placeholder.wrapMode = TextureWrapMode.Repeat;
					placeholder.Apply(false, true);
					status = LoadResult.Successful;
					__result = placeholder;
					fileData = null;
					resizedData = null;
					return false;
				}

				fileData = null;

				if (!taskSuccess || resizedData == null || resizedData.Length == 0)
				{
					Main.Logger?.Log($"[TextureManager] Background resize failed for {filePath}, creating placeholder");
					var placeholder = new Texture2D(64, 64, TextureFormat.RGBA32, false);
					placeholder.name = filePath;
					placeholder.wrapMode = TextureWrapMode.Repeat;
					placeholder.Apply(false, true);
					status = LoadResult.Successful;
					__result = placeholder;
					resizedData = null;
					return false;
				}

				var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
				if (!ImageConversion.LoadImage(tex, resizedData))
				{
					UnityEngine.Object.DestroyImmediate(tex);
					Main.Logger?.Log($"[TextureManager] LoadImage failed for {filePath}, creating placeholder");
					var placeholder = new Texture2D(64, 64, TextureFormat.RGBA32, false);
					placeholder.name = filePath;
					placeholder.wrapMode = TextureWrapMode.Repeat;
					placeholder.Apply(false, true);
					status = LoadResult.Successful;
					__result = placeholder;
					resizedData = null;
					return false;
				}

				resizedData = null;

				tex.name = filePath;
				tex.wrapMode = TextureWrapMode.Repeat;

				if (!dontCompress && tex.isReadable)
				{
					ApplyCompression(tex);
				}
				else
				{
					tex.Apply(false, false);
				}

				if (origW != newW || origH != newH)
				{
					float avgRatio = ((float)origW / newW + (float)origH / newH) * 0.5f;
					// Record against the texture instance too: the name table is reset on
					// every LoadLevel while the game keeps this texture cached.
					OptimizerShared.RecordDecorRatio(tex, filePath, new Vector3(avgRatio, avgRatio, 1f));
				}

				try { OptimizerShared.textureNameMap.Add(tex, filePath); } catch { }
				OptimizerShared.MarkPrefixCompleted(tex);

				if (!Main.Settings.optimizer.dontShowSavedMemory)
				{
					long oldSizeEst = (long)origW * origH * 4L;
					long newSizeEst = Profiler.GetRuntimeMemorySizeLong(tex);
					if (newSizeEst <= 0) newSizeEst = (long)tex.width * tex.height * 4L;
					OptimizerShared.savedVRAM_MB += (oldSizeEst - newSizeEst) / 1048576f;
				}

				status = LoadResult.Successful;
				__result = tex;

				OptimizerShared.IncrementProcessedTextureCount();
				if (OptimizerShared.GetProcessedTextureCount() % OptimizerShared.GetGcInterval() == 0)
				{
					GC.Collect();
					Resources.UnloadUnusedAssets();
				}

				Main.Logger?.Log($"[TextureManager] Pre-compressed {filePath} from {origW}x{origH} to {newW}x{newH} via background thread");

				return false;
			}
			catch (Exception ex)
			{
				Main.Logger?.Log($"[TextureManager] Prefix compression failed for {filePath}: {ex.Message}");
				fileData = null;
				resizedData = null;
				return true;
			}
		}

		public static void Postfix(ref Texture2D? __result, string filePath)
		{
			if (__result == null) return;
			if (__result.width <= 32 || __result.height <= 32) return;

			if (GCS.internalLevelName != null || ADOBase.isBundleLevel)
			{
				TryFastCompress(ref __result);
				return;
			}

			if (LoadingOptimizationPatches.FrameSpreadDecorationLoadingPatch.IsLoading)
			{
				TryFastCompress(ref __result);
				return;
			}

			long oldSize = 0;
			if (!Main.Settings.optimizer.dontShowSavedMemory)
			{
				oldSize = EstimateTextureSize(__result);
			}

			if (OptimizerShared.TryGetPrefixCompleted(__result))
				return;

			string texName = __result.name;
			try
			{
				double scaleFactor = Main.Settings.optimizer.divideBy;
				if (scaleFactor <= 1.01 && Main.Settings.optimizer.dontCompress)
					return;

				int newW = (int)Math.Round(__result.width / scaleFactor);
				int newH = (int)Math.Round(__result.height / scaleFactor);

				if (newW >= 4 && newH >= 4)
				{
					if (!Main.Settings.optimizer.dontResizeMultipleOf4)
					{
						newW = AlignTo4(newW);
						newH = AlignTo4(newH);
					}
				}
				else
				{
					newW = __result.width;
					newH = __result.height;
				}

				bool resized = false;
				if (__result.width != newW || __result.height != newH)
				{
					var optimized = OptimizerShared.CreateProcessedTexture(__result, newW, newH);
					if (optimized != null)
					{
						float avgRatio = ((float)__result.width / newW + (float)__result.height / newH) * 0.5f;
						OptimizerShared.RecordDecorRatio(optimized, texName, new Vector3(avgRatio, avgRatio, 1f));
						if (!Main.Settings.optimizer.dontCompress)
						{
							try
							{
								ApplyCompression(optimized);
							}
							catch (Exception _ex)
							{
								Main.Logger?.Error($"[Optimizer] Postfix compression failed: {_ex}");
								optimized.Apply(false, false);
							}
						}
						else
						{
							optimized.Apply(false, false);
						}

						var oldTex = __result;
						if (Main.IsMainThread)
							UnityEngine.Object.DestroyImmediate(oldTex);
						else
							Main.DestroyImmediate(oldTex);

						__result = optimized;
						try { OptimizerShared.textureNameMap.Add(optimized, texName); } catch (Exception) { }
						resized = true;
					}
				}

				if (!resized)
				{
					OptimizerShared.RecordDecorRatio(__result, texName, Vector3.one);
					try { OptimizerShared.textureNameMap.Add(__result, texName); } catch { }
					if (!Main.Settings.optimizer.dontCompress)
					{
						TryFastCompress(ref __result);
					}
				}
			}
			catch (Exception e)
			{
				Main.Logger?.Log($"[Optimizer] Postfix optimization failed for {texName}: {e.Message}");
			}

			if (!Main.Settings.optimizer.dontShowSavedMemory)
			{
				long newSize = EstimateTextureSize(__result);
				OptimizerShared.savedVRAM_MB += (oldSize - newSize) / 1048576f;
			}
		}
	}
}
