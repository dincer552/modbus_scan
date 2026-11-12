using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Ports;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using ModbusScan;

/// <summary>
/// Soket ve çerçeve seviyesinde kendi kendine test: aynı process içinde loopback sunucusu
/// açıp TCP (MBAP) yolunu, sahte bir seri akış açıp RTU (CRC16) yolunu doğrular.
/// Harici sunucu, COM port veya ayrı iş/proces gerektirmediği için CI'da deterministiktir.
/// </summary>
internal static class SelfTest
{
    private static int _failures;
    private static TcpListener _listener;
    private static byte[] _lastTid = new byte[2];

    private static void Check(string name, bool condition, string detail)
    {
        if (condition) Console.WriteLine("GECTI  " + name);
        else
        {
            _failures++;
            Console.WriteLine("KALDI  " + name + " -> " + detail);
        }
    }

    private static int Main()
    {
        UpdaterChecks();
        CodecChecks();
        RtuFrameChecks();
        PortLabelChecks();
        RtuStackChecks();

        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Thread server = new Thread(ServerLoop);
        server.IsBackground = true;
        server.Start();

        int port = ((IPEndPoint)_listener.LocalEndpoint).Port;

        using (ModbusTcpClient client = new ModbusTcpClient())
        {
            client.UnitId = 3;
            client.Connect("127.0.0.1", port, 3000);

            RegisterRead holding = client.Read(3, 100, 5, 3000);
            Check("FC03 veri uzunlugu 10 bayt", holding.Data.Length == 10, "Data.Length=" + holding.Data.Length);
            Check("FC03 uint16 0x1234", ModbusTcpClient.UInt16(holding.Data, 0) == 0x1234,
                  "got 0x" + ModbusTcpClient.UInt16(holding.Data, 0).ToString("X4"));
            Check("FC03 int16 -2", ModbusTcpClient.Int16(holding.Data, 2) == -2,
                  "got " + ModbusTcpClient.Int16(holding.Data, 2));
            Check("FC03 float ABCD = 2.5", Math.Abs(ModbusTcpClient.Float32(holding.Data, 4, WordOrder.ABCD) - 2.5f) < 1e-6,
                  "got " + ModbusTcpClient.Float32(holding.Data, 4, WordOrder.ABCD));
            Check("FC03 uint32 DCBA = 8256", ModbusTcpClient.UInt32(holding.Data, 4, WordOrder.DCBA) == 8256u,
                  "got " + ModbusTcpClient.UInt32(holding.Data, 4, WordOrder.DCBA));
            Check("FC03 uint32 ABCD = 0x40200000", ModbusTcpClient.UInt32(holding.Data, 4, WordOrder.ABCD) == 0x40200000u,
                  "got 0x" + ModbusTcpClient.UInt32(holding.Data, 4, WordOrder.ABCD).ToString("X8"));

            RegisterRead coils = client.Read(1, 0, 10, 3000);
            Check("FC01 10 bit = 2 bayt", coils.Data.Length == 2, "Data.Length=" + coils.Data.Length);
            Check("FC01 bit0 acik", (coils.Data[0] & 1) == 1, "byte0=" + coils.Data[0].ToString("X2"));
            Check("FC01 bit3 acik", ((coils.Data[0] >> 3) & 1) == 1, "byte0=" + coils.Data[0].ToString("X2"));
            Check("FC01 bit1 kapali", ((coils.Data[0] >> 1) & 1) == 0, "byte0=" + coils.Data[0].ToString("X2"));
            Check("FC01 bit9 acik", ((coils.Data[1] >> 1) & 1) == 1, "byte1=" + coils.Data[1].ToString("X2"));

            RegisterRead inputs = client.Read(2, 5, 3, 3000);
            Check("FC02 bit verisi", inputs.Data.Length == 1 && inputs.Data[0] == 0x05,
                  "len=" + inputs.Data.Length + " b0=" + inputs.Data[0].ToString("X2"));

            bool exceptionCaught = false;
            try { client.Read(3, 9000, 1, 3000); }
            catch (ModbusException ex) { exceptionCaught = ex.ExceptionCode == 2 && ex.FunctionCode == 3; }
            Check("FC03 exception kodu 2 yakalandi", exceptionCaught, "exception yakalanmadi veya kodu yanlis");

            client.WriteSingleRegister(7, 0x002A, 3000);
            Check("FC06 yazma yaniti alindi", true, "");

            byte[] floatBytes = ModbusTcpClient.EncodeFloat32(1.5f, WordOrder.CDAB);
            client.WriteMultipleRegisters(200, floatBytes, 3000);
            Check("FC16 yazma yaniti alindi", true, "");

            client.WriteSingleCoil(30, true, 3000);
            Check("FC05 yazma yaniti alindi", true, "");

            bool echoMismatch = false;
            try { client.WriteSingleRegister(8, 0x1111, 3000); }
            catch (IOException ex) { echoMismatch = ex.Message.Contains("yankisi"); }
            Check("FC06 yanki uysuzlugu yakalandi", echoMismatch, "yanki kontrolu calismadi");

            bool countMismatch = false;
            try { client.Read(4, 0, 4, 3000); }
            catch (IOException ex) { countMismatch = ex.Message.Contains("tutars"); }
            Check("Byte count tutarsizligi yakalandi", countMismatch, "tutarsizlik fark edilmedi");

            client.Close();
            Check("Kapatma sonrasi IsConnected false", !client.IsConnected, "IsConnected hâlâ true");
        }

        _listener.Stop();
        Console.WriteLine("SONUC: " + _failures + " hata");
        return _failures == 0 ? 0 : 1;
    }

