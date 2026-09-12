> [!NOTE]
> Iridium supports multiple mod loaders.See README.md for platform installation.

> [!WARNING]
> Linux/macOS requires libgdiplus for UI rendering and image compression. See gdi.md to installation.

### What's New

1. **ADOFAI 3.4.0 Support**: Fully compatible with the latest game version while maintaining backward compatibility.
2. **New Perfect Hit Text**: Independent text customization for big/small P in 3.4.0 with inherited old settings and `{offset}` placeholder support.
3. **Filter & Decoration Performance**: Eliminates per-frame recalculations when idle, significantly reducing dense chart stutters (built-in animated effects like VHS remain unaffected).
4. **v2 Version Update (ADOFAI 2.9.8)**: Aligned multiple v3 features for players on 2.9.8 (including decoration caching, reduced input lag, adjustable arc angle ranges, offset placeholders, and pause-state planet trails).
5. **Fault Tolerance**: Automatically disables failing features quietly after game updates without crashing or breaking input/rendering.

### Other Changes

- **Memory Optimization**: Simplified to a single toggle with an optional virtual memory feature (Windows only, moves idle memory to disk).
- **Major Performance Boost**: Added experimental Decoration Shader Cache and Static Decoration Batching for higher FPS on heavy charts.
- **Rewritten Easing Engine**: Fixed animation overlaps/freezes, ensured all easings work properly, and resolved leftover white tracks on exit.
- **Bug Fixes**: Resolved non-functional/misdisplayed editor shortcuts, unapplied settings, and play mode crashes.
- **UI Improvements**: Reorganized settings panel with collapsible sections and clearer descriptions.

> [!CAUTION]
> Disclaimer: Optimization mods are not a silver bullet — do not chase FPS blindly. If issues occur, disable the specific feature and report the bug instead of labeling the entire mod broken.
