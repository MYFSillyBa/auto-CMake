using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace CMakeDapLink.App;

internal sealed class DesktopDevicesPanel : UserControl
{
    private const int MaximumLogCharacters = 1024 * 1024;
    private readonly string _root;
    private readonly Func<string> _getDiagnosticText;
    private readonly bool _english;
    private readonly DesktopSerialSession _serial;
    private readonly ConcurrentQueue<(DateTime Time, DesktopSerialEvent Event)> _incoming = new();
    private readonly Queue<string> _retainedLog = new();
    private readonly System.Windows.Forms.Timer _receiveTimer = new() { Interval = 100 };
    private readonly ComboBox _ports = new() { Width = 150, DropDownStyle = ComboBoxStyle.DropDown };
    private readonly ComboBox _baud = new() { Width = 110, DropDownStyle = ComboBoxStyle.DropDown, Text = "115200" };
    private readonly ComboBox _mode = new() { Width = 100, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _newline = new() { Width = 120, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly RichTextBox _log = new()
    {
        Dock = DockStyle.Fill, ReadOnly = true, DetectUrls = false, WordWrap = false,
        BorderStyle = BorderStyle.None, BackColor = Color.FromArgb(247, 249, 252),
        ForeColor = Color.FromArgb(43, 57, 77), Font = new("Consolas", 9.5f), ScrollBars = RichTextBoxScrollBars.Both
    };
    private readonly TextBox _outgoing = new() { Multiline = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical, MaxLength = 131072, Font = new("Consolas", 9.5f) };
    private readonly Label _connection = new() { AutoSize = true, Margin = new(0, 10, 0, 6) };
    private readonly Label _counters = new() { AutoSize = true, Margin = new(0, 0, 0, 10) };
    private readonly Label _status = new() { AutoSize = true, ForeColor = Color.FromArgb(128, 83, 38), Margin = new(0, 8, 0, 6) };
    private readonly SoftButton _refresh;
    private readonly SoftButton _connect;
    private readonly SoftButton _disconnect;
    private readonly SoftButton _send;
    private readonly SoftButton _export;
    private readonly SoftButton _report;
    private bool _connected;
    private bool _busy;
    private bool _stopping;
    private long _receivedBytes;
    private long _sentBytes;
    private long _droppedChunks;
    private int _logCharacters;
    private string? _activePort;
    private int _activeBaud;

    public DesktopDevicesPanel(string root, Func<string> getDiagnosticText, bool english = false)
    {
        _root = root; _getDiagnosticText = getDiagnosticText; _english = english;
        _serial = new(Receive);
        Dock = DockStyle.Fill; AutoScaleMode = AutoScaleMode.Dpi; BackColor = Color.White;
        ForeColor = Color.FromArgb(42, 55, 74); Font = new("Microsoft YaHei UI", 9.5f); AutoScroll = true;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
        var layout = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Padding = new(20), Margin = new(0) };
        layout.ColumnStyles.Add(new(SizeType.Percent, 100)); Controls.Add(layout);
        var title = Caption(T("设备与故障报告", "Devices and Fault Reports")); title.Font = new(Font.FontFamily, 17, FontStyle.Bold); title.Margin = new(0, 0, 0, 12); layout.Controls.Add(title);
        layout.Controls.Add(Wrapped(T("串口连接由按钮手动触发。报告只在本机预览和保存。", "Connect to a serial port manually. Reports are previewed and saved locally."), layout));

        var serialCard = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Padding = new(16), Margin = new(0, 14, 0, 18), BackColor = Color.FromArgb(249, 251, 254) };
        serialCard.ColumnStyles.Add(new(SizeType.Percent, 100)); layout.Controls.Add(serialCard);
        var serialTitle = Caption(T("串口终端", "Serial Terminal")); serialTitle.Font = new(Font.FontFamily, 12, FontStyle.Bold); serialTitle.Margin = new(0, 0, 0, 10); serialCard.Controls.Add(serialTitle);
        serialCard.Controls.Add(Wrapped(T("8 数据位、无校验、1 停止位；连接时不主动拉高 DTR / RTS。日志保留最近 1500 条、最多 1 MiB。", "8 data bits, no parity, 1 stop bit; DTR / RTS stay disabled on connect. The log retains up to 1500 recent entries and 1 MiB."), serialCard));
        var controls = Flow();
        controls.Controls.Add(FieldLabel(T("串口", "Port"))); controls.Controls.Add(_ports);
        controls.Controls.Add(FieldLabel(T("波特率", "Baud rate"))); controls.Controls.Add(_baud);
        _baud.Items.AddRange(new object[] { "9600", "19200", "38400", "57600", "115200", "230400", "460800", "921600" });
        _refresh = MakeButton(T("刷新串口", "Refresh ports")); _connect = MakeButton(T("连接串口", "Connect"), true); _disconnect = MakeButton(T("断开连接", "Disconnect"));
        controls.Controls.AddRange([_refresh, _connect, _disconnect]); serialCard.Controls.Add(controls);
        _refresh.Click += async (_, _) => await RunAsync(_refresh, RefreshPortsAsync);
        _connect.Click += async (_, _) => await RunAsync(_connect, ConnectAsync);
        _disconnect.Click += async (_, _) => await RunAsync(_disconnect, async () => { await _serial.DisconnectAsync(); _connected = false; UpdateState(); });
        serialCard.Controls.Add(_connection); serialCard.Controls.Add(_counters);
        var logHost = new Panel { Dock = DockStyle.Fill, Height = 260, Padding = new(10), Margin = new(0, 0, 0, 12), BackColor = _log.BackColor };
        logHost.Controls.Add(_log); serialCard.Controls.Add(logHost);
        serialCard.RowStyles.Add(new(SizeType.AutoSize)); serialCard.RowStyles.Add(new(SizeType.AutoSize)); serialCard.RowStyles.Add(new(SizeType.AutoSize));
        serialCard.RowStyles.Add(new(SizeType.AutoSize)); serialCard.RowStyles.Add(new(SizeType.AutoSize)); serialCard.RowStyles.Add(new(SizeType.Absolute, 260));
        var sendControls = Flow();
        _mode.Items.AddRange(new object[] { "ASCII", "HEX" }); _mode.SelectedIndex = 0;
        _newline.Items.AddRange(new object[] { T("不追加", "None"), "LF", "CRLF" }); _newline.SelectedIndex = 0;
        sendControls.Controls.Add(FieldLabel(T("发送与显示格式", "Send / display format"))); sendControls.Controls.Add(_mode);
        sendControls.Controls.Add(FieldLabel(T("行尾", "Line ending"))); sendControls.Controls.Add(_newline); serialCard.Controls.Add(sendControls);
        serialCard.Controls.Add(Wrapped(T("ASCII 只接受 ASCII 字符；HEX 使用完整字节，例如 01 A5 FF。发送上限为 64 KiB。", "ASCII accepts ASCII characters only. HEX requires complete bytes, for example 01 A5 FF. Each send is limited to 64 KiB."), serialCard));
        var outgoingHost = new Panel { Dock = DockStyle.Fill, Height = 78, Padding = new(0, 2, 0, 0), Margin = new(0, 4, 0, 8) };
        outgoingHost.Controls.Add(_outgoing); serialCard.Controls.Add(outgoingHost);
        serialCard.RowStyles.Add(new(SizeType.AutoSize)); serialCard.RowStyles.Add(new(SizeType.AutoSize)); serialCard.RowStyles.Add(new(SizeType.Absolute, 78));
        var sendActions = Flow(); _send = MakeButton(T("发送", "Send"), true); _export = MakeButton(T("导出串口日志…", "Export serial log…"));
        sendActions.Controls.AddRange([_send, _export]); serialCard.Controls.Add(sendActions); serialCard.Controls.Add(_status);
        _send.Click += async (_, _) => await RunAsync(_send, SendAsync);
        _export.Click += async (_, _) => await RunAsync(_export, ExportLogAsync);

        var reportTitle = Caption(T("故障报告", "Fault Report")); reportTitle.Font = new(Font.FontFamily, 12, FontStyle.Bold); layout.Controls.Add(reportTitle);
        layout.Controls.Add(Wrapped(T("收集当前诊断信息，并隐藏常见路径、IP、邮箱与凭据。先检查可编辑的报告预览，再选择保存位置。", "Collect current diagnostics and hide common paths, IP addresses, email addresses, and credentials. Review the editable preview before choosing a save location."), layout));
        var reportActions = Flow(); _report = MakeButton(T("预览故障报告…", "Preview fault report…")); reportActions.Controls.Add(_report); layout.Controls.Add(reportActions);
        _report.Click += (_, _) => PreviewReport();
        foreach (var control in new Control[] { _ports, _baud, _mode, _newline }) control.Margin = new(0, 7, 12, 7);
        _receiveTimer.Tick += (_, _) => FlushIncoming(); _receiveTimer.Start(); UpdateState();
    }

