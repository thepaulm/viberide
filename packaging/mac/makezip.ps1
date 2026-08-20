# Build the macOS package zip with spec-compliant forward-slash entry names.
# PowerShell's Compress-Archive emits backslash separators, which macOS does not
# treat as directory separators -- it unpacks them as literal filenames and the
# .app bundle arrives as a heap of flat files instead of a bundle.

param(
    [Parameter(Mandatory = $true)][string]$Stage,
    [Parameter(Mandatory = $true)][string]$Zip
)

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

if (Test-Path $Zip) { [System.IO.File]::Delete($Zip) }

$sep = [string][char]92   # backslash, built from its code point to keep the
$fwd = [string][char]47   # shell's path scanner out of the way

$stream = [System.IO.File]::Create($Zip)
$archive = New-Object System.IO.Compression.ZipArchive($stream, [System.IO.Compression.ZipArchiveMode]::Create)

$count = 0
foreach ($f in Get-ChildItem $Stage -Recurse -File) {
    $rel = $f.FullName.Substring($Stage.Length + 1).Replace($sep, $fwd)
    $entry = $archive.CreateEntry($rel, [System.IO.Compression.CompressionLevel]::Optimal)
    $es = $entry.Open()
    $in = [System.IO.File]::OpenRead($f.FullName)
    $in.CopyTo($es)
    $in.Close()
    $es.Close()
    $count++
}

$archive.Dispose()
$stream.Close()

Write-Output "wrote $count entries"

# Verify no backslashes survived, and that the launcher is present at the right path.
$check = [System.IO.Compression.ZipFile]::OpenRead($Zip)
$bad = @($check.Entries | Where-Object { $_.FullName.Contains($sep) }).Count
Write-Output "entries containing a backslash: $bad"
Write-Output "launcher entry: $(($check.Entries | Where-Object { $_.FullName -like '*Contents/MacOS/*' } | Select-Object -First 1).FullName)"
# The bridge lives inside the bundle, not at the top level -- it moved there when
# the app took over spawning it. Matching 'bridge/*' reported 0 on every healthy
# release and would have made a genuinely broken one indistinguishable.
Write-Output "bridge entries: $(@($check.Entries | Where-Object { $_.FullName -like '*/Contents/Resources/bridge/*' }).Count)"
$check.Dispose()

$mb = [math]::Round((Get-Item $Zip).Length / 1MB, 1)
Write-Output "size: $mb MB"
