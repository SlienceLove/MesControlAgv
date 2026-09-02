Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent $PSScriptRoot
$docs = Join-Path $root 'docs'

function New-Canvas([int]$width, [int]$height) {
    $bitmap = [System.Drawing.Bitmap]::new($width, $height)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.Clear([System.Drawing.Color]::FromArgb(250, 252, 255))
    return @($bitmap, $graphics)
}

function Draw-Box($graphics, [int]$x, [int]$y, [int]$width, [int]$height, [string]$text, $fill, $stroke, [int]$fontSize = 18) {
    $brush = [System.Drawing.SolidBrush]::new($fill)
    $pen = [System.Drawing.Pen]::new($stroke, 2)
    $graphics.FillRectangle($brush, $x, $y, $width, $height)
    $graphics.DrawRectangle($pen, $x, $y, $width, $height)
    $format = [System.Drawing.StringFormat]::new()
    $format.Alignment = [System.Drawing.StringAlignment]::Center
    $format.LineAlignment = [System.Drawing.StringAlignment]::Center
    $font = [System.Drawing.Font]::new('Segoe UI', $fontSize, [System.Drawing.FontStyle]::Bold)
    $textBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(31, 55, 85))
    $graphics.DrawString($text, $font, $textBrush, [System.Drawing.RectangleF]::new($x + 6, $y + 4, $width - 12, $height - 8), $format)
    $textBrush.Dispose(); $font.Dispose(); $format.Dispose(); $pen.Dispose(); $brush.Dispose()
}

function Draw-Label($graphics, [int]$x, [int]$y, [int]$width, [int]$height, [string]$text, [int]$fontSize = 15) {
    $format = [System.Drawing.StringFormat]::new()
    $format.Alignment = [System.Drawing.StringAlignment]::Center
    $format.LineAlignment = [System.Drawing.StringAlignment]::Center
    $font = [System.Drawing.Font]::new('Segoe UI', $fontSize, [System.Drawing.FontStyle]::Bold)
    $brush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(80, 100, 120))
    $graphics.DrawString($text, $font, $brush, [System.Drawing.RectangleF]::new($x, $y, $width, $height), $format)
    $brush.Dispose(); $font.Dispose(); $format.Dispose()
}

function Draw-Arrow($graphics, [int]$x1, [int]$y1, [int]$x2, [int]$y2, $color) {
    $pen = [System.Drawing.Pen]::new($color, 3)
    $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::ArrowAnchor
    $graphics.DrawLine($pen, $x1, $y1, $x2, $y2)
    $pen.Dispose()
}

function T([string]$value) {
    return [System.Text.Encoding]::UTF8.GetString([System.Convert]::FromBase64String($value))
}

$navy = [System.Drawing.Color]::FromArgb(31, 78, 121)
$blue = [System.Drawing.Color]::FromArgb(60, 130, 196)
$lightBlue = [System.Drawing.Color]::FromArgb(227, 240, 252)
$green = [System.Drawing.Color]::FromArgb(44, 128, 91)
$lightGreen = [System.Drawing.Color]::FromArgb(229, 244, 235)
$orange = [System.Drawing.Color]::FromArgb(212, 130, 34)
$lightOrange = [System.Drawing.Color]::FromArgb(254, 241, 221)
$red = [System.Drawing.Color]::FromArgb(180, 57, 57)
$lightRed = [System.Drawing.Color]::FromArgb(252, 232, 232)
$grey = [System.Drawing.Color]::FromArgb(93, 108, 124)
$lightGrey = [System.Drawing.Color]::FromArgb(241, 244, 247)

$t0 = T '5pON5L2c5LiO57yW5o6S5bGC'; $t1 = T '5Lia5Yqh5LiO6K6+5aSH5pyN5Yqh5bGC'; $t2 = T '6L+Q6KGM546v5aKD5LiO6K6+5aSH5bGC'
$t3 = T 'V1BGIOS4reaOpwrku7vliqEgLyDmjIfmoIcgLyDlnLDlm74='; $t4 = T 'TUVTIOacjeWKoQrku7vliqHnirbmgIEgLyDlrqHorqEgLyDmgaLlpI0='; $t5 = T '5bel5L2c5rWB5pyN5YqhCuiNieeovyAvIOagoemqjCAvIOWPkeW4gw=='
$t6 = T '5aWR57qm5LiO5bqU55So5bGCCuaVsOaNruWlkee6piAvIOerr+WPoyAvIOW6lOeUqOi+ueeVjA=='; $t7 = T '6aKG5Z+f5bGCCuS7u+WKoeeKtuaAgeacuiAvIOi3r+W+hOinhOWIkgrlpJogQUdWIOiwg+W6piAvIOmFjee9rg=='
$t8 = T 'QWRhcHRlcgrljY/orq4gLyDluYLnrYkgLyDlronlhagK5o6n5Yi25p2DIC8g5a+56LSm'; $t9 = T 'QUdWIOS7v+ecn+WZqArkuInovabku7/nnJ8gLyDmlYXpmpzms6jlhaU='
$t10 = T '6amx5Yqo6L6555WMCuS7v+ecn+mpseWKqCAvIOWOguWVhiBUQ1Ag6amx5YqoCumFjee9ruWIh+aNog=='; $t11 = T '5Lu/55yf6L+Q6KGM6Lev5b6ECum7mOiupCAvIOemu+e6v+mqjOivgQ=='
$t12 = T '5Y6C5ZWGIFRDUCDoh7Plrp7kvZMgQUdWCumcgOimgeeOsOWcuuaOiOadgw=='
$t13 = T '5Yib5bu6CkNyZWF0ZWQ='; $t14 = T '5rS+5Y+RCkRpc3BhdGNoaW5n'; $t15 = T '5YmN5b6A5Y+W6LSnCk1vdmluZ1RvUGlja3Vw'; $t16 = T '562J5b6F5Y+W6LSn56Gu6K6kCldhaXRpbmdQaWNrdXA='
$t17 = T '5YmN5b6A5pS+6LSnCk1vdmluZ1RvRHJvcG9mZg=='; $t18 = T '562J5b6F5pS+6LSn56Gu6K6kCldhaXRpbmdEcm9wb2Zm'; $t19 = T '5a6M5oiQCkNvbXBsZXRlZA=='
$t20 = T '6K6+5aSH5aSx6LSlCkZhaWxlZCAvIOWPr+mHjeivlQ=='; $t21 = T '57uT5p6c5pyq56Gu6K6kClVua25vd24gLyDlr7notKbmgaLlpI0='; $t22 = T '5pqC5YGcIC8g5Y+W5raIClBhdXNlZCAvIENhbmNlbGxlZA=='
$t23 = T '5rS+5Y+R'; $t24 = T '5Yiw56uZ'; $t25 = T '56Gu6K6k'

