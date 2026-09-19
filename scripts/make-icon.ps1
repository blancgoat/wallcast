#requires -Version 5.1
<#
  Draws Wallcast.ico: a screen filling the tile with two desktop-icon silhouettes in front of it,
  which is what the app actually does - a live picture behind your icons.

  Every size is drawn natively rather than downscaled, because the two icon silhouettes turn to mush
  below about 32px. The small sizes drop them to plain squares and thicken every edge.

  Sizes up to 64 are stored as DIB, because System.Drawing.Icon - which the app itself uses for its
  window and tray icon - cannot read PNG-compressed entries, only the shell can. 128 and 256 are PNG,
  where only the shell ever looks and the size saving is worth it.
#>
[CmdletBinding()]
param(
    [string]$OutputPath = (Join-Path $PSScriptRoot '..\Wallpaper\Wallcast.ico'),
    # Also drop each size out as a PNG, for anywhere that wants the artwork rather than an icon.
    [string]$PngDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$sizes = 16, 20, 24, 32, 48, 64, 128, 256

function New-Tile([int]$size) {
    $bitmap = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    $unit = $size / 32.0
    $pad = [Math]::Max(1.0, [Math]::Round(2 * $unit))
    $screen = New-Object System.Drawing.RectangleF $pad, $pad, ($size - 2 * $pad), ($size - 2 * $pad)
    $radius = [Math]::Max(1.0, 4 * $unit)

    # The screen: a rounded tile carrying a picture, not a grey monitor bezel.
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $radius * 2
    $path.AddArc($screen.Left, $screen.Top, $d, $d, 180, 90)
    $path.AddArc($screen.Right - $d, $screen.Top, $d, $d, 270, 90)
    $path.AddArc($screen.Right - $d, $screen.Bottom - $d, $d, $d, 0, 90)
    $path.AddArc($screen.Left, $screen.Bottom - $d, $d, $d, 90, 90)
    $path.CloseFigure()

    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        $screen, [System.Drawing.Color]::FromArgb(255, 34, 108, 214),
        [System.Drawing.Color]::FromArgb(255, 128, 58, 196), 55.0)
    $g.FillPath($brush, $path)
    $brush.Dispose()

    # A hint of scenery so it reads as a picture rather than a flat swatch. Too fine below 32px.
    if ($size -ge 32) {
        $state = $g.Save()
        $g.SetClip($path)
        $hill = New-Object System.Drawing.Drawing2D.GraphicsPath
        $hill.AddEllipse($screen.Left - 6 * $unit, $screen.Bottom - 11 * $unit, $screen.Width * 0.95, 20 * $unit)
        $wash = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(70, 255, 255, 255))
        $g.FillPath($wash, $hill)
        $wash.Dispose(); $hill.Dispose()

        $sun = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(235, 255, 214, 92))
        $g.FillEllipse($sun, $screen.Right - 10 * $unit, $screen.Top + 4.5 * $unit, 4.5 * $unit, 4.5 * $unit)
        $sun.Dispose()
        $g.Restore($state)
    }

    # The two desktop icons in front. A dark outline keeps them legible over any part of the picture.
    $tile = [Math]::Max(2.0, [Math]::Round(7 * $unit))
    $gap = [Math]::Max(1.0, [Math]::Round(3 * $unit))
    $left = $screen.Left + [Math]::Max(1.0, [Math]::Round(3 * $unit))
    $top = $screen.Top + [Math]::Max(1.0, [Math]::Round(3 * $unit))
    $face = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 252, 252, 253))
    $edge = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(110, 12, 20, 38)), ([Math]::Max(1.0, $unit))
    foreach ($row in 0, 1) {
        $y = $top + $row * ($tile + $gap)
        $corner = [Math]::Max(1.0, 1.5 * $unit)
        $box = New-Object System.Drawing.Drawing2D.GraphicsPath
        $cd = $corner * 2
        $box.AddArc($left, $y, $cd, $cd, 180, 90)
        $box.AddArc($left + $tile - $cd, $y, $cd, $cd, 270, 90)
        $box.AddArc($left + $tile - $cd, $y + $tile - $cd, $cd, $cd, 0, 90)
        $box.AddArc($left, $y + $tile - $cd, $cd, $cd, 90, 90)
        $box.CloseFigure()
        $g.FillPath($face, $box)
        $g.DrawPath($edge, $box)
        $box.Dispose()
    }
    $face.Dispose(); $edge.Dispose()

    # A rim keeps the tile from bleeding into a dark taskbar.
    $rim = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(90, 8, 14, 30)), ([Math]::Max(1.0, $unit))
    $g.DrawPath($rim, $path)
    $rim.Dispose(); $path.Dispose()

    $g.Dispose()
    return $bitmap
}

