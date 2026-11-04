using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Text;

namespace ModbusScan
{
    public enum WordOrder { ABCD = 0, CDAB = 1, BADC = 2, DCBA = 3 }

    public class ModbusException : Exception
    {
        public byte FunctionCode;
        public byte ExceptionCode;

        public ModbusException(byte functionCode, byte exceptionCode)
            : base("Modbus hata yaniti: FC" + functionCode.ToString("D2") +
                   ", hata kodu " + exceptionCode.ToString("D2") + " - " + Describe(exceptionCode))
        {
            FunctionCode = functionCode;
            ExceptionCode = exceptionCode;
        }

        private static string Describe(byte code)
        {
            switch (code)
            {
                case 1: return "ILLEGAL FUNCTION";
                case 2: return "ILLEGAL DATA ADDRESS";
                case 3: return "ILLEGAL DATA VALUE";
                case 4: return "SLAVE FAILURE";
                case 6: return "SLAVE BUSY";
                case 8: return "MEMORY PARITY ERROR";
                case 10: return "GATEWAY PATH UNAVAILABLE";
                case 11: return "GATEWAY TARGET NO RESPONSE";
                default: return "BILINMEYEN";
            }
        }
    }

    public class RegisterRead
    {
        public byte FunctionCode;
        public int StartAddress;
        public int Quantity;
        public byte[] Data;
        public int ElapsedMilliseconds;
        public int BytesPerItem;
        public int Requests;
    }

    /// <summary>
    /// Taşımadan bağımsız Modbus istemcisi: PDU kurma, yanıt çözme ve kodlama burada.
    /// TCP (MBAP) ve seri (RTU) çerçeveleme farklı Transact uygulamalarında yaşar.
    /// </summary>
    public abstract class ModbusClient : IDisposable
    {
        public byte UnitId = 1;

        public abstract bool IsConnected { get; }

        /// <summary>Durum satırında gösterilen bağlantı tanımı (ip:port veya COM3 9600 8N1).</summary>
        public abstract string Description { get; }

        public abstract void Close();

        public void Dispose() { Close(); }

        /// <summary>Tek Modbus isteğinin sığdırabildiği en büyük miktarlar; üstü parçalara bölünür.</summary>
        public const int MaxRegistersPerRequest = 125;
        public const int MaxBitsPerRequest = 2000;

        /// <summary>Parçalı okumada her parçadan sonra (okunan, istenen) bildirir; uzun okumada arayüz donmuş görünmesin.</summary>
        public Action<int, int> Progress;

        /// <summary>İstenen aralığı taşımanın tek istekte kaldıramayacağı parçalara bölerek okur.</summary>
        public RegisterRead Read(byte functionCode, int startAddress, int quantity, int timeoutMs)
        {
            if (!IsConnected) throw new InvalidOperationException("Baglanti yok.");
            if (quantity < 1) throw new ArgumentException("Adet 1 veya daha büyük olmalı.");
            if (startAddress < 0 || startAddress + quantity > 65536)
                throw new ArgumentException("Adres alanı 0..65535: @" + startAddress + " ile en fazla " +
                                           (65536 - startAddress) + " kayıt okunabilir.");

            bool bits = functionCode == 1 || functionCode == 2;
            int bytesPerItem = bits ? 1 : 2;
            int chunkLimit = bits ? MaxBitsPerRequest : MaxRegistersPerRequest;

            List<byte> data = new List<byte>();
            int done = 0, requests = 0;
            while (done < quantity)
            {
                int take = quantity - done;
                if (take > chunkLimit) take = chunkLimit;

                int address = startAddress + done;
                byte[] request = new byte[5];
                request[0] = functionCode;
                request[1] = (byte)((address >> 8) & 0xFF);
                request[2] = (byte)(address & 0xFF);
                request[3] = (byte)((take >> 8) & 0xFF);
                request[4] = (byte)(take & 0xFF);

                byte[] response = Transact(request, timeoutMs);
                requests++;
                if (response.Length == 0)
                    throw new IOException("Sunucu boş yanıt verdi (byte count alanı yok).");

                int byteCount = response[0];
                int expected = bits ? (take + 7) / 8 : take * 2;
                if (byteCount != response.Length - 1)
                    throw new IOException("Byte count tutarsız: bildirilen " + byteCount + ", gelen " + (response.Length - 1) + ".");
                if (byteCount != expected)
                    throw new IOException("Beklenen " + expected + " bayt yerine " + byteCount + " bayt geldi.");

                for (int i = 1; i <= byteCount; i++) data.Add(response[i]);
                done += take;
                if (Progress != null) Progress(done, quantity);
            }

            RegisterRead result = new RegisterRead();
            result.FunctionCode = functionCode;
            result.StartAddress = startAddress;
            result.Quantity = quantity;
            result.Data = data.ToArray();
            result.BytesPerItem = bytesPerItem;
            result.Requests = requests;
            return result;
        }

