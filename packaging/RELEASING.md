# Releasing

> Building a `.dmg` or `.pkg` needs a Mac; see
> [mac/MAC_INSTALLER.md](mac/MAC_INSTALLER.md). Everything below runs on the
> Windows build host and produces the zip.

One command, from a clean tree to a published binary:

```powershell
powershell -File packaging\mac\release.ps1 -Version 0.2.0
```

That builds the macOS player, stages the package, zips it, and attaches it to a
GitHub Release tagged `v0.2.0`.

## Prerequisites, once

- Unity 6000.2.8f1 with **Mac Build Support (Mono)**
- GitHub CLI, authenticated:

  ```powershell
  winget install GitHub.cli
  gh auth login
  ```

  Authentication is interactive and browser-based — the script never handles a
  token itself.

## Useful flags

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
