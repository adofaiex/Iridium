> [!NOTE]
> Iridium supports multiple mod loaders — see README.md for platform-specific installation instructions.

> [!WARNING]
> On Linux/macOS, this mod may require libgdiplus to be installed separately. It is used for UI rendering (icons, buttons, switches) as well as image compression.

### Performance

1. **New (Experimental): Static Decoration Batching** (Rendering section). Static, unfiltered decorations are merged into a handful of batched meshes — one per atlas page — instead of one renderer per decoration. On charts with tens of thousands of decorations this collapses thousands of draw calls into a few. Requires the custom easing engine (it provides tween awareness); decorations with filters, masks, tiling or non-default blend modes keep the vanilla rendering path, and decorations automatically leave and rejoin the batch as tweens start and end.
2. **New (Experimental): Decoration Filter Cache.** Static decorations with filters no longer re-run their filter render chain every frame — the filtered result only depends on the source texture and the enabled filter set, so it is reused until something changes. If a filter animates its own parameters, disable this option.

Both options are experimental: if anything looks misplaced, stacked in the wrong order or frozen, disable the option and please report it to us with the chart name.
