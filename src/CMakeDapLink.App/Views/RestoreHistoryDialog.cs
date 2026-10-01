using CMakeDapLink.Core;

namespace CMakeDapLink.App;

internal sealed class RestoreHistoryDialog : Form
{
    private readonly string _root;
    private readonly ListBox _history = new() { Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, IntegralHeight = false,
        DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 72, BackColor = Color.White };
    private readonly Label _description = new() { Dock = DockStyle.Fill, AutoEllipsis = true, ForeColor = ChangeDialogStyle.Muted };
    private readonly ChangeDiffView _diff;
    private readonly SoftButton _restore = ChangeDialogStyle.Button("恢复所选记录", true);
    private IReadOnlyList<ChangeRecord> _records = [];

    public RestoreHistoryDialog(string root)
    {
        _root = root;
        ChangeDialogStyle.Initialize(this, "恢复记录", new Size(1220, 820));
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(22), RowCount = 3, ColumnCount = 1 };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
        var content = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 240)); content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _diff = new ChangeDiffView(root, [], "当前内容", "恢复后") { Dock = DockStyle.Fill, Margin = new Padding(12, 0, 0, 0) };
        content.Controls.Add(_history, 0, 0); content.Controls.Add(_diff, 1, 0);
        var footer = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 16, 0, 0), WrapContents = false };
        var close = ChangeDialogStyle.Button("关闭", false); close.DialogResult = DialogResult.Cancel;
        footer.Controls.Add(_restore); footer.Controls.Add(close);
        layout.Controls.Add(_description, 0, 0); layout.Controls.Add(content, 0, 1); layout.Controls.Add(footer, 0, 2);
        Controls.Add(layout); CancelButton = close;
        _history.DrawItem += DrawRecord; _history.SelectedIndexChanged += (_, _) => ShowRecord();
        _restore.Click += (_, _) => RestoreSelected();
        LoadRecords();
    }

    private void LoadRecords()
    {
        _records = ChangeHistory.List(_root);
        _history.Items.Clear();
        foreach (var record in _records) _history.Items.Add(record);
        if (_records.Count > 0) _history.SelectedIndex = 0;
        else { _description.Text = "这个工程还没有恢复记录。修改文件时会自动保存完整快照。"; _restore.Enabled = false; _diff.SetChanges([]); }
    }

    private void DrawRecord(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0) return;
        var record = _records[e.Index];
        var selected = (e.State & DrawItemState.Selected) != 0;
        using var fill = new SolidBrush(selected ? Color.FromArgb(234, 240, 255) : Color.White);
        e.Graphics.FillRectangle(fill, e.Bounds);
        var bounds = new Rectangle(e.Bounds.Left + 10, e.Bounds.Top + 8, e.Bounds.Width - 20, 27);
        TextRenderer.DrawText(e.Graphics, (record.Status != "Complete" ? "未完成 · " : "") + record.Label,
            Font, bounds, ChangeDialogStyle.Ink, TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
        bounds.Y += 28; bounds.Height = 24;
        TextRenderer.DrawText(e.Graphics, $"{record.CreatedUtc.ToLocalTime():MM-dd HH:mm} · {record.Changes.Count} 文件" + (record.RestoredUtc != null ? " · 已恢复" : ""),
            Font, bounds, ChangeDialogStyle.Muted, TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
    }

    private void ShowRecord()
    {
        if (_history.SelectedIndex < 0) return;
        try
        {
            var record = _records[_history.SelectedIndex];
            var conflicts = ChangeHistory.GetRestoreConflicts(_root, record.Id);
            _diff.SetChanges(ChangeHistory.GetRestoreChanges(_root, record.Id));
            _restore.Enabled = record.RestoredUtc == null;
            _description.Text = record.RestoredUtc != null ? "这条记录已经恢复。恢复操作也有快照，可选对应记录撤销恢复。"
                : record.Status != "Complete" ? "这次操作未完成，快照已保留。请查看当前文件与恢复结果，确认后可以恢复操作前的内容。"
                : conflicts.Count > 0 ? $"{conflicts.Count} 个文件在操作后又被修改。预览展示当前内容与恢复结果；恢复时需要确认覆盖这些编辑。"
                : "选择记录查看恢复差异。新增文件将删除，修改和删除的文件将恢复；快照保存在用户数据目录。";
            _description.ForeColor = conflicts.Count > 0 && record.RestoredUtc == null ? Color.FromArgb(166, 104, 32) : ChangeDialogStyle.Muted;
        }
        catch (Exception ex) { _description.Text = ex.Message; _restore.Enabled = false; }
    }

    private void RestoreSelected()
    {
        if (_history.SelectedIndex < 0) return;
        try
        {
            var record = _records[_history.SelectedIndex];
            var conflicts = ChangeHistory.GetRestoreConflicts(_root, record.Id);
            var changes = ChangeHistory.GetRestoreChanges(_root, record.Id);
            using var preview = new ChangePreviewDialog(_root,
                conflicts.Count > 0 ? "覆盖后续编辑并恢复 · " + record.Label : "恢复 · " + record.Label,
                changes, conflicts.Count > 0 ? "覆盖并恢复" : "确认恢复");
            if (preview.ShowDialog(this) != DialogResult.OK) return;
            ChangeHistory.Restore(_root, record.Id, conflicts.Count > 0, changes);
            LoadRecords();
            _description.Text = "已恢复。恢复操作也已保存，可以从最新记录撤销。";
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "恢复未完成", MessageBoxButtons.OK, MessageBoxIcon.Information); ShowRecord(); }
    }
}
