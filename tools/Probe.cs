using System;
using System.Text;
using ModbusScan;

internal static class Probe
{
    private static int Main(string[] args)
    {
        string ip = "127.0.0.1";
        int port = 502, address = 0, quantity = 10, timeout = 2000;
        byte fc = 3, unit = 1;
        WordOrder order = WordOrder.ABCD;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--ip": ip = args[++i]; break;
                case "--port": port = int.Parse(args[++i]); break;
                case "--unit": unit = byte.Parse(args[++i]); break;
                case "--fc": fc = byte.Parse(args[++i]); break;
                case "--addr": address = int.Parse(args[++i]); break;
                case "--qty": quantity = int.Parse(args[++i]); break;
                case "--timeout": timeout = int.Parse(args[++i]); break;
                case "--order": order = (WordOrder)Enum.Parse(typeof(WordOrder), args[++i]); break;
                default: Console.Error.WriteLine("Bilinmeyen param: " + args[i]); return 2;
            }
        }

        try
        {
            using (ModbusTcpClient client = new ModbusTcpClient())
            {
                client.UnitId = unit;
                client.Connect(ip, port, timeout);
                RegisterRead read = client.Read(fc, address, quantity, timeout);

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
}
