# Renders the MixDeck logo: three mixer faders (tracks with caps at different heights)
# on a rounded teal tile. Single source of the logo: writes assets\MixDeck.ico (exe, window,
# tray, installer) and assets\logo.png (in-app image). Run after changing the drawing.
param([string]$Assets = "$PSScriptRoot\..\assets")

Add-Type -AssemblyName System.Drawing

function New-Logo([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'

    function RoundRect([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
        $p = New-Object System.Drawing.Drawing2D.GraphicsPath
        $d = [Math]::Min($r * 2, [Math]::Min($w, $h))
        $p.AddArc($x, $y, $d, $d, 180, 90)
        $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
        $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
        $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
        $p.CloseFigure()
        return $p
    }

    # Tile: diagonal teal gradient
    $tile = RoundRect 0 0 ($size - 0.5) ($size - 0.5) ($size * 0.23)
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.PointF 0, 0), (New-Object System.Drawing.PointF $size, $size), ([System.Drawing.Color]::FromArgb(0x2D, 0xD4, 0xBF)), ([System.Drawing.Color]::FromArgb(0x0F, 0x76, 0x6E))
    $g.FillPath($grad, $tile)

    # Three fader tracks with caps; small sizes get thicker strokes so they stay readable
    $small = $size -le 24
    $trackW = [Math]::Max(1.0, $size * ($(if ($small) { 0.09 } else { 0.06 })))
    $capW = $size * ($(if ($small) { 0.24 } else { 0.2 }))
    $capH = [Math]::Max(2.0, $size * ($(if ($small) { 0.16 } else { 0.11 })))
    $top = $size * 0.2; $bottom = $size * 0.8
    $track = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(110, 255, 255, 255))
    $cap = [System.Drawing.Brushes]::White
    $xs = 0.27, 0.5, 0.73
    $levels = 0.66, 0.34, 0.52   # cap centres, top → bottom
    for ($i = 0; $i -lt 3; $i++) {
        $cx = $size * $xs[$i]
        $trackPath = RoundRect ($cx - $trackW / 2) $top $trackW ($bottom - $top) ($trackW / 2)
        $g.FillPath($track, $trackPath)
        $cy = $top + ($bottom - $top) * $levels[$i]
        $capPath = RoundRect ($cx - $capW / 2) ($cy - $capH / 2) $capW $capH ($capH * 0.35)
        $g.FillPath($cap, $capPath)
    }
    $g.Dispose()
    return $bmp
}

New-Item -ItemType Directory -Force $Assets | Out-Null
$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$frames = foreach ($s in $sizes) {
    $bmp = New-Logo $s
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    [pscustomobject]@{ Size = $s; Png = $ms.ToArray() }
}
[System.IO.File]::WriteAllBytes("$Assets\logo.png", ($frames | Where-Object Size -eq 256).Png)

$fs = [System.IO.File]::Create("$Assets\MixDeck.ico")
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$frames.Count)
$offset = 6 + 16 * $frames.Count
foreach ($f in $frames) {
    $dim = if ($f.Size -ge 256) { 0 } else { $f.Size }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]$f.Png.Length); $bw.Write([uint32]$offset)
    $offset += $f.Png.Length
}
foreach ($f in $frames) { $bw.Write($f.Png) }
$bw.Close()

# Preview sheet (all sizes on the app background) for checking legibility
$sheet = New-Object System.Drawing.Bitmap 760, 300
$g = [System.Drawing.Graphics]::FromImage($sheet)
$g.Clear([System.Drawing.Color]::FromArgb(0x0E, 0x10, 0x14))
$x = 16
foreach ($f in $frames) {
    $img = [System.Drawing.Image]::FromStream((New-Object System.IO.MemoryStream (, $f.Png)))
    $g.DrawImage($img, $x, 280 - $f.Size, $f.Size, $f.Size)
    $x += $f.Size + 14
}
$g.Dispose()
$sheet.Save("$Assets\..\build\logo-preview.png")
Write-Host "Wrote MixDeck.ico, logo.png and build\logo-preview.png"
