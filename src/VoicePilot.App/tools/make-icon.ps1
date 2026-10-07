# Draws the app icon (Assets\app.ico): cyan→violet rounded square with white sound-wave bars.
# Usage: pwsh -File tools\make-icon.ps1
Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'
$outDir = Join-Path $PSScriptRoot '..\Assets'

function New-RoundedPath([System.Drawing.RectangleF]$r, [float]$rad) {
    $p = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $d = [math]::Max(1, $rad * 2)
    $p.AddArc($r.X, $r.Y, $d, $d, 180, 90)
    $p.AddArc($r.Right - $d, $r.Y, $d, $d, 270, 90)
    $p.AddArc($r.Right - $d, $r.Bottom - $d, $d, $d, 0, 90)
    $p.AddArc($r.X, $r.Bottom - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function New-Frame([int]$s) {
    $bmp = [System.Drawing.Bitmap]::new($s, $s, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    $pad = [math]::Max(0.5, $s * 0.04)
    $rect = [System.Drawing.RectangleF]::new($pad, $pad, $s - 2 * $pad, $s - 2 * $pad)
    $bg = New-RoundedPath $rect ($s * 0.25)
    $grad = [System.Drawing.Drawing2D.LinearGradientBrush]::new($rect,
        [System.Drawing.Color]::FromArgb(255, 34, 211, 238),
        [System.Drawing.Color]::FromArgb(255, 124, 92, 255), 45.0)
    $g.FillPath($grad, $bg)

    $hi = [System.Drawing.RectangleF]::new($rect.X, $rect.Y, $rect.Width, $rect.Height * 0.55)
    $hiBrush = [System.Drawing.Drawing2D.LinearGradientBrush]::new($hi,
        [System.Drawing.Color]::FromArgb(55, 255, 255, 255),
        [System.Drawing.Color]::FromArgb(0, 255, 255, 255), 90.0)
    $g.SetClip($bg)
    $g.FillRectangle($hiBrush, $hi)
    $g.ResetClip()

    # Five rounded bars, tallest in the middle.
    $heights = 0.26, 0.46, 0.62, 0.46, 0.26
    $barW = $s * 0.088
    $gap = $s * 0.052
    $total = 5 * $barW + 4 * $gap
    $x = ($s - $total) / 2
    foreach ($h in $heights) {
        $bh = $s * $h
        $r = [System.Drawing.RectangleF]::new($x, ($s - $bh) / 2, $barW, $bh)
        $path = New-RoundedPath $r ($barW / 2)
        $g.FillPath([System.Drawing.Brushes]::White, $path)
        $x += $barW + $gap
    }

    $g.Dispose()
    $ms = [System.IO.MemoryStream]::new()
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return , $ms.ToArray()
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$frames = foreach ($s in $sizes) { , (New-Frame $s) }

New-Item -ItemType Directory -Force $outDir | Out-Null
$out = (Resolve-Path $outDir).Path + '\app.ico'
$fs = [System.IO.File]::Create($out)
$w = [System.IO.BinaryWriter]::new($fs)
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $data = $frames[$i]
    $w.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
    $w.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
    $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$data.Length); $w.Write([uint32]$offset)
    $offset += $data.Length
}
foreach ($data in $frames) { $w.Write($data) }
$w.Dispose()

# PNG copy for the web UI.
[System.IO.File]::WriteAllBytes((Join-Path (Resolve-Path (Join-Path $PSScriptRoot '..\web')).Path 'icon.png'), $frames[7])
Write-Host "Written: $out"
