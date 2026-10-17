using System.Text.Json;
using CMakeDapLink.Core;

namespace CMakeDapLink.App;

internal sealed partial class DesktopDevelopmentDialog : Form
{
    private static readonly Color Background = Color.FromArgb(246, 248, 252), Ink = Color.FromArgb(42, 55, 76), Muted = Color.FromArgb(106, 121, 142), Accent = Color.FromArgb(37, 99, 235);
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    private readonly string _root;
    private readonly ToolPaths _tools;
    private readonly SetupOptions? _initial;
    private readonly string _existingLog;
    private readonly ComboBox _preset = Choice(), _buildPreset = Choice(), _configuration = Choice(), _firmware = Choice(), _probe = Choice(), _reset = Choice(), _syncSource = Choice(), _syncTarget = Choice(), _core = Choice();
    private readonly TextBox _buildDirectory = Input(), _compilationDirectory = Input(), _targetScript = Input(), _gdb = Input(), _debugName = Input(), _analysisElf = Input(), _map = Input(), _endpoint = Input(), _registers = Input(true);
    private readonly NumericUpDown _speed = new() { Minimum = 1, Maximum = 50000, Value = 1000, ThousandsSeparator = true, Dock = DockStyle.Fill };
    private readonly NumericUpDown _top = new() { Minimum = 1, Maximum = 100, Value = 30, Dock = DockStyle.Fill };
    private readonly RichTextBox _developmentResult = Output(), _analysisResult = Output(), _syncResult = Output();
    private readonly DataGridView _analysisGrid = Grid();
    private readonly Label _status = Caption("准备就绪。", Muted), _syncNotice = Caption("正在记录 CubeMX 配置基线…", Muted);
    private readonly ProgressBar _progress = new() { Dock = DockStyle.Fill, Style = ProgressBarStyle.Continuous, Height = 5 };
    private readonly SoftButton _cancel = ActionButton("取消操作"), _close = ActionButton("关闭");
    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill, Padding = new Point(20, 10), Name = "DesktopDevelopmentTabs" };
    private readonly List<SoftButton> _actions = [];
    private readonly DesktopDevelopmentSyncState _sync;
    private readonly System.Windows.Forms.Timer _watchTimer = new() { Interval = 1000 };
    private readonly CancellationTokenSource _lifetime = new();
    private FileSystemWatcher? _watcher;
    private CancellationTokenSource? _operation;
    private ProjectInfo? _project;
    private bool _updating, _busy, _syncReady;
    private string _lastDiagnostic = "";
    private string? _debugConfigurationName;

    internal DesktopDevelopmentDialog(string root, ToolPaths tools, SetupOptions? initialOptions = null, string? existingLog = null)
    {
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        _tools = tools; _initial = initialOptions; _existingLog = existingLog ?? ""; _sync = new(_root);
        ChangeDialogStyle.Initialize(this, "开发与诊断 · STM32 工程助手", new Size(1240, 910));
        MinimumSize = new Size(940, 700); Name = "DesktopDevelopmentDialog";
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20, 14, 20, 14), ColumnCount = 1, RowCount = 4, BackColor = Background };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 60)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty };
        header.Controls.Add(new Label { Text = "开发与诊断", Font = new Font(Font.FontFamily, 17, FontStyle.Bold), ForeColor = Ink, Dock = DockStyle.Fill }, 0, 0);
        header.Controls.Add(Caption(_root, Muted), 0, 1);
        layout.Controls.Add(header, 0, 0); layout.Controls.Add(CommonSelection(), 0, 1);
        BuildDevelopmentTab(); BuildAnalysisTab(); BuildSyncTab();
        var deviceTab = new TabPage("串口与报告") { BackColor = Background, Padding = new Padding(8) };
        deviceTab.Controls.Add(new DesktopDevicesPanel(_root, DiagnosticText) { Dock = DockStyle.Fill }); _tabs.TabPages.Add(deviceTab);
        layout.Controls.Add(_tabs, 0, 2);
        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 2, Margin = new Padding(0, 10, 0, 0) };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); footer.RowStyles.Add(new RowStyle(SizeType.Absolute, 4));
        _status.Dock = DockStyle.Fill; _status.TextAlign = ContentAlignment.MiddleLeft;
        _cancel.Enabled = false; _cancel.Click += (_, _) => { _operation?.Cancel(); _status.Text = "正在取消，请等待工具结束…"; };
        _close.Click += (_, _) => Close();
        footer.Controls.Add(_status, 0, 0); footer.Controls.Add(_cancel, 1, 0); footer.Controls.Add(_close, 2, 0); footer.Controls.Add(_progress, 0, 1); footer.SetColumnSpan(_progress, 3);
        layout.Controls.Add(footer, 0, 3); Controls.Add(layout);
        Shown += async (_, _) => await RunAsync("读取工程配置", InitializeAsync);
        FormClosing += (_, e) => { if (_busy) { _operation?.Cancel(); e.Cancel = true; _status.Text = "正在取消当前操作，结束后可以关闭。"; } };
        _preset.SelectedIndexChanged += (_, _) => { if (!_updating) PresetChanged(); };
        _configuration.SelectedIndexChanged += (_, _) => { if (!_updating) RefreshArtifacts(); };
        _buildDirectory.Leave += (_, _) => { if (!_busy && !_updating) RefreshArtifacts(); };
        _watchTimer.Tick += async (_, _) => { _watchTimer.Stop(); if (_busy || !_syncReady) { _watchTimer.Start(); return; } await CheckSyncQuietlyAsync(); };
    }

    private Control CommonSelection()
    {
        var card = Rows();
        _configuration.Items.AddRange(["Debug", "Release", "RelWithDebInfo", "MinSizeRel"]); _configuration.SelectedIndex = 0;
        AddPair(card, "配置预设", _preset, "构建预设", _buildPreset);
        AddPair(card, "配置类型", _configuration, "当前 ELF", WithButton(_firmware, "刷新目标", () => RefreshArtifacts()));
        AddPair(card, "构建目录", WithButton(_buildDirectory, "浏览…", () => PickFolder(_buildDirectory, true)), "操作", Buttons(("编译", () => BuildAsync(false), true), ("烧录…", () => BuildAsync(true), false)));
        card.Margin = new Padding(0, 4, 0, 12); return card;
    }

    private void BuildDevelopmentTab()
    {
        var (top, result) = AddTab("开发配置", _developmentResult);
        var fields = Rows();
        _probe.Items.AddRange([new ChoiceItem("CMSIS-DAP / DAPLink", "cmsis-dap"), new ChoiceItem("ST-Link", "stlink"), new ChoiceItem("J-Link", "jlink")]); _probe.SelectedIndex = 0;
        _reset.Items.AddRange([new ChoiceItem("默认复位", "default"), new ChoiceItem("复位下连接", "connect-under-reset"), new ChoiceItem("SYSRESETREQ", "sysresetreq")]); _reset.SelectedIndex = 0;
        _debugName.Text = DevelopmentConfiguration.DebugName;
        AddPair(fields, "数据库目录", WithButton(_compilationDirectory, "浏览…", () => PickFolder(_compilationDirectory)), "调试名称", _debugName);
        AddPair(fields, "GDB 工具", WithButton(_gdb, "选择…", () => PickFile(_gdb, "GDB 工具|arm-none-eabi-gdb.exe;gdb-multiarch.exe;gdb.exe")), "芯片脚本", _targetScript);
        AddPair(fields, "下载器", _probe, "速度 / kHz", _speed);
        AddPair(fields, "复位方式", _reset, "连接检查", Buttons(("检查探针与芯片…", ProbeAsync, false)));
        AddWide(fields, Hint("clangd 配置保留现有参数与注释。调试配置使用 Cortex-Debug attach；桌面程序生成配置后，在 VS Code 中选择对应调试项。"));
        AddWide(fields, Buttons(("导出编译数据库…", CompileDatabaseAsync, false), ("预览并配置 clangd…", () => PreviewApplyAsync("previewClangd", _developmentResult), true), ("预览调试配置…", ConfigureDebugAsync, true), ("打开 VS Code 工作区", OpenWorkspaceAsync, false)));
        top.Controls.Add(fields); result.Name = "DesktopDevelopmentResult";
        _developmentResult.Text = "先检查预设、当前 ELF、下载器与工具路径，再选择操作。\n所有工程配置写入都会展示差异预览。";
    }

    private void BuildAnalysisTab()
    {
        var nested = new TabControl { Dock = DockStyle.Fill };
        var table = new TabPage("结果表格") { BackColor = Color.White }; table.Controls.Add(_analysisGrid);
        var notes = new TabPage("说明与完整结果") { BackColor = Color.White }; notes.Controls.Add(_analysisResult);
        nested.TabPages.Add(table); nested.TabPages.Add(notes);
        var (top, _) = AddTab("固件与运行诊断", nested);
        var fields = Rows();
        _analysisElf.PlaceholderText = "留空使用上方当前 ELF；也可明确选择工程内其他 ELF";
        _map.PlaceholderText = "可单独分析 MAP；同时选择时请确认来自同次构建";
        _core.Items.AddRange(["自动识别", "Cortex-M0", "Cortex-M0+", "Cortex-M3", "Cortex-M4", "Cortex-M7", "Cortex-M23", "Cortex-M33", "Cortex-M55"]); _core.SelectedIndex = 0;
        _endpoint.Text = "127.0.0.1:3333";
        _registers.PlaceholderText = "粘贴实际异常寄存器，例如 CFSR=0x00020000、HFSR=0x40000000、PC=0x08000101；或使用 JSON 对象。";
        _registers.Height = 92; _registers.ScrollBars = ScrollBars.Vertical;
        AddPair(fields, "分析 ELF", WithButton(_analysisElf, "选择…", () => PickFile(_analysisElf, "ELF 固件|*.elf")), "MAP 文件", WithButton(_map, "选择…", () => PickFile(_map, "GNU MAP|*.map")));
        AddPair(fields, "排行数量", _top, "故障内核", _core);
        AddWide(fields, Buttons(("分析 ELF / MAP", AnalyzeAsync, true), ("查看 Flash / RAM", MemoryAsync, false)));
        AddWide(fields, Caption("HardFault 寄存器（只分析粘贴的数据）", Ink)); AddWide(fields, _registers);
        AddWide(fields, Buttons(("诊断 HardFault", HardFaultAsync, true)));
        AddPair(fields, "本机 GDB", _endpoint, "RTOS 任务", Buttons(("连接并刷新任务…", RtosAsync, false)));
        AddWide(fields, Hint("RTOS 连接已有的本机 GDB 服务。刷新可能暂停目标；任务优先级来自 TCB，无法核实的栈高水位显示为空。"));
        top.Controls.Add(fields);
    }

    private void BuildSyncTab()
    {
        var (top, _) = AddTab("CubeMX 同步", _syncResult);
        var fields = Rows();
        AddWide(fields, Hint("在 CubeMX 重新生成代码后检查变化。读取 .ioc 与 MDK 工程来源，预览 CMake 同步差异，并恢复本工具管理的源文件块。"));
        AddPair(fields, "MDK 来源", _syncSource, "同步目标", _syncTarget);
        AddWide(fields, _syncNotice);
        AddWide(fields, Buttons(("检查来源与变化", InspectSyncAsync, true), ("预览并同步…", SyncAsync, true), ("记录当前基线", AcceptSyncAsync, false)));
        AddWide(fields, Hint("配置监视只提示变化，不自动写入，也不启动 CubeMX。恢复数据保存在本机应用数据目录；每次同步仍需确认文件差异。"));
        top.Controls.Add(fields);
    }

    private (Panel Top, Control Result) AddTab(string title, Control result)
    {
        var page = new TabPage(title) { BackColor = Background, Padding = new Padding(8) };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, Margin = Padding.Empty };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 59)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 41));
        var top = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Color.White, Margin = new Padding(0, 0, 0, 8) };
        result.Dock = DockStyle.Fill; layout.Controls.Add(top, 0, 0); layout.Controls.Add(result, 0, 1); page.Controls.Add(layout); _tabs.TabPages.Add(page);
        return (top, result);
    }

    private static TableLayoutPanel Rows()
    {
        var table = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 4, Padding = new Padding(14, 10, 14, 10), BackColor = Color.White, Margin = Padding.Empty };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 86)); table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 94)); table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); return table;
    }
    private static void AddPair(TableLayoutPanel table, string left, Control value, string right, Control other)
    {
        var row = table.RowCount++; table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var label = Caption(left, Muted); label.TextAlign = ContentAlignment.MiddleLeft; label.MinimumSize = new Size(0, 38);
        var rightLabel = Caption(right, Muted); rightLabel.TextAlign = ContentAlignment.MiddleLeft; rightLabel.Padding = new Padding(12, 0, 0, 0);
        foreach (var control in new[] { value, other }) { control.Dock = DockStyle.Fill; control.Margin = new Padding(0, 5, 0, 5); }
        table.Controls.Add(label, 0, row); table.Controls.Add(value, 1, row); table.Controls.Add(rightLabel, 2, row); table.Controls.Add(other, 3, row);
    }
    private static void AddWide(TableLayoutPanel table, Control value)
    {
        var row = table.RowCount++; table.RowStyles.Add(new RowStyle(SizeType.AutoSize)); value.Dock = DockStyle.Fill; value.Margin = new Padding(0, 6, 0, 4);
        table.Controls.Add(value, 0, row); table.SetColumnSpan(value, 4);
    }
    private FlowLayoutPanel Buttons(params (string Text, Func<Task> Action, bool Primary)[] items)
    {
        var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Margin = Padding.Empty };
        foreach (var item in items)
        {
            var button = ActionButton(item.Text, item.Primary); button.Click += async (_, _) => await RunAsync(item.Text.TrimEnd('…'), item.Action); _actions.Add(button); panel.Controls.Add(button);
        }
        return panel;
    }
    private static Control WithButton(Control input, string text, Action action)
    {
        var row = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true, Margin = Padding.Empty };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); input.Dock = DockStyle.Fill; input.Margin = new Padding(0, 3, 0, 0);
        var button = ActionButton(text); button.AutoSize = true; button.MinimumSize = new Size(62, 30); button.Height = 30; button.Click += (_, _) => action();
        row.Controls.Add(input, 0, 0); row.Controls.Add(button, 1, 0); return row;
    }
    private static SoftButton ActionButton(string text, bool primary = false) => new() { Text = text, AutoSize = true, Height = 34, MinimumSize = new Size(88, 34), Margin = new Padding(0, 0, 7, 5), BackColor = primary ? Accent : Color.FromArgb(232, 238, 248), ForeColor = primary ? Color.White : Ink, Cursor = Cursors.Hand };
    private static ComboBox Choice() => new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, IntegralHeight = false, DropDownHeight = 260, FlatStyle = FlatStyle.Flat };
    private static TextBox Input(bool multiline = false) => new() { Dock = DockStyle.Fill, Multiline = multiline, BorderStyle = BorderStyle.FixedSingle, BackColor = Color.White, ForeColor = Ink, MaxLength = multiline ? 32768 : 2048 };
    private static Label Caption(string text, Color color) => new() { Text = text, ForeColor = color, AutoEllipsis = true, Dock = DockStyle.Fill, MinimumSize = new Size(0, 24) };
    private static Label Hint(string text) => new() { Text = text, ForeColor = Muted, Dock = DockStyle.Fill, AutoSize = true, MaximumSize = new Size(1050, 0), Padding = new Padding(0, 4, 0, 6) };
    private static RichTextBox Output() => new() { Dock = DockStyle.Fill, ReadOnly = true, WordWrap = false, DetectUrls = false, BorderStyle = BorderStyle.None, BackColor = Color.White, ForeColor = Ink, Font = new Font("Consolas", 9.5f), ScrollBars = RichTextBoxScrollBars.Both, Padding = new Padding(12) };
    private static DataGridView Grid() => new() { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, RowHeadersVisible = false, BackgroundColor = Color.White, BorderStyle = BorderStyle.None, SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false, AutoGenerateColumns = false };
    private sealed record ChoiceItem(string Label, string? Value) { public override string ToString() => Label; }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _lifetime.Cancel(); _operation?.Cancel(); _watchTimer.Dispose(); _watcher?.Dispose(); _operation?.Dispose(); _lifetime.Dispose(); }
        base.Dispose(disposing);
    }
}
