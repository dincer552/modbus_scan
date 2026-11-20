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

function Close-Form($Form) {
    # Gosterilmeyen formda Close() FormClosing tetiklenmez; baglanti acik kalirsa sahte sunucu
    # eski istemcide bloklanir ve sonraki testlerin istekleri zaman asimina ugrar. Once kes.
    Invoke-Handler $Form 'OnDisconnect'
    $Form.Close()
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

# Kullanicinin tanim/ayar/tarama dosyalarina HIC dokunulmaz: MODBUSSCAN_* ortam degiskenleriyle
# testler gecici dosyalara yonlendirilir. (Yedekle-geri yukle yaklasimi, yarida kalan bir calismada
# kullanici kayitlarini bosaltabildiği icin birakildi.)
$defDir = [System.IO.Path]::Combine([System.IO.Path]::GetTempPath(), 'mscan-defs-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
[System.IO.Directory]::CreateDirectory($defDir) | Out-Null
$defPath = [System.IO.Path]::Combine($defDir, 'definitions.txt')
$env:MODBUSSCAN_DEFINITIONS = $defPath
$settingsPath = [System.IO.Path]::Combine($defDir, 'settings.txt')
$env:MODBUSSCAN_SETTINGS = $settingsPath
$scanPlanPath = [System.IO.Path]::Combine($defDir, 'scanplan.txt')
$env:MODBUSSCAN_SCANPLAN = $scanPlanPath
$userDefPath = [System.IO.Path]::Combine($env:APPDATA, 'ModbusScan', 'definitions.txt')
$userDefBefore = if (Test-Path $userDefPath) { [System.IO.File]::ReadAllBytes($userDefPath) } else { $null }
$userSettingsPath = [System.IO.Path]::Combine($env:APPDATA, 'ModbusScan', 'settings.txt')
$userSettingsBefore = if (Test-Path $userSettingsPath) { [System.IO.File]::ReadAllBytes($userSettingsPath) } else { $null }
$userScanPlanPath = [System.IO.Path]::Combine($env:APPDATA, 'ModbusScan', 'scanplan.txt')
$userScanPlanBefore = if (Test-Path $userScanPlanPath) { [System.IO.File]::ReadAllBytes($userScanPlanPath) } else { $null }

# Beklenmeyen hata: gecici klasor temizlenir, kullanici dosyasi yine de kontrol edilir.
trap {
    Write-Host ('HATA  betik kesildi: ' + $_.Exception.Message)
    if (Test-Path $defDir) { Remove-Item $defDir -Recurse -Force }
    exit 8
}

$form = [Activator]::CreateInstance($type)
(Get-Field $form 'txtIp').Text = '127.0.0.1'
(Get-Field $form 'txtPort').Text = "$Port"
(Get-Field $form 'txtUnit').Text = '1'
(Get-Field $form 'txtTimeout').Text = '2000'
Invoke-Handler $form 'OnConnect'
# Ana baglanti EBM sifresi yazmaz; normal test registerlari oldugu gibi korunur.

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
$cases += [pscustomobject]@{ Name = 'Adet limiti yok: 300 register 3 istek'; Function = 0; Decode = 0; Word = 0; Addr = '0'; Qty = '300'; Rows = 300; Cell = '3 istek' }
$cases += [pscustomobject]@{ Name = '125 register tek istek kalicyor'; Function = 0; Decode = 0; Word = 0; Addr = '0'; Qty = '125'; Rows = 125; Cell = '1 istek' }
$cases += [pscustomobject]@{ Name = 'Adet 0 reddediliyor'; Function = 0; Decode = 0; Word = 0; Addr = '0'; Qty = '0'; Rows = 0; Cell = 'Adet en az 1' }
$cases += [pscustomobject]@{ Name = 'Adres alani tasma uyarisi'; Function = 0; Decode = 0; Word = 0; Addr = '65500'; Qty = '100'; Rows = 0; Cell = '65535' }
$cases += [pscustomobject]@{ Name = 'Exception FC03/02 hucreye isaretlenir'; Function = 0; Decode = 0; Word = 0; Addr = '9000'; Qty = '1'; Rows = 1; Cell = 'ilegal hata 02' }

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

# --- Hata toleransi: yasadisi kayitlar isaretlenir, araligin geri kalani okunmaya devam eder ---

Set-Combo $form 'cmbFunction' 0
Set-Combo $form 'cmbDecode' 0
(Get-Field $form 'chkConvention').Checked = $true
(Get-Field $form 'txtAddress').Text = '350'
(Get-Field $form 'txtQuantity').Text = '200'
Invoke-Handler $form 'OnRead'

$rows = @(Get-GridRows $form)
$summary = [string](Get-Field $form 'lblSummary').Text
$values = @($rows | ForEach-Object { @($_ -split '\|')[2].Trim() })
$badCount = @($values | Where-Object { $_ -eq 'ilegal hata 02' }).Count
$read = Get-Field $form '_lastRead'

Assert ($rows.Count -eq 200) 'yasadisi kayitlar araligi bolmuyor: 200 satir doldu' ("satir=" + $rows.Count + ' ozet=' + $summary)
Assert ($values[50] -eq 'ilegal hata 02' -and $values[51] -eq 'ilegal hata 02' -and $values[100] -eq 'ilegal hata 02') `
    '400/401/450 deger sutununda ilegal olarak gorunuyor' (($values[50] + ' / ' + $values[51] + ' / ' + $values[100]))
Assert ($badCount -eq 3) 'yalnizca yasadisi 3 kayit hatali' ("hatali=" + $badCount)
Assert ($values[0] -match '^\d+$' -and $values[199] -match '^\d+$') 'hatasiz kayitlar degerleriyle dolu' (($values[0] + ' / ' + $values[199]))
Assert ($summary -like '*3 kay* hatal*') 'ozet satiri hata adedini soyluyor' $summary
Assert ([int]$read.ErrorCount -eq 3 -and $null -eq $read.StopReason) 'okuma durmadı, 3 hata isaretli' ("stop=" + $read.StopReason)
$grid0 = Get-Field $form 'grid'
Assert ($grid0.Rows[50].Cells['colValue'].Style.ForeColor.Name -eq 'Firebrick') 'hatali hucre kirmizi' `
    $grid0.Rows[50].Cells['colValue'].Style.ForeColor.Name
Assert ([string]$grid0.Rows[50].Cells['colValue'].ToolTipText -like '*ILEGAL ADRES*') 'hatali hucre ipucu tam nedeni tasıyor' `
    ([string]$grid0.Rows[50].Cells['colValue'].ToolTipText)
$logTolerant = [string](Get-Field $form 'txtLog').Text
Assert ($logTolerant -like '*OKUMA FC03 @350 x200*3 kay* hatal*') 'okuma logu hata adedini iceriyor' `
    (($logTolerant -split "`r?`n") | Where-Object { $_ -like '*@350*' } | Select-Object -Last 1)

# Yazma penceresi hatali satirda da acilmali: okunmus deger yok, bos mevcut degerle gelir.
$d = Dialog-Write -Form $form -Function 0 -Addr 350 -Qty 200 -Row 50 -TypeIndex 0 -Value '11'
Assert ($d['Acildi'] -and $d['Alan'] -like '*40401*') 'hatali satirdan yazma penceresi aciliyor' ($d['Alan'])
Assert (@($d['SonrakiSatirlar'][50] -split '\|')[2].Trim() -eq 'ilegal hata 02') 'basarisiz yazma isareti bozmuyor' $d['SonrakiSatirlar'][50]

# Cihaz hic cevap vermeyen aralikta okuma birakiyor, ama baglanti ve kalan kayitlar korunuyor.
Set-Combo $form 'cmbFunction' 0
(Get-Field $form 'txtAddress').Text = '60000'
(Get-Field $form 'txtQuantity').Text = '300'
(Get-Field $form 'txtTimeout').Text = '400'
Invoke-Handler $form 'OnRead'
$readDead = Get-Field $form '_lastRead'
$summaryDead = [string](Get-Field $form 'lblSummary').Text
Assert ($null -ne $readDead.StopReason) 'cevap vermeyen aralikta okuma duruyor' $summaryDead
Assert (@(Get-GridRows $form).Count -eq 300) 'durma sonrasi tablo yine 300 satir' ([string]@(Get-GridRows $form).Count)
Assert ($summaryDead -like '*Üst üste*') 'ozet durma nedenini kirmizi gosteriyor' $summaryDead
Assert ([string](Get-Field $form 'lblStatus').Text -like '*127.0.0.1*') 'durma baglantiyi kapatmiyor' ([string](Get-Field $form 'lblStatus').Text)
(Get-Field $form 'txtTimeout').Text = '2000'
(Get-Field $form 'txtAddress').Text = '0'
(Get-Field $form 'txtQuantity').Text = '10'
Invoke-Handler $form 'OnRead'
Assert (@(Get-GridRows $form).Count -eq 10) 'durmadan sonra normal okuma suruyor' ([string](Get-Field $form 'lblSummary').Text)

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

$d = Dialog-Write -Form $form -Function 0 -Addr 9000 -Qty 1 -Row 0 -TypeIndex 0 -Value '5'
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

$fileText = if (Test-Path $defPath) { [System.IO.File]::ReadAllText($defPath) } else { '' }
Assert ($fileText -like "*3`t0`tsatir 1*") 'tanim dosyasi fc/adres/metin olarak yazildi' (($fileText -split "`r?`n" | Where-Object { $_ -like '3`t0*' }) -join ' / ')

# Ctrl+V gercek pano yolu: pano kilitli/ersilemezse uygulama cokmemeli (son satira yazar, diger kontrolleri bozmaz)
$grid.CurrentCell = $grid.Rows[$grid.Rows.Count - 1].Cells['colDef']
$thrown = $false
try { Invoke-Method $form 'PasteDefinitions' @() } catch { $thrown = $true }
Assert (-not $thrown) 'ctrl+v pano yolu hatada cokmuyor' 'exception'

# --- Yeniden boyutlandirilabilir pencere ve sutunlar (ayni canli baglanti uzerinde) ---
Assert ($form.FormBorderStyle -eq 'Sizable') 'pencere kenardan suruklenerek boyutlandirilir' ([string]$form.FormBorderStyle)
Assert ($form.MaximizeBox) 'buyutme butonu acik' 'MaximizeBox=false'
Assert ($form.ClientSize.Width -eq 500 -and $form.ClientSize.Height -eq 600) `
    'varsayilan ic boyut 500x600 korunuyor' ("{0}x{1}" -f $form.ClientSize.Width, $form.ClientSize.Height)
Assert (($form.MinimumSize.Width - ($form.Size.Width - $form.ClientSize.Width)) -eq 500) `
    'kucultme siniri 500 px icerige kadar iniyor' ("min={0} pencere={1}" -f $form.MinimumSize.Width, $form.Size.Width)

$szGrid = Get-Field $form 'grid'
Assert ($szGrid.AllowUserToResizeColumns) 'sutunlar baslik kenarindan suruklenir' 'AllowUserToResizeColumns=false'
$locked = @($szGrid.Columns | Where-Object { $_.Resizable -ne 'True' } | ForEach-Object { $_.Name })
Assert ($locked.Count -eq 0) 'tum sutunlar yeniden boyutlanabilir' ($locked -join ',')
Assert ($szGrid.Columns['colDef'].AutoSizeMode -eq 'Fill') 'Tanim sutunu bosluklari dolduruyor (buyuyunce tablo bos kalmiyor)' `
    ([string]$szGrid.Columns['colDef'].AutoSizeMode)

$form.Show()
[System.Windows.Forms.Application]::DoEvents()
$base = @{ W = $szGrid.Width; H = $szGrid.Height; LogTop = (Get-Field $form 'txtLog').Top; LogH = (Get-Field $form 'txtLog').Height }
$form.ClientSize = New-Object System.Drawing.Size 760, 760
[System.Windows.Forms.Application]::DoEvents()
Assert (($szGrid.Width - $base.W) -ge 250 -and ($szGrid.Height - $base.H) -ge 150) `
    'buyutunce tablo hem genisler hem uzar' ("dw={0} dh={1}" -f ($szGrid.Width - $base.W), ($szGrid.Height - $base.H))
Assert ((Get-Field $form 'txtLog').Height -eq $base.LogH -and (Get-Field $form 'txtLog').Top -ge $base.LogTop + 150) `
    'log alta yapisaliyor, bos alanı tablo aliyor' ("logYukseklik={0} logUst={1}" -f (Get-Field $form 'txtLog').Height, (Get-Field $form 'txtLog').Top)
Assert ($szGrid.Bottom -le (Get-Field $form 'txtLog').Top -and (Get-Field $form 'txtLog').Bottom -le $form.ClientSize.Height - 2) `
    'buyuyen pencerede kontroller ust uste binmiyor/tasmiyor' `
    ("tabloAlt={0} logUst={1} logAlt={2} icerik={3}" -f $szGrid.Bottom, (Get-Field $form 'txtLog').Top, (Get-Field $form 'txtLog').Bottom, $form.ClientSize.Height)

$stripBad = @()
$lu = Get-Field $form 'lblUpdate'; $dc = Get-Field $form 'btnCheckUpdate'; $du = Get-Field $form 'btnUpdate'
if ($lu.Right -gt $dc.Left -or $dc.Right -gt $du.Left -or $du.Right -gt $form.ClientSize.Width - 4) {
    $stripBad += ('serit: etiketSag={0} denetleSag={1} guncelleSag={2}' -f $lu.Right, $dc.Right, $du.Right)
}
foreach ($pair in @(@('btnRead', 'gbRead'), @('btnClear', 'gbRead'), @('cmbWordOrder', 'gbRead'),
                    @('btnConnect', 'gbConnection'), @('btnDisconnect', 'gbConnection'))) {
    $c = Get-Field $form $pair[0]
    $box = Get-Field $form $pair[1]
    if ($c.Right -gt $box.Width - 2) { $stripBad += ('{0} sag={1} kutu={2}' -f $pair[0], $c.Right, $box.Width) }
}
Assert ($stripBad.Count -eq 0) 'buyuk pencerede sag butonlar etikete/kenara tasmiyor' ($stripBad -join ' | ')

# Buyutulmus pencerede parcali okuma: 200 kayit, 2 istek
(Get-Field $form 'txtAddress').Text = '0'
(Get-Field $form 'txtQuantity').Text = '200'
Invoke-Handler $form 'OnRead'
Assert (@(Get-GridRows $form).Count -eq 200) 'buyutulmus pencerede 200 kayitlik parcali okuma' `
    ([string](Get-Field $form 'lblSummary').Text)
Assert ($null -eq (Get-Field $form '_tcp').Progress) 'okuma bitince ilerleme geri cagrisi temizleniyor' `
    'Progress atanmis kaldi'

$form.ClientSize = New-Object System.Drawing.Size $form.MinimumSize.Width, $form.MinimumSize.Height
[System.Windows.Forms.Application]::DoEvents()
$displaced = @()
foreach ($name in @('gbConnection', 'gbRead', 'lblSummary', 'lblUpdate', 'grid', 'txtLog')) {
    $c = Get-Field $form $name
    if ($c.Top -lt 0 -or $c.Left -lt 0 -or $c.Height -le 0) { $displaced += ('{0}={1},{2}x{3}' -f $name, $c.Left, $c.Top, $c.Height) }
}
Assert ($displaced.Count -eq 0) 'en kucuk boyutta kontroller kaybolmuyor' ($displaced -join ',')
Assert ($szGrid.Columns['colDef'].Width -ge 10) 'Tanim sutunu cok daraltildiginda yok olmuyor' `
    ([string]$szGrid.Columns['colDef'].Width)
$form.Hide()

Close-Form $form

$restored = [Activator]::CreateInstance($type)
(Get-Field $restored 'txtIp').Text = '127.0.0.1'
(Get-Field $restored 'txtPort').Text = "$Port"
(Get-Field $restored 'txtUnit').Text = '1'
(Get-Field $restored 'txtTimeout').Text = '2000'
(Get-Field $restored 'txtAddress').Text = '0'
(Get-Field $restored 'txtQuantity').Text = '10'
Invoke-Handler $restored 'OnConnect'
Invoke-Handler $restored 'OnRead'
$rows = @(Get-GridRows $restored)
Assert (@($rows[0] -split '\|')[3].Trim() -eq 'satir 1') 'tanim yeni pencerede dosyadan geri yuklendi' `
    (($rows[0] + ' / durum=' + [string](Get-Field $restored 'lblStatus').Text))
Close-Form $restored

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

Close-Form $ui

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
# Acilir listede port adinin yanina surucu aciklamasi cizilir; saklanan deger yine sadece COM adi.
$portItems = @($cmbPorts.Items | ForEach-Object { [string]$_ })
$plainNames = @($portItems | Where-Object { $_ -notmatch '^COM\d+$' }).Count
Assert ($cmbPorts.DrawMode -eq 'OwnerDrawFixed') 'COM listesi owner-draw modunda' $cmbPorts.DrawMode
Assert ($plainNames -eq 0) 'COM listesi yalnizca port adlarini saklar' ($portItems -join ',')
Assert ($cmbPorts.DropDownWidth -ge $cmbPorts.Width) 'acilir liste kutudan dar degil' `
    ("aciklik={0} kutu={1}" -f $cmbPorts.DropDownWidth, $cmbPorts.Width)
$portLabels = Get-Field $serial '_portLabels'
$staleLabels = @($portLabels.Keys | Where-Object { $portItems -notcontains $_ }).Count
Assert ($staleLabels -eq 0 -and $portLabels.Count -le $portItems.Count) 'port açiklamalari mevcut listeyle sinirli' `
    ("etiket={0} liste={1} bayat={2}" -f $portLabels.Count, $portItems.Count, $staleLabels)
if ($portItems.Count -gt 0) {
    Assert ([string]$cmbPorts.Text -match '^COM\d+$') 'secili port kutuda sade COM adi olarak duruyor' $cmbPorts.Text
}
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
Close-Form $serial

# --- Tanim sutununu temizleme: Delete tusu, satir menuyu ve toplu temizlik ---
$clear = [Activator]::CreateInstance($type)
(Get-Field $clear 'txtIp').Text = '127.0.0.1'
(Get-Field $clear 'txtPort').Text = "$Port"
(Get-Field $clear 'txtUnit').Text = '1'
(Get-Field $clear 'txtTimeout').Text = '2000'
Invoke-Handler $clear 'OnConnect'
Set-Combo $clear 'cmbFunction' 0
(Get-Field $clear 'txtAddress').Text = '0'
(Get-Field $clear 'txtQuantity').Text = '10'
Invoke-Handler $clear 'OnRead'

$clrGrid = Get-Field $clear 'grid'
$menu = Get-Field $clear '_definitionMenu'
Assert ($null -ne $menu -and [object]::ReferenceEquals($clrGrid.ContextMenuStrip, $menu)) `
    'tablo tanim sag tik menusuna bagli' 'ContextMenuStrip atanmadi'
Assert ($menu.Items.Count -eq 2) 'menu iki temizleme secenegi iceriyor' ([string]$menu.Items.Count)
Assert ($null -ne $clrGrid.GetType().GetProperty('DeleteDefinitionHandler').GetValue($clrGrid)) `
    'Delete tusu handler ile baglanmis' 'DeleteDefinitionHandler bos'

function DefColumn($Form) { return @(Get-GridRows $Form | ForEach-Object { @($_ -split '\|')[3].Trim() }) }

Invoke-Method $clear 'ApplyDefinition' @(0, 'silinecek bir')
Invoke-Method $clear 'ApplyDefinition' @(1, 'silinecek iki')
$clrGrid.CurrentCell = $clrGrid.Rows[0].Cells['colDef']
Invoke-Method $clear 'ClearCurrentDefinition' @()
$t = DefColumn $clear
Assert ($t[0] -eq '' -and $t[1] -eq 'silinecek iki') 'Delete yolu yalnizca secili satiri siler' (($t[0] + '/' + $t[1]))
Assert ([int](Invoke-Value $clear 'FilledDefinitionRows') -ge 1) 'dolu satir sayisi sayiliyor' 'sifir dondu'

$clrGrid.CurrentCell = $clrGrid.Rows[1].Cells['colDef']
Invoke-Method $clear 'OnClearDefinitionRow' @($null, [EventArgs]::Empty)
$t = DefColumn $clear
Assert ($t[1] -eq '') 'sag tik menu tek satiri temizliyor' $t[1]

$fileText = if (Test-Path $defPath) { [System.IO.File]::ReadAllText($defPath) } else { '' }
Assert ($fileText -notlike "*3`t0`t*" -and $fileText -notlike "*3`t1`t*") 'temizlenen tanim dosyadan da dusuyor' `
    (($fileText -split "`r?`n" | Where-Object { $_ -like '3`t0*' -or $_ -like '3`t1*' }) -join ' / ')

Invoke-Method $clear 'ApplyDefinition' @(2, 'toplu silinecek')
$clrGrid.CurrentCell = $clrGrid.Rows[2].Cells['colDef']
Invoke-Method $clear 'OnDefinitionMenuOpening' @($null, [Activator]::CreateInstance([System.ComponentModel.CancelEventArgs]))
Assert ($menu.Items[0].Enabled -and $menu.Items[1].Enabled) 'yazi varken iki secenek de acik' `
    ('satir=' + $menu.Items[0].Enabled + ' butun=' + $menu.Items[1].Enabled)

Invoke-Method $clear 'ClearAllDefinitions' @()
$t = DefColumn $clear
Assert (@($t | Where-Object { $_ -ne '' }).Count -eq 0) 'toplu temizleme sutunu bosaltiyor' `
    (($t | Where-Object { $_ -ne '' }) -join ' / ')
Assert ([int](Invoke-Value $clear 'FilledDefinitionRows') -eq 0) 'toplu temizleme sonrasi dolu satir kalmadi' `
    ([string](Invoke-Value $clear 'FilledDefinitionRows'))
Assert (([string](Get-Field $clear 'lblSummary').Text) -like '*temizlendi*') 'ozet temizlemeyi bildiriyor' `
    ([string](Get-Field $clear 'lblSummary').Text)
$fileText = if (Test-Path $defPath) { [System.IO.File]::ReadAllText($defPath) } else { '' }
Assert ($fileText -notlike "*3`t2`t*") 'toplu temizlik dosyaya da yansidi' (($fileText -replace "`r?`n", ' / '))

Invoke-Method $clear 'OnDefinitionMenuOpening' @($null, [Activator]::CreateInstance([System.ComponentModel.CancelEventArgs]))
Assert (-not $menu.Items[0].Enabled -and -not $menu.Items[1].Enabled) 'sutun bosken secenekler pasif' `
    ('satir=' + $menu.Items[0].Enabled + ' butun=' + $menu.Items[1].Enabled)
Close-Form $clear

# --- Simge (ikon) ve kisayol menuyu ---
# Kisayollar KULLANICININ MASASTUSUNE YAZILMAZ: gecici klasore yazilip silinir.
function Invoke-Static([string]$Name, $Arguments) {
    # Private static metot: yansitma cagrisinda elemanlar sade [string]/[int] olmali, PSObject sarmalanirsa hata verir.
    $method = $type.GetMethod($Name, [Reflection.BindingFlags]'NonPublic,Static')
    if ($null -eq $method) { throw "Statik metot bulunamadi: $Name" }
    return $method.Invoke($null, $Arguments)
}

$icoPath = (Resolve-Path "$PSScriptRoot\..\assets\ModbusScan.ico").Path
$icoBytes = [System.IO.File]::ReadAllBytes($icoPath)
$icoCount = [BitConverter]::ToUInt16($icoBytes, 4)
$icoType = [BitConverter]::ToUInt16($icoBytes, 2)
Assert ($icoBytes[0] -eq 0 -and $icoBytes[1] -eq 0 -and $icoType -eq 1 -and $icoCount -ge 4) `
    'ico dosyasi gecerli ve cok boyutlu' ("tip=$icoType boyut=$icoCount")

$iconForm = [Activator]::CreateInstance($type)
$winIcon = $iconForm.Icon
Assert ($null -ne $winIcon) 'pencere simgesi atanmis' 'Icon = null'
Assert ($winIcon.Width -eq 32 -and $winIcon.Height -eq 32) 'pencere simgesi 32x32' `
    ("{0}x{1}" -f $winIcon.Width, $winIcon.Height)

$appMenu = Get-Field $iconForm '_appMenu'
Assert ((Get-Field $iconForm 'grid').ContextMenuStrip -ne $appMenu -and $iconForm.ContextMenuStrip -eq $appMenu) `
    'pencere sag tik menusu kisayol menuyu' ('form menuyesi eslesmedi')
Assert ($appMenu.Items.Count -eq 2) 'kisayol menuyu iki secenek iceriyor' ("sayi=" + $appMenu.Items.Count)
Assert ($appMenu.Items[0].Text.StartsWith('Masa') -and $appMenu.Items[1].Text.StartsWith('K')) `
    'menu metinleri kisayol islemlerini aciyor' ($appMenu.Items[0].Text + ' / ' + $appMenu.Items[1].Text)
Assert ((Get-Field $iconForm 'lblSummary').ContextMenuStrip -eq $appMenu) 'ozet satiri da ayni menusuya bagli' 'baglantisi yok'

$exePath = (Resolve-Path "$PSScriptRoot\..\dist\ModbusScan.exe").Path
$salt = [Guid]::NewGuid().ToString('N').Substring(0, 8)
# Combine ile: Join-Path'e metot grubu verilince PSObject sarmali uretiyor, yansitma cagrisi bunu kabul etmiyor.
$tmpRoot = [System.IO.Path]::GetTempPath()
$tmpDesk = [System.IO.Path]::Combine($tmpRoot, 'mscan-desk-' + $salt)
$tmpStart = [System.IO.Path]::Combine($tmpRoot, 'mscan-start-' + $salt)
try {
    $written = Invoke-Static 'CreateShortcutsIn' @([string]$tmpDesk, [string]$tmpStart, [string]$exePath)
    Assert ($written.Count -eq 2) 'iki kisayol yazildi' ("yazan=" + ($written -join ','))
    Assert ((Test-Path $written[0]) -and (Test-Path $written[1])) 'kisayol dosyalari diskte' 'eksik dosya'

    $shell = New-Object -ComObject WScript.Shell
    $link = $shell.CreateShortcut($written[0])
    Assert ($link.TargetPath -eq $exePath) 'kisayol hedefi exe yi gosteriyor' ($link.TargetPath)
    Assert ($link.IconLocation -eq ($exePath + ',0')) 'kisayol simgesi exe den aliniyor' ($link.IconLocation)
    Assert ($link.WorkingDirectory -eq (Split-Path $exePath -Parent)) 'calisma klasoru exe klasoru' ($link.WorkingDirectory)
    Assert ([string]::IsNullOrEmpty($link.Description) -eq $false) 'kisayol aciklamasi dolu' 'aciklama bos'
    [void][System.Runtime.InteropServices.Marshal]::ReleaseComObject($shell)

    $removed = Invoke-Static 'RemoveShortcutsIn' @([string]$tmpDesk, [string]$tmpStart)
    Assert ($removed -eq 2 -and -not (Test-Path $written[0]) -and -not (Test-Path $written[1])) `
        'kisayollar kaldiriliyor' ("silinen=$removed")
    $again = Invoke-Static 'RemoveShortcutsIn' @([string]$tmpDesk, [string]$tmpStart)
    Assert ($again -eq 0) 'kisayol yokken kaldiirme sessiz' ("silinen=$again")
} finally {
    foreach ($d in @($tmpDesk, $tmpStart)) { if (Test-Path $d) { Remove-Item $d -Recurse -Force } }
}

$tmpReal = [System.IO.Path]::Combine($tmpRoot, 'mscan-real-' + $salt + '.exe')
$isTemp = [bool](Invoke-Static 'IsTemporaryLocation' @([string]$tmpReal))
$isRepo = [bool](Invoke-Static 'IsTemporaryLocation' @([string]$exePath))
Assert ($isTemp -and -not $isRepo) 'gecici klasor hedefi ayirdediliyor' ("temp=$isTemp depo=$isRepo")
Close-Form $iconForm

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
    Close-Form $offline
}

