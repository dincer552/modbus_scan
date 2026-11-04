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

function Invoke-Method($Form, [string]$Name, $Arguments) {
    # Parametreli private metot (ApplyDefinition, PasteDefinitions...) dogrudan cagrilir.
    $method = $Form.GetType().GetMethod($Name, $flags)
    if ($null -eq $method) { throw "Metot bulunamadi: $Name" }
    $method.Invoke($Form, $Arguments) | Out-Null
}

function Get-GridRows($Form) {
    $grid = Get-Field $Form 'grid'
    $rows = @()
    foreach ($row in $grid.Rows) {
        if ($row.IsNewLine) { continue }
        $cells = @()
        foreach ($col in @('colIndex', 'colAddress', 'colValue', 'colDef')) {
            $cells += [string]$row.Cells[$col].Value
        }
        $rows += ($cells -join ' | ')
    }
    return $rows
}

function Set-Combo($Form, [string]$Field, [int]$Index) {
    Get-Field $Form $Field | ForEach-Object { $_.SelectedIndex = $Index }
}

function Assert([bool]$Ok, [string]$Name, [string]$Detail) {
    if ($Ok) {
        Write-Host ('GECTI  ' + $Name)
    } else {
        $script:fail++
        Write-Host ('KALDI  ' + $Name + ' -> ' + $Detail)
    }
}

# Yazma popup'ini modal dongu icinden surer: zamanlayici dialogu bulur, degeri yazar, kapatir.
function Invoke-GridDoubleClick($Form, [int]$Row) {
    # Olay yoneticisi (CellContentDoubleClick) ayni metodun kisa hali; int parametreyle cagirmak yeterli.
    $method = $Form.GetType().GetMethod('OpenWriteDialog', $flags)
    $method.Invoke($Form, @($Row)) | Out-Null
}

function Dialog-Write {
    param(
        $Form, [int]$Function, [int]$Addr, [int]$Qty, [int]$Row,
        [int]$TypeIndex, [string]$Value, [int]$WordIndex = 0, [int]$CoilIndex = -1
    )

    Set-Combo $Form 'cmbFunction' $Function
    (Get-Field $Form 'txtAddress').Text = "$Addr"
    (Get-Field $Form 'txtQuantity').Text = "$Qty"
    Invoke-Handler $Form 'OnRead'

    $script:rowsBefore = @(Get-GridRows $Form)
    if ($script:rowsBefore.Count -le $Row) { throw "yazma satiri yok: satir=$Row toplam=$($script:rowsBefore.Count)" }

    $script:typeIndex = $TypeIndex
    $script:value = $Value
    $script:wordIndex = $WordIndex
    $script:coilIndex = $CoilIndex
    $script:dlg = [ordered]@{ Acildi = $false; Alan = ''; Mevcut = ''; Onizleme = ''; Sonuc = ''; YazPasif = $false; Ipucu = '' }

    $probe = New-Object System.Windows.Forms.Timer
    $probe.Interval = 350
    $probe.Add_Tick({
        $probe.Stop()
        $dlg = $null
        foreach ($f in [System.Windows.Forms.Application]::OpenForms) {
            if ($f.GetType().Name -eq 'WriteDialog') { $dlg = $f; break }
        }
        if ($null -eq $dlg) { return }
        $script:dlg['Acildi'] = $true
        $d = Get-Field $dlg 'lblArea'
        $script:dlg['Alan'] = [string]$d.Text
        $script:dlg['Mevcut'] = [string](Get-Field $dlg 'lblCurrent').Text
        $script:dlg['Ipucu'] = [string](Get-Field $dlg 'lblHint').Text

        (Get-Field $dlg 'cmbType').SelectedIndex = $script:typeIndex
        if ($script:coilIndex -ge 0) {
            (Get-Field $dlg 'cmbCoil').SelectedIndex = $script:coilIndex
        } else {
            (Get-Field $dlg 'txtValue').Text = $script:value
        }
        (Get-Field $dlg 'cmbWord').SelectedIndex = $script:wordIndex
        $script:dlg['Onizleme'] = [string](Get-Field $dlg 'lblPreview').Text

        $btnWrite = Get-Field $dlg 'btnWrite'
        $script:dlg['YazPasif'] = -not $btnWrite.Enabled
        if ($btnWrite.Enabled) { $btnWrite.PerformClick() }
        $script:dlg['Sonuc'] = [string](Get-Field $dlg 'lblResult').Text
        (Get-Field $dlg 'btnClose').PerformClick()
    })
    $probe.Start()
    Invoke-GridDoubleClick $Form $Row
    $probe.Dispose()

    $script:dlg['SonrakiSatirlar'] = @(Get-GridRows $Form)
    return $script:dlg
}

