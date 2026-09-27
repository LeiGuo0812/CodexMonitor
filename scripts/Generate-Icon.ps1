$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$assetDir = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\src\CodexQuotaMonitor.App\Assets'))
# Geometry mirrors cqm.svg; supersampling keeps tiny tray sizes clean, with a full alpha channel.
$sizes = @(16, 20, 24, 28, 32, 40, 48, 64, 128, 256)
$frames = [Collections.Generic.List[byte[]]]::new()
foreach ($size in $sizes) {
    $large = [Drawing.Bitmap]::new($size * 4, $size * 4)
    $canvas = [Drawing.Graphics]::FromImage($large)
    $canvas.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $canvas.Clear([Drawing.Color]::Transparent)
    $canvas.ScaleTransform($size / 4.0, $size / 4.0)
    $shape = [Drawing.Drawing2D.GraphicsPath]::new()
    $shape.AddArc([single]0.6,[single]0.6,[single]8,[single]8,[single]180,[single]90)
    $shape.AddArc([single]7.4,[single]0.6,[single]8,[single]8,[single]270,[single]90)
    $shape.AddArc([single]7.4,[single]7.4,[single]8,[single]8,[single]0,[single]90)
    $shape.AddArc([single]0.6,[single]7.4,[single]8,[single]8,[single]90,[single]90)
    $shape.CloseFigure()
    $tile = [Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml('#087F78'))
    $outline = [Drawing.Pen]::new([Drawing.ColorTranslator]::FromHtml('#075D58'), [single]0.5)
    $ink = [Drawing.Pen]::new([Drawing.Color]::White, [single]1.9)
    $ink.StartCap = $ink.EndCap = [Drawing.Drawing2D.LineCap]::Round
    $canvas.FillPath($tile, $shape)
    $canvas.DrawPath($outline, $shape)
    # Open quota ring centered at 7.6, 7.6, radius 4; short diagonal completes a Q silhouette.
    $canvas.DrawArc($ink, [single]3.6,[single]3.6,[single]8,[single]8,[single]55,[single]325)
    $canvas.DrawLine($ink,[single]9.25,[single]9.25,[single]11.95,[single]11.95)
    $bitmap = [Drawing.Bitmap]::new($size, $size)
    $output = [Drawing.Graphics]::FromImage($bitmap)
    $output.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $output.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $output.DrawImage($large, 0, 0, $size, $size)
    $memory = [IO.MemoryStream]::new()
    $bitmap.Save($memory, [Drawing.Imaging.ImageFormat]::Png)
    $frames.Add($memory.ToArray())
    if ($size -eq 64) { $bitmap.Save((Join-Path $assetDir 'cqm-preview.png'), [Drawing.Imaging.ImageFormat]::Png) }
    $memory.Dispose(); $output.Dispose(); $bitmap.Dispose()
    $ink.Dispose(); $outline.Dispose(); $tile.Dispose(); $shape.Dispose(); $canvas.Dispose(); $large.Dispose()
}
$file = [IO.File]::Create((Join-Path $assetDir 'cqm.ico'))
$writer = [IO.BinaryWriter]::new($file)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($index = 0; $index -lt $sizes.Count; $index++) {
        $dimension = if ($sizes[$index] -eq 256) { 0 } else { $sizes[$index] }
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
        $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$frames[$index].Length); $writer.Write([uint32]$offset)
        $offset += $frames[$index].Length
    }
    foreach ($frame in $frames) { $writer.Write($frame) }
} finally { $writer.Dispose(); $file.Dispose() }
Write-Output ('Created alpha ICO with sizes: ' + ($sizes -join ', '))
