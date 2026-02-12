# generate-assets.ps1
# Single source of truth for all Nocturne branding assets.
#
# Prerequisites:
#   ImageMagick 7+ (https://imagemagick.org) - `magick` must be on PATH
#
# Usage:
#   .\generate-assets.ps1                       # Generate from placeholder
#   .\generate-assets.ps1 -Source logo.png       # Generate from a real logo
#
# When you have a final logo, drop it in this directory as a square PNG
# (ideally 512x512 or larger) and pass it via -Source.

param(
    [string]$Source
)

$ErrorActionPreference = "Stop"

# Verify ImageMagick is available
if (!(Get-Command "magick" -ErrorAction SilentlyContinue)) {
    Write-Host "ImageMagick 7+ is required. Install from https://imagemagick.org" -ForegroundColor Red
    Write-Host "  winget install ImageMagick.ImageMagick" -ForegroundColor Yellow
    exit 1
}

$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path

# --- Output directories ---
$Outputs = @{
    Web           = Join-Path $RepoRoot "src\Web\packages\app\static"
    Tray          = Join-Path $RepoRoot "src\Desktop\Nocturne.Desktop.Tray\Assets"
    TrayInstaller = Join-Path $RepoRoot "src\Desktop\Nocturne.Desktop.Tray\installer"
    Widget        = Join-Path $RepoRoot "src\Widgets\Nocturne.Widget.Windows11\Images"
    WidgetPkg     = Join-Path $RepoRoot "src\Widgets\Nocturne.Widget.Windows11.Package\Images"
}

foreach ($dir in $Outputs.Values) {
    if (!(Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
}
$webImagesDir = Join-Path $Outputs.Web "images"
if (!(Test-Path $webImagesDir)) { New-Item -ItemType Directory -Path $webImagesDir -Force | Out-Null }

# --- Colors ---
$DarkBg  = "#2D2D30"
$LightBg = "#F0F0F0"

# ============================================================
# Helpers
# ============================================================

function New-Placeholder([int]$Width, [int]$Height, [string]$Bg, [string]$Fg, [string]$OutPath) {
    $fontSize = [Math]::Floor([Math]::Min($Width, $Height) * 0.5)
    if ($fontSize -lt 8) { $fontSize = 8 }
    magick -size "${Width}x${Height}" "xc:${Bg}" `
        -font "Segoe-UI-Bold" -pointsize $fontSize `
        -fill $Fg -gravity center -annotate +0+0 "N" `
        $OutPath
    Write-Host "  $OutPath"
}

function Resize-Image([string]$Src, [int]$Size, [string]$OutPath) {
    magick $Src -resize "${Size}x${Size}" -strip $OutPath
    Write-Host "  $OutPath"
}

function New-WideImage([string]$Src, [int]$Width, [int]$Height, [string]$Bg, [string]$OutPath) {
    magick $Src -resize "x${Height}" `
        -gravity center -background $Bg -extent "${Width}x${Height}" `
        -strip $OutPath
    Write-Host "  $OutPath"
}

function Save-Ico([string]$Src, [string]$OutPath) {
    magick $Src -define icon:auto-resize=256,48,32,16 -strip $OutPath
    Write-Host "  $OutPath"
}

# ============================================================
# Create master source images
# ============================================================
$masterDark  = Join-Path $PSScriptRoot "_master_dark.png"
$masterLight = Join-Path $PSScriptRoot "_master_light.png"

if ($Source -and (Test-Path $Source)) {
    $resolved = (Resolve-Path $Source).Path
    Write-Host "Using source: $resolved" -ForegroundColor Cyan
    magick $resolved -resize "512x512" -strip $masterDark
    Copy-Item $masterDark $masterLight
} else {
    if ($Source) {
        Write-Host "Source file '$Source' not found, generating placeholder." -ForegroundColor Yellow
    }
    Write-Host "Generating placeholder assets (run with -Source logo.png when you have a logo)" -ForegroundColor Yellow
    New-Placeholder 512 512 $DarkBg "white" $masterDark
    New-Placeholder 512 512 $LightBg "#1E1E1E" $masterLight
}

# ============================================================
# Asset generation pipeline
# ============================================================

Write-Host ""
Write-Host "=== Web ===" -ForegroundColor Green
Resize-Image $masterDark 32  (Join-Path $Outputs.Web "favicon.png")
Resize-Image $masterDark 64  (Join-Path $webImagesDir "logo-64.png")
Resize-Image $masterDark 128 (Join-Path $webImagesDir "logo-128.png")

Write-Host ""
Write-Host "=== Desktop Tray ===" -ForegroundColor Green
Resize-Image $masterDark 44  (Join-Path $Outputs.Tray "Square44x44Logo.png")
Resize-Image $masterDark 150 (Join-Path $Outputs.Tray "Square150x150Logo.png")
Resize-Image $masterDark 50  (Join-Path $Outputs.Tray "StoreLogo.png")
Resize-Image $masterDark 48  (Join-Path $Outputs.Tray "LockScreenLogo.scale-200.png")
New-WideImage $masterDark 310 150 $DarkBg (Join-Path $Outputs.Tray "Wide310x150Logo.png")
New-WideImage $masterDark 620 300 $DarkBg (Join-Path $Outputs.Tray "SplashScreen.scale-200.png")

Write-Host ""
Write-Host "=== Tray Installer (ICO) ===" -ForegroundColor Green
Save-Ico $masterDark (Join-Path $Outputs.TrayInstaller "app.ico")

Write-Host ""
Write-Host "=== Windows 11 Widget ===" -ForegroundColor Green
foreach ($targetDir in @($Outputs.Widget, $Outputs.WidgetPkg)) {
    Write-Host "  -> $targetDir" -ForegroundColor DarkGray

    # Package icons
    Resize-Image $masterDark 44  (Join-Path $targetDir "Square44x44Logo.png")
    Resize-Image $masterDark 150 (Join-Path $targetDir "Square150x150Logo.png")
    Resize-Image $masterDark 50  (Join-Path $targetDir "StoreLogo.png")
    New-WideImage $masterDark 310 150 $DarkBg (Join-Path $targetDir "Wide310x150Logo.png")

    # Widget provider icon
    Resize-Image $masterDark 44  (Join-Path $targetDir "WidgetProviderIcon.png")

    # Widget icons - each size in default (dark), dark, and light
    foreach ($sizeLabel in @("Small", "Medium", "Large")) {
        Resize-Image $masterDark  44 (Join-Path $targetDir "WidgetIcon${sizeLabel}.png")
        Resize-Image $masterDark  44 (Join-Path $targetDir "WidgetIcon${sizeLabel}Dark.png")
        Resize-Image $masterLight 44 (Join-Path $targetDir "WidgetIcon${sizeLabel}Light.png")
    }

    # Widget screenshots (placeholders - these will be replaced with real screenshots)
    New-Placeholder 200 100 $DarkBg "white" (Join-Path $targetDir "WidgetScreenshotSmall.png")
    New-Placeholder 300 200 $DarkBg "white" (Join-Path $targetDir "WidgetScreenshotMedium.png")
    New-Placeholder 400 300 $DarkBg "white" (Join-Path $targetDir "WidgetScreenshotLarge.png")
}

# Clean up temp files
Remove-Item $masterDark  -ErrorAction SilentlyContinue
Remove-Item $masterLight -ErrorAction SilentlyContinue

Write-Host ""
Write-Host "All assets generated." -ForegroundColor Green
Write-Host "To use a real logo, run: .\generate-assets.ps1 -Source your-logo.png" -ForegroundColor DarkGray
