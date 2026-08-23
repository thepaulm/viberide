#!/usr/bin/env bash
# Build a .dmg and/or .pkg on a Mac, from a release zip or a built app.
#
#   bash make_installer.sh                       # newest VibeRide-*.zip here
#   bash make_installer.sh --zip ~/Downloads/VibeRide-0.8.3-mac-universal.zip
#   bash make_installer.sh --app /Applications/VibeRide.app --dmg
#
# Needs the Xcode command line tools (xcode-select --install). Nothing else.
#
# See MAC_INSTALLER.md for what these artifacts are for and how they differ.

set -euo pipefail

DMG=0
PKG=0
ZIP=""
APP=""
VERSION=""

while [ $# -gt 0 ]; do
    case "$1" in
        --dmg) DMG=1 ;;
        --pkg) PKG=1 ;;
        --zip) ZIP="${2:-}"; shift ;;
        --app) APP="${2:-}"; shift ;;
        --version) VERSION="${2:-}"; shift ;;
        -h|--help) sed -n '2,10p' "$0"; exit 0 ;;
        *) echo "unknown option: $1" >&2; exit 2 ;;
    esac
    shift
done
# Neither asked for means both.
[ "$DMG" = 0 ] && [ "$PKG" = 0 ] && { DMG=1; PKG=1; }

say()  { printf '\n==> %s\n' "$1"; }
die()  { printf '\nerror: %s\n' "$1" >&2; exit 1; }

command -v hdiutil  >/dev/null || die "hdiutil not found. Install the Xcode command line tools: xcode-select --install"
command -v codesign >/dev/null || die "codesign not found. Install the Xcode command line tools: xcode-select --install"

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
OUT="$PWD"

# ---------------------------------------------------------------- find the app
if [ -z "$APP" ]; then
    if [ -z "$ZIP" ]; then
        # Newest release zip in the current directory.
        ZIP="$(ls -t VibeRide-*-mac-universal.zip 2>/dev/null | head -1 || true)"
        [ -n "$ZIP" ] || die "No VibeRide-*-mac-universal.zip here. Pass --zip <path> or --app <path>."
    fi
    [ -f "$ZIP" ] || die "No such zip: $ZIP"
    say "Unpacking $(basename "$ZIP")"
    mkdir -p "$WORK/unzip"
    unzip -q "$ZIP" -d "$WORK/unzip"

    # The app travels inside the installer bundle; older zips had it at the top.
    for c in "$WORK/unzip/Install VibeRide.app/Contents/Resources/VibeRide.app" \
             "$WORK/unzip/VibeRide.app"; do
        [ -d "$c" ] && { APP="$c"; break; }
    done
    [ -n "$APP" ] || die "No VibeRide.app inside $ZIP"

    if [ -z "$VERSION" ] && [ -f "$WORK/unzip/VERSION.txt" ]; then
        VERSION="$(awk 'NR==1{print $2}' "$WORK/unzip/VERSION.txt")"
    fi
fi
[ -d "$APP" ] || die "No such app: $APP"

if [ -z "$VERSION" ]; then
    VERSION="$(defaults read "$(cd "$APP" && pwd)/Contents/Info" CFBundleShortVersionString 2>/dev/null || echo dev)"
fi
IDENT="$(defaults read "$(cd "$APP" && pwd)/Contents/Info" CFBundleIdentifier 2>/dev/null || echo com.viberide.app)"

say "App:        $APP"
echo "    version:    $VERSION"
echo "    identifier: $IDENT"

# Work on a copy: signing rewrites the bundle, and the source may be a download
# or someone's installed copy.
STAGE="$WORK/root"
mkdir -p "$STAGE"
cp -R "$APP" "$STAGE/VibeRide.app"
APP="$STAGE/VibeRide.app"

# A zip fetched with a browser is quarantined, and the flag would be baked into
# whatever we build from it.
say "Clearing quarantine on the copy"
xattr -dr com.apple.quarantine "$APP" 2>/dev/null || true

# ---------------------------------------------------------------- sign it
# This is the real reason to do any of this on a Mac. Unity ad-hoc signs the
# app, but the Windows build then adds the bridge under Contents/Resources and
# writes the Bluetooth usage strings into Info.plist, which invalidates that
# signature. macOS identifies apps to the privacy system by their signature, so
# a broken one means the Bluetooth grant never sticks and the bridge sees no
# devices. Signing here means the artifact is correct before it is packaged,
# rather than being patched up on the way in.
say "Signing (ad-hoc)"
codesign --force --deep --sign - "$APP"
codesign --verify --deep --strict --verbose=2 "$APP" 2>&1 | sed 's/^/    /'

# Expected to be rejected: ad-hoc is not a Developer ID and is not notarised.
# Printed anyway so the output is never a surprise.
say "Gatekeeper assessment (rejection here is expected)"
spctl --assess --type execute --verbose "$APP" 2>&1 | sed 's/^/    /' || true

# ---------------------------------------------------------------- .dmg
if [ "$DMG" = 1 ]; then
    say "Building the disk image"
    DMGDIR="$WORK/dmg"
    mkdir -p "$DMGDIR"
    cp -R "$APP" "$DMGDIR/VibeRide.app"
    ln -s /Applications "$DMGDIR/Applications"
    cp "$(dirname "$0")/START_HERE.md" "$DMGDIR/" 2>/dev/null || true

    DMGOUT="$OUT/VibeRide-$VERSION.dmg"
    rm -f "$DMGOUT"
    # UDZO is the compressed read-only format. Without it the image is the full
    # uncompressed size of the bundle -- about 112 MB against 46.
    hdiutil create -volname "VibeRide $VERSION" \
                   -srcfolder "$DMGDIR" \
                   -fs HFS+ -format UDZO -ov \
                   "$DMGOUT" | sed 's/^/    /'
    echo "    $DMGOUT ($(du -h "$DMGOUT" | cut -f1))"
fi

# ---------------------------------------------------------------- .pkg
if [ "$PKG" = 1 ]; then
    say "Building the installer package"
    PKGOUT="$OUT/VibeRide-$VERSION.pkg"
    rm -f "$PKGOUT"
    # --root holds what lands in --install-location, so this installs
    # /Applications/VibeRide.app and replaces any bundle already there. The app
    # is signed already, so there is no postinstall script to go wrong.
    pkgbuild --root "$STAGE" \
             --identifier "$IDENT" \
             --version "$VERSION" \
             --install-location /Applications \
             "$PKGOUT" | sed 's/^/    /'
    echo "    $PKGOUT ($(du -h "$PKGOUT" | cut -f1))"
fi

cat <<EOF

==> Done

Built on this Mac, so neither artifact carries a quarantine flag and neither
will be blocked when you open it here. Anything you upload and someone else
downloads is quarantined by their browser and will be blocked -- that needs a
paid Developer ID and notarisation, not these tools.

First launch still builds the Python environment in
~/Library/Application Support/VibeRide/bridge (about a minute, once). Saved
courses live beside it and are not touched by installing.
EOF
