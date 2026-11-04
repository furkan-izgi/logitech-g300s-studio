# Builds a self-contained, single-file portable executable of Logitech G300s Studio into ./dist/
$ErrorActionPreference = "Stop"

$projPath = Join-Path $PSScriptRoot "native\G300sController\G300sController.csproj"
$outDir = Join-Path $PSScriptRoot "dist"

Write-Host "Publishing self-contained portable Logitech G300s Studio..." -ForegroundColor Cyan

dotnet publish $projPath `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:IncludeAllContentForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -o $outDir

$exePath = Join-Path $outDir "G300sController.exe"
$friendlyPath = Join-Path $outDir "LogitechG300sStudio.exe"

$assetsSrc = Join-Path $PSScriptRoot "native\G300sController\Assets"
$assetsDest = Join-Path $outDir "Assets"
if (Test-Path $assetsSrc) {
    Copy-Item -Path $assetsSrc -Destination $outDir -Recurse -Force
}

if (Test-Path $exePath) {
    Copy-Item $exePath $friendlyPath -Force
    Write-Host "Portable executable ready: $friendlyPath" -ForegroundColor Green
}
