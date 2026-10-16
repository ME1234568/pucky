param(
    [ValidateSet("win-x64", "win-arm64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64")]
    [string]$Runtime = "win-x64",
    [switch]$FrameworkDependent
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path (Join-Path (Join-Path $root "src") "Pucky.App") "Pucky.App.csproj"
$output = Join-Path (Join-Path $root "artifacts") $Runtime
$selfContained = if ($FrameworkDependent) { "false" } else { "true" }

# Start from an empty output directory so files left over from an earlier
# build (or a different publish layout) never end up in a package.
if (Test-Path -LiteralPath $output) {
    Remove-Item -LiteralPath $output -Recurse -Force
}

dotnet publish $project `
    --configuration Release `
    --runtime $Runtime `
    --self-contained $selfContained `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    --output $output

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

Write-Host "Pucky published to $output"
