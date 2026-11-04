using System;
using System.Globalization;
using System.IO;
using System.Net.Sockets;

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
    }

    /// <summary>Modbus TCP (MBAP) istemcisi. Ham soket uzerinden, hicbir ek kutuphane kullanmadan calisir.</summary>
    public class ModbusTcpClient : IDisposable
    {
        private TcpClient _tcp;
        private NetworkStream _net;
        private ushort _transaction = 0;

        public byte UnitId = 1;

        public bool IsConnected
        {
            get { return _tcp != null && _tcp.Connected; }
        }

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
        }

        public void Close()
        {
            if (_net != null) { _net.Close(); _net = null; }
            if (_tcp != null) { _tcp.Close(); _tcp = null; }
        }

        public void Dispose() { Close(); }

        public RegisterRead Read(byte functionCode, int startAddress, int quantity, int timeoutMs)
        {
            if (!IsConnected) throw new InvalidOperationException("Baglanti yok.");
            if (quantity < 1 || quantity > 125) throw new ArgumentException("Adet 1..125 olmali.");

            byte[] request = new byte[5];
            request[0] = functionCode;
            request[1] = (byte)((startAddress >> 8) & 0xFF);
            request[2] = (byte)(startAddress & 0xFF);
            request[3] = (byte)((quantity >> 8) & 0xFF);
            request[4] = (byte)(quantity & 0xFF);

            int bytesPerItem = (functionCode == 1 || functionCode == 2) ? 1 : 2;
            byte[] response = Transact(request, timeoutMs);
            if (response.Length == 0)
                throw new IOException("Sunucu boş yanıt verdi (byte count alanı yok).");

            int byteCount = response[0];
            int expected = bytesPerItem == 1 ? (quantity + 7) / 8 : quantity * 2;
            if (byteCount != response.Length - 1)
                throw new IOException("Byte count tutarsız: bildirilen " + byteCount + ", gelen " + (response.Length - 1) + ".");
            if (byteCount != expected)
                throw new IOException("Beklenen " + expected + " bayt yerine " + byteCount + " bayt geldi.");

            byte[] data = new byte[byteCount];
            Array.Copy(response, 1, data, 0, byteCount);

            RegisterRead result = new RegisterRead();
            result.FunctionCode = functionCode;
            result.StartAddress = startAddress;
            result.Quantity = quantity;
            result.Data = data;
            result.BytesPerItem = bytesPerItem;
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
            return Transact(body, timeoutMs);
        }

        /// <summary>PDU gönderir, MBAP başlığını doğrular, fonksiyon kodu sonrası gövdeyi döndürür.</summary>
        private byte[] Transact(byte[] pdu, int timeoutMs)
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

        public static string FormatFloat(float value)
        {
            return value.ToString("0.000000", CultureInfo.InvariantCulture);
        }
    }
}
