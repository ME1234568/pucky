<#
.SYNOPSIS
Builds Pucky for a Windows runtime and assembles
artifacts/dist/pucky-<version>-<rid>.zip.

.PARAMETER Runtime
win-x64 or win-arm64. Use scripts/package.sh for Linux and macOS.

.PARAMETER Version
Version used in the archive name. Defaults to <Version> in Directory.Build.props.
#>
param(
    [ValidateSet("win-x64", "win-arm64")]
    [string]$Runtime = "win-x64",
    [string]$Version
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

if (-not $Version) {
    $props = Get-Content -Raw -LiteralPath (Join-Path $root "Directory.Build.props")
    $match = [regex]::Match($props, "<Version>\s*([^<\s]+)\s*</Version>")
    if (-not $match.Success) {
        throw "Could not read <Version> from Directory.Build.props."
    }
    $Version = $match.Groups[1].Value
}
$Version = $Version -replace "^v", ""
if ($Version -notmatch "^[0-9A-Za-z.+-]+$") {
    throw "Invalid version: $Version"
}

& (Join-Path $PSScriptRoot "build.ps1") -Runtime $Runtime

$publish = Join-Path (Join-Path $root "artifacts") $Runtime
$name = "pucky-$Version-$Runtime"
$stageRoot = Join-Path (Join-Path $root "artifacts") "stage"
$stage = Join-Path $stageRoot $name
$dist = Join-Path (Join-Path $root "artifacts") "dist"
$archive = Join-Path $dist "$name.zip"

if (Test-Path -LiteralPath $stage) {
    Remove-Item -LiteralPath $stage -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $stage, $dist | Out-Null

Copy-Item -Path (Join-Path $publish "*") -Destination $stage -Recurse
# Debug symbols are not useful to end users and only grow the download.
Get-ChildItem -LiteralPath $stage -Recurse -Filter "*.pdb" | Remove-Item -Force

Copy-Item -LiteralPath (Join-Path $root "LICENSE") -Destination (Join-Path $stage "LICENSE.txt")
Copy-Item -LiteralPath (Join-Path (Join-Path (Join-Path $root "packaging") "windows") "INSTALL.txt") `
    -Destination (Join-Path $stage "INSTALL.txt")

if (Test-Path -LiteralPath $archive) {
    Remove-Item -LiteralPath $archive -Force
}
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
# Add entries one by one so names always use "/" separators. Windows
# PowerShell's ZipFile.CreateFromDirectory writes "\", which other unzip tools
# do not treat as directories.
$zip = [System.IO.Compression.ZipFile]::Open($archive, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    $stageRootFull = (Resolve-Path -LiteralPath $stageRoot).ProviderPath.TrimEnd('\', '/')
    Get-ChildItem -LiteralPath $stage -Recurse -File | Sort-Object FullName | ForEach-Object {
        $entryName = $_.FullName.Substring($stageRootFull.Length + 1).Replace('\', '/')
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
            $zip,
            $_.FullName,
            $entryName,
            [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
}
finally {
    $zip.Dispose()
}

Remove-Item -LiteralPath $stage -Recurse -Force
Write-Host "Packaged $archive"
