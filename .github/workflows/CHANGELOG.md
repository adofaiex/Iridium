> [!NOTE]
> Iridium supports multiple mod loaders.See README.md for platform installation.

> [!WARNING]
> Linux/macOS requires libgdiplus for UI rendering and image compression. See gdi.md to installation.

## CHANGES

### Fixes

1. Fixed the settings panel scaling on high-resolution displays. On a fresh install the panel used to default to a near-fullscreen window, which overflowed and covered almost the whole screen on 4K monitors. It now opens at a fixed design size (1280×840) on large screens; smaller screens still fit proportionally.
2. Fixed a saved window geometry from another resolution or device being restored larger than the screen, leaving both edges cut off. The size is now clamped back into the visible area on restore.

> [!CAUTION]
> Disclaimer: Optimization mods are not a silver bullet — do not chase FPS blindly. If issues occur, disable the specific feature and report the bug instead of labeling the entire mod broken.
