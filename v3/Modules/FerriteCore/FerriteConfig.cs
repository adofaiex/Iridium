namespace Iridium.Modules.FerriteCore
{
    /// <summary>
    /// Loaded from the standalone Config/FerriteCore.json, deliberately outside
    /// Settings.xml (which only carries the 基础优化 master switch).
    /// The defaults below ARE that switch's basic profile: safe tuning only.
    /// Framerate / vsync / audio / L1 renderer sleep stay opt-in here.
    /// </summary>
    public class FerriteConfig
    {
        /// <summary>2 = basic-profile defaults (incremental GC, scene-switch GC, shadow cap).</summary>
        public int configVersion = 2;

        public L0Settings L0 { get; set; } = new();
        public L1Settings L1 { get; set; } = new();
    }

    /// <summary>L0: engine-level tuning (no Harmony on game methods).</summary>
    public class L0Settings
    {
        public bool enableEngineTune = true;

        public int targetFrameRate = 0;
        public bool forceVSyncCount = false;
        public int vsyncCount = 0;

        public bool enableQualityPreset = false;
        public int qualityPreset = 0;

        public bool limitShadowDistance = true;
        public float shadowDistance = 30f;

        public bool enableIncrementalGC = true;
        public bool gcOnSceneSwitch = true;

        public bool limitFixedTimestep = false;
        public float fixedTimestep = 0.01f;
        public bool limitMaxAllowedTimestep = false;
        public float maxAllowedTimestep = 0.1f;

        public bool tuneAudioBuffer = false;
        public int audioBufferSize = 512;
    }

    /// <summary>L1: soft optimizations (renderer sleep).</summary>
    public class L1Settings
    {
        public bool enableL1 = false;

        public bool sleepOffscreenRenderers = false;
        public float sleepDistance = 30f;
    }
}
