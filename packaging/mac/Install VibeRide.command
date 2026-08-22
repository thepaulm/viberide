#!/usr/bin/env bash
# Double-click me.
#
# Installs VibeRide into /Applications, replacing any previous copy, and hands
# macOS what it needs to trust the app. Safe to run again over an existing
# install -- that is the point of it.
#
# It deliberately does NOT build the Python environment. The app does that
# itself on first launch, into ~/Library/Application Support/VibeRide, because
# a virtualenv cannot live inside a bundle that has been signed without
# invalidating the signature macOS hangs the Bluetooth permission on.
#
# If double-clicking is blocked, this also works:   bash "Install VibeRide.command"

set -euo pipefail
cd "$(dirname "$0")"

APP="VibeRide.app"
NAME="VibeRide"

say() { printf '\n==> %s\n' "$1"; }
die() { printf '\nerror: %s\n\n' "$1" >&2; read -r -p "Press return to close." _; exit 1; }

[ -d "$APP" ] || die "$APP is not next to this script. Keep them in the same folder."

# --- where it goes -----------------------------------------------------------
# /Applications is writable by admin users without sudo. When it is not, fall
# back to the per-user one rather than asking for a password.
DEST="/Applications"
if [ ! -w "$DEST" ]; then
    DEST="$HOME/Applications"
    mkdir -p "$DEST"
    say "/Applications is not writable; installing to $DEST"
fi
TARGET="$DEST/$APP"

# --- make way ----------------------------------------------------------------
if pgrep -x "$NAME" >/dev/null 2>&1; then
    say "Quitting the running copy"
    osascript -e "quit app \"$NAME\"" >/dev/null 2>&1 || true
    for _ in 1 2 3 4 5 6 7 8 9 10; do
        pgrep -x "$NAME" >/dev/null 2>&1 || break
        sleep 0.5
    done
    pkill -x "$NAME" 2>/dev/null || true
fi

if [ -e "$TARGET" ]; then
    say "Removing the previous install"
    # Whole-bundle replace rather than a merge. Copying over the top leaves
    # files from the old version behind in the new bundle, and a stale managed
    # library or data file is a genuinely baffling way for an app to break.
    rm -rf "$TARGET"
fi

say "Installing to $TARGET"
cp -R "$APP" "$TARGET"

# --- make macOS trust it -----------------------------------------------------
say "Clearing the quarantine flag"
xattr -dr com.apple.quarantine "$TARGET" 2>/dev/null || true

# Unity ad-hoc signs the app, but the build then adds the bridge under
# Contents/Resources and writes the Bluetooth usage strings into Info.plist,
# which invalidates that signature. macOS identifies apps to the privacy system
# by their signature, so a broken one means the Bluetooth grant never sticks --
# and the bridge, launched by the app, silently sees zero BLE devices.
say "Re-signing (ad-hoc)"
if command -v codesign >/dev/null 2>&1; then
    codesign --force --deep --sign - "$TARGET" 2>/dev/null \
        && echo "    signed" \
        || echo "    WARNING: re-signing failed; the Bluetooth permission may not stick"
else
    echo "    WARNING: codesign not found (install Xcode command line tools)"
    echo "             the Bluetooth permission may not stick"
fi

cat <<EOF

============================================================
Installed: $TARGET

Opening it now. The FIRST launch builds the Python
environment the trainer bridge needs -- that takes about a
minute, and the status panel in the app shows the progress.
Needs Python 3.9+: brew install python3

Wake the trainer first (spin the cranks), and make sure
nothing else holds it: Zwift, the Wahoo phone app or a head
unit will take the one BLE connection a trainer allows.

BLUETOOTH PERMISSION
--------------------
macOS asks on first scan. Allow it, or the trainer is never
found. The grant belongs to whichever app is RESPONSIBLE for
the process, so a permission you gave Terminal does NOT
cover the app launching the bridge itself.

  System Settings > Privacy & Security > Bluetooth

To make macOS ask again:
  tccutil reset Bluetooth com.viberide.app
============================================================

EOF

open "$TARGET"
