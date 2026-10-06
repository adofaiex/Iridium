#!/usr/bin/env bash
# Builds the native hot-path library and stages it for deployment.
# Output: native/iridium-core/target/release/<artifact> -> out/native/
set -euo pipefail
repo="$(cd "$(dirname "$0")/.." && pwd)"

cd "$repo/native/iridium-core"
cargo build --release "$@"

mkdir -p "$repo/out/native"

case "$(uname -s)" in
    MINGW*|MSYS*|CYGWIN*) artifact="target/release/iridium_core.dll" ;;
    Darwin)               artifact="target/release/libiridium_core.dylib" ;;
    *)                    artifact="target/release/libiridium_core.so" ;;
esac

cp "$artifact" "$repo/out/native/"
echo "staged: $repo/out/native/$(basename "$artifact")"
