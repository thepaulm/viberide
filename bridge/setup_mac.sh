#!/usr/bin/env bash
# One-time setup for the bridge on macOS.
#
#   ./setup_mac.sh
#
# The bridge is the only part that touches Bluetooth. The Unity app talks to it
# over 127.0.0.1, so the app itself needs no Bluetooth permission -- but THIS
# process does, and macOS will prompt for it the first time you scan.

set -euo pipefail
cd "$(dirname "$0")"

PY="${PYTHON:-python3}"

if ! command -v "$PY" >/dev/null 2>&1; then
    echo "error: $PY not found."
    echo "Install it with:  brew install python3      (or xcode-select --install)"
    exit 1
fi

# bleak needs 3.9+; CoreBluetooth support assumes a reasonably modern macOS.
"$PY" - <<'EOF'
import sys
if sys.version_info < (3, 9):
    sys.exit(f"error: Python 3.9+ required, found {sys.version.split()[0]}")
EOF

echo "Creating virtualenv in .venv ..."
"$PY" -m venv .venv

echo "Installing dependencies ..."
./.venv/bin/python -m pip install --quiet --upgrade pip
./.venv/bin/python -m pip install --quiet bleak websockets

echo
echo "Verifying ..."
./.venv/bin/python selftest.py

cat <<'EOF'

Python environment ready.

You do NOT need to start the bridge yourself -- VibeRide.app launches it and
shuts it down with the app. Just open the app.

Running it by hand is only for debugging, when you want to watch its log. The
app detects a bridge that is already listening and uses that one rather than
starting a second:

  ./run_bridge.sh            connect to the KICKR
  ./run_bridge.sh --demo     synthetic rider, no trainer needed

macOS asks for Bluetooth permission on the first scan, attributed to whatever
is responsible for the process -- the app when it launches the bridge, or your
terminal when you run it by hand. If you miss the prompt, enable it under
System Settings > Privacy & Security > Bluetooth.

Note: macOS reports BLE devices by CoreBluetooth UUID rather than MAC address,
so match the trainer by NAME (the default, "KICKR") rather than by the address
you might have noted on Windows -- it will be a different identifier there.
EOF
