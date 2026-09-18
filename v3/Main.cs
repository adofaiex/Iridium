using System.Reflection;
using Iridium.Runtime;
using UnityEngine;

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

        public static bool Initialize(IHandler handler, IRuntimeHost runtimeHost)
        {
            _mainThreadId = System.Threading.Thread.CurrentThread.ManagedThreadId;
            Handler = handler;
            Logger = new Logger();
            Settings = handler.LoadSettings<Settings>();
            Localization.Load();
            // Migrate pre-rework memory settings: the old FerriteCore master
            // switch folds into the unified basic-optimization switch.
            if (Settings.memory.enableFerriteCore)
            {
                Settings.memory.enableBasicOptimization = true;
                Settings.memory.enableFerriteCore = false;
            }
            Settings.ValidateCustomEasingConflict(Settings);
            Settings.MigrateJudgeText(Settings);
            // Heal shortcut keys saved by builds whose defaults used ASCII
            // letter codes instead of Unity KeyCode values (never triggerable).
            Settings.editorShortcuts.MigrateLegacyAsciiKeyCodes();

            handler.OnToggle += OnToggle;
            handler.OnGUI += () => Settings.OnGUI();
            handler.OnSaveGUI += () => handler.SaveSettings(Settings);
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
                // （默认存档里已开启的配置，之前只在设置回调里生效 → 启动后从不激活）
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
