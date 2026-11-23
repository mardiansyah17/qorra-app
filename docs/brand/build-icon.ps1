Add-Type -AssemblyName PresentationCore, WindowsBase
$out = $PSScriptRoot
$accent = '#5B5BF0'; $tileTop = '#7C7CF3'; $tileBottom = '#4444B4'; $ink = '#17171C'

function Get-Dots([bool]$small) {
    $r = if ($small) { 2 } else { 3 }
    $ring = if ($small) { 4, 5 } else { 4, 5, 8, 9, 10 }
    foreach ($a in -$r..3) { foreach ($b in -$r..3) {
        $tail = ($a -eq $b) -and ($a -ge 2)
        if (($a -gt $r -or $b -gt $r) -and -not $tail) { continue }
        if ($tail -or ($ring -contains ($a * $a + $b * $b))) { [pscustomobject]@{ A = $a; B = $b; Tail = $tail } }
    } }
}

function Write-Svg($name, [bool]$small, [bool]$tile) {
    $step = if ($small) { 10 } else { 8 }; $rad = if ($small) { 3.8 } else { 3.2 }
    $body = foreach ($d in Get-Dots $small) {
        $fill = if ($tile) { if ($d.Tail) { '#FFFFFF' } else { '#FFFFFF" fill-opacity="0.62' } } else { if ($d.Tail) { $accent } else { $ink } }
        $o = if ($small) { 27 } else { 32 }
        "  <circle cx=`"$($o + $d.A * $step)`" cy=`"$($o + $d.B * $step)`" r=`"$rad`" fill=`"$fill`"/>"
    }
    $bg = ''
    if ($tile) {
        $bg = "  <defs><linearGradient id=`"g`" x1=`"0`" y1=`"0`" x2=`"0.6`" y2=`"1`"><stop offset=`"0`" stop-color=`"$tileTop`"/><stop offset=`"0.55`" stop-color=`"$accent`"/><stop offset=`"1`" stop-color=`"$tileBottom`"/></linearGradient></defs>`n" +
               "  <rect width=`"64`" height=`"64`" rx=`"15`" fill=`"url(#g)`"/>`n"
        $body = "  <g transform=`"translate(32 32) scale(0.72) translate(-32 -32)`">`n" + ($body -join "`n") + "`n  </g>"
    } else { $body = $body -join "`n" }
    $svg = "<svg xmlns=`"http://www.w3.org/2000/svg`" viewBox=`"0 0 64 64`">`n$bg$body`n</svg>`n"
    [IO.File]::WriteAllText((Join-Path $out $name), $svg)
}

function Get-Png([int]$size) {
    $small = $size -le 24
    $s = $size / 64.0
    $v = New-Object Windows.Media.DrawingVisual
    $dc = $v.RenderOpen()
    $grad = New-Object Windows.Media.LinearGradientBrush
    $grad.StartPoint = '0,0'; $grad.EndPoint = '0.6,1'
    $grad.GradientStops.Add((New-Object Windows.Media.GradientStop ([Windows.Media.ColorConverter]::ConvertFromString($tileTop)), 0))
    $grad.GradientStops.Add((New-Object Windows.Media.GradientStop ([Windows.Media.ColorConverter]::ConvertFromString($accent)), 0.55))
    $grad.GradientStops.Add((New-Object Windows.Media.GradientStop ([Windows.Media.ColorConverter]::ConvertFromString($tileBottom)), 1))
    $corner = [Math]::Max(3, 15 * $s)
    $dc.DrawRoundedRectangle($grad, $null, (New-Object Windows.Rect 0, 0, $size, $size), $corner, $corner)
    $scale = if ($small) { 0.7 } else { 0.72 }
    $step = $(if ($small) { 10 } else { 8 }) * $s * $scale
    $rad = $(if ($small) { 3.8 } else { 3.2 }) * $s * $scale
    $ringBrush = New-Object Windows.Media.SolidColorBrush ([Windows.Media.Color]::FromArgb(158, 255, 255, 255))
    foreach ($d in Get-Dots $small) {
        $brush = if ($d.Tail) { [Windows.Media.Brushes]::White } else { $ringBrush }
        $o = $size / 2 - $(if ($small) { $step / 2 } else { 0 })
        $c = New-Object Windows.Point ($o + $d.A * $step), ($o + $d.B * $step)
        $dc.DrawEllipse($brush, $null, $c, $rad, $rad)
    }
    $dc.Close()
    $bmp = New-Object Windows.Media.Imaging.RenderTargetBitmap $size, $size, 96, 96, ([Windows.Media.PixelFormats]::Pbgra32)
    $bmp.Render($v)
    $enc = New-Object Windows.Media.Imaging.PngBitmapEncoder
    $enc.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bmp))
    $ms = New-Object IO.MemoryStream
    $enc.Save($ms)
    , $ms.ToArray()
}

Write-Svg 'qorra-mark.svg' $false $false
Write-Svg 'qorra-mark-small.svg' $true $false
Write-Svg 'qorra-icon.svg' $false $true

$sizes = 16, 20, 24, 32, 48, 64, 128, 256
$pngs = foreach ($z in $sizes) { , (Get-Png $z) }
[IO.File]::WriteAllBytes((Join-Path $out 'qorra-256.png'), $pngs[-1])
$fs = [IO.File]::Create((Join-Path $out 'qorra.ico'))
$w = New-Object IO.BinaryWriter $fs
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $z = $sizes[$i]; $dim = if ($z -ge 256) { 0 } else { $z }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$pngs[$i].Length); $w.Write([uint32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $w.Write($p) }
$w.Close()
Write-Output "OK: $out"
