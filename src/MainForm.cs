using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.IO.Ports;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Windows.Forms;

namespace ModbusScan
{
    public class MainForm : Form
    {
        private readonly ModbusTcpClient _tcp = new ModbusTcpClient();
        private readonly ModbusRtuClient _rtu = new ModbusRtuClient();

        private GroupBox gbConnection;
        private GroupBox gbRead;
        private TextBox txtIp, txtPort, txtUnit, txtTimeout, txtAddress, txtQuantity, txtLog;
        private ComboBox cmbMode, cmbSerialPort, cmbBaud, cmbParity, cmbStop, cmbFunction, cmbDecode, cmbWordOrder;
        private Label lblIp, lblTcpPort, lblUnit, lblMs, lblCom, lblBaud, lblParity, lblStop;
        private CheckBox chkConvention;
        private Button btnConnect, btnDisconnect, btnRead, btnClear;
        private Label lblStatus, lblSummary;
        private Label lblScanLive;
        private Label lblUpdate;
        private Button btnCheckUpdate, btnUpdate;
        private Timer updateTimer;
        private Timer firstCheckTimer;
        private UpdateInfo _updateInfo;
        private bool _updateCheckRunning;
        private bool _updateInstallRunning;
        private DataGridView grid;
        private readonly Dictionary<string, string> _definitions = new Dictionary<string, string>();
        private readonly Dictionary<string, string> _portLabels = new Dictionary<string, string>();
        private string _portLabelKey = "";
        private ContextMenuStrip _definitionMenu;
        private ContextMenuStrip _appMenu;
        private Icon _appIcon;

        // Tarama planı
        private GroupBox gbScan;
        private DataGridView scanGrid;
        private Button btnScanAdd, btnScanRemove, btnScanRun, btnScanStop;
        private DataGridView scanResultGrid;
        private Button btnScanBack;
        private Panel scanOptions;
        private TextBox txtScanUnitStart, txtScanUnitEnd, txtScanTimeout;
        private Panel scanSerial;
        private CheckBox[] _scanBaudChecks, _scanParityChecks, _scanStopChecks;
        private Label lblScanSerialInfo;
        private static readonly int[] ScanBaudRates = { 1200, 2400, 4800, 9600, 19200, 38400, 57600, 115200 };
        private static readonly string[] ScanParityNames = { "Yok", "Tek", "Çift" };
        private static readonly string[] ScanStopNames = { "1", "1.5", "2" };
        private sealed class SerialCombo
        {
            public int Baud;
            public Parity Parity;
            public StopBits Stop;
            public string Text;
        }
        private sealed class ScanSegment
        {
            public bool Enabled;
            public string Name;
            public byte Function;     // 1,2,3,4
            public int StartAddress;
            public int Quantity;
            public int DecodeIndex;   // cmbDecode index
            public int WordOrderIndex; // cmbWordOrder index
            public bool Show4xx;
            public string ToLine()
            {
                return (Enabled ? "1" : "0") + "\t" + Name + "\t" + Function + "\t" + StartAddress + "\t" + Quantity + "\t" + DecodeIndex + "\t" + WordOrderIndex + "\t" + (Show4xx ? "1" : "0");
            }
            public static ScanSegment Parse(string line)
            {
                string[] p = line.Split('\t');
                if (p.Length < 8) return null;
                ScanSegment s = new ScanSegment();
                s.Enabled = p[0] == "1";
                s.Name = p[1];
                byte fc; int sa, q, di, woi;
                if (!byte.TryParse(p[2], out fc)) return null;
                if (!int.TryParse(p[3], out sa)) return null;
                if (!int.TryParse(p[4], out q)) return null;
                if (!int.TryParse(p[5], out di)) return null;
                if (!int.TryParse(p[6], out woi)) return null;
                s.Function = fc;
                s.StartAddress = sa;
                s.Quantity = q;
                s.DecodeIndex = di;
                s.WordOrderIndex = woi;
                s.Show4xx = p[7] == "1";
                return s;
            }
        }
        private readonly List<ScanSegment> _scanSegments = new List<ScanSegment>();
        private volatile bool _scanStopRequested;

        // EBM fan adresleme
        private GroupBox gbEbm;
        private Button btnScanToggle;
        private Button btnEbmEnter, btnEbmBack, btnEbmStart25, btnEbmSaveAddr;
        private TextBox txtEbmStatus, txtEbmFoundAddr, txtEbmSifre;
        private CheckBox chkEbmFc16;
        private Label lblEbmAddr, lblEbmSifre;

        // Ayar kalıcılığı
        private static string SettingsPath()
        {
            string over = Environment.GetEnvironmentVariable("MODBUSSCAN_SETTINGS");
            if (over != null && over.Trim().Length > 0) return over;
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ModbusScan");
            return Path.Combine(dir, "settings.txt");
        }

        private void LoadSettings()
        {
            try
            {
                string path = SettingsPath();
                if (!File.Exists(path)) return;
                string[] lines = File.ReadAllLines(path, new UTF8Encoding(false));
                foreach (string line in lines)
                {
                    string[] parts = line.Split(new char[] { '=' }, 2);
                    if (parts.Length != 2) continue;
                    string key = parts[0].Trim();
                    string val = parts[1].Trim();
                    switch (key)
                    {
                        case "mode": cmbMode.SelectedIndex = val == "RS485" ? 1 : 0; break;
                        case "ip": txtIp.Text = val; break;
                        case "port": txtPort.Text = val; break;
                        case "unit": txtUnit.Text = val; break;
                        case "timeout": txtTimeout.Text = val; break;
                        case "com": SetComboByText(cmbSerialPort, val); break;
                        case "baud": SetComboByText(cmbBaud, val); break;
                        case "parity": SetComboByText(cmbParity, val); break;
                        case "stop": SetComboByText(cmbStop, val); break;
                        case "function": SetComboByText(cmbFunction, val); break;
                        case "address": txtAddress.Text = val; break;
                        case "quantity": txtQuantity.Text = val; break;
                        case "decode": SetComboByText(cmbDecode, val); break;
                        case "wordorder": SetComboByText(cmbWordOrder, val); break;
                        case "convention": chkConvention.Checked = val == "1"; break;
                        case "scanunitstart": txtScanUnitStart.Text = val; break;
                        case "scanunitend": txtScanUnitEnd.Text = val; break;
                        case "scantimeout": txtScanTimeout.Text = val; break;
                        case "scanbauds": ApplyScanChecks(_scanBaudChecks, ScanBaudRates, val); break;
                        case "scanparities": ApplyScanChecks(_scanParityChecks, ScanParityNames, val); break;
                        case "scanstops": ApplyScanChecks(_scanStopChecks, ScanStopNames, val); break;
                    }
                }
            }
            catch (Exception) { }
        }

        private static void SetComboByText(ComboBox cmb, string val)
        {
            for (int i = 0; i < cmb.Items.Count; i++)
            {
                if (string.Equals(Convert.ToString(cmb.Items[i]), val, StringComparison.OrdinalIgnoreCase))
                {
                    cmb.SelectedIndex = i;
                    return;
                }
            }
        }

        private static void ApplyScanChecks(CheckBox[] checks, int[] values, string csv)
        {
            string[] parts = csv.Split(',');
            for (int i = 0; i < checks.Length; i++)
            {
                bool found = false;
                for (int j = 0; j < parts.Length; j++)
                    if (parts[j].Trim() == values[i].ToString()) { found = true; break; }
                checks[i].Checked = found;
            }
        }

        private static void ApplyScanChecks(CheckBox[] checks, string[] values, string csv)
        {
            string[] parts = csv.Split(',');
            for (int i = 0; i < checks.Length; i++)
            {
                bool found = false;
                for (int j = 0; j < parts.Length; j++)
                    if (string.Equals(parts[j].Trim(), values[i], StringComparison.OrdinalIgnoreCase)) { found = true; break; }
                checks[i].Checked = found;
            }
        }

        private static string CheckedCsv(CheckBox[] checks, int[] values)
        {
            List<string> sel = new List<string>();
            for (int i = 0; i < checks.Length; i++)
                if (checks[i].Checked) sel.Add(values[i].ToString());
            return string.Join(",", sel.ToArray());
        }

        private static string CheckedCsv(CheckBox[] checks, string[] values)
        {
            List<string> sel = new List<string>();
            for (int i = 0; i < checks.Length; i++)
                if (checks[i].Checked) sel.Add(values[i]);
            return string.Join(",", sel.ToArray());
        }

