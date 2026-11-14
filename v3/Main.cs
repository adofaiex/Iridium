using System;
using System.Reflection;
using System.Runtime.InteropServices;
using Iridium.Runtime;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Iridium
{
    public static class Main
    {
        public static IHandler? Handler { get; private set; }
        public static IRuntimeHost? RuntimeHost { get; private set; }
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
        private static void LogStartupBanner(IRuntimeHost runtimeHost)
        {
            try
            {
                string runtime = runtimeHost != null ? runtimeHost.Runtime.ToString() : "Mono";
                Logger?.Log($"[Startup] Iridium {VersionManager.GetFullVersionString()} | " +
                            $"{SystemInfo.operatingSystem} ({ArchitectureName()}) | {runtime} | game {DescribeGameBuild()}");
            }
            catch (Exception ex)
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
            return Environment.Is64BitProcess ? "x64/amd64" : "x86/i386";
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
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    var type = assembly.GetType("Releases", throwOnError: false);
                    var field = type?.GetField(fieldName, BindingFlags.Public | BindingFlags.Static);
                    if (field == null) continue;

                    if (field.GetValue(null) is string value
                        && !string.IsNullOrEmpty(value)
                        && !string.Equals(value, "not-set", StringComparison.OrdinalIgnoreCase))
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

        public static bool Initialize(IHandler handler, IRuntimeHost runtimeHost)
        {
            _mainThreadId = System.Threading.Thread.CurrentThread.ManagedThreadId;
            Handler = handler;
            Logger = new Logger();
            LogStartupBanner(runtimeHost);
            Settings = handler.LoadSettings<Settings>();
            Localization.Load();
            // Pre-rework builds stored the FerriteCore master switch; it folds into the
            // unified basic-optimization switch.
            if (Settings.memory.enableFerriteCore)
            {
                Settings.memory.enableBasicOptimization = true;
                Settings.memory.enableFerriteCore = false;
            }
            Settings.ValidateCustomEasingConflict(Settings);
            Settings.MigrateJudgeText(Settings);
            Settings.ValidatePanelHotkey(Settings);
            // Heal shortcut keys saved by builds whose defaults used ASCII
            // letter codes instead of Unity KeyCode values (never triggerable).
            Settings.editorShortcuts.MigrateLegacyAsciiKeyCodes();

            handler.OnToggle += OnToggle;
            // UMM/MiniModLoader still call IHandler.OnGUI to draw their own IMGUI settings
            // page. UGUI cannot be embedded there, so v3 draws a one-line hotkey hint (see
            // Settings.OnGUI). MelonLoader/BepInEx go through PanelShell.RenderGui, which is
            // a no-op in v3, so the hint never appears there.
            handler.OnGUI += () => Settings.OnGUI();
            handler.OnUpdate += OnUpdate;

            RuntimeHost = runtimeHost;
            RuntimeHost.Initialize(handler.ModId);
            RuntimeHost.PatchBackend.SetPerformanceMode(Settings.patchMode.useILPatch);

            // 补丁运行时异常隔离：patch 内抛出的异常记录后吞掉，不破坏游戏热路径
            Iridium.Runtime.PatchExceptionGuard.ErrorLogger = msg => Logger?.Error(msg);
            Iridium.Runtime.PatchExceptionGuard.WarnLogger = msg => Logger?.Warning(msg);

            // 预加载 UI 纹理资源，避免首次打开面板时卡顿
            Iridium.UI.IridiumLayout.EnsureTexturesAlive();

            // 初始化自定义缓速引擎
            Iridium.Core.CustomEasingEngine.Initialize();

            // Probe the optional native library; absence or ABI mismatch falls back to C#.
            Iridium.Native.IridiumNative.Probe();

            Logger?.Log(Localization.Get("ModLoaded", Settings.language));
            return true;
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
            // UGUI panel: hotkey + window chrome, then the settings tree. Guarded so a
            // UI hiccup can never starve the patch/main-thread work below (that queue is
            // how AsyncPatchManager applies pending patches).
            try
            {
                Iridium.UI.PanelShell.Tick(dt);
                Settings?.TickUI();
            }
            catch (System.Exception ex)
            {
                Logger?.Error($"[Main] UI tick failed: {ex.Message}");
            }

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
            Iridium.Patches.Optimizer.ChunkedFloorSpawnPatch.Tick();

            // 自定义缓速引擎帧驱动；开关关闭后继续驱动在飞的 tween 直到自然结束
            // （补丁已卸载不会再创建新的），避免精灵卡在中间状态。
            if (Settings.optimizer.enableCustomEasingEngine || Iridium.Core.CustomEasingEngine.ActiveCount > 0)
            {
                Iridium.Core.CustomEasingEngine.Update(dt);
            }

            // 静态装饰物合批渲染（每帧提交）
            if (Settings.optimizer.enableStaticDecorationBatching
                && Settings.optimizer.enableCustomEasingEngine)
            {
                Patches.Optimizer.StaticDecorationBatcher.FrameRender();
            }
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

                if (Main.Settings.memory.enableBasicOptimization)
                    Modules.FerriteCore.FerriteCoreModule.Enable();
                Modules.FerriteCore.VirtualMemoryOptimizer.SetEnabled(
                    Main.Settings.memory.enableVirtualMemoryOptimization);

                if (Main.Settings.optimizer.enableOptimizer)
                {
                    Iridium.Patches.Optimizer.OptimizerShared.ResetDecorOptimization(true);
                    if (Main.Settings.optimizer.optimizeDOTweenGlobal)
                    {
                        Iridium.Patches.DOTweenOptimizationPatches.ApplyRuntimeSettings();
                    }
                }

                // 静态装饰物合批：依赖缓速引擎，二者都开启时随启动激活
                if (Main.Settings.optimizer.enableStaticDecorationBatching
                    && Main.Settings.optimizer.enableCustomEasingEngine)
                    Patches.Optimizer.StaticDecorationBatcher.SetEnabled(true);

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

                Iridium.UI.PanelShell.SetVisible(false);

                Modules.FerriteCore.FerriteCoreModule.Disable();
                Modules.FerriteCore.VirtualMemoryOptimizer.SetEnabled(false);
                Patches.Optimizer.StaticDecorationBatcher.SetEnabled(false);
                Modules.AsyncInputOptimize.Main.Disable();
                Iridium.Patches.AsyncPatchManager.Stop();
                Iridium.Patches.PatchManager.UnpatchAll();
            }
        }
    }
}
