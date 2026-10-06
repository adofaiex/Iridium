> [!NOTE]
> Iridium supports multiple mod loaders.See README.md for platform installation.

> [!WARNING]
> Linux/macOS requires libgdiplus for UI rendering and image compression. See gdi.md to installation.

## CHANGES

1. We rewrote part of the mod in Rust, a faster compiled language. Loading levels, custom easing animations, updating paths in the editor, parallax decorations and hitbox checks now all run faster and use less CPU.
2. One download works everywhere: the package now includes the new code for Windows, Linux and macOS (both Intel and Apple Silicon), and the mod picks the right one automatically when it starts.
3. If the new code can't run on your system, the mod falls back to the old code and works exactly as before — just without the extra speed.

> [!CAUTION]
> Disclaimer: Optimization mods are not a silver bullet — do not chase FPS blindly. If issues occur, disable the specific feature and report the bug instead of labeling the entire mod broken.
