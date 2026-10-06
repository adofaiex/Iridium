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
#
# Any other arguments are passed through to `cargo build`.
set -euo pipefail
repo="$(cd "$(dirname "$0")/.." && pwd)"

cross=false
universal=false
args=()
for a in "$@"; do
    case "$a" in
        --cross)     cross=true ;;
        --universal) universal=true ;;
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
