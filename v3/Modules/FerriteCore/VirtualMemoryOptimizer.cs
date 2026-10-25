using System;
using System.Runtime.InteropServices;
using HarmonyLib;
using UnityEngine;

namespace Iridium.Modules.FerriteCore
{
    /// <summary>
    /// Advanced memory optimization: page inactive process memory out to the page file.
    /// Managed GC + Resources.UnloadUnusedAssets first, then trim the working set (Windows only);
    /// Mono/Boehm does not return physical pages after GC, so the trim is what actually frees.
    /// </summary>
    public static class VirtualMemoryOptimizer
    {
        private static Harmony? _harmony;

        public static bool IsWindows =>
            System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        public static void SetEnabled(bool enabled)
        {
            if (enabled) Apply();
            else Remove();
        }

        private static void Apply()
        {
            if (_harmony != null) return;
            _harmony = new Harmony("Iridium.FerriteCore.VirtualMemory");
            try
            {
                _harmony.CreateClassProcessor(typeof(LevelLoadTrimPatch)).Patch();
                _harmony.CreateClassProcessor(typeof(EditorEnterTrimPatch)).Patch();
                Main.Logger?.Log(IsWindows
                    ? "[Memory] Virtual memory optimization enabled (working set trim available)"
                    : "[Memory] Virtual memory optimization enabled, but working set trim is Windows-only; managed cleanup will still run");
            }
            catch (Exception ex)
            {
                Main.Logger?.Error($"[Memory] Failed to apply virtual memory patches: {ex.Message}");
            }
        }

        private static void Remove()
        {
            if (_harmony == null) return;
            try { _harmony.UnpatchAll("Iridium.FerriteCore.VirtualMemory"); }
            catch { /* ignore */ }
            _harmony = null;
            Main.Logger?.Log("[Memory] Virtual memory optimization disabled");
        }

        /// <summary>
        /// 清理 + 裁剪入口。任何线程都可调用，但 Unity 资源卸载回调
        /// 与 Harmony postfix 都发生在主线程。
        /// </summary>
        public static void Trim(string reason)
        {
            // 1) 托管侧：把死对象交给 GC（Boehm 不归还物理页，仅标记可复用）
            try
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }
            catch (Exception ex)
            {
                Main.Logger?.Error($"[Memory] GC failed during trim ({reason}): {ex.Message}");
            }

            // 2) 引擎侧：卸载无引用资源，完成后立刻裁剪工作集，
            //    这样被卸载的页才会被挤出物理内存
            try
            {
                var op = Resources.UnloadUnusedAssets();
                if (op != null)
                {
                    op.completed += _ => TrimWorkingSet(reason);
                    return;
                }
            }
            catch (Exception ex)
            {
                Main.Logger?.Error($"[Memory] UnloadUnusedAssets failed ({reason}): {ex.Message}");
            }

            TrimWorkingSet(reason);
        }

        private static void TrimWorkingSet(string reason)
        {
            if (!IsWindows) return;
            try
            {
                if (SetProcessWorkingSetSize(new IntPtr(-1), new IntPtr(-1), new IntPtr(-1)))
                {
                    Main.Logger?.Log($"[Memory] Working set trimmed ({reason})");
                }
                else
                {
                    Main.Logger?.Error($"[Memory] SetProcessWorkingSetSize failed ({reason}), Win32Error={Marshal.GetLastWin32Error()}");
                }
            }
            catch (DllNotFoundException ex)
            {
                Main.Logger?.Error($"[Memory] kernel32 P/Invoke unavailable ({reason}): {ex.Message}");
            }
            catch (Exception ex)
            {
                Main.Logger?.Error($"[Memory] Working set trim failed ({reason}): {ex.Message}");
            }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetProcessWorkingSetSize(IntPtr hProcess, IntPtr min, IntPtr max);

        [HarmonyPatch(typeof(scnGame), "LoadLevel")]
        private static class LevelLoadTrimPatch
        {
            private static void Postfix()
            {
                if (Main.Settings?.memory.vmTrimOnLevelLoad == true)
                    Trim("level-load");
            }
        }

        [HarmonyPatch(typeof(scnEditor), "Awake")]
        private static class EditorEnterTrimPatch
        {
            private static void Postfix()
            {
                if (Main.Settings?.memory.vmTrimOnEditorEnter == true)
                    Trim("editor-enter");
            }
        }
    }
}