        private void SaveSettings()
        {
            try
            {
                string path = SettingsPath();
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                List<string> lines = new List<string>
                {
                    "mode=" + (SelectedIsRtu() ? "RS485" : "TCP"),
                    "ip=" + txtIp.Text.Trim(),
                    "port=" + txtPort.Text.Trim(),
                    "unit=" + txtUnit.Text.Trim(),
                    "timeout=" + txtTimeout.Text.Trim(),
                    "com=" + cmbSerialPort.Text.Trim(),
                    "baud=" + cmbBaud.Text.Trim(),
                    "parity=" + cmbParity.Text.Trim(),
                    "stop=" + cmbStop.Text.Trim(),
                    "function=" + cmbFunction.Text.Trim(),
                    "address=" + txtAddress.Text.Trim(),
                    "quantity=" + txtQuantity.Text.Trim(),
                    "decode=" + cmbDecode.Text.Trim(),
                    "wordorder=" + cmbWordOrder.Text.Trim(),
                    "convention=" + (chkConvention.Checked ? "1" : "0"),
                    "scanunitstart=" + txtScanUnitStart.Text.Trim(),
                    "scanunitend=" + txtScanUnitEnd.Text.Trim(),
                    "scantimeout=" + txtScanTimeout.Text.Trim(),
                    "scanbauds=" + CheckedCsv(_scanBaudChecks, ScanBaudRates),
                    "scanparities=" + CheckedCsv(_scanParityChecks, ScanParityNames),
                    "scanstops=" + CheckedCsv(_scanStopChecks, ScanStopNames)
                };
                File.WriteAllLines(path, lines.ToArray(), new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                Log("Ayarlar kaydedilemedi: " + ex.Message);
            }
        }

        public MainForm()
        {
            BuildUi();
            LoadDefinitions();
            LoadScanPlan();
            LoadSettings();
            ApplyMode();
            UpdateConnectionState(false);
            RelayoutPanels();
        }

        private ModbusClient Client
        {
            get { return SelectedIsRtu() ? (ModbusClient)_rtu : _tcp; }
        }

        private bool SelectedIsRtu()
        {
            return cmbMode.SelectedIndex == 1;
        }

        private void BuildUi()
        {
            Text = "Modbus Test v" + typeof(Program).Assembly.GetName().Version.ToString(3);
            ClientSize = new Size(500, 600);
            // Varsayılan 500x600 ama kullanıcı kenardan tutup büyütebilsin; öğeler anchor ile dağılıyor.
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = true;
            // MinimumSize çerçeve payını da içerir; pay düşülmezse küçültme sınırı yüzünden
            // istenen 500 px'lik içerik genisligine inilemiyor (ClientSize 502'de takılıyor).
            MinimumSize = new Size(ClientSize.Width + (Size.Width - ClientSize.Width), 430);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 8.25f);
            LoadAppIcon();

            gbConnection = new GroupBox();
            gbConnection.Text = "Bağlantı";
            gbConnection.SetBounds(8, 4, 484, 74);
            gbConnection.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

            // Başlık satırındaki seçim kutusu grubun içeriğini TCP / RS485 arasında değiştirir.
            cmbMode = new ComboBox();
            cmbMode.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbMode.SetBounds(66, 1, 64, 22);
            cmbMode.Items.AddRange(new object[] { "TCP", "RS485" });
            cmbMode.SelectedIndex = 0;
            cmbMode.SelectedIndexChanged += OnModeChanged;
            gbConnection.Controls.Add(cmbMode);

            lblIp = MakeLabel("IP", 10, 28);
            txtIp = MakeTextBox("127.0.0.1", 30, 25, 92);
            lblTcpPort = MakeLabel("Port", 128, 28);
            txtPort = MakeTextBox("502", 158, 25, 38);
            lblUnit = MakeLabel("ID", 202, 28);
            txtUnit = MakeTextBox("1", 216, 25, 30);
            lblMs = MakeLabel("ms", 252, 28);
            txtTimeout = MakeTextBox("2000", 268, 25, 40);
            gbConnection.Controls.Add(lblIp);
            gbConnection.Controls.Add(txtIp);
            gbConnection.Controls.Add(lblTcpPort);
            gbConnection.Controls.Add(txtPort);
            gbConnection.Controls.Add(lblUnit);
            gbConnection.Controls.Add(txtUnit);
            gbConnection.Controls.Add(lblMs);
            gbConnection.Controls.Add(txtTimeout);

            lblCom = MakeLabel("COM", 10, 28);
            cmbSerialPort = new ComboBox();
            cmbSerialPort.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbSerialPort.SetBounds(42, 25, 68, 22);
            // Açılır listede port adının sağına sürücü açıklaması yazılır (COM14 = USB-SERIAL Ch A).
            cmbSerialPort.DrawMode = DrawMode.OwnerDrawFixed;
            cmbSerialPort.ItemHeight = 16;
            cmbSerialPort.DrawItem += OnPortDrawItem;
            // Listeyi açtıkca yeniden sorgula: dönüştürücü takılıp çıkarılabiliyor.
            cmbSerialPort.DropDown += delegate { RefreshPorts(); };
            lblBaud = MakeLabel("Hz", 114, 28);
            cmbBaud = new ComboBox();
            cmbBaud.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbBaud.SetBounds(130, 25, 54, 22);
            cmbBaud.Items.AddRange(new object[] { "1200", "2400", "4800", "9600", "19200", "38400", "57600", "115200" });
            cmbBaud.SelectedIndex = 3;
            lblParity = MakeLabel("Par", 188, 28);
            cmbParity = new ComboBox();
            cmbParity.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbParity.SetBounds(212, 25, 50, 22);
            cmbParity.Items.AddRange(new object[] { "Yok", "Tek", "Çift" });
            cmbParity.SelectedIndex = 0;
            lblStop = MakeLabel("Dur", 266, 28);
            cmbStop = new ComboBox();
            cmbStop.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbStop.SetBounds(290, 25, 40, 22);
            cmbStop.Items.AddRange(new object[] { "1", "1.5", "2" });
            cmbStop.SelectedIndex = 0;
            gbConnection.Controls.Add(lblCom);
            gbConnection.Controls.Add(cmbSerialPort);
            gbConnection.Controls.Add(lblBaud);
            gbConnection.Controls.Add(cmbBaud);
            gbConnection.Controls.Add(lblParity);
            gbConnection.Controls.Add(cmbParity);
            gbConnection.Controls.Add(lblStop);
            gbConnection.Controls.Add(cmbStop);

            btnConnect = new Button();
            btnConnect.Text = "Bağlan";
            btnConnect.SetBounds(314, 23, 74, 25);
            btnConnect.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnConnect.Click += OnConnect;
            gbConnection.Controls.Add(btnConnect);

            btnDisconnect = new Button();
            btnDisconnect.Text = "Kes";
            btnDisconnect.SetBounds(392, 23, 80, 25);
            btnDisconnect.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnDisconnect.Click += OnDisconnect;
            btnDisconnect.Enabled = false;
            gbConnection.Controls.Add(btnDisconnect);

            lblStatus = new Label();
            lblStatus.SetBounds(10, 50, 462, 16);
            lblStatus.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            lblStatus.Text = "Durum: bağlı değil";
            lblStatus.ForeColor = Color.Gray;
            gbConnection.Controls.Add(lblStatus);

            gbRead = new GroupBox();
            gbRead.Text = "Okuma";
            gbRead.SetBounds(8, 82, 484, 112);
            gbRead.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

            cmbFunction = new ComboBox();
            cmbFunction.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbFunction.SetBounds(10, 20, 190, 22);
            cmbFunction.Items.AddRange(new object[] {
                "03 - Holding Register",
                "04 - Input Register",
                "01 - Coil",
                "02 - Discrete Input"
            });
            cmbFunction.SelectedIndex = 0;
            cmbFunction.SelectedIndexChanged += OnFunctionChanged;
            gbRead.Controls.Add(cmbFunction);

            chkConvention = new CheckBox();
            chkConvention.Text = "4xxxx göster";
            chkConvention.SetBounds(208, 23, 96, 18);
            chkConvention.Checked = true;
            chkConvention.CheckedChanged += OnRedisplay;
            gbRead.Controls.Add(chkConvention);

            btnRead = new Button();
            btnRead.Text = "OKU";
            btnRead.SetBounds(312, 19, 76, 25);
            btnRead.Enabled = false;
            btnRead.Click += OnRead;
            gbRead.Controls.Add(btnRead);

            btnClear = new Button();
            btnClear.Text = "Temizle";
            btnClear.SetBounds(392, 19, 80, 25);
            btnClear.Click += OnClear;
            gbRead.Controls.Add(btnClear);

            gbRead.Controls.Add(MakeLabel("Adres", 10, 52));
            txtAddress = MakeTextBox("0", 52, 49, 56);
            gbRead.Controls.Add(txtAddress);
            gbRead.Controls.Add(MakeLabel("Adet", 116, 52));
            txtQuantity = MakeTextBox("10", 142, 49, 44);
            gbRead.Controls.Add(txtQuantity);

            gbRead.Controls.Add(MakeLabel("Değer", 194, 52));
            cmbDecode = new ComboBox();
            cmbDecode.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbDecode.SetBounds(230, 49, 116, 22);
            cmbDecode.Items.AddRange(new object[] {
                "Ondalık (uint16)",
                "İşaretli (int16)",
                "Hex",
                "Float32",
                "Uint32",
                "Int32"
            });
            cmbDecode.SelectedIndex = 0;
            cmbDecode.SelectedIndexChanged += OnRedisplay;
            gbRead.Controls.Add(cmbDecode);

            Label lblWords = MakeLabel("Kelime", 352, 52);
            gbRead.Controls.Add(lblWords);
            cmbWordOrder = new ComboBox();
            cmbWordOrder.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbWordOrder.SetBounds(396, 49, 76, 22);
            cmbWordOrder.Items.AddRange(new object[] { "ABCD", "CDAB", "BADC", "DCBA" });
            cmbWordOrder.SelectedIndex = 0;
            cmbWordOrder.SelectedIndexChanged += OnRedisplay;
            gbRead.Controls.Add(cmbWordOrder);

            lblSummary = new Label();
            lblSummary.SetBounds(10, 80, 462, 16);
            lblSummary.Text = "—";
            lblSummary.TextAlign = ContentAlignment.MiddleLeft;
            lblSummary.ForeColor = Color.DimGray;
            gbRead.Controls.Add(lblSummary);

            // Buyume davranisi: sagdaki buton/secim sag kenara, soldaki alanlar sol kenara yapik kalir.
            btnRead.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnClear.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            lblSummary.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            lblWords.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            cmbWordOrder.Anchor = AnchorStyles.Top | AnchorStyles.Right;

            lblUpdate = new Label();
            lblUpdate.SetBounds(8, 229, 296, 16);
            lblUpdate.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            lblUpdate.Text = "Güncelleme: henüz denetlenmedi";
            lblUpdate.ForeColor = Color.Gray;

            btnCheckUpdate = new Button();
            btnCheckUpdate.Text = "Denetle";
            btnCheckUpdate.SetBounds(310, 224, 82, 25);
            btnCheckUpdate.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnCheckUpdate.Click += OnCheckUpdate;

            btnUpdate = new Button();
            btnUpdate.Text = "Güncelle";
            btnUpdate.SetBounds(396, 224, 96, 25);
            btnUpdate.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnUpdate.Enabled = false;
            btnUpdate.Click += OnInstallUpdate;

            grid = new DefinitionGrid();
            grid.SetBounds(8, 254, 484, 218);
            grid.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            grid.AllowUserToAddRows = false;
            grid.AllowUserToDeleteRows = false;
            grid.AllowUserToResizeRows = false;
            grid.RowHeadersVisible = false;
            grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            grid.MultiSelect = false;
            grid.AllowUserToResizeColumns = true;
            grid.ScrollBars = ScrollBars.Vertical;
            grid.BackgroundColor = Color.White;
            grid.BorderStyle = BorderStyle.Fixed3D;
            grid.Font = new Font("Consolas", 9f);
            grid.Columns.Add(NewColumn("colIndex", "#", 28, DataGridViewAutoSizeColumnMode.None, true));
            grid.Columns.Add(NewColumn("colAddress", "Adres", 62, DataGridViewAutoSizeColumnMode.None, true));
            grid.Columns.Add(NewColumn("colValue", "Değer", 110, DataGridViewAutoSizeColumnMode.None, true));

            DataGridViewTextBoxColumn colDef = NewColumn("colDef", "Tanım", 260, DataGridViewAutoSizeColumnMode.Fill, false);
            colDef.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleLeft;
            colDef.ToolTipText = "Kullanıcı notu. Hücreyi seç, yaz veya Excel'de kopyaladığın tek sütunu Ctrl+V ile buraya yapıştır.";
            grid.Columns.Add(colDef);

            grid.CellContentDoubleClick += OnGridCellDoubleClick;
            grid.CellEndEdit += OnDefinitionCellEndEdit;
            ((DefinitionGrid)grid).PasteDefinitionsHandler = PasteDefinitions;
            ((DefinitionGrid)grid).DeleteDefinitionHandler = ClearCurrentDefinition;

            _definitionMenu = new ContextMenuStrip();
            _definitionMenu.Items.Add("Bu satırın tanımını temizle", null, OnClearDefinitionRow);
            _definitionMenu.Items.Add("Tanım sütunundaki tüm yazıları temizle", null, OnClearAllDefinitions);
            _definitionMenu.Opening += OnDefinitionMenuOpening;
            grid.ContextMenuStrip = _definitionMenu;

            btnScanToggle = new Button();
            btnScanToggle.Text = "Tarama";
            btnScanToggle.SetBounds(8, 196, 90, 23);
            btnScanToggle.Click += OnScanToggle;

            btnEbmEnter = new Button();
            btnEbmEnter.Text = "EBM Adresleme";
            btnEbmEnter.SetBounds(104, 196, 110, 23);
            btnEbmEnter.Click += OnEbmEnter;

            BuildAppMenu();
            BuildScanUi();
            BuildEbmUi();

            txtLog = new TextBox();
            txtLog.SetBounds(8, 502, 484, 90);
            txtLog.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            txtLog.Multiline = true;
            txtLog.ReadOnly = true;
            txtLog.ScrollBars = ScrollBars.Vertical;
            txtLog.BackColor = Color.White;
            txtLog.Font = new Font("Consolas", 8.25f);

            // Alt canlı tarama satırı: o an hangi mod/kombinasyon ve hangi ID/adres taranıyor.
            lblScanLive = new Label();
            lblScanLive.SetBounds(8, 486, 484, 14);
            lblScanLive.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            lblScanLive.Font = new Font("Segoe UI", 8.25f);
            lblScanLive.ForeColor = Color.FromArgb(0, 90, 158);
            lblScanLive.Text = "";

            Controls.Add(gbConnection);
            Controls.Add(gbRead);
            Controls.Add(btnScanToggle);
            Controls.Add(btnEbmEnter);
            Controls.Add(gbScan);
            Controls.Add(gbEbm);
            Controls.Add(lblUpdate);
            Controls.Add(btnCheckUpdate);
            Controls.Add(btnUpdate);
            Controls.Add(grid);
            Controls.Add(scanResultGrid);
            Controls.Add(txtLog);
            Controls.Add(lblScanLive);

            UpdateDecodeControls();
            AcceptButton = btnRead;
        }

        /// <summary>
        /// Pencere ve görev çubuğu simgesi: build.bat /win32icon ile exe'ye gömülen assets\ModbusScan.ico.
        /// Assembly konumundan okunur; kısayoldan veya yansıtarak (test) çalıştırıldığında da doğru exe bulunur.
        /// </summary>
        private void LoadAppIcon()
        {
            try
            {
                string path = typeof(MainForm).Assembly.Location;
                if (path.Length == 0 || !File.Exists(path)) return;
                Icon icon = Icon.ExtractAssociatedIcon(path);
                if (icon == null) return;
                _appIcon = icon;
                Icon = icon;
            }
            catch (Exception)
            {
                // Simgesiz de çalışır: Windows varsayılan uygulama simgesi kalır.
            }
        }

        /// <summary>Pencere sag tik menuyu: masaustu ve Baslat Menuusu kisayol islemleri.</summary>
        private void BuildAppMenu()
        {
            _appMenu = new ContextMenuStrip();
            _appMenu.Items.Add("Masaüstü + Başlat Menüsü kısayolu oluştur", null, OnCreateShortcuts);
            _appMenu.Items.Add("Kısayolları kaldır", null, OnRemoveShortcuts);
            ContextMenuStrip = _appMenu;
            // Özet satırı her zaman görünür: butonların yerini bozmadan menüye oradan da erişilebiliyor.
            lblSummary.ContextMenuStrip = _appMenu;
        }

