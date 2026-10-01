#!/usr/bin/env bash
# The steps of a Skua.app release (.github/workflows/release-macos.yml) that are Skua's own, so the tests run them as the workflow does.
#
#   release.sh version <tag>                  vX.Y.Z -> X.Y.Z, the app's CFBundleShortVersionString; fails for any other tag
#   release.sh notes <changelogs-mac.md>      the newest section of the change log, as Markdown
#   release.sh html                           Markdown on stdin -> the HTML the update window shows
#   release.sh public-key                     a Sparkle private key on stdin -> its public key, as SUPublicEDKey holds it
#   release.sh verify <public-key> <file> <signature>
#   release.sh appcast <version> <build> <zip-url> <sign_update output> <notes.html>
#                                             the appcast.xml for one release, on stdout
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

fail() {
  echo "release.sh: $*" >&2
  exit 1
}

# Escapes text for XML and HTML.
escape() {
  sed -e 's/&/\&amp;/g' -e 's/</\&lt;/g' -e 's/>/\&gt;/g' -e 's/"/\&quot;/g'
}

case "${1:-}" in
  version)
    [[ $# -eq 2 ]] || fail "usage: release.sh version <tag>"
    [[ "$2" =~ ^v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$ ]] || fail "'$2' isn't a release tag; tags are vX.Y.Z, e.g. v1.0.0."
    echo "${2#v}"
    ;;
  notes)
    [[ $# -eq 2 ]] || fail "usage: release.sh notes <changelogs-mac.md>"
    # From the first dated heading to the next ---.
    notes="$(awk '/^## / { found = 1 } found && /^---/ { exit } found' "$2")"
    [[ -n "$notes" ]] || fail "$2 has no section under a ## heading."
    echo "$notes"
    ;;
  html)
    [[ $# -eq 1 ]] || fail "usage: release.sh html < notes.md"
    escape | perl -ne '
      chomp;
      s/\*\*(.+?)\*\*/<b>$1<\/b>/g;
      s/`(.+?)`/<code>$1<\/code>/g;
      s/\[(.+?)\]\((.+?)\)/<a href="$2">$1<\/a>/g;
      if (/^[*-] (.*)/) { print "<ul>\n" unless $list; $list = 1; print "<li>$1</li>\n"; next }
      if ($list) { print "</ul>\n"; $list = 0 }
      if (/^(#{1,6}) (.*)/) { my $n = length($1) + 1; $n = 6 if $n > 6; print "<h$n>$2</h$n>\n" }
      elsif (/\S/) { print "<p>$_</p>\n" }
      END { print "</ul>\n" if $list }'
    ;;
  public-key)
    [[ $# -eq 1 ]] || fail "usage: release.sh public-key < private-key"
    swift "$here/ed25519.swift" public-key
    ;;
  verify)
    [[ $# -eq 4 ]] || fail "usage: release.sh verify <public-key> <file> <signature>"
    swift "$here/ed25519.swift" verify "$2" "$3" "$4"
    ;;
  appcast)
    [[ $# -eq 6 ]] || fail "usage: release.sh appcast <version> <build> <zip-url> <sign_update output> <notes.html>"
    version="$2" build="$3" url="$4" signed="$5" notes="$6"
    [[ "$build" =~ ^[1-9][0-9]*$ ]] || fail "the build '$build' isn't a positive whole number."
    [[ "$signed" =~ ^sparkle:edSignature=\"[A-Za-z0-9+/]+=*\"\ length=\"[0-9]+\"$ ]] || fail "'$signed' isn't sign_update's output."
    [[ -f "$notes" ]] || fail "no release notes at $notes."
    cat <<EOF
<?xml version="1.0" encoding="utf-8"?>
<rss version="2.0" xmlns:sparkle="http://www.andymatuschak.org/xml-namespaces/sparkle">
  <channel>
    <title>Skua</title>
    <item>
      <title>Skua $(escape <<<"$version")</title>
      <pubDate>$(LC_ALL=C date -u '+%a, %d %b %Y %H:%M:%S +0000')</pubDate>
      <sparkle:version>$build</sparkle:version>
      <sparkle:shortVersionString>$(escape <<<"$version")</sparkle:shortVersionString>
      <description><![CDATA[$(sed 's/]]>/]]]]><![CDATA[>/g' "$notes")]]></description>
      <enclosure url="$(escape <<<"$url")" type="application/octet-stream" $signed/>
    </item>
  </channel>
</rss>
EOF
    ;;
  *)
    sed -n '2,10p' "$0" | sed 's/^# \{0,1\}//' >&2
    exit 2
    ;;
esac
