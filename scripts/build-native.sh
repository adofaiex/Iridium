#!/usr/bin/env bash
# Builds the native hot-path library and stages it for deployment.
# Output: native/iridium-core/target/release/<artifact> -> out/native/
#
#   --cross  additionally cross-compile iridium_core.dll for Windows
#            (CI uses this on Linux; needs the rustup target
#            x86_64-pc-windows-gnu + a mingw-w64 linker)
#
# Any other arguments are passed through to `cargo build`.
set -euo pipefail
repo="$(cd "$(dirname "$0")/.." && pwd)"

cross=false
args=()
for a in "$@"; do
    if [ "$a" = "--cross" ]; then cross=true; else args+=("$a"); fi
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
