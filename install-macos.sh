#!/bin/bash
# Installs Skua's macOS Engine, CLI and TUI from this checkout: builds skua, skua-engine, skua-gamehost, skua.swf and skua-tui,
# publishes them side by side as a self-contained build, and links skua and skua-tui into a directory on PATH. Re-run it to update; the
# next skua command replaces a running Engine of another build once no Script runs in it.
#
# With --app, the build is a Skua.app instead, with that same skua and skua-tui inside it, which the links point to; a small Skua.app in
# the apps folder opens it. Once that is installed, a plain re-run updates the app too, so the app and skua always share a build. A
# running app keeps its own build, whose folder is kept, until it's reopened.
#
# These dev builds never update themselves; a release of Skua.app does (BUILD.md, "Install a release"), and this script leaves one alone.
#
# Usage: ./install-macos.sh [--app] [dotnet publish arguments, e.g. -p:SkuaGameHostPath=<file>]
#
#   SKUA_INSTALL_DIR  where builds go, each in versions/<build> (default ~/.local/share/skua)
#   SKUA_BIN_DIR      where the skua and skua-tui links go (default ~/.local/bin)
#   SKUA_APPS_DIR     where Skua.app goes (default ~/Applications)
#   SKUA_NO_TUI=1     installs everything but skua-tui, so it needs no cargo, and removes a skua-tui link into its builds
set -euo pipefail

repo="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
install_dir="${SKUA_INSTALL_DIR:-$HOME/.local/share/skua}"
bin_dir="${SKUA_BIN_DIR:-$HOME/.local/bin}"
apps_dir="${SKUA_APPS_DIR:-$HOME/Applications}"
versions="$install_dir/versions"
link="$bin_dir/skua"
tui_link="$bin_dir/skua-tui"
launcher="$apps_dir/Skua.app"
# Starts the line after the shebang of the Skua.app this script makes, which tells it from any other.
launcher_mark="# Made by install-macos.sh."
# Builds kept besides the one installed; any build a process still runs from is kept too.
keep=2

fail() {
  echo "install-macos.sh: $*" >&2
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

# Whether a process runs from the folder $1: it has files open there. lsof's exit code also counts processes it may not inspect, so only
# its output says whether one does.
running_from() {
  [[ -n "$(/usr/sbin/lsof -t +D "$1" 2>/dev/null)" ]]
}

if [[ "${SKUA_NO_TUI:-}" == 1 ]]; then with_tui=false; else with_tui=true; fi

[[ "$(uname -s)" == Darwin ]] || fail "this installs Skua on macOS; for Windows, see BUILD.md."
command -v dotnet >/dev/null || fail "the .NET 10 SDK was not found. Install it: brew install dotnet"
if $with_tui; then
  command -v cargo >/dev/null || fail "cargo was not found. Install Rust: https://rustup.rs, or set SKUA_NO_TUI=1 to install without skua-tui."
fi
case "$(uname -m)" in
  arm64) rid=osx-arm64 ;;
  x86_64) rid=osx-x64 ;;
  *) fail "unsupported CPU '$(uname -m)'." ;;
esac
[[ ! -e "$link" || -L "$link" ]] || fail "$link exists and isn't a link; move it away first."
if $with_tui; then
  [[ ! -e "$tui_link" || -L "$tui_link" ]] || fail "$tui_link exists and isn't a link; move it away first."
fi

app=false
publish_args=()
for arg in "$@"; do
  if [[ "$arg" == --app ]]; then
    app=true
  else
    publish_args+=("$arg")
  fi
done
if [[ -e "$launcher" ]]; then
  if ! sed -n 2p "$launcher/Contents/MacOS/Skua" 2>/dev/null | grep -qF "$launcher_mark"; then
    ! $app || fail "$launcher exists and isn't one this script made (a release of Skua.app, which updates itself?); move it away first, or set SKUA_APPS_DIR."
  elif grep -qF "$versions/" "$launcher/Contents/MacOS/Skua"; then
    # It opens a build of this install, so it moves to the build skua links to.
    app=true
  fi
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
bundle="$target/Skua.app"
if $app; then built="$bundle"; else built="$target"; fi
# A Skua.app made with SKUA_NO_TUI=1 lacks skua-tui, so it is built again, as one an app or Engine runs from never changes.
rebuild=false
if $app && $with_tui && [[ -e "$bundle" && ! -e "$bundle/Contents/Helpers/skua-tui" ]]; then
  if running_from "$bundle"; then
    fail "Skua $build's Skua.app was built without skua-tui and is running; quit it and its Engines, then run this again."
  fi
  rebuild=true
fi