# --- Tarama paneli: ac/kapa, segment ekleme, kalicilik ve TARA ---
function Read-Reg($Form, [int]$Addr) {
    # Sahte sunucudan tek register okur (EBM yazmalarini dogrulamak icin).
    $tcp = Get-Field $Form '_tcp'
    $m = $tcp.GetType().GetMethod('ReadTolerant')
    $r = $m.Invoke($tcp, @([byte]3, $Addr, 1, 2000))
    if ($null -eq $r -or $r.Failed(0)) { return -1 }
    return ([int]$r.Data[0] -shl 8) -bor [int]$r.Data[1]
}

function Get-ScanRows($Form) {
    $g = Get-Field $Form 'scanResultGrid'
    $rows = @()
    foreach ($row in $g.Rows) {
        $cells = @()
        foreach ($col in @('rDevice', 'rAddress', 'rValue', 'rDef')) {
            $cells += [string]$row.Cells[$col].Value
        }
        $rows += ($cells -join ' | ')
    }
    return $rows
}

$scan = [Activator]::CreateInstance($type)
$scan.Show()
(Get-Field $scan 'txtIp').Text = '127.0.0.1'
(Get-Field $scan 'txtPort').Text = "$Port"
(Get-Field $scan 'txtUnit').Text = '1'
(Get-Field $scan 'txtTimeout').Text = '2000'
Invoke-Handler $scan 'OnConnect'
# Normal test kayitlarini bu bolumden onceki yazma testlerinden kalan duruma gore geri yukle.
$tcpS = Get-Field $scan '_tcp'
$mS = $tcpS.GetType().GetMethod('WriteSingleRegister')
$mS.Invoke($tcpS, @([byte]1, 0xF5C3, 2000))
$mS.Invoke($tcpS, @([byte]2, 0xFFFF, 2000))
$mS.Invoke($tcpS, @([byte]3, 1234, 2000))
(Get-Field $scan 'txtAddress').Text = '0'
(Get-Field $scan 'txtQuantity').Text = '5'
Set-Combo $scan 'cmbFunction' 0

