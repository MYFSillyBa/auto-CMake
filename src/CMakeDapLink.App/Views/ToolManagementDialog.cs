using CMakeDapLink.Core;

namespace CMakeDapLink.App;

public sealed class ToolManagementDialog : Form
{
    private ToolVersionManager _manager = new();
    private readonly TargetPicker _tool = new() { Width = 150, Height = 40 };
    private readonly RoundedTextField _root = new() { ReadOnly = true, Dock = DockStyle.Fill, Height = 40 };
    private readonly DataGridView _versions = new()
    {
        Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
        AllowUserToResizeRows = false, MultiSelect = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, RowHeadersVisible = false,
        BackgroundColor = Color.White, BorderStyle = BorderStyle.None, AutoGenerateColumns = false
    };
    private readonly TextBox _log = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = Color.White };
    private readonly Label _latest = new() { Dock = DockStyle.Fill, AutoSize = true, Text = "选择工具后，点击“查找官方更新”查看版本。", Padding = new(0, 8, 0, 8) };
    private readonly Label _status = new() { AutoSize = true, Dock = DockStyle.Fill, Text = "软件验证仅运行工具版本、ARM 编译与 OpenOCD 脚本解析，不检查硬件连接。" };
    private readonly ProgressBar _progress = new() { Dock = DockStyle.Fill, Height = 14 };
    private readonly SoftButton _install = MakeButton("安装所查版本");
    private readonly SoftButton _activate = MakeButton("验证并启用所选版本");
    private readonly SoftButton _close = MakeButton("关闭");
    private readonly List<Control> _actions = [];
    private readonly Dictionary<string, ToolPackage> _updates = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<ToolInstallation> _installations = [];
    private CancellationTokenSource? _operation;

    public ToolPaths Tools { get; private set; }
    public ToolPaths ResultingTools => Tools;

    public ToolManagementDialog(ToolPaths current)
    {
        Tools = current;
        Name = "ToolManagementDialog"; _root.Name = "ToolInstallRoot"; _tool.Name = "ToolKind"; _versions.Name = "ToolVersions";
        Text = "工具管理"; Icon = AppIcon.Chip;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi; Font = new("Microsoft YaHei UI", 9);
        BackColor = Color.FromArgb(245, 248, 253); ForeColor = Color.FromArgb(29, 44, 68);
        ClientSize = new(1120, 760); MinimumSize = new(920, 640);
        MinimizeBox = false;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new(22), ColumnCount = 1, RowCount = 9 };
        layout.ColumnStyles.Add(new(SizeType.Percent, 100));
        layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.RowStyles.Add(new(SizeType.Percent, 65));
        layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.RowStyles.Add(new(SizeType.Percent, 35));
        layout.RowStyles.Add(new(SizeType.AutoSize));
        Controls.Add(layout);
        layout.Controls.Add(new Label { Text = "工具版本与离线安装", Font = new(Font.FontFamily, 18, FontStyle.Bold), AutoSize = true, Padding = new(0, 0, 0, 14) }, 0, 0);
        var rootRow = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, ColumnCount = 3, Margin = new(0, 0, 0, 12) };
        rootRow.ColumnStyles.Add(new(SizeType.AutoSize)); rootRow.ColumnStyles.Add(new(SizeType.Percent, 100)); rootRow.ColumnStyles.Add(new(SizeType.AutoSize));
        rootRow.Controls.Add(new Label { Text = "安装目录", AutoSize = true, Anchor = AnchorStyles.Left, Padding = new(0, 0, 12, 0) }, 0, 0);
        _root.Text = _manager.Root; rootRow.Controls.Add(_root, 1, 0);
        var chooseRoot = MakeButton("选择目录"); rootRow.Controls.Add(chooseRoot, 2, 0); _actions.Add(chooseRoot);
        chooseRoot.Click += (_, _) => ChooseRoot(); layout.Controls.Add(rootRow, 0, 1);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Margin = new(0), WrapContents = true };
        _tool.Items.AddRange(ToolVersionManager.Keys.Select(ToolVersionManager.DisplayName)); _tool.SelectedIndex = 0;
        _tool.Margin = new(0, 8, 12, 8); actions.Controls.Add(_tool); _actions.Add(_tool);
        AddAction(actions, "查找官方更新", () => ExecuteAsync(CheckUpdateAsync));
        actions.Controls.Add(_install); _actions.Add(_install); _install.Click += async (_, _) => await ExecuteAsync(InstallUpdateAsync);
        AddAction(actions, "导入离线 ZIP", () => ImportZipAsync());
        AddAction(actions, "指定外部工具", () => ChooseExternalAsync());
        AddAction(actions, "清理缓存与旧版本", () => CleanupAsync());
        layout.Controls.Add(actions, 0, 2); layout.Controls.Add(_latest, 0, 3);
        _versions.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "工具", FillWeight = 13, MinimumWidth = 82 });
        _versions.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "版本", FillWeight = 16, MinimumWidth = 92 });
        _versions.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "状态", FillWeight = 16, MinimumWidth = 100 });
        _versions.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "占用", FillWeight = 12, MinimumWidth = 85 });
        _versions.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "路径", FillWeight = 60, MinimumWidth = 230 });
        _versions.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
        _versions.DefaultCellStyle.SelectionBackColor = Color.FromArgb(222, 233, 255);
        _versions.DefaultCellStyle.SelectionForeColor = ForeColor;
        _versions.RowTemplate.Height = 32;
        _versions.SelectionChanged += (_, _) => UpdateActions(); layout.Controls.Add(_versions, 0, 4);
        layout.Controls.Add(_status, 0, 5); layout.Controls.Add(_progress, 0, 6); layout.Controls.Add(_log, 0, 7);
        var footer = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Margin = new(0, 12, 0, 0) };
        footer.Controls.Add(_close); footer.Controls.Add(_activate); _actions.Add(_activate);
        _activate.Click += async (_, _) => await ExecuteAsync(ActivateAsync);
        _close.Click += (_, _) => { if (_operation != null) ConfirmStop(); else Close(); };
        layout.Controls.Add(footer, 0, 8);
        _tool.SelectedIndexChanged += (_, _) => { RenderVersions(); ShowLatest(); };
        Shown += async (_, _) => await ExecuteAsync(LoadAsync);
        UpdateActions();
    }

    private string SelectedKey => ToolVersionManager.Keys[Math.Max(0, _tool.SelectedIndex)];
    private ToolInstallation? SelectedInstallation => _versions.CurrentRow?.Tag as ToolInstallation;
    private static SoftButton MakeButton(string text) => new ToolButton
    {
        Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
        BackColor = Color.FromArgb(231, 238, 253), ForeColor = Color.FromArgb(46, 82, 158), Margin = new(4), IconKind = 1
    };
    private sealed class ToolButton : SoftButton
    {
        public override Size GetPreferredSize(Size proposedSize)
        {
            var text = TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.SingleLine);
            var scale = DeviceDpi / 96f;
            return new(text.Width + (int)Math.Ceiling(60 * scale), Math.Max(text.Height + (int)Math.Ceiling(16 * scale), (int)Math.Ceiling(36 * scale)));
        }
    }
    private void AddAction(FlowLayoutPanel panel, string title, Func<Task> action)
    {
        var button = MakeButton(title); panel.Controls.Add(button); _actions.Add(button);
        button.Click += async (_, _) => await action();
    }
    private async Task ExecuteAsync(Func<CancellationToken, Task> action)
    {
        if (_operation != null) return;
        using var cancellation = new CancellationTokenSource(); _operation = cancellation;
        _close.Text = "停止操作"; _progress.Value = 0; UpdateActions();
        try { await action(cancellation.Token); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { Append("操作已停止，既有版本和启用路径已保留。"); }
        catch (Exception ex) { Append("操作未完成：" + ex.Message); MessageBox.Show(this, ex.Message, "工具管理", MessageBoxButtons.OK, MessageBoxIcon.Information); }
        finally { _operation = null; _close.Text = "关闭"; UpdateActions(); }
    }
    private IProgress<ToolRepairProgress> CreateProgress() => new Progress<ToolRepairProgress>(item =>
    {
        if (IsDisposed || Disposing) return;
        _progress.Value = Math.Clamp(item.Percent, 0, 100); _status.Text = item.Stage;
        if (item.Message != null) Append(item.Message);
    });
    private void Append(string text) => _log.AppendText(text + Environment.NewLine);
    private void UpdateActions()
    {
        foreach (var control in _actions) control.Enabled = _operation == null;
        _install.Enabled = _operation == null && _updates.ContainsKey(SelectedKey);
        _activate.Enabled = _operation == null && SelectedInstallation != null;
        _versions.Enabled = _operation == null;
    }
    private async Task LoadAsync(CancellationToken cancellation)
    {
        _installations = await Task.Run(() => _manager.ListAsync(Tools, cancellation), cancellation);
        RenderVersions(); ShowLatest();
    }
    private void RenderVersions(string? selectExecutable = null)
    {
        var oldSelection = selectExecutable ?? SelectedInstallation?.Executable;
        _versions.Rows.Clear();
        foreach (var item in _installations.Where(x => x.Key == SelectedKey))
        {
            var state = item.IsActive ? "正在启用" : "可选版本";
            if (!item.IsManaged) state += " · 外部";
            var index = _versions.Rows.Add(ToolVersionManager.DisplayName(item.Key), item.Version, state, FormatSize(item.SizeBytes), item.Executable);
            var row = _versions.Rows[index]; row.Tag = item; row.Cells[4].ToolTipText = item.Executable + (item.Scripts == null ? "" : "\n脚本：" + item.Scripts);
            if (item.Executable == oldSelection) _versions.CurrentCell = row.Cells[0];
        }
        UpdateActions();
    }
    private void ShowLatest()
    {
        var current = _installations.FirstOrDefault(x => x.Key == SelectedKey && x.IsActive)?.Version ?? "未安装";
        _latest.Text = _updates.TryGetValue(SelectedKey, out var package)
            ? $"当前启用：{current}    官方最新稳定版：{package.Version}    来源：{package.Publisher}\n点击“安装所查版本”开始下载；安装后可选择版本启用，原版本仍可回退。"
            : $"当前启用：{current}    点击“查找官方更新”读取发布方版本信息。外部路径占用只显示执行文件大小。";
        UpdateActions();
    }
    private async Task CheckUpdateAsync(CancellationToken cancellation)
    {
        _status.Text = "查询官方版本…";
        var update = await _manager.CheckUpdateAsync(SelectedKey, Tools, cancellation);
        _updates[SelectedKey] = update.Latest; ShowLatest();
        Append($"{ToolVersionManager.DisplayName(SelectedKey)} 当前 {update.CurrentVersion}；官方最新 {update.Latest.Version}；发布方 {update.Latest.Publisher}。");
        _status.Text = "版本查询完成。点击安装按钮后才下载。";
    }
    private async Task InstallUpdateAsync(CancellationToken cancellation)
    {
        if (!_updates.TryGetValue(SelectedKey, out var package)) return;
        var progress = CreateProgress();
        var installation = await Task.Run(() => _manager.InstallAsync(package, Tools, progress, cancellation), cancellation);
        Append("安装完成：" + installation.Executable + "；验证并启用可切换到该版本。");
        await LoadAsync(cancellation); RenderVersions(installation.Executable);
    }
    private async Task ImportZipAsync()
    {
        using var chooser = new OpenFileDialog { Title = "选择 " + ToolVersionManager.DisplayName(SelectedKey) + " 的完整离线 ZIP", Filter = "ZIP 安装包|*.zip", CheckFileExists = true };
        if (chooser.ShowDialog(this) != DialogResult.OK) return;
        var archive = chooser.FileName;
        await ExecuteAsync(async cancellation =>
        {
            var progress = CreateProgress();
            var installation = await Task.Run(() => _manager.ImportZipAsync(SelectedKey, archive, Tools, progress, cancellation), cancellation);
            Append("离线包版本验证通过：" + installation.Version + "；安装目录：" + installation.InstallDirectory);
            Append("本地 SHA-256 用于标识内容；启用前会再次运行版本验证。ARM 工具齐全时实际编译；OpenOCD 仅解析脚本。");
            await LoadAsync(cancellation); RenderVersions(installation.Executable);
        });
    }
    private async Task ActivateAsync(CancellationToken cancellation)
    {
        var installation = SelectedInstallation;
        if (installation == null) return;
        _status.Text = "验证所选版本…";
        Tools = await Task.Run(() => _manager.ActivateAsync(installation, Tools, cancellation: cancellation), cancellation);
        Append("已验证并启用 " + ToolVersionManager.DisplayName(installation.Key) + "：" + installation.Executable);
        _status.Text = "启用路径已保存，可随时选择另一个已安装版本回退。";
        await LoadAsync(cancellation); ShowLatest();
    }
    private async Task ChooseExternalAsync()
    {
        var key = SelectedKey;
        using var chooser = new OpenFileDialog { Title = "选择并启用 " + ToolVersionManager.DisplayName(key), Filter = "工具执行文件|*.exe", CheckFileExists = true };
        if (chooser.ShowDialog(this) != DialogResult.OK) return;
        var executable = chooser.FileName;
        var installation = new ToolInstallation(key, "待验证", executable,
            key == "OpenOcd" ? EnvironmentScanner.FindScripts(executable) : null, Path.GetDirectoryName(executable)!, new FileInfo(executable).Length, false, false);
        await ExecuteAsync(async cancellation =>
        {
            Tools = await Task.Run(() => _manager.ActivateAsync(installation, Tools, cancellation: cancellation), cancellation);
            Append("外部工具验证通过并启用：" + executable);
            await LoadAsync(cancellation); ShowLatest();
        });
    }
    private void ChooseRoot()
    {
        using var chooser = new FolderBrowserDialog { Description = "选择工具安装目录，已启用的工具路径继续保留。", SelectedPath = _manager.Root, UseDescriptionForTitle = true };
        if (chooser.ShowDialog(this) != DialogResult.OK) return;
        _ = ExecuteAsync(async cancellation =>
        {
            ManagedTools.SetRoot(chooser.SelectedPath); _manager = new(); _root.Text = _manager.Root;
            Append("安装目录已保存：" + _manager.Root); await LoadAsync(cancellation); ShowLatest();
        });
    }
    private async Task CleanupAsync()
    {
        await ExecuteAsync(async cancellation =>
        {
            var entries = await Task.Run(() => _manager.ListCleanup(Tools), cancellation);
            if (entries.Count == 0) { Append("没有可清理的缓存或未启用版本。"); return; }
            using var dialog = new CleanupPicker(entries);
            if (dialog.ShowDialog(this) != DialogResult.OK || dialog.Selected.Count == 0) return;
            var names = string.Join(Environment.NewLine, dialog.Selected.Select(x => x.Name + " · " + FormatSize(x.SizeBytes) + "\n" + x.Path));
            if (MessageBox.Show(this, "将永久删除以下选中项目：\n\n" + names, "确认清理工具文件", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            await Task.Run(() => _manager.Cleanup(dialog.Selected, Tools), cancellation);
            Append("已清理：" + string.Join("、", dialog.Selected.Select(x => x.Name))); await LoadAsync(cancellation);
        });
    }
    private static string FormatSize(long bytes) => bytes >= 1073741824 ? $"{bytes / 1073741824.0:F2} GB" : bytes >= 1048576 ? $"{bytes / 1048576.0:F1} MB" : $"{bytes / 1024.0:F1} KB";
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_operation != null) { e.Cancel = true; ConfirmStop(); }
        base.OnFormClosing(e);
    }
    private void ConfirmStop()
    {
        if (_operation == null || _operation.IsCancellationRequested) return;
        if (MessageBox.Show(this, "当前操作尚未完成。停止后保留已安装的版本与启用路径，未完成的临时文件会清理。确认停止？",
            "停止工具操作", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        _operation.Cancel(); _status.Text = "正在停止操作，请稍候…";
    }

    private sealed class CleanupPicker : Form
    {
        private readonly CheckedListBox _list = new() { Dock = DockStyle.Fill, CheckOnClick = true, HorizontalScrollbar = true, IntegralHeight = false };
        private readonly IReadOnlyList<ToolCleanupEntry> _entries;
        public IReadOnlyList<ToolCleanupEntry> Selected => _list.CheckedIndices.Cast<int>().Select(x => _entries[x]).ToArray();
        public CleanupPicker(IReadOnlyList<ToolCleanupEntry> entries)
        {
            _entries = entries; Text = "选择清理项目"; Icon = AppIcon.Chip; StartPosition = FormStartPosition.CenterParent;
            Font = new("Microsoft YaHei UI", 9); AutoScaleMode = AutoScaleMode.Dpi; ClientSize = new(880, 460); MinimumSize = new(740, 380);
            BackColor = Color.FromArgb(245, 248, 253); MinimizeBox = false;
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new(18), RowCount = 3, ColumnCount = 1 };
            layout.RowStyles.Add(new(SizeType.AutoSize)); layout.RowStyles.Add(new(SizeType.Percent, 100)); layout.RowStyles.Add(new(SizeType.AutoSize));
            layout.Controls.Add(new Label { Text = "勾选要删除的缓存和未启用版本。正在启用的版本不会列出。", AutoSize = true, Padding = new(0, 0, 0, 12) }, 0, 0);
            foreach (var entry in entries) _list.Items.Add($"{entry.Name} · {FormatSize(entry.SizeBytes)} · {entry.Path}");
            layout.Controls.Add(_list, 0, 1);
            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
            var next = MakeButton("确认所选项目"); next.DialogResult = DialogResult.OK;
            var cancel = MakeButton("取消"); cancel.DialogResult = DialogResult.Cancel; actions.Controls.Add(next); actions.Controls.Add(cancel);
            layout.Controls.Add(actions, 0, 2); Controls.Add(layout); CancelButton = cancel;
        }
    }
}