if [[ -e "$built" ]] && ! $rebuild; then
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
  if $app; then
    # The app's publish puts skua-engine's inside the bundle too (Skua.App.Mac/AppBundle.targets).
    dotnet publish "$repo/Skua.App.Mac/Skua.App.Mac.csproj" --configuration Release --runtime "$rid" "-p:SkuaAppBundle=$staging/Skua.app" \
      "-p:InformationalVersion=$build" -p:IncludeSourceRevisionInInformationalVersion=false ${publish_args[@]+"${publish_args[@]}"}
  else
    dotnet publish "$repo/Skua.App.Engine/Skua.App.Engine.csproj" --configuration Release --runtime "$rid" --self-contained \
      --output "$staging" "-p:InformationalVersion=$build" -p:IncludeSourceRevisionInInformationalVersion=false ${publish_args[@]+"${publish_args[@]}"}
  fi
  if $with_tui && $app; then
    cp "$tui" "$staging/Skua.app/Contents/Helpers/"
    # Re-sealed, as the bundle was signed without it.
    codesign --force --sign - "$staging/Skua.app/Contents/Helpers/skua-tui" || fail "couldn't sign skua-tui."
    codesign --force --sign - "$staging/Skua.app" || fail "couldn't sign $staging/Skua.app."
    codesign --verify --deep --strict "$staging/Skua.app" || fail "$staging/Skua.app's signature doesn't verify."
  elif $with_tui; then
    cp "$tui" "$staging/"
  fi
  $rebuild || [[ ! -e "$built" ]] || fail "$built appeared during the build; is another install running?"
  if [[ -d "$target" ]]; then
    # The build was installed before without the app, or with one made without skua-tui; adding the bundle changes no file an Engine
    # runs from.
    if $rebuild; then
      if running_from "$bundle"; then fail "$bundle started running during the build; quit it and its Engines, then run this again."; fi
      rm -rf "$bundle"
    fi
    mv "$staging/Skua.app" "$bundle"
    rm -rf "$staging"
  else
    mv "$staging" "$target"
  fi
  trap - EXIT
fi

# Once the build has the app, skua is the one inside it, so the two always share a build.
if $app || [[ ! -e "$target/skua" ]]; then cli="$bundle/Contents/Helpers/skua"; else cli="$target/skua"; fi
if $with_tui && [[ ! -e "$(dirname "$cli")/skua-tui" ]]; then
  # The build was made with SKUA_NO_TUI=1. skua-tui is added beside its files, whole, and changes none of them.
  [[ "$cli" == "$target/skua" ]] || fail "Skua $build's Skua.app was built without skua-tui; run ./install-macos.sh --app to build it again."
  build_tui
  cp "$tui" "$target/.skua-tui.$$"
  mv "$target/.skua-tui.$$" "$target/skua-tui"
fi
mkdir -p "$bin_dir"
previous="$(readlink "$link" 2>/dev/null || true)"
ln -sfn "$cli" "$link"
if $with_tui; then
  ln -sfn "$(dirname "$cli")/skua-tui" "$tui_link"
elif [[ "$(readlink "$tui_link" 2>/dev/null)" == "$versions/"* ]]; then
  # It would keep an older build's skua-tui, and dangle once that build is removed.
  rm "$tui_link"
fi
if [[ -n "$previous" && "$previous" != "$versions/"* ]]; then
  echo "$link linked to $previous; it now links to this build."
fi

if $app; then
  # The installed Skua.app only opens the build's, so reinstalling never changes a file a running app uses, and the Dock and Finder keep
  # one Skua.app across builds. It is made afresh and swapped in whole.
  mkdir -p "$apps_dir"
  made="$(mktemp -d "$apps_dir/.Skua.app.XXXXXX")"
  trap 'rm -rf "$made" "$made.old"' EXIT
  chmod 755 "$made"
  mkdir -p "$made/Contents/MacOS" "$made/Contents/Resources"
  cp "$bundle/Contents/Info.plist" "$made/Contents/"
  cp "$bundle/Contents/Resources/Skua.icns" "$made/Contents/Resources/"
  # The build's app, quoted for sh.
  q="'"
  executable="$q${bundle//$q/$q\\$q$q}/Contents/MacOS/Skua$q"
  cat >"$made/Contents/MacOS/Skua" <<EOF
#!/bin/sh
$launcher_mark It opens Skua $build.
app=$executable
if [ ! -x "\$app" ]; then
  /usr/bin/osascript -e 'display alert "Skua is not installed" message "Its build is gone. Run ./install-macos.sh --app in your Skua checkout."' >/dev/null 2>&1
  exit 1
fi
exec "\$app" "\$@"
EOF
  chmod 755 "$made/Contents/MacOS/Skua"
  codesign --force --sign - "$made" || fail "couldn't sign $made."
  if [[ -e "$launcher" ]]; then
    mv "$launcher" "$made.old"
  fi
  mv "$made" "$launcher"
  rm -rf "$made.old"
  trap - EXIT
fi

# Older builds go, newest first, except those an Engine, an MCP server or an app still runs from (in its folder, or in its Skua.app).
# shellcheck disable=SC2012 # this script names the build folders, and ls sorts them by time
ls -1t "$versions" | { grep -vxF -- "$build" || true; } | tail -n +$((keep + 1)) | while IFS= read -r old; do
  if ! running_from "$versions/$old"; then
    rm -rf "${versions:?}/$old"
  fi
done

echo "Installed skua $build"
echo "  in $target"
if $with_tui; then echo "  linked from $link, and skua-tui from $tui_link"; else echo "  linked from $link, without skua-tui"; fi
if $app; then
  echo "  with the Mac App, opened by $launcher"
  if ps -axo command= | grep -F "$versions/" | grep -F "/Skua.app/Contents/MacOS/Skua" | grep -qvF "$bundle/"; then
    echo "A Skua app from an older build is running; it keeps that build until you quit and reopen it."
  fi
fi
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