    /// <summary>Kodlama yardimcilari ve adres gosterimi — aga/sokete ihtiyaci yok.</summary>
    private static void CodecChecks()
    {
        WordOrder[] orders = new WordOrder[] { WordOrder.ABCD, WordOrder.CDAB, WordOrder.BADC, WordOrder.DCBA };
        for (int i = 0; i < orders.Length; i++)
        {
            WordOrder o = orders[i];
            byte[] f = ModbusTcpClient.EncodeFloat32(1.5f, o);
            Check("Float32 " + o + " kodla->coz", Math.Abs(ModbusTcpClient.Float32(f, 0, o) - 1.5f) < 1e-6,
                  "got " + ModbusTcpClient.Float32(f, 0, o) + " bayt=" + ModbusTcpClient.ToHex(f));
            byte[] u = ModbusTcpClient.EncodeUInt32(0x11223344u, o);
            Check("Uint32 " + o + " kodla->coz", ModbusTcpClient.UInt32(u, 0, o) == 0x11223344u,
                  "got 0x" + ModbusTcpClient.UInt32(u, 0, o).ToString("X8"));
            byte[] s = ModbusTcpClient.EncodeInt32(-70000, o);
            Check("Int32 " + o + " kodla->coz", ModbusTcpClient.Int32(s, 0, o) == -70000,
                  "got " + ModbusTcpClient.Int32(s, 0, o));
        }

        Check("ABCD 1.5 = 3F C0 00 00",
              ModbusTcpClient.ToHex(ModbusTcpClient.EncodeFloat32(1.5f, WordOrder.ABCD)) == "3F C0 00 00",
              ModbusTcpClient.ToHex(ModbusTcpClient.EncodeFloat32(1.5f, WordOrder.ABCD)));
        Check("CDAB 1.5 = 00 00 3F C0",
              ModbusTcpClient.ToHex(ModbusTcpClient.EncodeFloat32(1.5f, WordOrder.CDAB)) == "00 00 3F C0",
              ModbusTcpClient.ToHex(ModbusTcpClient.EncodeFloat32(1.5f, WordOrder.CDAB)));
        Check("BADC 1.5 = C0 3F 00 00",
              ModbusTcpClient.ToHex(ModbusTcpClient.EncodeFloat32(1.5f, WordOrder.BADC)) == "C0 3F 00 00",
              ModbusTcpClient.ToHex(ModbusTcpClient.EncodeFloat32(1.5f, WordOrder.BADC)));
        Check("DCBA 1.5 = 00 00 C0 3F",
              ModbusTcpClient.ToHex(ModbusTcpClient.EncodeFloat32(1.5f, WordOrder.DCBA)) == "00 00 C0 3F",
              ModbusTcpClient.ToHex(ModbusTcpClient.EncodeFloat32(1.5f, WordOrder.DCBA)));

        Check("EncodeUInt16 big-endian", ModbusTcpClient.ToHex(ModbusTcpClient.EncodeUInt16(0x1234)) == "12 34",
              ModbusTcpClient.ToHex(ModbusTcpClient.EncodeUInt16(0x1234)));
        Check("EncodeInt16 -2 = FF FE", ModbusTcpClient.ToHex(ModbusTcpClient.EncodeInt16(-2)) == "FF FE",
              ModbusTcpClient.ToHex(ModbusTcpClient.EncodeInt16(-2)));
        Check("ToHex bos dizi", ModbusTcpClient.ToHex(new byte[0]) == "" && ModbusTcpClient.ToHex(null) == "", "bos degil");

        Check("DisplayAddress FC03 4xxxx", ModbusTcpClient.DisplayAddress(3, 10, true) == 40011,
              "got " + ModbusTcpClient.DisplayAddress(3, 10, true));
        Check("DisplayAddress FC04 3xxxx", ModbusTcpClient.DisplayAddress(4, 2, true) == 30003,
              "got " + ModbusTcpClient.DisplayAddress(4, 2, true));
        Check("DisplayAddress FC02 1xxxx", ModbusTcpClient.DisplayAddress(2, 2, true) == 10003,
              "got " + ModbusTcpClient.DisplayAddress(2, 2, true));
        Check("DisplayAddress FC01 0xxxx", ModbusTcpClient.DisplayAddress(1, 5, true) == 6,
              "got " + ModbusTcpClient.DisplayAddress(1, 5, true));
        Check("DisplayAddress PDU modu", ModbusTcpClient.DisplayAddress(3, 10, false) == 10,
              "got " + ModbusTcpClient.DisplayAddress(3, 10, false));
    }

