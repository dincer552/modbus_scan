using System;
using System.Drawing;
using System.Globalization;
using System.Net.Sockets;
using System.Windows.Forms;

namespace ModbusScan
{
    public class MainForm : Form
    {
        private readonly ModbusTcpClient _client = new ModbusTcpClient();

        private GroupBox gbConnection;
        private GroupBox gbRead;
        private TextBox txtIp, txtPort, txtUnit, txtTimeout, txtAddress, txtQuantity, txtLog;
        private ComboBox cmbFunction, cmbDecode, cmbWordOrder;
        private CheckBox chkConvention;
        private Button btnConnect, btnDisconnect, btnRead, btnClear;
        private Label lblStatus, lblSummary;
        private DataGridView grid;

        public MainForm()
        {
            BuildUi();
            UpdateConnectionState(false);
        }

        private void BuildUi()
        {
            Text = "Modbus TCP Test v" + typeof(Program).Assembly.GetName().Version.ToString(3);
            ClientSize = new Size(500, 600);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 8.25f);

            gbConnection = new GroupBox();
            gbConnection.Text = "Bağlantı";
            gbConnection.SetBounds(8, 4, 484, 74);

            gbConnection.Controls.Add(MakeLabel("IP", 10, 28));
            txtIp = MakeTextBox("127.0.0.1", 30, 25, 92);
            gbConnection.Controls.Add(txtIp);
            gbConnection.Controls.Add(MakeLabel("Port", 128, 28));
            txtPort = MakeTextBox("502", 158, 25, 38);
            gbConnection.Controls.Add(txtPort);
            gbConnection.Controls.Add(MakeLabel("ID", 202, 28));
            txtUnit = MakeTextBox("1", 216, 25, 30);
            gbConnection.Controls.Add(txtUnit);
            gbConnection.Controls.Add(MakeLabel("ms", 252, 28));
            txtTimeout = MakeTextBox("2000", 268, 25, 40);
            gbConnection.Controls.Add(txtTimeout);

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

            grid = new DataGridView();
            grid.SetBounds(8, 198, 484, 274);
            grid.AllowUserToAddRows = false;
            grid.AllowUserToDeleteRows = false;
            grid.AllowUserToResizeRows = false;
            grid.ReadOnly = true;
            grid.RowHeadersVisible = false;
            grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            grid.MultiSelect = false;
            grid.AllowUserToResizeColumns = false;
            grid.ScrollBars = ScrollBars.Vertical;
            grid.BackgroundColor = Color.White;
            grid.BorderStyle = BorderStyle.Fixed3D;
            grid.Font = new Font("Consolas", 9f);
            grid.Columns.Add(NewColumn("colIndex", "#", 30, DataGridViewAutoSizeColumnMode.None));
            grid.Columns.Add(NewColumn("colAddress", "Adres", 60, DataGridViewAutoSizeColumnMode.None));
            grid.Columns.Add(NewColumn("colHex", "Hex", 56, DataGridViewAutoSizeColumnMode.None));
            grid.Columns.Add(NewColumn("colDec", "uint", 50, DataGridViewAutoSizeColumnMode.None));
            grid.Columns.Add(NewColumn("colSigned", "int", 50, DataGridViewAutoSizeColumnMode.None));
            grid.Columns.Add(NewColumn("colValue", "Değer", 130, DataGridViewAutoSizeColumnMode.Fill));

            txtLog = new TextBox();
            txtLog.SetBounds(8, 478, 484, 114);
            txtLog.Multiline = true;
            txtLog.ReadOnly = true;
            txtLog.ScrollBars = ScrollBars.Vertical;
            txtLog.BackColor = Color.White;
            txtLog.Font = new Font("Consolas", 8.25f);

            Controls.Add(gbConnection);
            Controls.Add(gbRead);
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

