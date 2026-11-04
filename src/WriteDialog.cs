using System;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace ModbusScan
{
    /// <summary>
    /// Tabloda bir hücreye çift tıklandığında açılan yazma penceresi.
    /// Holding register için FC06/FC16, coil için FC05 kullanır; yazmadan önce
    /// gönderilecek baytları gösterir ve yazma sonrası kaydı geri okuyarak doğrular.
    /// </summary>
    public class WriteDialog : Form
    {
        private readonly ModbusTcpClient _client;
        private readonly byte _functionCode;
        private readonly int _address;
        private readonly byte[] _current;
        private readonly bool _convention;
        private readonly int _timeoutMs;

        private Label lblArea;
        private Label lblCurrent;
        private Label lblType;
        private Label lblValue;
        private Label lblWord;
        private Label lblPreview;
        private Label lblResult;
        private Label lblHint;
        private ComboBox cmbType;
        private ComboBox cmbWord;
        private ComboBox cmbCoil;
        private TextBox txtValue;
        private Button btnWrite;
        private Button btnClose;

        private bool _isCoil;
        private bool _readOnly;

        /// <summary>Kullanıcı en az bir başarılı yazma yaptıysa true; ana pencere tabloyu buna göre tazeler.</summary>
        public bool WritePerformed { get; set; }

        /// <summary>Son başarılı yazmanın fonksiyon kodu (5, 6 veya 16).</summary>
        public byte WrittenFunctionCode { get; set; }

        public WriteDialog(ModbusTcpClient client, byte functionCode, int address,
                           byte[] current, bool convention, int timeoutMs)
        {
            _client = client;
            _functionCode = functionCode;
            _address = address;
            _current = current ?? new byte[0];
            _convention = convention;
            _timeoutMs = timeoutMs;
            _isCoil = functionCode == 1;
            _readOnly = functionCode == 2 || functionCode == 4;
            BuildUi();
            RefreshPreview();
        }

        private string DisplayAddress
        {
            get { return ModbusTcpClient.DisplayAddress(_functionCode, _address, _convention).ToString(); }
        }

        private void BuildUi()
        {
            Text = "Yaz — kayıt " + DisplayAddress;
            ClientSize = new Size(430, 296);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Segoe UI", 8.25f);

            lblArea = new Label();
            lblArea.SetBounds(12, 10, 406, 16);
            lblArea.Text = AreaName() + " · adres " + DisplayAddress + " (PDU " + _address + ")";
            lblArea.Font = new Font("Segoe UI", 9f, FontStyle.Bold);

            lblCurrent = new Label();
            lblCurrent.SetBounds(12, 30, 406, 16);
            lblCurrent.Text = CurrentText();

            cmbType = new ComboBox();
            cmbType.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbType.SetBounds(12, 62, 176, 22);
            if (_isCoil) cmbType.Items.Add("Bobin (FC05)");
            else if (_readOnly) cmbType.Items.Add("Salt okunur alan");
            else
            {
                cmbType.Items.Add("Uint16 (FC06)");
                cmbType.Items.Add("Int16 (FC06)");
                cmbType.Items.Add("Hex (FC06)");
                cmbType.Items.Add("Float32 (FC16, 2 kelime)");
                cmbType.Items.Add("Uint32 (FC16, 2 kelime)");
                cmbType.Items.Add("Int32 (FC16, 2 kelime)");
            }
            cmbType.SelectedIndex = 0;
            cmbType.SelectedIndexChanged += OnTypeChanged;

            lblType = MakeLabel("Yazma biçimi", 12, 46);

            lblWord = MakeLabel("Kelime", 200, 65);
            cmbWord = new ComboBox();
            cmbWord.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbWord.SetBounds(244, 62, 76, 22);
            cmbWord.Items.AddRange(new object[] { "ABCD", "CDAB", "BADC", "DCBA" });
            cmbWord.SelectedIndex = 0;
            cmbWord.SelectedIndexChanged += OnTypeChanged;

            txtValue = new TextBox();
            txtValue.SetBounds(12, 106, 176, 22);
            txtValue.TextChanged += OnTypeChanged;

            lblValue = MakeLabel("Değer", 12, 90);

            cmbCoil = new ComboBox();
            cmbCoil.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbCoil.SetBounds(12, 106, 176, 22);
            cmbCoil.Items.AddRange(new object[] { "ACIK (ON)", "KAPALI (OFF)" });
            cmbCoil.SelectedIndex = 0;
            cmbCoil.SelectedIndexChanged += OnTypeChanged;

            lblPreview = new Label();
            lblPreview.SetBounds(12, 136, 406, 16);
            lblPreview.Font = new Font("Consolas", 9f);

            btnWrite = new Button();
            btnWrite.Text = "Yaz";
            btnWrite.SetBounds(12, 162, 92, 27);
            btnWrite.Click += OnWrite;

            btnClose = new Button();
            btnClose.Text = "Kapat";
            btnClose.SetBounds(112, 162, 92, 27);
            btnClose.DialogResult = DialogResult.Cancel;
            btnClose.Click += delegate { Close(); };

            lblResult = new Label();
            lblResult.SetBounds(12, 198, 406, 34);
            lblResult.Text = "Hazır.";
            lblResult.ForeColor = Color.Gray;

            lblHint = new Label();
            lblHint.SetBounds(12, 238, 406, 50);
            lblHint.ForeColor = Color.DimGray;
            lblHint.Text = _readOnly
                ? "Bu alan Modbus'ta salt okunur (giriş). Cihazlar buraya yazmayı genelde ILLEGAL DATA ADDRESS ile reddeder; " +
                  "değer değiştirmek için karşılık gelen holding register / coil alanına yaz."
                : "Yazma cihazda anında etkili olabilir. Gönderilecek baytları kontrol edip Yaz'a bas; " +
                  "yazmadan sonra kayıt geri okunur.";

            Controls.Add(lblArea);
            Controls.Add(lblCurrent);
            Controls.Add(lblType);
            Controls.Add(cmbType);
            Controls.Add(lblWord);
            Controls.Add(cmbWord);
            Controls.Add(lblValue);
            Controls.Add(txtValue);
            Controls.Add(cmbCoil);
            Controls.Add(lblPreview);
            Controls.Add(btnWrite);
            Controls.Add(btnClose);
            Controls.Add(lblResult);
            Controls.Add(lblHint);
            AcceptButton = btnWrite;
            CancelButton = btnClose;

            UpdateTypeVisibility();
        }

        private static Label MakeLabel(string text, int x, int y)
        {
            Label l = new Label();
            l.Text = text;
            l.SetBounds(x, y, 100, 16);
            return l;
        }

        private string AreaName()
        {
            switch (_functionCode)
            {
                case 1: return "Coil";
                case 2: return "Discrete input";
                case 4: return "Input register";
                default: return "Holding register";
            }
        }

        private string CurrentText()
        {
            if (_current.Length == 0) return "Mevcut değer okunamadı.";
            if (_isCoil)
            {
                int bit = _current[0] & 1;
                return "Mevcut: " + (bit == 1 ? "ACIK (1)" : "KAPALI (0)");
            }
            int u = ModbusTcpClient.UInt16(_current, 0);
            int i = ModbusTcpClient.Int16(_current, 0);
            return "Mevcut: 0x" + u.ToString("X4") + " = " + u + " (int16 " + i + ")";
        }

        private void UpdateTypeVisibility()
        {
            int type = _isCoil || _readOnly ? 0 : cmbType.SelectedIndex;
            bool wide = type >= 3;
            cmbWord.Enabled = wide && !_isCoil && !_readOnly;
            lblWord.Visible = wide;
            txtValue.Visible = !_isCoil && !_readOnly;
            lblValue.Visible = !_isCoil && !_readOnly;
            cmbCoil.Visible = _isCoil;
            cmbType.Enabled = !_readOnly;
            btnWrite.Enabled = !_readOnly;
        }

        private void OnTypeChanged(object sender, EventArgs e)
        {
            UpdateTypeVisibility();
            RefreshPreview();
        }

        private bool TryPayload(out byte[] payload, out string error)
        {
            payload = null;
            error = null;

            if (_isCoil)
            {
                bool on = cmbCoil.SelectedIndex == 0;
                payload = ModbusTcpClient.EncodeUInt16(on ? 0xFF00 : 0x0000);
                return true;
            }
            if (_readOnly)
            {
                error = "Salt okunur alan.";
                return false;
            }

            WordOrder order = (WordOrder)cmbWord.SelectedIndex;
            string text = txtValue.Text.Trim();
            if (text.Length == 0)
            {
                error = "Değer gir.";
                return false;
            }

            int type = cmbType.SelectedIndex;
            try
            {
                if (type == 2)
                {
                    string hex = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text.Substring(2) : text;
                    int value = int.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    if (value < 0 || value > 0xFFFF)
                    {
                        error = "Hex 0..FFFF aralığında olmalı.";
                        return false;
                    }
                    payload = ModbusTcpClient.EncodeUInt16(value);
                    return true;
                }
                if (type == 0)
                {
                    int value = int.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture);
                    if (value < 0 || value > 0xFFFF)
                    {
                        error = "Uint16 0..65535 aralığında olmalı.";
                        return false;
                    }
                    payload = ModbusTcpClient.EncodeUInt16(value);
                    return true;
                }
                if (type == 1)
                {
                    int value = int.Parse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
                    if (value < short.MinValue || value > short.MaxValue)
                    {
                        error = "Int16 -32768..32767 aralığında olmalı.";
                        return false;
                    }
                    payload = ModbusTcpClient.EncodeInt16(value);
                    return true;
                }
                if (type == 3)
                {
                    float value = float.Parse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture);
                    payload = ModbusTcpClient.EncodeFloat32(value, order);
                    return true;
                }
                if (type == 4)
                {
                    ulong value = ulong.Parse(text, CultureInfo.InvariantCulture);
                    if (value > uint.MaxValue)
                    {
                        error = "Uint32 0..4294967295 aralığında olmalı.";
                        return false;
                    }
                    payload = ModbusTcpClient.EncodeUInt32((uint)value, order);
                    return true;
                }
                int signed = int.Parse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
                payload = ModbusTcpClient.EncodeInt32(signed, order);
                return true;
            }
            catch (Exception)
            {
                error = "Değer çözümlenemedi: \"" + text + "\"";
                return false;
            }
        }

        private void RefreshPreview()
        {
            byte[] payload;
            string error;
            if (!TryPayload(out payload, out error))
            {
                lblPreview.Text = error ?? "";
                lblPreview.ForeColor = Color.Firebrick;
                btnWrite.Enabled = false;
                return;
            }

            lblPreview.ForeColor = Color.DimGray;
            btnWrite.Enabled = !_readOnly;

            if (_isCoil)
            {
                bool on = (payload[0] & 0xFF) == 0xFF;
                lblPreview.Text = "FC05 @" + _address + " (" + DisplayAddress + ") → " +
                                 ModbusTcpClient.ToHex(payload) + "  " + (on ? "ACIK" : "KAPALI");
                return;
            }

            bool wide = cmbType.SelectedIndex >= 3;
            string fc = wide ? "FC16 @" + _address + " x2" : "FC06 @" + _address;
            lblPreview.Text = fc + " (" + DisplayAddress + ") → " + ModbusTcpClient.ToHex(payload);
        }

        private void OnWrite(object sender, EventArgs e)
        {
            byte[] payload;
            string error;
            if (!TryPayload(out payload, out error))
            {
                ShowResult(false, error ?? "Değer geçersiz.");
                return;
            }

            bool wide = !_isCoil && cmbType.SelectedIndex >= 3;
            byte usedFc = _isCoil ? (byte)5 : (wide ? (byte)16 : (byte)6);
            Cursor previous = Cursor;
            Cursor = Cursors.WaitCursor;
            try
            {
                if (_isCoil) _client.WriteSingleCoil(_address, (payload[0] & 0xFF) == 0xFF, _timeoutMs);
                else if (wide) _client.WriteMultipleRegisters(_address, payload, _timeoutMs);
                else _client.WriteSingleRegister(_address, (payload[0] << 8) | payload[1], _timeoutMs);

                WritePerformed = true;
                WrittenFunctionCode = usedFc;
                ShowResult(true, ReadBack(wide ? 2 : 1));
            }
            catch (Exception ex)
            {
                ShowResult(false, ex.Message);
            }
            finally
            {
                Cursor = previous;
            }
        }

        /// <summary>Yazılan kaydı geri okuyup "gelen değer" satırını hazırlar.</summary>
        private string ReadBack(int words)
        {
            try
            {
                byte fc = _isCoil ? (byte)1 : (byte)3;
                RegisterRead back = _client.Read(fc, _address, _isCoil ? 1 : words, _timeoutMs);
                if (_isCoil)
                {
                    int bit = back.Data[0] & 1;
                    return "Geri okundu: " + (bit == 1 ? "ACIK (1)" : "KAPALI (0)");
                }
                string hex = ModbusTcpClient.ToHex(back.Data);
                WordOrder order = (WordOrder)cmbWord.SelectedIndex;
                if (words == 1)
                {
                    int u = ModbusTcpClient.UInt16(back.Data, 0);
                    return "Geri okundu: " + hex + " = " + u + " (int16 " + ModbusTcpClient.Int16(back.Data, 0) + ")";
                }
                switch (cmbType.SelectedIndex)
                {
                    case 3: return "Geri okundu: " + hex + " = " + ModbusTcpClient.FormatFloat(ModbusTcpClient.Float32(back.Data, 0, order)) + " (" + order + ")";
                    case 4: return "Geri okundu: " + hex + " = " + ModbusTcpClient.UInt32(back.Data, 0, order) + " (" + order + ")";
                    default: return "Geri okundu: " + hex + " = " + ModbusTcpClient.Int32(back.Data, 0, order) + " (" + order + ")";
                }
            }
            catch (Exception ex)
            {
                return "Yazıldı ama geri okunamadı: " + ex.Message;
            }
        }

        private void ShowResult(bool ok, string message)
        {
            lblResult.ForeColor = ok ? Color.FromArgb(0, 128, 0) : Color.Firebrick;
            lblResult.Text = (ok ? "OK " : "HATA ") + message;
            if (ok) RefreshPreview();
        }
    }
}
