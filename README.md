# ModbusScan

Kompakt (500 × 600 px) bir **Modbus TCP test aracı**. `mbpoll`ün okuma tarafını, tek bir `.exe` ve kurulum derdi olmadan sunar.

Bu sürüm yalnızca **okuma** yapar: FC01 (coil), FC02 (discrete input), FC03 (holding register), FC04 (input register).
Yazma, tarama ve kayıt sekmesi sonraki sürümlerde eklenecek.

## İndir

Releases sayfasındaki `ModbusScan-vX.Y.Z.exe` dosyasını indirip çift tıklaman yeterli:
<https://github.com/dincer552/modbus_scan/releases/latest>

Ek bir şey kurmana gerek yok — Windows 10/11'de gelen .NET Framework 4.8 ile çalışır.

## Kullanım

1. **IP / Port / ID / ms**: cihazın veya gateway'in adresi. Port genelde `502`, zaman aşımı `2000` ms.
2. **Bağlan** → durum satırı yeşile döner.
3. **Fonksiyon** seç, **Adres** (PDU, 0 tabanlı) ve **Adet** (1–125) gir, **OKU**.
4. Tablo her kayıt için ham hex, `uint16`, `int16` ve seçtiğin çözümlenmiş değeri gösterir.
   - **4xxxx göster** işaretliyken adresler mbpoll alışkanlığıyla `40001 / 30001 / 10001 / 00001` biçiminde yazılır; kaldırırsan düz PDU adresi görürsün.
   - **Değer** kutusundan `Float32 / Uint32 / Int32` seçince **Kelime** kutusu açılır: `ABCD` (big-endian), `CDAB` (word swap), `BADC` (byte swap), `DCBA` (little-endian).

## Geliştirme

### Derleme

Windows'ta gelen C# derleyicisini kullanır, SDK gerekmez:

```bat
build.bat
```

Çıktı: `dist\ModbusScan.exe` (GUI) ve `dist\Probe.exe` (konsol test aracı).

### Testler

Gerçek cihaz yokken protokolü doğrulamak için sahte bir Modbus TCP sunucusu var:

```powershell
# 1. terminal
powershell -ExecutionPolicy Bypass -File tools\FakeServer.ps1 -Port 5021

# 2. terminal — konsol probe
dist\Probe.exe --port 5021 --fc 3 --addr 0 --qty 10

# arayüz mantığı (headless, pencere açmadan)
powershell -ExecutionPolicy Bypass -File tools\GuiTest.ps1 -Port 5021
```

`GuiTest.ps1` 13 senaryoyu kontrol eder: dört fonksiyon kodu, `uint16/int16/hex/float32/uint32` çözümleri, `ABCD/CDAB` kelime sırası, `4xxxx` ↔ PDU adres biçimi, adet sınırı, Modbus exception yanıtı ve bağlantı kurulamayan durum.

### CI

`v*` tag'i itildiğinde GitHub Actions (windows-latest) derler, smoke testleri koşar ve exe'yi release asset olarak yayınlar:

```bash
git tag v0.1.1
git push origin v0.1.1
```

## Dosyalar

| Yol | Açıklama |
| --- | --- |
| `src/ModbusTcp.cs` | MBAP başlığı + PDU kurulumu, okuma/yazma, veri çözme (hex, uint, int, float, kelime sırası) |
| `src/MainForm.cs` | 500 × 600 px pencere, bağlantı ve okuma alanları, sonuç tablosu, log alanı |
| `src/Program.cs` | Giriş noktası ve sürüm bilgisi |
| `tools/Probe.cs` | Konsol okuma aracı (CI smoke testi bunu kullanır) |
| `tools/FakeServer.ps1` | Sahte Modbus TCP sunucusu |
| `tools/GuiTest.ps1` | Arayüz mantığını yansıtma ile test eden betik |

## Bilinen sınırlar

- Sadece Modbus **TCP**; seri (RS485/USB gateway) yok.
- Tek istek = en fazla 125 kayıt; büyük aralıklar için parçala.
- Hata durumunda bağlantı kapanır, **Bağlan** ile yeniden dene.