        private static DataGridViewTextBoxColumn NewColumn(string name, string header, int width, DataGridViewAutoSizeColumnMode mode)
        {
            DataGridViewTextBoxColumn col = new DataGridViewTextBoxColumn();
            col.Name = name;
            col.HeaderText = header;
            col.Width = width;
            col.AutoSizeMode = mode;
            col.SortMode = DataGridViewColumnSortMode.NotSortable;
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

        private void OnConnect(object sender, EventArgs e)
        {
            string ip = txtIp.Text.Trim();
            int port, timeoutMs;
            byte unit;
            if (!int.TryParse(txtPort.Text.Trim(), out port) || port < 1 || port > 65535)
            {
                Log("Geçersiz port.");
                return;
            }
            if (!int.TryParse(txtTimeout.Text.Trim(), out timeoutMs) || timeoutMs < 100)
            {
                Log("Geçersiz zaman aşımı (en az 100 ms).");
                return;
            }
            if (!byte.TryParse(txtUnit.Text.Trim(), out unit))
            {
                Log("Geçersiz ünite id (0..255).");
                return;
            }

            btnConnect.Enabled = false;
            try
            {
                DateTime t0 = DateTime.Now;
                _client.Connect(ip, port, timeoutMs);
                _client.UnitId = unit;
                int ms = (int)(DateTime.Now - t0).TotalMilliseconds;
                Log("Bağlandı: " + ip + ":" + port + " (unit " + unit + ") " + ms + " ms");
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
            _client.Close();
            Log("Bağlantı kesildi.");
            UpdateConnectionState(false);
        }

        private void UpdateConnectionState(bool connected)
        {
            btnConnect.Enabled = !connected;
            btnDisconnect.Enabled = connected;
            btnRead.Enabled = connected;
            txtIp.Enabled = !connected;
            txtPort.Enabled = !connected;
            txtUnit.Enabled = !connected;
            lblStatus.Text = connected
                ? "Durum: bağlı → " + txtIp.Text.Trim() + ":" + txtPort.Text.Trim()
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
                read = _client.Read(fc, address, quantity, timeoutMs);
            }
            catch (Exception ex)
            {
                read = null;
                lblSummary.Text = "Hata: " + ShortMessage(ex);
                lblSummary.ForeColor = Color.Firebrick;
                Log("OKUMA FC" + fc.ToString("D2") + " @" + address + " x" + quantity + " → HATA: " + ex.Message);
                if (ex is SocketException || ex is TimeoutException) UpdateConnectionState(false);
                return;
            }

            read.ElapsedMilliseconds = (int)(DateTime.Now - start).TotalMilliseconds;
            _lastRead = read;
            lblSummary.ForeColor = Color.DimGray;
            ShowRead(read);
            Log("OKUMA FC" + fc.ToString("D2") + " @" + address + " x" + quantity +
                " → " + read.Data.Length + " bayt, " + read.ElapsedMilliseconds + " ms");
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
            grid.Columns["colHex"].Visible = !bits;
            grid.Columns["colDec"].Visible = !bits;
            grid.Columns["colSigned"].Visible = !bits;

            int decodeType = cmbDecode.SelectedIndex;
            WordOrder order = (WordOrder)cmbWordOrder.SelectedIndex;
            int baseAddress = read.StartAddress;

            for (int i = 0; i < read.Quantity; i++)
            {
                int offset = i * (bits ? 1 : 2);
                string hex = "";
                string dec = "";
                string signed = "";
                string value = "";

                if (bits)
                {
                    if (offset / 8 < read.Data.Length)
                    {
                        int bit = (read.Data[offset / 8] >> (offset % 8)) & 1;
                        value = bit.ToString();
                    }
                }
                else if (offset + 1 < read.Data.Length)
                {
                    hex = read.Data[offset].ToString("X2") + " " + read.Data[offset + 1].ToString("X2");
                    dec = ModbusTcpClient.UInt16(read.Data, offset).ToString();
                    signed = ModbusTcpClient.Int16(read.Data, offset).ToString();

                    bool wide = decodeType >= 3 && offset % 4 == 0 && offset + 3 < read.Data.Length;
                    if (decodeType >= 3 && !wide)
                    {
                        value = "";
                    }
                    else if (wide)
                    {
                        switch (decodeType)
                        {
                            case 3: value = ModbusTcpClient.FormatFloat(ModbusTcpClient.Float32(read.Data, offset, order)); break;
                            case 4: value = ModbusTcpClient.UInt32(read.Data, offset, order).ToString(); break;
                            default: value = ModbusTcpClient.Int32(read.Data, offset, order).ToString(); break;
                        }
                    }
                    else
                    {
                        switch (decodeType)
                        {
                            case 0: value = dec; break;
                            case 1: value = signed; break;
                            case 2: value = "0x" + read.Data[offset].ToString("X2") + read.Data[offset + 1].ToString("X2"); break;
                            default: value = dec; break;
                        }
                    }
                }

                int displayAddress = baseAddress + i;
                if (chkConvention.Checked)
                {
                    switch (read.FunctionCode)
                    {
                        case 3: displayAddress += 40001; break;
                        case 4: displayAddress += 30001; break;
                        case 2: displayAddress += 10001; break;
                        default: displayAddress += 1; break;
                    }
                }

                grid.Rows.Add(new object[] {
                    i.ToString(), displayAddress.ToString(), hex, dec, signed, value
                });
            }

            grid.ResumeLayout();
            lblSummary.Text = read.Quantity + " kayıt, " + read.Data.Length + " bayt, " +
                              read.ElapsedMilliseconds + " ms";
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

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _client.Close();
            base.OnFormClosing(e);
        }
    }
}