        public byte[] WriteSingleRegister(int address, int value, int timeoutMs)
        {
            byte[] body = new byte[5];
            body[0] = 6;
            body[1] = (byte)((address >> 8) & 0xFF);
            body[2] = (byte)(address & 0xFF);
            body[3] = (byte)((value >> 8) & 0xFF);
            body[4] = (byte)(value & 0xFF);

            byte[] echo = Transact(body, timeoutMs);
            ExpectEcho(echo, 6, address, "yazılan değer", value);
            return echo;
        }

        /// <summary>FC05 ile tek bobin (coil) yazar; ACIK 0xFF00, KAPALI 0x0000.</summary>
        public byte[] WriteSingleCoil(int address, bool on, int timeoutMs)
        {
            int value = on ? 0xFF00 : 0x0000;
            byte[] body = new byte[5];
            body[0] = 5;
            body[1] = (byte)((address >> 8) & 0xFF);
            body[2] = (byte)(address & 0xFF);
            body[3] = (byte)((value >> 8) & 0xFF);
            body[4] = (byte)(value & 0xFF);

            byte[] echo = Transact(body, timeoutMs);
            ExpectEcho(echo, 5, address, "durum", value);
            return echo;
        }

        /// <summary>FC16 ile bitişik birden çok holding register yazar. data big-endian, çift uzunlukta.</summary>
        public byte[] WriteMultipleRegisters(int address, byte[] data, int timeoutMs)
        {
            if (data == null || data.Length % 2 != 0)
                throw new ArgumentException("Yazılacak bayt sayısı çift olmalı (2 bayt = 1 kelime).");
            int quantity = data.Length / 2;
            if (quantity < 1 || quantity > 123) throw new ArgumentException("Adet 1..123 kelime olmali.");

            byte[] body = new byte[6 + data.Length];
            body[0] = 16;
            body[1] = (byte)((address >> 8) & 0xFF);
            body[2] = (byte)(address & 0xFF);
            body[3] = (byte)((quantity >> 8) & 0xFF);
            body[4] = (byte)(quantity & 0xFF);
            body[5] = (byte)data.Length;
            Array.Copy(data, 0, body, 6, data.Length);

            byte[] echo = Transact(body, timeoutMs);
            if (echo.Length != 4)
                throw new IOException("FC16 yaniti " + echo.Length + " bayt, beklenen 4.");
            int echoAddress = (echo[0] << 8) | echo[1];
            int echoQuantity = (echo[2] << 8) | echo[3];
            if (echoAddress != address || echoQuantity != quantity)
                throw new IOException("FC16 yankisi tutmadi: adres " + echoAddress + "/" + address +
                                      ", adet " + echoQuantity + "/" + quantity + ".");
            return echo;
        }

