#!/bin/bash
# Installs Skua's macOS Engine and CLI from this checkout: builds skua, skua-engine, skua-gamehost and skua.swf, publishes them side
# by side as a self-contained build, and links skua into a directory on PATH. Re-run it to update; the next skua command replaces a
# running Engine of another build once no Script runs in it.
#
# Usage: ./install-macos.sh [dotnet publish arguments, e.g. -p:SkuaGameHostPath=<file>]
#
#   SKUA_INSTALL_DIR  where builds go, each in versions/<build> (default ~/.local/share/skua)
#   SKUA_BIN_DIR      where the skua link goes (default ~/.local/bin)
set -euo pipefail

repo="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
install_dir="${SKUA_INSTALL_DIR:-$HOME/.local/share/skua}"
bin_dir="${SKUA_BIN_DIR:-$HOME/.local/bin}"
versions="$install_dir/versions"
link="$bin_dir/skua"
# Builds kept besides the one installed; any build a process still runs from is kept too.
keep=2

fail() {
  echo "install-macos.sh: $*" >&2
  exit 1
}

[[ "$(uname -s)" == Darwin ]] || fail "this installs Skua on macOS; for Windows, see BUILD.md."
command -v dotnet >/dev/null || fail "the .NET 10 SDK was not found. Install it: brew install dotnet"
case "$(uname -m)" in
  arm64) rid=osx-arm64 ;;
  x86_64) rid=osx-x64 ;;
  *) fail "unsupported CPU '$(uname -m)'." ;;
esac
[[ ! -e "$link" || -L "$link" ]] || fail "$link exists and isn't a link; move it away first."

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

if [[ -d "$target" ]]; then
  echo "Skua $build is already built."
  touch "$target"
else
  mkdir -p "$versions"
  # Published into a staging folder and moved into place, so a build folder is always complete and never changes under a running Engine.
  staging="$(mktemp -d "$versions/.staging.XXXXXX")"
  trap 'rm -rf "$staging"' EXIT
  echo "Building Skua $build..."
  # Self-contained, so skua and skua-engine start without DOTNET_ROOT; Scripts compile against the runtime published with them.
  dotnet publish "$repo/Skua.App.Engine/Skua.App.Engine.csproj" --configuration Release --runtime "$rid" --self-contained \
    --output "$staging" "-p:InformationalVersion=$build" -p:IncludeSourceRevisionInInformationalVersion=false "$@"
  [[ ! -e "$target" ]] || fail "$target appeared during the build; is another install running?"
  mv "$staging" "$target"
  trap - EXIT
fi

mkdir -p "$bin_dir"
ln -sfn "$target/skua" "$link"

# Older builds go, newest first, except those an Engine or an MCP server still runs from.
# shellcheck disable=SC2012 # this script names the build folders, and ls sorts them by time
ls -1t "$versions" | { grep -vxF -- "$build" || true; } | tail -n +$((keep + 1)) | while IFS= read -r old; do
  # The publish is flat, so a process running from a build has files open directly in its folder. lsof's exit code also counts
  # processes it may not inspect, so only its output says whether one does.
  if [[ -z "$(/usr/sbin/lsof -t +d "$versions/$old" 2>/dev/null)" ]]; then
    rm -rf "${versions:?}/$old"
  fi
done

echo "Installed skua $build"
echo "  in $target"
echo "  linked from $link"
case ":$PATH:" in
  *":$bin_dir:"*)
    if [[ "$(command -v skua)" != "$link" ]]; then
      echo "Another skua comes first on PATH: $(command -v skua). Remove it, or put $bin_dir before it."
    fi
    ;;
  *)
    echo "$bin_dir isn't on PATH yet. Add it: fish_add_path $bin_dir (fish), or add export PATH=\"$bin_dir:\$PATH\" to ~/.zshrc (zsh)."
    ;;
esac