    /// <summary>RTU cerceveleme ve CRC16 hesabi — akis/donanim gerektirmez.</summary>
    private static void RtuFrameChecks()
    {
        byte[] readReq = new byte[] { 3, 0x00, 0x00, 0x00, 0x0A };
        Check("BuildFrame FC03 = C5 CD",
              ModbusRtuClient.ToHex(ModbusRtuClient.BuildFrame(1, readReq)) == "01 03 00 00 00 0A C5 CD",
              ModbusRtuClient.ToHex(ModbusRtuClient.BuildFrame(1, readReq)));

        byte[] writeReq = new byte[] { 6, 0x00, 0x01, 0x00, 0x03 };
        Check("BuildFrame FC06 = 98 0B",
              ModbusRtuClient.ToHex(ModbusRtuClient.BuildFrame(1, writeReq)) == "01 06 00 01 00 03 98 0B",
              ModbusRtuClient.ToHex(ModbusRtuClient.BuildFrame(1, writeReq)));

        byte[] resp = new byte[] { 3, 0x02, 0x00, 0x2A };
        Check("BuildFrame birim id 0x12 = BC 58",
              ModbusRtuClient.ToHex(ModbusRtuClient.BuildFrame(0x12, resp)) == "12 03 02 00 2A BC 58",
              ModbusRtuClient.ToHex(ModbusRtuClient.BuildFrame(0x12, resp)));

        byte[] frame = ModbusRtuClient.BuildFrame(1, readReq);
        Check("HasValidCrc dogru cerceve", ModbusRtuClient.HasValidCrc(frame), "false");
        frame[3] ^= 0x01;
        Check("HasValidCrc bozuk veri false", !ModbusRtuClient.HasValidCrc(frame), "true");
        Check("HasValidCrc kisa cerceve false", !ModbusRtuClient.HasValidCrc(new byte[] { 1, 2 }), "true");

        Check("Crc16 01 04 02 FF FF = 80B8",
              ModbusRtuClient.Crc16(new byte[] { 0x01, 0x04, 0x02, 0xFF, 0xFF }, 0, 5) == 0x80B8,
              "got 0x" + ModbusRtuClient.Crc16(new byte[] { 0x01, 0x04, 0x02, 0xFF, 0xFF }, 0, 5).ToString("X4"));

        Check("Crc16 offset/count", ModbusRtuClient.Crc16(new byte[] { 0xEE, 0x01, 0x03, 0xEE }, 1, 2) ==
              ModbusRtuClient.Crc16(new byte[] { 0x01, 0x03 }, 0, 2), "offsetlu farkli");

        Check("FormatSettings 8N1",
              ModbusRtuClient.FormatSettings("COM3", 9600, Parity.None, 8, StopBits.One) == "COM3 9600 8N1",
              "got " + ModbusRtuClient.FormatSettings("COM3", 9600, Parity.None, 8, StopBits.One));
        Check("FormatSettings 8E2 / 1.5",
              ModbusRtuClient.FormatSettings("COM1", 19200, Parity.Even, 8, StopBits.Two) == "COM1 19200 8E2" &&
              ModbusRtuClient.StopLetter(StopBits.OnePointFive) == "1.5" &&
              ModbusRtuClient.ParityLetter(Parity.Odd) == "O",
              "got " + ModbusRtuClient.FormatSettings("COM1", 19200, Parity.Even, 8, StopBits.Two));
    }

    /// <summary>COM port listesinde portun yaninda yazan aciklama metni (WMI caption) islemi.</summary>
    private static void PortLabelChecks()
    {
        Check("PortNameOf COM14", SerialPortInfo.PortNameOf("USB Serial Port (COM14)") == "COM14",
              "got " + SerialPortInfo.PortNameOf("USB Serial Port (COM14)"));
        Check("PortNameOf kucuk harf", SerialPortInfo.PortNameOf("Bluetooth Link (com9)") == "COM9",
              "got " + SerialPortInfo.PortNameOf("Bluetooth Link (com9)"));
        Check("PortNameOf parantez yoksa null", SerialPortInfo.PortNameOf("COM5") == null, "null degil");
        Check("PortNameOf rakam yoksa null", SerialPortInfo.PortNameOf("Paralel (COM)") == null, "null degil");
        Check("PortNameOf kapanis yoksa null", SerialPortInfo.PortNameOf("Port (COM12") == null, "null degil");

        Check("StripPortSuffix onden kirpilir",
              SerialPortInfo.StripPortSuffix("USB-SERIAL Ch A (COM14)", "COM14") == "USB-SERIAL Ch A",
              "got " + SerialPortInfo.StripPortSuffix("USB-SERIAL Ch A (COM14)", "COM14"));
        Check("StripPortSuffix sondan kirpilir",
              SerialPortInfo.StripPortSuffix("(COM3) USB Serial Converter", "COM3") == "USB Serial Converter",
              "got " + SerialPortInfo.StripPortSuffix("(COM3) USB Serial Converter", "COM3"));
        Check("StripPortSuffix aciklama yoksa bos",
              SerialPortInfo.StripPortSuffix("(COM3)", "COM3") == "",
              "got " + SerialPortInfo.StripPortSuffix("(COM3)", "COM3"));
        Check("StripPortSuffix caption yoksa degismeden",
              SerialPortInfo.StripPortSuffix("Paralel Port", "COM1") == "Paralel Port", "degisti");

        Dictionary<string, string> described = SerialPortInfo.Describe();
        Check("Describe hata vermeden sozluk dondurur", described != null, "null");
    }

