param(
    [string]$Version = "0.1.0"
)

$ErrorActionPreference = "Stop"
$ProjectDir = $PSScriptRoot
$PublishDir = Join-Path $ProjectDir "publish"

Write-Host "Building Nocturne.Desktop.Tray v$Version" -ForegroundColor Cyan
Write-Host "Project directory: $ProjectDir"
Write-Host ""

# Clean previous publish output
if (Test-Path $PublishDir) {
    Write-Host "Cleaning previous publish output..." -ForegroundColor Yellow
    Remove-Item -Recurse -Force $PublishDir
}

# Publish x64
Write-Host ""
Write-Host "=== Publishing x64 ===" -ForegroundColor Green
$x64Output = Join-Path $PublishDir "x64"
dotnet publish $ProjectDir `
    -c Release `
    -p:Platform=x64 `
    -r win-x64 `
    -p:Version=$Version `
    -p:GenerateNSwagClient=false `
    -o $x64Output

if ($LASTEXITCODE -ne 0) {
    Write-Host "x64 publish failed with exit code $LASTEXITCODE" -ForegroundColor Red
    exit $LASTEXITCODE
}

# Publish arm64
Write-Host ""
Write-Host "=== Publishing arm64 ===" -ForegroundColor Green
$arm64Output = Join-Path $PublishDir "arm64"
dotnet publish $ProjectDir `
    -c Release `
    -p:Platform=arm64 `
    -r win-arm64 `
    -p:Version=$Version `
    -p:GenerateNSwagClient=false `
    -o $arm64Output

if ($LASTEXITCODE -ne 0) {
    Write-Host "arm64 publish failed with exit code $LASTEXITCODE" -ForegroundColor Red
    exit $LASTEXITCODE
}

# Summary
Write-Host ""
Write-Host "=== Build Summary ===" -ForegroundColor Cyan
Write-Host "Version: $Version"
Write-Host ""

foreach ($arch in @("x64", "arm64")) {
    $dir = Join-Path $PublishDir $arch
    if (Test-Path $dir) {
        $files = Get-ChildItem -Path $dir -Recurse -File
        $totalSize = ($files | Measure-Object -Property Length -Sum).Sum
        $sizeMB = [math]::Round($totalSize / 1MB, 2)
        $fileCount = $files.Count
        Write-Host "$arch : $dir" -ForegroundColor White
        Write-Host "       $fileCount files, $sizeMB MB" -ForegroundColor Gray
    }
}

Write-Host ""
Write-Host "Build complete." -ForegroundColor Green
