#!/usr/bin/env bash
# Terminal equivalent of double-clicking "Install VibeRide".
#
#   bash install.sh
#
# Same script either way -- this just reaches inside the installer bundle, which
# is where it lives so that macOS has a way to let you approve it.
exec bash "$(dirname "$0")/Install VibeRide.app/Contents/MacOS/install" "$@"