        private static void ExpectEcho(byte[] echo, byte functionCode, int address, string what, int value)
        {
            if (echo.Length != 4)
                throw new IOException("FC" + functionCode.ToString("D2") + " yaniti " + echo.Length + " bayt, beklenen 4.");
            int echoAddress = (echo[0] << 8) | echo[1];
            int echoValue = (echo[2] << 8) | echo[3];
            if (echoAddress != address)
                throw new IOException("Yazılan adres yankisi tutmadi: " + echoAddress + " != " + address + ".");
            if (echoValue != value)
                throw new IOException(what + " yankisi tutmadi: gelen 0x" + echoValue.ToString("X4") +
                                      ", yazilan 0x" + value.ToString("X4") + ".");
        }

        /// <summary>
        /// PDU'yu taşımaya özgü çerçeveye sarıp yanıt gövdesini (fonksiyon kodu sonrası,
        /// CRC/MBAP hariç) döndürür. Modbus exception'ı ModbusException olarak fırlatılır.
        /// </summary>
        protected abstract byte[] Transact(byte[] pdu, int timeoutMs);

        public static int UInt16(byte[] d, int i) { return (d[i] << 8) | d[i + 1]; }

        public static int Int16(byte[] d, int i)
        {
            ushort v = (ushort)((d[i] << 8) | d[i + 1]);
            return unchecked((short)v);
        }

        /// <summary>4 ham bayti secilen kelime sirasina gore buyuk-sonlu (ABCD) dizilime cevirir.</summary>
        public static byte[] Reorder(byte[] d, int i, WordOrder order)
        {
            byte a = d[i], b = d[i + 1], c = d[i + 2], e = d[i + 3];
            switch (order)
            {
                case WordOrder.ABCD: return new byte[] { a, b, c, e };
                case WordOrder.CDAB: return new byte[] { c, e, a, b };
                case WordOrder.BADC: return new byte[] { b, a, e, c };
                default: return new byte[] { e, c, b, a };
            }
        }

        public static uint UInt32(byte[] d, int i, WordOrder order)
        {
            byte[] b = Reorder(d, i, order);
            return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
        }

        public static int Int32(byte[] d, int i, WordOrder order)
        {
            return unchecked((int)UInt32(d, i, order));
        }

        public static float Float32(byte[] d, int i, WordOrder order)
        {
            byte[] b = Reorder(d, i, order);
            byte[] little = new byte[] { b[3], b[2], b[1], b[0] };
            return BitConverter.ToSingle(little, 0);
        }

        /// <summary>mbpoll alışkanlığındaki 4xxxx / 3xxxx / 1xxxx / 0xxxx gösterim adresini üretir.</summary>
        public static int DisplayAddress(byte functionCode, int pduAddress, bool convention)
        {
            if (!convention) return pduAddress;
            switch (functionCode)
            {
                case 3: return pduAddress + 40001;
                case 4: return pduAddress + 30001;
                case 2: return pduAddress + 10001;
                default: return pduAddress + 1;
            }
        }

        public static string FormatFloat(float value)
        {
            return value.ToString("0.000000", CultureInfo.InvariantCulture);
        }

        public static byte[] EncodeUInt16(int value)
        {
            return new byte[] { (byte)((value >> 8) & 0xFF), (byte)(value & 0xFF) };
        }

        public static byte[] EncodeInt16(int value)
        {
            return EncodeUInt16(unchecked((ushort)value));
        }

        /// <summary>
        /// 32 bitlik değeri iki kelimeye böler. Reorder kendi tersi olduğu için (involution)
        /// aynı fonksiyon okuma ve yazma yönünde kullanilabilir.
        /// </summary>
        public static byte[] EncodeUInt32(uint value, WordOrder order)
        {
            byte[] abcd = new byte[]
            {
                (byte)((value >> 24) & 0xFF), (byte)((value >> 16) & 0xFF),
                (byte)((value >> 8) & 0xFF), (byte)(value & 0xFF)
            };
            return Reorder(abcd, 0, order);
        }

        public static byte[] EncodeInt32(int value, WordOrder order)
        {
            return EncodeUInt32(unchecked((uint)value), order);
        }