$fail = 0

# Kullanicinin kendi tanim dosyasi test kirliliginden korunur; sonda geri konur.
$defPath = Join-Path $env:APPDATA 'ModbusScan\definitions.txt'
$defBackup = if (Test-Path $defPath) { [System.IO.File]::ReadAllText($defPath) } else { $null }

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

# --- Yazma popup'i: hucreye cift tiklama senaryolari (ayni baglanti uzerinde kalir) ---

$d = Dialog-Write -Form $form -Function 0 -Addr 6 -Qty 4 -Row 0 -TypeIndex 0 -Value '777'
Assert ($d['Acildi'] -and $d['Alan'] -like '*40007*' -and $d['Alan'] -like '*PDU 6*') `
    'cift tiklama dialogu acildi + adres dogru' ($d['Alan'])
Assert ($d['Onizleme'] -like 'FC06 @6*03 09*') 'Uint16 onizleme baytlari' $d['Onizleme']
Assert ($d['Sonuc'] -like 'OK*Geri okundu: 03 09 = 777*') 'Uint16 yazma + geri okuma' $d['Sonuc']
Assert (@($d['SonrakiSatirlar'][0] -split '\|')[2].Trim() -eq '777') 'tablo yazma sonrasi tazelendi' $d['SonrakiSatirlar'][0]

$d = Dialog-Write -Form $form -Function 0 -Addr 6 -Qty 4 -Row 0 -TypeIndex 3 -Value '2.25'
Assert ($d['Onizleme'] -like 'FC16 @6 x2*40 10 00 00*') 'Float32 FC16 onizleme' $d['Onizleme']
Assert ($d['Sonuc'] -like 'OK*40 10 00 00 = 2.250000 (ABCD)*') 'Float32 yazma + geri okuma' $d['Sonuc']

$d = Dialog-Write -Form $form -Function 0 -Addr 8 -Qty 2 -Row 0 -TypeIndex 1 -Value '-5'
Assert ($d['Onizleme'] -like '*FF FB*') 'Int16 negatif onizleme' $d['Onizleme']
Assert ($d['Sonuc'] -like 'OK*FF FB = 65531 (int16 -5)*') 'Int16 yazma + geri okuma' $d['Sonuc']

$d = Dialog-Write -Form $form -Function 0 -Addr 16 -Qty 4 -Row 0 -TypeIndex 3 -Value '2.25' -WordIndex 1
Assert ($d['Onizleme'] -like '*00 00 40 10*') 'Float32 CDAB onizleme' $d['Onizleme']
Assert ($d['Sonuc'] -like 'OK*(CDAB)*') 'Float32 CDAB yazma' $d['Sonuc']

$d = Dialog-Write -Form $form -Function 2 -Addr 0 -Qty 12 -Row 0 -TypeIndex 0 -Value '' -CoilIndex 1
Assert ($d['Alan'] -like 'Coil*00001*' -or $d['Alan'] -like 'Coil*1 *') 'coil dialog adresi' $d['Alan']
Assert ($d['Onizleme'] -like 'FC05 @0*KAPALI*') 'coil FC05 onizleme' $d['Onizleme']
Assert ($d['Sonuc'] -like 'OK*Geri okundu: KAPALI (0)*') 'coil yazma + geri okuma' $d['Sonuc']
Assert (@($d['SonrakiSatirlar'][0] -split '\|')[2].Trim() -eq '0') 'coil sonrasi tablo 0 gosteriyor' $d['SonrakiSatirlar'][0]

$d = Dialog-Write -Form $form -Function 0 -Addr 20 -Qty 1 -Row 0 -TypeIndex 0 -Value '5'
Assert ($d['Sonuc'] -like 'HATA*hata kodu 02*') 'hata yaniti dialogda goruntulendi' $d['Sonuc']

$d = Dialog-Write -Form $form -Function 0 -Addr 3 -Qty 1 -Row 0 -TypeIndex 0 -Value '70000'
Assert ($d['Onizleme'] -like '*65535*' -and $d['YazPasif']) 'gecersiz deger: onizleme + Yaz pasif' ($d['Onizleme'] + ' / yazPasif=' + $d['YazPasif'])

$d = Dialog-Write -Form $form -Function 1 -Addr 2 -Qty 4 -Row 0 -TypeIndex 0 -Value '5'
Assert ($d['YazPasif'] -and $d['Ipucu'] -like '*salt okunur*') 'input register: Yaz pasif + uyarı' ($d['Ipucu'])

$log = [string](Get-Field $form 'txtLog').Text
Assert ($log -like '*YAZMA FC06*' -and $log -like '*YAZMA FC16*' -and $log -like '*YAZMA FC05*') `
    'yazmalar ana loga yazildi' ($log -replace "`r?`n", ' / ')

