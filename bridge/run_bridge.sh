#!/usr/bin/env bash
# Start the bridge. Any arguments are passed through to the server, e.g.
#
#   ./run_bridge.sh --demo
#   ./run_bridge.sh --match KICKR --rider-kg 78

set -euo pipefail
cd "$(dirname "$0")"

if [ ! -x ./.venv/bin/python ]; then
    echo "error: no virtualenv here. Run ./setup_mac.sh first."
    exit 1
fi

exec ./.venv/bin/python -m kickr_bridge.server "$@"
