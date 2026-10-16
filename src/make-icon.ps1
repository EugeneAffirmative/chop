# Draws Chop's icon (a timeline with a cut) and writes a multi-size PNG-compressed .ico
param([string]$Out = "$PSScriptRoot\chop.ico")
Add-Type -AssemblyName System.Drawing

function Draw([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)
    $r = $s * 0.18
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $w = $s - 1
    $path.AddArc(0, 0, $r * 2, $r * 2, 180, 90); $path.AddArc($w - $r * 2, 0, $r * 2, $r * 2, 270, 90)
    $path.AddArc($w - $r * 2, $w - $r * 2, $r * 2, $r * 2, 0, 90); $path.AddArc(0, $w - $r * 2, $r * 2, $r * 2, 90, 90)
    $path.CloseFigure()
    $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.Point 0, 0), (New-Object System.Drawing.Point 0, $s), ([System.Drawing.Color]::FromArgb(36, 40, 52)), ([System.Drawing.Color]::FromArgb(18, 19, 24))
    $g.FillPath($bg, $path)
    $blue = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(64, 128, 240))
    $blue2 = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(46, 98, 200))
    $y = $s * 0.40; $h = $s * 0.24
    $g.FillRectangle($blue, $s * 0.12, $y, $s * 0.30, $h)
    $g.FillRectangle($blue2, $s * 0.58, $y, $s * 0.30, $h)
    $red = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 80, 80)), ([Math]::Max(1.5, $s * 0.07))
    $g.DrawLine($red, $s * 0.50, $s * 0.18, $s * 0.50, $s * 0.82)
    $tri = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 80, 80))
    $g.FillPolygon($tri, [System.Drawing.PointF[]]@((New-Object System.Drawing.PointF ($s * 0.38), ($s * 0.14)), (New-Object System.Drawing.PointF ($s * 0.62), ($s * 0.14)), (New-Object System.Drawing.PointF ($s * 0.50), ($s * 0.28))))
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return ,$ms.ToArray()
}

$sizes = 16, 24, 32, 48, 64, 256
$pngs = @(); foreach ($sz in $sizes) { $pngs += ,(Draw $sz) }
$fs = [System.IO.File]::Create($Out)
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $sz = $sizes[$i]; $b = if ($sz -ge 256) { 0 } else { $sz }
    $bw.Write([byte]$b); $bw.Write([byte]$b); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([UInt16]1); $bw.Write([UInt16]32); $bw.Write([UInt32]$pngs[$i].Length); $bw.Write([UInt32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $bw.Write($p) }
$bw.Close()
