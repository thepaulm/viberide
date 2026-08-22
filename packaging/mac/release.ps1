# Build, package and publish a macOS release to GitHub.
#
#   powershell -File packaging\mac\release.ps1 -Version 0.2.0
#   powershell -File packaging\mac\release.ps1 -Version 0.2.0 -Draft
#   powershell -File packaging\mac\release.ps1 -Version 0.2.0 -SkipBuild   # repackage only
#
# The zip is attached to a GitHub Release rather than committed. A 45 MB binary
# committed on every build would grow the repository without bound, and git
# stores no useful diff between two builds of the same app.
#
# Requires: Unity 6000.2.8f1 with Mac Build Support, `gh auth login` once,
# and Python 3 on PATH -- the zip has to record Unix file modes and .NET
# cannot write them. See makezip.py.

param(
    [Parameter(Mandatory = $true)][string]$Version,
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
$tag     = "v$Version"
$zip     = Join-Path $repo "VibeRide-$Version-mac-universal.zip"

function Step($m) { Write-Host "`n==> $m" -ForegroundColor Cyan }

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
        -logFile $log | Out-Null

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

Copy-Item $app (Join-Path $stage "VibeRide.app") -Recurse
Copy-Item (Join-Path $PSScriptRoot "Install VibeRide.command") $stage
Copy-Item (Join-Path $PSScriptRoot "START_HERE.md")            $stage

# Stamp the build so a downloaded copy can be traced back to a commit.
@"
VibeRide $Version
commit  $sha
built   $(Get-Date -Format 'yyyy-MM-dd HH:mm')
source  https://github.com/thepaulm/viberide
"@ | Out-File -FilePath (Join-Path $stage "VERSION.txt") -Encoding ascii

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

If macOS blocks the double-click because the file came from the internet,
right-click it and choose Open, then confirm. Or run ``bash "Install VibeRide.command"``.

The first launch builds the Python environment the trainer bridge needs -- about a
minute, with progress in the app's status panel. Needs Python 3.9+
(``brew install python3``). There is no separate setup step any more.

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
        $ghArgs = @("release", "create", $tag, $zip,
                    "--title", "VibeRide $Version", "--notes", $Notes, "--target", $shaFull)
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
