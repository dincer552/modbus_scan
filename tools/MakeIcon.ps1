# ModbusScan ikon ureticisi
# Ikonu GDI+ ile kod cizerek uretir: gercek alfa (saydam arka plan), her boyutta net.
#
#   powershell -ExecutionPolicy Bypass -File tools/MakeIcon.ps1
#
# Cikti: assets/ModbusScan.ico (16/24/32/48/64/128/256) + dist/icon-preview/*.png onizlemeleri.
# Cizim kodla yapildigi icin derleme yeniden uretilebilir (CI ayni ico'yu uretebilir).

param(
    [int[]]$Sizes = @(16, 24, 32, 48, 64, 128, 256),
    [string]$Out = 'assets\ModbusScan.ico',
    [string]$MasterPng = 'assets\icon-master.png',
    [string]$PreviewDir = 'dist\icon-preview',
    [double]$Fill = 0.94
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$Teal = [System.Drawing.Color]::FromArgb(255, 15, 124, 134)      # cerceve + dalga + register cubuklari
$Green = [System.Drawing.Color]::FromArgb(255, 40, 199, 94)      # tarama tamam vurgusu

function Add-RoundedRect([System.Drawing.Drawing2D.GraphicsPath]$path, [single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $d = $r * 2
    [void]$path.AddArc($x, $y, $d, $d, [single]180, [single]90)
    [void]$path.AddArc($x + $w - $d, $y, $d, $d, [single]270, [single]90)
    [void]$path.AddArc($x + $w - $d, $y + $h - $d, $d, $d, [single]0, [single]90)
    [void]$path.AddArc($x, $y + $h - $d, $d, $d, [single]90, [single]90)
    $path.CloseFigure()
}

function New-Pen([System.Drawing.Color]$color, [single]$width) {
    $pen = New-Object System.Drawing.Pen($color, $width)
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    $pen
}

# Bir boyutta ikon cizer. $supersample = hedefin 8 kati cozunurlukte cizim, sonra bicubic ile kucultulur.
function Render-Icon([int]$size, [int]$supersample = 1024) {
    $u = $supersample / 100.0                       # 1 birim = tuvalin %1'i
    $canvas = New-Object System.Drawing.Bitmap($supersample, $supersample, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($canvas)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    $detail = if ($size -le 24) { 'simple' } elseif ($size -le 48) { 'medium' } else { 'full' }

    # --- cerceve: koseyuvarlak kare, disa hizali tek cizgi ---
    $framePen = if ($detail -eq 'simple') { New-Pen $Teal ([single](9.5 * $u)) } else { New-Pen $Teal ([single](8.5 * $u)) }
    $framePath = New-Object System.Drawing.Drawing2D.GraphicsPath
    $framePath.FillMode = [System.Drawing.Drawing2D.FillMode]::Winding
    $m = [single](6.0 * $u)
    $side = [single]($supersample - 2 * $m)
    Add-RoundedRect $framePath $m $m $side $side ([single](0.22 * $side))
    $g.DrawPath($framePen, $framePath)
    $framePath.Dispose(); $framePen.Dispose()

    # ic alan (cerceve cizgisinin ici)
    $inset = [single](6.0 + 8.5 / 2 + 3.5)
    $x0 = [single]($inset * $u)
    $x1 = [single](($supersample / $u - $inset) * $u)

    # --- dalga (Modbus sinyali): ust bantta, sabit genlikli nabiz ---
    if ($detail -eq 'simple') {
        $waveY = 50.0
        $amp = 15.0
        $left = 16.0; $right = 84.0
        $wavePen = New-Pen $Teal ([single](11.0 * $u))
    } else {
        $waveY = if ($detail -eq 'medium') { 46.0 } else { 40.0 }
        $amp = 13.0
        $left = 17.0; $right = 83.0
        $wavePen = New-Pen $Teal ([single](7.6 * $u))
    }
    $pts = @(
        [System.Drawing.PointF]::new([single]($left * $u), [single]($waveY * $u)),
        [System.Drawing.PointF]::new([single](($left + 8) * $u), [single]($waveY * $u)),
        [System.Drawing.PointF]::new([single](($left + 16) * $u), [single](($waveY - $amp * 0.55) * $u)),
        [System.Drawing.PointF]::new([single](($left + 24) * $u), [single](($waveY + $amp * 0.45) * $u)),
        [System.Drawing.PointF]::new([single](($left + 33) * $u), [single](($waveY - $amp) * $u)),
        [System.Drawing.PointF]::new([single](($left + 42) * $u), [single](($waveY + $amp * 0.6) * $u)),
        [System.Drawing.PointF]::new([single](($left + 50) * $u), [single](($waveY - $amp * 0.35) * $u)),
        [System.Drawing.PointF]::new([single](($left + 58) * $u), [single]($waveY * $u)),
        [System.Drawing.PointF]::new([single](($right * $u)), [single]($waveY * $u))
    )
    $g.DrawCurve($wavePen, $pts, [single]0.35)
    $wavePen.Dispose()

    if ($detail -ne 'simple') {
        # --- tarama tamam vurgusu (yesil tik) ---
        $tickPen = New-Pen $Green ([single](9.0 * $u))
        $check = @(
            [System.Drawing.PointF]::new([single](52.0 * $u), [single](72.0 * $u)),
            [System.Drawing.PointF]::new([single](61.0 * $u), [single](81.0 * $u)),
            [System.Drawing.PointF]::new([single](80.0 * $u), [single](56.0 * $u))
        )
        $path = New-Object System.Drawing.Drawing2D.GraphicsPath
        $path.AddLines($check)
        $g.DrawPath($tickPen, $path)
        $path.Dispose(); $tickPen.Dispose()
    }

    if ($detail -eq 'full') {
        # --- register cubuklari: sol altta uc hap sekil, her birinde saydam delik ---
        $barW = [single](30.0 * $u)
        $barH = [single](9.0 * $u)
        $barX = [single](17.0 * $u)
        $brush = New-Object System.Drawing.SolidBrush($Teal)
        foreach ($cy in @(58.0, 70.0, 82.0)) {
            $barPath = New-Object System.Drawing.Drawing2D.GraphicsPath
            $barPath.FillMode = [System.Drawing.Drawing2D.FillMode]::Alternate   # icindeki daire delik olur
            Add-RoundedRect $barPath $barX ([single](($cy - 4.5) * $u)) $barW $barH ([single]($barH / 2))
            $holeR = [single](2.3 * $u)
            $barPath.AddEllipse([single]($barX + 5.5 * $u - $holeR), [single]($cy * $u - $holeR), [single](2 * $holeR), [single](2 * $holeR))
            $g.FillPath($brush, $barPath)
            $barPath.Dispose()
        }
        $brush.Dispose()
    }

    $g.Dispose()

    if ($supersample -eq $size) { return $canvas }

    $dst = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $gd = [System.Drawing.Graphics]::FromImage($dst)
    $gd.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $gd.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $gd.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $gd.Clear([System.Drawing.Color]::Transparent)
    $attr = New-Object System.Drawing.Imaging.ImageAttributes
    # TileFlipXY: kenarda saydam siyaha kacaklanan halka (koyu halo) olusmasini engeller.
    $attr.SetWrapMode([System.Drawing.Drawing2D.WrapMode]::TileFlipXY)
    $inner = [int][Math]::Round($size * $Fill)
    $pad = [int][Math]::Floor(($size - $inner) / 2)
    $gd.DrawImage($canvas, [System.Drawing.Rectangle]::FromLTRB($pad, $pad, $pad + $inner, $pad + $inner),
        0, 0, $supersample, $supersample, [System.Drawing.GraphicsUnit]::Pixel, $attr)
    $gd.Dispose(); $attr.Dispose(); $canvas.Dispose()
    $dst
}

function Get-PngBytes([System.Drawing.Bitmap]$bmp) {
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bytes = $ms.ToArray()
    $ms.Dispose()
    , $bytes
}

function Get-IcoEntryBytes([System.Drawing.Bitmap]$bmp) {
    # 32 bpp BGRA XOR verisi (alttan uste satirlar) + tumuyla opak AND maskesi.
    $w = $bmp.Width
    $h = $bmp.Height
    $rect = New-Object System.Drawing.Rectangle(0, 0, $w, $h)
    $data = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $stride = [Math]::Abs($data.Stride)
    $buf = New-Object byte[] ($stride * $h)
    [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $buf, 0, $buf.Length)
    $bmp.UnlockBits($data)

    $xor = New-Object byte[] ($w * $h * 4)
    $pos = 0
    for ($y = $h - 1; $y -ge 0; $y--) {
        $row = $y * $stride
        for ($x = 0; $x -lt $w; $x++) {
            $i = $row + $x * 4
            $xor[$pos++] = $buf[$i]
            $xor[$pos++] = $buf[$i + 1]
            $xor[$pos++] = $buf[$i + 2]
            $xor[$pos++] = $buf[$i + 3]
        }
    }

    $maskStride = ([int][Math]::Ceiling($w / 32.0)) * 4
    $mask = New-Object byte[] ($maskStride * $h)   # 0 = ciz => her piksel gorunur (alfa zaten 32 bpp'de)
    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($ms)
    $bw.Write([int32]40)
    $bw.Write([int32]$w)
    $bw.Write([int32]($h * 2))                     # ICO'da yukseklik = XOR + AND satir sayisi
    $bw.Write([int16]1)
    $bw.Write([int16]32)
    $bw.Write([int32]0)                             # BI_RGB
    $bw.Write([int32]($xor.Length + $mask.Length))
    $bw.Write([int32]0); $bw.Write([int32]0)
    $bw.Write([int32]0); $bw.Write([int32]0)
    $bw.Write($xor)
    $bw.Write($mask)
    $bw.Flush()
    $bytes = $ms.ToArray()
    $bw.Dispose(); $ms.Dispose()
    , $bytes
}

$outFull = Join-Path (Get-Location) $Out
$masterFull = Join-Path (Get-Location) $MasterPng
foreach ($d in @((Split-Path $outFull), (Split-Path $masterFull), $PreviewDir)) {
    if ($d -and -not (Test-Path $d)) { New-Item -ItemType Directory -Path $d -Force | Out-Null }
}

# Ust boyut PNG olarak da saklanir (belge/onizleme icin).
$master = Render-Icon 1024
$master.Save($masterFull, [System.Drawing.Imaging.ImageFormat]::Png)
$master.Dispose()
Write-Host ("Master PNG: {0}" -f $MasterPng)

$entries = @()
foreach ($s in ($Sizes | Sort-Object)) {
    $bmp = Render-Icon $s
    if ($s -ge 64) {
        $payload = Get-PngBytes $bmp        # 64px ve ustu PNG tasiyicisi (Vista+) - exe kucuk kalir
        $kind = 'png'
    } else {
        $payload = Get-IcoEntryBytes $bmp
        $kind = 'bmp'
    }
    $entries += [pscustomobject]@{ Size = $s; Bytes = $payload; Kind = $kind }
    $bmp.Save((Join-Path $PreviewDir ("icon-{0}.png" -f $s)), [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Host ("  {0}px -> {1} bayt ({2})" -f $s, $payload.Length, $kind)
}

$ms = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter($ms)
$bw.Write([int16]0); $bw.Write([int16]1); $bw.Write([int16]$entries.Count)
$offset = 6 + 16 * $entries.Count
foreach ($e in $entries) {
    $dim = if ($e.Size -ge 256) { [byte]0 } else { [byte]$e.Size }
    $bw.Write($dim); $bw.Write($dim)
    $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([int16]1)
    $bw.Write([int16]32)
    $bw.Write([int32]$e.Bytes.Length)
    $bw.Write([int32]$offset)
    $offset += $e.Bytes.Length
}
foreach ($e in $entries) { $bw.Write($e.Bytes) }
$bw.Flush()
[System.IO.File]::WriteAllBytes($outFull, $ms.ToArray())
$bw.Dispose(); $ms.Dispose()

Write-Host ("ICO yazildi: {0} ({1} bayt, {2} boyut)" -f $Out, (Get-Item $Out).Length, $entries.Count)
Write-Host ("Onizleme: {0}\icon-*.png" -f $PreviewDir)
