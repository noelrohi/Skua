#!/usr/bin/env bash
# Offline stress suite for skua-gamehost (README.md, "Changing Ruffle", step 2).
# Builds the release host and the stress SWFs, then runs every case against stress/baseline.txt.
# About 5 minutes, on a Mac with a real GPU (not CI: footprint and render times mean nothing on a
# paravirtual GPU). The smoke case loads the live game's login screen, so it needs the network.
#
# Usage: stress/check.sh [--only case,case] [--record]
#   Cases: stress2 stress3 stress4 sounds weakdict events smoke lifecycle
#   --record prints the measured values in baseline.txt's format instead of judging them.
# Env:
#   SKUA_FLEX_HOME   Apache Flex SDK 4.16.1 with playerglobal 32.0 (default: the SDK that
#                    Skua.AS3/compile-as3.sh caches in ~/Library/Caches/skua-as3)
#   SKUA_SWF         a prebuilt skua.swf (default: built from Skua.AS3 with the same SDK)
#   SKUA_GAMEHOST    a prebuilt host binary (default: cargo build --release --locked)

set -euo pipefail

stress_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
crate_dir="$(dirname "$stress_dir")"
repo_dir="$(dirname "$crate_dir")"
out_dir="$stress_dir/out"
mkdir -p "$out_dir"

flex_home="${SKUA_FLEX_HOME:-}"
if [ -z "$flex_home" ]; then
    for d in "${SKUA_AS3_CACHE:-$HOME/Library/Caches/skua-as3}"/flex-*; do
        [ -x "$d/bin/mxmlc" ] && flex_home="$d"
    done
fi
if [ -z "$flex_home" ] || [ ! -x "$flex_home/bin/mxmlc" ]; then
    echo "No Flex SDK found. Run Skua.AS3/compile-as3.sh once (it caches one), or set SKUA_FLEX_HOME." >&2
    exit 1
fi
if [ ! -f "$flex_home/frameworks/libs/player/32.0/playerglobal.swc" ]; then
    echo "$flex_home has no playerglobal 32.0 (frameworks/libs/player/32.0/playerglobal.swc)." >&2
    exit 1
fi

mxmlc() {
    PLAYERGLOBAL_HOME="$flex_home/frameworks/libs/player" "$flex_home/bin/mxmlc" \
        -default-size 958 550 -target-player 32.0 "$@" >"$out_dir/mxmlc.log" 2>&1 \
        || { cat "$out_dir/mxmlc.log" >&2; exit 1; }
}

echo "Building the stress SWFs"
for case in Stress2 Stress3 Stress4 Sounds WeakDict Events; do
    if [ ! "$out_dir/$case.swf" -nt "$stress_dir/$case.as" ]; then
        mxmlc -source-path "$stress_dir" -omit-trace-statements=false -output "$out_dir/$case.swf" "$stress_dir/$case.as"
    fi
done

skua_swf="${SKUA_SWF:-}"
if [ -z "$skua_swf" ]; then
    # The same flags as Skua.AS3/compile-as3.sh.
    echo "Building skua.swf"
    skua_swf="$out_dir/skua.swf"
    (cd "$repo_dir/Skua.AS3" && mxmlc -source-path skua/src -output "$skua_swf" skua/src/skua/Main.as -optimize)
fi

host="${SKUA_GAMEHOST:-}"
if [ -z "$host" ]; then
    echo "Building skua-gamehost"
    cargo build --release --locked --manifest-path "$crate_dir/Cargo.toml"
    host="$crate_dir/target/release/skua-gamehost"
fi

exec python3 "$stress_dir/check.py" --host "$host" --swfs "$out_dir" --skua-swf "$skua_swf" --out "$out_dir" "$@"
