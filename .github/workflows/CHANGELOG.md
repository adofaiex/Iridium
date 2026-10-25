> [!NOTE]
> Iridium supports multiple mod loaders.See README.md for platform installation.

> [!WARNING]
> Linux/macOS requires libgdiplus for UI rendering and image compression. See gdi.md to installation.

## CHANGES

1. The settings window is now a real UGUI panel instead of IMGUI: it opens without a hitch, scrolls smoothly, can be dragged and resized from any edge or corner, and remembers its position and size between sessions.
2. The panel is available on every loader now (MelonLoader / BepInEx / UnityModManager / MiniModLoader), and its hotkey is a normal setting under General → Panel hotkey (default **Ctrl+F9**). Under UMM the old settings page only shows a reminder of the current hotkey.
3. Shortcut recording is now click-to-start and click-to-save: press the button, type the combination (shown live on the button), then click anywhere or press the button again to save. Esc keeps the old value. Function keys and modifier combinations are recorded correctly.
4. Reopening a chart from the editor no longer shrinks image decorations: the texture scale is remembered per texture instead of being reset on every level load (v2 / v3).
5. Fixed the settings panel not opening with the hotkey under MelonLoader and BepInEx.
6. MelonLoader now enables the mod at startup, so the optimizer patches are actually applied there — previously nothing was patched under MelonLoader.
7. Fixed patch errors on MelonLoader caused by a Harmony API version mismatch.
8. UI fixes: buttons no longer render as plain white rectangles, nested (indented) rows line up with their parent, long descriptions wrap instead of being cut off, and sliders, switches, checkboxes, info icons and section arrows all use the same theme values as before.
9. Added a vertical scrollbar in the panel's colours, and the option list keeps its scroll position across rebuilds.
10. v2 (ADOFAI 2.9.8) keeps the existing IMGUI settings window; the UGUI panel, resizing and position memory are v3 only.

> [!CAUTION]
> Disclaimer: Optimization mods are not a silver bullet — do not chase FPS blindly. If issues occur, disable the specific feature and report the bug instead of labeling the entire mod broken.
