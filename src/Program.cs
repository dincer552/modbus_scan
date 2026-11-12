using System;
using System.Reflection;
using System.Windows.Forms;

[assembly: AssemblyVersion("0.8.0")]
[assembly: AssemblyFileVersion("0.8.0")]
[assembly: AssemblyTitle("Modbus Test")]
[assembly: AssemblyProduct("ModbusScan")]

namespace ModbusScan
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }
}
