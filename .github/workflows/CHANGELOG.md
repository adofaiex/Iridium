> [!NOTE]
> Iridium supports multiple mod loaders.See README.md for platform installation.

> [!WARNING]
> Linux/macOS requires libgdiplus for UI rendering and image compression. See gdi.md to installation.

## CHANGES

1. Restart after fail: floors that are already at their start state are skipped, and hold objects are reused instead of destroyed and rebuilt (v2 / v3).
2. Lower per-frame overhead: holds skip material writes while their geometry is unchanged, each floor runs OnBeat once per beat instead of twice, and the audio spectrum is computed once per frame and shared with the conductor, volume trackers and background bars.
3. Faster editor interaction: selecting floors or decorations and opening panels no longer deep-copies the whole level, and decoration / event lookups are indexed instead of scanning every event (noticeable on charts with hundreds of thousands of events).
4. Hit texts are created on demand instead of pre-building about 1400 of them when a level loads (v3).
5. Search across all settings: type in the search box to filter every option, then click a result to jump straight to it.
6. The optimizer is split into four categories (basics / gameplay / effects / advanced), with clearer sections and parent-child option grouping.
7. Option descriptions are rewritten for players, and the info icon is now placed consistently next to the option text.
8. v2 (ADOFAI 2.9.8) receives the restart and editor optimizations from items 1 and 3.
9. Fixed a freeze when switching settings categories, and fixed broken UI files being rendered silently.

> [!CAUTION]
> Disclaimer: Optimization mods are not a silver bullet — do not chase FPS blindly. If issues occur, disable the specific feature and report the bug instead of labeling the entire mod broken.