    /// <summary>Tam RTU yiginini sahte seri akis uzerinden kosar: okuma, yazma, exception, CRC, zaman asimi.</summary>
    private static void RtuStackChecks()
    {
        FakeSerialStream io = new FakeSerialStream(RtuDevice);
        using (ModbusRtuClient client = new ModbusRtuClient())
        {
            client.UnitId = 1;
            client.Connect(io, "COM_TEST 9600 8N1");
            Check("RTU akis modunda IsConnected true", client.IsConnected, "false");
            Check("RTU Description", client.Description == "COM_TEST 9600 8N1", "got " + client.Description);

            RegisterRead holding = client.Read(3, 100, 5, 3000);
            Check("RTU istek cercevesi FC03 @100 x5", io.LastRequest.StartsWith("01 03 00 64 00 05"), io.LastRequest);
            Check("RTU FC03 10 bayt", holding.Data.Length == 10, "len=" + holding.Data.Length);
            Check("RTU FC03 uint16 0x1234", ModbusRtuClient.UInt16(holding.Data, 0) == 0x1234, "istek=" + io.LastRequest);
            Check("RTU FC03 int16 -2", ModbusRtuClient.Int16(holding.Data, 2) == -2, "got " + ModbusRtuClient.Int16(holding.Data, 2));
            Check("RTU FC03 float ABCD = 2.5",
                  Math.Abs(ModbusRtuClient.Float32(holding.Data, 4, WordOrder.ABCD) - 2.5f) < 1e-6,
                  "got " + ModbusRtuClient.Float32(holding.Data, 4, WordOrder.ABCD));

            RegisterRead coils = client.Read(1, 0, 10, 3000);
            Check("RTU FC01 2 bayt", coils.Data.Length == 2, "len=" + coils.Data.Length);
            Check("RTU FC01 bit0 acik / bit1 kapali", (coils.Data[0] & 1) == 1 && ((coils.Data[0] >> 1) & 1) == 0,
                  "b0=" + coils.Data[0].ToString("X2"));

            RegisterRead inputs = client.Read(2, 5, 3, 3000);
            Check("RTU FC02 bit verisi", inputs.Data.Length == 1 && inputs.Data[0] == 0x05, "len=" + inputs.Data.Length);

            bool exceptionCaught = false;
            try { client.Read(3, 9000, 1, 3000); }
            catch (ModbusException ex) { exceptionCaught = ex.ExceptionCode == 2 && ex.FunctionCode == 3; }
            Check("RTU exception kodu 2 yakalandi", exceptionCaught, "exception yakalanmadi");

            client.WriteSingleRegister(7, 0x002A, 3000);
            Check("RTU FC06 cercevesi", io.LastRequest.StartsWith("01 06 00 07 00 2A"), io.LastRequest);

            client.WriteMultipleRegisters(200, ModbusRtuClient.EncodeFloat32(1.5f, WordOrder.CDAB), 3000);
            Check("RTU FC16 cercevesi CDAB", io.LastRequest.StartsWith("01 10 00 C8 00 02 04 00 00 3F C0"), io.LastRequest);

            client.WriteSingleCoil(30, true, 3000);
            Check("RTU FC05 ACIK cercevesi", io.LastRequest.StartsWith("01 05 00 1E FF 00"), io.LastRequest);

            bool echoMismatch = false;
            try { client.WriteSingleRegister(8, 0x1111, 3000); }
            catch (IOException ex) { echoMismatch = ex.Message.Contains("yankisi"); }
            Check("RTU FC06 yanki uysuzlugu yakalandi", echoMismatch, "yanki kontrolu calismadi");

            bool countMismatch = false;
            try { client.Read(4, 0, 4, 3000); }
            catch (IOException ex) { countMismatch = ex.Message.Contains("bayt yerine"); }
            Check("RTU beklenen/ gelen bayt sayisi farki yakalandi", countMismatch, "uyusmazlik fark edilmedi");

            io.CorruptNext = 1;
            bool crcError = false;
            try { client.Read(3, 100, 5, 3000); }
            catch (IOException ex) { crcError = ex.Message.Contains("CRC"); }
            Check("RTU CRC hatasi yakalandi", crcError, "bozuk CRC fark edilmedi");

            io.CorruptNext = 2;
            bool unitMismatch = false;
            try { client.Read(3, 100, 5, 3000); }
            catch (IOException ex) { unitMismatch = ex.Message.Contains("Birim id"); }
            Check("RTU birim id uyuşmazlığı yakalandi", unitMismatch, "yanlis birim id gecmedi");

            io.SilentNext = true;
            bool timedOut = false;
            try { client.Read(3, 100, 5, 200); }
            catch (TimeoutException ex) { timedOut = ex.Message.Contains("gelmedi"); }
            Check("RTU yantsizlik zaman asimi", timedOut, "zaman asimi firlamadi");
            Check("Zaman asimi sonrasi baglanti acik kaliyor", client.IsConnected, "kapandi");

            RegisterRead afterTimeout = client.Read(3, 100, 5, 3000);
            Check("Zaman asimindan sonra okuma suruyor", afterTimeout.Data.Length == 10, "len=" + afterTimeout.Data.Length);

            // Adet siniri kaldirildi: tek istege sigmayan aralik parcalara bolunur.
            io.RequestCount = 0;
            RegisterRead exact = client.Read(3, 5000, ModbusClient.MaxRegistersPerRequest, 3000);
            Check("125 register tek istekte", io.RequestCount == 1 && exact.Requests == 1, "istek=" + io.RequestCount);

            io.RequestCount = 0;
            int progressCalls = 0;
            int lastDone = -1;
            client.Progress = delegate(int done, int total)
            {
                progressCalls++;
                if (total != 300 || done <= lastDone) throw new InvalidOperationException("akis bozuk: " + done + "/" + total);
                lastDone = done;
            };
            RegisterRead chunked = client.Read(3, 5000, 300, 3000);
            client.Progress = null;
            Check("300 register 3 istekte", io.RequestCount == 3 && chunked.Requests == 3, "istek=" + io.RequestCount);
            Check("Ilerleme her parcada bildirildi", progressCalls == 3 && lastDone == 300,
                  "bildirim=" + progressCalls + " son=" + lastDone);
            Check("Parcali okuma 600 bayt", chunked.Data.Length == 600, "len=" + chunked.Data.Length);
            Check("Parcali okuma Quantity alani", chunked.Quantity == 300, "q=" + chunked.Quantity);
            Check("Parcalar adres desende surekli", MatchesPattern(chunked.Data, 5000, false), FirstPatternDiff(chunked.Data, 5000, false));

            io.RequestCount = 0;
            RegisterRead manyBits = client.Read(1, 5000, 2100, 3000);
            Check("2100 bit 2 istekte", io.RequestCount == 2 && manyBits.Requests == 2, "istek=" + io.RequestCount);
            Check("2100 bit 263 bayt", manyBits.Data.Length == 263, "len=" + manyBits.Data.Length);
            Check("Bit parcalari bayt hizali surekli", MatchesBitPattern(manyBits.Data, 5000, 2100), FirstBitPatternDiff(manyBits.Data, 5000, 2100));

            bool rejectedZero = false;
            try { client.Read(3, 0, 0, 1000); }
            catch (ArgumentException ex) { rejectedZero = ex.Message.Contains("Adet"); }
            Check("Adet sifir reddediliyor", rejectedZero, "istisna yok");

            bool rejectedOverflow = false;
            try { client.Read(3, 65500, 100, 1000); }
            catch (ArgumentException ex) { rejectedOverflow = ex.Message.Contains("65535"); }
            Check("Adres alani tasma siniri", rejectedOverflow, "istisna yok");

            // --- hata toleransi: yasadisi register isaretlenir, digerleri okunmaya devam eder ---
            io.RequestCount = 0;
            RegisterRead tolerant = client.ReadTolerant(3, 6000, 20, 3000);
            Check("Toleransli okuma 20 kaydi bitiriyor", tolerant.Quantity == 20 && tolerant.Data.Length == 40,
                  "q=" + tolerant.Quantity + " len=" + tolerant.Data.Length);
            Check("Yasadisi 3 register isaretlendi", tolerant.ErrorCount == 3, "hatali=" + tolerant.ErrorCount);
            Check("6003 yasadisi hata kodu tasiyor", tolerant.Failed(3) && tolerant.ItemErrors[3].Contains("02"),
                  "hucre=" + tolerant.ItemErrors[3]);
            Check("6010 de yasadisi olarak isaretli", tolerant.Failed(10), "isaret yok");
            Check("6000 saglikli kayit isaretli degil", !tolerant.Failed(0), "yanlis isaret");
            Check("Saglikli kayitlar adres deseniyle ayni", TolerantPatternOk(tolerant, 6000, 20), "desen farki");
            Check("Toleransli okumada durma nedeni yok", tolerant.StopReason == null, "sebep=" + tolerant.StopReason);
            Check("Parca bozukunca tek tek deneme istekleri", tolerant.Requests > 20, "istek=" + tolerant.Requests);

            RegisterRead cleanTolerant = client.ReadTolerant(3, 5000, 10, 3000);
            RegisterRead plain = client.Read(3, 5000, 10, 3000);
            Check("Hatasiz aralik tek istekte kalıyor", cleanTolerant.Requests == 1 && cleanTolerant.ErrorCount == 0,
                  "istek=" + cleanTolerant.Requests + " hatali=" + cleanTolerant.ErrorCount);
            Check("Toleransli okuma duz okuma ile ayni veri", SameData(cleanTolerant, plain), "veri farkli");

            RegisterRead dead = client.ReadTolerant(3, 8000, 20, 200);
            Check("Yanitsizlikta okuma duruyor", dead.StopReason != null && dead.StopReason.Contains("Üst üste"),
                  "sebep=" + dead.StopReason);
            Check("Kalan kayitlar okunmadi isaretli", dead.ErrorCount == 20 && dead.ItemErrors[19] == "okunmadi",
                  "hatali=" + dead.ErrorCount + " son=" + dead.ItemErrors[19]);
            Check("Yanitsizlik sonrasi baglanti acik kaliyor", client.IsConnected, "kapandi");
            Check("Durduktan sonra okuma suruyor", client.Read(3, 5000, 4, 3000).Data.Length == 8, "sonraki okuma basarisiz");

            client.Close();
            Check("RTU kapatma sonrasi IsConnected false", !client.IsConnected, "IsConnected hala true");
        }
    }

