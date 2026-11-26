> [!NOTE]
> Iridium supports multiple mod loaders.See README.md for platform installation.

> [!WARNING]
> Linux/macOS requires libgdiplus for UI rendering and image compression. See gdi.md to installation.

## CHANGES

### Fixes

1. Fixed rounded track corners sometimes falling back to the vanilla right angle. Floating-point error could put an exact 90° turn just below the configured range, so roughly one corner orientation in five was drawn square. On v2 the corner range now also matches v3: the upper bound is exclusive and capped just under 180°.
2. Fixed the custom easing engine misplacing or hiding tracks. The native OutQuint curve overshot to twice the distance, and Flash eases did not round their amplitude the way DOTween does; both now match DOTween exactly.
3. When the native library is missing, the custom easing engine now falls back to DOTween's own easing formulas, with a one-time warning in the log, instead of jumping straight to the end of every tween.
4. Fixed the decoration filter part of the gameplay allocation optimization never taking effect: its patch produced invalid code and was silently skipped. It now applies whenever that optimization is enabled.
5. Fixed the AIO scene-load hook running after the method had already returned, so AIO timing is now actually reset when a scene loads.
6. Several patches now keep jump targets intact when they replace or insert instructions, so they stay valid if a future game update changes the patched code.

### New

7. Added a native library build for bionic ARM64 (arm64-v8a). It ships in every release zip, and both v2 and v3 load it automatically where the game runs on bionic.

> [!CAUTION]
> Disclaimer: Optimization mods are not a silver bullet — do not chase FPS blindly. If issues occur, disable the specific feature and report the bug instead of labeling the entire mod broken.
