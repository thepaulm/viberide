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

# Find a Python already on this Mac. Installing one is the last resort, not the
# first move -- most Macs already have a usable interpreter, and the user's own
# is the one they would expect to be used.
#
# Each candidate is RUN, not merely looked for. Two reasons:
#
#   - /usr/bin/python3 exists on every Mac since Catalina, but it is a shim for
#     the command line tools. Where those are not installed it does not run at
#     all; it pops a GUI dialog offering to install them. So it is only ever
#     considered when xcode-select can confirm something is behind it, and it is
#     considered last.
#   - Version and venv support have to be checked on the interpreter that will
#     actually be used, and testing one while running another is how you get a
#     confident yes followed by a failure two steps later.

clt_ok() { xcode-select -p >/dev/null 2>&1; }

usable() {
    local py="$1" resolved
    [ -n "$py" ] || return 1
    command -v "$py" >/dev/null 2>&1 || return 1
    resolved=$(command -v "$py")

    case "$resolved" in
        /usr/bin/python3) clt_ok || return 1 ;;
    esac

    "$resolved" -c 'import sys, venv; sys.exit(0 if sys.version_info >= (3, 9) else 1)' \
        >/dev/null 2>&1
}

# In preference order: an explicit override, then the user's own PATH (pyenv,
# conda, Homebrew -- their choice, so honour it), then the usual install
# locations, then Apple's.
CANDIDATES=("${PYTHON:-}")

# EVERY python3 along PATH, not just the first. An old interpreter earlier in
# PATH than a new one is an ordinary situation -- a system python3 ahead of a
# Homebrew one, say -- and taking only the first match turns that into "no
# Python 3.9+ found" on a machine that plainly has one.
while IFS= read -r p; do CANDIDATES+=("$p"); done < <(type -aP python3 2>/dev/null)

# Versioned names too, newest first. A Mac can easily have python3.12 without a
# bare python3 pointing at it.
for v in 3.14 3.13 3.12 3.11 3.10 3.9; do
    while IFS= read -r p; do CANDIDATES+=("$p"); done < <(type -aP "python$v" 2>/dev/null)
done

CANDIDATES+=(
    /opt/homebrew/bin/python3
    /usr/local/bin/python3
    /opt/local/bin/python3
    "$HOME/.pyenv/shims/python3"
)
# python.org installs, newest first.
for f in $(ls -d /Library/Frameworks/Python.framework/Versions/*/bin/python3 2>/dev/null | sort -rV); do
    CANDIDATES+=("$f")
done
CANDIDATES+=(/usr/bin/python3)

PY=""
for c in "${CANDIDATES[@]}"; do
    if usable "$c"; then PY=$(command -v "$c"); break; fi
done

if [ -z "$PY" ]; then
    echo "Looked for Python 3.9+ in:"
    for c in "${CANDIDATES[@]}"; do [ -n "$c" ] && echo "    $c"; done
    if ! clt_ok; then
        echo
        echo "Apple's /usr/bin/python3 is only a stub until the command line tools"
        echo "are installed. Either of these gives a working Python:"
        echo "    xcode-select --install"
        echo "    brew install python3"
    fi
    echo
    echo "error: no Python 3.9+ found -- try: brew install python3"
    exit 1
fi

echo "Using $PY ($("$PY" -c 'import sys;print(sys.version.split()[0])'))"

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