Assert (-not (Visible $scan 'gbScan')) 'tarama paneli varsayilan olarak gizli' 'gorunur'
Invoke-Handler $scan 'OnScanToggle'
Assert ((Visible $scan 'gbScan')) 'Tarama dugmesi paneli aciyor' 'gizli kaldi'
Invoke-Handler $scan 'OnScanAdd'
$sg = Get-Field $scan 'scanGrid'
Assert ($sg.RowCount -eq 1) 'segment eklendi' ([string]$sg.RowCount)
Assert ([string]$sg.Rows[0].Cells['scStart'].Value -eq '0' -and [string]$sg.Rows[0].Cells['scQty'].Value -eq '5') `
    'segment, okuma alanlarindaki adres/adet ile olusuyor' `
    ('start=' + $sg.Rows[0].Cells['scStart'].Value + ' qty=' + $sg.Rows[0].Cells['scQty'].Value)
Assert ((Test-Path $scanPlanPath)) 'tarama plani dosyaya yazildi' 'dosya yok'
Assert ((Get-Field $scan 'grid').Top -gt (Get-Field $scan 'gbScan').Bottom -and
        (Get-Field $scan 'lblUpdate').Top -gt (Get-Field $scan 'gbScan').Bottom) `
    'panel acikken serit ve tablo asagi kayiyor, cakisma yok' `
    ('panelAlt=' + (Get-Field $scan 'gbScan').Bottom + ' tabloUst=' + (Get-Field $scan 'grid').Top)

