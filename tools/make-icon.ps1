<#
.SYNOPSIS
  Draws the VR Mod Loader icon in code (no image files needed) and writes a multi-size .ico.
  Sizes <= 64 px are stored as classic 32-bit DIBs (what csc / Explorer / the tray like best);
  128 and 256 px are stored PNG-compressed. Also writes a 256 px PNG preview next to it.
#>
param(
    [string]$Out = (Join-Path (Split-Path $PSScriptRoot -Parent) "assets\app.ico")
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function C([int]$a, [int]$r, [int]$g, [int]$b) { [System.Drawing.Color]::FromArgb($a, $r, $g, $b) }

function New-RoundRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function Draw-Icon([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap($s, $s, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)
    $f = [float]$s

    # --- dark rounded tile with a diagonal gradient and a faint cyan rim
    $tile = New-RoundRect 0.5 0.5 ($f - 1) ($f - 1) ($f * 0.22)
    $lg = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.PointF(0, 0)), (New-Object System.Drawing.PointF($f, $f)),
        (C 255 22 32 46), (C 255 6 9 14))
    $g.FillPath($lg, $tile)
    $rim = New-Object System.Drawing.Pen((C 110 25 211 245), [Math]::Max(1.0, $f * 0.02))
    $g.DrawPath($rim, $tile)

    # --- magenta head strap, drawn first so the visor sits in front of it
    $strap = New-Object System.Drawing.Pen((C 235 255 61 166), [Math]::Max(1.0, $f * 0.075))
    $strap.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $strap.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $g.DrawArc($strap, $f * 0.17, $f * 0.20, $f * 0.66, $f * 0.62, 200, 140)

    # --- visor body
    $vx = $f * 0.13; $vy = $f * 0.37; $vw = $f * 0.74; $vh = $f * 0.36; $vr = $f * 0.13
    $visor = New-RoundRect $vx $vy $vw $vh $vr
    $vyBottom = $vy + $vh
    $vb = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.PointF(0, $vy)), (New-Object System.Drawing.PointF(0, $vyBottom)),
        (C 255 32 44 62), (C 255 13 19 28))
    $g.FillPath($vb, $visor)
    $vpen = New-Object System.Drawing.Pen((C 255 25 211 245), [Math]::Max(1.0, $f * 0.045))
    $g.DrawPath($vpen, $visor)

    # --- two glowing lenses
    $lensX = @(($f * 0.355), ($f * 0.645))
    foreach ($cx in $lensX) {
        $cy = $f * 0.55
        for ($i = 4; $i -ge 1; $i--) {
            $rr = $f * 0.065 + $i * $f * 0.028
            $gb = New-Object System.Drawing.SolidBrush((C (16 * $i) 25 211 245))
            $g.FillEllipse($gb, $cx - $rr, $cy - $rr, $rr * 2, $rr * 2)
        }
        $rr = $f * 0.082
        $g.FillEllipse((New-Object System.Drawing.SolidBrush((C 255 110 232 255))), $cx - $rr, $cy - $rr, $rr * 2, $rr * 2)
        $rr = $f * 0.036
        $g.FillEllipse((New-Object System.Drawing.SolidBrush((C 255 242 252 255))), $cx - $rr * 1.4, $cy - $rr * 1.4, $rr * 2, $rr * 2)
    }

    # --- nose notch cut into the bottom edge of the visor
    $nb = New-Object System.Drawing.SolidBrush((C 255 9 13 19))
    $g.FillEllipse($nb, $f * 0.425, $f * 0.63, $f * 0.15, $f * 0.17)
    $g.DrawArc($vpen, $f * 0.425, $f * 0.63, $f * 0.15, $f * 0.17, 180, 180)

    $g.Dispose()
    return $bmp
}

# Classic ICO image: BITMAPINFOHEADER + bottom-up BGRA pixels + empty AND mask.
function Get-Dib([System.Drawing.Bitmap]$bmp) {
    $s = $bmp.Width
    $rect = New-Object System.Drawing.Rectangle(0, 0, $s, $s)
    $data = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $stride = $data.Stride
    $bytes = New-Object byte[] ($stride * $s)
    [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $bytes, 0, $bytes.Length)
    $bmp.UnlockBits($data)
    $maskRow = [int](((($s + 31) -band (-bnot 31))) / 8)
    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($ms)
    $bw.Write([uint32]40); $bw.Write([int32]$s); $bw.Write([int32]($s * 2))
    $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]0)
    $bw.Write([uint32]($s * $s * 4 + $maskRow * $s))
    $bw.Write([int32]0); $bw.Write([int32]0); $bw.Write([uint32]0); $bw.Write([uint32]0)
    for ($y = $s - 1; $y -ge 0; $y--) { $bw.Write($bytes, $y * $stride, $s * 4) }
    $bw.Write((New-Object byte[] ($maskRow * $s)))
    $bw.Flush()
    return $ms.ToArray()
}

function Get-Png([System.Drawing.Bitmap]$bmp) {
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    return $ms.ToArray()
}

$dir = Split-Path $Out -Parent
if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$images = @()
foreach ($s in $sizes) {
    $bmp = Draw-Icon $s
    if ($s -le 64) { $images += , (Get-Dib $bmp) } else { $images += , (Get-Png $bmp) }
    if ($s -eq 256) { $bmp.Save((Join-Path (Split-Path $Out -Parent) "app-256.png"), [System.Drawing.Imaging.ImageFormat]::Png) }
    $bmp.Dispose()
}

$fs = [System.IO.File]::Create($Out)
$bw = New-Object System.IO.BinaryWriter($fs)
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]
    $b = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([byte]$b); $bw.Write([byte]$b); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]([byte[]]$images[$i]).Length); $bw.Write([uint32]$offset)
    $offset += $images[$i].Length
}
foreach ($img in $images) { $bw.Write([byte[]]$img) }
$bw.Flush(); $fs.Close()
Write-Host ("Icon written: {0} ({1} sizes, {2:N0} bytes)" -f $Out, $sizes.Count, (Get-Item $Out).Length)

