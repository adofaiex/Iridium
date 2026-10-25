using System;
using System.IO;
using System.Reflection;
using HarmonyLib;
using Newtonsoft.Json;
using UnityEngine;

namespace Iridium.Modules.FerriteCore
{
    /// <summary>
    /// FerriteCore-style memory/engine tuning embedded in Iridium.
    /// Config lives in a standalone Config/FerriteCore.json and does not participate
    /// in Settings.xml. Fully optional: when the 基础优化 switch is off, nothing is
    /// applied and no Harmony patches are installed.
    /// </summary>
    public static class FerriteCoreModule
    {
        public static FerriteConfig Config { get; private set; } = new();
        public static bool IsEnabled { get; private set; }
        public static bool IsActive { get; private set; }

        private static Harmony? _harmony;
        private static Harmony? _coreHarmony;
        private static bool _originalsCaptured;
        private static int _origFrameRate;
        private static int _origVsync;
        private static int _origQualityLevel;
        private static float _origShadowDist;
        private static float _origFixedDT;
        private static float _origMaxDT;
        private static System.Runtime.GCLatencyMode _origGCMode;

        public static string ConfigPath => Path.Combine(
            Main.Handler?.ModPath ?? ".",
            "Config", "FerriteCore.json");

        /// <summary>Reload config from disk and (re)apply if enabled.</summary>
        public static void ReloadConfig()
        {
            try
            {
                if (File.Exists(ConfigPath))
                {
                    var json = File.ReadAllText(ConfigPath);
                    var parsed = JsonConvert.DeserializeObject<FerriteConfig>(json);
                    if (parsed != null)
                    {
                        Config = parsed;
                        MigrateConfig();
                    }
                }
                else
                {
                    Config = new FerriteConfig();
                    EnsureDefaultConfig();
                    Main.Logger?.Log($"[FerriteCore] config not found, wrote defaults: {ConfigPath}");
                }
            }
            catch (Exception ex)
            {
                Main.Logger?.Error($"[FerriteCore] Failed to load config: {ex.Message}");
            }
        }

        /// <summary>
        /// Bring pre-rework config files up to the basic profile: the three safe
        /// defaults the 基础优化 switch promises were all opt-in before.
        /// </summary>
        private static void MigrateConfig()
        {
            if (Config.configVersion >= 2) return;
            Config.L0.enableIncrementalGC = true;
            Config.L0.gcOnSceneSwitch = true;
            Config.L0.limitShadowDistance = true;
            Config.configVersion = 2;
            EnsureDefaultConfig();
            Main.Logger?.Log("[FerriteCore] migrated config to basic profile (v2)");
        }

        private static void EnsureDefaultConfig()
        {
            try
            {
                var dir = Path.GetDirectoryName(ConfigPath);
                if (dir != null) Directory.CreateDirectory(dir);
                var json = JsonConvert.SerializeObject(Config, Formatting.Indented);
                File.WriteAllText(ConfigPath, json);
            }
            catch (Exception ex)
            {
                Main.Logger?.Error($"[FerriteCore] Failed to write default config: {ex.Message}");
            }
        }

        public static void Enable()
        {
            if (IsActive) return;
            IsEnabled = true;
            ReloadConfig();
            Apply();
            IsActive = true;
            Main.Logger?.Log("[FerriteCore] enabled");
        }

        public static void Disable()
        {
            if (!IsActive && !IsEnabled) return;
            Revert();
            IsEnabled = false;
            IsActive = false;
            Main.Logger?.Log("[FerriteCore] disabled");
        }

        // L0: engine-level tuning

        public static void Apply()
        {
            if (!Config.L0.enableEngineTune) return;

            CaptureOriginals();

            var s = Config.L0;

            if (s.enableQualityPreset)
            {
                ApplyQualityPreset(s.qualityPreset);
            }
            else
            {
                Application.targetFrameRate = s.targetFrameRate > 0 ? s.targetFrameRate : _origFrameRate;

                QualitySettings.vSyncCount = s.forceVSyncCount ? s.vsyncCount : _origVsync;

                QualitySettings.shadowDistance = s.limitShadowDistance ? s.shadowDistance : _origShadowDist;
            }

            System.Runtime.GCSettings.LatencyMode = s.enableIncrementalGC
                ? System.Runtime.GCLatencyMode.LowLatency
                : _origGCMode;

            Time.fixedDeltaTime = s.limitFixedTimestep ? s.fixedTimestep : _origFixedDT;
            Time.maximumDeltaTime = s.limitMaxAllowedTimestep ? s.maxAllowedTimestep : _origMaxDT;

            if (s.tuneAudioBuffer)
                ApplyAudioBuffer(s.audioBufferSize);

            // Core Harmony patch (scene-switch GC) — independent of L1
            ApplyCorePatches();

            // L1 Harmony patches (renderer sleep)
            if (Config.L1.enableL1)
                ApplyL1Patches();
        }

        public static void Revert()
        {
            if (!_originalsCaptured) return;
            Application.targetFrameRate = _origFrameRate;
            QualitySettings.vSyncCount = _origVsync;
            QualitySettings.SetQualityLevel(_origQualityLevel);
            QualitySettings.shadowDistance = _origShadowDist;
            System.Runtime.GCSettings.LatencyMode = _origGCMode;
            Time.fixedDeltaTime = _origFixedDT;
            Time.maximumDeltaTime = _origMaxDT;

            RemoveCorePatches();
            RemoveL1Patches();
        }

        private static void CaptureOriginals()
        {
            if (_originalsCaptured) return;
            _origFrameRate = Application.targetFrameRate;
            _origVsync = QualitySettings.vSyncCount;
            _origQualityLevel = QualitySettings.GetQualityLevel();
            _origShadowDist = QualitySettings.shadowDistance;
            _origGCMode = System.Runtime.GCSettings.LatencyMode;
            _origFixedDT = Time.fixedDeltaTime;
            _origMaxDT = Time.maximumDeltaTime;
            _originalsCaptured = true;
        }