        public static byte[] EncodeFloat32(float value, WordOrder order)
        {
            byte[] little = BitConverter.GetBytes(value);
            byte[] abcd = new byte[] { little[3], little[2], little[1], little[0] };
            return Reorder(abcd, 0, order);
        }

        public static string ToHex(byte[] data)
        {
            if (data == null || data.Length == 0) return "";
            StringBuilder sb = new StringBuilder(data.Length * 3);
            for (int i = 0; i < data.Length; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(data[i].ToString("X2"));
            }
            return sb.ToString();
        }
    }

    /// <summary>Modbus TCP (MBAP) istemcisi. Ham soket uzerinden, hicbir ek kutuphane kullanmadan calisir.</summary>
    public class ModbusTcpClient : ModbusClient
    {
        private TcpClient _tcp;
        private NetworkStream _net;
        private ushort _transaction = 0;
        private string _description = "";

        public override bool IsConnected
        {
            get { return _tcp != null && _tcp.Connected; }
        }

        public override string Description { get { return _description; } }

        public void Connect(string host, int port, int timeoutMs)
        {
            Close();
            TcpClient client = new TcpClient();
            IAsyncResult ar = client.BeginConnect(host, port, null, null);
            bool ok = ar.AsyncWaitHandle.WaitOne(timeoutMs, true);
            if (!ok)
            {
                client.Close();
                throw new TimeoutException("Baglanma zaman asimi: " + host + ":" + port + " (" + timeoutMs + " ms)");
            }
            client.EndConnect(ar);
            client.NoDelay = true;
            _tcp = client;
            _net = client.GetStream();
            _transaction = 0;
            _description = host + ":" + port;
        }

        public override void Close()
        {
            if (_net != null) { _net.Close(); _net = null; }
            if (_tcp != null) { _tcp.Close(); _tcp = null; }
            _description = "";
        }

        /// <summary>PDU gönderir, MBAP başlığını doğrular, fonksiyon kodu sonrası gövdeyi döndürür.</summary>
        protected override byte[] Transact(byte[] pdu, int timeoutMs)
        {
            if (!IsConnected) throw new InvalidOperationException("Bağlantı yok.");

            _transaction = (ushort)(_transaction + 1);
            int length = pdu.Length + 1;

            byte[] frame = new byte[7 + pdu.Length];
            frame[0] = (byte)((_transaction >> 8) & 0xFF);
            frame[1] = (byte)(_transaction & 0xFF);
            frame[2] = 0; frame[3] = 0;
            frame[4] = (byte)((length >> 8) & 0xFF);
            frame[5] = (byte)(length & 0xFF);
            frame[6] = UnitId;
            Array.Copy(pdu, 0, frame, 7, pdu.Length);

            _net.ReadTimeout = timeoutMs;
            _net.WriteTimeout = timeoutMs;
            _net.Write(frame, 0, frame.Length);

            byte[] head = ReadExact(8);
            ushort rid = (ushort)((head[0] << 8) | head[1]);
            if (rid != _transaction)
                throw new IOException("Transaction id uymadı (beklenen " + _transaction + ", gelen " + rid + ").");
            int rlen = (head[4] << 8) | head[5];
            if (head[6] != UnitId)
                throw new IOException("Unit id uymadı (beklenen " + UnitId + ", gelen " + head[6] + ").");

            byte sentFunction = pdu[0];
            byte gotFunction = head[7];

            if (gotFunction == (byte)(sentFunction | 0x80))
                throw new ModbusException(sentFunction, ReadExact(1)[0]);

            if (rlen < 2) return new byte[0];
            return ReadExact(rlen - 2);
        }

        private byte[] ReadExact(int count)
        {
            byte[] buf = new byte[count];
            int off = 0;
            while (off < count)
            {
                int n = _net.Read(buf, off, count - off);
                if (n <= 0) throw new IOException("Baglanti sunucu tarafindan kapatildi.");
                off += n;
            }
            return buf;
        }
    }
}
