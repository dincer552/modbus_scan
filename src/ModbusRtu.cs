using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Ports;
using System.Management;

namespace ModbusScan
{
    /// <summary>
    /// COM portlarının Windows'ta görünen sürücü açıklamalarını sorgular, böylece listede
    /// "COM14" yerine "COM14  USB-SERIAL Ch A" yazabilir.
    /// </summary>
    public static class SerialPortInfo
    {
        /// <summary>Port adı → açıklama. WMI kapalıysa veya eşleşme yoksa boş sözlük döner.</summary>
        public static Dictionary<string, string> Describe()
        {
            Dictionary<string, string> result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                    "SELECT Caption, Name FROM Win32_PnPEntity WHERE Caption LIKE '%(COM%'");
                using (ManagementObjectCollection rows = searcher.Get())
                {
                    foreach (ManagementObject row in rows)
                    {
                        string caption = Text(row["Caption"]);
                        if (caption.Length == 0) caption = Text(row["Name"]);
                        string port = PortNameOf(caption);
                        if (port == null) continue;
                        string label = StripPortSuffix(caption, port);
                        string current;
                        if (label.Length > 0 && (!result.TryGetValue(port, out current) || current.Length < label.Length))
                        {
                            result[port] = label;
                        }
                    }
                }
            }
            catch (Exception)
            {
                // WMI sorgulanamıyorsa liste çıplak COM adlarıyla devam eder.
            }
            return result;
        }

        private static string Text(object value)
        {
            return value == null ? "" : Convert.ToString(value).Trim();
        }

        /// <summary>"USB Serial Port (COM14)" metninden "COM14" çıkarır; yoksa null.</summary>
        public static string PortNameOf(string caption)
        {
            if (caption == null) return null;
            int open = caption.IndexOf("(COM", StringComparison.OrdinalIgnoreCase);
            if (open < 0) return null;
            int end = open + 4;
            while (end < caption.Length && caption[end] >= '0' && caption[end] <= '9') end++;
            if (end == open + 4 || end >= caption.Length || caption[end] != ')') return null;
            return caption.Substring(open + 1, end - open - 1).ToUpperInvariant();
        }

        /// <summary>Açıklamadaki "(COM14)" parçasını silip kalanı döner; port zaten listede görünüyor.</summary>
        public static string StripPortSuffix(string caption, string portName)
        {
            if (caption == null) return "";
            int open = caption.IndexOf("(COM", StringComparison.OrdinalIgnoreCase);
            if (open < 0) return caption.Trim();
            int end = open + 4;
            while (end < caption.Length && caption[end] >= '0' && caption[end] <= '9') end++;
            if (end == open + 4 || end >= caption.Length || caption[end] != ')') return caption.Trim();
            string head = caption.Substring(0, open).Trim();
            string tail = caption.Substring(end + 1).Trim();
            if (head.Length == 0) return tail;
            if (tail.Length == 0) return head;
            return head + " " + tail;
        }
    }
    /// <summary>
    /// Modbus RTU (seri / RS485) istemcisi. Çerçeve birim id + PDU + CRC16 (düşük bayt önce)
    /// biçimindedir; MBAP başlığı ve transaction takibi yoktur, yanıt uzunluğu fonksiyon
    /// kodundan ve byte count alanından çözülür.
    /// </summary>
    public class ModbusRtuClient : ModbusClient
    {
        private SerialPort _port;
        private Stream _io;
        private string _description = "";

        public override bool IsConnected { get { return _io != null; } }

        public override string Description { get { return _description; } }

        public void Connect(string portName, int baudRate, Parity parity, int dataBits, StopBits stopBits, int timeoutMs)
        {
            Close();
            SerialPort port = new SerialPort(portName, baudRate, parity, dataBits, stopBits);
            port.ReadTimeout = timeoutMs;
            port.WriteTimeout = timeoutMs;
            // USB-RS485 çeviricilerinin çoğu hattı açık tutmak için carrier sinyallerini ister.
            port.DtrEnable = true;
            port.RtsEnable = true;
            try
            {
                port.Open();
                port.DiscardInBuffer();
                port.DiscardOutBuffer();
                _port = port;
                _io = port.BaseStream;
                _description = FormatSettings(portName, baudRate, parity, dataBits, stopBits);
            }
            catch (Exception)
            {
                try { port.Close(); }
                catch (Exception) { }
                port.Dispose();
                throw;
            }
        }

        /// <summary>Seri port yerine hazır bir akışa bağlanır (test ve sanal COM sürücüleri için).</summary>
        public void Connect(Stream io, string description)
        {
            Close();
            _io = io;
            _description = description;
        }

        public override void Close()
        {
            _io = null;
            _description = "";
            SerialPort port = _port;
            if (port != null)
            {
                _port = null;
                try { if (port.IsOpen) port.Close(); }
                catch (Exception) { }
                port.Dispose();
            }
        }

        public static string FormatSettings(string portName, int baudRate, Parity parity, int dataBits, StopBits stopBits)
        {
            return portName + " " + baudRate + " " + dataBits + ParityLetter(parity) + StopLetter(stopBits);
        }

        public static string ParityLetter(Parity parity)
        {
            switch (parity)
            {
                case Parity.Odd: return "O";
                case Parity.Even: return "E";
                case Parity.Mark: return "M";
                case Parity.Space: return "S";
                default: return "N";
            }
        }

        public static string StopLetter(StopBits stopBits)
        {
            switch (stopBits)
            {
                case StopBits.Two: return "2";
                case StopBits.OnePointFive: return "1.5";
                default: return "1";
            }
        }

        protected override byte[] Transact(byte[] pdu, int timeoutMs)
        {
            Stream io = _io;
            if (io == null) throw new InvalidOperationException("Bağlantı yok.");

            byte[] frame = BuildFrame(UnitId, pdu);
            try
            {
                if (io.CanTimeout)
                {
                    io.ReadTimeout = timeoutMs;
                    io.WriteTimeout = timeoutMs;
                }
                io.Write(frame, 0, frame.Length);
                io.Flush();
                return ReadResponse(io, pdu[0], timeoutMs);
            }
            catch (Exception ex)
            {
                Drain(io);
                throw AsTimeout(ex, timeoutMs);
            }
        }

        private byte[] ReadResponse(Stream io, byte sentFunction, int timeoutMs)
        {
            byte unit = ReadOne(io, timeoutMs);
            if (unit != UnitId)
                throw new IOException("Birim id uyuşmadı (beklenen " + UnitId + ", gelen " + unit + ").");

            byte fc = ReadOne(io, timeoutMs);

            if (fc == (byte)(sentFunction | 0x80))
            {
                byte code = ReadOne(io, timeoutMs);
                VerifyCrc(io, new byte[] { unit, fc, code }, timeoutMs);
                throw new ModbusException(sentFunction, code);
            }
            if (fc != sentFunction)
                throw new IOException("Fonksiyon kodu uyuşmadı: gelen 0x" + fc.ToString("X2") +
                                      ", beklenen 0x" + sentFunction.ToString("X2") + ".");

            byte[] received;
            byte[] body;

            if (fc == 1 || fc == 2 || fc == 3 || fc == 4)
            {
                byte count = ReadOne(io, timeoutMs);
                byte[] data = ReadBlock(io, count, timeoutMs);
                received = new byte[3 + count];
                received[0] = unit;
                received[1] = fc;
                received[2] = count;
                Array.Copy(data, 0, received, 3, count);
                body = new byte[1 + count];
                body[0] = count;
                Array.Copy(data, 0, body, 1, count);
            }
            else
            {
                int length = FixedBodyLength(fc);
                byte[] data = ReadBlock(io, length, timeoutMs);
                received = new byte[2 + length];
                received[0] = unit;
                received[1] = fc;
                Array.Copy(data, 0, received, 2, length);
                body = data;
            }

            VerifyCrc(io, received, timeoutMs);
            return body;
        }

        /// <summary>Bu istemcinin desteklediği sabit gövdeli yazma fonksiyonlarının yanıt uzunluğu.</summary>
        private static int FixedBodyLength(byte functionCode)
        {
            switch (functionCode)
            {
                case 5:
                case 6:
                case 15:
                case 16:
                    return 4;
                default:
                    throw new IOException("FC" + functionCode.ToString("D2") + " için yanıt uzunluğu bilinmiyor.");
            }
        }

        private static void VerifyCrc(Stream io, byte[] received, int timeoutMs)
        {
            byte[] crc = ReadBlock(io, 2, timeoutMs);
            ushort got = (ushort)(crc[0] | (crc[1] << 8));
            ushort want = Crc16(received, 0, received.Length);
            if (got != want)
                throw new IOException("CRC hatalı: beklenen " + want.ToString("X4") + ", gelen " + got.ToString("X4") +
                                      " (gürültü, yanlış hız veya parite olabilir).");
        }

        private static byte ReadOne(Stream io, int timeoutMs) { return ReadBlock(io, 1, timeoutMs)[0]; }

        private static byte[] ReadBlock(Stream io, int count, int timeoutMs)
        {
            byte[] buf = new byte[count];
            int off = 0;
            while (off < count)
            {
                int n;
                try { n = io.Read(buf, off, count - off); }
                catch (Exception ex) { throw AsTimeout(ex, timeoutMs); }
                if (n <= 0) throw new IOException("Seri akış kapandı.");
                off += n;
            }
            return buf;
        }

        private static Exception AsTimeout(Exception ex, int timeoutMs)
        {
            if (ex is TimeoutException)
                return new TimeoutException("Yanıt gelmedi (" + timeoutMs + " ms) — cihaz adresi, hız ve pariteyi kontrol et.");

            IOException io = ex as IOException;
            if (io != null && io.InnerException is TimeoutException)
                return new TimeoutException("Yanıt gelmedi (" + timeoutMs + " ms) — cihaz adresi, hız ve pariteyi kontrol et.");

            return ex;
        }

        /// <summary>Zaman aşımında gelen yarım çerçeve bir sonraki işlemi bozmasın.</summary>
        private void Drain(Stream io)
        {
            try
            {
                SerialPort port = _port;
                if (port != null)
                {
                    if (port.IsOpen) port.DiscardInBuffer();
                    return;
                }
                if (!io.CanTimeout) return;
                int previous = io.ReadTimeout;
                io.ReadTimeout = 10;
                try
                {
                    while (io.ReadByte() >= 0) { }
                }
                finally
                {
                    io.ReadTimeout = previous;
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>RTU çerçevesi kurar: birim id + PDU + CRC16 (düşük bayt önce).</summary>
        public static byte[] BuildFrame(byte unitId, byte[] pdu)
        {
            byte[] frame = new byte[pdu.Length + 3];
            frame[0] = unitId;
            Array.Copy(pdu, 0, frame, 1, pdu.Length);
            ushort crc = Crc16(frame, 0, frame.Length - 2);
            frame[frame.Length - 2] = (byte)(crc & 0xFF);
            frame[frame.Length - 1] = (byte)((crc >> 8) & 0xFF);
            return frame;
        }

        public static bool HasValidCrc(byte[] frame)
        {
            if (frame == null || frame.Length < 3) return false;
            ushort got = (ushort)(frame[frame.Length - 2] | (frame[frame.Length - 1] << 8));
            return got == Crc16(frame, 0, frame.Length - 2);
        }

        /// <summary>Modbus RTU CRC16: polinom 0xA001 (ters 0x8005), başlangıç 0xFFFF.</summary>
        public static ushort Crc16(byte[] data, int offset, int count)
        {
            ushort crc = 0xFFFF;
            for (int i = 0; i < count; i++)
            {
                crc ^= data[offset + i];
                for (int bit = 0; bit < 8; bit++)
                {
                    if ((crc & 0x0001) != 0) crc = (ushort)((crc >> 1) ^ 0xA001);
                    else crc = (ushort)(crc >> 1);
                }
            }
            return crc;
        }
    }
}
