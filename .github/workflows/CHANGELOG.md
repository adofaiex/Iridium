> [!NOTE]
> Iridium supports multiple mod loaders.See README.md for platform installation.

> [!WARNING]
> Linux/macOS requires libgdiplus for UI rendering and image compression. See gdi.md to installation.

## CHANGES

1. Support loading, editing and saving adofai 3.x charts on the 2.9.8 version (v3-only properties are preserved).
2. Editor: much faster tile insert / delete and tile switching on large charts.
3. Loading: large charts load faster (lazy effects and floor numbers, chunked activation).
4. Lower memory usage on very large charts.
5. Adjustable render resolution — lower it a bit for higher FPS (v2 / v3).
6. MiniModLoader(A Third-party mod loader) support.
7. Faster decoration hitbox detection, and decoration scaling while playing.

> [!CAUTION]
> Disclaimer: Optimization mods are not a silver bullet — do not chase FPS blindly. If issues occur, disable the specific feature and report the bug instead of labeling the entire mod broken.