(Get-Field $scan 'txtScanUnitStart').Text = '1'
(Get-Field $scan 'txtScanUnitEnd').Text = '3'
(Get-Field $scan 'txtScanTimeout').Text = '300'
Invoke-Handler $scan 'OnScanRun'
$waited = 0
$liveSeen = $false
$stopBtnSeen = $false
while (-not (Get-Field $scan 'btnScanRun').Enabled -and $waited -lt 20000) {
    [System.Windows.Forms.Application]::DoEvents()
    if ([string](Get-Field $scan 'lblScanLive').Text -like '*Taran*TCP*ID*') { $liveSeen = $true }
    if ((Get-Field $scan 'btnScanStop').Visible) { $stopBtnSeen = $true }
    Start-Sleep -Milliseconds 50
    $waited += 50
}
Assert $liveSeen 'canli satir tarama sirasinda mod + ID gosteriyor' 'gorulmedi'
Assert $stopBtnSeen 'durdur dugmesi tarama sirasinda gorunur' 'gorunmedi'
$scanRows = @(Get-ScanRows $scan)
Assert ($scanRows.Count -eq 7 -and $scanRows[0] -like 'ID 1*cihaz bulundu*') `
    'TARA: bulunan cihaz basligi + segment + 5 kayit sonuc tablosunda' ('satir=' + $scanRows.Count + ' ilk=' + $scanRows[0])
Assert ((Visible $scan 'scanResultGrid') -and -not (Visible $scan 'grid') -and (Visible $scan 'btnScanBack')) `
    'tarama sonuclari gosterilirken normal register tablosu gizli' 'gorunurluk yanlis'