    /// <summary>
    /// Sahte cihaz deseninin tek kaynagi: bayt indeksini mutlak adrese baglar. Parca sinirlarinda
    /// istemci adet (kayit/bit) bazinda ilerledigi icin desen burada adres cinsinden tanimli olmali.
    /// </summary>
    private static byte ExpectedPatternByte(int address, int i, bool bits)    {
        if (bits) return (byte)(((address >> 3) + i) & 0xFF);
        int register = address + (i >> 1);
        return (byte)((i & 1) == 0 ? ((register >> 8) & 0xFF) : (register & 0xFF));
    }

    /// <summary>Sahte cihazın desen yanıtı: istenen adet kadar bayt, desen mutlak adresten üretilir.</summary>
    private static byte[] PatternReply(byte unit, byte fc, int address, int quantity)
    {
        bool bitFc = fc == 1 || fc == 2;
        int bytes = bitFc ? (quantity + 7) / 8 : quantity * 2;
        byte[] pdu = new byte[bytes + 2];
        pdu[0] = fc;
        pdu[1] = (byte)bytes;
        for (int i = 0; i < bytes; i++) pdu[i + 2] = ExpectedPatternByte(address, i, bitFc);
        return ModbusRtuClient.BuildFrame(unit, pdu);
    }

    /// <summary>İstenen aralık yasadisi bir kayda degiyorsa cihaz butun istege exception ile cevap verir.</summary>
    private static bool TouchesIllegalAddress(int start, int count)
    {
        for (int address = start; address < start + count; address++)
            if (address == 6003 || address == 6004 || address == 6010) return true;
        return false;
    }

    /// <summary>Hata toleransli okumada isaretlenmeyen kayitlarin desene uydugunu dogrular.</summary>
    private static bool TolerantPatternOk(RegisterRead read, int startAddress, int quantity)
    {
        for (int i = 0; i < quantity; i++)
        {
            if (read.Failed(i)) continue;
            int offset = i * 2;
            if (read.Data[offset] != ExpectedPatternByte(startAddress, offset, false) ||
                read.Data[offset + 1] != ExpectedPatternByte(startAddress, offset + 1, false)) return false;
        }
        return true;
    }

