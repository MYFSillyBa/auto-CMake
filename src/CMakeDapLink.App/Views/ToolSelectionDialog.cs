using CMakeDapLink.Core;

namespace CMakeDapLink.App;

public sealed class ToolSelectionDialog : Form
{
    private readonly DataGridView _candidates = new()
    {
        Name = "ToolCandidates", Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false,
        AllowUserToDeleteRows = false, RowHeadersVisible = false, MultiSelect = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect, AutoGenerateColumns = false,
        BackgroundColor = Color.White, BorderStyle = BorderStyle.None
    };
    private readonly string _key;
    public string? SelectedExecutable { get; private set; }

    public ToolSelectionDialog(string key, IReadOnlyList<ToolInstallation> candidates)
    {
        _key = key;
        Name = "ToolSelectionDialog"; Text = "选择 " + ToolVersionManager.DisplayName(key);
        StartPosition = FormStartPosition.CenterParent; AutoScaleMode = AutoScaleMode.Dpi;
        Font = new("Microsoft YaHei UI", 9); Icon = AppIcon.Chip;
        ClientSize = new(960, 460); MinimumSize = new(650, 360); MinimizeBox = false; MaximizeBox = false;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new(18), ColumnCount = 1, RowCount = 3 };
        layout.RowStyles.Add(new(SizeType.AutoSize)); layout.RowStyles.Add(new(SizeType.Percent, 100)); layout.RowStyles.Add(new(SizeType.AutoSize));
        Controls.Add(layout);
        layout.Controls.Add(new Label { Text = "选择要使用的工具。版本和完整路径如下；也可以浏览其他位置。", AutoSize = true, Padding = new(0, 0, 0, 12) }, 0, 0);
        _candidates.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "状态", Width = 88 });
        _candidates.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "版本", Width = 140 });
        _candidates.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "完整路径", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, MinimumWidth = 320 });
        foreach (var candidate in candidates.Where(x => x.Key == key))
        {
            var row = _candidates.Rows[_candidates.Rows.Add(candidate.IsActive ? "当前使用" : candidate.IsManaged ? "已安装" : "检测到", candidate.Version, candidate.Executable)];
            row.Tag = candidate;
        }
        layout.Controls.Add(_candidates, 0, 1);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new(0, 12, 0, 0) };
        var cancel = new Button { Name = "CancelToolSelection", Text = "取消", AutoSize = true, DialogResult = DialogResult.Cancel };
        var choose = new Button { Name = "ConfirmToolSelection", Text = "使用所选版本", AutoSize = true };
        var browse = new Button { Name = "BrowseToolExecutable", Text = "浏览其他路径…", AutoSize = true };
        choose.Click += (_, _) => ConfirmSelection();
        browse.Click += (_, _) => BrowseExecutable();
        _candidates.CellDoubleClick += (_, args) => { if (args.RowIndex >= 0) ConfirmSelection(); };
        choose.Enabled = _candidates.Rows.Count > 0;
        actions.Controls.AddRange([cancel, choose, browse]); layout.Controls.Add(actions, 0, 2);
        CancelButton = cancel; AcceptButton = choose;
        Shown += (_, _) =>
        {
            var active = _candidates.Rows.Cast<DataGridViewRow>().FirstOrDefault(x => x.Tag is ToolInstallation { IsActive: true });
            if (active != null) _candidates.CurrentCell = active.Cells[0];
        };
    }

    private void ConfirmSelection()
    {
        if (_candidates.CurrentRow?.Tag is not ToolInstallation candidate) return;
        SelectedExecutable = candidate.Executable; DialogResult = DialogResult.OK; Close();
    }
    private void BrowseExecutable()
    {
        var expected = _key switch { "CMake" => "cmake.exe", "Ninja" => "ninja.exe", "Compiler" => "arm-none-eabi-gcc.exe", _ => "openocd.exe" };
        using var chooser = new OpenFileDialog { Title = "选择 " + expected, Filter = expected + "|" + expected + "|可执行文件 (*.exe)|*.exe", FileName = expected, CheckFileExists = true };
        if (chooser.ShowDialog(this) != DialogResult.OK) return;
        SelectedExecutable = chooser.FileName; DialogResult = DialogResult.OK; Close();
    }
}
