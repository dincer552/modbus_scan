using System;
using System.IO.Ports;
using System.Text;
using ModbusScan;

internal static class Probe
{
    private static int Main(string[] args)
    {
        string ip = "127.0.0.1";
        string com = null, parity = "N", stop = "1";
        int port = 502, baud = 9600, address = 0, quantity = 10, timeout = 2000;
        byte fc = 3, unit = 1;
        WordOrder order = WordOrder.ABCD;
        bool tolerant = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--list-ports":
                    {
                        string[] found = SerialPort.GetPortNames();
                        Array.Sort(found);
                        Console.WriteLine("ports=" + string.Join(",", found));
                        return 0;
                    }
                case "--ip": ip = args[++i]; break;
                case "--port": port = int.Parse(args[++i]); break;
                case "--com": com = args[++i]; break;
                case "--baud": baud = int.Parse(args[++i]); break;
                case "--parity": parity = args[++i]; break;
                case "--stop": stop = args[++i]; break;
                case "--unit": unit = byte.Parse(args[++i]); break;
                case "--fc": fc = byte.Parse(args[++i]); break;
                case "--addr": address = int.Parse(args[++i]); break;
                case "--qty": quantity = int.Parse(args[++i]); break;
                case "--timeout": timeout = int.Parse(args[++i]); break;
                case "--order": order = (WordOrder)Enum.Parse(typeof(WordOrder), args[++i]); break;
                case "--tolerant": tolerant = true; break;
                default: Console.Error.WriteLine("Bilinmeyen param: " + args[i]); return 2;
            }
        }

        try
        {
            ModbusClient client = com == null ? (ModbusClient)new ModbusTcpClient() : new ModbusRtuClient();
            using (client)
            {
                client.UnitId = unit;
                ModbusTcpClient tcp = client as ModbusTcpClient;
                if (tcp != null) tcp.Connect(ip, port, timeout);
                else ((ModbusRtuClient)client).Connect(com, baud, ParityOf(parity), 8, StopOf(stop), timeout);

                Console.WriteLine("baglanti=" + client.Description);
                RegisterRead read = tolerant ? client.ReadTolerant(fc, address, quantity, timeout)
                                             : client.Read(fc, address, quantity, timeout);
                if (tolerant)
                {
                    Console.WriteLine("istek=" + read.Requests + " hatali=" + read.ErrorCount +
                                      (read.StopReason == null ? "" : " durma=" + read.StopReason));
                    for (int i = 0; i < read.Quantity; i++)
                        if (read.Failed(i)) Console.WriteLine("hata[" + (address + i) + "] " + read.ItemErrors[i]);
                }

                Console.WriteLine("data=" + read.Data.Length + " bytes");
                StringBuilder hex = new StringBuilder();
                for (int i = 0; i < read.Data.Length; i++)
                {
                    if (i % 2 == 0 && i > 0) hex.Append(' ');
                    hex.Append(read.Data[i].ToString("X2"));
                }
                Console.WriteLine("hex=" + hex);

                bool bits = fc == 1 || fc == 2;
                if (bits)
                {
                    StringBuilder sb = new StringBuilder();
                    for (int i = 0; i < read.Quantity; i++)
                    {
                        sb.Append((read.Data[i / 8] >> (i % 8)) & 1);
                        if (i < read.Quantity - 1) sb.Append(' ');
                    }
                    Console.WriteLine("bits=" + sb);
                }
                else
                {
                    for (int i = 0; i + 1 < read.Data.Length; i += 2)
                    {
                        Console.WriteLine("reg[" + (address + i / 2) + "] u=" + ModbusTcpClient.UInt16(read.Data, i) +
                                          " i=" + ModbusTcpClient.Int16(read.Data, i));
                    }
                    if (read.Data.Length >= 4)
                    {
                        Console.WriteLine("float(" + order + ")=" +
                            ModbusTcpClient.Float32(read.Data, 0, order).ToString("0.000###"));
                        Console.WriteLine("uint32(" + order + ")=" + ModbusTcpClient.UInt32(read.Data, 0, order));
                    }
                }

                client.Close();
                return 0;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("HATA: " + ex.Message);
            return 1;
        }
    }

    private static Parity ParityOf(string text)
    {
        switch (text.ToUpperInvariant())
        {
            case "O": case "ODD": return Parity.Odd;
            case "E": case "EVEN": return Parity.Even;
            case "N": case "NONE": return Parity.None;
            default: throw new ArgumentException("Parite N/O/E olmalı: " + text);
        }
    }

    private static StopBits StopOf(string text)
    {
        switch (text)
        {
            case "1": return StopBits.One;
            case "1.5": return StopBits.OnePointFive;
            case "2": return StopBits.Two;
            default: throw new ArgumentException("Durdurma biti 1/1.5/2 olmalı: " + text);
        }
    }
}
