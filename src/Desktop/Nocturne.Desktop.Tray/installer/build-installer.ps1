param(
    [string]$Version = "0.1.0"
)

$ErrorActionPreference = "Stop"
$InstallerDir = $PSScriptRoot

Write-Host "Building Nocturne Tray installer v$Version" -ForegroundColor Cyan
Write-Host "Installer directory: $InstallerDir"
Write-Host ""

# Locate Inno Setup compiler
$isccPaths = @(
    "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
    "C:\Program Files\Inno Setup 6\ISCC.exe"
)

$iscc = $null
foreach ($path in $isccPaths) {
    if (Test-Path $path) {
        $iscc = $path
        break
    }
}

if (-not $iscc) {
    $found = Get-Command "ISCC.exe" -ErrorAction SilentlyContinue
    if ($found) {
        $iscc = $found.Source
    }
}

if (-not $iscc) {
    Write-Host "Inno Setup compiler (ISCC.exe) not found. Install Inno Setup 6 or add it to PATH." -ForegroundColor Red
    exit 1
}

Write-Host "Using Inno Setup: $iscc" -ForegroundColor Gray

# Verify publish output exists
$publishDir = Join-Path $InstallerDir "..\publish\x64"
if (-not (Test-Path $publishDir)) {
    Write-Host "Publish output not found at $publishDir" -ForegroundColor Red
    Write-Host "Run build.ps1 first to produce the publish output." -ForegroundColor Red
    exit 1
}

# Build installer
$setupScript = Join-Path $InstallerDir "setup.iss"
Write-Host "Compiling installer..." -ForegroundColor Green

& $iscc "/DMyAppVersion=$Version" $setupScript

if ($LASTEXITCODE -ne 0) {
    Write-Host "Inno Setup compilation failed with exit code $LASTEXITCODE" -ForegroundColor Red
    exit $LASTEXITCODE
}

# Summary
$outputDir = Join-Path $InstallerDir "output"
$installer = Get-ChildItem -Path $outputDir -Filter "*.exe" | Select-Object -First 1

if ($installer) {
    $sizeMB = [math]::Round($installer.Length / 1MB, 2)
    Write-Host ""
    Write-Host "=== Installer Built ===" -ForegroundColor Cyan
    Write-Host "File: $($installer.FullName)" -ForegroundColor White
    Write-Host "Size: $sizeMB MB" -ForegroundColor Gray
} else {
    Write-Host "Warning: No installer .exe found in output directory." -ForegroundColor Yellow
}

Write-Host ""
Write-Host "Installer build complete." -ForegroundColor Green
