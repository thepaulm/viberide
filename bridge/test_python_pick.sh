#!/usr/bin/env bash
# Exercise the interpreter search in setup_mac.sh with fake pythons.
#
#   bash test_python_pick.sh
#
# The search has to run each candidate rather than look for the file, and it has
# to keep going after a rejection. Both are easy to get subtly wrong and neither
# shows up on a machine that happens to have one good interpreter first on PATH:
# the "old first, good later" case below failed silently until it was written
# down, on a Mac that plainly had a usable Python.
set -uo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(mktemp -d)"
trap 'rm -rf "$ROOT"' EXIT

mkfake() {          # mkfake <dir> <version>
    mkdir -p "$1"
    cat > "$1/python3" <<EOF
#!/usr/bin/env bash
V="$2"
maj=\${V%%.*}; rest=\${V#*.}; min=\${rest%%.*}
case "\$*" in
    *"version_info >= (3, 9)"*)
        if [ "\$maj" -gt 3 ] || { [ "\$maj" -eq 3 ] && [ "\$min" -ge 9 ]; }; then exit 0; else exit 1; fi ;;
    *"print(sys.version.split()"*) echo "\$V"; exit 0 ;;
esac
exit 0
EOF
    chmod +x "$1/python3"
}

# setup_mac.sh cds to its own directory, so run a copy from a scratch dir and
# stop it once it has announced its choice.
stage() {
    local d="$ROOT/run"; rm -rf "$d"; mkdir -p "$d"
    sed 's/^echo "Creating virtualenv.*/exit 42/' \
        "$HERE/setup_mac.sh" > "$d/setup_mac.sh"
    echo "$d"
}

run() {             # run <label> <PATH-prefix> [PYTHON override]
    local d; d=$(stage)
    local out
    if [ -n "${3:-}" ]; then
        out=$(cd "$d" && PATH="$2:/usr/bin:/bin" PYTHON="$3" bash setup_mac.sh 2>&1)
    else
        out=$(cd "$d" && PATH="$2:/usr/bin:/bin" bash setup_mac.sh 2>&1)
    fi
    printf '%-28s %s\n' "$1" "$(echo "$out" | grep -E '^(Using|error:)' | head -1)"
}

mkfake "$ROOT/good" 3.11.7
mkfake "$ROOT/old"  3.8.10
mkfake "$ROOT/new"  3.13.1

run "modern python on PATH"   "$ROOT/good"
run "only an old python"      "$ROOT/old"
run "old first, good later"   "$ROOT/old:$ROOT/good"
run "PYTHON override honoured" "$ROOT/good" "$ROOT/new/python3"
# Only a versioned interpreter, no bare python3 -- ordinary after a Homebrew
# python upgrade leaves python3.12 without relinking python3.
mkdir -p "$ROOT/vonly"
cp "$ROOT/new/python3" "$ROOT/vonly/python3.13"
run "only python3.13 present"  "$ROOT/vonly"

run "nothing at all"          "$ROOT/empty"
