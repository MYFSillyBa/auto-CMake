using System.Diagnostics;
using CMakeDapLink.Core;

namespace CMakeDapLink.App;

internal sealed class BuildProblemsDialog : Form
{
    private readonly DataGridView _grid = new() { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false,
        AllowUserToDeleteRows = false, RowHeadersVisible = false, MultiSelect = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells, BackgroundColor = Color.White, BorderStyle = BorderStyle.None,
        EnableHeadersVisualStyles = false, GridColor = Color.FromArgb(232, 236, 243) };
    private readonly Label _advice = new() { Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(12), ForeColor = Color.FromArgb(87, 105, 128) };
    private readonly IReadOnlyList<BuildProblem> _problems;
    private readonly string _log;

    public BuildProblemsDialog(IReadOnlyList<BuildProblem> problems, string log)
    {
        _problems = problems; _log = log;
        Text = "构建问题与处理建议"; Icon = AppIcon.Chip; StartPosition = FormStartPosition.CenterParent;
        Size = new(1020, 690); MinimumSize = new(780, 520); Font = new("Microsoft YaHei UI", 9);
        BackColor = Color.FromArgb(246, 248, 251); AutoScaleMode = AutoScaleMode.Dpi;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20), ColumnCount = 1, RowCount = 5 };
        layout.RowStyles.Add(new(SizeType.AutoSize)); layout.RowStyles.Add(new(SizeType.Percent, 58));
        layout.RowStyles.Add(new(SizeType.AutoSize)); layout.RowStyles.Add(new(SizeType.Percent, 42)); layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.Controls.Add(new Label { Text = problems.Count == 0 ? "尚未提取到构建错误。可在下方查看、复制或导出完整日志。" : $"共 {problems.Count} 条问题，双击可定位文件；建议需要结合工程实际情况核对。", AutoSize = true, Padding = new Padding(0, 0, 0, 14) }, 0, 0);
        _grid.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
        _grid.DefaultCellStyle.Padding = new Padding(6);
        _grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(234, 241, 255);
        _grid.DefaultCellStyle.SelectionForeColor = Color.FromArgb(34, 43, 57);
        _grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(240, 244, 250);
        _grid.Columns.Add("severity", "类型"); _grid.Columns.Add("stage", "阶段"); _grid.Columns.Add("file", "位置"); _grid.Columns.Add("message", "问题");
        _grid.Columns[0].Width = 62; _grid.Columns[1].Width = 90; _grid.Columns[2].Width = 250;
        _grid.Columns[3].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
        foreach (var problem in problems)
        {
            var index = _grid.Rows.Add(problem.Severity, problem.Stage, problem.FilePath == null ? "—" : problem.FilePath + (problem.Line.HasValue ? ":" + problem.Line : ""), problem.Message);
            _grid.Rows[index].Tag = problem;
        }
        _grid.SelectionChanged += (_, _) => UpdateAdvice();
        _grid.CellDoubleClick += (_, _) => OpenSelected();
        layout.Controls.Add(_grid, 0, 1); layout.Controls.Add(_advice, 0, 2);
        layout.Controls.Add(new RichTextBox { Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.None,
            BackColor = Color.FromArgb(24, 32, 45), ForeColor = Color.FromArgb(214, 222, 235), Font = new("Consolas", 9), Text = log }, 0, 3);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(0, 12, 0, 0) };
        buttons.Controls.Add(Action("打开文件位置", OpenSelected));
        buttons.Controls.Add(Action("复制问题摘要", () => Clipboard.SetText(Summary())));
        buttons.Controls.Add(Action("复制完整日志", () => { if (_log.Length > 0) Clipboard.SetText(_log); }));
        buttons.Controls.Add(Action("导出日志", Export));
        buttons.Controls.Add(Action("关闭", Close));
        layout.Controls.Add(buttons, 0, 4); Controls.Add(layout); UpdateAdvice();
    }
    private static SoftButton Action(string text, System.Action action)
    {
        var button = new SoftButton { Text = text, AutoSize = true, Padding = new Padding(8), Height = 38,
            BackColor = Color.FromArgb(236, 242, 251), ForeColor = Color.FromArgb(37, 79, 139), Margin = new Padding(0, 0, 8, 0) };
        button.Click += (_, _) => action(); return button;
    }
    private BuildProblem? Selected => _grid.CurrentRow?.Tag as BuildProblem;
    private void UpdateAdvice() => _advice.Text = Selected?.Suggestion ?? "编译完成或失败后，程序会从输出中提取文件位置、阶段及可能原因。";
    private string Summary() => _problems.Count == 0 ? "未提取到构建问题。" : string.Join(Environment.NewLine + Environment.NewLine,
        _problems.Select(x => $"[{x.Severity}/{x.Stage}] {x.FilePath}:{x.Line}\n{x.Message}\n{x.Suggestion}"));
    private void Export()
    {
        using var dialog = new SaveFileDialog { Filter = "日志文件 (*.log)|*.log|文本文件 (*.txt)|*.txt", FileName = "build-output.log" };
        if (dialog.ShowDialog(this) == DialogResult.OK) File.WriteAllText(dialog.FileName, Summary() + "\n\n完整日志\n" + _log);
    }
    private void OpenSelected()
    {
        var problem = Selected; if (problem?.FilePath == null || !File.Exists(problem.FilePath))
        { MessageBox.Show(this, "该问题没有可打开的本地文件位置。", "定位文件"); return; }
        try
        {
            var code = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
                .Select(x => Path.Combine(x.Trim('"'), "code.cmd")).FirstOrDefault(File.Exists);
            if (code != null)
            {
                var info = new ProcessStartInfo(code) { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden };
                info.ArgumentList.Add("--goto"); info.ArgumentList.Add(problem.FilePath + ":" + (problem.Line ?? 1) + ":" + (problem.Column ?? 1));
                Process.Start(info);
            }
            else Process.Start(new ProcessStartInfo(problem.FilePath) { UseShellExecute = true });
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "打开文件"); }
    }
}
