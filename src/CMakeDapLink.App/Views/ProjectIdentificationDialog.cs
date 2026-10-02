using System.Text.RegularExpressions;
using CMakeDapLink.Core;

namespace CMakeDapLink.App;

internal sealed class ProjectIdentificationDialog : Form
{
    public string? ConfirmedChip { get; private set; }
    public ProjectIdentificationDialog(ProjectInfo project)
    {
        Text = "工程识别依据"; Icon = AppIcon.Chip; Size = new(780, 540); MinimumSize = new(640, 440);
        StartPosition = FormStartPosition.CenterParent; Font = new("Microsoft YaHei UI", 9); BackColor = Color.FromArgb(246, 248, 251);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24), ColumnCount = 1, RowCount = 5 };
        layout.RowStyles.Add(new(SizeType.AutoSize)); layout.RowStyles.Add(new(SizeType.Percent, 100));
        layout.RowStyles.Add(new(SizeType.AutoSize)); layout.RowStyles.Add(new(SizeType.AutoSize)); layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.Controls.Add(new Label { Text = "芯片、构建预设与工具链的识别依据", Font = new(Font.FontFamily, 14, FontStyle.Bold), AutoSize = true, Padding = new Padding(0, 0, 0, 14) }, 0, 0);
        var lines = new List<string> { "工程：" + project.Root, "识别芯片：" + (project.Chip ?? "待确认"), "依据：" + project.ChipEvidence,
            "候选型号：" + string.Join("、", project.ChipCandidates), "构建预设：" + (project.ConfigurePreset ?? "默认 Debug"),
            "构建目录：" + project.BuildDirectory, "工具链：" + (project.ToolchainFile ?? "由工程定义") };
        lines.AddRange(project.ConfigurePresets.Select(x => $"预设 {x.Name}：{x.Description}\n来源：{x.SourceFile}"));
        lines.AddRange(project.Notes);
        layout.Controls.Add(new RichTextBox { Text = string.Join("\n\n", lines), Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = Color.White }, 0, 1);
        layout.Controls.Add(new Label { Text = "确认芯片型号（有冲突时请先核对工程和芯片标识）", AutoSize = true, Padding = new Padding(0, 12, 0, 6) }, 0, 2);
        var chip = new RoundedTextField { Dock = DockStyle.Top, Text = project.Chip ?? "", PlaceholderText = "根据实际芯片填写型号" };
        layout.Controls.Add(chip, 0, 3);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(0, 12, 0, 0) };
        var confirm = new SoftButton { Text = "确认型号", Width = 132, Height = 38, BackColor = Color.FromArgb(37, 99, 235), ForeColor = Color.White };
        confirm.Click += (_, _) =>
        {
            var value = chip.Text.Trim().ToUpperInvariant();
            if (!Regex.IsMatch(value, @"\ASTM32(?:WBA|[A-Z]{1,2})[0-9][A-Z0-9]{2,12}\z"))
            { MessageBox.Show(this, "请填写可核对的 STM32 型号，例如 STM32H723VGT6。", "型号待确认"); return; }
            ConfirmedChip = value; DialogResult = DialogResult.OK; Close();
        };
        buttons.Controls.Add(confirm);
        buttons.Controls.Add(new SoftButton { Text = "关闭", Width = 100, Height = 38, DialogResult = DialogResult.Cancel, BackColor = Color.FromArgb(237, 242, 250) });
        layout.Controls.Add(buttons, 0, 4); Controls.Add(layout);
    }
}

internal sealed class FirmwarePickerDialog : Form
{
    private readonly ListBox _list = new() { Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, ItemHeight = 30, IntegralHeight = false };
    public string? SelectedPath { get; private set; }
    public FirmwarePickerDialog(IReadOnlyList<string> paths, string root)
    {
        Text = "选择烧录固件"; Icon = AppIcon.Chip; Size = new(820, 450); StartPosition = FormStartPosition.CenterParent;
        Font = new("Microsoft YaHei UI", 9); BackColor = Color.FromArgb(246, 248, 251);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(22), ColumnCount = 1, RowCount = 3 };
        layout.RowStyles.Add(new(SizeType.AutoSize)); layout.RowStyles.Add(new(SizeType.Percent, 100)); layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.Controls.Add(new Label { Text = "构建目录包含多个 ELF，请选择需要写入烧录任务的固件。", AutoSize = true, Padding = new Padding(0, 0, 0, 14) }, 0, 0);
        foreach (var path in paths) _list.Items.Add($"{Path.GetRelativePath(root, path)}    {new FileInfo(path).Length:N0} 字节    {File.GetLastWriteTime(path):yyyy-MM-dd HH:mm:ss}");
        _list.SelectedIndex = 0; layout.Controls.Add(_list, 0, 1);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(0, 14, 0, 0) };
        var accept = new SoftButton { Text = "使用所选固件", Width = 160, Height = 38, BackColor = Color.FromArgb(37, 99, 235), ForeColor = Color.White };
        accept.Click += (_, _) => { if (_list.SelectedIndex < 0) return; SelectedPath = paths[_list.SelectedIndex]; DialogResult = DialogResult.OK; Close(); };
        buttons.Controls.Add(accept); buttons.Controls.Add(new SoftButton { Text = "取消", Width = 100, Height = 38, BackColor = Color.FromArgb(237, 242, 250), DialogResult = DialogResult.Cancel });
        layout.Controls.Add(buttons, 0, 2); Controls.Add(layout);
    }
}
