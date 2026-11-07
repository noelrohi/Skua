#!/bin/bash
# Installs Skua's Linux Engine, CLI and TUI from this checkout: builds skua, skua-engine, skua-gamehost, skua.swf and skua-tui,
# publishes them side by side as a self-contained build, and links skua and skua-tui into a directory on PATH. Re-run it to update; the
# next skua command replaces a running Engine of another build once no Script runs in it. install-macos.sh's Linux twin, without --app
# and SKUA_NO_TUI: the Game Host needs cargo anyway.
#
# Usage: ./install-linux.sh [dotnet publish arguments, e.g. -p:SkuaGameHostPath=<file>]
#
#   SKUA_INSTALL_DIR  where builds go, each in versions/<build> (default ~/.local/share/skua)
#   SKUA_BIN_DIR      where the skua and skua-tui links go (default ~/.local/bin)
set -euo pipefail

repo="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
install_dir="${SKUA_INSTALL_DIR:-$HOME/.local/share/skua}"
bin_dir="${SKUA_BIN_DIR:-$HOME/.local/bin}"
versions="$install_dir/versions"
link="$bin_dir/skua"
tui_link="$bin_dir/skua-tui"
# Builds kept besides the one installed; any build a process still runs from is kept too.
keep=2

fail() {
  echo "install-linux.sh: $*" >&2
  exit 1
}

[[ "$(uname -s)" == Linux ]] || fail "this installs Skua on Linux; on macOS, run ./install-macos.sh; for Windows, see BUILD.md."
command -v dotnet >/dev/null || fail "the .NET 10 SDK was not found. Install it: https://learn.microsoft.com/dotnet/core/install/linux"
command -v cargo >/dev/null || fail "cargo was not found. Install Rust: https://rustup.rs"
command -v java >/dev/null || fail "Java was not found. Install a JDK (17 is known to work): sudo apt install openjdk-17-jdk-headless"
case "$(uname -m)" in
  x86_64) rid=linux-x64 ;;
  aarch64) rid=linux-arm64 ;;
  *) fail "unsupported CPU '$(uname -m)'." ;;
esac
for l in "$link" "$tui_link"; do
  [[ ! -e "$l" || -L "$l" ]] || fail "$l exists and isn't a link; move it away first."
done

# The build is the version and the commit, as in a dev build's informational version, plus the time for uncommitted changes:
# installs of different code never share a build, so the CLI can tell their Engines apart.
version="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$repo/Directory.Build.props")"
[[ -n "$version" ]] || fail "no <Version> in $repo/Directory.Build.props."
commit="$(git -C "$repo" rev-parse HEAD)" || fail "$repo isn't a git checkout."
build="$version+$commit"
if [[ -n "$(git -C "$repo" status --porcelain)" ]]; then
  build="$build.dirty.$(date -u +%Y%m%dT%H%M%SZ)"
fi
target="$versions/$build"

if [[ -e "$target" ]]; then
  echo "Skua $build is already built."
  touch "$target"
else
  echo "Building skua-tui..."
  # From its folder, so rustup picks the crate's rust-toolchain.toml.
  (cd "$repo/Skua.Tui" && cargo build --release --locked) || fail "couldn't build skua-tui."
  tui="$(cd "$repo/Skua.Tui" && cargo metadata --format-version 1 --no-deps | sed -n 's/.*"target_directory":"\([^"]*\)".*/\1/p')/release/skua-tui"
  mkdir -p "$versions"
  # Published into a staging folder and moved into place, so a build folder is always complete and never changes under a running Engine.
  staging="$(mktemp -d "$versions/.staging.XXXXXX")"
  trap 'rm -rf "$staging"' EXIT
  echo "Building Skua $build..."
  # Self-contained, so skua and skua-engine start without DOTNET_ROOT; Scripts compile against the runtime published with them.
  dotnet publish "$repo/Skua.App.Engine/Skua.App.Engine.csproj" --configuration Release --runtime "$rid" --self-contained \
    --output "$staging" "-p:InformationalVersion=$build" -p:IncludeSourceRevisionInInformationalVersion=false "$@"
  cp "$tui" "$staging/"
  [[ ! -e "$target" ]] || fail "$target appeared during the build; is another install running?"
  mv "$staging" "$target"
  trap - EXIT
fi

mkdir -p "$bin_dir"
ln -sfn "$target/skua" "$link"
ln -sfn "$target/skua-tui" "$tui_link"

# Older builds go, newest first, except those an Engine or an MCP server still runs from: a process of this user has a file there mapped,
# which /proc shows without lsof.
# shellcheck disable=SC2012 # this script names the build folders, and ls sorts them by time
ls -1t "$versions" | { grep -vxF -- "$build" || true; } | tail -n +$((keep + 1)) | while IFS= read -r old; do
  if ! grep -qsF "$versions/$old/" /proc/[0-9]*/maps; then
    rm -rf "${versions:?}/$old"
  fi
done

echo "Installed skua $build"
echo "  in $target, linked from $link, and skua-tui from $tui_link"
case ":$PATH:" in
  *":$bin_dir:"*)
    if [[ "$(command -v skua)" != "$link" ]]; then
      echo "Another skua comes first on PATH: $(command -v skua). Remove it, or put $bin_dir before it."
    fi
    ;;
  *)
    echo "$bin_dir isn't on PATH yet. Add export PATH=\"$bin_dir:\$PATH\" to ~/.bashrc (bash) or ~/.zshrc (zsh)."
    ;;
esac
