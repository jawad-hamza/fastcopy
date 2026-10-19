# Draws the FastCopy logo (same design as Logo.Draw in FastCopy.cs) and writes
# src\FastCopy.ico with PNG images at every Windows icon size.
#   powershell -ExecutionPolicy Bypass -File src\make-icon.ps1
Add-Type -AssemblyName System.Drawing

function Draw-Logo([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap($s, $s)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $r = $s * 0.22; $d = $r * 2; $e = $s - 1
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc(0, 0, $d, $d, 180, 90)
    $path.AddArc($e - $d, 0, $d, $d, 270, 90)
    $path.AddArc($e - $d, $e - $d, $d, $d, 0, 90)
    $path.AddArc(0, $e - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.PointF(0, 0)), (New-Object System.Drawing.PointF($s, $s)),
        [System.Drawing.Color]::FromArgb(30, 136, 229), [System.Drawing.Color]::FromArgb(13, 71, 161))
    $g.FillPath($brush, $path)
    $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::White, [single]([math]::Max(1.5, $s * 0.11)))
    $pen.StartCap = 'Round'; $pen.EndCap = 'Round'; $pen.LineJoin = 'Round'
    foreach ($dx in 0.24, 0.46) {
        $pts = [System.Drawing.PointF[]]@(
            (New-Object System.Drawing.PointF(($s * $dx), ($s * 0.28))),
            (New-Object System.Drawing.PointF(($s * ($dx + 0.22)), ($s * 0.50))),
            (New-Object System.Drawing.PointF(($s * $dx), ($s * 0.72))))
        $g.DrawLines($pen, $pts)
    }
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return ,$ms.ToArray()
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$images = foreach ($s in $sizes) { ,(Draw-Logo $s) }

$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter($out)
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $len = $images[$i].Length
    $w.Write([byte]($s % 256)); $w.Write([byte]($s % 256))   # 256 is stored as 0
    $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$len); $w.Write([uint32]$offset)
    $offset += $len
}
foreach ($img in $images) { $w.Write($img) }
$w.Flush()
$dest = Join-Path $PSScriptRoot 'FastCopy.ico'
[System.IO.File]::WriteAllBytes($dest, $out.ToArray())
"Wrote $dest ($($out.Length) bytes, sizes: $($sizes -join ', '))"
