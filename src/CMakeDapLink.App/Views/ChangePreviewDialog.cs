using System.Text;
using System.Security.Cryptography;
using CMakeDapLink.Core;

namespace CMakeDapLink.App;

internal sealed class ChangePreviewDialog : Form
{
    public ChangePreviewDialog(string root, string label, IReadOnlyList<FileChange> changes)
        : this(root, label, changes, "确认写入") { }

    internal ChangePreviewDialog(string root, string label, IReadOnlyList<FileChange> changes, string acceptText)
    {
        ChangeDialogStyle.Initialize(this, "差异预览 · " + label, new Size(1120, 760));
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(22), BackColor = BackColor };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 76));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
        var heading = new Label { Text = label + "\n" + (changes.Count == 0 ? "文件内容已一致，无需写入。" : $"将修改 {changes.Count} 个文件。红色为移除，绿色为新增；确认后可从恢复记录撤销。"),
            Dock = DockStyle.Fill, AutoEllipsis = true, Font = Font, ForeColor = ChangeDialogStyle.Ink };
        var diff = new ChangeDiffView(root, changes) { Dock = DockStyle.Fill };
        var footer = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 16, 0, 0), WrapContents = false };
        var accept = ChangeDialogStyle.Button(acceptText, true);
        accept.DialogResult = DialogResult.OK;
        var cancel = ChangeDialogStyle.Button("取消", false);
        cancel.DialogResult = DialogResult.Cancel;
        footer.Controls.Add(accept); footer.Controls.Add(cancel);
        layout.Controls.Add(heading, 0, 0); layout.Controls.Add(diff, 0, 1); layout.Controls.Add(footer, 0, 2);
        Controls.Add(layout); AcceptButton = accept; CancelButton = cancel;
    }
}

internal static class ChangeDialogStyle
{
    internal static readonly Color Ink = Color.FromArgb(42, 55, 76);
    internal static readonly Color Muted = Color.FromArgb(106, 121, 142);
    internal static void Initialize(Form form, string title, Size size)
    {
        form.Text = title; form.Icon = AppIcon.Chip;
        form.Font = new Font("Microsoft YaHei UI", 9);
        form.BackColor = Color.FromArgb(246, 248, 252); form.ForeColor = Ink;
        form.StartPosition = FormStartPosition.CenterParent;
        form.AutoScaleMode = AutoScaleMode.Dpi;
        form.Size = size; form.MinimumSize = new Size(850, 560);
        form.ShowInTaskbar = false;
    }

    internal static SoftButton Button(string text, bool primary) => new()
    {
        Text = text, Width = 184, Height = 38, Margin = new Padding(8, 0, 0, 0), Cursor = Cursors.Hand,
        BackColor = primary ? Color.FromArgb(37, 99, 235) : Color.FromArgb(231, 237, 246),
        ForeColor = primary ? Color.White : Ink
    };
}

internal sealed class ChangeDiffView : UserControl
{
    private readonly string _root;
    private readonly ListBox _files = new() { Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, IntegralHeight = false,
        BackColor = Color.White, ForeColor = ChangeDialogStyle.Ink, DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 54 };
    private readonly RichTextBox _before = Editor();
    private readonly RichTextBox _after = Editor();
    private IReadOnlyList<FileChange> _changes = [];

