# Dev-time: renders the 1024px app logo (white ScrollShot glyph on a rounded accent tile).
# Usage: pwsh tools/make-logo.ps1 [out.png]
param([string]$Out = "$PSScriptRoot\..\logo.png")
Add-Type -AssemblyName System.Drawing
$sz = 1024
$bmp = New-Object System.Drawing.Bitmap $sz, $sz
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'; $g.Clear([System.Drawing.Color]::Transparent)
$r = $sz * 0.22
$tile = New-Object System.Drawing.Drawing2D.GraphicsPath
$tile.AddArc(0, 0, 2*$r, 2*$r, 180, 90); $tile.AddArc($sz-2*$r, 0, 2*$r, 2*$r, 270, 90)
$tile.AddArc($sz-2*$r, $sz-2*$r, 2*$r, 2*$r, 0, 90); $tile.AddArc(0, $sz-2*$r, 2*$r, 2*$r, 90, 90); $tile.CloseFigure()
$br = New-Object System.Drawing.Drawing2D.LinearGradientBrush ([System.Drawing.Rectangle]::new(0,0,$sz,$sz)), ([System.Drawing.Color]::FromArgb(0,120,212)), ([System.Drawing.Color]::FromArgb(0,90,170)), 90.0
$g.FillPath($br, $tile)
# Glyph in a 16-unit design space, inset 18% inside the tile.
$inset = $sz * 0.18; $s = ($sz - 2*$inset) / 16
$g.TranslateTransform($inset, $inset); $g.ScaleTransform($s, $s)
$pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), 1.3
$pen.StartCap = 'Round'; $pen.EndCap = 'Round'; $pen.LineJoin = 'Round'
$a = 1.75; $b = 14.25; $len = 3.75; $cr = 1.6
foreach ($c in @(@($a,$a,1,1), @($b,$a,-1,1), @($a,$b,1,-1), @($b,$b,-1,-1))) {
  $x,$y,$dx,$dy = $c
  $p = New-Object System.Drawing.Drawing2D.GraphicsPath
  $p.AddLine([float]$x, [float]($y + $dy*$len), [float]$x, [float]($y + $dy*$cr))
  $cx = $x + $dx*$cr; $cy = $y + $dy*$cr
  $p.AddArc([float]($cx-$cr), [float]($cy-$cr), [float](2*$cr), [float](2*$cr), $(if ($dx -gt 0) {180} else {0}), [float]($dx*$dy*90))
  $p.AddLine([float]$cx, [float]$y, [float]($x + $dx*$len), [float]$y)
  $g.DrawPath($pen, $p)
}
foreach ($t in 4.9, 8.35) {
  $g.DrawLines($pen, [System.Drawing.PointF[]]@([System.Drawing.PointF]::new(5.25,$t), [System.Drawing.PointF]::new(8,$t+2.75), [System.Drawing.PointF]::new(10.75,$t)))
}
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
