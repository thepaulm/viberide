#!/usr/bin/env bash
# One-time macOS setup. Run this once, then just open VibeRide.app.
#
#   bash setup.sh
#
# Does four things:
#   1. restores the executable bit (a zip made on Windows cannot carry it)
#   2. clears the Gatekeeper quarantine flag (the app is unsigned)
#   3. re-signs the app so macOS can attach a Bluetooth permission to it
#   4. builds the Python virtualenv the app needs to launch the bridge

set -euo pipefail
cd "$(dirname "$0")"

APP="VibeRide.app"
[ -d "$APP" ] || { echo "error: $APP not found next to this script."; exit 1; }

echo "==> Restoring executable permissions"
chmod +x "$APP/Contents/MacOS/"* 2>/dev/null || true
chmod +x bridge/*.sh 2>/dev/null || true

echo "==> Clearing quarantine flag"
xattr -dr com.apple.quarantine "$APP" 2>/dev/null || true

# Unity ad-hoc signs the app, but the build then adds the bridge to
# Contents/Resources and writes the Bluetooth usage strings into Info.plist,
# which invalidates that signature. macOS identifies apps to the privacy system
# by their signature, so a broken one means the Bluetooth grant will not stick --
# and the bridge, launched by the app, silently sees zero BLE devices.
echo "==> Re-signing the app (ad-hoc)"
if command -v codesign >/dev/null 2>&1; then
    codesign --force --deep --sign - "$APP" 2>/dev/null \
        && echo "    signed" \
        || echo "    WARNING: re-signing failed; Bluetooth permission may not stick"
else
    echo "    WARNING: codesign not found; Bluetooth permission may not stick"
fi

# Prefer a bridge beside the app: the app looks there first, and it avoids
# writing a virtualenv inside the bundle, which fails wherever the app is not
# writable. The bundled copy inside Contents/Resources stays as a fallback.
if [ ! -d bridge ]; then
    echo "==> Copying bridge out of the app bundle"
    cp -R "$APP/Contents/Resources/bridge" ./bridge
    chmod +x bridge/*.sh 2>/dev/null || true
fi

echo "==> Setting up the Python environment"
cd bridge
chmod +x setup_mac.sh 2>/dev/null || true
bash setup_mac.sh

cat <<'EOF'

============================================================
Setup complete. Open VibeRide.app -- it starts the bridge
itself and shuts it down when you quit.

Wake the trainer first (spin the cranks), and make sure no
other app holds it: Zwift, the Wahoo phone app, or a head
unit will take the one BLE connection a trainer allows.

BLUETOOTH PERMISSION
--------------------
macOS asks on first launch. Allow it, or the trainer is
never found. The grant belongs to whichever app is
RESPONSIBLE for the process -- so a permission you already
gave Terminal does NOT cover the app launching the bridge
itself. They are tracked separately.

Check it under:
  System Settings > Privacy & Security > Bluetooth
and make sure "VibeRide" is listed and switched on.

The in-app status panel names the reason when the trainer
is not connected. "No BLE devices visible at all" means
permission, not a sleeping trainer.

To force macOS to ask again:
  tccutil reset Bluetooth com.viberide.app
============================================================
EOF