function ConvertTo-Dib([System.Drawing.Bitmap]$frame) {
    $w = $frame.Width; $h = $frame.Height
    $stream = New-Object System.IO.MemoryStream
    $writer = New-Object System.IO.BinaryWriter $stream
    $writer.Write([UInt32]40)                     # BITMAPINFOHEADER
    $writer.Write([Int32]$w)
    $writer.Write([Int32]($h * 2))                # colour rows plus the AND mask rows
    $writer.Write([UInt16]1)
    $writer.Write([UInt16]32)
    $writer.Write([UInt32]0)                      # BI_RGB
    $writer.Write([UInt32]($w * $h * 4))
    $writer.Write([Int32]0); $writer.Write([Int32]0); $writer.Write([UInt32]0); $writer.Write([UInt32]0)

    $rect = New-Object System.Drawing.Rectangle 0, 0, $w, $h
    $data = $frame.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly,
        [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $row = New-Object byte[] ($w * 4)
    try {
        for ($y = $h - 1; $y -ge 0; $y--) {      # DIB rows run bottom-up
            [System.Runtime.InteropServices.Marshal]::Copy(
                [IntPtr]::Add($data.Scan0, $y * $data.Stride), $row, 0, $row.Length)
            $writer.Write($row)
        }
    } finally { $frame.UnlockBits($data) }

    # The AND mask is unused for 32bpp icons but must still be present and padded to 4 bytes.
    $maskStride = [Math]::Floor(($w + 31) / 32) * 4
    $blank = New-Object byte[] $maskStride
    for ($y = 0; $y -lt $h; $y++) { $writer.Write($blank) }

    $writer.Flush()
    $bytes = $stream.ToArray()
    $writer.Dispose(); $stream.Dispose()
    # The leading comma stops PowerShell unrolling the array into loose bytes on the way out.
    return , $bytes
}

function Write-Icon([string]$path, [System.Collections.IEnumerable]$frames) {
    $payloads = @()
    foreach ($frame in $frames) {
        if ($frame.Width -le 64) {
            $bytes = ConvertTo-Dib $frame
        } else {
            $buffer = New-Object System.IO.MemoryStream
            $frame.Save($buffer, [System.Drawing.Imaging.ImageFormat]::Png)
            $bytes = $buffer.ToArray()
            $buffer.Dispose()
        }
        $payloads += , @{ Size = $frame.Width; Bytes = $bytes }
    }
    $stream = [System.IO.File]::Create($path)
    $writer = New-Object System.IO.BinaryWriter $stream
    $writer.Write([UInt16]0)                      # reserved
    $writer.Write([UInt16]1)                      # type: icon
    $writer.Write([UInt16]$payloads.Count)
    $offset = 6 + 16 * $payloads.Count
    foreach ($entry in $payloads) {
        $writer.Write([Byte]($(if ($entry.Size -ge 256) { 0 } else { $entry.Size })))
        $writer.Write([Byte]($(if ($entry.Size -ge 256) { 0 } else { $entry.Size })))
        $writer.Write([Byte]0)                    # palette entries
        $writer.Write([Byte]0)                    # reserved
        $writer.Write([UInt16]1)                  # colour planes
        $writer.Write([UInt16]32)                 # bits per pixel
        $writer.Write([UInt32]$entry.Bytes.Length)
        $writer.Write([UInt32]$offset)
        $offset += $entry.Bytes.Length
    }
    foreach ($entry in $payloads) { $writer.Write([byte[]]$entry.Bytes) }
    $writer.Flush(); $writer.Dispose(); $stream.Dispose()
}

$frames = @()
foreach ($size in $sizes) { $frames += , (New-Tile $size) }
$resolved = [System.IO.Path]::GetFullPath($OutputPath)
Write-Icon $resolved $frames
if ($PngDirectory) {
    $pngRoot = [System.IO.Path]::GetFullPath($PngDirectory)
    if (-not (Test-Path $pngRoot)) { New-Item -ItemType Directory -Force $pngRoot | Out-Null }
    foreach ($frame in $frames) {
        $frame.Save((Join-Path $pngRoot "wallcast-$($frame.Width).png"), [System.Drawing.Imaging.ImageFormat]::Png)
    }
    Write-Output "wrote $pngRoot\wallcast-*.png"
}
foreach ($frame in $frames) { $frame.Dispose() }
Write-Output "wrote $resolved ($($sizes -join ', '))"
