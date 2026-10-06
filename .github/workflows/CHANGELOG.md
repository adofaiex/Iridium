> [!NOTE]
> Iridium supports multiple mod loaders.See README.md for platform installation.

> [!WARNING]
> Linux/macOS requires libgdiplus for UI rendering and image compression. See gdi.md to installation.

## CHANGES

1. The hot paths now run natively: easing evaluation, floor-path rebuilds, level JSON parsing, hitbox checks, parallax scrolling and texture downscaling were moved from C# into a compiled Rust library. Level loading and per-frame CPU usage both drop, with every result verified byte-for-byte identical to the old managed code.
2. One package for every platform: release zips now bundle the native library for Windows, Linux and macOS (both Intel and Apple Silicon) side by side — no separate download per platform. The mod detects and loads the right one at startup.
3. Graceful fallback kept: if the native library is missing or fails to load for any reason, the mod silently continues on its built-in managed code — you lose the speedup, never functionality.
4. Releases are gated by tests: CI builds all three native libraries and runs the native test suite (including the bit-exactness checks against the managed reference) before packaging; a failing kernel blocks the release instead of shipping.

> [!CAUTION]
> Disclaimer: Optimization mods are not a silver bullet — do not chase FPS blindly. If issues occur, disable the specific feature and report the bug instead of labeling the entire mod broken.
