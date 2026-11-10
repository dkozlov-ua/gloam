<#
.SYNOPSIS
    Draws Gloam's icons: src\Gloam\app.ico, app-media.ico, app-sdr.ico and app_icon.png.

.DESCRIPTION
    The icon is a disc, washed-out grey on one side and true black on the other: what SDR
    content looks like in Windows HDR mode, and what Gloam's gamma correction gives back.

    app.ico is the Normal profile's icon, in colour: the app, its window and the setup use
    it. The tray icon switches with the profile: app-media.ico adds a play badge for Media,
    and app-sdr.ico is the same mark in grey for SDR, which turns HDR off. The badge and the
    missing colour are what still tell the three apart at 16 px.

    Every size is drawn from the same geometry rather than scaled from one bitmap, so the
    small tray sizes stay sharp. Each .ico holds 16-64 px as 32-bit DIBs, which every
    consumer (the shell, WPF, System.Drawing for the tray icon) reads, and 256 px as PNG.
    app_icon.png is the Normal icon at 512 px, for pages that want a large one.

    Runs in Windows PowerShell 5.1 or PowerShell 7 on Windows; needs no other tools.

.PARAMETER PreviewPath
    Also writes a contact sheet of every size of the three icons on dark and light
    backgrounds, to check the small sizes by eye.