        /// <summary>Tarama planı paneli: segment listesi + TARA butonu.</summary>
        private void BuildScanUi()
        {
            gbScan = new GroupBox();
            gbScan.Text = "Tarama";
            gbScan.SetBounds(8, 224, 484, 28);
            gbScan.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            gbScan.Visible = false;

            // İçerik: sol tarafta bir DataGridView (segmentler), sağ tarafta butonlar
            scanGrid = new DataGridView();
            scanGrid.SetBounds(6, 18, 380, 160);
            scanGrid.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            scanGrid.VirtualMode = true;
            scanGrid.AllowUserToAddRows = false;
            scanGrid.AllowUserToDeleteRows = false;
            scanGrid.AllowUserToResizeRows = false;
            scanGrid.RowHeadersVisible = false;
            scanGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            scanGrid.MultiSelect = false;
            scanGrid.AllowUserToResizeColumns = true;
            scanGrid.ScrollBars = ScrollBars.Vertical;
            scanGrid.BackgroundColor = Color.White;
            scanGrid.BorderStyle = BorderStyle.Fixed3D;
            scanGrid.Font = new Font("Consolas", 8.25f);
            scanGrid.Columns.Add(NewColumn("scEnabled", "✓", 28, DataGridViewAutoSizeColumnMode.None, true));
            scanGrid.Columns.Add(NewColumn("scName", "Ad", 70, DataGridViewAutoSizeColumnMode.None, false));
            scanGrid.Columns.Add(NewColumn("scFunc", "FC", 30, DataGridViewAutoSizeColumnMode.None, true));
            scanGrid.Columns.Add(NewColumn("scStart", "Başlangıç", 60, DataGridViewAutoSizeColumnMode.None, true));
            scanGrid.Columns.Add(NewColumn("scQty", "Adet", 44, DataGridViewAutoSizeColumnMode.None, true));
            scanGrid.Columns.Add(NewColumn("scDecode", "Çözümleme", 90, DataGridViewAutoSizeColumnMode.None, true));
            scanGrid.Columns.Add(NewColumn("scWords", "Kelime", 50, DataGridViewAutoSizeColumnMode.None, true));
            scanGrid.Columns.Add(NewColumn("sc4xx", "4xx", 30, DataGridViewAutoSizeColumnMode.None, true));
            scanGrid.CellValueNeeded += ScanGrid_CellValueNeeded;
            scanGrid.CellValuePushed += ScanGrid_CellValuePushed;
            scanGrid.CurrentCellDirtyStateChanged += ScanGrid_CurrentCellDirtyStateChanged;
            scanGrid.CellMouseDoubleClick += ScanGrid_CellMouseDoubleClick;

            btnScanAdd = new Button();
            btnScanAdd.Text = "Ekle";
            btnScanAdd.SetBounds(392, 18, 84, 23);
            btnScanAdd.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnScanAdd.Click += OnScanAdd;

            btnScanRemove = new Button();
            btnScanRemove.Text = "Sil";
            btnScanRemove.SetBounds(392, 44, 84, 23);
            btnScanRemove.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnScanRemove.Click += OnScanRemove;

            btnScanRun = new Button();
            btnScanRun.Text = "TARA";
            btnScanRun.SetBounds(392, 70, 84, 28);
            btnScanRun.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnScanRun.BackColor = Color.FromArgb(0, 128, 0);
            btnScanRun.ForeColor = Color.White;
            btnScanRun.Click += OnScanRun;

            btnScanStop = new Button();
            btnScanStop.Text = "DURDUR";
            btnScanStop.SetBounds(392, 70, 84, 28);
            btnScanStop.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnScanStop.BackColor = Color.FromArgb(128, 0, 0);
            btnScanStop.ForeColor = Color.White;
            btnScanStop.Visible = false;
            btnScanStop.Click += OnScanStop;

            btnScanBack = new Button();
            btnScanBack.Text = "Tabloya dön";
            btnScanBack.SetBounds(392, 102, 84, 23);
            btnScanBack.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnScanBack.Visible = false;
            btnScanBack.Click += OnScanBack;

            // Cihaz keşfi seçenekleri: taranacak ünite ID aralığı ve keşif zaman aşımı.
            scanOptions = new Panel();
            scanOptions.SetBounds(6, 182, 380, 24);
            scanOptions.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            scanOptions.Controls.Add(MakeLabel("ID", 2, 4));
            txtScanUnitStart = MakeTextBox("1", 22, 0, 40);
            scanOptions.Controls.Add(txtScanUnitStart);
            scanOptions.Controls.Add(MakeLabel("–", 66, 4));
            txtScanUnitEnd = MakeTextBox("247", 78, 0, 40);
            scanOptions.Controls.Add(txtScanUnitEnd);
            scanOptions.Controls.Add(MakeLabel("ms", 124, 4));
            txtScanTimeout = MakeTextBox("150", 142, 0, 44);
            scanOptions.Controls.Add(txtScanTimeout);
            Label lblScanHint = MakeLabel("cihaz keşfi: bu ID aralığı taranır", 196, 4);
            lblScanHint.ForeColor = Color.DimGray;
            scanOptions.Controls.Add(lblScanHint);

            // Seri parametre seçimi: işaretli baud × parite × stop kombinasyonlarının tümü denenir (yalnız RS485).
            scanSerial = new Panel();
            scanSerial.SetBounds(6, 210, 472, 66);
            scanSerial.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            scanSerial.Visible = false;
            scanSerial.Controls.Add(MakeLabel("Baud", 2, 3));
            _scanBaudChecks = new CheckBox[ScanBaudRates.Length];
            for (int i = 0; i < ScanBaudRates.Length; i++)
            {
                CheckBox cb = new CheckBox();
                cb.Text = ScanBaudRates[i].ToString();
                cb.SetBounds(38 + i * 54, 0, 54, 20);
                cb.AutoSize = false;
                cb.Font = new Font("Segoe UI", 7.5f);
                cb.Checked = ScanBaudRates[i] == 9600;
                _scanBaudChecks[i] = cb;
                scanSerial.Controls.Add(cb);
            }
            scanSerial.Controls.Add(MakeLabel("Parite", 2, 25));
            _scanParityChecks = new CheckBox[ScanParityNames.Length];
            for (int i = 0; i < ScanParityNames.Length; i++)
            {
                CheckBox cb = new CheckBox();
                cb.Text = ScanParityNames[i];
                cb.SetBounds(44 + i * 52, 22, 52, 20);
                cb.AutoSize = false;
                cb.Checked = i == 0;
                _scanParityChecks[i] = cb;
                scanSerial.Controls.Add(cb);
            }
            scanSerial.Controls.Add(MakeLabel("Stop", 208, 25));
            _scanStopChecks = new CheckBox[ScanStopNames.Length];
            for (int i = 0; i < ScanStopNames.Length; i++)
            {
                CheckBox cb = new CheckBox();
                cb.Text = ScanStopNames[i];
                cb.SetBounds(242 + i * 44, 22, 44, 20);
                cb.AutoSize = false;
                cb.Checked = i == 0;
                _scanStopChecks[i] = cb;
                scanSerial.Controls.Add(cb);
            }
            lblScanSerialInfo = MakeLabel("İşaretli kombinasyonların tümü, seçili ID aralığında baştan sona taranır.", 2, 47);
            lblScanSerialInfo.ForeColor = Color.DimGray;
            scanSerial.Controls.Add(lblScanSerialInfo);

            gbScan.Controls.Add(scanGrid);
            gbScan.Controls.Add(scanOptions);
            gbScan.Controls.Add(scanSerial);
            gbScan.Controls.Add(btnScanAdd);
            gbScan.Controls.Add(btnScanRemove);
            gbScan.Controls.Add(btnScanRun);
            gbScan.Controls.Add(btnScanStop);
            gbScan.Controls.Add(btnScanBack);

            // Tarama sonuçları tablosu: normal register tablosunun yerini alır.
            scanResultGrid = new DataGridView();
            scanResultGrid.SetBounds(8, 254, 484, 218);
            scanResultGrid.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            scanResultGrid.AllowUserToAddRows = false;
            scanResultGrid.AllowUserToDeleteRows = false;
            scanResultGrid.AllowUserToResizeRows = false;
            scanResultGrid.RowHeadersVisible = false;
            scanResultGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            scanResultGrid.MultiSelect = false;
            scanResultGrid.ReadOnly = true;
            scanResultGrid.ScrollBars = ScrollBars.Vertical;
            scanResultGrid.BackgroundColor = Color.White;
            scanResultGrid.BorderStyle = BorderStyle.Fixed3D;
            scanResultGrid.Font = new Font("Consolas", 9f);
            scanResultGrid.Visible = false;
            scanResultGrid.Columns.Add(NewColumn("rDevice", "Cihaz", 56, DataGridViewAutoSizeColumnMode.None, true));
            scanResultGrid.Columns.Add(NewColumn("rAddress", "Adres", 70, DataGridViewAutoSizeColumnMode.None, true));
            scanResultGrid.Columns.Add(NewColumn("rValue", "Değer", 120, DataGridViewAutoSizeColumnMode.None, true));
            DataGridViewTextBoxColumn colRDef = NewColumn("rDef", "Tanım", 220, DataGridViewAutoSizeColumnMode.Fill, false);
            colRDef.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleLeft;
            scanResultGrid.Columns.Add(colRDef);
        }

        private void ShowScanResults(bool show)
        {
            grid.Visible = !show;
            scanResultGrid.Visible = show;
            btnScanBack.Visible = show;
        }

        private void OnScanBack(object sender, EventArgs e)
        {
            ShowScanResults(false);
        }

        /// <summary>EBM-papst fan adresleme paneli: %25 start teyidi + adres kaydı (RTU, D100/D001/D101/D00C).</summary>
        private void BuildEbmUi()
        {
            gbEbm = new GroupBox();
            gbEbm.Text = "EBM Fan Adresleme";
            gbEbm.SetBounds(8, 224, 484, 142);
            gbEbm.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            gbEbm.Visible = false;

            lblEbmAddr = MakeLabel("Adres (1..247)", 10, 24);
            gbEbm.Controls.Add(lblEbmAddr);

            txtEbmFoundAddr = MakeTextBox("1", 96, 20, 44);
            gbEbm.Controls.Add(txtEbmFoundAddr);

            btnEbmStart25 = new Button();
            btnEbmStart25.Text = "%25 Start";
            btnEbmStart25.SetBounds(150, 19, 90, 25);
            btnEbmStart25.Click += OnEbmStart25;
            gbEbm.Controls.Add(btnEbmStart25);

            btnEbmSaveAddr = new Button();
            btnEbmSaveAddr.Text = "Kaydet + Bitir";
            btnEbmSaveAddr.SetBounds(246, 19, 110, 25);
            btnEbmSaveAddr.BackColor = Color.FromArgb(0, 128, 0);
            btnEbmSaveAddr.ForeColor = Color.White;
            btnEbmSaveAddr.Click += OnEbmSaveAddr;
            gbEbm.Controls.Add(btnEbmSaveAddr);

            btnEbmBack = new Button();
            btnEbmBack.Text = "Geri Dön";
            btnEbmBack.SetBounds(362, 19, 110, 25);
            btnEbmBack.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnEbmBack.Click += OnEbmBack;
            gbEbm.Controls.Add(btnEbmBack);

            chkEbmFc16 = new CheckBox();
            chkEbmFc16.Text = "FC16 ile yaz";
            chkEbmFc16.SetBounds(10, 82, 72, 18);
            chkEbmFc16.AutoSize = false;
            gbEbm.Controls.Add(chkEbmFc16);

            lblEbmSifre = MakeLabel("Şifre (D002-D004)", 96, 84);
            gbEbm.Controls.Add(lblEbmSifre);

            txtEbmSifre = MakeTextBox("", 166, 80, 44);
            gbEbm.Controls.Add(txtEbmSifre);

            txtEbmStatus = new TextBox();
            txtEbmStatus.SetBounds(10, 50, 462, 28);
            txtEbmStatus.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            txtEbmStatus.Multiline = true;
            txtEbmStatus.ReadOnly = true;
            txtEbmStatus.ScrollBars = ScrollBars.Vertical;
            txtEbmStatus.BackColor = Color.White;
            txtEbmStatus.Font = new Font("Consolas", 8.25f);
            gbEbm.Controls.Add(txtEbmStatus);
        }

        private void EbmStatus(string msg)
        {
            txtEbmStatus.AppendText(msg + Environment.NewLine);
        }

        private int EbmTimeoutMs()
        {
            int t;
            if (!int.TryParse(txtTimeout.Text.Trim(), out t) || t < 100) t = 2000;
            return t;
        }

        private void OnScanToggle(object sender, EventArgs e)
        {
            gbScan.Visible = !gbScan.Visible;
            if (gbScan.Visible)
            {
                gbEbm.Visible = false;
                UpdateScanSerialInfo();
                UpdateScanPanelSize();
            }
            RelayoutPanels();
        }

        private void UpdateScanPanelSize()
        {
            int rows = Math.Max(1, Math.Min(_scanSegments.Count, 6));
            int gridH = 23 + rows * 22;
            scanGrid.Height = gridH;
            scanOptions.Top = 18 + gridH + 4;
            scanSerial.Top = scanOptions.Top + 24 + 2;
            int extra = scanSerial.Visible ? scanSerial.Height + 4 : 0;
            gbScan.Height = Math.Max(gridH + 26 + 28, 132) + extra;
        }

        /// <summary>Tarama/EBM paneli açıkken güncelleme şeridini ve tabloyu aşağı kaydırır; çakışmayı önler.</summary>
        private void RelayoutPanels()
        {
            int off = 0;
            if (gbEbm != null && gbEbm.Visible) off = gbEbm.Height + 4;
            else if (gbScan != null && gbScan.Visible) off = gbScan.Height + 4;

            lblUpdate.Top = 229 + off;
            btnCheckUpdate.Top = 224 + off;
            btnUpdate.Top = 224 + off;
            grid.Top = 254 + off;
            grid.Height = Math.Max(60, 472 - grid.Top);
            scanResultGrid.Top = 254 + off;
            scanResultGrid.Height = Math.Max(60, 472 - scanResultGrid.Top);

            int minH = 430 + off;
            if (MinimumSize.Height != minH)
            {
                MinimumSize = new Size(MinimumSize.Width, minH);
                if (ClientSize.Height < minH) ClientSize = new Size(ClientSize.Width, minH);
            }
        }

