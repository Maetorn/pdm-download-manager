# Draws the PDM icons (no external images): app .ico files and extension PNGs.
# Run with Windows PowerShell 5.1 (System.Drawing is built in).
param([string]$Root = (Split-Path -Parent $PSScriptRoot))

Add-Type -AssemblyName System.Drawing

function New-IconBitmap([int]$size, [string]$c1, [string]$c2) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)

    $pad = [Math]::Max(0.5, $size * 0.04)
    $r = $size * 0.22
    $w = $size - 2 * $pad
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = 2 * $r
    $path.AddArc($pad, $pad, $d, $d, 180, 90)
    $path.AddArc($pad + $w - $d, $pad, $d, $d, 270, 90)
    $path.AddArc($pad + $w - $d, $pad + $w - $d, $d, $d, 0, 90)
    $path.AddArc($pad, $pad + $w - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    $rect = New-Object System.Drawing.RectangleF 0, 0, $size, $size
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, ([System.Drawing.ColorTranslator]::FromHtml($c1)), ([System.Drawing.ColorTranslator]::FromHtml($c2)), 90
    $g.FillPath($brush, $path)

    # Arrow: shaft + head, and a base line.
    $white = [System.Drawing.Brushes]::White
    $cx = $size / 2
    $shaftW = $size * 0.16
    $top = $size * 0.20
    $headTop = $size * 0.44
    $headBottom = $size * 0.66
    $headHalf = $size * 0.25
    $g.FillRectangle($white, [float]($cx - $shaftW / 2), [float]$top, [float]$shaftW, [float]($headTop - $top + 1))
    $pts = @(
        (New-Object System.Drawing.PointF ([float]($cx - $headHalf)), ([float]$headTop)),
        (New-Object System.Drawing.PointF ([float]($cx + $headHalf)), ([float]$headTop)),
        (New-Object System.Drawing.PointF ([float]$cx), ([float]$headBottom))
    )
    $g.FillPolygon($white, $pts)
    $barH = [Math]::Max(1.5, $size * 0.075)
    $barW = $size * 0.52
    $g.FillRectangle($white, [float]($cx - $barW / 2), [float]($size * 0.74), [float]$barW, [float]$barH)
    $g.Dispose()
    return $bmp
}

function Save-Ico([string]$file, [string]$c1, [string]$c2) {
    $sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
    $pngs = @()
    foreach ($s in $sizes) {
        $bmp = New-IconBitmap $s $c1 $c2
        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $pngs += , $ms.ToArray()
        $bmp.Dispose()
    }
    $out = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter $out
    $bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $s = $sizes[$i]
        $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
        $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
        $bw.Write([byte]0); $bw.Write([byte]0)
        $bw.Write([UInt16]1); $bw.Write([UInt16]32)
        $bw.Write([UInt32]$pngs[$i].Length); $bw.Write([UInt32]$offset)
        $offset += $pngs[$i].Length
    }
    foreach ($p in $pngs) { $bw.Write($p) }
    $bw.Flush()
    [System.IO.File]::WriteAllBytes($file, $out.ToArray())
}

$assets = Join-Path $Root "src\PDM\Assets"
New-Item -ItemType Directory -Force $assets | Out-Null
Save-Ico (Join-Path $assets "pdm.ico") "#3B82F6" "#1D4ED8"
Save-Ico (Join-Path $assets "pdm-active.ico") "#22C55E" "#15803D"

$extIcons = Join-Path $Root "extension\opera\icons"
New-Item -ItemType Directory -Force $extIcons | Out-Null
foreach ($s in 16, 32, 48, 128) {
    $bmp = New-IconBitmap $s "#3B82F6" "#1D4ED8"
    $bmp.Save((Join-Path $extIcons "icon$s.png"), [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    $bmp = New-IconBitmap $s "#9CA3AF" "#6B7280"
    $bmp.Save((Join-Path $extIcons "icon$s-off.png"), [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
}
Write-Output "icons written"
