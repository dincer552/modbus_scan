using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using ModbusScan;

/// <summary>
/// Soket seviyesinde kendi kendine test: aynı process içinde loopback sunucusu açıp
/// istemcinin MBAP çerçevelemesini, çözümlemesini ve hata yollarını doğrular.
/// Harici sunucu veya ayrı iş/proces gerektirmediği için CI'da deterministiktir.
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