    private static bool SameData(RegisterRead a, RegisterRead b)
    {
        if (a.Data.Length != b.Data.Length) return false;
        for (int i = 0; i < a.Data.Length; i++) if (a.Data[i] != b.Data[i]) return false;
        return true;
    }

    /// <summary>Parcalanmis okumada verinin adres deseni boyunca kesintisiz aktigini dogrular.</summary>
    private static bool MatchesPattern(byte[] data, int startAddress, bool bits)
    {
        for (int i = 0; i < data.Length; i++)
        {
            if (data[i] != ExpectedPatternByte(startAddress, i, bits)) return false;
        }
        return true;
    }

    private static string FirstPatternDiff(byte[] data, int startAddress, bool bits)
    {
        for (int i = 0; i < data.Length; i++)
        {
            if (data[i] != ExpectedPatternByte(startAddress, i, bits))
                return i + ": beklenen 0x" + ExpectedPatternByte(startAddress, i, bits).ToString("X2") +
                       ", gelen 0x" + data[i].ToString("X2");
        }
        return "fark yok";
    }

    /// <summary>
    /// Bit okumasinda son baytin istenmeyen bitleri istemci tarafindan sifirlaniyor (desen degil,
    /// cihaz da o bitleri rastgele doldurabiliyor), o yuzden yalnizca bitCount kadar karsilastirilir.
    /// </summary>
    private static byte BitTailMask(int bitCount, int index, int length)
    {
        int rest = bitCount % 8;
        if (index != length - 1 || rest == 0) return (byte)0xFF;
        return (byte)((1 << rest) - 1);
    }

    private static bool MatchesBitPattern(byte[] data, int startAddress, int bitCount)
    {
        for (int i = 0; i < data.Length; i++)
        {
            byte mask = BitTailMask(bitCount, i, data.Length);
            if ((data[i] & mask) != (ExpectedPatternByte(startAddress, i, true) & mask)) return false;
        }
        return true;
    }

    private static string FirstBitPatternDiff(byte[] data, int startAddress, int bitCount)
    {
        for (int i = 0; i < data.Length; i++)
        {
            byte mask = BitTailMask(bitCount, i, data.Length);
            if ((data[i] & mask) != (ExpectedPatternByte(startAddress, i, true) & mask))
                return i + ": beklenen 0x" + (ExpectedPatternByte(startAddress, i, true) & mask).ToString("X2") +
                       ", gelen 0x" + (data[i] & mask).ToString("X2");
        }
        return "fark yok";
    }

    /// <summary>Sahte RTU cihazinin PDU uretici: cerceve CRC'sini dogrular, istege yanit PDU'su dondurur.</summary>
    private static byte[] RtuDevice(byte[] frame)
    {
        if (frame.Length < 6 || !ModbusRtuClient.HasValidCrc(frame)) return null;  // cihaz bozuk cerceveye cevap vermez

        byte unit = frame[0];
        byte fc = frame[1];
        int address = (frame[2] << 8) | frame[3];

        if (fc == 3 && address == 9000) return ModbusRtuClient.BuildFrame(unit, new byte[] { 0x83, 0x02 });
        // Hata toleransi senaryosu: 6003, 6004 ve 6010 yasadisi; araliga denk gelen istek exception 2 alir.
        if (address >= 6000 && address < 6100 && (fc == 1 || fc == 2 || fc == 3 || fc == 4))
        {
            int span = (frame[4] << 8) | frame[5];
            if (TouchesIllegalAddress(address, span)) return ModbusRtuClient.BuildFrame(unit, new byte[] { (byte)(fc | 0x80), 0x02 });
            return PatternReply(unit, fc, address, span);
        }
        // Yanitsizlik senaryosu: bu araliktaki kayitlar icin cihaz hic konusmaz.
        if (address >= 8000 && address < 8100 && (fc == 1 || fc == 2 || fc == 3 || fc == 4)) return null;
        if (address >= 5000 && (fc == 1 || fc == 2 || fc == 3 || fc == 4))
        {
            // Cok istekli okuma senaryosu: istenen adet kadar cevap, desen mutlak adresten.
            int qty = (frame[4] << 8) | frame[5];
            return PatternReply(unit, fc, address, qty);
        }
        if (fc == 3) return ModbusRtuClient.BuildFrame(unit, new byte[] { 3, 10, 0x12, 0x34, 0xFF, 0xFE, 0x40, 0x20, 0x00, 0x00, 0x00, 0x64 });
        if (fc == 1) return ModbusRtuClient.BuildFrame(unit, new byte[] { 1, 2, 0x09, 0x02 });
        if (fc == 2) return ModbusRtuClient.BuildFrame(unit, new byte[] { 2, 1, 0x05 });
        if (fc == 4) return ModbusRtuClient.BuildFrame(unit, new byte[] { 4, 6, 0x00, 0x01, 0x00, 0x02, 0x00, 0x03 });  // 4 kayıt istendi, 3 geldi
        if (fc == 6 && address == 8) return ModbusRtuClient.BuildFrame(unit, new byte[] { 6, frame[2], frame[3], 0x22, 0x22 });
        if (fc == 5 || fc == 6 || fc == 16)
            return ModbusRtuClient.BuildFrame(unit, new byte[] { fc, frame[2], frame[3], frame[4], frame[5] });

        return null;
    }

    /// <summary>COM port yerine kullanilan sahte cift yonlu akis; yazilan cerceveye aninda yanit uretir.</summary>
    private sealed class FakeSerialStream : Stream
    {
        private readonly Queue<byte> _rx = new Queue<byte>();
        private readonly List<byte> _tx = new List<byte>();
        private readonly Func<byte[], byte[]> _device;

