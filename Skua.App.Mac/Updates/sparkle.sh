#!/usr/bin/env bash
# Fetches Sparkle, the Mac App's updater, at its pinned version and prints the folder it is in: Sparkle.framework, which Skua.app embeds,
# and bin/ with sign_update, which signs a release's zip.
#
# Usage: ./sparkle.sh (prints only the folder, so MSBuild can read it)
#   The download is cached in $SKUA_SPARKLE_CACHE (default ~/Library/Caches/skua-sparkle) and checked against its pinned SHA-256.
set -euo pipefail

SPARKLE_VERSION="2.10.0"
SPARKLE_URL="https://github.com/sparkle-project/Sparkle/releases/download/${SPARKLE_VERSION}/Sparkle-${SPARKLE_VERSION}.tar.xz"
# The digest GitHub lists for the release asset.
SPARKLE_SHA256="c2bf58aa8387266ac179357b1415d6f2635f044da8be41042af32425dae6da0c"

cache_dir="${SKUA_SPARKLE_CACHE:-$HOME/Library/Caches/skua-sparkle}"
archive="$cache_dir/Sparkle-${SPARKLE_VERSION}.tar.xz"
# Keyed by the archive checksum, so a changed pin never reuses a stale tree.
sparkle="$cache_dir/sparkle-${SPARKLE_SHA256:0:12}"

if [ ! -d "$sparkle/Sparkle.framework" ]; then
    mkdir -p "$cache_dir"
    if [ ! -f "$archive" ] || [ "$(shasum -a 256 "$archive" | cut -d ' ' -f 1)" != "$SPARKLE_SHA256" ]; then
        curl -fsSL --retry 3 -o "$archive.part" "$SPARKLE_URL"
        actual="$(shasum -a 256 "$archive.part" | cut -d ' ' -f 1)"
        if [ "$actual" != "$SPARKLE_SHA256" ]; then
            rm -f "$archive.part"
            echo "Checksum mismatch for $SPARKLE_URL" >&2
            echo "  expected $SPARKLE_SHA256" >&2
            echo "  actual   $actual" >&2
            exit 1
        fi
        mv "$archive.part" "$archive"
    fi
    rm -rf "$sparkle.part.$$"
    mkdir -p "$sparkle.part.$$"
    tar -xf "$archive" -C "$sparkle.part.$$"
    # Another build may have extracted it meanwhile; either tree is the same.
    if [ -d "$sparkle" ]; then rm -rf "$sparkle.part.$$"; else mv "$sparkle.part.$$" "$sparkle"; fi
fi
echo "$sparkle"
