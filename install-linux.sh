#!/bin/bash
# Installs Skua's Linux Engine, CLI and TUI from this checkout: builds skua, skua-engine, skua-gamehost, skua.swf and skua-tui,
# publishes them side by side as a self-contained build, and links skua and skua-tui into a directory on PATH. Re-run it to update; the
# next skua command replaces a running Engine of another build once no Script runs in it. install-macos.sh's Linux twin, without --app:
# Linux has no Mac App.
#
# The Game Host renders with Vulkan or GL; on a machine without a GPU, Mesa's lavapipe (mesa-vulkan-drivers) does it on the CPU.
#
# Usage: ./install-linux.sh [dotnet publish arguments, e.g. -p:SkuaGameHostPath=<file>]
#
#   SKUA_INSTALL_DIR  where builds go, each in versions/<build> (default ~/.local/share/skua)
#   SKUA_BIN_DIR      where the skua and skua-tui links go (default ~/.local/bin)
#   SKUA_NO_TUI=1     installs everything but skua-tui, and removes a skua-tui link into its builds
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

# Builds skua-tui in release, into $tui.
build_tui() {
  echo "Building skua-tui..."
  # From its folder, so rustup picks the crate's rust-toolchain.toml.
  cd "$repo/Skua.Tui"
  cargo build --release --locked || fail "couldn't build skua-tui."
  tui="$(cargo metadata --format-version 1 --no-deps | sed -n 's/.*"target_directory":"\([^"]*\)".*/\1/p')/release/skua-tui"
  cd - >/dev/null
}

# Whether a process of this user runs from the folder $1: one has a file there mapped (its executable or a library), which /proc shows
# without lsof.
running_from() {
  grep -qsF "$1/" /proc/[0-9]*/maps
}

if [[ "${SKUA_NO_TUI:-}" == 1 ]]; then with_tui=false; else with_tui=true; fi

[[ "$(uname -s)" == Linux ]] || fail "this installs Skua on Linux; on macOS, run ./install-macos.sh; for Windows, see BUILD.md."
command -v dotnet >/dev/null || fail "the .NET 10 SDK was not found. Install it: https://learn.microsoft.com/dotnet/core/install/linux"
command -v cargo >/dev/null || fail "cargo was not found; it builds the Game Host. Install Rust: https://rustup.rs"
command -v java >/dev/null || fail "Java was not found; it builds skua.swf. Install a JDK (17 is known to work): sudo apt install openjdk-17-jdk-headless"
case "$(uname -m)" in
  x86_64) rid=linux-x64 ;;
  aarch64 | arm64) rid=linux-arm64 ;;
  *) fail "unsupported CPU '$(uname -m)'." ;;
esac
if ! compgen -G "/usr/share/vulkan/icd.d/*.json" >/dev/null && ! compgen -G "/etc/vulkan/icd.d/*.json" >/dev/null; then
  echo "No Vulkan driver was found; the Game Host falls back to GL. Without a GPU, install Mesa's: sudo apt install mesa-vulkan-drivers" >&2
fi
[[ ! -e "$link" || -L "$link" ]] || fail "$link exists and isn't a link; move it away first."
if $with_tui; then
  [[ ! -e "$tui_link" || -L "$tui_link" ]] || fail "$tui_link exists and isn't a link; move it away first."
fi

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
  if $with_tui; then build_tui; fi
  mkdir -p "$versions"
  # Published into a staging folder and moved into place, so a build folder is always complete and never changes under a running Engine.
  staging="$(mktemp -d "$versions/.staging.XXXXXX")"
  trap 'rm -rf "$staging"' EXIT
  echo "Building Skua $build..."
  # Self-contained, so skua and skua-engine start without DOTNET_ROOT; Scripts compile against the runtime published with them.
  dotnet publish "$repo/Skua.App.Engine/Skua.App.Engine.csproj" --configuration Release --runtime "$rid" --self-contained \
    --output "$staging" "-p:InformationalVersion=$build" -p:IncludeSourceRevisionInInformationalVersion=false "$@"
  if $with_tui; then cp "$tui" "$staging/"; fi
  [[ ! -e "$target" ]] || fail "$target appeared during the build; is another install running?"
  mv "$staging" "$target"
  trap - EXIT
fi

if $with_tui && [[ ! -e "$target/skua-tui" ]]; then
  # The build was made with SKUA_NO_TUI=1. skua-tui is added beside its files, whole, and changes none of them.
  build_tui
  cp "$tui" "$target/.skua-tui.$$"
  mv "$target/.skua-tui.$$" "$target/skua-tui"
fi
mkdir -p "$bin_dir"
previous="$(readlink "$link" 2>/dev/null || true)"
ln -sfn "$target/skua" "$link"
if $with_tui; then
  ln -sfn "$target/skua-tui" "$tui_link"
elif [[ "$(readlink "$tui_link" 2>/dev/null)" == "$versions/"* ]]; then
  # It would keep an older build's skua-tui, and dangle once that build is removed.
  rm "$tui_link"
fi
if [[ -n "$previous" && "$previous" != "$versions/"* ]]; then
  echo "$link linked to $previous; it now links to this build."
fi

# Older builds go, newest first, except those an Engine or an MCP server still runs from.
# shellcheck disable=SC2012 # this script names the build folders, and ls sorts them by time
ls -1t "$versions" | { grep -vxF -- "$build" || true; } | tail -n +$((keep + 1)) | while IFS= read -r old; do
  if ! running_from "$versions/$old"; then
    rm -rf "${versions:?}/$old"
  fi
done

echo "Installed skua $build"
echo "  in $target"
if $with_tui; then echo "  linked from $link, and skua-tui from $tui_link"; else echo "  linked from $link, without skua-tui"; fi
case ":$PATH:" in
  *":$bin_dir:"*)
    if [[ "$(command -v skua)" != "$link" ]]; then
      echo "Another skua comes first on PATH: $(command -v skua). Remove it, or put $bin_dir before it."
    fi
    ;;
  *)
    echo "$bin_dir isn't on PATH yet. Add it: add export PATH=\"$bin_dir:\$PATH\" to ~/.bashrc (bash) or ~/.zshrc (zsh)."
    ;;
esac
