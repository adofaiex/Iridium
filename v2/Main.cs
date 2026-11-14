using System.Reflection;
using System.Runtime.InteropServices;
using HarmonyLib;
using UnityEngine;
using Iridium.UI;
using Iridium.Runtime;
using Object = UnityEngine.Object;

namespace Iridium
{
    public static class Main
    {
        public static IHandler? Handler { get; private set; }
        public static Harmony? Harmony { get; private set; }
        public static Settings Settings { get; private set; } = null!;
        public static Logger? Logger;
        private static int _mainThreadId;

        public static bool IsMainThread => System.Threading.Thread.CurrentThread.ManagedThreadId == _mainThreadId;

        private static string CurrentVersion => VersionManager.GetFullVersionString();

        /// <summary>
        /// First Iridium log line: mod version, OS + ABI, runtime and the game build.
        /// Bug reports are triaged with this, and when the game ships a build commit SHA
        /// that is the authoritative identifier (the human version string is secondary).
        /// </summary>
        private static void LogStartupBanner()
        {
            try
            {
                Logger?.Log($"[Startup] Iridium {VersionManager.GetFullVersionString()} | " +
                            $"{SystemInfo.operatingSystem} ({ArchitectureName()}) | Mono | game {DescribeGameBuild()}");
            }
            catch (System.Exception ex)
            {
                Logger?.Log($"[Startup] Iridium {VersionManager.GetFullVersionString()} (startup info unavailable: {ex.Message})");
            }
        }

        private static string ArchitectureName()
        {
            try
            {
                switch (RuntimeInformation.ProcessArchitecture)
                {
                    case Architecture.X86: return "x86/i386";
                    case Architecture.X64: return "x64/amd64";
                    // ABI-style names: 32-bit ARM targets are arm-v7a, 64-bit is arm-v8a.
                    case Architecture.Arm: return "arm-v7a";
                    case Architecture.Arm64: return "arm-v8a/aarch64";
                }
            }
            catch
            {
                // IL2CPP / older profiles may not implement RuntimeInformation.
            }
            return System.Environment.Is64BitProcess ? "x64/amd64" : "x86/i386";
        }

        private static string DescribeGameBuild()
        {
            string version = Application.version;
            string commit = ReadGameBuildField("buildCommit");
            string date = ReadGameBuildField("buildDate");

            if (string.IsNullOrEmpty(commit)) return version; // older game builds have no SHA
            return string.IsNullOrEmpty(date) ? $"{version} @ {commit}" : $"{version} @ {commit} ({date})";
        }

        /// <summary>
        /// Reads Releases.buildCommit / buildDate reflectively: 2.9.8 does not ship them.
        /// Returns "" when absent or still "not-set".
        /// </summary>
        private static string ReadGameBuildField(string fieldName)
        {
            try
            {
                foreach (var assembly in System.AppDomain.CurrentDomain.GetAssemblies())
                {
                    var type = assembly.GetType("Releases", throwOnError: false);
                    var field = type?.GetField(fieldName, BindingFlags.Public | BindingFlags.Static);
                    if (field == null) continue;

                    if (field.GetValue(null) is string value
                        && !string.IsNullOrEmpty(value)
                        && !string.Equals(value, "not-set", System.StringComparison.OrdinalIgnoreCase))
                    {
                        return value;
                    }
                }
            }
            catch
            {
                // Reflection over game assemblies is best-effort only.
            }
            return "";
        }

