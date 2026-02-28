# make_icon.ps1 — generates Resources\app.ico (16x16 + 32x32) for BackgroundSlideShow
# Run once from the project root: powershell -ExecutionPolicy Bypass -File docs\make_icon.ps1

param(
    [string]$OutPath = "Resources\app.ico"
)

Add-Type -AssemblyName System.Drawing

$projectRoot = Split-Path -Parent $PSScriptRoot
$icoPath     = Join-Path $projectRoot $OutPath
$resDir      = Split-Path -Parent $icoPath

if (-not (Test-Path $resDir)) {
    New-Item -ItemType Directory -Path $resDir | Out-Null
}

# ── Draw a simple icon: dark-blue background + white "S" glyph ───────────────

function Make-Bitmap([int]$size) {
    $bmp  = New-Object System.Drawing.Bitmap($size, $size)
    $g    = [System.Drawing.Graphics]::FromImage($bmp)

    $g.SmoothingMode   = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit

    # Background: rounded rect, Windows-blue
    $bgBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 0, 103, 192))
    $g.FillRectangle($bgBrush, 0, 0, $size, $size)

    # Foreground: white "S" centered
    $font     = New-Object System.Drawing.Font("Segoe UI", ([int]($size * 0.58)),
                    [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
    $fgBrush  = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)
    $sf       = New-Object System.Drawing.StringFormat
    $sf.Alignment     = [System.Drawing.StringAlignment]::Center
    $sf.LineAlignment = [System.Drawing.StringAlignment]::Center
    $rect = New-Object System.Drawing.RectangleF(0, 0, $size, $size)
    $g.DrawString("S", $font, $fgBrush, $rect, $sf)

    $g.Dispose()
    return $bmp
}

# Build multi-size ICO manually (ICO format: ICONDIR + ICONDIRENTRYs + image data)
function Write-Ico([System.Drawing.Bitmap[]]$bitmaps, [string]$path) {
    $imageStreams = @()
    foreach ($bmp in $bitmaps) {
        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $imageStreams += $ms
    }

    $count   = $bitmaps.Length
    $dataOff = 6 + $count * 16   # ICONDIR(6) + N * ICONDIRENTRY(16)

    $out = New-Object System.IO.MemoryStream
    $w   = New-Object System.IO.BinaryWriter($out)

    # ICONDIR
    $w.Write([uint16]0)      # reserved
    $w.Write([uint16]1)      # type = ICO
    $w.Write([uint16]$count)

    # ICONDIRENTRY array
    $offset = $dataOff
    for ($i = 0; $i -lt $count; $i++) {
        $sz   = [int]$bitmaps[$i].Width
        $data = $imageStreams[$i].ToArray()
        $w.Write([byte]($sz -band 0xFF))   # width  (0 = 256)
        $w.Write([byte]($sz -band 0xFF))   # height (0 = 256)
        $w.Write([byte]0)                  # color count
        $w.Write([byte]0)                  # reserved
        $w.Write([uint16]1)                # planes
        $w.Write([uint16]32)               # bit count
        $w.Write([uint32]$data.Length)     # size of image data
        $w.Write([uint32]$offset)          # offset to image data
        $offset += $data.Length
    }

    # Image data
    foreach ($ms in $imageStreams) {
        $w.Write($ms.ToArray())
    }

    $w.Flush()
    [System.IO.File]::WriteAllBytes($path, $out.ToArray())
    $w.Dispose()
    foreach ($ms in $imageStreams) { $ms.Dispose() }
}

$b16 = Make-Bitmap 16
$b32 = Make-Bitmap 32
$b48 = Make-Bitmap 48

Write-Ico @($b16, $b32, $b48) $icoPath

$b16.Dispose(); $b32.Dispose(); $b48.Dispose()

Write-Host "Icon written to: $icoPath"
