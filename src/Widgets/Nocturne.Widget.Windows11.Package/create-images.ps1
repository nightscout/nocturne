Add-Type -AssemblyName System.Drawing

function Create-PlaceholderPng($path, $width, $height, $bgColor, $textColor) {
    $bitmap = New-Object System.Drawing.Bitmap($width, $height)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.Clear($bgColor)

    # Draw a simple "N" for Nocturne
    $fontSize = [Math]::Min($width, $height) * 0.5
    if ($fontSize -lt 8) { $fontSize = 8 }
    $font = New-Object System.Drawing.Font("Segoe UI", $fontSize)
    $brush = New-Object System.Drawing.SolidBrush($textColor)
    $sf = New-Object System.Drawing.StringFormat
    $sf.Alignment = [System.Drawing.StringAlignment]::Center
    $sf.LineAlignment = [System.Drawing.StringAlignment]::Center
    $rect = New-Object System.Drawing.RectangleF(0, 0, $width, $height)
    $graphics.DrawString("N", $font, $brush, $rect, $sf)

    $graphics.Dispose()
    $bitmap.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
    Write-Host "Created $path"
}

$basePath = "src\Widgets\Nocturne.Widget.Windows11.Package\Images"
$darkBg = [System.Drawing.Color]::FromArgb(45, 45, 48)
$lightBg = [System.Drawing.Color]::FromArgb(240, 240, 240)
$white = [System.Drawing.Color]::White
$dark = [System.Drawing.Color]::FromArgb(30, 30, 30)

# Package logos
Create-PlaceholderPng "$basePath\StoreLogo.png" 50 50 $darkBg $white
Create-PlaceholderPng "$basePath\Square44x44Logo.png" 44 44 $darkBg $white
Create-PlaceholderPng "$basePath\Square150x150Logo.png" 150 150 $darkBg $white
Create-PlaceholderPng "$basePath\Wide310x150Logo.png" 310 150 $darkBg $white

# Widget provider icon
Create-PlaceholderPng "$basePath\WidgetProviderIcon.png" 44 44 $darkBg $white

# Widget icons - Small
Create-PlaceholderPng "$basePath\WidgetIconSmall.png" 44 44 $darkBg $white
Create-PlaceholderPng "$basePath\WidgetIconSmallDark.png" 44 44 $darkBg $white
Create-PlaceholderPng "$basePath\WidgetIconSmallLight.png" 44 44 $lightBg $dark

# Widget icons - Medium
Create-PlaceholderPng "$basePath\WidgetIconMedium.png" 44 44 $darkBg $white
Create-PlaceholderPng "$basePath\WidgetIconMediumDark.png" 44 44 $darkBg $white
Create-PlaceholderPng "$basePath\WidgetIconMediumLight.png" 44 44 $lightBg $dark

# Widget icons - Large
Create-PlaceholderPng "$basePath\WidgetIconLarge.png" 44 44 $darkBg $white
Create-PlaceholderPng "$basePath\WidgetIconLargeDark.png" 44 44 $darkBg $white
Create-PlaceholderPng "$basePath\WidgetIconLargeLight.png" 44 44 $lightBg $dark

# Widget screenshots (placeholders)
Create-PlaceholderPng "$basePath\WidgetScreenshotSmall.png" 200 100 $darkBg $white
Create-PlaceholderPng "$basePath\WidgetScreenshotMedium.png" 300 200 $darkBg $white
Create-PlaceholderPng "$basePath\WidgetScreenshotLarge.png" 400 300 $darkBg $white

Write-Host "Done creating all images"