        private void OnEbmEnter(object sender, EventArgs e)
        {
            if (Client == null || !Client.IsConnected)
            {
                Log("EBM adresleme için önce cihaza bağlanın.");
                return;
            }
            if (!SelectedIsRtu())
                Log("Not: EBM fanlar RS485 (RTU) üzerinden adreslenir; TCP modunda devam ediliyor.");

            gbScan.Visible = false;
            gbEbm.Visible = true;
            RelayoutPanels();
            txtEbmStatus.Clear();

            int timeoutMs = EbmTimeoutMs();
            try
            {
                RegisterRead r = Client.ReadTolerant(3, 256, 1, timeoutMs);
                if (r != null && r.Quantity >= 1 && !r.Failed(0))
                {
                    int cur = (r.Data[0] << 8) | r.Data[1];
                    txtEbmFoundAddr.Text = cur.ToString();
                    EbmStatus("Mevcut fan adresi (D100): " + cur);
                }
                else
                {
                    EbmStatus("Mevcut adres okunamadı (D100). Yeni adresi elle girin.");
                }
            }
            catch (Exception ex)
            {
                EbmStatus("Adres okuma hatası: " + ex.Message);
            }
            EbmStatus("Akış: yeni adresi gir → %25 Start ile fanı döndürüp gözle teyit et → Kaydet + Bitir.");
        }

        private void EbmWriteReg(int address, int value, int timeoutMs)
        {
            if (chkEbmFc16 != null && chkEbmFc16.Checked)
            {
                byte[] data = new byte[] { (byte)((value >> 8) & 0xFF), (byte)(value & 0xFF) };
                Client.WriteMultipleRegisters(address, data, timeoutMs);
            }
            else
            {
                Client.WriteSingleRegister(address, value, timeoutMs);
            }
        }

        private void EbmWritePassword(int timeoutMs)
        {
            if (txtEbmSifre == null) return;
            string s = txtEbmSifre.Text.Trim();
            if (s.Length == 0) return;
            int pwd;
            if (!int.TryParse(s, out pwd)) return;
            EbmWriteReg(2, pwd, timeoutMs);   // D002
            EbmWriteReg(3, pwd, timeoutMs);   // D003
            EbmWriteReg(4, pwd, timeoutMs);   // D004
        }

        private void OnEbmStart25(object sender, EventArgs e)
        {
            if (Client == null || !Client.IsConnected) { EbmStatus("Bağlantı yok."); return; }
            int timeoutMs = EbmTimeoutMs();
            try
            {
                EbmWritePassword(timeoutMs);
                EbmWriteReg(257, 1, timeoutMs);      // D101: setpoint kaynağı = RS485 (Modbus kontrol)
                EbmStatus("D101=1 (RS485) yazıldı.");
                EbmWriteReg(15, 1, timeoutMs);       // D00F: Enable / Disable = enable (1), yoksa fan dönmez
                EbmStatus("D00F=1 (Enable) yazıldı.");
                EbmWriteReg(1, 16000, timeoutMs);    // D001: 16000/64000 = %25 (4 LSB yoksayılır)
                EbmStatus("D001=16000 (%25 start) yazıldı. Fan dönmeye başlamalı.");
                EbmStatus("Gözle kontrol edin: dönüyorsa adresi girip Kaydet + Bitir'e basın.");
                try
                {
                    RegisterRead r = Client.ReadTolerant(3, 8, 1, timeoutMs);
                    if (r != null && r.Quantity >= 1 && !r.Failed(0))
                        EbmStatus("Anlık hız (D008): " + ((r.Data[0] << 8) | r.Data[1]) + " rpm");
                }
                catch (Exception) { }
            }
            catch (Exception ex)
            {
                EbmStatus("Start hatası: " + ex.Message);
            }
        }

        private void OnEbmSaveAddr(object sender, EventArgs e)
        {
            if (Client == null || !Client.IsConnected) { EbmStatus("Bağlantı yok."); return; }
            int addr;
            if (!int.TryParse(txtEbmFoundAddr.Text.Trim(), out addr) || addr < 1 || addr > 247)
            {
                EbmStatus("Geçersiz adres: 1..247 arası olmalı.");
                return;
            }
            int timeoutMs = EbmTimeoutMs();
            try
            {
                EbmWritePassword(timeoutMs);
                EbmWriteReg(256, addr, timeoutMs);   // D100: fan adresi
                EbmWriteReg(12, 0, timeoutMs);       // D00C: adresleme kapalı (adres D100'e işlenir)
                EbmWriteReg(257, 1, timeoutMs);      // D101: kaynak RS485
                EbmWriteReg(1, 0, timeoutMs);        // D001: fan durdur

                RegisterRead r = Client.ReadTolerant(3, 256, 1, timeoutMs);
                if (r != null && r.Quantity >= 1 && !r.Failed(0))
                {
                    int back = (r.Data[0] << 8) | r.Data[1];
                    if (back != addr)
                    {
                        EbmStatus("Doğrulama başarısız: D100=" + back + ", beklenen " + addr + ". Kaydedilmedi.");
                        return;
                    }
                }

                StoreDefinition(3, 256, "EBM fan adresi (D100)");
                StoreDefinition(3, 1, "EBM setpoint (D001) 0..64000, %25=16000 (D00F Enable=1 gerekli)");
                StoreDefinition(3, 257, "EBM setpoint kaynağı (D101) 1=RS485");
                StoreDefinition(3, 15, "EBM enable/disable (D00F) 0=durdur, 1=çalıştır");
                StoreDefinition(3, 12, "EBM adresleme aç/kapa (D00C)");
                StoreDefinition(3, 8, "EBM anlık hız (D008) rpm");
                SaveDefinitions();

                EbmStatus("Kaydedildi: fan adresi " + addr + ". Fan durduruldu (D001=0), tanımlar kaydedildi.");
                Log("EBM fan adresleme tamamlandı: adres " + addr);
                gbEbm.Visible = false;
                RelayoutPanels();
            }
            catch (Exception ex)
            {
                EbmStatus("Kaydetme hatası: " + ex.Message);
            }
        }

        private void OnEbmBack(object sender, EventArgs e)
        {
            if (Client != null && Client.IsConnected)
            {
                try { Client.WriteSingleRegister(1, 0, EbmTimeoutMs()); }
                catch (Exception) { }
            }
            gbEbm.Visible = false;
            RelayoutPanels();
            Log("EBM adresleme modundan çıkıldı.");
        }

        private void ScanGrid_CellValueNeeded(object sender, DataGridViewCellValueEventArgs e)
        {
            if (e.RowIndex < 0 || e.RowIndex >= _scanSegments.Count) return;
            ScanSegment s = _scanSegments[e.RowIndex];
            switch (e.ColumnIndex)
            {
                case 0: e.Value = s.Enabled; break;
                case 1: e.Value = s.Name; break;
                case 2: e.Value = FunctionName(s.Function); break;
                case 3: e.Value = s.StartAddress; break;
                case 4: e.Value = s.Quantity; break;
                case 5: e.Value = DecodeName(s.DecodeIndex); break;
                case 6: e.Value = s.WordOrderIndex >= 0 && s.WordOrderIndex < 4 ? (new string[] { "ABCD", "CDAB", "BADC", "DCBA" })[s.WordOrderIndex] : ""; break;
                case 7: e.Value = s.Show4xx; break;
            }
        }

        private void ScanGrid_CellValuePushed(object sender, DataGridViewCellValueEventArgs e)
        {
            if (e.RowIndex < 0 || e.RowIndex >= _scanSegments.Count) return;
            ScanSegment s = _scanSegments[e.RowIndex];
            switch (e.ColumnIndex)
            {
                case 0: s.Enabled = (bool)e.Value; break;
                case 1: s.Name = (e.Value ?? "").ToString(); break;
                case 3:
                    {
                        int sa;
                        string val3 = e.Value != null ? e.Value.ToString() : "";
                        if (int.TryParse(val3, out sa)) s.StartAddress = sa;
                    }
                    break;
                case 4:
                    {
                        int q;
                        string val4 = e.Value != null ? e.Value.ToString() : "";
                        if (int.TryParse(val4, out q)) s.Quantity = q;
                    }
                    break;
                case 5:
                    {
                        int di;
                        string val5 = e.Value != null ? e.Value.ToString() : "";
                        if (int.TryParse(val5, out di) && di >= 0 && di < cmbDecode.Items.Count) s.DecodeIndex = di;
                    }
                    break;
                case 6:
                    {
                        int woi;
                        string val6 = e.Value != null ? e.Value.ToString() : "";
                        if (int.TryParse(val6, out woi) && woi >= 0 && woi < 4) s.WordOrderIndex = woi;
                    }
                    break;
                case 7: s.Show4xx = (bool)e.Value; break;
            }
            SaveScanPlan();
        }

        private void ScanGrid_CurrentCellDirtyStateChanged(object sender, EventArgs e)
        {
            if (scanGrid.IsCurrentCellDirty)
                scanGrid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        }

        private void ScanGrid_CellMouseDoubleClick(object sender, DataGridViewCellMouseEventArgs e)
        {
            if (e.RowIndex < 0 || e.RowIndex >= _scanSegments.Count) return;
            if (e.ColumnIndex == 2) // FC sütununa çift tık -> döndür
            {
                ScanSegment s = _scanSegments[e.RowIndex];
                s.Function = NextFunction(s.Function);
                scanGrid.InvalidateRow(e.RowIndex);
                SaveScanPlan();
            }
        }

        private static byte NextFunction(byte fc)
        {
            // 3->4->1->2->3
            if (fc == 3) return 4;
            if (fc == 4) return 1;
            if (fc == 1) return 2;
            return 3;
        }

        private static string FunctionName(byte fc)
        {
            switch (fc)
            {
                case 1: return "01";
                case 2: return "02";
                case 3: return "03";
                case 4: return "04";
                default: return fc.ToString();
            }
        }

        private static string DecodeName(int idx)
        {
            string[] names = { "uint16", "int16", "Hex", "Float32", "Uint32", "Int32" };
            if (idx >= 0 && idx < names.Length) return names[idx];
            return "";
        }

        private void OnScanAdd(object sender, EventArgs e)
        {
            ScanSegment s = new ScanSegment();
            s.Enabled = true;
            s.Name = "Segment" + (_scanSegments.Count + 1);
            s.Function = SelectedFunction();
            int sa, qt;
            s.StartAddress = int.TryParse(txtAddress.Text.Trim(), out sa) && sa >= 0 ? sa : 0;
            s.Quantity = int.TryParse(txtQuantity.Text.Trim(), out qt) && qt >= 1 ? qt : 10;
            s.DecodeIndex = cmbDecode.SelectedIndex;
            s.WordOrderIndex = cmbWordOrder.SelectedIndex;
            s.Show4xx = chkConvention.Checked;
            _scanSegments.Add(s);
            scanGrid.RowCount = _scanSegments.Count;
            scanGrid.Invalidate();
            SaveScanPlan();
            if (gbScan.Visible) { UpdateScanPanelSize(); RelayoutPanels(); }
        }

        private void OnScanRemove(object sender, EventArgs e)
        {
            int idx = -1;
            if (scanGrid.CurrentCell != null) idx = scanGrid.CurrentCell.RowIndex;
            if (idx < 0 || idx >= _scanSegments.Count) return;
            _scanSegments.RemoveAt(idx);
            scanGrid.RowCount = _scanSegments.Count;
            scanGrid.Invalidate();
            SaveScanPlan();
            if (gbScan.Visible) { UpdateScanPanelSize(); RelayoutPanels(); }
        }

        private void OnScanRun(object sender, EventArgs e)
        {
            if (Client == null || !Client.IsConnected)
            {
                lblSummary.Text = "Önce Bağlan butonuyla bağlantı kurun.";
                return;
            }
            int uStart, uEnd, scanMs;
            if (!int.TryParse(txtScanUnitStart.Text.Trim(), out uStart) || uStart < 1 || uStart > 247)
            {
                lblSummary.Text = "ID başlangıç 1..247 olmalı.";
                return;
            }
            if (!int.TryParse(txtScanUnitEnd.Text.Trim(), out uEnd) || uEnd < uStart || uEnd > 247)
            {
                lblSummary.Text = "ID bitiş, başlangıçtan küçük olamaz (1..247).";
                return;
            }
            if (!int.TryParse(txtScanTimeout.Text.Trim(), out scanMs) || scanMs < 50) scanMs = 150;

            bool rtu = SelectedIsRtu();
            List<SerialCombo> combos = null;
            if (rtu)
            {
                combos = CollectSerialCombos();
                if (combos.Count == 0)
                {
                    lblSummary.Text = "En az bir baud/parite/stop kombinasyonu işaretleyin.";
                    return;
                }
            }

            scanResultGrid.Rows.Clear();
            ShowScanResults(true);
            _scanStopRequested = false;
            btnScanRun.Visible = false;
            btnScanRun.Enabled = false;
            btnScanStop.Visible = true;
            btnScanStop.Enabled = true;
            RunScanAsync(uStart, uEnd, scanMs, rtu, combos);
        }

        private void OnScanStop(object sender, EventArgs e)
        {
            _scanStopRequested = true;
            btnScanStop.Enabled = false;
            lblSummary.Text = "Tarama durduruluyor...";
        }

