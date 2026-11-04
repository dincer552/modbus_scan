param(
    [int]$Port = 5020,
    [byte]$UnitId = 1
)

# Test amacli sahte Modbus TCP sunucusu. Gercek cihaz yokken istemciyi dogrulamak icin kullanilir.
$ErrorActionPreference = 'Stop'

$script:Holding = New-Object 'byte[]' 40
$script:Coils = @{}
function Set-Reg([int]$Index, [int]$Value) {
    $script:Holding[$Index * 2] = [byte](($Value -shr 8) -band 0xFF)
    $script:Holding[$Index * 2 + 1] = [byte]($Value -band 0xFF)
}
# reg0..1 = 40 48 F5 C3 -> float 3.14 (ABCD)
Set-Reg 0 0x4048; Set-Reg 1 0xF5C3
Set-Reg 2 0xFFFF          # 65535 / -1
Set-Reg 3 1234
Set-Reg 4 0x0001          # reg4..5 = uint32 100000 (ABCD)
Set-Reg 5 0x86A0
for ($i = 6; $i -lt 20; $i++) { Set-Reg $i ($i * 10) }
# reg12..13 = F5 C3 40 48 -> CDAB sirasiyla okununca float 3.14
Set-Reg 12 0xF5C3; Set-Reg 13 0x4048

function Read-Exact($Stream, [int]$Count) {
    $buf = New-Object 'byte[]' $Count
    $off = 0
    while ($off -lt $Count) {
        $n = $Stream.Read($buf, $off, $Count - $off)
        if ($n -le 0) { return $null }
        $off += $n
    }
    return ,$buf
}

function Send-Frame($Stream, [byte[]]$Tid, [byte]$Unit, [byte[]]$Pdu) {
    $len = $Pdu.Length + 1
    $head = [byte[]]@(
        ([int]$Tid[0]), ([int]$Tid[1]), 0, 0,
        (($len -shr 8) -band 0xFF), ($len -band 0xFF), [int]$Unit
    )
    $frame = New-Object 'byte[]' ($head.Length + $Pdu.Length)
    [Array]::Copy($head, 0, $frame, 0, $head.Length)
    [Array]::Copy($Pdu, 0, $frame, $head.Length, $Pdu.Length)
    $Stream.Write($frame, 0, $frame.Length)
    $Stream.Flush()
}

function Build-ReadResponse([byte]$Fc, [byte[]]$Data) {
    $pdu = New-Object 'byte[]' ($Data.Length + 2)
    $pdu[0] = $Fc
    $pdu[1] = [byte]$Data.Length
    if ($Data.Length -gt 0) { [Array]::Copy($Data, 0, $pdu, 2, $Data.Length) }
    return $pdu
}

function Build-Error([byte]$Fc, [byte]$Code) {
    return [byte[]]@((($Fc -bor 0x80) -band 0xFF), $Code)
}

function Get-Slice16([int]$Start, [int]$Qty, [byte[]]$Source) {
    $out = New-Object 'byte[]' ($Qty * 2)
    for ($i = 0; $i -lt $Qty; $i++) {
        $src = ($Start + $i) * 2
        if ($src + 1 -lt $Source.Length) {
            $out[$i * 2] = $Source[$src]
            $out[$i * 2 + 1] = $Source[$src + 1]
        } else {
            $out[$i * 2] = [byte](($src -shr 8) -band 0xFF)
            $out[$i * 2 + 1] = [byte]($src -band 0xFF)
        }
    }
    return $out
}

function Get-Bits([int]$Start, [int]$Qty) {
    $bytes = New-Object 'byte[]' ([int][Math]::Ceiling($Qty / 8.0))
    for ($i = 0; $i -lt $Qty; $i++) {
        $index = $Start + $i
        if ($script:Coils.ContainsKey($index)) { $bit = [int]$script:Coils[$index] -eq 1 }
        else { $bit = ($index % 3) -eq 0 }
        if ($bit) {
            $byteIndex = [int][Math]::Floor($i / 8)
            $bytes[$byteIndex] = $bytes[$byteIndex] -bor (1 -shl ($i % 8))
        }
    }
    return $bytes
}

function Set-Holding([int]$Address, [int]$Value) {
    if ($Address * 2 + 1 -ge $script:Holding.Length) { return $false }
    Set-Reg $Address $Value
    return $true
}

