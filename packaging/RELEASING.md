# Releasing

A release takes two machines. Unity only runs on the Windows build host, and
`pkgbuild` and `codesign` only exist on a Mac, so the zip is published from one
and the `.pkg` people actually download is added from the other.

## 1. Windows: build and publish the zip

```powershell
powershell -File packaging\mac\release.ps1 -Version 0.2.0
```

That builds the macOS player, stages the package, zips it, and attaches it to a
GitHub Release tagged `v0.2.0`, with notes describing the zip.

## 2. Mac: add the .pkg

```bash
bash packaging/mac/release_pkg.sh          # newest release
bash packaging/mac/release_pkg.sh v0.2.0   # or a specific tag
```

That pulls the zip back off the release, signs the app properly, builds
`VibeRide-0.2.0.pkg`, uploads it, and rewrites the release notes to lead with
it. `--dry-run` does everything except the upload and leaves the pkg in the
repo root for inspection.

Signing is the reason this half cannot be faked on Windows: Unity ad-hoc signs
the app, then the Windows staging adds the bridge and the Bluetooth usage
strings, which invalidates that signature — and macOS keys the Bluetooth grant
to it. [mac/MAC_INSTALLER.md](mac/MAC_INSTALLER.md) has the detail, plus how to
build a `.dmg` if you want one.

Stopping after step 1 leaves a correct, zip-only release; the notes written
there describe the zip and stay true. Step 2 only ever adds.

## Prerequisites, once

On the Windows host:

- Unity 6000.2.8f1 with **Mac Build Support (Mono)**
- GitHub CLI, authenticated:

  ```powershell
  winget install GitHub.cli
  gh auth login
  ```

  Authentication is interactive and browser-based — the script never handles a
  token itself.

On the Mac:

- the Xcode command line tools (`xcode-select --install`)
- `gh`, likewise authenticated (`brew install gh && gh auth login`)

## Useful flags for release.ps1

| Flag | Effect |
| --- | --- |
| `-Draft` | create the release unpublished, to check before it goes live |
| `-SkipBuild` | repackage an existing `Builds/Mac` without rebuilding |
| `-NoPublish` | build and zip only, upload nothing |
| `-Notes "..."` | replace the generated release notes |

Re-running with a version that already exists replaces the attached asset rather
than erroring, so a bad upload is easy to correct.

## What the script refuses to do

It stops before building if the working tree is **dirty** or has **unpushed
commits**. A published binary that nobody can reproduce from a commit is worse
than no binary — and the stamped `VERSION.txt` inside the zip would be a lie.

It also refuses to build while the Unity editor is open, because an open editor
holds the project lock and the batch build dies with a bare exit code 1 and no
useful message.

## Why a release rather than a commit

A 45 MB zip committed on every build grows the repository without bound, and git
stores no meaningful diff between two builds of the same application. Release
assets live outside git history, so cloning the source stays cheap.

## Not automated in CI

Building this in GitHub Actions would need a Unity licence activated inside the
runner, which means putting Unity credentials in repository secrets. That is a
real option — `game-ci` exists for it — but it is a bigger commitment than a
local script, and worth doing only once releases become frequent enough for the
manual step to be the bottleneck.