        private void RunScanAsync(int uStart, int uEnd, int scanMs, bool rtu, List<SerialCombo> combos)
        {
            btnScanRun.Enabled = false;
            btnScanRun.Text = "Taranıyor...";
            btnRead.Enabled = false;

            // UI değerlerini iş parçacığı başlamadan yakala (cross-thread okuma güvenliği).
            string portName = cmbSerialPort.Text.Trim();
            int origBaud;
            if (!int.TryParse(cmbBaud.Text.Trim(), out origBaud) || origBaud < 300) origBaud = 9600;
            Parity origParity = SelectedParity();
            StopBits origStop = SelectedStopBits();
            int connTimeout;
            if (!int.TryParse(txtTimeout.Text.Trim(), out connTimeout) || connTimeout < 100) connTimeout = 2000;
            int comboCount = (combos != null) ? combos.Count : 1;
            string modeText = rtu ? "RS485" : "TCP";

            System.Threading.Thread worker = new System.Threading.Thread(delegate()
            {
                ModbusClient client = rtu ? (ModbusClient)_rtu : _tcp;
                byte origUnit = client.UnitId;
                int found = 0;
                int scanned = 0;
                bool stopped = false;
                bool presenceOnly = true;
                for (int i = 0; i < _scanSegments.Count; i++) if (_scanSegments[i].Enabled) { presenceOnly = false; break; }
                try
                {
                    for (int ci = 0; ci < comboCount; ci++)
                    {
                        if (_scanStopRequested) { stopped = true; break; }
                        string comboLabel = "";
                        if (rtu && combos != null)
                        {
                            SerialCombo combo = combos[ci];
                            comboLabel = combo.Text;
                            try
                            {
                                _rtu.Close();
                                _rtu.Connect(portName, combo.Baud, combo.Parity, 8, combo.Stop, connTimeout);
                            }
                            catch (Exception ex)
                            {
                                Log("Kombinasyon açılamadı (" + comboLabel + "): " + ex.Message);
                                continue;
                            }
                            int ciNow = ci;
                            Ui(delegate
                            {
                                lblSummary.Text = "Tarama: " + comboLabel + " (" + (ciNow + 1) + "/" + comboCount + ")";
                                int row = scanResultGrid.Rows.Add();
                                scanResultGrid.Rows[row].Cells["rValue"].Value = "=== " + comboLabel + " ===";
                                scanResultGrid.Rows[row].DefaultCellStyle.BackColor = Color.FromArgb(220, 230, 241);
                                scanResultGrid.Rows[row].DefaultCellStyle.Font = new Font(scanResultGrid.Font, FontStyle.Bold);
                            });
                            Log("Kombinasyon taranıyor: " + comboLabel);
                        }

                        for (int unit = uStart; unit <= uEnd; unit++)
                        {
                            if (_scanStopRequested) { stopped = true; break; }
                            if (!client.IsConnected) break;
                            client.UnitId = (byte)unit;
                            bool deviceSeen = false;
                            string liveBase = modeText + (comboLabel.Length > 0 ? " " + comboLabel : "") + " — ID " + unit + "/" + uEnd;
                            if (presenceOnly) SetScanLive("Taranıyor: " + liveBase + " (var/yok: FC03/04/01/02 @0)");

                            if (presenceOnly && ProbeAny(client, scanMs))
                            {
                                deviceSeen = true;
                                found++;
                                AddScanDeviceHeader(unit, found, comboLabel);
                            }

                            for (int i = 0; !presenceOnly && i < _scanSegments.Count; i++)
                            {
                                ScanSegment s = _scanSegments[i];
                                if (!s.Enabled) continue;

                                byte fc = s.Function;
                                int start = s.StartAddress;
                                int qty = s.Quantity;
                                int decodeIdx = s.DecodeIndex;
                                int wordIdx = s.WordOrderIndex;
                                bool show4xx = s.Show4xx;

                                SetScanLive("Taranıyor: " + liveBase + " FC" + FunctionName(fc) + " adres " + start + ".." + (start + qty - 1));
                                if (!client.Probe(fc, start, scanMs)) continue;

                                if (!deviceSeen)
                                {
                                    deviceSeen = true;
                                    found++;
                                    AddScanDeviceHeader(unit, found, comboLabel);
                                }

                                int readMs = Math.Max(scanMs, 1000);
                                RegisterRead read = client.ReadTolerant(fc, start, qty, readMs);
                                if (read == null) continue;

                                Ui(delegate
                                {
                                    int row = scanResultGrid.Rows.Add();
                                    scanResultGrid.Rows[row].Cells["rValue"].Value = "--- " + s.Name + " (FC" + FunctionName(fc) + " " + start + ".." + (start + qty - 1) + ") ---";
                                    scanResultGrid.Rows[row].DefaultCellStyle.ForeColor = Color.DimGray;
                                });

                                for (int r = 0; r < qty; r++)
                                {
                                    int addr = start + r;
                                    string errorText = null;
                                    if (read.ItemErrors != null && r < read.ItemErrors.Length)
                                        errorText = read.ItemErrors[r];
                                    string valText = errorText != null
                                        ? errorText
                                        : ValueOf(read, r, fc == 1 || fc == 2, decodeIdx, (WordOrder)wordIdx);
                                    string addrText = show4xx ? ModbusTcpClient.DisplayAddress(fc, addr, true).ToString() : addr.ToString();
                                    string def = DefinitionOf(fc, addr);

                                    Ui(delegate
                                    {
                                        int row = scanResultGrid.Rows.Add();
                                        scanResultGrid.Rows[row].Cells["rAddress"].Value = addrText;
                                        scanResultGrid.Rows[row].Cells["rValue"].Value = valText;
                                        if (def.Length > 0) scanResultGrid.Rows[row].Cells["rDef"].Value = def;
                                        if (errorText != null)
                                        {
                                            scanResultGrid.Rows[row].Cells["rValue"].Style.ForeColor = Color.Red;
                                            scanResultGrid.Rows[row].Cells["rValue"].ToolTipText = errorText;
                                        }
                                    });
                                }
                            }

                            scanned++;
                            int scannedNow = scanned;
                            int foundNow2 = found;
                            int unitNow2 = unit;
                            string cl2 = comboLabel;
                            Ui(delegate
                            {
                                string prefix = cl2.Length > 0 ? cl2 + " — " : "";
                                lblSummary.Text = "Tarama: " + prefix + "ID " + unitNow2 + "/" + uEnd + " — " + foundNow2 + " cihaz bulundu";
                            });
                        }
                    }
                }
                finally
                {
                    client.UnitId = origUnit;
                    if (rtu && combos != null && combos.Count > 0)
                    {
                        try { _rtu.Close(); _rtu.Connect(portName, origBaud, origParity, 8, origStop, connTimeout); }
                        catch (Exception) { }
                    }
                }

                Ui(delegate
                {
                    btnScanRun.Enabled = true;
                    btnScanRun.Text = "TARA";
                    btnScanRun.Visible = true;
                    btnScanStop.Visible = false;
                    btnRead.Enabled = client.IsConnected;
                    string comboNote = comboCount > 1 ? ", " + comboCount + " kombinasyon" : "";
                    if (stopped)
                    {
                        lblSummary.Text = "Tarama durduruldu: " + found + " cihaz bulundu (" + scanned + " ID tarandı" + comboNote + ")";
                        lblScanLive.Text = "Tarama durduruldu: " + found + " cihaz / " + scanned + " ID" + comboNote;
                        Log("Tarama durduruldu: " + found + " cihaz / " + scanned + " ID" + comboNote);
                    }
                    else
                    {
                        lblSummary.Text = "Tarama tamamlandı: " + found + " cihaz bulundu (" + scanned + " ID tarandı" + comboNote + ")";
                        lblScanLive.Text = "Tarama bitti: " + found + " cihaz / " + scanned + " ID" + comboNote;
                        Log("Tarama bitti: " + found + " cihaz / " + scanned + " ID" + comboNote);
                    }
                });
            });
            worker.IsBackground = true;
            worker.Start();
        }

        /// <summary>Segment yokken "herhangi bir veri geliyor mu" kontrolü: FC03/04/01/02 @0 probe, ilk yanıtta durur.</summary>
        private static bool ProbeAny(ModbusClient client, int scanMs)
        {
            byte[] fcs = { 3, 4, 1, 2 };
            for (int i = 0; i < fcs.Length; i++)
                if (client.Probe(fcs[i], 0, scanMs)) return true;
            return false;
        }

        private void AddScanDeviceHeader(int unit, int foundNow, string comboLabel)
        {
            Ui(delegate
            {
                int row = scanResultGrid.Rows.Add();
                scanResultGrid.Rows[row].Cells["rDevice"].Value = "ID " + unit;
                string hdr = "--- cihaz bulundu (" + foundNow + ".) ---";
                if (comboLabel.Length > 0) hdr = "--- cihaz bulundu (" + foundNow + ".) [" + comboLabel + "] ---";
                scanResultGrid.Rows[row].Cells["rValue"].Value = hdr;
                scanResultGrid.Rows[row].DefaultCellStyle.BackColor = Color.LightGray;
                scanResultGrid.Rows[row].DefaultCellStyle.Font = new Font(scanResultGrid.Font, FontStyle.Bold);
            });
            Log("Cihaz bulundu: ID " + unit + (comboLabel.Length > 0 ? " @ " + comboLabel : ""));
        }

        private void SetScanLive(string text)
        {
            Ui(delegate { lblScanLive.Text = text; });
        }

        private const string ShortcutName = "Modbus Scan.lnk";

        private static string DesktopDirectory()
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        }