$listener = New-Object System.Net.Sockets.TcpListener([System.Net.IPAddress]::Loopback, $Port)
$listener.Start()
Write-Host "SAhte Modbus TCP sunucusu 127.0.0.1:$Port uzerinde dinliyor (unit $UnitId)."
Write-Host "FC03/04 => holding, FC01/02 => bit deseni, FC05/06/16 => yazilir ve saklanir, adres >= 9000 => exception 2."

try {
    while ($true) {
        $client = $listener.AcceptTcpClient()
        $stream = $client.GetStream()
        $stream.ReadTimeout = 15000
        Write-Host "Istemci baglandi."
        try {
            while ($true) {
                $head = Read-Exact $stream 7
                if ($null -eq $head) { break }
                $tid = $head[0..1]
                $length = ($head[4] -shl 8) -bor $head[5]
                $unit = $head[6]
                $pdu = Read-Exact $stream ($length - 1)
                if ($null -eq $pdu) { break }
                $fc = [int]$pdu[0]
                $addr = ([int]$pdu[1] -shl 8) -bor [int]$pdu[2]
                $qty = ([int]$pdu[3] -shl 8) -bor [int]$pdu[4]
                Write-Host ("REQ unit={0} fc={1} addr={2} qty={3}" -f $unit, $fc, $addr, $qty)

                $resp = $null
                if ($addr -ge 9000) {
                    $resp = Build-Error $fc 2
                } elseif ($fc -in 3, 4) {
                    $resp = Build-ReadResponse $fc (Get-Slice16 $addr $qty $script:Holding)
                } elseif ($fc -in 1, 2) {
                    $resp = Build-ReadResponse $fc (Get-Bits $addr $qty)
                } elseif ($fc -eq 6) {
                    $value = ([int]$pdu[3] -shl 8) -bor [int]$pdu[4]
                    if (Set-Holding $addr $value) {
                        Write-Host ("  WRITE FC06 @{0} = 0x{1:X4}" -f $addr, $value)
                        $resp = [byte[]]@($fc, $pdu[1], $pdu[2], $pdu[3], $pdu[4])
                    } else {
                        $resp = Build-Error $fc 2
                    }
                } elseif ($fc -eq 5) {
                    $value = ([int]$pdu[3] -shl 8) -bor [int]$pdu[4]
                    if ($value -eq 0xFF00) {
                        $script:Coils[$addr] = 1
                        Write-Host ("  WRITE FC05 @{0} = ACIK" -f $addr)
                        $resp = [byte[]]@($fc, $pdu[1], $pdu[2], $pdu[3], $pdu[4])
                    } elseif ($value -eq 0x0000) {
                        $script:Coils[$addr] = 0
                        Write-Host ("  WRITE FC05 @{0} = KAPALI" -f $addr)
                        $resp = [byte[]]@($fc, $pdu[1], $pdu[2], $pdu[3], $pdu[4])
                    } else {
                        $resp = Build-Error $fc 3
                    }
                } elseif ($fc -eq 16) {
                    $byteCount = [int]$pdu[5]
                    if ($qty -lt 1 -or $qty -gt 123 -or $byteCount -ne ($qty * 2)) {
                        $resp = Build-Error $fc 3
                    } else {
                        $ok = $true
                        for ($i = 0; $i -lt $qty; $i++) {
                            $value = ([int]$pdu[6 + $i * 2] -shl 8) -bor [int]$pdu[7 + $i * 2]
                            if (-not (Set-Holding ($addr + $i) $value)) { $ok = $false }
                        }
                        if ($ok) {
                            Write-Host ("  WRITE FC16 @{0} x{1}" -f $addr, $qty)
                            $resp = [byte[]]@($fc, $pdu[1], $pdu[2], $pdu[3], $pdu[4])
                        } else {
                            $resp = Build-Error $fc 2
                        }
                    }
                } else {
                    $resp = Build-Error $fc 1
                }
                Send-Frame $stream $tid $unit $resp
            }
        } catch {
            Write-Host ("Istemci hatasi: " + $_.Exception.Message)
        } finally {
            $client.Close()
        }
        if ($args -contains 'once') { break }
    }
} finally {
    $listener.Stop()
}
