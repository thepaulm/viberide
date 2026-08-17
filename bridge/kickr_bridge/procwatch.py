"""Is a given process still alive?

Used as the backstop that stops the bridge outliving the app. The app asks for a
graceful shutdown when it quits normally, but a crash, a force-quit or a SIGKILL
never sends anything -- and an orphaned bridge keeps the trainer's single BLE
connection, which blocks the next launch. Polling the parent PID covers every one
of those cases.
"""

from __future__ import annotations

import asyncio
import logging
import os
import sys

log = logging.getLogger(__name__)

_STILL_ACTIVE = 259
_PROCESS_QUERY_LIMITED_INFORMATION = 0x1000


def _alive_windows(pid: int) -> bool:
    import ctypes

    kernel32 = ctypes.windll.kernel32
    handle = kernel32.OpenProcess(_PROCESS_QUERY_LIMITED_INFORMATION, False, pid)
    if not handle:
        return False
    try:
        code = ctypes.c_ulong()
        if not kernel32.GetExitCodeProcess(handle, ctypes.byref(code)):
            return False
        # A process that has exited still has an openable handle until every
        # reference is dropped, so the handle alone is not proof of life.
        return code.value == _STILL_ACTIVE
    finally:
        kernel32.CloseHandle(handle)


def _alive_posix(pid: int) -> bool:
    try:
        os.kill(pid, 0)
    except ProcessLookupError:
        return False
    except PermissionError:
        # Exists, owned by someone else. Alive as far as we care.
        return True
    return True


def is_alive(pid: int) -> bool:
    if pid <= 0:
        return False
    return _alive_windows(pid) if sys.platform == "win32" else _alive_posix(pid)


async def watch_stdin(stop: asyncio.Event):
    """Stop on a "shutdown" line, or on EOF.

    The app writes "shutdown" when it quits normally, which gets us an immediate
    clean stop rather than waiting for the next parent poll. EOF covers the case
    where the app died without saying anything -- the pipe closes by itself when
    the writing end goes away, so this needs no cooperation from a crashing
    process.
    """
    loop = asyncio.get_running_loop()

    def _readline() -> str:
        try:
            return sys.stdin.readline()
        except Exception:  # noqa: BLE001 - stdin may be closed underneath us
            return ""

    while not stop.is_set():
        line = await loop.run_in_executor(None, _readline)
        if line == "":
            log.info("stdin closed -- shutting down.")
            stop.set()
            return
        if line.strip().lower() == "shutdown":
            log.info("Shutdown requested on stdin.")
            stop.set()
            return


async def watch_parent(pid: int, stop: asyncio.Event, interval: float = 2.0):
    """Set `stop` once the given process is gone."""
    log.info("Watching parent process %d; will shut down when it exits.", pid)
    while not stop.is_set():
        await asyncio.sleep(interval)
        if not is_alive(pid):
            log.info("Parent process %d has exited -- shutting down.", pid)
            stop.set()
            return