$drawing = New-Canvas 1600 760; $bitmap = $drawing[0]; $g = $drawing[1]
Draw-Label $g 30 34 1540 30 $t0 13
Draw-Label $g 30 235 1540 30 $t1 13
Draw-Label $g 30 512 1540 30 $t2 13
Draw-Box $g 100 83 260 82 $t3 $lightBlue $navy 16
Draw-Box $g 520 69 300 112 $t4 $lightBlue $navy 16
Draw-Box $g 1040 83 280 82 $t5 $lightBlue $navy 16
Draw-Arrow $g 360 124 520 124 $navy; Draw-Arrow $g 820 124 1040 124 $navy
Draw-Label $g 380 95 120 22 'HTTP / JSON' 12; Draw-Label $g 853 95 150 22 'HTTP / JSON' 12
Draw-Box $g 100 310 260 112 $t6 $lightBlue $blue 15
Draw-Box $g 470 290 340 152 $t7 $lightBlue $navy 15
Draw-Box $g 970 310 270 112 $t8 $lightGreen $green 15
Draw-Arrow $g 360 366 470 366 $blue; Draw-Arrow $g 810 366 970 366 $green
Draw-Box $g 480 451 130 48 'MES SQLite' $lightGrey $grey 13
Draw-Box $g 665 451 150 48 'Adapter SQLite' $lightGrey $grey 13
Draw-Arrow $g 545 442 545 451 $grey; Draw-Arrow $g 735 442 735 451 $grey
Draw-Box $g 100 574 285 92 $t9 $lightGreen $green 15
Draw-Box $g 525 552 340 140 $t10 $lightOrange $orange 14
Draw-Box $g 1015 552 280 62 $t11 $lightGreen $green 13
Draw-Box $g 1015 634 280 62 $t12 $lightRed $red 13
Draw-Arrow $g 385 620 525 620 $green; Draw-Arrow $g 865 586 1015 586 $green; Draw-Arrow $g 865 658 1015 665 $red
$bitmap.Save((Join-Path $docs 'agv-mes-architecture.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bitmap.Dispose()

$drawing = New-Canvas 1600 410; $bitmap = $drawing[0]; $g = $drawing[1]
Draw-Box $g 40 115 190 100 $t13 $lightBlue $navy 15
Draw-Box $g 270 115 190 100 $t14 $lightBlue $navy 15
Draw-Box $g 500 115 205 100 $t15 $lightBlue $navy 15
Draw-Box $g 745 115 215 100 $t16 $lightOrange $orange 14
Draw-Box $g 1000 115 205 100 $t17 $lightBlue $navy 15
Draw-Box $g 1245 115 215 100 $t18 $lightOrange $orange 14
Draw-Box $g 1320 285 195 78 $t19 $lightGreen $green 15
Draw-Box $g 480 285 190 78 $t20 $lightRed $red 14
Draw-Box $g 765 285 190 78 $t21 $lightOrange $orange 14
Draw-Box $g 1015 285 190 78 $t22 $lightGrey $grey 14
foreach ($pair in @(@(230,165,270,165), @(460,165,500,165), @(705,165,745,165), @(960,165,1000,165), @(1205,165,1245,165))) { Draw-Arrow $g $pair[0] $pair[1] $pair[2] $pair[3] $navy }
Draw-Arrow $g 1352 215 1417 285 $green; Draw-Arrow $g 600 215 575 285 $red; Draw-Arrow $g 600 363 765 363 $orange; Draw-Arrow $g 850 285 850 215 $orange; Draw-Arrow $g 1102 215 1110 285 $grey; Draw-Arrow $g 1110 363 955 363 $grey
Draw-Label $g 230 135 40 22 $t23 10; Draw-Label $g 705 135 40 22 $t24 10; Draw-Label $g 960 135 40 22 $t25 10; Draw-Label $g 1205 135 40 22 $t24 10; Draw-Label $g 1390 235 55 22 $t25 10
$bitmap.Save((Join-Path $docs 'agv-mes-task-flow.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bitmap.Dispose()