    private string T(string chinese, string english) => _english ? english : chinese;
    private static Label Caption(string text) => new() { Text = text, AutoSize = true, Margin = new(0, 0, 0, 6) };
    private static FlowLayoutPanel Flow() => new() { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true, Margin = new(0), Padding = new(0) };
    private static Label FieldLabel(string text) => new() { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new(0, 11, 8, 6) };
    private static SoftButton MakeButton(string text, bool primary = false) => new()
    {
        Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Cursor = Cursors.Hand,
        Margin = new(0, 3, 9, 5), BackColor = primary ? Color.FromArgb(57, 92, 197) : Color.FromArgb(232, 238, 251),
        ForeColor = primary ? Color.White : Color.FromArgb(46, 77, 145)
    };
    private Label Wrapped(string text, Control parent)
    {
        var label = Caption(text); label.ForeColor = Color.FromArgb(104, 117, 137); label.Margin = new(0, 0, 0, 10);
        void ResizeLabel() => label.MaximumSize = new(Math.Max(180, parent.ClientSize.Width - parent.Padding.Horizontal - 8), 0);
        parent.SizeChanged += (_, _) => ResizeLabel(); ResizeLabel(); return label;
    }

    private async Task RunAsync(SoftButton active, Func<Task> action)
    {
        if (_busy || _stopping) return;
        _busy = true; active.IsBusy = true; _status.Text = T("正在处理…", "Working…"); UpdateState();
        try { await action(); if (!_stopping) _status.Text = T("操作完成。", "Operation completed."); }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        { if (!_stopping) _status.Text = T("操作未完成：", "Operation failed: ") + ex.Message; }
        finally
        {
            _busy = false;
            if (!_stopping) { active.IsBusy = false; UpdateState(); }
        }
    }

