# Build, package and publish a macOS release to GitHub.
#
#   powershell -File packaging\mac\release.ps1                  # version from .\VERSION
#   powershell -File packaging\mac\release.ps1 -Draft
#   powershell -File packaging\mac\release.ps1 -SkipBuild        # repackage only
#   powershell -File packaging\mac\release.ps1 -Replace          # overwrite that version
#
# The version lives in the VERSION file at the repo root, so bumping it is part
# of the change that needs releasing and shows up in the diff. It used to be
# only an argument here, which meant nothing in the repository recorded what had
# been shipped -- five commits once piled up behind a published tag without
# anything noticing.
#
# The zip is attached to a GitHub Release rather than committed. A 45 MB binary
# committed on every build would grow the repository without bound, and git
# stores no useful diff between two builds of the same app.
#
# Requires: Unity 6000.2.8f1 with Mac Build Support, `gh auth login` once,
# and Python 3 on PATH -- the zip has to record Unix file modes and .NET
# cannot write them. See makezip.py.

param(
    [string]$Version = "",
    [switch]$Replace,
    [switch]$Draft,
    [switch]$SkipBuild,
    [switch]$NoPublish,
    [string]$Notes = ""
)

$ErrorActionPreference = "Stop"

$repo    = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$unity   = "C:\Program Files\Unity\Hub\Editor\6000.2.8f1\Editor\Unity.exe"
$project = Join-Path $repo "unity"
$app     = Join-Path $project "Builds\Mac\VibeRide.app"

if (-not $Version) {
    $versionFile = Join-Path $repo "VERSION"
    if (-not (Test-Path $versionFile)) {
        throw "No -Version given and no VERSION file at $versionFile"
    }
    $Version = (Get-Content $versionFile -Raw).Trim()
    if (-not $Version) { throw "VERSION file is empty" }
}

$tag     = "v$Version"
$zip     = Join-Path $repo "VibeRide-$Version-mac-universal.zip"

function Step($m) { Write-Host "`n==> $m" -ForegroundColor Cyan }

# Refuse to ship a version that is already out, before spending a build on it.
#
# The old behaviour was to quietly replace the asset on an existing release,
# which is exactly how work can accumulate unnoticed: the release looks updated
# while the tag still points at an older commit. Bump VERSION, or say -Replace
# and mean it.
if (-not $NoPublish -and (Get-Command gh -ErrorAction SilentlyContinue)) {
    $prevEAPv = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    $null = & gh release view $tag --json tagName 2>&1
    $alreadyOut = ($LASTEXITCODE -eq 0)
    $ErrorActionPreference = $prevEAPv

    if ($alreadyOut -and -not $Replace) {
        throw "$tag is already published. Bump VERSION for a new release, or pass -Replace to overwrite this one."
    }
}

# --- refuse to ship a dirty or unpushed tree ---------------------------------
# A published binary nobody can reproduce from a commit is worse than no binary.
Step "Checking the working tree"
Push-Location $repo
$dirty = git status --porcelain
if ($dirty) {
    Write-Host $dirty
    throw "Working tree is dirty. Commit or stash before releasing."
}
git fetch origin --quiet 2>$null
$ahead = git rev-list --count origin/main..HEAD 2>$null
if ($ahead -and [int]$ahead -gt 0) { throw "$ahead commit(s) not pushed. Push before releasing." }
$sha = (git rev-parse --short HEAD).Trim()
# GitHub rejects an abbreviated SHA as target_commitish with HTTP 422, so
# keep the full one for the API and the short one for humans.
$shaFull = (git rev-parse HEAD).Trim()
Write-Host "    clean, at $sha"