        public static void TriggerSceneGC()
        {
            if (!Config.L0.gcOnSceneSwitch) return;
            GC.Collect(2, GCCollectionMode.Optimized, false);
        }

        private static void ApplyAudioBuffer(int bufferSize)
        {
            try
            {
                var config = AudioSettings.GetConfiguration();
                config.dspBufferSize = bufferSize;
                var method = typeof(AudioSettings).GetMethod(
                    "SetConfiguration",
                    BindingFlags.NonPublic | BindingFlags.Static,
                    null,
                    new[] { typeof(AudioConfiguration) },
                    null);
                method?.Invoke(null, new object[] { config });
            }
            catch (Exception ex)
            {
                Main.Logger?.Error($"[FerriteCore] Failed to set audio buffer: {ex.Message}");
            }
        }

        private static void ApplyQualityPreset(int preset)
        {
            switch (preset)
            {
                case 0: // Performance
                    Application.targetFrameRate = 60;
                    QualitySettings.vSyncCount = 0;
                    QualitySettings.SetQualityLevel(0);
                    QualitySettings.shadowDistance = 30f;
                    QualitySettings.antiAliasing = 0;
                    QualitySettings.anisotropicFiltering = AnisotropicFiltering.Disable;
                    break;
                case 1: // Balanced
                    Application.targetFrameRate = 120;
                    QualitySettings.vSyncCount = 0;
                    QualitySettings.SetQualityLevel(3);
                    QualitySettings.shadowDistance = 70f;
                    QualitySettings.antiAliasing = 2;
                    QualitySettings.anisotropicFiltering = AnisotropicFiltering.Enable;
                    break;
                case 2: // Quality
                    Application.targetFrameRate = -1;
                    QualitySettings.vSyncCount = 1;
                    QualitySettings.SetQualityLevel(5);
                    QualitySettings.shadowDistance = 150f;
                    QualitySettings.antiAliasing = 4;
                    QualitySettings.anisotropicFiltering = AnisotropicFiltering.ForceEnable;
                    break;
            }
        }

        // Core patch: scene-switch GC

        private static void ApplyCorePatches()
        {
            if (_coreHarmony != null) return;
            _coreHarmony = new Harmony("Iridium.FerriteCore.Core");
            try
            {
                _coreHarmony.CreateClassProcessor(typeof(ScnGameDestroyGC)).Patch();
            }
            catch (Exception ex)
            {
                Main.Logger?.Error($"[FerriteCore] Failed to apply core patches: {ex.Message}");
            }
        }

        private static void RemoveCorePatches()
        {
            if (_coreHarmony == null) return;
            try { _coreHarmony.UnpatchAll("Iridium.FerriteCore.Core"); }
            catch { /* ignore */ }
            _coreHarmony = null;
        }

        // L1: soft optimizations

        private static void ApplyL1Patches()
        {
            if (_harmony != null) return;
            _harmony = new Harmony("Iridium.FerriteCore.L1");
            try
            {
                _harmony.CreateClassProcessor(typeof(PlanetRendererSleep)).Patch();
                _harmony.CreateClassProcessor(typeof(FloorRendererSleep)).Patch();
                _harmony.CreateClassProcessor(typeof(DecorationRendererSleep)).Patch();
            }
            catch (Exception ex)
            {
                Main.Logger?.Error($"[FerriteCore] Failed to apply L1 patches: {ex.Message}");
            }
        }

        private static void RemoveL1Patches()
        {
            if (_harmony == null) return;
            try { _harmony.UnpatchAll("Iridium.FerriteCore.L1"); }
            catch { /* ignore */ }
            _harmony = null;
        }

        private static bool L1Active => Config.L1.enableL1;

        private static bool ShouldSleep()
        {
            if (!L1Active) return false;
            if (!Config.L1.sleepOffscreenRenderers) return false;
            return true;
        }

        private static void HandleSleep(GameObject go, Vector3 pos)
        {
            if (go == null) return;
            var cam = Camera.main;
            if (cam == null) return;

            var dist = (pos - cam.transform.position).sqrMagnitude;
            var threshold = Config.L1.sleepDistance;
            var shouldShow = dist < threshold * threshold;

            var renderer = go.GetComponent<Renderer>();
            if (renderer != null && renderer.enabled != shouldShow)
                renderer.enabled = shouldShow;
        }

        [HarmonyPatch(typeof(scrPlanet), "Update")]
        public static class PlanetRendererSleep
        {
            public static void Postfix(scrPlanet __instance)
            {
                if (!ShouldSleep()) return;
                HandleSleep(__instance.gameObject, __instance.transform.position);
            }
        }

        [HarmonyPatch(typeof(scrFloor), "Update")]
        public static class FloorRendererSleep
        {
            public static void Postfix(scrFloor __instance)
            {
                if (!ShouldSleep()) return;
                HandleSleep(__instance.gameObject, __instance.transform.position);
            }
        }

        [HarmonyPatch(typeof(scrDecoration), "Update")]
        public static class DecorationRendererSleep
        {
            public static void Postfix(scrDecoration __instance)
            {
                if (!ShouldSleep()) return;
                HandleSleep(__instance.gameObject, __instance.transform.position);
            }
        }

        [HarmonyPatch(typeof(scnGame), "OnDestroy")]
        public static class ScnGameDestroyGC
        {
            public static void Postfix()
            {
                if (!Config.L0.gcOnSceneSwitch) return;
                TriggerSceneGC();
            }
        }
    }
}
