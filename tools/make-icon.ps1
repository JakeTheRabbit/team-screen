# Regenerates app.ico (16-256 px PNG frames) with the same monitor glyph the app draws at runtime (IconFactory.cs).
Add-Type -AssemblyName System.Drawing
$sizes = 16, 24, 32, 48, 64, 128, 256
$frames = foreach ($size in $sizes) {
  $bmp = New-Object Drawing.Bitmap $size, $size
  $g = [Drawing.Graphics]::FromImage($bmp)
  $g.SmoothingMode = 'AntiAlias'
  $s = $size / 32
  $pen = New-Object Drawing.Pen ([Drawing.Color]::FromArgb(0, 120, 212)), ([Math]::Max(1.5, 2.4 * $s))
  $pen.LineJoin = 'Round'
  $r = 3 * $s; $x = 3 * $s; $y = 5 * $s; $w = 26 * $s; $h = 17 * $s
  $p = New-Object Drawing.Drawing2D.GraphicsPath
  $p.AddArc($x, $y, $r * 2, $r * 2, 180, 90); $p.AddArc($x + $w - $r * 2, $y, $r * 2, $r * 2, 270, 90)
  $p.AddArc($x + $w - $r * 2, $y + $h - $r * 2, $r * 2, $r * 2, 0, 90); $p.AddArc($x, $y + $h - $r * 2, $r * 2, $r * 2, 90, 90)
  $p.CloseFigure(); $g.DrawPath($pen, $p)
  $g.DrawLine($pen, 16 * $s, 22 * $s, 16 * $s, 27 * $s); $g.DrawLine($pen, 10 * $s, 27.5 * $s, 22 * $s, 27.5 * $s)
  $g.FillEllipse((New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(16, 137, 62))), 19 * $s, 1 * $s, 12 * $s, 12 * $s)
  $ms = New-Object IO.MemoryStream; $bmp.Save($ms, [Drawing.Imaging.ImageFormat]::Png)
  [pscustomobject]@{ Size = $size; Bytes = $ms.ToArray() }
}
$out = New-Object IO.MemoryStream; $bw = New-Object IO.BinaryWriter $out
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$frames.Count)
$offset = 6 + 16 * $frames.Count
foreach ($f in $frames) {
  $dim = if ($f.Size -ge 256) { 0 } else { $f.Size }
  $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
  $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]$f.Bytes.Length); $bw.Write([uint32]$offset)
  $offset += $f.Bytes.Length
}
foreach ($f in $frames) { $bw.Write($f.Bytes) }
[IO.File]::WriteAllBytes((Join-Path $PSScriptRoot '..\app.ico'), $out.ToArray())