# --- Tanim sutunu: kullanicinin yazdigi metin, Excel'den yapistirma, kalici dosya ---

Set-Combo $form 'cmbFunction' 0
(Get-Field $form 'txtAddress').Text = '0'
(Get-Field $form 'txtQuantity').Text = '10'
Invoke-Handler $form 'OnRead'

$colNames = @(((Get-Field $form 'grid').Columns | ForEach-Object { $_.Name }))
Assert (($colNames -join ',') -eq 'colIndex,colAddress,colValue,colDef') `
    'sutunlar sadelesti: #/Adres/Değer/Tanım' ($colNames -join ',')

Assert (([string](Get-Field $form 'lblSummary').Text) -like '*Ctrl+V*') `
    'ozet satiri yapistirmayi andiriyor' ([string](Get-Field $form 'lblSummary').Text)

Invoke-Method $form 'ApplyDefinition' @(0, 'sicaklik set')
$rows = @(Get-GridRows $form)
Assert (@($rows[0] -split '\|')[3].Trim() -eq 'sicaklik set') 'tanim hucreye yazildi' $rows[0]

# Excel'de tek sutun kopyalaninca sonda bos satir kalir; o yok sayilmali.
# 4. satirdaki tanım kullanıcının kendi dosyasından geliyor olabilir: yapistirilmayan satir degismemeli.
$untouchedBefore = @(Get-GridRows $form | ForEach-Object { @($_ -split '\|')[3].Trim() })[3]
Invoke-Method $form 'ApplyPastedDefinitions' @("motor hizasi`nsicaklik set`nalarm`r`n")
$tanims = @(Get-GridRows $form | ForEach-Object { @($_ -split '\|')[3].Trim() })
Assert ($tanims[0] -eq 'motor hizasi' -and $tanims[1] -eq 'sicaklik set' -and `
        $tanims[2] -eq 'alarm' -and $tanims[3] -eq $untouchedBefore) `
    'excelden tek sutun yapistirildi, 4. satir korundu' (($tanims[0..3] -join ' / '))

Invoke-Method $form 'ApplyPastedDefinitions' @("baslik`t100`nalarm`t200")
$tanims = @(Get-GridRows $form | ForEach-Object { @($_ -split '\|')[3].Trim() })
Assert ($tanims[0] -eq 'baslik' -and $tanims[1] -eq 'alarm') `
    'cok sutunlu kopyada ilk sutun alindi' (($tanims[0..1] -join ' / '))

$grid = Get-Field $form 'grid'
$grid.CurrentCell = $grid.Rows[5].Cells['colDef']
Invoke-Method $form 'ApplyPastedDefinitions' @("fan`nkontrol")
$tanims = @(Get-GridRows $form | ForEach-Object { @($_ -split '\|')[3].Trim() })
Assert ($tanims[5] -eq 'fan' -and $tanims[6] -eq 'kontrol' -and $tanims[0] -eq 'baslik') `
    'yapistirma secili satirdan baslar' (($tanims[0] + '/' + $tanims[5] + '/' + $tanims[6]))

$grid.CurrentCell = $grid.Rows[0].Cells['colDef']
Invoke-Method $form 'ApplyPastedDefinitions' @(((1..14 | ForEach-Object { "satir $_" }) -join "`n"))
$log = [string](Get-Field $form 'txtLog').Text
$lastLog = (($log -split "`r?`n") | Where-Object { $_ -ne '' } | Select-Object -Last 1)
Assert ($lastLog -like '*10 satır*' -and $lastLog -like '*sığmadı*') `
    'tabloya sigmayan satirlar logda' $lastLog

# Tanimlar yeni bir okumada kaybolmamali
Invoke-Handler $form 'OnRead'
$rows = @(Get-GridRows $form)
Assert (@($rows[0] -split '\|')[3].Trim() -eq 'satir 1') 'okuma tekrarlaninca tanim korundu' $rows[0]

$defFile = Join-Path $env:APPDATA 'ModbusScan\definitions.txt'
$fileText = if (Test-Path $defFile) { [System.IO.File]::ReadAllText($defFile) } else { '' }
Assert ($fileText -like "*3`t0`tsatir 1*") 'tanim dosyasi fc/adres/metin olarak yazildi' ($fileText -replace "`r?`n", ' / ')

# Ctrl+V gercek pano yolu: pano kilitli/ersilemezse uygulama cokmemeli (son satira yazar, diger kontrolleri bozmaz)
$grid.CurrentCell = $grid.Rows[$grid.Rows.Count - 1].Cells['colDef']
$thrown = $false
try { Invoke-Method $form 'PasteDefinitions' @() } catch { $thrown = $true }
Assert (-not $thrown) 'ctrl+v pano yolu hatada cokmuyor' 'exception'

$form.Close()

$restored = [Activator]::CreateInstance($type)
(Get-Field $restored 'txtIp').Text = '127.0.0.1'
(Get-Field $restored 'txtPort').Text = "$Port"
(Get-Field $restored 'txtUnit').Text = '1'
(Get-Field $restored 'txtTimeout').Text = '2000'
Invoke-Handler $restored 'OnConnect'
Invoke-Handler $restored 'OnRead'
$rows = @(Get-GridRows $restored)
Assert (@($rows[0] -split '\|')[3].Trim() -eq 'satir 1') 'tanim yeni pencerede dosyadan geri yuklendi' `
    (($rows[0] + ' / durum=' + [string](Get-Field $restored 'lblStatus').Text))
$restored.Close()

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

function Invoke-Value($Form, [string]$Name) {
    # Paramsiz private metot (SelectedParity, SelectedStopBits) cagrisinin donus degerini alir.
    $method = $Form.GetType().GetMethod($Name, $flags)
    if ($null -eq $method) { throw "Metot bulunamadi: $Name" }
    return $method.Invoke($Form, @())
}

function Visible($Form, [string]$Name) { return [bool](Get-Field $Form $Name).Visible }

# RS485 modu: baglanti kutusunun icerigi secime gore degisiyor, seri alanlar dogru kuruluyor.
$serial = [Activator]::CreateInstance($type)
# Control.Visible ust kontrolun gorunurlugune de baktigi icin pencereyi gostermeden gorunluk test edilemez.
$serial.Show()

Assert ((Visible $serial 'txtIp') -and (Visible $serial 'txtPort') -and -not (Visible $serial 'cmbSerialPort')) `
    'RS485 varsayilani: TCP modunda IP/Port gorunur, COM gizli' 'ilk gorunum yanlis'

$gb = Get-Field $serial 'gbConnection'
(Get-Field $serial 'cmbMode').SelectedIndex = 1
Assert ((Visible $serial 'cmbSerialPort') -and (Visible $serial 'cmbBaud') -and (Visible $serial 'cmbParity') -and
        (Visible $serial 'cmbStop') -and -not (Visible $serial 'txtIp') -and -not (Visible $serial 'txtPort')) `
    'RS485 secilince seri alanlar gorunur, TCP alanlar gizli' 'ALANLAR'

Assert (((Get-Field $serial 'btnConnect').Top -lt 20) -and ((Get-Field $serial 'lblStatus').Top -ge 45)) `
    'RS485: baglan butonu baslik satirinda, durum satiri korunuyor' 'YERLESIM'

$overflow = @()
foreach ($name in @('cmbSerialPort', 'cmbBaud', 'cmbParity', 'cmbStop', 'txtUnit', 'txtTimeout', 'btnConnect', 'btnDisconnect')) {
    $c = Get-Field $serial $name
    if ($c.Right -gt $gb.Width -or $c.Bottom -gt $gb.Height -or $c.Left -lt 0) {
        $overflow += ('{0} sag={1} alt={2}' -f $name, $c.Right, $c.Bottom)
    }
}
Assert ($overflow.Count -eq 0) 'RS485 alanlari baglanti kutusuna sigiyor' ($overflow -join ', ')

Assert ((Get-Field $serial 'cmbBaud').Text -eq '9600') 'RS485 varsayilani 9600' (Get-Field $serial 'cmbBaud').Text
Assert ((Invoke-Value $serial 'SelectedParity') -eq 'None') 'RS485 varsayilan parite Yok = None' (Invoke-Value $serial 'SelectedParity')
Assert ((Invoke-Value $serial 'SelectedStopBits') -eq 'One') 'RS485 varsayilan durdurma 1 = One' (Invoke-Value $serial 'SelectedStopBits')

(Get-Field $serial 'cmbParity').SelectedIndex = 2
(Get-Field $serial 'cmbStop').SelectedIndex = 2
Assert ((Invoke-Value $serial 'SelectedParity') -eq 'Even' -and (Invoke-Value $serial 'SelectedStopBits') -eq 'Two') `
    'RS485 parite/durdurma secimi enum eslemesi' ("{0}/{1}" -f (Invoke-Value $serial 'SelectedParity'), (Invoke-Value $serial 'SelectedStopBits'))

Invoke-Method $serial 'RefreshPorts' @()
$realPorts = @([System.IO.Ports.SerialPort]::GetPortNames()).Count
Assert ((Get-Field $serial 'cmbSerialPort').Items.Count -eq $realPorts) 'COM port sorgusu sistem listesiyle ayni' `
    ("liste={0} sistem={1}" -f (Get-Field $serial 'cmbSerialPort').Items.Count, $realPorts)

$cmbPorts = Get-Field $serial 'cmbSerialPort'
$cmbPorts.SelectedIndex = -1
Invoke-Handler $serial 'OnConnect'
$serLog = [string](Get-Field $serial 'txtLog').Text
Assert ($serLog -match 'COM port' -and -not [bool](Get-Field $serial 'btnRead').Enabled `
        -and [bool](Get-Field $serial 'btnConnect').Enabled) `
    'RS485: port secsiz baglanma reddediliyor, okuma pasif, tekrar denenebilir' $serLog

Assert (Visible $serial 'txtAddress') 'RS485 modunda okuma alanlari duruyor' 'okuma alani kayboldu'

(Get-Field $serial 'cmbMode').SelectedIndex = 0
Assert ((Visible $serial 'txtIp') -and ((Get-Field $serial 'btnConnect').Top -eq 23)) `
    'TCP modune donus: IP/Port gorunur, buton eski satirinda' 'geri donus bozuk'

