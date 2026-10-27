<#
.SYNOPSIS
  Renders the Allycord mark (handheld silhouette whose screen carries a voice waveform)
  and writes every package asset size from one 512px master.
#>
param([string]$AssetsDir = (Join-Path $PSScriptRoot '..\src\Allycord.Widget\Assets'))

Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'

function New-RoundRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = 2 * $r
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function Get-Master {
    $size = 512
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)

    $ink = [System.Drawing.Color]::FromArgb(255, 11, 11, 11)
    $neon = [System.Drawing.Color]::FromArgb(255, 155, 240, 11)

    # Body: a handheld with rounded grips, Xbox green.
    $body = New-RoundRect 16 140 480 232 116
    $rect = New-Object System.Drawing.RectangleF 16, 140, 480, 232
    $green = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, ([System.Drawing.Color]::FromArgb(255, 52, 196, 52)), ([System.Drawing.Color]::FromArgb(255, 12, 100, 12)), 60.0
    $g.FillPath($green, $body)

    # Screen.
    $screen = New-RoundRect 148 170 216 172 30
    $g.FillPath((New-Object System.Drawing.SolidBrush $ink), $screen)

    # Voice waveform on the screen (the "Discord" half of the idea, without Discord's mark).
    $bars = @(@(196, 46), @(234, 96), @(272, 66), @(310, 112))
    $barW = 24
    foreach ($b in $bars) {
        $h = $b[1]; $cx = $b[0]
        $path = New-RoundRect ($cx - $barW / 2) (256 - $h / 2) $barW $h ($barW / 2)
        $g.FillPath((New-Object System.Drawing.SolidBrush $neon), $path)
    }

    # Left stick and right face buttons.
    $inkBrush = New-Object System.Drawing.SolidBrush $ink
    $g.FillEllipse($inkBrush, 62, 196, 64, 64)
    $g.FillEllipse((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 30, 30, 30))), 76, 210, 36, 36)
    foreach ($pt in @(@(420, 214), @(448, 242), @(392, 242), @(420, 270))) {
        $g.FillEllipse($inkBrush, $pt[0] - 11, $pt[1] - 11, 22, 22)
    }
    $g.Dispose()
    return $bmp
}

# Compact variant for icon sizes (<= 64px): the handheld's screen on a green tile. The wide
# silhouette is unreadable at 24px; this keeps the same screen + waveform motif.
function Get-Compact {
    $size = 512
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)

    $tile = New-RoundRect 16 16 480 480 128
    $rect = New-Object System.Drawing.RectangleF 16, 16, 480, 480
    $green = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, ([System.Drawing.Color]::FromArgb(255, 52, 196, 52)), ([System.Drawing.Color]::FromArgb(255, 12, 100, 12)), 60.0
    $g.FillPath($green, $tile)

    $screen = New-RoundRect 96 128 320 256 56
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 11, 11, 11))), $screen)

    $neon = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 155, 240, 11))
    $barW = 40
    foreach ($b in @(@(176, 72), @(232, 152), @(288, 104), @(344, 176))) {
        $path = New-RoundRect ($b[0] - $barW / 2) (256 - $b[1] / 2) $barW $b[1] ($barW / 2)
        $g.FillPath($neon, $path)
    }
    $g.Dispose()
    return $bmp
}

function Save-Scaled($master, [string]$name, [int]$w, [int]$h, [float]$fill = 0.92) {
    $out = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($out)
    $g.InterpolationMode = 'HighQualityBicubic'
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)
    $side = [Math]::Min($w, $h) * $fill
    $g.DrawImage($master, ($w - $side) / 2, ($h - $side) / 2, $side, $side)
    $g.Dispose()
    $out.Save((Join-Path $AssetsDir $name), [System.Drawing.Imaging.ImageFormat]::Png)
    $out.Dispose()
}

$master = Get-Master
$compact = Get-Compact
Save-Scaled $compact 'Square44x44Logo.png' 44 44 1.0
Save-Scaled $compact 'Square44x44Logo.targetsize-24_altform-unplated.png' 24 24 1.0
Save-Scaled $compact 'StoreLogo.png' 50 50 1.0
Save-Scaled $compact 'GameBarIcon.png' 64 64 1.0
Save-Scaled $compact 'LogoMark.png' 128 128 1.0
Save-Scaled $master 'Square150x150Logo.png' 150 150 0.8
Save-Scaled $master 'Wide310x150Logo.png' 310 150 0.8
Save-Scaled $master 'SplashScreen.png' 620 300 0.8
$docs = Join-Path $AssetsDir '..\..\..\docs'
$master.Save((Join-Path $docs 'logo.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$compact.Save((Join-Path $docs 'logo-icon.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$master.Dispose()
$compact.Dispose()
Write-Host 'Logo assets written.'