Assert ([string](Get-Field $scan 'lblSummary').Text -like '*1 cihaz bulundu*3 ID*') `
    'ozet: kac cihaz bulundugu ve taranan ID sayisi' ([string](Get-Field $scan 'lblSummary').Text)
Assert ([string](Get-Field $scan 'lblScanLive').Text -like '*Tarama bitti*1 cihaz*3 ID*') `
    'alt canli satir: bitis ozeti' ([string](Get-Field $scan 'lblScanLive').Text)
Assert (-not (Visible $scan 'btnScanStop')) `
    'durdur dugmesi tarama bitince gizlenir' 'gorunur kaldi'
Assert ((Visible $scan 'btnScanRun')) `
    'tara dugmesi tarama bitince tekrar gorunur' 'gizli kaldi'
Assert ((Get-Field $scan '_tcp').UnitId -eq 1) 'tarama sonrasi unite id eski degerine donuyor' `
    ([string](Get-Field $scan '_tcp').UnitId)
Invoke-Handler $scan 'OnScanBack'
Assert ((Visible $scan 'grid') -and -not (Visible $scan 'scanResultGrid')) `
    'Tabloya don: register tablosu geri geliyor' 'geri donus bozuk'

$scan2 = [Activator]::CreateInstance($type)
$scan2.Show()
Assert ((Visible $scan2 'gbScan') -and (Get-Field $scan2 'scanGrid').RowCount -eq 1) `
    'tarama plani yeni pencerede dosyadan geri yuklendi' 'panel/segment yok'
Close-Form $scan2

Invoke-Handler $scan 'OnScanToggle'
Assert (-not (Visible $scan 'gbScan') -and (Get-Field $scan 'grid').Top -eq 254) `
    'panel kapaninca duzen eski haline donuyor' ('tabloUst=' + (Get-Field $scan 'grid').Top)