(Get-Field $serial 'txtIp').Text = '127.0.0.1'
(Get-Field $serial 'txtPort').Text = "$Port"
Invoke-Handler $serial 'OnConnect'
$serStatus = [string](Get-Field $serial 'lblStatus').Text
Assert ($serStatus -match [regex]::Escape("127.0.0.1:$Port")) 'TCP modune donus test sunucusuna baglaniyor' $serStatus
Assert (-not [bool](Get-Field $serial 'cmbMode').Enabled) 'bagliyken TCP/RS485 secimi kilitli' 'secim acik kaldi'
Invoke-Handler $serial 'OnDisconnect'
Assert ([bool](Get-Field $serial 'cmbMode').Enabled -and [bool](Get-Field $serial 'btnConnect').Enabled `
        -and -not [bool](Get-Field $serial 'btnRead').Enabled) 'kestikten sonra mod secimi ve baglan acik' 'durumlar yanlis'
$serial.Close()

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

if ($null -eq $defBackup) {
    if (Test-Path $defPath) { Remove-Item $defPath -Force }
} else {
    [System.IO.File]::WriteAllText($defPath, $defBackup, (New-Object System.Text.UTF8Encoding($false)))
}

Write-Host ('SONUC: {0} hata' -f $fail)
exit $fail
