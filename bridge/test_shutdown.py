"""Verify the bridge always dies with its parent.

An orphaned bridge keeps the trainer's one BLE connection and blocks the next
launch, so each shutdown path gets checked independently rather than trusting
that at least one of them works.
"""

import socket
import subprocess
import sys
import time

PY = sys.executable
PORT = 8799


def wait_listening(port, timeout=15.0):
    deadline = time.time() + timeout
    while time.time() < deadline:
        try:
            with socket.create_connection(("127.0.0.1", port), timeout=0.25):
                return True
        except OSError:
            time.sleep(0.15)
    return False


def spawn(extra=(), stdin=subprocess.PIPE):
    cmd = [PY, "-u", "-m", "kickr_bridge.server", "--demo", "--port", str(PORT), *extra]
    return subprocess.Popen(
        cmd, stdin=stdin, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL
    )


def check(label, ok, detail=""):
    print(f"  [{'ok' if ok else 'FAIL'}] {label}{('  ' + detail) if detail else ''}")
    return ok


def test_stdin_shutdown():
    print("shutdown via stdin message")
    p = spawn(["--watch-stdin"])
    try:
        if not wait_listening(PORT):
            return check("came up", False)
        t0 = time.time()
        p.stdin.write(b"shutdown\n")
        p.stdin.flush()
        try:
            p.wait(timeout=10)
        except subprocess.TimeoutExpired:
            return check("exited on 'shutdown'", False, "still running after 10s")
        return check("exited on 'shutdown'", True, f"in {time.time() - t0:.2f}s")
    finally:
        if p.poll() is None:
            p.kill()


def test_stdin_eof():
    print("shutdown via stdin EOF (parent vanished)")
    p = spawn(["--watch-stdin"])
    try:
        if not wait_listening(PORT):
            return check("came up", False)
        t0 = time.time()
        p.stdin.close()  # EOF, as if the parent died
        try:
            p.wait(timeout=10)
        except subprocess.TimeoutExpired:
            return check("exited on EOF", False, "still running after 10s")
        return check("exited on EOF", True, f"in {time.time() - t0:.2f}s")
    finally:
        if p.poll() is None:
            p.kill()


def test_parent_watchdog():
    print("shutdown via parent-pid watchdog (hard crash)")
    # A stand-in parent that exits on its own.
    parent = subprocess.Popen([PY, "-c", "import time; time.sleep(2.5)"])
    # Note: no --watch-stdin, and stdin inherited, so ONLY the watchdog can help.
    p = spawn(["--parent-pid", str(parent.pid)], stdin=None)
    try:
        if not wait_listening(PORT):
            return check("came up", False)
        parent.wait(timeout=15)
        t0 = time.time()
        try:
            p.wait(timeout=15)
        except subprocess.TimeoutExpired:
            return check("exited when parent died", False, "still running after 15s")
        return check("exited when parent died", True, f"in {time.time() - t0:.2f}s")
    finally:
        if p.poll() is None:
            p.kill()
        if parent.poll() is None:
            parent.kill()


def test_port_conflict():
    print("second bridge on a taken port fails cleanly")
    first = spawn(["--watch-stdin"])
    try:
        if not wait_listening(PORT):
            return check("first came up", False)
        second = subprocess.run(
            [PY, "-m", "kickr_bridge.server", "--demo", "--port", str(PORT)],
            capture_output=True, timeout=30,
        )
        ok = second.returncode != 0
        return check("second exits non-zero", ok, f"rc={second.returncode}")
    finally:
        if first.poll() is None:
            first.kill()


if __name__ == "__main__":
    results = [
        test_stdin_shutdown(),
        test_stdin_eof(),
        test_parent_watchdog(),
        test_port_conflict(),
    ]
    print("\nPASS" if all(results) else "\nFAILURES ABOVE")
    sys.exit(0 if all(results) else 1)