        private static string StartMenuDirectory()
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        }

        private void OnCreateShortcuts(object sender, EventArgs e)
        {
            string exe = Application.ExecutablePath;
            try
            {
                string[] written = CreateShortcutsIn(DesktopDirectory(), StartMenuDirectory(), exe);
                if (written.Length == 0)
                {
                    lblSummary.Text = "Kısayol yazılacak klasör bulunamadı.";
                    return;
                }
                for (int i = 0; i < written.Length; i++) Log("Kısayol oluşturuldu: " + written[i]);
                if (IsTemporaryLocation(exe))
                    Log("UYARI: program geçici klasörde; silinirse kısayollar bozulur. Önce kalıcı bir klasöre taşı.");
                lblSummary.Text = written.Length + " kısayol oluşturuldu (masaüstü + Başlat Menüsü).";
            }
            catch (Exception ex)
            {
                lblSummary.Text = "Kısayol oluşturulamadı: " + ShortMessage(ex);
                Log("Kısayol hatası: " + ex.Message);
            }
        }

        private void OnRemoveShortcuts(object sender, EventArgs e)
        {
            int removed = RemoveShortcutsIn(DesktopDirectory(), StartMenuDirectory());
            lblSummary.Text = removed > 0 ? removed + " kısayol kaldırıldı." : "Kısayol yok, kaldırılacak bir şey bulunamadı.";
            Log(removed > 0 ? "Kısayol kaldırıldı: " + removed + " dosya." : "Kısayol kaldırma: dosya bulunamadı.");
        }

        /// <summary>Test edilebilir çekirdek: kısayolları verilen klasörlere yazar, yazılan yolları döner.</summary>
        private static string[] CreateShortcutsIn(string desktopDir, string startMenuDir, string exePath)
        {
            List<string> written = new List<string>();
            string[] dirs = new string[] { desktopDir, startMenuDir };
            for (int i = 0; i < dirs.Length; i++)
            {
                string dir = dirs[i];
                if (dir.Length == 0) continue;
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                string lnk = Path.Combine(dir, ShortcutName);
                WriteShortcut(lnk, exePath, "Modbus TCP/RS485 test aracı — kayıt okuma, yazma ve tarama");
                written.Add(lnk);
            }
            return written.ToArray();
        }

        /// <summary>Test edilebilir çekirdek: iki klasördeki kısayolu siler, silinen sayısını döner.</summary>
        private static int RemoveShortcutsIn(string desktopDir, string startMenuDir)
        {
            int removed = 0;
            string[] dirs = new string[] { desktopDir, startMenuDir };
            for (int i = 0; i < dirs.Length; i++)
            {
                string dir = dirs[i];
                if (dir.Length == 0) continue;
                string lnk = Path.Combine(dir, ShortcutName);
                try
                {
                    if (File.Exists(lnk))
                    {
                        File.Delete(lnk);
                        removed++;
                    }
                }
                catch (Exception)
                {
                    // Silinemese sorun değil: kullanıcı klasörden elle kaldırabilir.
                }
            }
            return removed;
        }

        /// <summary>WScript.Shell ile .lnk üretir: hedef, çalışma klasörü ve exe'nin gömülü simgesi (0).</summary>
        private static void WriteShortcut(string lnkPath, string exePath, string description)
        {
            Type shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) throw new InvalidOperationException("WScript.Shell bulunamadı");
            object shell = Activator.CreateInstance(shellType);
            try
            {
                object link = shellType.InvokeMember("CreateShortcut",
                    BindingFlags.InvokeMethod | BindingFlags.Instance | BindingFlags.Public, null, shell,
                    new object[] { lnkPath });
                Type linkType = link.GetType();
                SetComProperty(linkType, link, "TargetPath", exePath);
                SetComProperty(linkType, link, "WorkingDirectory", Path.GetDirectoryName(exePath));
                SetComProperty(linkType, link, "IconLocation", exePath + ",0");
                SetComProperty(linkType, link, "WindowStyle", 1);
                SetComProperty(linkType, link, "Description", description);
                linkType.InvokeMember("Save",
                    BindingFlags.InvokeMethod | BindingFlags.Instance | BindingFlags.Public, null, link, null);
            }
            finally
            {
                IDisposable com = shell as IDisposable;
                if (com != null) com.Dispose();
            }
        }

        private static void SetComProperty(Type type, object target, string name, object value)
        {
            type.InvokeMember(name, BindingFlags.SetProperty | BindingFlags.Instance | BindingFlags.Public,
                null, target, new object[] { value });
        }

        private static bool IsTemporaryLocation(string path)
        {
            string temp = Path.GetTempPath().TrimEnd('\\', '/');
            return path.StartsWith(temp + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        private static Label MakeLabel(string text, int x, int y)
        {
            Label lbl = new Label();
            lbl.Text = text;
            lbl.AutoSize = true;
            lbl.Location = new Point(x, y);
            return lbl;
        }

        private static TextBox MakeTextBox(string text, int x, int y, int width)
        {
            TextBox tb = new TextBox();
            tb.Text = text;
            tb.SetBounds(x, y, width, 21);
            return tb;
        }

        private static void AnchorRight(params Control[] controls)
        {
            for (int i = 0; i < controls.Length; i++)
                controls[i].Anchor = AnchorStyles.Top | AnchorStyles.Right;
        }

        private static void AnchorLeft(params Control[] controls)
        {
            for (int i = 0; i < controls.Length; i++)
                controls[i].Anchor = AnchorStyles.Top | AnchorStyles.Left;
        }

        private static DataGridViewTextBoxColumn NewColumn(string name, string header, int width,
                                                           DataGridViewAutoSizeColumnMode mode, bool readOnly)
        {
            DataGridViewTextBoxColumn col = new DataGridViewTextBoxColumn();
            col.Name = name;
            col.HeaderText = header;
            col.Width = width;
            col.AutoSizeMode = mode;
            col.SortMode = DataGridViewColumnSortMode.NotSortable;
            col.ReadOnly = readOnly;
            col.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
            return col;
        }

        private byte SelectedFunction()
        {
            switch (cmbFunction.SelectedIndex)
            {
                case 1: return 4;
                case 2: return 1;
                case 3: return 2;
                default: return 3;
            }
        }

        private bool SelectedIsBits()
        {
            byte fc = SelectedFunction();
            return fc == 1 || fc == 2;
        }

        private void OnFunctionChanged(object sender, EventArgs e)
        {
            UpdateDecodeControls();
            OnRedisplay(sender, e);
        }

        private void UpdateDecodeControls()
        {
            bool bits = SelectedIsBits();
            cmbDecode.Enabled = !bits;
            cmbWordOrder.Enabled = !bits && IsWideType();
        }

        private bool IsWideType()
        {
            return cmbDecode.SelectedIndex >= 3;
        }

        /// <summary>Seçime göre bağlantı alanlarını değiştirir: TCP IP/Port, RS485 COM/hız/parite/durdurma.</summary>
        private void ApplyMode()
        {
            bool rtu = SelectedIsRtu();

            lblIp.Visible = !rtu;
            txtIp.Visible = !rtu;
            lblTcpPort.Visible = !rtu;
            txtPort.Visible = !rtu;

            lblCom.Visible = rtu;
            cmbSerialPort.Visible = rtu;
            lblBaud.Visible = rtu;
            cmbBaud.Visible = rtu;
            lblParity.Visible = rtu;
            cmbParity.Visible = rtu;
            lblStop.Visible = rtu;
            cmbStop.Visible = rtu;

            if (rtu)
            {
                // Seri alanlar tek satıra sığsın diye Bağlan/Kes başlık satırına çıkar.
                lblUnit.Location = new Point(336, 28);
                txtUnit.SetBounds(352, 25, 30, 21);
                lblMs.Location = new Point(388, 28);
                txtTimeout.SetBounds(408, 25, 40, 21);
                btnConnect.SetBounds(314, 1, 74, 21);
                btnDisconnect.SetBounds(392, 1, 78, 21);
                // ID/ms satırın sağında; pencere büyüdüğünde sağ kenarla birlikte kayar.
                AnchorRight(lblUnit, txtUnit, lblMs, txtTimeout);
                RefreshPorts();
            }
            else
            {
                lblUnit.Location = new Point(202, 28);
                txtUnit.SetBounds(216, 25, 30, 21);
                lblMs.Location = new Point(252, 28);
                txtTimeout.SetBounds(268, 25, 40, 21);
                btnConnect.SetBounds(314, 23, 74, 25);
                btnDisconnect.SetBounds(392, 23, 80, 25);
                AnchorLeft(lblUnit, txtUnit, lblMs, txtTimeout);
            }

            if (scanSerial != null)
            {
                scanSerial.Visible = rtu;
                UpdateScanSerialInfo();
                if (gbScan != null && gbScan.Visible) { UpdateScanPanelSize(); RelayoutPanels(); }
            }
        }

        private void UpdateScanSerialInfo()
        {
            if (lblScanSerialInfo == null) return;
            lblScanSerialInfo.Text = "Şu an: " + cmbBaud.Text + " " + cmbParity.Text + " " + cmbStop.Text
                + " — işaretli tüm kombinasyonlar, seçili ID aralığında taranır.";
        }

        private List<SerialCombo> CollectSerialCombos()
        {
            List<SerialCombo> list = new List<SerialCombo>();
            for (int b = 0; b < _scanBaudChecks.Length; b++)
            {
                if (!_scanBaudChecks[b].Checked) continue;
                for (int p = 0; p < _scanParityChecks.Length; p++)
                {
                    if (!_scanParityChecks[p].Checked) continue;
                    for (int s = 0; s < _scanStopChecks.Length; s++)
                    {
                        if (!_scanStopChecks[s].Checked) continue;
                        SerialCombo c = new SerialCombo();
                        c.Baud = ScanBaudRates[b];
                        c.Parity = p == 1 ? Parity.Odd : (p == 2 ? Parity.Even : Parity.None);
                        c.Stop = s == 1 ? StopBits.OnePointFive : (s == 2 ? StopBits.Two : StopBits.One);
                        c.Text = c.Baud + " " + ScanParityNames[p] + " " + ScanStopNames[s];
                        list.Add(c);
                    }
                }
            }
            return list;
        }

        private void OnModeChanged(object sender, EventArgs e)
        {
            ApplyMode();
            UpdateConnectionState(false);
        }

        private Parity SelectedParity()
        {
            switch (cmbParity.SelectedIndex)
            {
                case 1: return Parity.Odd;
                case 2: return Parity.Even;
                default: return Parity.None;
            }
        }

        private StopBits SelectedStopBits()
        {
            switch (cmbStop.SelectedIndex)
            {
                case 1: return StopBits.OnePointFive;
                case 2: return StopBits.Two;
                default: return StopBits.One;
            }
        }

        /// <summary>Sistemdeki COM portlarını sayı sırasıyla listeye yeniler (COM2, COM10 gibi).</summary>
        private void RefreshPorts()
        {
            string[] names = SerialPort.GetPortNames();
            Array.Sort(names, delegate(string a, string b)
            {
                int na = PortNumber(a), nb = PortNumber(b);
                if (na != nb) return na.CompareTo(nb);
                return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
            });

            string previous = cmbSerialPort.Text;
            FillPortLabels(names);
            cmbSerialPort.BeginUpdate();
            cmbSerialPort.Items.Clear();
            for (int i = 0; i < names.Length; i++) cmbSerialPort.Items.Add(names[i]);
            cmbSerialPort.EndUpdate();

            if (names.Length == 0)
            {
                cmbSerialPort.SelectedIndex = -1;
                return;
            }
            int keep = previous.Length > 0 ? Array.IndexOf(names, previous) : -1;
            cmbSerialPort.SelectedIndex = keep >= 0 ? keep : 0;
        }

        /// <summary>Port listesi değiştiyse açıklamaları (sürücü adları) yeniden sorgular.</summary>
        private void FillPortLabels(string[] names)
        {
            StringBuilder key = new StringBuilder();
            for (int i = 0; i < names.Length; i++) key.Append(names[i]).Append(';');
            if (key.ToString() == _portLabelKey) return;
            _portLabelKey = key.ToString();
            _portLabels.Clear();
            if (names.Length == 0) return;

            Dictionary<string, string> found = SerialPortInfo.Describe();
            for (int i = 0; i < names.Length; i++)
            {
                string label;
                if (found.TryGetValue(names[i], out label)) _portLabels[names[i]] = label;
            }

            int width = cmbSerialPort.Width;
            for (int i = 0; i < names.Length; i++)
            {
                string label;
                if (!_portLabels.TryGetValue(names[i], out label)) label = "";
                Size measured = TextRenderer.MeasureText(names[i] + "   " + label, cmbSerialPort.Font,
                    Size.Empty, TextFormatFlags.NoPrefix);
                if (measured.Width + 24 > width) width = measured.Width + 24;
            }
            cmbSerialPort.DropDownWidth = width > 320 ? 320 : width;
        }

        private void OnPortDrawItem(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0) return;
            string name = Convert.ToString(cmbSerialPort.Items[e.Index]);
            string label;
            if (!_portLabels.TryGetValue(name, out label)) label = "";
            bool selected = (e.State & DrawItemState.Selected) != 0;
            bool editBox = (e.State & DrawItemState.ComboBoxEdit) != 0;

            e.DrawBackground();
            Color textColor = selected ? SystemColors.HighlightText : SystemColors.WindowText;
            TextRenderer.DrawText(e.Graphics, name, cmbSerialPort.Font, e.Bounds, textColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

            // Kutu kapalıyken yer dar; açıklama yalnızca açılır listede yazılır.
            if (!editBox && label.Length > 0)
            {
                int nameWidth = TextRenderer.MeasureText(name, cmbSerialPort.Font, Size.Empty,
                    TextFormatFlags.NoPrefix).Width;
                Rectangle tail = new Rectangle(e.Bounds.X + nameWidth + 8, e.Bounds.Y,
                    e.Bounds.Width - nameWidth - 10, e.Bounds.Height);
                Color labelColor = selected ? Color.FromArgb(215, 228, 255) : Color.Gray;
                TextRenderer.DrawText(e.Graphics, label, cmbSerialPort.Font, tail, labelColor,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            }
            e.DrawFocusRectangle();
        }

        private static int PortNumber(string name)
        {
            if (name == null) return 0;
            StringBuilder digits = new StringBuilder();
            for (int i = 0; i < name.Length; i++)
            {
                if (name[i] >= '0' && name[i] <= '9') digits.Append(name[i]);
            }
            int value;
            return int.TryParse(digits.ToString(), out value) ? value : 0;
        }

        private void OnConnect(object sender, EventArgs e)
        {
            int timeoutMs;
            if (!int.TryParse(txtTimeout.Text.Trim(), out timeoutMs) || timeoutMs < 100)
            {
                Log("Geçersiz zaman aşımı (en az 100 ms).");
                return;
            }
            byte unit;
            if (!byte.TryParse(txtUnit.Text.Trim(), out unit))
            {
                Log("Geçersiz ünite id (0..255).");
                return;
            }

            bool rtu = SelectedIsRtu();
            btnConnect.Enabled = false;
            try
            {
                DateTime t0 = DateTime.Now;
                if (rtu)
                {
                    string portName = cmbSerialPort.Text.Trim();
                    if (portName.Length == 0)
                    {
                        RefreshPorts();
                        Log(cmbSerialPort.Items.Count == 0
                            ? "COM port bulunamadı — RS485 dönüştürücünü tak, sonra listeyi yenile."
                            : "COM port seçilmedi.");
                        UpdateConnectionState(false);
                        return;
                    }

                    int baud;
                    if (!int.TryParse(cmbBaud.Text.Trim(), out baud) || baud < 300)
                    {
                        Log("Geçersiz baud hızı.");
                        UpdateConnectionState(false);
                        return;
                    }

                    _rtu.UnitId = unit;
                    _rtu.Connect(portName, baud, SelectedParity(), 8, SelectedStopBits(), timeoutMs);
                }
                else
                {
                    string ip = txtIp.Text.Trim();
                    int port;
                    if (!int.TryParse(txtPort.Text.Trim(), out port) || port < 1 || port > 65535)
                    {
                        Log("Geçersiz port.");
                        UpdateConnectionState(false);
                        return;
                    }
                    _tcp.UnitId = unit;
                    _tcp.Connect(ip, port, timeoutMs);
                }

                int ms = (int)(DateTime.Now - t0).TotalMilliseconds;
                Log("Bağlandı (" + (rtu ? "RS485" : "TCP") + "): " + Client.Description + " (unit " + unit + ") " + ms + " ms");
                UpdateConnectionState(true);
                SaveSettings();
            }
            catch (Exception ex)
            {
                Log("Bağlantı hatası: " + ex.Message);
                UpdateConnectionState(false);
            }
        }

        private void OnDisconnect(object sender, EventArgs e)
        {
            _tcp.Close();
            _rtu.Close();
            Log("Bağlantı kesildi.");
            UpdateConnectionState(false);
        }

        private void UpdateConnectionState(bool connected)
        {
            btnConnect.Enabled = !connected;
            btnDisconnect.Enabled = connected;
            btnRead.Enabled = connected;
            cmbMode.Enabled = !connected;
            txtIp.Enabled = !connected;
            txtPort.Enabled = !connected;
            txtUnit.Enabled = !connected;
            cmbSerialPort.Enabled = !connected;
            cmbBaud.Enabled = !connected;
            cmbParity.Enabled = !connected;
            cmbStop.Enabled = !connected;
            lblStatus.Text = connected
                ? "Durum: bağlı → " + Client.Description
                : "Durum: bağlı değil";
            lblStatus.ForeColor = connected ? Color.FromArgb(0, 128, 0) : Color.Gray;
        }

        private void OnRead(object sender, EventArgs e)
        {
            int address, quantity, timeoutMs;
            if (!int.TryParse(txtAddress.Text.Trim(), out address) || address < 0 || address > 65535)
            {
                lblSummary.Text = "Geçersiz adres.";
                return;
            }
            if (!int.TryParse(txtQuantity.Text.Trim(), out quantity) || quantity < 1)
            {
                lblSummary.Text = "Adet en az 1 olmalı.";
                return;
            }
            if (address + quantity > 65536)
            {
                lblSummary.Text = "Adres alanı 0..65535: @" + address + " için en fazla " + (65536 - address) + " kayıt.";
                return;
            }
            if (!int.TryParse(txtTimeout.Text.Trim(), out timeoutMs)) timeoutMs = 2000;

            byte fc = SelectedFunction();
            DateTime start = DateTime.Now;
            RegisterRead read;
            Client.Progress = ReportReadProgress;
            try
            {
                // Hata toleransli: yasadisi olmayan register tabloya isaretlenir, digerleri okunmaya devam eder.
                read = Client.ReadTolerant(fc, address, quantity, timeoutMs);
            }
            catch (Exception ex)
            {
                Client.Progress = null;
                read = null;
                lblSummary.Text = "Hata: " + ShortMessage(ex);
                lblSummary.ForeColor = Color.Firebrick;
                Log("OKUMA FC" + fc.ToString("D2") + " @" + address + " x" + quantity + " → HATA: " + ex.Message);
                // RS485'te yanıt alamamak hattın kapandığı anlamına gelmez; port açık kalır.
                bool linkLost = !Client.IsConnected || ex is SocketException ||
                                (!SelectedIsRtu() && ex is TimeoutException);
                if (linkLost) UpdateConnectionState(false);
                return;
            }

            Client.Progress = null;
            read.ElapsedMilliseconds = (int)(DateTime.Now - start).TotalMilliseconds;
            _lastRead = read;
            lblSummary.ForeColor = Color.DimGray;
            ShowRead(read);
            Log("OKUMA FC" + fc.ToString("D2") + " @" + address + " x" + quantity +
                " → " + read.Data.Length + " bayt, " + read.Requests + " istek, " + read.ElapsedMilliseconds + " ms" +
                (read.ErrorCount > 0 ? ", " + read.ErrorCount + " kayıt hatalı" : ""));
            if (read.StopReason != null) Log("DURDU: " + read.StopReason);
        }

        /// <summary>Parçalı okuma ilerlemesini durum satırında gösterir; büyük okumalarda pencere donmuş görünmesin.</summary>
        private void ReportReadProgress(int done, int total)
        {
            lblSummary.ForeColor = Color.DimGray;
            lblSummary.Text = "Okunuyor: " + done + " / " + total + " kayıt";
            lblSummary.Refresh();
        }

        /// <summary>Değer sütununa çift tık: yazma penceresi. Tanım sütununa çift tık: hücreyi düzenle.</summary>
        private void OnGridCellDoubleClick(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0) return;
            if (grid.Columns[e.ColumnIndex].Name == "colDef")
            {
                grid.CurrentCell = grid.Rows[e.RowIndex].Cells[e.ColumnIndex];
                grid.BeginEdit(true);
                return;
            }
            OpenWriteDialog(e.RowIndex);
        }

        private void OpenWriteDialog(int rowIndex)
        {
            if (rowIndex < 0 || _lastRead == null) return;

            if (!Client.IsConnected)
            {
                lblSummary.ForeColor = Color.Firebrick;
                lblSummary.Text = "Yazmak için önce Bağlan gerekiyor.";
                return;
            }

            RegisterRead read = _lastRead;
            int address = read.StartAddress + rowIndex;
            // Hatalı satırda okunmuş değer yok; yazma penceresi boş mevcut değerle açılır.
            byte[] current = read.Failed(rowIndex) ? new byte[0] : CurrentBytesOfRow(read, rowIndex);

            int timeoutMs;
            if (!int.TryParse(txtTimeout.Text.Trim(), out timeoutMs) || timeoutMs < 100) timeoutMs = 2000;

            using (WriteDialog dialog = new WriteDialog(Client, read.FunctionCode, address,
                                                        current, chkConvention.Checked, timeoutMs))
            {
                dialog.ShowDialog(this);
                if (dialog.WritePerformed)
                {
                    Log("YAZMA FC" + dialog.WrittenFunctionCode.ToString("D2") +
                        " @" + address + " başarılı, tablo tazeleniyor.");
                    OnRead(this, EventArgs.Empty);
                }
            }
        }

        private static byte[] CurrentBytesOfRow(RegisterRead read, int rowIndex)
        {
            if (read.BytesPerItem == 1)
            {
                int byteIndex = rowIndex / 8;
                if (byteIndex >= read.Data.Length) return new byte[0];
                int bit = (read.Data[byteIndex] >> (rowIndex % 8)) & 1;
                return new byte[] { (byte)bit };
            }
            int offset = rowIndex * 2;
            if (offset + 1 >= read.Data.Length) return new byte[0];
            return new byte[] { read.Data[offset], read.Data[offset + 1] };
        }

        private static string ShortMessage(Exception ex)
        {
            string m = ex.Message.Replace("\r", " ").Replace("\n", " ");
            return m.Length > 46 ? m.Substring(0, 46) + "…" : m;
        }

        private void ShowRead(RegisterRead read)
        {
            ShowScanResults(false);
            grid.SuspendLayout();
            grid.Rows.Clear();

            bool bits = read.FunctionCode == 1 || read.FunctionCode == 2;
            int decodeType = cmbDecode.SelectedIndex;
            WordOrder order = (WordOrder)cmbWordOrder.SelectedIndex;

            for (int i = 0; i < read.Quantity; i++)
            {
                int pduAddress = read.StartAddress + i;
                bool failed = read.Failed(i);
                string value = failed ? ErrorCellText(read.ItemErrors[i])
                                      : ValueOf(read, i, bits, decodeType, order);
                int rowIndex = grid.Rows.Add(new object[] {
                    i.ToString(),
                    ModbusTcpClient.DisplayAddress(read.FunctionCode, pduAddress, chkConvention.Checked).ToString(),
                    value,
                    DefinitionOf(read.FunctionCode, pduAddress)
                });
                if (failed)
                {
                    DataGridViewCell cell = grid.Rows[rowIndex].Cells["colValue"];
                    cell.Style.ForeColor = Color.Firebrick;
                    cell.ToolTipText = read.ItemErrors[i];
                }
            }

            grid.ResumeLayout();
            int errors = read.ErrorCount;
            lblSummary.Text = read.Quantity + " kayıt, " + read.Data.Length + " bayt, " +
                              read.Requests + " istek, " + read.ElapsedMilliseconds + " ms" +
                              (errors > 0 ? " · " + errors + " kayıt hatalı" : "") +
                              " · çift tık: yaz · Tanım'a Ctrl+V, Delete: temizle";
            if (read.StopReason != null)
            {
                lblSummary.ForeColor = Color.Firebrick;
                lblSummary.Text = read.StopReason;
            }
        }

        /// <summary>Hata metnini Değer sütununa sığacak kısa biçime indirger; tamamı hücre ipucunda kalır.</summary>
        private static string ErrorCellText(string error)
        {
            if (string.IsNullOrEmpty(error)) return "hata";
            int sep = error.IndexOf(" · ");
            return sep > 0 ? error.Substring(0, sep) : error;
        }

        /// <summary>Satırın Değer sütununda görünecek metni, seçili çözümlemeye göre üretir.</summary>
        private static string ValueOf(RegisterRead read, int index, bool bits, int decodeType, WordOrder order)
        {
            int offset = index * (bits ? 1 : 2);

            if (bits)
            {
                if (offset / 8 >= read.Data.Length) return "";
                return (((read.Data[offset / 8] >> (offset % 8)) & 1)).ToString();
            }
            if (offset + 1 >= read.Data.Length) return "";

            if (decodeType == 0) return ModbusTcpClient.UInt16(read.Data, offset).ToString();
            if (decodeType == 1) return ModbusTcpClient.Int16(read.Data, offset).ToString();
            if (decodeType == 2)
            {
                return "0x" + read.Data[offset].ToString("X2") + read.Data[offset + 1].ToString("X2");
            }

            bool wide = offset % 4 == 0 && offset + 3 < read.Data.Length;
            if (!wide) return "";
            switch (decodeType)
            {
                case 3: return ModbusTcpClient.FormatFloat(ModbusTcpClient.Float32(read.Data, offset, order));
                case 4: return ModbusTcpClient.UInt32(read.Data, offset, order).ToString();
                default: return ModbusTcpClient.Int32(read.Data, offset, order).ToString();
            }
        }

        private static string DefinitionKey(byte functionCode, int pduAddress)
        {
            return functionCode.ToString() + ":" + pduAddress.ToString();
        }

        private string DefinitionOf(byte functionCode, int pduAddress)
        {
            string text;
            return _definitions.TryGetValue(DefinitionKey(functionCode, pduAddress), out text) ? text : "";
        }

        private void StoreDefinition(byte functionCode, int pduAddress, string text)
        {
            string key = DefinitionKey(functionCode, pduAddress);
            text = CleanDefinitionText(text);
            if (text.Length == 0) _definitions.Remove(key);
            else _definitions[key] = text;
        }

        private void ApplyDefinition(int rowIndex, string text)
        {
            RegisterRead read = _lastRead;
            if (read == null || rowIndex < 0 || rowIndex >= grid.Rows.Count) return;
            int pduAddress = read.StartAddress + rowIndex;
            StoreDefinition(read.FunctionCode, pduAddress, text);
            grid.Rows[rowIndex].Cells["colDef"].Value = DefinitionOf(read.FunctionCode, pduAddress);
        }

        private void OnDefinitionCellEndEdit(object sender, DataGridViewCellEventArgs e)
        {
            RegisterRead read = _lastRead;
            if (read == null || e.RowIndex < 0 || e.RowIndex >= read.Quantity) return;
            ApplyDefinition(e.RowIndex, Convert.ToString(grid.Rows[e.RowIndex].Cells[e.ColumnIndex].Value));
            SaveDefinitions();
        }

        /// <summary>Ctrl+V: pano metnini okur ve Tanım sütununa dağıtır.</summary>
        private void PasteDefinitions()
        {
            string clipboard;
            try { clipboard = Clipboard.GetText(); }
            catch (Exception ex) { Log("Pano okunamadı: " + ex.Message); return; }
            ApplyPastedDefinitions(clipboard);
        }

        /// <summary>Excel'de kopyalanan sütunu satır satır, seçili satırdan itibaren Tanım'a yazar.</summary>
        private void ApplyPastedDefinitions(string text)
        {
            RegisterRead read = _lastRead;
            if (read == null || grid.Rows.Count == 0)
            {
                lblSummary.Text = "Tanım yapıştırmak için önce OKU gerekiyor.";
                return;
            }
            if (string.IsNullOrEmpty(text)) { Log("Panoda metin yok."); return; }

            string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            int count = lines.Length;
            while (count > 0 && lines[count - 1].Trim().Length == 0) count--;  // Excel sonda boş satır bırakır
            if (count == 0) { Log("Panoda satır yok."); return; }

            int startRow = grid.CurrentCell != null ? grid.CurrentCell.RowIndex : 0;
            int applied = Math.Min(count, grid.Rows.Count - startRow);
            for (int i = 0; i < applied; i++)
            {
                string line = lines[i];
                int tab = line.IndexOf('\t');
                if (tab >= 0) line = line.Substring(0, tab);                            // çok sütunlu kopyada ilk sütun
                ApplyDefinition(startRow + i, line);
            }

            SaveDefinitions();
            Log("Tanım yapıştırıldı: " + applied + " satır (" + (startRow + 1) + ". satırdan itibaren" +
                (applied < count ? ", kalan " + (count - applied) + " satır sığmadı" : "") + ").");
        }

        /// <summary>Delete tuşu: seçili satırın tanım yazısını siler.</summary>
        private void ClearCurrentDefinition()
        {
            if (grid.CurrentCell == null) return;
            ClearDefinitionAt(grid.CurrentCell.RowIndex);
        }

        private void OnClearDefinitionRow(object sender, EventArgs e)
        {
            if (grid.CurrentCell == null) return;
            ClearDefinitionAt(grid.CurrentCell.RowIndex);
        }

        private void ClearDefinitionAt(int rowIndex)
        {
            if (_lastRead == null || grid.Rows.Count == 0)
            {
                lblSummary.Text = "Tanım temizlemek için önce OKU gerekiyor.";
                return;
            }
            if (rowIndex < 0 || rowIndex >= grid.Rows.Count) return;

            string existing = Convert.ToString(grid.Rows[rowIndex].Cells["colDef"].Value);
            if (existing.Length == 0) return;

            ApplyDefinition(rowIndex, "");
            SaveDefinitions();
            Log("Tanım temizlendi (" + (rowIndex + 1) + ". satır): \"" + existing + "\"");
        }

        /// <summary>Sağ tık: tablodaki tüm tanım yazılarını siler; kalıcı dosyaya da yansır.</summary>
        private void OnClearAllDefinitions(object sender, EventArgs e)
        {
            if (_lastRead == null || grid.Rows.Count == 0)
            {
                lblSummary.Text = "Tanım temizlemek için önce OKU gerekiyor.";
                return;
            }
            int filled = FilledDefinitionRows();
            if (filled == 0)
            {
                lblSummary.Text = "Tanım sütunu zaten boş.";
                return;
            }

            DialogResult answer = MessageBox.Show(this,
                "Tablodaki " + filled + " satırın tanım yazısı silinecek ve kalıcı tanım dosyasından da düşürülecek." +
                Environment.NewLine + "Onaylıyor musunuz?",
                "Tanım sütununu temizle", MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes) return;
            ClearAllDefinitions();
        }

        /// <summary>Tüm tanım yazılarını siler ve kalıcı dosyaya yazar; onay diyaloğu çağırandadır.</summary>
        private void ClearAllDefinitions()
        {
            int filled = FilledDefinitionRows();
            for (int i = 0; i < grid.Rows.Count; i++) ApplyDefinition(i, "");
            SaveDefinitions();
            Log("Tanım sütunu temizlendi: " + filled + " satır.");
            lblSummary.Text = "Tanım sütunu temizlendi: " + filled + " satır.";
        }

        private int FilledDefinitionRows()
        {
            int count = 0;
            for (int i = 0; i < grid.Rows.Count; i++)
            {
                if (Convert.ToString(grid.Rows[i].Cells["colDef"].Value).Length > 0) count++;
            }
            return count;
        }

        /// <summary>Menü seçenekleri yalnızca silinecek yazı varsa etkin olur.</summary>
        private void OnDefinitionMenuOpening(object sender, CancelEventArgs e)
        {
            bool hasRows = _lastRead != null && grid.Rows.Count > 0;
            bool currentHasText = false;
            if (hasRows && grid.CurrentCell != null)
                currentHasText = Convert.ToString(grid.Rows[grid.CurrentCell.RowIndex].Cells["colDef"].Value).Length > 0;
            _definitionMenu.Items[0].Enabled = currentHasText;
            _definitionMenu.Items[1].Enabled = hasRows && FilledDefinitionRows() > 0;
        }

        private static string CleanDefinitionText(string text)
        {
            if (text == null) return "";
            string cleaned = text.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ').Trim();
            return cleaned.Length > 120 ? cleaned.Substring(0, 120) : cleaned;
        }

        private static string DefinitionPath()
        {
            // GuiTest bu değişkenle geçici bir dosyaya yönlendirir; kullanıcının gerçek tanımlarına dokunmaz.
            string over = Environment.GetEnvironmentVariable("MODBUSSCAN_DEFINITIONS");
            if (over != null && over.Trim().Length > 0) return over;
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ModbusScan");
            return Path.Combine(dir, "definitions.txt");
        }

        private void LoadDefinitions()
        {
            try
            {
                string path = DefinitionPath();
                if (!File.Exists(path)) return;
                string[] lines = File.ReadAllLines(path, new UTF8Encoding(false));
                for (int i = 0; i < lines.Length; i++)
                {
                    string[] parts = lines[i].Split(new char[] { '\t' }, 3);
                    if (parts.Length < 3) continue;
                    byte fc;
                    int address;
                    if (!byte.TryParse(parts[0], out fc)) continue;
                    if (!int.TryParse(parts[1], out address)) continue;
                    string text = CleanDefinitionText(parts[2]);
                    if (text.Length == 0) continue;
                    _definitions[DefinitionKey(fc, address)] = text;
                }
            }
            catch (Exception)
            {
                // Bozuk ya da okunamayan tanım dosyası boş listeyle devam eder.
            }
        }

        private void SaveDefinitions()
        {
            try
            {
                string path = DefinitionPath();
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                List<string> lines = new List<string>();
                foreach (KeyValuePair<string, string> kv in _definitions)
                {
                    if (kv.Value.Length == 0) continue;
                    int sep = kv.Key.IndexOf(':');
                    lines.Add(kv.Key.Substring(0, sep) + "\t" + kv.Key.Substring(sep + 1) + "\t" + kv.Value);
                }
                File.WriteAllLines(path, lines.ToArray(), new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                Log("Tanımlar kaydedilemedi: " + ex.Message);
            }
        }

        private static string ScanPlanPath()
        {
            // Test izolasyonu için: MODBUSSCAN_SCANPLAN ortam değişkeniyle geçici dosyaya yönlendir.
            string over = Environment.GetEnvironmentVariable("MODBUSSCAN_SCANPLAN");
            if (over != null && over.Trim().Length > 0) return over;
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ModbusScan");
            return Path.Combine(dir, "scanplan.txt");
        }

        private void LoadScanPlan()
        {
            try
            {
                string path = ScanPlanPath();
                if (!File.Exists(path)) return;
                string[] lines = File.ReadAllLines(path, new UTF8Encoding(false));
                _scanSegments.Clear();
                for (int i = 0; i < lines.Length; i++)
                {
                    ScanSegment s = ScanSegment.Parse(lines[i]);
                    if (s != null) _scanSegments.Add(s);
                }
                if (scanGrid != null)
                {
                    scanGrid.RowCount = _scanSegments.Count;
                    scanGrid.Invalidate();
                }
                // Eğer segment varsa tarama panelini göster
                if (_scanSegments.Count > 0 && gbScan != null)
                {
                    gbScan.Visible = true;
                    UpdateScanPanelSize();
                }
            }
            catch (Exception)
            {
            }
        }

        private void SaveScanPlan()
        {
            try
            {
                string path = ScanPlanPath();
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                List<string> lines = new List<string>();
                foreach (ScanSegment s in _scanSegments)
                    lines.Add(s.ToLine());
                File.WriteAllLines(path, lines.ToArray(), new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                Log("Tarama planı kaydedilemedi: " + ex.Message);
            }
        }

        private RegisterRead _lastRead;

        private void OnRedisplay(object sender, EventArgs e)
        {
            UpdateDecodeControls();
            if (_lastRead != null) ShowRead(_lastRead);
        }

        private void OnClear(object sender, EventArgs e)
        {
            _lastRead = null;
            grid.Rows.Clear();
            scanResultGrid.Rows.Clear();
            ShowScanResults(false);
            txtLog.Clear();
            lblSummary.Text = "—";
        }

        private void Log(string message)
        {
            txtLog.AppendText(DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + "  " + message + Environment.NewLine);
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);

            updateTimer = new Timer();
            updateTimer.Interval = 60000;
            updateTimer.Tick += OnUpdateTimerTick;
            updateTimer.Start();

            firstCheckTimer = new Timer();
            firstCheckTimer.Interval = 1200;
            firstCheckTimer.Tick += OnFirstCheckTick;
            firstCheckTimer.Start();
        }

        private void OnFirstCheckTick(object sender, EventArgs e)
        {
            firstCheckTimer.Stop();
            StartUpdateCheck(false);
        }

        private void OnUpdateTimerTick(object sender, EventArgs e)
        {
            StartUpdateCheck(false);
        }

        private void OnCheckUpdate(object sender, EventArgs e)
        {
            StartUpdateCheck(true);
        }

        private void StartUpdateCheck(bool manual)
        {
            if (_updateCheckRunning || _updateInstallRunning) return;
            _updateCheckRunning = true;
            btnCheckUpdate.Enabled = false;
            if (manual)
            {
                lblUpdate.Text = "Güncelleme denetleniyor…";
                lblUpdate.ForeColor = Color.Gray;
            }

            System.Threading.Thread worker = new System.Threading.Thread(delegate()
            {
                string error;
                UpdateInfo info = Updater.Check(Updater.CurrentVersion, out error);
                Ui(delegate { FinishUpdateCheck(info, error, manual); });
            });
            worker.IsBackground = true;
            worker.Start();
        }

        private void FinishUpdateCheck(UpdateInfo info, string error, bool manual)
        {
            _updateCheckRunning = false;
            btnCheckUpdate.Enabled = !_updateInstallRunning;

            if (info == null)
            {
                if (manual)
                {
                    lblUpdate.Text = "Denetlenemedi: " + error;
                    lblUpdate.ForeColor = Color.Firebrick;
                    Log("Güncelleme denetimi başarısız: " + error);
                }
                else if (_updateInfo == null)
                {
                    lblUpdate.Text = "Güncelleme denetlenemedi (" + error + ")";
                    lblUpdate.ForeColor = Color.Gray;
                }
                return;
            }

            bool changed = _updateInfo == null || _updateInfo.Tag != info.Tag;
            _updateInfo = info;

            if (info.NewerAvailable)
            {
                btnUpdate.Enabled = !_updateInstallRunning;
                btnUpdate.Text = info.AssetAvailable ? "Güncelle" : "Sayfayı aç";
                lblUpdate.ForeColor = info.AssetAvailable ? Color.FromArgb(0, 128, 0) : Color.DarkOrange;
                lblUpdate.Text = info.AssetAvailable
                    ? "YENİ sürüm var: " + info.Tag + " (" + Updater.FormatSize(info.SizeBytes) + ")"
                    : "Yeni sürüm " + info.Tag + " ama hazır exe yok";
                if (changed || manual) Log("GitHub'da yeni sürüm: " + info.Tag);
            }
            else
            {
                btnUpdate.Enabled = false;
                btnUpdate.Text = "Güncelle";
                lblUpdate.ForeColor = Color.Gray;
                lblUpdate.Text = "Güncel: v" + Updater.CurrentVersion.ToString(3) +
                                 " · son kontrol " + DateTime.Now.ToString("HH:mm", CultureInfo.InvariantCulture);
                if (manual) Log("Güncelleme yok, en yeni sürüm " + info.Tag);
            }
        }

        private void OnInstallUpdate(object sender, EventArgs e)
        {
            if (_updateInfo == null || _updateInstallRunning) return;

            if (!_updateInfo.AssetAvailable)
            {
                try { Updater.OpenUrl(_updateInfo.ReleasePage); }
                catch (Exception ex) { Log("Tarayıcı açılamadı: " + ex.Message); }
                return;
            }

            _updateInstallRunning = true;
            btnUpdate.Enabled = false;
            btnCheckUpdate.Enabled = false;
            lblUpdate.Text = "Güncelleme hazırlanıyor…";
            lblUpdate.ForeColor = Color.Gray;

            UpdateInfo info = _updateInfo;
            string exePath = Application.ExecutablePath;

            System.Threading.Thread worker = new System.Threading.Thread(delegate()
            {
                string error;
                bool ok = Updater.TryInstall(info, exePath,
                    delegate(string status) { Ui(delegate { lblUpdate.Text = status; }); },
                    out error);
                Ui(delegate { FinishInstall(ok, error); });
            });
            worker.IsBackground = true;
            worker.Start();
        }

        private void FinishInstall(bool ok, string error)
        {
            if (ok)
            {
                lblUpdate.Text = "İndirildi, program kapanıyor…";
                lblUpdate.ForeColor = Color.FromArgb(0, 128, 0);
                Log("Güncelleme indirildi; değişim betiği başlatıldı, program kapanıyor.");
                Close();
                return;
            }

            _updateInstallRunning = false;
            btnCheckUpdate.Enabled = true;
            btnUpdate.Enabled = true;
            lblUpdate.Text = "Güncelleme başarısız: " + error;
            lblUpdate.ForeColor = Color.Firebrick;
            Log("Güncelleme başarısız: " + error);
        }

        private void Ui(Action action)
        {
            // Arka plan işi ile pencere kapanışı yarısabilir; bu durumda sessizce yoksay.
            try
            {
                if (IsHandleCreated) BeginInvoke(action);
                else action();
            }
            catch (Exception)
            {
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            SaveSettings();
            if (updateTimer != null) updateTimer.Stop();
            if (firstCheckTimer != null) firstCheckTimer.Stop();
            _tcp.Close();
            _rtu.Close();
            base.OnFormClosing(e);
        }

        /// <summary>Ctrl+V'yi tablonun kendi yapıştırmasından önce yakalar; Excel sütunu için kullanılır.</summary>
        private sealed class DefinitionGrid : DataGridView
        {
            public Action PasteDefinitionsHandler { get; set; }
            public Action DeleteDefinitionHandler { get; set; }

            protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
            {
                if (keyData == (Keys.Control | Keys.V))
                {
                    Action handler = PasteDefinitionsHandler;
                    if (handler != null)
                    {
                        handler();
                        return true;
                    }
                }
                if (keyData == Keys.Delete)
                {
                    Action handler = DeleteDefinitionHandler;
                    // Hücre düzenleniyorsa Delete normal metin silme davranışı olarak kalsın.
                    if (handler != null && !IsCurrentCellInEditMode)
                    {
                        handler();
                        return true;
                    }
                }
                return base.ProcessCmdKey(ref msg, keyData);
            }
        }
    }
}