Close-Form $scan

# --- Seri parametre secimi: baud/parite/stop checkbox'lari ve kombinasyon toplama (RS485) ---
$sc = [Activator]::CreateInstance($type)
$sc.Show()
Assert (-not (Visible $sc 'scanSerial')) 'TCP modunda seri secim paneli gizli' 'gorunur'
Set-Combo $sc 'cmbMode' 1
Assert ((Visible $sc 'scanSerial')) 'RS485 modunda seri secim paneli gorunur' 'gizli'
Assert ((Visible $sc 'gbScan')) 'seri test: tarama paneli acik (plan dosyasindan)' 'acilmadi'

$baudChecks = Get-Field $sc '_scanBaudChecks'
$parChecks = Get-Field $sc '_scanParityChecks'
$stopChecks = Get-Field $sc '_scanStopChecks'
foreach ($cb in $baudChecks) { $cb.Checked = $false }
foreach ($cb in $parChecks) { $cb.Checked = $false }
foreach ($cb in $stopChecks) { $cb.Checked = $false }
$baudChecks[3].Checked = $true   # 9600
$baudChecks[4].Checked = $true   # 19200
$parChecks[0].Checked = $true    # Yok
$parChecks[2].Checked = $true    # Cift
$stopChecks[0].Checked = $true   # 1
$stopChecks[2].Checked = $true   # 2

$mCollect = $sc.GetType().GetMethod('CollectSerialCombos', $flags)
$combos = $mCollect.Invoke($sc, @())
Assert ($combos.Count -eq 8) '2 baud x 2 parite x 2 stop = 8 kombinasyon' ('adet=' + $combos.Count)
Assert ([string]$combos[0].Text -eq '9600 Yok 1') 'ilk kombinasyon metni dogru' ([string]$combos[0].Text)
Assert ($combos[0].Baud -eq 9600 -and $combos[7].Baud -eq 19200) 'kombinasyon baud degerleri dogru' 'baud yanlis'

Set-Combo $sc 'cmbMode' 0
Assert (-not (Visible $sc 'scanSerial')) 'TCP moduna donunce seri paneli tekrar gizleniyor' 'gorunur'
Close-Form $sc

$sc2 = [Activator]::CreateInstance($type)
$sc2.Show()
Set-Combo $sc2 'cmbMode' 1
$baudChecks2 = Get-Field $sc2 '_scanBaudChecks'
$parChecks2 = Get-Field $sc2 '_scanParityChecks'
$stopChecks2 = Get-Field $sc2 '_scanStopChecks'
Assert ($baudChecks2[3].Checked -and $baudChecks2[4].Checked -and -not $baudChecks2[0].Checked) `
    'seri secimleri yeni pencerede geri yuklendi (baud)' 'baud kaliciligi bozuk'
Assert ($parChecks2[0].Checked -and $parChecks2[2].Checked -and -not $parChecks2[1].Checked) `
    'seri secimleri yeni pencerede geri yuklendi (parite)' 'parite kaliciligi bozuk'
Assert ($stopChecks2[0].Checked -and $stopChecks2[2].Checked -and -not $stopChecks2[1].Checked) `
    'seri secimleri yeni pencerede geri yuklendi (stop)' 'stop kaliciligi bozuk'
Set-Combo $sc2 'cmbMode' 0
Close-Form $sc2

# --- Segmentsiz tarama: tablo bosken TARA sadece "cihaz yanit veriyor mu" kontrolu yapar ---
$po = [Activator]::CreateInstance($type)
$po.Show()
(Get-Field $po 'txtIp').Text = '127.0.0.1'
(Get-Field $po 'txtPort').Text = "$Port"
(Get-Field $po 'txtUnit').Text = '1'
(Get-Field $po 'txtTimeout').Text = '2000'
Invoke-Handler $po 'OnConnect'
$segsList = $po.GetType().GetField('_scanSegments', $flags).GetValue($po)
$segsList.Clear()
(Get-Field $po 'scanGrid').RowCount = 0
Invoke-Method $po 'SaveScanPlan' @()
Invoke-Method $po 'UpdateScanPanelSize' @()
Assert ((Get-Field $po 'scanGrid').RowCount -eq 0) 'segmentsiz tarama: plan bosaltildi' 'segment kaldi'
(Get-Field $po 'txtScanUnitStart').Text = '1'
(Get-Field $po 'txtScanUnitEnd').Text = '3'
(Get-Field $po 'txtScanTimeout').Text = '300'
Invoke-Handler $po 'OnScanRun'
$waited = 0
$poLive = $false
while (-not (Get-Field $po 'btnScanRun').Enabled -and $waited -lt 20000) {
    [System.Windows.Forms.Application]::DoEvents()
    if ([string](Get-Field $po 'lblScanLive').Text -like '*Taran*TCP*ID*var/yok*') { $poLive = $true }
    Start-Sleep -Milliseconds 50
    $waited += 50
}
Assert $poLive 'segmentsiz canli satir: var/yok kontrolu gosteriliyor' 'gorulmedi'
$poRows = @(Get-ScanRows $po)
Assert ($poRows.Count -eq 1 -and $poRows[0] -like 'ID 1*cihaz bulundu*') `
    'segmentsiz TARA: sadece cihaz basligi (register okumadan)' ('satir=' + $poRows.Count + ' ilk=' + $poRows[0])
