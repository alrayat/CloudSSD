param([string]$Output = (Join-Path $PSScriptRoot '..\src\CloudSSD.Desktop\Assets\cloudssd.ico'))
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$sizes = @(16, 24, 32, 48, 64, 128, 256)
$images = @()
foreach ($size in $sizes) {
    $bitmap = [Drawing.Bitmap]::new($size, $size)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.Clear([Drawing.Color]::Transparent)
    $graphics.ScaleTransform($size / 64.0, $size / 64.0)
    $background = [Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml('#202C27'))
    $graphics.FillRectangle($background, 0, 0, 64, 64)
    $polygons = @(
        @{ Color='#D3E6A2'; Points=@(12,18, 32,8, 52,18, 32,28) },
        @{ Color='#A4C67C'; Points=@(12,27, 32,37, 52,27, 52,34, 32,44, 12,34) },
        @{ Color='#F0F3E9'; Points=@(12,41, 32,51, 52,41, 52,48, 32,58, 12,48) }
    )
    foreach ($polygon in $polygons) {
        $points = for ($i=0; $i -lt $polygon.Points.Count; $i+=2) { [Drawing.PointF]::new($polygon.Points[$i], $polygon.Points[$i+1]) }
        $brush = [Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml($polygon.Color))
        $graphics.FillPolygon($brush, [Drawing.PointF[]]$points)
        $brush.Dispose()
    }
    $stream = [IO.MemoryStream]::new()
    $bitmap.Save($stream, [Drawing.Imaging.ImageFormat]::Png)
    $images += ,$stream.ToArray()
    $stream.Dispose(); $background.Dispose(); $graphics.Dispose(); $bitmap.Dispose()
}
$file = [IO.File]::Create([IO.Path]::GetFullPath($Output))
$writer = [IO.BinaryWriter]::new($file)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
    $offset = 6 + $sizes.Count * 16
    for ($i=0; $i -lt $sizes.Count; $i++) {
        $dimension = if ($sizes[$i] -eq 256) { 0 } else { $sizes[$i] }
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension); $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$images[$i].Length); $writer.Write([uint32]$offset)
        $offset += $images[$i].Length
    }
    foreach ($bytes in $images) { $writer.Write([byte[]]$bytes) }
} finally { $writer.Dispose(); $file.Dispose() }
Write-Host "Generated CloudSSD icon: $Output"
