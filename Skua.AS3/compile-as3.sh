#!/usr/bin/env bash
# Builds skua.swf on macOS with Apache Flex SDK 4.16.1 and Adobe playerglobal 32.0.
# The Windows build is compile-as3.ps1; this script uses the same mxmlc flags.
#
# Usage: ./compile-as3.sh [-o <output.swf>]
#   Default output: skua/bin/skua.swf (the path the Windows projects link).
#   Downloads are cached in $SKUA_AS3_CACHE (default ~/Library/Caches/skua-as3) and checked against pinned SHA-256s.
#   Requires Java (17 is known to work).

set -euo pipefail

FLEX_VERSION="4.16.1"
FLEX_URL="https://archive.apache.org/dist/flex/${FLEX_VERSION}/binaries/apache-flex-sdk-${FLEX_VERSION}-bin.tar.gz"
# Matches Apache's published md5 (0fba6c912c3919ae1b978ca2d053fe07) and its PGP signature.
FLEX_SHA256="17fda7ac8d3e476cad3127f345455ef316acfb87c6f4322e5897bd8d9b09388e"

PLAYER_VERSION="32.0"
PLAYERGLOBAL_URL="https://fpdownload.macromedia.com/get/flashplayer/updaters/32/playerglobal32_0.swc"
PLAYERGLOBAL_SHA256="7d4d6168d27603cfb3b750302448e354e0bbc1bdd58f5d101c3dcf6891e9bb65"

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cache_dir="${SKUA_AS3_CACHE:-$HOME/Library/Caches/skua-as3}"
output="$script_dir/skua/bin/skua.swf"

while [ $# -gt 0 ]; do
    case "$1" in
        -o) output="${2:?-o needs a path}"; shift 2 ;;
        -h|--help) sed -n '2,8p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
        *) echo "Unknown argument: $1" >&2; exit 2 ;;
    esac
done
case "$output" in /*) ;; *) output="$PWD/$output" ;; esac

if ! java -version >/dev/null 2>&1; then
    echo "Java was not found. Install a JDK (17 is known to work) and try again." >&2
    exit 1
fi

sha256() { shasum -a 256 "$1" | cut -d ' ' -f 1; }

# doabc_sha256 <swf>: hashes the SWF's DoABC tags, which identify a build. The file hash changes on every
# build because mxmlc writes a compile timestamp into the ProductInfo tag.
doabc_sha256() {
    perl -MIO::Uncompress::Inflate=inflate -MDigest::SHA=sha256_hex -e '
        local $/; my $swf = <STDIN>;
        my ($sig, $body) = (substr($swf, 0, 3), substr($swf, 8));
        if ($sig eq "CWS") { inflate(\$body => \my $out) or die "cannot inflate SWF\n"; $body = $out }
        elsif ($sig ne "FWS") { die "not an uncompressed or zlib SWF: $sig\n" }
        my $pos = int((5 + 4 * (ord($body) >> 3) + 7) / 8) + 4;  # frame RECT, frame rate, frame count
        my $abc = "";
        while ($pos < length $body) {
            my $header = unpack("v", substr($body, $pos, 2)); $pos += 2;
            my ($code, $len) = ($header >> 6, $header & 0x3f);
            if ($len == 0x3f) { $len = unpack("V", substr($body, $pos, 4)); $pos += 4 }
            $abc .= substr($body, $pos, $len) if $code == 72 || $code == 82;  # DoABC, DoABC2
            $pos += $len;
        }
        die "no DoABC tag\n" if $abc eq "";
        print sha256_hex($abc), "\n";
    ' < "$1"
}

# fetch <url> <dest> <sha256>: downloads once, then reuses the cached file while its checksum matches.
fetch() {
    local url="$1" dest="$2" expected="$3"
    if [ -f "$dest" ] && [ "$(sha256 "$dest")" = "$expected" ]; then
        return
    fi
    echo "Downloading $url"
    curl -fL --retry 3 --progress-bar -o "$dest.part" "$url"
    local actual
    actual="$(sha256 "$dest.part")"
    if [ "$actual" != "$expected" ]; then
        rm -f "$dest.part"
        echo "Checksum mismatch for $url" >&2
        echo "  expected $expected" >&2
        echo "  actual   $actual" >&2
        exit 1
    fi
    mv "$dest.part" "$dest"
}

mkdir -p "$cache_dir"
flex_archive="$cache_dir/apache-flex-sdk-${FLEX_VERSION}-bin.tar.gz"
playerglobal="$cache_dir/playerglobal32_0.swc"
fetch "$FLEX_URL" "$flex_archive" "$FLEX_SHA256"
fetch "$PLAYERGLOBAL_URL" "$playerglobal" "$PLAYERGLOBAL_SHA256"

# The extracted SDK is keyed by the archive checksum, so a changed pin never reuses a stale tree.
flex_home="$cache_dir/flex-${FLEX_SHA256:0:12}"
if [ ! -x "$flex_home/bin/mxmlc" ]; then
    echo "Extracting Apache Flex SDK $FLEX_VERSION"
    rm -rf "$flex_home" "$flex_home.part"
    mkdir -p "$flex_home.part"
    tar -xzf "$flex_archive" -C "$flex_home.part" --strip-components 1
    mv "$flex_home.part" "$flex_home"
fi

player_dir="$flex_home/frameworks/libs/player"
mkdir -p "$player_dir/$PLAYER_VERSION"
cp "$playerglobal" "$player_dir/$PLAYER_VERSION/playerglobal.swc"

mkdir -p "$(dirname "$output")"
rm -f "$output"

cd "$script_dir"
PLAYERGLOBAL_HOME="$player_dir" "$flex_home/bin/mxmlc" \
    -source-path skua/src -default-size 958 550 -output "$output" skua/src/skua/Main.as \
    -target-player "$PLAYER_VERSION" -optimize

if [ ! -f "$output" ]; then
    echo "mxmlc did not produce $output" >&2
    exit 1
fi
echo "Built $output ($(wc -c < "$output" | tr -d ' ') bytes, DoABC sha256 $(doabc_sha256 "$output"))"