        public string LastRequest = "";
        public int RequestCount;    // kac istek cercevesi gitti (okuma parcalama testi icin)
        public int CorruptNext;     // 1 = CRC baytini boz, 2 = birim id'yi boz
        public bool SilentNext;     // true = cihaz hiç cevap vermesin

        public FakeSerialStream(Func<byte[], byte[]> device) { _device = device; }

        public override bool CanRead { get { return true; } }
        public override bool CanWrite { get { return true; } }
        public override bool CanSeek { get { return false; } }
        public override bool CanTimeout { get { return true; } }
        public override int ReadTimeout { get; set; }
        public override int WriteTimeout { get; set; }

        public override void Write(byte[] buffer, int offset, int count)
        {
            for (int i = 0; i < count; i++) _tx.Add(buffer[offset + i]);
            byte[] request = _tx.ToArray();
            _tx.Clear();
            LastRequest = ModbusRtuClient.ToHex(request);
            RequestCount++;

            byte[] reply = _device(request);
            if (reply == null || SilentNext) { SilentNext = false; return; }

            if (CorruptNext == 1) reply[reply.Length - 1] ^= 0x55;
            else if (CorruptNext == 2) reply[0] ^= 0x0F;
            CorruptNext = 0;

            for (int i = 0; i < reply.Length; i++) _rx.Enqueue(reply[i]);
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_rx.Count == 0) throw new TimeoutException("akista bayt yok");
            int n = 0;
            while (n < count && _rx.Count > 0)
            {
                buffer[offset + n] = _rx.Dequeue();
                n++;
            }
            return n;
        }

        public override void Flush() { }

