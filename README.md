![Iridium](https://socialify.git.ci/Xbodwf/Iridium/image?custom_description=An+optimized+mod+for+ADOFAI&custom_language=csharp&description=1&font=Lexend&forks=1&issues=1&language=1&name=1&pulls=1&stargazers=1&theme=Auto)

# Iridium

An optimized mod for A Dance of Fire and Ice, focusing on performance, visual customization, and compatibility.

[![License: LGPL v3](https://img.shields.io/badge/License-LGPL%20v3-blue.svg)](LICENSE)

[中文](README_zh-CN.md)


Welcome to join our discord server!

https://discord.gg/ddndY4xXeK

---

> [!IMPORTANT]
> Iridium is designed to enhance your "A Dance of Fire and Ice" experience through extreme performance optimization and modern visual adjustments.

## Supported Versions

- **v2 branch**: ADOFAI v2
- **v3 branch**: ADOFAI v3

---

## Features

### Performance Optimization
Improves overall smoothness and reduces lag by optimizing rendering efficiency, enhancing effect performance, and speeding up scene loading. Includes decoration texture compression, frame-spread decoration loading with progress, move track / move decorations optimizations (with freeroam support), particle optimization (object pooling / culling / LOD), DOTween tuning, a custom easing engine, and async input optimization for precise timing.

### Native Core (Rust)
The hottest loops now run in a small Rust library (`native/iridium-core`, C ABI) instead of Mono. Six hot paths are covered:

| Area | Native entry point | Measured gain |
| --- | --- | --- |
| Chart JSON parsing | `iridium_core_json_*` | 477 ms -> 82 ms on a 30k event chart |
| Easing evaluation | `iridium_core_evaluate_batch` | 3.7x on the 150k floor benchmark |
| Editor floor path rebuild | `iridium_core_remake_path_from` | full rebuild plus incremental edits from an anchor |
| Batched decoration vertices | `iridium_core_parallax_compute` | dirty tracking so unchanged slots skip `Mesh.SetVertices` |
| Decoration hit queries | `iridium_core_spatial_*` | AABB broad phase skips physics queries with no candidates |
| Texture downscaling | `iridium_core_resize_bilinear` | 1.7x-2.1x on bilinear downscale |

The library is entirely optional. It is loaded with `dlopen`/`LoadLibraryEx`, gated on an ABI version probe, and every call site falls back to the original managed code when the library is missing or too old. Panics are caught at the FFI boundary and returned as error codes, so a native bug degrades performance instead of taking down the game. Bit exactness is enforced by a Rust test suite that CI runs before every release.

### UI Customization
Offers various interface adjustments including removing the news panel, hiding the beta watermark, repositioning the autoplay text, and displaying the countdown in the editor. The v3 settings UI is organized into General / Optimizer / Editor / Compatibility / Audio tabs, with Switch/Checkbox usage aligned to their semantics, and CJK font fallback for Chinese/Japanese/Korean input and labels.

### Lobby Music
Switch between different background music tracks based on speed (BPM), with support for custom music file paths.

### Judge Text Customization
Freely customize judgment text content (e.g. "Perfect", "Too Early"), with rich text tag support and an optional offset display mode.

### Hit Sound
Hit sound pitch follows the music pitch automatically.

### Editor Enhancements
Improves editor workflow in multiple ways: performance optimizations for floor insert/delete operations on large levels (10k+ floors), customizable keyboard shortcuts for decorations and floors, and pause/resume support during auto-play preview.

### Third-Party Mod Compatibility
- **Ignore required third-party mods**: open and play charts that declare third-party mod dependencies even when those mods are missing; `requiredMods` is restored intact on save, and a notification lists the missing mods after loading.
- **Third-party custom events**: unknown event types (CustomEvent) are temporarily registered so charts load without crashing; in the editor they appear in read-only panels with their own tabs and a notice, and survive save/load untouched.

### Compatibility & Bug Fixes
Provides behavior options for legacy levels (such as legacy Flash and Camera Relative modes), along with fixes for known game issues including portal softlocks, hairpin turn beat detection, and editor replay mistake tracking.

### Patch Mode
Choose between IL Transpiler (performance-oriented) and Prefix/Postfix (compatibility-oriented) patch modes.

---

## Installation

Select your modloader below for installation instructions:

- [UnityModManager](docs/loader/umm.md)
- [MelonLoader](docs/loader/melonloader.md)
- [BepInEx](docs/loader/bepinex.md)

> [!CAUTION]
> Unless it is a specially tuned version of the mod released for older game versions, do not attempt to run Iridium on ADOFAI **2.9.7 or below**. We do not guarantee functional stability or compatibility in such cases.

---

## Build from Source

1. Ensure the .NET SDK is installed. The Rust toolchain is optional and only needed if you want to build the native core yourself, otherwise the mod builds fine without it.
2. Clone this repository with submodules:
   ```bash
   git clone --recursive https://github.com/Xbodwf/Iridium.git
   cd Iridium
   ```
3. Set your game directory path in `Iridium.csproj`.
4. Build and deploy:
   ```bash
   dotnet build
   ```

### Building the native core

`scripts/build-native.sh` compiles `iridium-core` and stages the artifact into `out/native/`, where the csproj picks it up and packs it into the release zip. Released builds already contain the `.dll`, `.so` and `.dylib` for all three platforms, so this step is only for local iteration.

```bash
# native library for the host platform
bash scripts/build-native.sh

# tests, plus the bit exactness suite CI runs before a release
cd native/iridium-core && cargo test --release
```

On macOS, `--universal` also builds the other CPU arch and merges both slices into a single fat dylib, so one file works on arm64 and Intel (Rosetta):

```bash
bash scripts/build-native.sh --universal
```

Running the benchmarks is optional and requires `cargo bench`:

```bash
cd native/iridium-core && cargo bench
```

---

## Special Thanks

Thanks to all contributors:

<a href="https://github.com/Xbodwf/Iridium/graphs/contributors">
  <img src="https://contrib.rocks/image?repo=Xbodwf/Iridium&max=200&columns=14" />
</a>

> For other contributors, see [contributors.md](contributors.md)