    private async Task RefreshPortsAsync()
    {
        var ports = await _serial.ListAsync(); if (_stopping) return;
        var selected = _ports.Text; _ports.BeginUpdate();
        try
        {
            _ports.Items.Clear(); _ports.Items.AddRange(ports.Cast<object>().ToArray());
            _ports.Text = !string.IsNullOrWhiteSpace(selected) ? selected : ports.FirstOrDefault() ?? "";
        }
        finally { _ports.EndUpdate(); }
        if (ports.Length == 0) _status.Text = T("未发现串口，可输入 COM 端口名称后连接。", "No serial ports found. You can enter a COM port name manually.");
    }

    private async Task ConnectAsync()
    {
        if (!int.TryParse(_baud.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var baud))
            throw new ArgumentException(T("波特率须为 50–4000000 的整数。", "Baud rate must be an integer from 50 to 4000000."));
        var port = _ports.Text.Trim().ToUpperInvariant(); await _serial.ConnectAsync(port, baud);
        if (_stopping) return; _connected = true; _activePort = port; _activeBaud = baud; UpdateState();
    }

    private async Task SendAsync()
    {
        if (!_connected) throw new InvalidOperationException(T("请先连接串口。", "Connect a serial port first."));
        var bytes = DesktopSerialSession.CreatePayload(_outgoing.Text, _mode.Text, _newline.SelectedIndex switch { 1 => "LF", 2 => "CRLF", _ => "none" });
        await _serial.SendAsync(bytes);
    }

