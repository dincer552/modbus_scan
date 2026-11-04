using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.IO.Ports;
using System.Net.Sockets;
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

        public MainForm()
        {
            BuildUi();
            LoadDefinitions();
            ApplyMode();
            UpdateConnectionState(false);
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
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 8.25f);

            gbConnection = new GroupBox();
            gbConnection.Text = "Bağlantı";
            gbConnection.SetBounds(8, 4, 484, 74);

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
            btnConnect.Click += OnConnect;
            gbConnection.Controls.Add(btnConnect);

            btnDisconnect = new Button();
            btnDisconnect.Text = "Kes";
            btnDisconnect.SetBounds(392, 23, 80, 25);
            btnDisconnect.Click += OnDisconnect;
            btnDisconnect.Enabled = false;
            gbConnection.Controls.Add(btnDisconnect);

            lblStatus = new Label();
            lblStatus.SetBounds(10, 50, 462, 16);
            lblStatus.Text = "Durum: bağlı değil";
            lblStatus.ForeColor = Color.Gray;
            gbConnection.Controls.Add(lblStatus);

            gbRead = new GroupBox();
            gbRead.Text = "Okuma";
            gbRead.SetBounds(8, 82, 484, 112);

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

            gbRead.Controls.Add(MakeLabel("Kelime", 352, 52));
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

            lblUpdate = new Label();
            lblUpdate.SetBounds(8, 201, 296, 16);
            lblUpdate.Text = "Güncelleme: henüz denetlenmedi";
            lblUpdate.ForeColor = Color.Gray;

            btnCheckUpdate = new Button();
            btnCheckUpdate.Text = "Denetle";
            btnCheckUpdate.SetBounds(310, 196, 82, 25);
            btnCheckUpdate.Click += OnCheckUpdate;

            btnUpdate = new Button();
            btnUpdate.Text = "Güncelle";
            btnUpdate.SetBounds(396, 196, 96, 25);
            btnUpdate.Enabled = false;
            btnUpdate.Click += OnInstallUpdate;

            grid = new DefinitionGrid();
            grid.SetBounds(8, 226, 484, 246);
            grid.AllowUserToAddRows = false;
            grid.AllowUserToDeleteRows = false;
            grid.AllowUserToResizeRows = false;
            grid.RowHeadersVisible = false;
            grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            grid.MultiSelect = false;
            grid.AllowUserToResizeColumns = false;
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

            txtLog = new TextBox();
            txtLog.SetBounds(8, 478, 484, 114);
            txtLog.Multiline = true;
            txtLog.ReadOnly = true;
            txtLog.ScrollBars = ScrollBars.Vertical;
            txtLog.BackColor = Color.White;
            txtLog.Font = new Font("Consolas", 8.25f);

            Controls.Add(gbConnection);
            Controls.Add(gbRead);
            Controls.Add(lblUpdate);
            Controls.Add(btnCheckUpdate);
            Controls.Add(btnUpdate);
            Controls.Add(grid);
            Controls.Add(txtLog);

            UpdateDecodeControls();
            AcceptButton = btnRead;
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
            }
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
            if (!int.TryParse(txtQuantity.Text.Trim(), out quantity) || quantity < 1 || quantity > 125)
            {
                lblSummary.Text = "Adet 1..125 olmalı.";
                return;
            }
            if (!int.TryParse(txtTimeout.Text.Trim(), out timeoutMs)) timeoutMs = 2000;

            byte fc = SelectedFunction();
            DateTime start = DateTime.Now;
            RegisterRead read;
            try
            {
                read = Client.Read(fc, address, quantity, timeoutMs);
            }
            catch (Exception ex)
            {
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

            read.ElapsedMilliseconds = (int)(DateTime.Now - start).TotalMilliseconds;
            _lastRead = read;
            lblSummary.ForeColor = Color.DimGray;
            ShowRead(read);
            Log("OKUMA FC" + fc.ToString("D2") + " @" + address + " x" + quantity +
                " → " + read.Data.Length + " bayt, " + read.ElapsedMilliseconds + " ms");
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
            byte[] current = CurrentBytesOfRow(read, rowIndex);

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
            grid.SuspendLayout();
            grid.Rows.Clear();

            bool bits = read.FunctionCode == 1 || read.FunctionCode == 2;
            int decodeType = cmbDecode.SelectedIndex;
            WordOrder order = (WordOrder)cmbWordOrder.SelectedIndex;

            for (int i = 0; i < read.Quantity; i++)
            {
                int pduAddress = read.StartAddress + i;
                grid.Rows.Add(new object[] {
                    i.ToString(),
                    ModbusTcpClient.DisplayAddress(read.FunctionCode, pduAddress, chkConvention.Checked).ToString(),
                    ValueOf(read, i, bits, decodeType, order),
                    DefinitionOf(read.FunctionCode, pduAddress)
                });
            }

            grid.ResumeLayout();
            lblSummary.Text = read.Quantity + " kayıt, " + read.Data.Length + " bayt, " +
                              read.ElapsedMilliseconds + " ms · çift tık: yaz · Tanım'a Ctrl+V";
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

        private static string CleanDefinitionText(string text)
        {
            if (text == null) return "";
            string cleaned = text.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ').Trim();
            return cleaned.Length > 120 ? cleaned.Substring(0, 120) : cleaned;
        }

        private static string DefinitionPath()
        {
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
                return base.ProcessCmdKey(ref msg, keyData);
            }
        }
    }
}
