param(
    [int]$Port = 5021,
    [switch]$SkipOfflineCheck
)

# ModbusScan.exe içindeki MainForm'i yansıtmayla kurar ve okuma yollarını headless test eder.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms

$asm = [Reflection.Assembly]::LoadFrom((Resolve-Path "$PSScriptRoot\..\dist\ModbusScan.exe").Path)
$type = $asm.GetType('ModbusScan.MainForm')
$flags = [Reflection.BindingFlags]'NonPublic,Instance'

function Get-Field($Object, [string]$Name) {
    $field = $Object.GetType().GetField($Name, $flags)
    if ($null -eq $field) { throw "Alan bulunamadi: $Name" }
    return $field.GetValue($Object)
}

function Invoke-Handler($Form, [string]$Name) {
    # PerformClick pencere gösterilmeden tetiklenmediği için handler doğrudan çağrılır.
    $method = $Form.GetType().GetMethod($Name, $flags)
    if ($null -eq $method) { throw "Metot bulunamadi: $Name" }
    $arguments = @($Form, [EventArgs]::Empty)
    $method.Invoke($Form, $arguments) | Out-Null
}

function Get-GridRows($Form) {
    $grid = Get-Field $Form 'grid'
    $rows = @()
    foreach ($row in $grid.Rows) {
        if ($row.IsNewLine) { continue }
        $cells = @()
        foreach ($col in @('colIndex', 'colAddress', 'colHex', 'colDec', 'colSigned', 'colValue')) {
            $cells += [string]$row.Cells[$col].Value
        }
        $rows += ($cells -join ' | ')
    }
    return $rows
}

function Set-Combo($Form, [string]$Field, [int]$Index) {
    Get-Field $Form $Field | ForEach-Object { $_.SelectedIndex = $Index }
}

$fail = 0
$form = [Activator]::CreateInstance($type)
(Get-Field $form 'txtIp').Text = '127.0.0.1'
(Get-Field $form 'txtPort').Text = "$Port"
(Get-Field $form 'txtUnit').Text = '1'
(Get-Field $form 'txtTimeout').Text = '2000'
Invoke-Handler $form 'OnConnect'

if ((Get-Field $form 'lblStatus').Text -notmatch '127') {
    Write-Host 'HAYIR  test sunucusuna baglanilamadi, testler atlandi.'
    exit 9
}

# Adres listesi: 0-1 float 3.14 (40 48 F5 C3), 2 = 65535/-1, 3 = 1234, 4-5 = uint32 100000
$cases = @()
$cases += [pscustomobject]@{ Name = 'FC03 uint16 + 4xxxx'; Function = 0; Decode = 0; Word = 0; Addr = '0'; Qty = '10'; Rows = 10; Cell = '40009' }
$cases += [pscustomobject]@{ Name = 'FC03 int16 negatif'; Function = 0; Decode = 1; Word = 0; Addr = '2'; Qty = '2'; Rows = 2; Cell = '-1' }
$cases += [pscustomobject]@{ Name = 'FC03 hex'; Function = 0; Decode = 2; Word = 0; Addr = '3'; Qty = '1'; Rows = 1; Cell = '0x04D2' }
$cases += [pscustomobject]@{ Name = 'FC03 float32 ABCD'; Function = 0; Decode = 3; Word = 0; Addr = '0'; Qty = '4'; Rows = 4; Cell = '3.140000' }
$cases += [pscustomobject]@{ Name = 'FC03 float32 CDAB'; Function = 0; Decode = 3; Word = 1; Addr = '12'; Qty = '2'; Rows = 2; Cell = '3.140000' }
$cases += [pscustomobject]@{ Name = 'FC03 uint32 ABCD'; Function = 0; Decode = 4; Word = 0; Addr = '4'; Qty = '2'; Rows = 2; Cell = '100000' }
$cases += [pscustomobject]@{ Name = 'FC04 input 3xxxx'; Function = 1; Decode = 0; Word = 0; Addr = '2'; Qty = '4'; Rows = 4; Cell = '30003' }
$cases += [pscustomobject]@{ Name = 'FC01 coil bit deseni'; Function = 2; Decode = 0; Word = 0; Addr = '0'; Qty = '12'; Rows = 12; Cell = '3 | 4 |' }
$cases += [pscustomobject]@{ Name = 'FC02 discrete 1xxxx'; Function = 3; Decode = 0; Word = 0; Addr = '2'; Qty = '6'; Rows = 6; Cell = '1 | 10004 |' }
$cases += [pscustomobject]@{ Name = 'PDU adres modu'; Function = 0; Decode = 0; Word = 0; Addr = '3'; Qty = '1'; Rows = 1; Cell = '0 | 3 |'; Convention = $false }
$cases += [pscustomobject]@{ Name = 'Adet siniri 126'; Function = 0; Decode = 0; Word = 0; Addr = '0'; Qty = '126'; Rows = 0; Cell = 'Adet 1..125' }
$cases += [pscustomobject]@{ Name = 'Exception FC03/02'; Function = 0; Decode = 0; Word = 0; Addr = '9000'; Qty = '1'; Rows = 0; Cell = 'hata kodu 02' }

