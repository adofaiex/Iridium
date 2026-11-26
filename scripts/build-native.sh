#!/usr/bin/env bash
# Builds the native hot-path library and stages it for deployment.
# Output: native/iridium-core/target/release/<artifact> -> out/native/
#
#   --cross      additionally cross-compile iridium_core.dll for Windows
#                (CI uses this on Linux; needs the rustup target
#                x86_64-pc-windows-gnu + a mingw-w64 linker)
#   --universal  macOS only: also build the other CPU arch and merge both
#                slices into one fat dylib, so a single file works whether
#                the game is arm64 or Intel x86_64 (Rosetta). Reuses the
#                host build above as one of the two slices.
#   --arm        additionally cross-compile the Linux ARM64 slice (arm-v8a)
#                -> libiridium_core_arm64.so. Needs the rustup target
#                aarch64-unknown-linux-gnu plus gcc-aarch64-linux-gnu
#                (CI installs it).
#   --bionic     additionally cross-compile the bionic ARM64 slice (arm64-v8a)
#                -> libiridium_core_bionic_arm64.so. Needs the rustup target
#                aarch64-linux-android and an NDK, found through
#                ANDROID_NDK_HOME or ANDROID_NDK_LATEST_HOME (CI uses the NDK
#                preinstalled on the runner).
#
# Any other arguments are passed through to `cargo build`.
set -euo pipefail
repo="$(cd "$(dirname "$0")/.." && pwd)"

cross=false
universal=false
arm=false
bionic=false
args=()
for a in "$@"; do
    case "$a" in
        --cross)     cross=true ;;
        --universal) universal=true ;;
        --arm)       arm=true ;;
        --bionic)    bionic=true ;;
        *)           args+=("$a") ;;
    esac
done

cd "$repo/native/iridium-core"
cargo build --release ${args[@]+"${args[@]}"}

mkdir -p "$repo/out/native"

case "$(uname -s)" in
    MINGW*|MSYS*|CYGWIN*) artifact="target/release/iridium_core.dll" ;;
    Darwin)               artifact="target/release/libiridium_core.dylib" ;;
    *)                    artifact="target/release/libiridium_core.so" ;;
esac

cp "$artifact" "$repo/out/native/"
echo "staged: $repo/out/native/$(basename "$artifact")"

# --cross: stage the Windows dll too. On a Windows host the artifact above
# already IS the dll, so only cross-compile elsewhere.
if [ "$cross" = true ]; then
    case "$(uname -s)" in
        MINGW*|MSYS*|CYGWIN*) ;;
        *)
            cargo build --release --target x86_64-pc-windows-gnu ${args[@]+"${args[@]}"}
            cp "target/x86_64-pc-windows-gnu/release/iridium_core.dll" "$repo/out/native/"
            echo "staged: $repo/out/native/iridium_core.dll"
            ;;
    esac
fi

# --universal: replace the staged dylib with a fat binary covering both CPU
# slices (host build + the other arch). lipo ships with the Xcode CLI tools.
if [ "$universal" = true ]; then
    case "$(uname -s)" in
        Darwin)
            case "$(uname -m)" in
                arm64) other="x86_64-apple-darwin" ;;
                *)     other="aarch64-apple-darwin" ;;
            esac
            if command -v rustup >/dev/null 2>&1; then
                rustup target add "$other"
            fi
            cargo build --release --target "$other" ${args[@]+"${args[@]}"}
            lipo -create "target/release/libiridium_core.dylib" \
                         "target/$other/release/libiridium_core.dylib" \
                     -output "$repo/out/native/libiridium_core.dylib"
            echo "staged: $repo/out/native/libiridium_core.dylib (universal: $(uname -m) + $other)"
            ;;
        *)
            echo "build-native.sh: --universal ignored (not macOS)" >&2
            ;;
    esac
fi

# --arm: cross-compile the Linux ARM64 (arm-v8a) slice only. The name matches
# the runtime probe (libiridium_core_arm64.so). Needs the rustup target
# aarch64-unknown-linux-gnu plus gcc-aarch64-linux-gnu (CI installs it).
if [ "$arm" = true ]; then
    target="aarch64-unknown-linux-gnu"
    env_key="CARGO_TARGET_$(echo "$target" | tr 'a-z-' 'A-Z_')_LINKER"

    if command -v rustup >/dev/null 2>&1; then
        rustup target add "$target" >/dev/null 2>&1 || true
    fi
    env "$env_key=aarch64-linux-gnu-gcc" cargo build --release --target "$target" ${args[@]+"${args[@]}"}
    cp "target/$target/release/libiridium_core.so" "$repo/out/native/libiridium_core_arm64.so"
    echo "staged: $repo/out/native/libiridium_core_arm64.so ($target)"
fi

# --bionic: cross-compile the bionic ARM64 (arm64-v8a) slice with the NDK's
# clang. Bionic, not glibc, so it ships under its own name and the runtime
# picks it only where the libc is bionic. API 26 is the game's minimum API
# level; 16 KB page alignment keeps the library loadable on 16 KB-page devices.
if [ "$bionic" = true ]; then
    target="aarch64-linux-android"
    ndk="${ANDROID_NDK_HOME:-${ANDROID_NDK_LATEST_HOME:-}}"
    if [ -z "$ndk" ]; then
        echo "build-native.sh: --bionic needs ANDROID_NDK_HOME (or ANDROID_NDK_LATEST_HOME)" >&2
        exit 1
    fi
    case "$(uname -s)" in
        Darwin)               ndk_host="darwin-x86_64"; ndk_ext="" ;;
        MINGW*|MSYS*|CYGWIN*) ndk_host="windows-x86_64"; ndk_ext=".cmd" ;;
        *)                    ndk_host="linux-x86_64"; ndk_ext="" ;;
    esac
    linker="$ndk/toolchains/llvm/prebuilt/$ndk_host/bin/aarch64-linux-android26-clang$ndk_ext"
    triple_key="$(echo "$target" | tr 'a-z-' 'A-Z_')"

    if command -v rustup >/dev/null 2>&1; then
        rustup target add "$target" >/dev/null 2>&1 || true
    fi
    env "CARGO_TARGET_${triple_key}_LINKER=$linker" \
        "CARGO_TARGET_${triple_key}_RUSTFLAGS=-C link-arg=-Wl,-z,max-page-size=16384" \
        cargo build --release --target "$target" ${args[@]+"${args[@]}"}
    cp "target/$target/release/libiridium_core.so" "$repo/out/native/libiridium_core_bionic_arm64.so"
    echo "staged: $repo/out/native/libiridium_core_bionic_arm64.so ($target)"
fi