        public override long Length { get { throw new NotSupportedException(); } }
        public override long Position { get { throw new NotSupportedException(); } set { throw new NotSupportedException(); } }
        public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
        public override void SetLength(long value) { throw new NotSupportedException(); }
    }

    private static void UpdaterChecks()
    {
        Check("ParseTag v0.2.0", Updater.ParseTag("v0.2.0") == new Version(0, 2, 0),
              "got " + Updater.ParseTag("v0.2.0"));
        Check("ParseTag onay v'siz", Updater.ParseTag("0.2.0") == new Version(0, 2, 0),
              "got " + Updater.ParseTag("0.2.0"));
        Check("ParseTag gecersiz etiket null", Updater.ParseTag("v-taslak") == null, "null degil");
        Check("ParseTag bos null", Updater.ParseTag("") == null && Updater.ParseTag(null) == null, "null degil");

        Check("TagFromLocation 302 hedefi",
              Updater.TagFromLocation("https://github.com/dincer552/modbus_scan/releases/tag/v0.2.0") == "v0.2.0",
              "got " + Updater.TagFromLocation("https://github.com/dincer552/modbus_scan/releases/tag/v0.2.0"));
        Check("TagFromLocation sorgu parametresini atlar",
              Updater.TagFromLocation("https://github.com/x/y/releases/tag/v1.10.2?foo=1") == "v1.10.2",
              "got " + Updater.TagFromLocation("https://github.com/x/y/releases/tag/v1.10.2?foo=1"));
        Check("TagFromLocation etiket yoksa null",
              Updater.TagFromLocation("https://github.com/x/y/releases") == null && Updater.TagFromLocation(null) == null,
              "null degil");

        Check("AssetName", Updater.AssetName("v0.2.0") == "ModbusScan-v0.2.0.exe", "got " + Updater.AssetName("v0.2.0"));
        Check("DownloadUrlFor",
              Updater.DownloadUrlFor("v0.2.0") == "https://github.com/dincer552/modbus_scan/releases/download/v0.2.0/ModbusScan-v0.2.0.exe",
              "got " + Updater.DownloadUrlFor("v0.2.0"));
        Check("ReleasePageFor",
              Updater.ReleasePageFor("v0.2.0") == "https://github.com/dincer552/modbus_scan/releases/tag/v0.2.0",
              "got " + Updater.ReleasePageFor("v0.2.0"));
        Check("LatestReleaseUrl yonlendirme ucu",
              Updater.LatestReleaseUrl == "https://github.com/dincer552/modbus_scan/releases/latest",
              "got " + Updater.LatestReleaseUrl);

        Check("IsNewer 0.1.1 -> 0.2.0 yeni", Updater.IsNewer(new Version(0, 1, 1), new Version(0, 2, 0)), "yanlis");
        Check("IsNewer ayni sürüm degil", !Updater.IsNewer(new Version(0, 2, 0), new Version(0, 2, 0)), "yanlis");
        Check("IsNewer geriye dusmez", !Updater.IsNewer(new Version(0, 2, 0), new Version(0, 1, 9)), "yanlis");
        Check("IsNewer yama numarasini da karsilastirir", Updater.IsNewer(new Version(0, 2, 0), new Version(0, 2, 1)), "yanlis");
        Check("IsNewer null latest -> false", !Updater.IsNewer(new Version(0, 2, 0), null), "yanlis");

        Check("FormatSize bilinmiyor", Updater.FormatSize(0) == "bilinmiyor", "got " + Updater.FormatSize(0));
        Check("FormatSize bayt", Updater.FormatSize(512) == "512 B", "got " + Updater.FormatSize(512));
        Check("FormatSize KB", Updater.FormatSize(2048) == "2 KB", "got " + Updater.FormatSize(2048));
        Check("FormatSize MB", Updater.FormatSize(1572864) == "1.50 MB", "got " + Updater.FormatSize(1572864));

        Check("CurrentVersion okunabiliyor", Updater.CurrentVersion != null && Updater.CurrentVersion.Major == 0,
              "got " + Updater.CurrentVersion);
    }

    private static byte[] ReadBytes(NetworkStream s, int count)
    {
        byte[] buf = new byte[count];
        int off = 0;
        while (off < count)
        {
            int n = s.Read(buf, off, count - off);
            if (n <= 0) throw new IOException("istemci kapandi");
            off += n;
        }
        return buf;
    }

    private static void Send(NetworkStream s, byte unit, byte[] pdu)
    {
        int length = pdu.Length + 1;
        byte[] frame = new byte[7 + pdu.Length];
        frame[0] = _lastTid[0];
        frame[1] = _lastTid[1];
        frame[2] = 0;
        frame[3] = 0;
        frame[4] = (byte)((length >> 8) & 0xFF);
        frame[5] = (byte)(length & 0xFF);
        frame[6] = unit;
        Array.Copy(pdu, 0, frame, 7, pdu.Length);
        s.Write(frame, 0, frame.Length);
        s.Flush();
    }

    private static byte[] NextRequest(NetworkStream s)
    {
        byte[] head = ReadBytes(s, 7);
        _lastTid = new byte[] { head[0], head[1] };
        int pduLength = ((head[4] << 8) | head[5]) - 1;
        Check("MBAP birim id 3", head[6] == 3, "unit=" + head[6]);
        Check("MBAP protokolu 0 (Modbus)", head[2] == 0 && head[3] == 0,
              "proto=" + head[2].ToString("X2") + head[3].ToString("X2"));
        return ReadBytes(s, pduLength);
    }

    private static void ServerLoop()
    {
        try
        {
            using (TcpClient peer = _listener.AcceptTcpClient())
            using (NetworkStream s = peer.GetStream())
            {
                s.ReadTimeout = 5000;

                byte[] pdu = NextRequest(s);
                Check("Istek FC03", pdu[0] == 3, "fc=" + pdu[0]);
                Check("Istek adresi big-endian 100", ((pdu[1] << 8) | pdu[2]) == 100,
                      "addr=" + ((pdu[1] << 8) | pdu[2]));
                Check("Istek adedi big-endian 5", ((pdu[3] << 8) | pdu[4]) == 5,
                      "qty=" + ((pdu[3] << 8) | pdu[4]));
                Send(s, 3, new byte[] { 3, 10, 0x12, 0x34, 0xFF, 0xFE, 0x40, 0x20, 0x00, 0x00, 0x00, 0x64 });

                pdu = NextRequest(s);
                Check("Istek FC01", pdu[0] == 1, "fc=" + pdu[0]);
                Check("Istek FC01 adedi 10", ((pdu[3] << 8) | pdu[4]) == 10, "qty=" + ((pdu[3] << 8) | pdu[4]));
                Send(s, 3, new byte[] { 1, 2, 0x09, 0x02 });

                NextRequest(s);
                Send(s, 3, new byte[] { 2, 1, 0x05 });

                NextRequest(s);
                Send(s, 3, new byte[] { 0x83, 0x02 });

                pdu = NextRequest(s);
                Check("Istek FC06", pdu[0] == 6, "fc=" + pdu[0]);
                Check("Istek FC06 degeri 0x002A", ((pdu[3] << 8) | pdu[4]) == 0x002A,
                      "val=" + ((pdu[3] << 8) | pdu[4]));
                Send(s, 3, new byte[] { 6, pdu[1], pdu[2], pdu[3], pdu[4] });

                pdu = NextRequest(s);
                Check("Istek FC16", pdu[0] == 16, "fc=" + pdu[0]);
                Check("Istek FC16 adresi 200", ((pdu[1] << 8) | pdu[2]) == 200,
                      "addr=" + ((pdu[1] << 8) | pdu[2]));
                Check("Istek FC16 adedi 2", ((pdu[3] << 8) | pdu[4]) == 2, "qty=" + ((pdu[3] << 8) | pdu[4]));
                Check("Istek FC16 byte count 4", pdu[5] == 4, "bc=" + pdu[5]);
                Check("Istek FC16 verisi CDAB 1.5",
                      pdu[6] == 0x00 && pdu[7] == 0x00 && pdu[8] == 0x3F && pdu[9] == 0xC0,
                      "data=" + ModbusTcpClient.ToHex(new byte[] { pdu[6], pdu[7], pdu[8], pdu[9] }));
                Send(s, 3, new byte[] { 16, pdu[1], pdu[2], pdu[3], pdu[4] });

                pdu = NextRequest(s);
                Check("Istek FC05", pdu[0] == 5, "fc=" + pdu[0]);
                Check("Istek FC05 adresi 30", ((pdu[1] << 8) | pdu[2]) == 30, "addr=" + ((pdu[1] << 8) | pdu[2]));
                Check("Istek FC05 ACIK 0xFF00", ((pdu[3] << 8) | pdu[4]) == 0xFF00,
                      "val=0x" + ((pdu[3] << 8) | pdu[4]).ToString("X4"));
                Send(s, 3, new byte[] { 5, pdu[1], pdu[2], 0xFF, 0x00 });

                pdu = NextRequest(s);
                Check("Istek FC06 (kotu yanki senaryosu)", pdu[0] == 6 && pdu[3] == 0x11,
                      "fc=" + pdu[0] + " v=" + pdu[3].ToString("X2"));
                Send(s, 3, new byte[] { 6, pdu[1], pdu[2], 0x22, 0x22 });

                NextRequest(s);
                Send(s, 3, new byte[] { 4, 6, 0x00, 0x01 });

                peer.Close();
            }
        }
        catch (Exception ex)
        {
            Check("Sunucu dongusu", false, ex.GetType().Name + ": " + ex.Message);
        }
    }
}