# --- build -------------------------------------------------------------------
if (-not $SkipBuild) {
    Step "Building the macOS player (editor must be closed)"
    if (Get-Process Unity -ErrorAction SilentlyContinue) {
        throw "Unity is running. An open editor holds the project lock and the batch build fails with a bare exit code 1."
    }
    if (Test-Path (Join-Path $project "Builds\Mac")) {
        Remove-Item (Join-Path $project "Builds\Mac") -Recurse -Force
    }
    $log = Join-Path $repo "unity-release.log"
    & $unity -batchmode -quit -projectPath $project `
        -executeMethod KickrWorld.EditorTools.PlayerBuilder.BuildAllMacFromCommandLine `
        -buildVersion $Version `
        -logFile $log | Out-Null

    # -buildVersion writes PlayerSettings.bundleVersion into ProjectSettings.asset,
    # so the build dirties the tree it just insisted was clean -- and the next
    # release would refuse to start. The built app already carries the version;
    # the setting itself is not worth keeping.
    git checkout -- (Join-Path $project "ProjectSettings/ProjectSettings.asset") 2>$null

    $result = Select-String -Path $log -Pattern "result=Succeeded" -Quiet
    if (-not $result) {
        Select-String -Path $log -Pattern "error CS|FAILED|result=" | Select-Object -First 10 |
            ForEach-Object { Write-Host "    $($_.Line)" }
        throw "Unity build failed. See $log"
    }
    Select-String -Path $log -Pattern "architecture set|result=" | ForEach-Object { Write-Host "    $($_.Line)" }
}

if (-not (Test-Path $app)) { throw "No app at $app" }

# --- stage -------------------------------------------------------------------
Step "Staging the package"
$stage = Join-Path $env:TEMP "viberide-release\VibeRide"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stage | Out-Null

# The installer is an .app, not a .command. Gatekeeper blocks both, but only an
# .app gets an "Open Anyway" button in System Settings > Privacy & Security --
# a script is refused with no visible way to allow it.
#
# The app being installed goes INSIDE the installer, under Contents/Resources.
# macOS runs a quarantined app through App Translocation -- from a randomised
# read-only copy, without its siblings -- so an installer that expects to find
# the payload beside itself finds an empty directory instead.
Copy-Item (Join-Path $PSScriptRoot "installer\Install VibeRide.app") $stage -Recurse
$payload = Join-Path $stage "Install VibeRide.app\Contents\Resources"
New-Item -ItemType Directory -Force -Path $payload | Out-Null
Copy-Item $app (Join-Path $payload "VibeRide.app") -Recurse
Copy-Item (Join-Path $PSScriptRoot "install.sh")        $stage
Copy-Item (Join-Path $PSScriptRoot "START_HERE.md")     $stage

# Stamp the build so a downloaded copy can be traced back to a commit.
#
# Written with explicit LF endings. Out-File on Windows PowerShell writes CRLF,
# and this file ships inside a macOS package where something will eventually
# parse it -- as the Mac-side installer build did, carrying a trailing carriage
# return into every artifact name. That failure hides well: ls prints the name
# looking correct while stat and cp both report no such file.
$versionText = @(
    "VibeRide $Version"
    "commit  $sha"
    "built   $(Get-Date -Format 'yyyy-MM-dd HH:mm')"
    "source  https://github.com/thepaulm/viberide"
) -join "`n"
[System.IO.File]::WriteAllText(
    (Join-Path $stage "VERSION.txt"),
    $versionText + "`n",
    (New-Object System.Text.ASCIIEncoding))

Get-ChildItem $stage | ForEach-Object { Write-Host "    $($_.Name)" }

# --- zip ---------------------------------------------------------------------
Step "Building the zip"
if (-not (Get-Command python -ErrorAction SilentlyContinue)) {
    throw "python not found. The zip must record Unix file modes, which .NET cannot write."
}
& python (Join-Path $PSScriptRoot "makezip.py") $stage $zip
if ($LASTEXITCODE -ne 0) { throw "makezip.py failed (exit $LASTEXITCODE)" }
$mb = [math]::Round((Get-Item $zip).Length / 1MB, 1)
Write-Host "    $zip  ($mb MB)"

if ($NoPublish) { Step "-NoPublish given; stopping before upload"; Pop-Location; return }

# --- publish -----------------------------------------------------------------
Step "Publishing release $tag"
if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    throw "gh not found. Install it (winget install GitHub.cli) and run 'gh auth login'."
}
$prevEAP0 = $ErrorActionPreference
$ErrorActionPreference = "Continue"
$null = & gh auth status 2>&1
$authed = ($LASTEXITCODE -eq 0)
$ErrorActionPreference = $prevEAP0
if (-not $authed) { throw "gh is not authenticated. Run: gh auth login" }

if (-not $Notes) {
    $Notes = @"
macOS universal build (Apple Silicon + Intel), built from $sha.

**Install:** unzip and double-click **Install VibeRide**. It replaces any previous
copy in /Applications, clears the Gatekeeper quarantine flag, re-signs the app so
macOS can attach a Bluetooth permission to it, and opens it.

macOS will block it the first time, because the app is not signed with a paid
Apple Developer ID. Open **System Settings > Privacy & Security**, scroll to the
message naming Install VibeRide, and click **Open Anyway**. Once only.

To skip that entirely, download from a terminal -- files fetched with curl are not
quarantined, so nothing gets blocked:

```
cd ~/Downloads && curl -fL -O https://github.com/thepaulm/viberide/releases/download/$tag/VibeRide-$Version-mac-universal.zip && unzip -o VibeRide-$Version-mac-universal.zip -d VibeRide && bash VibeRide/install.sh
```

The first launch builds the Python environment the trainer bridge needs -- about a
minute, with progress in the app's status panel. It uses a Python 3.9+ you already
have, and only asks you to install one if it cannot find any. There is no separate
setup step any more.

See START_HERE.md inside the zip for the full instructions.
"@
}

# PowerShell 5.1 turns a native command's stderr into a terminating error while
# ErrorActionPreference is Stop, and `gh release view` writes "release not found"
# to stderr as its normal way of reporting absence. Drop to Continue around gh
# and judge success by exit code instead.
$prevEAP = $ErrorActionPreference
$ErrorActionPreference = "Continue"
try {
    $null = & gh release view $tag --json tagName 2>&1
    $exists = ($LASTEXITCODE -eq 0)

    if ($exists) {
        Write-Host "    release $tag exists; replacing the asset"
        & gh release upload $tag $zip --clobber 2>&1 | ForEach-Object { Write-Host "    $_" }
        if ($LASTEXITCODE -ne 0) { throw "gh release upload failed ($LASTEXITCODE)" }
    } else {
        # Notes go via a file, never as an argument. They contain quoted shell
        # snippets, and PowerShell re-splits a native command's arguments on the
        # embedded quotes -- which turned `bash "Install VibeRide.command"` into
        # gh complaining "no matches found for VibeRide.command".
        $notesFile = Join-Path $env:TEMP "viberide-notes-$Version.md"
        Set-Content -Path $notesFile -Value $Notes -Encoding utf8
        $ghArgs = @("release", "create", $tag, $zip,
                    "--title", "VibeRide $Version", "--notes-file", $notesFile,
                    "--target", $shaFull)
        if ($Draft) { $ghArgs += "--draft" }
        & gh @ghArgs 2>&1 | ForEach-Object { Write-Host "    $_" }
        if ($LASTEXITCODE -ne 0) { throw "gh release create failed ($LASTEXITCODE)" }
    }

    Step "Done"
    & gh release view $tag --json tagName,url,assets 2>&1 | ForEach-Object { Write-Host "    $_" }
}
finally {
    $ErrorActionPreference = $prevEAP
    Pop-Location
}