Assert ([string](Get-Field $po 'lblSummary').Text -like '*1 cihaz bulundu*3 ID*') `
    'segmentsiz ozet: 1 cihaz / 3 ID' ([string](Get-Field $po 'lblSummary').Text)
Close-Form $po

# --- EBM fan adresleme: %25 start, adres kaydi, geri donus (yalnizca sahte sunucuya yazilir) ---
$ebm = [Activator]::CreateInstance($type)
$ebm.Show()
(Get-Field $ebm 'txtIp').Text = '127.0.0.1'
(Get-Field $ebm 'txtPort').Text = "$Port"
(Get-Field $ebm 'txtUnit').Text = '1'
(Get-Field $ebm 'txtTimeout').Text = '2000'
Invoke-Handler $ebm 'OnConnect'

Invoke-Handler $ebm 'OnEbmEnter'
Assert ((Visible $ebm 'gbEbm') -and -not (Visible $ebm 'gbScan')) 'EBM paneli aciliyor, tarama paneli gizleniyor' 'gorunurluk yanlis'
Assert ([string](Get-Field $ebm 'txtEbmStatus').Text -like '*D100*') 'mevcut adres D100 den okunup raporlaniyor' `
    ([string](Get-Field $ebm 'txtEbmStatus').Text)

# Normal okuma ve EBM paneli ayni 53530 registerini ayni degerle gostermeli.
Set-Combo $ebm 'cmbFunction' 0
(Get-Field $ebm 'txtAddress').Text = '53530'
(Get-Field $ebm 'txtQuantity').Text = '1'
Invoke-Handler $ebm 'OnRead'
$normalRows = @(Get-GridRows $ebm)
Assert ($normalRows.Count -eq 1 -and $normalRows[0] -like '*| 3000 |*') `
    'normal okuma: FC03 53530 degeri 3000' ($normalRows -join '; ')
Invoke-Handler $ebm 'OnEbmConnectAndRead'
Assert ([string](Get-Field $ebm 'lblEbmRpm').Text -like '*53530*3000*') `
    'EBM Baglan: 53530 RPM degeri 3000 olarak gosteriliyor' ([string](Get-Field $ebm 'lblEbmRpm').Text)
Assert ([string](Get-Field $ebm 'txtEbmStatus').Text -like '*RPM (53530) = 3000*') `
    'EBM Baglan: RPM durumu 53530 = 3000 bildiriyor' ([string](Get-Field $ebm 'txtEbmStatus').Text)

# Normal okuma FC04 53270 ham=168 gosterir; EBM paneli 168/10 = 16,8 C gostermeli.
Set-Combo $ebm 'cmbFunction' 1
(Get-Field $ebm 'txtAddress').Text = '53270'
(Get-Field $ebm 'txtQuantity').Text = '1'
Invoke-Handler $ebm 'OnRead'
$normalTempRows = @(Get-GridRows $ebm)
Assert ($normalTempRows.Count -eq 1 -and $normalTempRows[0] -like '*| 168 |*') `
    'normal okuma: FC04 53270 sicaklik ham degeri 168' ($normalTempRows -join '; ')
Invoke-Handler $ebm 'OnEbmConnectAndRead'
Assert ([string](Get-Field $ebm 'lblEbmSicaklikDeger').Text -match '^16[.,]8 °C$') `
    'EBM Baglan: FC04 53270 ham 168 degeri 16,8 C gosteriliyor' ([string](Get-Field $ebm 'lblEbmSicaklikDeger').Text)
Assert ([string](Get-Field $ebm 'txtEbmStatus').Text -like '*FC04 53270*16?8*ham=168*') `
    'EBM Baglan: sicaklik durumu 53270 ham 168 / 10 = 16,8 C bildiriyor' ([string](Get-Field $ebm 'txtEbmStatus').Text)

(Get-Field $ebm 'txtEbmFoundAddr').Text = '300'
Invoke-Handler $ebm 'OnEbmSaveAddr'
Assert ([string](Get-Field $ebm 'txtEbmStatus').Text -like '*1..247*' -and (Visible $ebm 'gbEbm')) `
    'gecersiz adres (300) reddediliyor, panel acik kaliyor' ([string](Get-Field $ebm 'txtEbmStatus').Text)

(Get-Field $ebm 'txtEbmFoundAddr').Text = '42'
Invoke-Handler $ebm 'OnEbmStart25'
Assert ((Read-Reg $ebm 1) -eq 16000 -and (Read-Reg $ebm 257) -eq 1) `
    '%25 start: D001=16000 ve D101=1 yazildi' `
    ('D001=' + (Read-Reg $ebm 1) + ' D101=' + (Read-Reg $ebm 257) + ' durum=' + [string](Get-Field $ebm 'txtEbmStatus').Text)

Invoke-Handler $ebm 'OnEbmSaveAddr'
Assert ((Read-Reg $ebm 256) -eq 42 -and (Read-Reg $ebm 12) -eq 0 -and (Read-Reg $ebm 1) -eq 0 -and (Read-Reg $ebm 257) -eq 1) `
    'kaydet: D100=42, D00C=0, D001=0 (dur), D101=1' `
    ('D100=' + (Read-Reg $ebm 256) + ' D00C=' + (Read-Reg $ebm 12) + ' D001=' + (Read-Reg $ebm 1))
Assert (-not (Visible $ebm 'gbEbm')) 'kayit sonrasi EBM modundan cikiliyor' 'panel acik kaldi'
$defText = if (Test-Path $defPath) { [System.IO.File]::ReadAllText($defPath) } else { '' }
Assert ($defText -like "*3`t256`tEBM*") 'EBM register tanimlari dosyaya kaydedildi' 'tanim yok'

Invoke-Handler $ebm 'OnEbmEnter'
Invoke-Handler $ebm 'OnEbmBack'
Assert (-not (Visible $ebm 'gbEbm') -and (Read-Reg $ebm 1) -eq 0) 'Geri Don: panel kapaniyor, fan durduruluyor' 'durum yanlis'
# EBM yazmalari reg1/reg12 yi degistirdi; float desen testleri bozulmasin diye ilk degerlere geri al.
$tcpE = Get-Field $ebm '_tcp'
$wm = $tcpE.GetType().GetMethod('WriteSingleRegister')
$wm.Invoke($tcpE, @(1, 0xF5C3, 2000)) | Out-Null
$wm.Invoke($tcpE, @(12, 0xF5C3, 2000)) | Out-Null
Close-Form $ebm

# --- Ayar kaliciligi: son girilen degerler yeniden baslayinca geri geliyor ---
$setA = [Activator]::CreateInstance($type)
$setA.Show()   # FormClosing yalnizca gosterilmis formda tetiklenir
(Get-Field $setA 'txtIp').Text = '10.20.30.40'
(Get-Field $setA 'txtPort').Text = '5030'
(Get-Field $setA 'txtUnit').Text = '9'
(Get-Field $setA 'txtTimeout').Text = '1500'
(Get-Field $setA 'cmbMode').SelectedIndex = 1
Set-Combo $setA 'cmbBaud' 4
Set-Combo $setA 'cmbParity' 2
Set-Combo $setA 'cmbStop' 2
Set-Combo $setA 'cmbFunction' 1
(Get-Field $setA 'txtAddress').Text = '100'
(Get-Field $setA 'txtQuantity').Text = '25'
Set-Combo $setA 'cmbDecode' 2
Set-Combo $setA 'cmbWordOrder' 1
(Get-Field $setA 'chkConvention').Checked = $false
Close-Form $setA   # OnFormClosing ayarlari kaydeder

$setB = [Activator]::CreateInstance($type)
$ok = ((Get-Field $setB 'txtIp').Text -eq '10.20.30.40') -and
      ((Get-Field $setB 'txtPort').Text -eq '5030') -and
      ((Get-Field $setB 'txtUnit').Text -eq '9') -and
      ((Get-Field $setB 'txtTimeout').Text -eq '1500') -and
      ((Get-Field $setB 'cmbMode').SelectedIndex -eq 1) -and
      ((Get-Field $setB 'cmbBaud').Text -eq '19200') -and
      ((Get-Field $setB 'cmbParity').Text -eq 'Çift') -and
      ((Get-Field $setB 'cmbStop').Text -eq '2') -and
      ((Get-Field $setB 'cmbFunction').SelectedIndex -eq 1) -and
      ((Get-Field $setB 'txtAddress').Text -eq '100') -and
      ((Get-Field $setB 'txtQuantity').Text -eq '25') -and
      ((Get-Field $setB 'cmbDecode').SelectedIndex -eq 2) -and
      ((Get-Field $setB 'cmbWordOrder').SelectedIndex -eq 1) -and
      (-not (Get-Field $setB 'chkConvention').Checked)
Assert $ok 'ayarlar (ip/port/mod/baud/okuma) yeni pencerede geri yuklendi' `
    ('ip=' + (Get-Field $setB 'txtIp').Text + ' mod=' + (Get-Field $setB 'cmbMode').SelectedIndex + ' baud=' + (Get-Field $setB 'cmbBaud').Text)
