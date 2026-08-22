#!/usr/bin/env python3
"""Build the macOS package zip, carrying Unix permissions.

Written in Python rather than PowerShell for one reason: the zip has to record
Unix file modes, and .NET's ZipArchive cannot. The ZIP central directory keeps
the mode in the high 16 bits of `external attributes`, but an unpacker only
believes it when the `version made by` host byte says Unix -- and .NET stamps
that from the machine it runs on, which here is Windows. Python lets both be
set, so an archive built on Windows can still arrive on a Mac with an
executable app in it.

That is what removed the setup step. Before this, `Contents/MacOS/VibeRide`
unpacked as mode 644, the bundle would not launch, and a shell script had to
chmod it back before the app could be opened at all.

Entry names always use forward slashes: macOS does not treat a backslash as a
separator and unpacks such an entry as one long filename, which turns the
bundle into a heap of flat files.
"""
import os
import stat
import sys
import zipfile

# Anything matching these is stored executable. Everything else is 0644.
EXEC_DIRS = ("/Contents/MacOS/",)
EXEC_SUFFIXES = (".sh", ".command", ".dylib", ".so")

UNIX = 3          # "version made by" host: 3 == Unix
MODE_EXEC = 0o755
MODE_FILE = 0o644
MODE_DIR = 0o755


def mode_for(rel: str) -> int:
    lowered = "/" + rel
    if any(d in lowered for d in EXEC_DIRS):
        return MODE_EXEC
    if rel.endswith(EXEC_SUFFIXES):
        return MODE_EXEC
    return MODE_FILE


def add(zf: zipfile.ZipFile, rel: str, source: str, mode: int) -> None:
    info = zipfile.ZipInfo(rel, date_time=(2026, 1, 1, 0, 0, 0))
    info.create_system = UNIX
    info.external_attr = (mode & 0xFFFF) << 16
    info.compress_type = zipfile.ZIP_DEFLATED
    with open(source, "rb") as fh:
        zf.writestr(info, fh.read())


def add_dir(zf: zipfile.ZipFile, rel: str) -> None:
    info = zipfile.ZipInfo(rel.rstrip("/") + "/", date_time=(2026, 1, 1, 0, 0, 0))
    info.create_system = UNIX
    # The directory bit has to be set in the mode as well as the DOS attribute,
    # or some unpackers create a zero-length file with the directory's name.
    info.external_attr = ((stat.S_IFDIR | MODE_DIR) & 0xFFFF) << 16 | 0x10
    info.compress_type = zipfile.ZIP_STORED
    zf.writestr(info, b"")


def main() -> int:
    if len(sys.argv) != 3:
        print("usage: makezip.py <stage-dir> <out.zip>", file=sys.stderr)
        return 2
    stage, out = sys.argv[1], sys.argv[2]

    if os.path.exists(out):
        os.remove(out)

    files = 0
    execs = 0
    dirs = 0
    with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as zf:
        for root, dirnames, filenames in os.walk(stage):
            dirnames.sort()
            rel_root = os.path.relpath(root, stage).replace("\\", "/")
            if rel_root != ".":
                add_dir(zf, rel_root)
                dirs += 1
            for name in sorted(filenames):
                rel = name if rel_root == "." else rel_root + "/" + name
                mode = mode_for(rel)
                add(zf, rel, os.path.join(root, name), mode)
                files += 1
                if mode == MODE_EXEC:
                    execs += 1

    print(f"wrote {files} files, {dirs} directories, {execs} executable")

    # Read it back. Every one of these has been a real failure at some point:
    # backslash separators turning the bundle into flat files, the launcher
    # landing at the wrong path, the bridge missing from Contents/Resources,
    # and the app arriving without its executable bit.
    with zipfile.ZipFile(out) as zf:
        entries = zf.infolist()
        bad = [e.filename for e in entries if "\\" in e.filename]
        print(f"entries containing a backslash: {len(bad)}")

        launcher = next((e for e in entries if "/Contents/MacOS/" in e.filename
                         and not e.is_dir()), None)
        if launcher is None:
            print("ERROR: no launcher entry under Contents/MacOS/")
            return 1
        mode = (launcher.external_attr >> 16) & 0o7777
        host = launcher.create_system
        print(f"launcher entry: {launcher.filename} mode {mode:o} host {host}")
        if host != UNIX or not mode & 0o111:
            print("ERROR: launcher is not marked executable for Unix")
            return 1

        bridge = [e for e in entries if "/Contents/Resources/bridge/" in e.filename
                  and not e.is_dir()]
        print(f"bridge entries: {len(bridge)}")
        if not bridge:
            print("ERROR: bridge missing from the bundle")
            return 1

    print(f"size: {os.path.getsize(out) / 1048576:.1f} MB")
    return 0


if __name__ == "__main__":
    sys.exit(main())