    private void Receive(DesktopSerialEvent item)
    {
        if (Volatile.Read(ref _stopping)) return;
        if (item.Kind == "data") Interlocked.Add(ref _receivedBytes, item.Bytes?.Length ?? 0);
        else if (item.Kind == "sent") Interlocked.Add(ref _sentBytes, item.Bytes?.Length ?? 0);
        _incoming.Enqueue((DateTime.Now, item));
        while (_incoming.Count > 256 && _incoming.TryDequeue(out var dropped))
        {
            if (dropped.Event.Kind is "data" or "sent") Interlocked.Increment(ref _droppedChunks);
            else { _incoming.Enqueue(dropped); break; }
        }
    }

    private void FlushIncoming()
    {
        if (_stopping) return;
        var appended = new StringBuilder();
        var dropped = Interlocked.Exchange(ref _droppedChunks, 0);
        if (dropped > 0) Retain($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] " + T($"显示队列已丢弃 {dropped} 个较早数据块；字节计数仍完整。", $"Display queue dropped {dropped} older chunks; byte counts remain complete.") + Environment.NewLine, appended);
        for (var count = 0; count < 256 && _incoming.TryDequeue(out var pending); count++)
        {
            var item = pending.Event;
            if (item.Kind is "data" or "sent")
            {
                var formatted = DesktopSerialSession.FormatBytes(item.Bytes ?? [], _mode.Text);
                Retain($"[{pending.Time:yyyy-MM-dd HH:mm:ss.fff}] {(item.Kind == "data" ? "RX" : "TX")} {formatted}" + Environment.NewLine, appended);
            }
            else if (item.Kind == "opened") { _connected = true; _activePort = item.Port; _activeBaud = item.Baud; }
            else if (item.Kind == "closed") _connected = false;
            else if (item.Kind == "error") _status.Text = T("串口提示：", "Serial notice: ") + item.Error;
        }
        if (appended.Length > 0)
        {
            var bottom = LogScroll.IsAtBottom(_log);
            var position = LogScroll.GetPosition(_log); var selection = _log.SelectionStart; var selected = _log.SelectionLength;
            _log.AppendText(appended.ToString());
            var removed = Math.Max(0, _log.TextLength - _logCharacters);
            if (removed > 0) { _log.Select(0, removed); _log.SelectedText = ""; }
            if (bottom) { _log.Select(_log.TextLength, 0); _log.ScrollToCaret(); }
            else { _log.Select(Math.Clamp(selection - removed, 0, _log.TextLength), Math.Min(selected, Math.Max(0, _log.TextLength - Math.Max(0, selection - removed)))); LogScroll.SetPosition(_log, position); }
        }
        UpdateState();
    }

    private void Retain(string line, StringBuilder appended)
    {
        _retainedLog.Enqueue(line); _logCharacters += line.Length; appended.Append(line);
        while (_retainedLog.Count > 1500 || _logCharacters > MaximumLogCharacters)
            _logCharacters -= _retainedLog.Dequeue().Length;
    }

    private void UpdateState()
    {
        if (_stopping) return;
        _connect.Enabled = !_busy && !_connected; _disconnect.Enabled = !_busy && _connected;
        _refresh.Enabled = !_busy; _send.Enabled = !_busy && _connected; _export.Enabled = !_busy; _report.Enabled = !_busy;
        _ports.Enabled = !_busy && !_connected; _baud.Enabled = !_busy && !_connected;
        _connection.Text = _connected ? T($"已连接 · {_activePort} · {_activeBaud} baud · 8N1", $"Connected · {_activePort} · {_activeBaud} baud · 8N1") : T("串口未连接", "Serial port disconnected");
        _connection.ForeColor = _connected ? Color.FromArgb(38, 132, 95) : Color.FromArgb(104, 117, 137);
        _counters.Text = T($"接收 {Interlocked.Read(ref _receivedBytes):N0} B    发送 {Interlocked.Read(ref _sentBytes):N0} B", $"Received {Interlocked.Read(ref _receivedBytes):N0} B    Sent {Interlocked.Read(ref _sentBytes):N0} B");
        _connection.MaximumSize = _counters.MaximumSize = _status.MaximumSize = new(Math.Max(180, ClientSize.Width - Padding.Horizontal - 90), 0);
    }

