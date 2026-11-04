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
