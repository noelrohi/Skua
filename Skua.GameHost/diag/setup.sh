#!/usr/bin/env bash
# Points the Game Host at a local Ruffle checkout, for Ruffle work and for the `diag` build (README.md).
# Writes the gitignored .cargo/config.toml with [patch] path overrides for the four Ruffle crates and,
# with --diag, for gc-arena and wgpu-hal patched with this folder's census and peak-counter patches.
#
# Usage: diag/setup.sh <ruffle checkout> [--diag]
#        diag/setup.sh --off      (removes the override; then `git checkout Cargo.lock`)

set -euo pipefail

crate_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
config="$crate_dir/.cargo/config.toml"

if [ "${1:-}" = "--off" ]; then
    rm -f "$config"
    echo "Removed $config. Restore the lockfile with: git -C '$crate_dir' checkout Cargo.lock"
    exit 0
fi

ruffle="$(cd "${1:?usage: diag/setup.sh <ruffle checkout> [--diag]}" && pwd)"
[ -f "$ruffle/core/Cargo.toml" ] || { echo "$ruffle is not a Ruffle checkout" >&2; exit 1; }

# The stock crates' sources, before the override changes what Cargo resolves.
if [ "${2:-}" = "--diag" ]; then
    rm -f "$config"
    (cd "$crate_dir" && cargo fetch --locked >/dev/null)
fi

mkdir -p "$crate_dir/.cargo"
{
    echo "# Written by diag/setup.sh; gitignored. Remove with diag/setup.sh --off."
    echo '[patch."https://github.com/noelrohi/ruffle"]'
    for c in core render render/wgpu frontend-utils; do
        name="ruffle_$(basename "$c" | tr - _)"
        [ "$c" = "render/wgpu" ] && name=ruffle_render_wgpu
        echo "$name = { path = \"$ruffle/$c\" }"
    done
} >"$config"

if [ "${2:-}" = "--diag" ]; then
    build="$crate_dir/target/diag-crates"
    mkdir -p "$build"
    for spec in gc-arena-0.7.0:gc-arena-0.7.0-census.patch wgpu-hal-30.0.1:wgpu-hal-30.0.1-peak-counter.patch; do
        src="${spec%%:*}" patch_file="$crate_dir/diag/${spec#*:}"
        # The index directory holding this crate (there can be several).
        stock="$(ls -d "${CARGO_HOME:-$HOME/.cargo}"/registry/src/*/"$src" | head -1)"
        rm -rf "$build/$src"
        cp -R "$stock" "$build/$src"
        chmod -R u+w "$build/$src"
        patch -s -p1 -d "$build/$src" <"$patch_file"
    done
    {
        echo '[patch.crates-io]'
        echo "gc-arena = { path = \"$build/gc-arena-0.7.0\" }"
        echo "wgpu-hal = { path = \"$build/wgpu-hal-30.0.1\" }"
    } >>"$config"
fi

echo "Wrote $config. Build without --locked (the override rewrites Cargo.lock; don't commit that), e.g.:"
if [ "${2:-}" = "--diag" ]; then
    echo "  cargo build --release --features diag   # needs $ruffle on branch skua-diag"
else
    echo "  cargo build --release"
fi