    private async Task ExportLogAsync()
    {
        FlushIncoming();
        using var chooser = new SaveFileDialog { Title = T("导出串口日志", "Export Serial Log"), Filter = T("文本日志|*.txt|所有文件|*.*", "Text log|*.txt|All files|*.*"), FileName = "stm32-serial-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt", OverwritePrompt = true };
        if (chooser.ShowDialog(this) != DialogResult.OK) return;
        var text = string.Concat(_retainedLog); await File.WriteAllTextAsync(chooser.FileName, text, new UTF8Encoding(false));
    }

    private void PreviewReport()
    {
        if (_stopping || _busy) return;
        try
        {
            var diagnostics = _getDiagnosticText() ?? "";
            var raw = "# STM32 " + T("故障报告", "Fault Report") + Environment.NewLine + DateTimeOffset.Now.ToString("O") + Environment.NewLine + Environment.NewLine +
                diagnostics + Environment.NewLine + Environment.NewLine + "## " + T("串口摘要", "Serial Summary") + Environment.NewLine + _connection.Text + Environment.NewLine + _counters.Text;
            var report = RedactDiagnosticText(raw, _root);
            using var preview = new Form
            {
                Text = T("故障报告预览", "Fault Report Preview"), StartPosition = FormStartPosition.CenterParent,
                AutoScaleMode = AutoScaleMode.Dpi, Font = Font, ClientSize = new(920, 650), MinimumSize = new(680, 440),
                BackColor = Color.White, MinimizeBox = false, Icon = FindForm()?.Icon
            };
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new(18), ColumnCount = 1, RowCount = 3 };
            layout.ColumnStyles.Add(new(SizeType.Percent, 100)); layout.RowStyles.Add(new(SizeType.AutoSize)); layout.RowStyles.Add(new(SizeType.Percent, 100)); layout.RowStyles.Add(new(SizeType.AutoSize));
            var hint = Wrapped(T("已隐藏常见隐私信息。请检查并编辑报告，确认后点击“另存为”。", "Common private information has been hidden. Review and edit the report, then select Save As."), layout); layout.Controls.Add(hint, 0, 0);
            var editor = new RichTextBox { Dock = DockStyle.Fill, Text = report, DetectUrls = false, WordWrap = false, Font = new("Consolas", 9.5f), BackColor = Color.FromArgb(248, 250, 253), BorderStyle = BorderStyle.None, ScrollBars = RichTextBoxScrollBars.Both };
            layout.Controls.Add(editor, 0, 1);
            var actions = Flow(); actions.FlowDirection = FlowDirection.RightToLeft; actions.Margin = new(0, 10, 0, 0);
            var save = MakeButton(T("另存为…", "Save As…"), true); var close = MakeButton(T("关闭预览", "Close preview")); close.DialogResult = DialogResult.Cancel; preview.CancelButton = close;
            actions.Controls.AddRange([close, save]); layout.Controls.Add(actions, 0, 2); preview.Controls.Add(layout);
            save.Click += async (_, _) =>
            {
                using var chooser = new SaveFileDialog { Title = T("保存故障报告", "Save Fault Report"), Filter = T("Markdown 报告|*.md|文本文件|*.txt", "Markdown report|*.md|Text file|*.txt"), FileName = "stm32-report-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".md", OverwritePrompt = true };
                if (chooser.ShowDialog(preview) != DialogResult.OK) return;
                save.Enabled = false; save.IsBusy = true;
                try { await File.WriteAllTextAsync(chooser.FileName, editor.Text, new UTF8Encoding(false)); hint.Text = T("报告已保存：", "Report saved: ") + chooser.FileName; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { MessageBox.Show(preview, ex.Message, T("报告保存未完成", "Unable to Save Report"), MessageBoxButtons.OK, MessageBoxIcon.Information); }
                finally { if (!preview.IsDisposed) { save.Enabled = true; save.IsBusy = false; } }
            };
            preview.ShowDialog(this);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        { _status.Text = T("报告生成未完成：", "Unable to generate report: ") + ex.Message; }
    }

    internal static string RedactDiagnosticText(string text, string root)
    {
        if (text.Length > 2 * 1024 * 1024) text = text[..(2 * 1024 * 1024)] + "\n[report truncated]";
        if (!string.IsNullOrWhiteSpace(root))
        {
            foreach (var variant in new[] { root, root.Replace('\\', '/'), root.Replace("\\", "\\\\") }.Distinct(StringComparer.OrdinalIgnoreCase))
                text = text.Replace(variant, "<workspace>", StringComparison.OrdinalIgnoreCase);
        }
        string Replace(string input, string expression, string replacement) => Regex.Replace(input, expression, replacement,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(150));
        text = Replace(text, @"\b[A-Za-z]:[\\/][^\r\n""<>|]+", "<path>");
        text = Replace(text, @"\\\\[^\\\s]+\\[^\r\n""<>|]+", "<path>");
        text = Replace(text, @"(?<![A-Za-z0-9])/(?:home|Users|tmp|var|mnt|Volumes|opt|workspace)/[^\r\n""<>|]+", "<path>");
        text = Replace(text, @"(authorization\s*[:=]\s*)(?:Bearer\s+)?[^\r\n]+", "$1<redacted>");
        text = Replace(text, "([\"']?(?:api[_-]?key|access[_-]?token|refresh[_-]?token|token|password|passwd|secret|credential)[\"']?\\s*[:=]\\s*)(?:\"[^\"]*\"|'[^']*'|[^\\s,;&]+)", "$1<redacted>");
        text = Replace(text, @"\beyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]+\b", "<token>");
        text = Replace(text, @"\bgh[pousr]_[A-Za-z0-9_]{16,}\b", "<token>");
        text = Replace(text, @"[A-Za-z0-9.!#$%&'*+/=?^_`{|}~-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}", "<email>");
        text = Replace(text, @"(?<![\w.])(?:\d{1,3}\.){3}\d{1,3}(?::\d{1,5})?(?![\w.])", "<ip>");
        text = Replace(text, @"(?<![\w:])(?:[0-9a-f]{0,4}:){2,}[0-9a-f:]{0,4}(?:%[\w.-]+)?(?![\w:])", "<ip>");
        return text;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_stopping)
        {
            Volatile.Write(ref _stopping, true); _receiveTimer.Stop(); _receiveTimer.Dispose(); _serial.Dispose();
            while (_incoming.TryDequeue(out _)) { }
        }
        base.Dispose(disposing);
    }

    private static class LogScroll
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct ScrollInfo { public uint Size, Mask; public int Min, Max; public uint Page; public int Position, TrackPosition; }
        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, ref Point point);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetScrollInfo(IntPtr window, int bar, ref ScrollInfo info);
        public static bool IsAtBottom(RichTextBox log)
        {
            if (!log.IsHandleCreated || log.TextLength == 0) return true;
            var info = new ScrollInfo { Size = (uint)Marshal.SizeOf<ScrollInfo>(), Mask = 0x17 };
            return !GetScrollInfo(log.Handle, 1, ref info) || info.Position >= info.Max - (int)info.Page - 2;
        }
        public static Point GetPosition(RichTextBox log) { var point = new Point(); if (log.IsHandleCreated) SendMessage(log.Handle, 0x04DD, IntPtr.Zero, ref point); return point; }
        public static void SetPosition(RichTextBox log, Point point) { if (log.IsHandleCreated) SendMessage(log.Handle, 0x04DE, IntPtr.Zero, ref point); }
    }
}