        public static bool Initialize(IHandler handler)
        {
            _mainThreadId = System.Threading.Thread.CurrentThread.ManagedThreadId;
            Handler = handler;
            Logger = new Logger();
            LogStartupBanner();
            Settings = handler.LoadSettings<Settings>();
            Localization.Load();
            Settings.ValidateCustomEasingConflict(Settings);
            // Heal shortcut keys saved by builds whose defaults used ASCII
            // letter codes instead of Unity KeyCode values (never triggerable).
            Settings.editorShortcuts.MigrateLegacyAsciiKeyCodes();

            handler.OnToggle += OnToggle;
            handler.OnGUI += () => Settings.OnGUI();
            handler.OnSaveGUI += () => handler.SaveSettings(Settings);
            handler.OnUpdate += OnUpdate;

            Harmony = new Harmony(handler.ModId);

            // 初始化自定义缓速引擎
            Iridium.Core.CustomEasingEngine.Initialize();

            // Probe the optional native hot-path library (logs the outcome;
            // absence or ABI mismatch disables native-backed features).
            Iridium.Native.IridiumNative.Probe();

            Logger?.Log(Localization.Get("ModLoaded", Settings.language));
            return true;
        }

        // Shared Loader entry point. The legacy main runtime still owns its
        // Harmony instance; the v3 project consumes the injected backend directly.
        public static bool Initialize(IHandler handler, IRuntimeHost runtimeHost)
        {
            return Initialize(handler);
        }

        private static readonly System.Collections.Concurrent.ConcurrentQueue<System.Action> _actionQueue = new();
        private static System.Collections.Concurrent.ConcurrentQueue<Object> _destroyImmObj = new();

        public static void RunOnMainThread(System.Action action)
        {
            _actionQueue.Enqueue(action);
        }

        public static void DestroyImmediate(Object obj)
        {
            _destroyImmObj.Enqueue(obj);
        }

        private static void OnUpdate(float dt)
        {
            // IMGUI panel window: hotkey toggle + window state. Driven from here (not
            // from the loader) so it ticks exactly once per frame on every loader.
            UI.PanelShell.Tick(dt);

            while (_destroyImmObj.TryDequeue(out var obj))
            {
                Object.DestroyImmediate(obj);
            }
            while (_actionQueue.TryDequeue(out var action))
            {
                try
                {
                    action?.Invoke();
                }
                catch (System.Exception e)
                {
                    Logger?.Log($"[Main] Error in main thread action: {e}");
                }
            }
            Logger.TaskRun();

            // 大谱面加载优化 B：分帧激活砖块
            Iridium.Patches.ChunkedFloorSpawnPatch.Tick();

            // 自定义缓速引擎帧驱动
            if (Settings.optimizer.enableCustomEasingEngine)
                Iridium.Core.CustomEasingEngine.Update(dt);
        }

        private static void OnToggle(bool value)
        {
            if (value)
            {
                Logger?.Log(Localization.Get("ModEnabled"));

                Iridium.Patches.AsyncPatchManager.Start();
                Iridium.Patches.AsyncPatchManager.UpdateAllPatchesAsync();

                if (Main.Settings.asyncInput.enableAIO)
                    Modules.AsyncInputOptimize.Main.Enable();

                if (Main.Settings.optimizer.enableOptimizer)
                {
                    Iridium.Patches.OptimizerPatches.ResetDecorOptimization(true);
                    if (Main.Settings.optimizer.optimizeDOTweenGlobal)
                    {
                        Iridium.Patches.DOTweenOptimizationPatches.ApplyRuntimeSettings();
                    }
                }

                if (Main.Settings.firstRun)
                {
                    UI.MainWindow.ShowFirstRun();
                }
                else if (Main.Settings.lastVersion != CurrentVersion
                    && (string.IsNullOrEmpty(Main.Settings.lastUpgradeMessageSeen_106_beta5)
                        || Main.Settings.lastUpgradeMessageSeen_106_beta5 != "1.0.6_beta5"))
                {
                    UI.MainWindow.ShowUpgrade("UpgradeMessage_1_0_6_beta5");
                }
            }
            else
            {
                Logger?.Log(Localization.Get("ModDisabled"));

                Modules.AsyncInputOptimize.Main.Disable();
                Iridium.Patches.AsyncPatchManager.Stop();
                Iridium.Patches.PatchManager.UnpatchAll();
            }
        }
    }
}