foreach ($case in $cases) {
    Set-Combo $form 'cmbFunction' $case.Function
    Set-Combo $form 'cmbDecode' $case.Decode
    Set-Combo $form 'cmbWordOrder' $case.Word
    if ($case.PSObject.Properties.Name -contains 'Convention') {
        (Get-Field $form 'chkConvention').Checked = $case.Convention
    } else {
        (Get-Field $form 'chkConvention').Checked = $true
    }
    (Get-Field $form 'txtAddress').Text = $case.Addr
    (Get-Field $form 'txtQuantity').Text = $case.Qty

    Invoke-Handler $form 'OnRead'

    $rows = @(Get-GridRows $form)
    $summary = [string](Get-Field $form 'lblSummary').Text

    $rowCountOk = ($rows.Count -eq $case.Rows)
    $cellOk = ($summary -like ('*' + $case.Cell + '*')) -or (($rows | Where-Object { $_ -like ('*' + $case.Cell + '*') }).Count -gt 0)
    $ok = if ($case.Rows -eq 0) { $cellOk } else { $rowCountOk -and $cellOk }

    if ($ok) {
        Write-Host ('GECTI  {0}: {1} satir, {2}' -f $case.Name, $rows.Count, $summary)
    } else {
        $fail++
        Write-Host ('KALDI  {0}: satir={1}/{2} beklenen="{3}" ozet="{4}" durum="{5}"' -f `
                $case.Name, $rows.Count, $case.Rows, $case.Cell, $summary, (Get-Field $form 'lblStatus').Text)
        $rows | Select-Object -First 2 | ForEach-Object { Write-Host "        $_" }
    }
}

$form.Close()

# --- Guncelleme seridi: durum makinesi yansitmaya dogrudan surulur (aga gidilmez) ---
function New-UpdateInfo([string]$Tag, [bool]$Newer, [bool]$Asset, [long]$Size) {
    $t = $asm.GetType('ModbusScan.UpdateInfo')
    $i = [Activator]::CreateInstance($t)
    $i.Tag = $Tag
    $i.Version = [Version]::new(9, 9, 9)
    $i.NewerAvailable = $Newer
    $i.AssetAvailable = $Asset
    $i.SizeBytes = $Size
    $i.ReleasePage = "https://github.com/dincer552/modbus_scan/releases/tag/$Tag"
    $i.DownloadUrl = "https://github.com/dincer552/modbus_scan/releases/download/$Tag/ModbusScan-$Tag.exe"
    return $i
}

function Invoke-Finish($Form, $Info, [string]$Error, [bool]$Manual) {
    $m = $Form.GetType().GetMethod('FinishUpdateCheck', $flags)
    $m.Invoke($Form, @($Info, $Error, $Manual)) | Out-Null
}

$ui = [Activator]::CreateInstance($type)

Invoke-Finish $ui (New-UpdateInfo 'v9.9.9' $true $true 19968) $null $false
$lbl = [string](Get-Field $ui 'lblUpdate').Text
$btn = Get-Field $ui 'btnUpdate'
$green = (Get-Field $ui 'lblUpdate').ForeColor
if ($btn.Enabled -and $btn.Text -eq 'Güncelle' -and $lbl -like '*YENİ sürüm var*9.9.9*' -and $green.R -eq 0 -and $green.G -eq 128) {
    Write-Host "GECTI  yeni surum: yesil bilgi + Guncelle aktif -> $lbl"
} else {
    $fail++
    Write-Host "KALDI  yeni surum durumu: '$lbl' btn=$($btn.Text) enabled=$($btn.Enabled) renk=$($green.R),$($green.G),$($green.B)"
}

Invoke-Finish $ui (New-UpdateInfo 'v9.9.8' $true $false 0) $null $false
$lbl = [string](Get-Field $ui 'lblUpdate').Text
$btn = Get-Field $ui 'btnUpdate'
if ($btn.Enabled -and ($btn.Text -eq 'Sayfayı aç' -or $btn.Text -eq 'Sayfayi ac') -and ($lbl -like '*hazır exe yok*' -or $lbl -like '*hazir exe yok*')) {
    Write-Host "GECTI  exe yok: sayfa onerisi -> $lbl / $($btn.Text)"
} else {
    $fail++
    Write-Host "KALDI  exe yok durumu: '$lbl' btn=$($btn.Text) enabled=$($btn.Enabled)"
}

Invoke-Finish $ui (New-UpdateInfo 'v0.2.0' $false $false 0) $null $true
$lbl = [string](Get-Field $ui 'lblUpdate').Text
$btn = Get-Field $ui 'btnUpdate'
if (-not $btn.Enabled -and ($lbl -like '*Güncel*' -or $lbl -like '*Guncel*')) {
    Write-Host "GECTI  guncel surum: buton pasif -> $lbl"
} else {
    $fail++
    Write-Host "KALDI  guncel surum durumu: '$lbl' enabled=$($btn.Enabled)"
}

Invoke-Finish $ui $null 'sunucu yanit vermedi' $true
$lbl = [string](Get-Field $ui 'lblUpdate').Text
if ($lbl -like '*yanit vermedi*') {
    Write-Host "GECTI  denetim hatasi goruntulendi -> $lbl"
} else {
    $fail++
    Write-Host "KALDI  denetim hatasi goruntulenmedi: '$lbl'"
}

$running = [bool](Get-Field $ui '_updateCheckRunning')
$installing = [bool](Get-Field $ui '_updateInstallRunning')
if (-not $running -and -not $installing) {
    Write-Host 'GECTI  denetim bitti: bayraklar sifirlandi (tekrar denetlenebilir)'
} else {
    $fail++
    Write-Host "KALDI  denetim bayraklari takili: check=$running install=$installing"
}

# Duzen: serit 500 px genislikte kalmali ve tabloya tasmasın
$layout = @()
foreach ($name in @('lblUpdate', 'btnCheckUpdate', 'btnUpdate', 'grid')) {
    $c = Get-Field $ui $name
    $layout += [pscustomobject]@{ Name = $name; X = $c.Left; Y = $c.Top; Right = $c.Right; Bottom = $c.Bottom }
}
$strip = $layout | Where-Object { $_.Name -ne 'grid' }
$gridRect = $layout | Where-Object { $_.Name -eq 'grid' }
$maxRight = ($strip | Measure-Object -Property Right -Maximum).Maximum
$maxStripBottom = ($strip | Measure-Object -Property Bottom -Maximum).Maximum
if ($maxRight -le 492 -and ($gridRect.Y - $maxStripBottom) -ge 4 -and $gridRect.Bottom -le 592) {
    Write-Host ("GECTI  duzen: serit sag={0}, serit alt={1}, tablo ust={2}" -f $maxRight, $maxStripBottom, $gridRect.Y)
} else {
    $fail++
    Write-Host ("KALDI  duzen: sag={0} seritAlt={1} tabloUst={2} tabloAlt={3}" -f $maxRight, $maxStripBottom, $gridRect.Y, $gridRect.Bottom)
}

$ui.Close()

if (-not $SkipOfflineCheck) {
    $offline = [Activator]::CreateInstance($type)
    (Get-Field $offline 'txtIp').Text = '127.0.0.1'
    (Get-Field $offline 'txtPort').Text = '5099'
    (Get-Field $offline 'txtTimeout').Text = '600'
    Invoke-Handler $offline 'OnConnect'
    $log = [string](Get-Field $offline 'txtLog').Text
    $canRetry = [bool](Get-Field $offline 'btnConnect').Enabled
    $readEnabled = [bool](Get-Field $offline 'btnRead').Enabled
    if ($canRetry -and -not $readEnabled -and $log.Length -gt 0) {
        Write-Host 'GECTI  baglanti yok: buton durumlari + log satiri'
    } else {
        $fail++
        Write-Host "KALDI  baglanti yok senaryosu: $log / bas=$canRetry oku=$readEnabled"
    }
    $offline.Close()
}

Write-Host ('SONUC: {0} hata' -f $fail)
exit $fail
