param(
    [string]$OutDir = (Join-Path $PSScriptRoot '..\src\ShortcutManager')
)

Add-Type -AssemblyName System.Drawing

# 生成一个简单的应用图标：圆角蓝色方块 + 白色快捷方式箭头
function New-IconBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    $pad = [double]$size * 0.06
    $rect = New-Object System.Drawing.RectangleF($pad, $pad, ($size - 2 * $pad), ($size - 2 * $pad))
    $radius = [double]$size * 0.22
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $radius * 2
    $path.AddArc($rect.X, $rect.Y, $d, $d, 180, 90)
    $path.AddArc($rect.Right - $d, $rect.Y, $d, $d, 270, 90)
    $path.AddArc($rect.Right - $d, $rect.Bottom - $d, $d, $d, 0, 90)
    $path.AddArc($rect.X, $rect.Bottom - $d, $d, $d, 90, 90)
    $path.CloseFigure()

    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        $rect,
        [System.Drawing.Color]::FromArgb(255, 58, 132, 255),
        [System.Drawing.Color]::FromArgb(255, 30, 86, 200),
        45.0)
    $g.FillPath($brush, $path)

    # 白色箭头（快捷方式符号）
    $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::White, ([float]($size * 0.10)))
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    $ax = [float]($size * 0.34); $ay = [float]($size * 0.66)
    $bx = [float]($size * 0.68); $by = [float]($size * 0.30)
    $g.DrawLine($pen, $ax, $ay, $bx, $by)
    $head = New-Object System.Drawing.Drawing2D.GraphicsPath
    $s = [float]($size * 0.20)
    $head.AddPolygon(@(
        (New-Object System.Drawing.PointF($bx, $by)),
        (New-Object System.Drawing.PointF(($bx - $s), $by)),
        (New-Object System.Drawing.PointF($bx, ($by + $s)))
    ))
    $g.FillPath([System.Drawing.Brushes]::White, $head)

    $g.Dispose()
    return $bmp
}

$sizes = @(16, 24, 32, 48, 64, 128, 256)
$bitmaps = @{}
foreach ($s in $sizes) { $bitmaps[$s] = New-IconBitmap $s }

$ms = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter($ms)
$bw.Write([UInt16]0)              # reserved
$bw.Write([UInt16]1)              # type = icon
$bw.Write([UInt16]$sizes.Count)   # count

$pngData = @{}
foreach ($s in $sizes) {
    $m = New-Object System.IO.MemoryStream
    $bitmaps[$s].Save($m, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngData[$s] = $m.ToArray()
    $m.Dispose()
}

$offset = 6 + 16 * $sizes.Count
foreach ($s in $sizes) {
    $data = $pngData[$s]
    $dim = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([Byte]$dim)
    $bw.Write([Byte]$dim)
    $bw.Write([Byte]0)
    $bw.Write([Byte]0)
    $bw.Write([UInt16]1)
    $bw.Write([UInt16]32)
    $bw.Write([UInt32]$data.Length)
    $bw.Write([UInt32]$offset)
    $offset += $data.Length
}
foreach ($s in $sizes) { $bw.Write($pngData[$s]) }
$bw.Flush()

$out = Join-Path $OutDir 'app.ico'
[System.IO.File]::WriteAllBytes($out, $ms.ToArray())
$bw.Dispose(); $ms.Dispose()
foreach ($s in $sizes) { $bitmaps[$s].Dispose() }

Write-Host "icon written: $out ($((Get-Item $out).Length) bytes)"
