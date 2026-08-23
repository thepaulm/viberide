# "I installed it but I'm still running the old one"

**Solved.** The `.pkg` was skipping itself. The Installer compared version
numbers, decided the copy already in `/Applications` was newer than the one it
was carrying, installed nothing, wrote a success receipt, and showed a green
checkmark. Fixed in `a32aeba`; every `.pkg` built after it installs
unconditionally.

If you are hitting this right now, get a pkg built after that commit -- the one
on the v0.9.1 release has been rebuilt, so the download link is the same:

```bash
cd ~/Downloads
curl -fL -O https://github.com/thepaulm/viberide/releases/download/v0.9.1/VibeRide-0.9.1-Installer.pkg
open VibeRide-0.9.1-Installer.pkg
```

`curl` does not quarantine, so nothing downloaded this way gets blocked. **You do
not need to delete the old app first** -- overwriting it is the point of the fix.
Then confirm you are on the new one:

```bash
defaults read /Applications/VibeRide.app/Contents/Info CFBundleShortVersionString
```

`0.9.1` means it worked.

---

## What it actually was

`pkgbuild` infers a **bundle component** from an `.app` in its payload, and a
bundle component is version-checked by default. So the Installer treats the pkg
as a proposal rather than an instruction: it reads
`CFBundleShortVersionString` out of the installed bundle, compares it against
the one in the payload, and drops the component if the installed copy looks
newer. From `/var/log/install.log`:

```
12:47:26  Installer[39312]:  Upgrade: "VibeRide-0.9.1-Installer"
12:47:26  PackageKit: Skipping component "com.viberide.app" (0.9.1-0.0.0-*)
          because the version 1.0.0-0.0.0-* is already installed
          at /Applications/VibeRide.app.
12:47:26  PackageKit: Extracting … (destination=/Library/InstallerSandboxes/
          .PKInstallSandboxManager/9DDF42DB-….activeSandbox/Root/Applications)
12:47:27  PackageKit: Writing receipt for com.viberide.app to /
12:47:27  Installed "VibeRide-0.9.1-Installer" ()
```

It resolved the right target, skipped the only component in the package,
unpacked the payload into a sandbox that gets thrown away, and recorded a
success.

**Where the `1.0` came from.** Unity leaves `CFBundleShortVersionString` at its
default `1.0` unless it is told otherwise, and `release.ps1` only started passing
`-buildVersion` after v0.8.3. So a v0.8.3-era app in `/Applications` claims to be
version 1.0 -- and every version this project has shipped is `0.x`. `1.0` beats
`0.9.1`, so **every release looked like a downgrade** and the pkg no-opped.

### Why it took three releases to notice

- **The Installer reported success**, with no warning anywhere in the UI.
- **The receipt agreed with it.** `pkgutil --pkg-info com.viberide.app` read
  `version: 0.9.1` while the bundle on disk read `1.0`.
- **The payload was correct the whole time**, so opening the pkg up and checking
  it proved nothing was wrong with it. The installed binary and the payload
  binary simply had different hashes.
- **The zip route kept working**, because the `Install VibeRide` script does
  `rm -rf` and `cp` and has no opinion about versions. Only the pkg was
  affected, which pointed suspicion at the app and the release rather than at
  the package.
- **It happened three times** -- 12:10 and 12:12 for the 0.9.0 pkg, 12:47 for
  0.9.1 -- with the identical skip line each time.

## The fix

`make_installer.sh` now runs `pkgbuild --analyze` first and turns off two
defaults on the component before building:

| | Default | Now | Why |
| --- | --- | --- | --- |
| `BundleIsVersionChecked` | true | **false** | there is nothing to compare; the package carries one specific build and the only correct thing to do is install it |
| `BundleIsRelocatable` | true | **false** | otherwise the Installer installs over a bundle with this identifier found *elsewhere* instead of `--install-location`, and `~/Applications` is exactly where the zip installer falls back to |

To check any pkg before trusting it:

```bash
pkgutil --expand VibeRide-0.9.1-Installer.pkg exp && cat exp/PackageInfo
```

`<bundle-version/>` and `<relocate/>` should both be **empty**, with the bundle
still listed under `<upgrade-bundle>`. If `<bundle-version>` names the bundle,
that pkg can skip itself.

## If it happens again -- the checks

These are what found it, in the order worth running.

### 1. Does the receipt disagree with the disk?

The tell for this whole class of bug. If the receipt names a version the bundle
does not, nothing was installed.

```bash
pkgutil --pkg-info com.viberide.app
defaults read /Applications/VibeRide.app/Contents/Info CFBundleShortVersionString
stat -f '%Sm' /Applications/VibeRide.app
```

### 2. What did the Installer decide?

The authoritative answer, and it says so in plain words.

```bash
grep -i vibe /var/log/install.log | tail -40
```

Two lines decide it. `Skipping component` is the failure, and

```
PackageKit: Touched bundle /Applications/VibeRide.app
```

is the success -- that is the payload actually landing. Note that the
`destination=` on the `Extracting` line is a path under
`/Library/InstallerSandboxes/` **either way**: every install stages there first
and is then "atomically shoved" into place, so a sandbox path is not itself a
sign of trouble. It is the missing `Touched bundle` that tells you the shove
never happened.

### 3. How many copies are on this machine?

Nothing stops several copies existing at once, and the Dock, Spotlight and a
double-click can each land on a different one.

```bash
mdfind -name 'VibeRide.app' 2>/dev/null
find / -maxdepth 6 -name 'VibeRide.app' -prune 2>/dev/null
```

`~/Applications` is the one to look for -- the zip installer falls back to it
without stopping when `/Applications` is not writable. A second hit under
`/System/Volumes/Data` is not a second copy; that is the same bundle via the
APFS firmlink.

### 4. What does the Dock icon point at?

A Dock entry keeps the path it was pinned from, so it can launch a copy in
`~/Downloads` no matter what is in `/Applications`.

```bash
defaults read com.apple.dock persistent-apps \
  | grep -A 2 -i viberide | grep _CFURLString
```

### 5. Ask the running app what it is

The menu shows the version at the bottom, under Exit, and every launch logs the
version and the directory it is running from:

```bash
grep -i 'VibeRide. version' ~/Library/Logs/VibeRide/VibeRide/Player.log | tail -5
```

Anything before v0.9.1 reports `1.0` regardless of what it actually is, so this
only settles it for newer builds.

## Also worth knowing

Before v0.9.1 the zip's installer aborted immediately *before* opening the app,
because a comment in it had lost its `#` and bash tried to run the text as a
command. The copy had already happened by then, so an install looked like it had
done nothing while having actually worked. Unrelated to the pkg bug above, and
also fixed.

## Still to fix

- **The `~/Applications` fallback in the zip installer.** An install can still
  succeed somewhere other than where it was asked to go, and say so only in a
  dialog that is easy to dismiss. The fallback should ask rather than guess.
  Turning off `BundleIsRelocatable` stopped the `.pkg` from being dragged into
  that directory, but did nothing about the script that creates it.

- **`VERSION` is still below 1.0.** Harmless now that nothing compares versions
  at install time, but a bundle claiming `1.0` next to releases numbered `0.9.x`
  will keep reading oddly anywhere else macOS or a person compares the two.