    public ChangeDiffView(string root, IReadOnlyList<FileChange> changes, string beforeTitle = "修改前", string afterTitle = "修改后")
    {
        _root = root;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = Color.White, Padding = new Padding(10) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 240));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var columns = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Margin = new Padding(12, 0, 0, 0) };
        columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        columns.RowStyles.Add(new RowStyle(SizeType.Absolute, 32)); columns.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        columns.Controls.Add(new Label { Text = beforeTitle, Dock = DockStyle.Fill, ForeColor = ChangeDialogStyle.Muted }, 0, 0);
        columns.Controls.Add(new Label { Text = afterTitle, Dock = DockStyle.Fill, ForeColor = ChangeDialogStyle.Muted }, 1, 0);
        columns.Controls.Add(_before, 0, 1); columns.Controls.Add(_after, 1, 1);
        layout.Controls.Add(_files, 0, 0); layout.Controls.Add(columns, 1, 0); Controls.Add(layout);
        _files.DrawItem += DrawFile;
        _files.SelectedIndexChanged += (_, _) => ShowSelected();
        SetChanges(changes);
    }

    public void SetChanges(IReadOnlyList<FileChange> changes)
    {
        _changes = changes;
        _files.Items.Clear();
        foreach (var change in changes) _files.Items.Add(change);
        if (changes.Count > 0) _files.SelectedIndex = 0;
        else { _before.Text = "没有文件变更。"; _after.Clear(); }
    }

    private void DrawFile(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0) return;
        var selected = (e.State & DrawItemState.Selected) != 0;
        using var fill = new SolidBrush(selected ? Color.FromArgb(234, 240, 255) : Color.White);
        e.Graphics.FillRectangle(fill, e.Bounds);
        var change = _changes[e.Index];
        var path = Path.IsPathRooted(change.Path) ? Path.GetRelativePath(_root, change.Path) : change.Path;
        var kind = change.Before == null ? "新增文件" : change.After == null ? "删除文件" : "修改文件";
        var bounds = new Rectangle(e.Bounds.Left + 10, e.Bounds.Top + 5, e.Bounds.Width - 20, 24);
        TextRenderer.DrawText(e.Graphics, path, Font, bounds, ChangeDialogStyle.Ink, TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
        bounds.Y += 24; bounds.Height = 20;
        TextRenderer.DrawText(e.Graphics, kind, Font, bounds, ChangeDialogStyle.Muted, TextFormatFlags.SingleLine);
    }

    private void ShowSelected()
    {
        if (_files.SelectedIndex < 0) return;
        var change = _changes[_files.SelectedIndex];
        var before = Decode(change.Before); var after = Decode(change.After);
        var rows = Align(before.Split('\n'), after.Split('\n'));
        Render(_before, rows, true); Render(_after, rows, false);
    }

    private static RichTextBox Editor() => new() { Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.None,
        BackColor = Color.White, ForeColor = ChangeDialogStyle.Ink, WordWrap = false, DetectUrls = false,
        Font = new Font("Consolas", 9), ScrollBars = RichTextBoxScrollBars.Both, Margin = new Padding(0, 0, 10, 0) };

    private static string Decode(byte[]? bytes)
    {
        if (bytes == null) return "（文件不存在）";
        using var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, true);
        var text = reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal);
        if (text.Contains('\0')) return $"（二进制文件，共 {bytes.Length:N0} 字节）\nSHA256: {Convert.ToHexString(SHA256.HashData(bytes))}";
        const int limit = 180000;
        var end = Math.Min(text.Length, limit);
        var lineEnd = 0;
        for (var line = 0; line < 3000; line++)
        {
            var next = text.IndexOf('\n', lineEnd);
            if (next < 0 || next >= end) break;
            lineEnd = next + 1;
            if (line == 2999) end = lineEnd;
        }
        return end < text.Length ? text[..end] + "\n（预览已截断；实际操作保留完整文件）" : text;
    }

    private sealed record DiffRow(string? Before, string? After, bool Changed);

    private static List<DiffRow> Align(string[] before, string[] after)
    {
        var rows = new List<DiffRow>();
        if ((long)before.Length * after.Length > 1000000)
        {
            for (var i = 0; i < Math.Max(before.Length, after.Length); i++)
            {
                var left = i < before.Length ? before[i] : null; var right = i < after.Length ? after[i] : null;
                rows.Add(new(left, right, left != right));
            }
            return rows;
        }
        var lengths = new int[before.Length + 1, after.Length + 1];
        for (var i = before.Length - 1; i >= 0; i--)
            for (var j = after.Length - 1; j >= 0; j--)
                lengths[i, j] = before[i] == after[j] ? lengths[i + 1, j + 1] + 1 : Math.Max(lengths[i + 1, j], lengths[i, j + 1]);
        var leftIndex = 0; var rightIndex = 0;
        while (leftIndex < before.Length || rightIndex < after.Length)
        {
            if (leftIndex < before.Length && rightIndex < after.Length && before[leftIndex] == after[rightIndex])
                rows.Add(new(before[leftIndex++], after[rightIndex++], false));
            else if (leftIndex < before.Length && (rightIndex == after.Length || lengths[leftIndex + 1, rightIndex] >= lengths[leftIndex, rightIndex + 1]))
                rows.Add(new(before[leftIndex++], null, true));
            else rows.Add(new(null, after[rightIndex++], true));
        }
        var aligned = new List<DiffRow>();
        for (var index = 0; index < rows.Count;)
        {
            if (!rows[index].Changed) { aligned.Add(rows[index++]); continue; }
            var removed = new List<string>(); var added = new List<string>();
            while (index < rows.Count && rows[index].Changed)
            {
                var row = rows[index++];
                if (row.Before != null) removed.Add(row.Before);
                if (row.After != null) added.Add(row.After);
            }
            for (var offset = 0; offset < Math.Max(removed.Count, added.Count); offset++)
                aligned.Add(new(offset < removed.Count ? removed[offset] : null, offset < added.Count ? added[offset] : null, true));
        }
        return aligned;
    }

    private static void Render(RichTextBox box, IEnumerable<DiffRow> rows, bool left)
    {
        box.Clear();
        var lineNumber = 1;
        foreach (var row in rows)
        {
            var value = left ? row.Before : row.After;
            box.SelectionBackColor = value != null && row.Changed
                ? left ? Color.FromArgb(255, 231, 231) : Color.FromArgb(225, 247, 231) : Color.White;
            box.SelectionColor = value != null && row.Changed ? left ? Color.FromArgb(156, 49, 49) : Color.FromArgb(37, 115, 67) : ChangeDialogStyle.Ink;
            box.AppendText(value == null ? "\n" : $"{lineNumber++,4}  {value}\n");
        }
        box.Select(0, 0); box.ScrollToCaret();
    }
}