Close-Form $setB
# Sonraki formlar varsayilanlarla baslasin: gecici ayar dosyasini kaldir.
if (Test-Path $settingsPath) { Remove-Item $settingsPath -Force }

# --- EBM Baglan: sirali FC06 sifre yazma + 53270 sicaklik testi ---
$tst = [Activator]::CreateInstance($type)
$tst.Show()
(Get-Field $tst 'cmbMode').SelectedIndex = 0
(Get-Field $tst 'txtIp').Text = '127.0.0.1'
(Get-Field $tst 'txtPort').Text = "$Port"
(Get-Field $tst 'txtUnit').Text = '1'
(Get-Field $tst 'txtTimeout').Text = '2000'
Invoke-Handler $tst 'OnConnect'
Invoke-Handler $tst 'OnEbmEnter'
Invoke-Handler $tst 'OnEbmConnectAndRead'
$tstS2 = [string](Get-Field $tst 'txtEbmStatus').Text
$i1 = $tstS2.IndexOf('FC06 53250=25968 gönderildi.')
$i2 = $tstS2.IndexOf('FC06 53251=19744 gönderildi.')
$i3 = $tstS2.IndexOf('FC06 53252=17731 gönderildi.')
Assert ($i1 -ge 0 -and $i2 -gt $i1 -and $i3 -gt $i2) `
    'EBM Baglan: sifreler FC06 53250/53251/53252 sirasiyla gonderildi' ('durum=' + $tstS2)
$tcpT = Get-Field $tst '_tcp'
$readT = $tcpT.GetType().GetMethod('Read')
$pw1 = $readT.Invoke($tcpT, @([byte]3, 53250, 1, 2000))
$pw2 = $readT.Invoke($tcpT, @([byte]3, 53251, 1, 2000))
$pw3 = $readT.Invoke($tcpT, @([byte]3, 53252, 1, 2000))
$v1 = ([int]$pw1.Data[0] * 256) + [int]$pw1.Data[1]
$v2 = ([int]$pw2.Data[0] * 256) + [int]$pw2.Data[1]
$v3 = ([int]$pw3.Data[0] * 256) + [int]$pw3.Data[1]
Assert ($v1 -eq 25968 -and $v2 -eq 19744 -and $v3 -eq 17731) `
    'EBM sifre degerleri 25968/19744/17731 registerlara yazildi' ("degerler=$v1/$v2/$v3")
Assert ($tstS2 -like '*FC04 53270*16?8*ham=168*') 'EBM FC04 53270 ham 168 / 10 sicakligi okundu' ('durum=' + $tstS2)
Close-Form $tst

# Kullanicinin kendi dosyalarina dokunulmadigini dogrula ve gecici klasoru topla.
$userDefAfter = if (Test-Path $userDefPath) { [System.IO.File]::ReadAllBytes($userDefPath) } else { $null }
$untouched = ($null -eq $userDefBefore -and $null -eq $userDefAfter)
if (-not $untouched -and $null -ne $userDefBefore -and $null -ne $userDefAfter) {
    $untouched = ([System.BitConverter]::ToString($userDefBefore) -eq [System.BitConverter]::ToString($userDefAfter))
}
Assert $untouched 'kullanici tanim dosyasina dokunulmadi' ('farkli baytlar: ' + $userDefBefore.Length + ' -> ' + $userDefAfter.Length)

foreach ($pair in @(@('ayar', $userSettingsPath, $userSettingsBefore), @('tarama plani', $userScanPlanPath, $userScanPlanBefore))) {
    $after = if (Test-Path $pair[1]) { [System.IO.File]::ReadAllBytes($pair[1]) } else { $null }
    $same = ($null -eq $pair[2] -and $null -eq $after)
    if (-not $same -and $null -ne $pair[2] -and $null -ne $after) {
        $same = ([System.BitConverter]::ToString($pair[2]) -eq [System.BitConverter]::ToString($after))
    }
    Assert $same ('kullanici ' + $pair[0] + ' dosyasina dokunulmadi') 'farkli baytlar'
}

if (Test-Path $defDir) { Remove-Item $defDir -Recurse -Force }

Write-Host ('SONUC: {0} hata' -f $fail)
exit $fail
