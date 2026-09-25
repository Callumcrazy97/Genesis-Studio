<#
.SYNOPSIS
    Rasterizes Genesis Studio branding into Inno Setup wizard bitmaps.

.DESCRIPTION
    Uses the same logo, emblem and dark canvas colour as the Studio shell so the installer
    matches the product rather than the default wizard beige. Output is 24-bit BMP because that
    is the Inno Setup 6 format that does not depend on PNG alpha support.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$AssetsDirectory,

    [Parameter(Mandatory)]
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing

$logoPath = Join-Path $AssetsDirectory 'Genesis_Studio_Logo.png'
$emblemPath = Join-Path $AssetsDirectory 'Genesis_Studio_Emblem.png'
if (-not (Test-Path -LiteralPath $logoPath -PathType Leaf)) {
    throw "Genesis logo missing: $logoPath"
}
if (-not (Test-Path -LiteralPath $emblemPath -PathType Leaf)) {
    throw "Genesis emblem missing: $emblemPath"
}

[void][System.IO.Directory]::CreateDirectory($OutputDirectory)

$canvas = [System.Drawing.Color]::FromArgb(255, 30, 30, 30)

function New-Canvas([int]$Width, [int]$Height) {
    $bitmap = New-Object System.Drawing.Bitmap $Width, $Height, ([System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.Clear($canvas)
    $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    return @{ Bitmap = $bitmap; Graphics = $graphics }
}

function Add-CenteredImage(
    $Surface,
    [System.Drawing.Image]$Image,
    [int]$Padding
) {
    $maxWidth = [Math]::Max(1, $Surface.Bitmap.Width - (2 * $Padding))
    $maxHeight = [Math]::Max(1, $Surface.Bitmap.Height - (2 * $Padding))
    $scale = [Math]::Min($maxWidth / $Image.Width, $maxHeight / $Image.Height)
    $drawWidth = [Math]::Max(1, [int][Math]::Round($Image.Width * $scale))
    $drawHeight = [Math]::Max(1, [int][Math]::Round($Image.Height * $scale))
    $x = [int](($Surface.Bitmap.Width - $drawWidth) / 2)
    $y = [int](($Surface.Bitmap.Height - $drawHeight) / 2)
    $Surface.Graphics.DrawImage($Image, $x, $y, $drawWidth, $drawHeight)
}

function Save-WizardBmp($Surface, [string]$Path) {
    $Surface.Graphics.Dispose()
    $Surface.Bitmap.Save($Path, [System.Drawing.Imaging.ImageFormat]::Bmp)
    $Surface.Bitmap.Dispose()
}

$logo = [System.Drawing.Image]::FromFile($logoPath)
$emblem = [System.Drawing.Image]::FromFile($emblemPath)
try {
    $side = New-Canvas 164 314
    Add-CenteredImage $side $logo 14
    Save-WizardBmp $side (Join-Path $OutputDirectory 'wizard-side.bmp')

    $small = New-Canvas 55 58
    Add-CenteredImage $small $emblem 4
    Save-WizardBmp $small (Join-Path $OutputDirectory 'wizard-small.bmp')
}
finally {
    $logo.Dispose()
    $emblem.Dispose()
}

Write-Host "Wizard artwork written to $OutputDirectory"