#>
[CmdletBinding()]
param(
    [string]$PreviewPath
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, WindowsBase

$Root = Split-Path -Parent $PSScriptRoot
$IcoPath = Join-Path $Root 'src\Gloam\app.ico'
$MediaIcoPath = Join-Path $Root 'src\Gloam\app-media.ico'
$SdrIcoPath = Join-Path $Root 'src\Gloam\app-sdr.ico'
$PngPath = Join-Path $Root 'src\Gloam\app_icon.png'
$IcoSizes = 16, 20, 24, 32, 40, 48, 64, 256
$PngSize = 512
$Variants = 'Normal', 'Media', 'Sdr'

# One colour, a magenta-violet, for the HDR profiles; SDR is its grey. The tile gradient
# runs at 60 degrees from the top-left.
$Palettes = @{
    Normal = @{ Tile = @('#FFE0479E', '#FF6A3DF0'); Ring = '#FFFFFFFF' }
    Media  = @{ Tile = @('#FFE0479E', '#FF6A3DF0'); Ring = '#FFFFFFFF' }
    Sdr    = @{ Tile = @('#FF6E6E76', '#FF3E3E46'); Ring = '#FFD6D6DA' }
}
$WashedOut = '#FFBDBAC2'
$TrueBlack = '#FF06060A'

function New-Color([string]$Hex) {
    [System.Windows.Media.ColorConverter]::ConvertFromString($Hex)
}

function New-Point([double]$X, [double]$Y) { New-Object System.Windows.Point($X, $Y) }

function New-Brush([string]$Hex) {
    $brush = New-Object System.Windows.Media.SolidColorBrush((New-Color $Hex))
    $brush.Freeze()
    $brush
}

# Half of a circle, from its top to its bottom, on the left or the right.
function New-HalfDisc([System.Windows.Point]$Center, [double]$Radius, [bool]$Right) {
    $sweep = if ($Right) { 'Clockwise' } else { 'Counterclockwise' }
    $arc = New-Object System.Windows.Media.ArcSegment((New-Point $Center.X ($Center.Y + $Radius)),
        (New-Object System.Windows.Size($Radius, $Radius)), 0, $false, $sweep, $true)
    $figure = New-Object System.Windows.Media.PathFigure
    $figure.StartPoint = New-Point $Center.X ($Center.Y - $Radius)
    $figure.IsClosed = $true
    $figure.Segments.Add($arc)
    $geometry = New-Object System.Windows.Media.PathGeometry
    $geometry.Figures.Add($figure)
    $geometry.Freeze()
    $geometry
}

# Draws the icon at $Size px and returns it as straight-alpha BGRA.
function New-IconBitmap([int]$Size, [string]$Variant) {
    $palette = $Palettes[$Variant]

    # A small margin from 24 px up, like the Windows app icons; none below, where a
    # pixel of margin is a sixteenth of the icon.
    $inset = [math]::Floor($Size / 24)
    $side = [double]($Size - 2 * $inset)
    $tile = New-Object System.Windows.Rect($inset, $inset, $side, $side)
    $corner = $side * 0.22
    $tileShape = New-Object System.Windows.Media.RectangleGeometry($tile, $corner, $corner)
    $tileShape.Freeze()
    $tileBrush = New-Object System.Windows.Media.LinearGradientBrush((New-Color $palette.Tile[0]), (New-Color $palette.Tile[1]), 60.0)
    $tileBrush.Freeze()

    $visual = New-Object System.Windows.Media.DrawingVisual
    $dc = $visual.RenderOpen()
    $dc.DrawGeometry($tileBrush, $null, $tileShape)

    $center = New-Point ($tile.Left + $side / 2) ($tile.Top + $side / 2)
    $radius = $side * 0.31
    $dc.DrawGeometry((New-Brush $WashedOut), $null, (New-HalfDisc $center $radius $false))
    $dc.DrawGeometry((New-Brush $TrueBlack), $null, (New-HalfDisc $center $radius $true))
    $ring = New-Object System.Windows.Media.Pen((New-Brush $palette.Ring), [math]::Max(1.0, $side * 0.045))
    $ring.Freeze()
    $dc.DrawEllipse($null, $ring, $center, $radius, $radius)

    # Media: a play badge in the bottom-right corner, cut out of the mark by a ring of the
    # tile's colour so it stays apart from the disc at 16 px.
    if ($Variant -eq 'Media') {
        $badge = $side * 0.21
        $at = New-Point ($tile.Right - $badge - $side * 0.035) ($tile.Bottom - $badge - $side * 0.035)
        $gap = [math]::Max(1.0, $side * 0.05)
        $dc.PushClip($tileShape)
        $dc.DrawEllipse($tileBrush, $null, $at, ($badge + $gap), ($badge + $gap))
        $dc.Pop()
        $dc.DrawEllipse([System.Windows.Media.Brushes]::White, $null, $at, $badge, $badge)
        $s = $badge * 0.62
        $play = New-Object System.Windows.Media.PathFigure
        $play.StartPoint = New-Point ($at.X - $s * 0.45) ($at.Y - $s * 0.75)
        $play.IsClosed = $true
        $points = New-Object System.Windows.Media.PointCollection
        $points.Add((New-Point ($at.X + $s * 0.85) $at.Y))
        $points.Add((New-Point ($at.X - $s * 0.45) ($at.Y + $s * 0.75)))
        $play.Segments.Add((New-Object System.Windows.Media.PolyLineSegment($points, $true)))
        $triangle = New-Object System.Windows.Media.PathGeometry
        $triangle.Figures.Add($play)
        $dc.DrawGeometry((New-Brush '#FF1A1A1F'), $null, $triangle)
    }
    $dc.Close()

    $target = New-Object System.Windows.Media.Imaging.RenderTargetBitmap($Size, $Size, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
    $target.Render($visual)
    $straight = New-Object System.Windows.Media.Imaging.FormatConvertedBitmap($target, [System.Windows.Media.PixelFormats]::Bgra32, $null, 0)
    $straight.Freeze()
    $straight
}

function Get-PngBytes([System.Windows.Media.Imaging.BitmapSource]$Bitmap) {
    $encoder = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($Bitmap))
    $stream = New-Object System.IO.MemoryStream
    $encoder.Save($stream)
    $stream.ToArray()
}

# An icon image as a 32-bit DIB: BITMAPINFOHEADER (height doubled for the AND mask),
# bottom-up BGRA rows, then a 1-bit AND mask set where the pixel is fully transparent.
function Get-DibBytes([System.Windows.Media.Imaging.BitmapSource]$Bitmap) {
    $size = $Bitmap.PixelWidth
    $stride = $size * 4
    $pixels = New-Object byte[] ($stride * $size)
    $Bitmap.CopyPixels($pixels, $stride, 0)
    $maskStride = [int]([math]::Ceiling($size / 32) * 4)

    $stream = New-Object System.IO.MemoryStream
    $writer = New-Object System.IO.BinaryWriter($stream)
    $writer.Write([int]40)
    $writer.Write([int]$size)
    $writer.Write([int]($size * 2))
    $writer.Write([int16]1)
    $writer.Write([int16]32)
    $writer.Write([int]0)
    $writer.Write([int]($stride * $size + $maskStride * $size))
    $writer.Write([int]0); $writer.Write([int]0); $writer.Write([int]0); $writer.Write([int]0)
    for ($y = $size - 1; $y -ge 0; $y--) { $writer.Write($pixels, $y * $stride, $stride) }
    for ($y = $size - 1; $y -ge 0; $y--) {
        $row = New-Object byte[] $maskStride
        for ($x = 0; $x -lt $size; $x++) {
            if ($pixels[$y * $stride + $x * 4 + 3] -eq 0) { $row[$x -shr 3] = $row[$x -shr 3] -bor (0x80 -shr ($x -band 7)) }
        }
        $writer.Write($row)
    }
    $writer.Flush()
    $stream.ToArray()
}

function Write-Ico([string]$Path, [int[]]$Sizes, [string]$Variant) {
    $images = foreach ($size in $Sizes) {
        $bitmap = New-IconBitmap $size $Variant
        $bytes = if ($size -ge 256) { Get-PngBytes $bitmap } else { Get-DibBytes $bitmap }
        , @($size, $bytes)
    }
    $stream = New-Object System.IO.MemoryStream
    $writer = New-Object System.IO.BinaryWriter($stream)
    $writer.Write([int16]0)
    $writer.Write([int16]1)
    $writer.Write([int16]$images.Count)
    $offset = 6 + 16 * $images.Count
    foreach ($image in $images) {
        $size = $image[0]; $bytes = $image[1]
        $writer.Write([byte]($size % 256))
        $writer.Write([byte]($size % 256))
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([int16]1)
        $writer.Write([int16]32)
        $writer.Write([int]$bytes.Length)
        $writer.Write([int]$offset)
        $offset += $bytes.Length
    }
    foreach ($image in $images) { $writer.Write([byte[]]$image[1]) }
    $writer.Flush()
    [System.IO.File]::WriteAllBytes($Path, $stream.ToArray())
}

# One row per icon: 256 px, then every smaller size on a dark and a light strip.
function Write-Preview([string]$Path, [int[]]$Sizes) {
    $pad = 12
    $small = $Sizes | Where-Object { $_ -lt 256 } | Sort-Object -Descending
    $stripWidth = $pad
    foreach ($size in $small) { $stripWidth += $size + $pad }
    $rowHeight = 256 + 2 * $pad
    $width = 256 + 3 * $pad + $stripWidth
    $height = $Variants.Count * $rowHeight

    $visual = New-Object System.Windows.Media.DrawingVisual
    $dc = $visual.RenderOpen()
    $dc.DrawRectangle((New-Brush '#FF2B2B2B'), $null, (New-Object System.Windows.Rect(0, 0, $width, $height)))
    $y = 0
    foreach ($variant in $Variants) {
        $dc.DrawImage((New-IconBitmap 256 $variant), (New-Object System.Windows.Rect($pad, ($y + $pad), 256, 256)))
        $stripHeight = (256 - $pad) / 2
        $band = 0
        foreach ($background in '#FF1C1C1C', '#FFEEEEEE') {
            $top = $y + $pad + $band * ($stripHeight + $pad)
            $left = 256 + 2 * $pad
            $dc.DrawRectangle((New-Brush $background), $null, (New-Object System.Windows.Rect($left, $top, $stripWidth, $stripHeight)))
            $x = $left + $pad
            foreach ($size in $small) {
                $dc.DrawImage((New-IconBitmap $size $variant), (New-Object System.Windows.Rect($x, [math]::Round($top + ($stripHeight - $size) / 2), $size, $size)))
                $x += $size + $pad
            }
            $band++
        }
        $y += $rowHeight
    }
    $dc.Close()

    $target = New-Object System.Windows.Media.Imaging.RenderTargetBitmap([int]$width, [int]$height, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
    $target.Render($visual)
    [System.IO.File]::WriteAllBytes($Path, (Get-PngBytes $target))
}

Write-Ico $IcoPath $IcoSizes 'Normal'
Write-Ico $MediaIcoPath $IcoSizes 'Media'
Write-Ico $SdrIcoPath $IcoSizes 'Sdr'
[System.IO.File]::WriteAllBytes($PngPath, (Get-PngBytes (New-IconBitmap $PngSize 'Normal')))
Write-Host "Wrote $IcoPath, $MediaIcoPath and $SdrIcoPath ($($IcoSizes -join ', ') px), and $PngPath ($PngSize px)."
if ($PreviewPath) {
    Write-Preview $PreviewPath $IcoSizes
    Write-Host "Wrote $PreviewPath."
}
