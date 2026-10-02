using System.Drawing.Drawing2D;

namespace CMakeDapLink.App;

internal sealed class FileSelectionList : Control
{
    private sealed record FileRow(string Path, string Label, bool Checked);
    private sealed record DisplayRow(string Path, string Label, int Depth, bool Folder, IReadOnlyList<string> Files);
    private readonly List<FileRow> _files = [];
    private readonly List<DisplayRow> _rows = [];
    private readonly HashSet<string> _collapsed = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _registered = new(StringComparer.OrdinalIgnoreCase);
    private readonly ToolTip _tip = new();
    private string _query = "";
    private int _filter;
    private int _offset, _focused, _hover = -1, _dragY, _dragOffset;
    private bool _dragging;
    private bool _showRegistrationStatus, _notifyingSelection;
    public event EventHandler? SelectionChanged;
    public int Count => _files.Count;
    public int VisibleFileCount => FilteredFiles().Count();
    public int VisibleCheckedCount => FilteredFiles().Count(x => x.Checked);
    public int HiddenCheckedCount => _files.Count(x => x.Checked) - VisibleCheckedCount;
    public int RegisteredFileCount => _files.Count(x => _registered.Contains(x.Path));
    public int SelectedPendingCount => _files.Count(x => x.Checked && !_registered.Contains(x.Path));
    [System.ComponentModel.DefaultValue(false)]
    public bool ShowRegistrationStatus
    {
        get => _showRegistrationStatus;
        set { if (_showRegistrationStatus == value) return; _showRegistrationStatus = value; UpdateAccessibleName(); Invalidate(); }
    }
    public IReadOnlyList<string> CheckedPaths => _files.Where(x => x.Checked).Select(x => x.Path).ToArray();
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string EmptyText { get; set; } = "选择工程内文件夹，在目录树中勾选需要加入构建的文件。";
    [System.ComponentModel.DefaultValue(1f)]
    public float UiScale { get; set; } = 1;
    private int Sc(float n) => Math.Max(1, (int)Math.Round(n * UiScale));
    private int RowHeight => Math.Max(Sc(34), Font.Height + Sc(14));
    private int MaxOffset => Math.Max(0, _rows.Count * RowHeight - Height + Sc(12));
    private Rectangle Thumb
    {
        get
        {
            var track = Math.Max(1, Height - Sc(12));
            var height = Math.Min(track, Math.Max(Sc(28), track * Height / Math.Max(1, _rows.Count * RowHeight)));
            return new(Width - Sc(9), Sc(6) + (MaxOffset == 0 ? 0 : (track - height) * _offset / MaxOffset), Sc(4), height);
        }
    }

    public FileSelectionList()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.Selectable | ControlStyles.ResizeRedraw, true);
        BackColor = Color.FromArgb(249, 250, 252); ForeColor = Color.FromArgb(46, 59, 77);
        TabStop = true; Cursor = Cursors.Hand; AccessibleRole = AccessibleRole.Outline;
        Font = new Font("Microsoft YaHei UI", 9);
    }

    public void SetFiles(IEnumerable<(string Path, string Label)> files, IEnumerable<string>? selected = null, bool preservePosition = false)
    {
        var chosen = selected?.Select(x => x.Replace('\\', '/')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _files.Clear();
        _files.AddRange(files.Select(x => new FileRow(x.Path.Replace('\\', '/'), x.Label, chosen == null || chosen.Contains(x.Path.Replace('\\', '/')))));
        if (!preservePosition) { _offset = 0; _focused = 0; _hover = -1; _collapsed.Clear(); }
        Rebuild();
    }
    public void SetRegisteredPaths(IEnumerable<string> paths) { _registered = paths.Select(x => x.Replace('\\', '/')).ToHashSet(StringComparer.OrdinalIgnoreCase); Rebuild(); }
    public void SetFilter(string query, int mode)
    {
        _query = query.Trim(); _filter = mode; _offset = 0; Rebuild();
    }
    public void ExpandAll() { _collapsed.Clear(); Rebuild(); }
    public void CollapseAll()
    {
        foreach (var file in _files)
        {
            var parts = file.Path.Split('/');
            for (var i = 1; i < parts.Length; i++) _collapsed.Add(string.Join('/', parts.Take(i)));
        }
        Rebuild();
    }
    public void SetAll(bool check)
    {
        var visible = FilteredFiles().Select(x => x.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!_files.Any(x => visible.Contains(x.Path) && x.Checked != check)) return;
        for (var i = 0; i < _files.Count; i++) if (visible.Contains(_files[i].Path)) _files[i] = _files[i] with { Checked = check };
        Rebuild(); NotifySelectionChanged();
    }
    public void SetChecked(int index, bool check)
    {
        if (index < 0 || index >= _files.Count) throw new ArgumentOutOfRangeException(nameof(index));
        if (_files[index].Checked == check) return;
        _files[index] = _files[index] with { Checked = check };
        Rebuild(); NotifySelectionChanged();
    }
    private IEnumerable<FileRow> FilteredFiles() => _files.Where(x =>
        (_query.Length == 0 || x.Path.Contains(_query, StringComparison.OrdinalIgnoreCase)) &&
        (_filter == 0 || _filter == 1 && x.Checked || _filter == 2 && !x.Checked || _filter == 3 && _registered.Contains(x.Path)));
    private void Rebuild()
    {
        _rows.Clear();
        var files = FilteredFiles().OrderBy(x => x.Path, StringComparer.OrdinalIgnoreCase).ToArray();
        AddDirectory("", 0, files);
        _offset = Math.Clamp(_offset, 0, MaxOffset); _focused = Math.Clamp(_focused, 0, Math.Max(0, _rows.Count - 1));
        _hover = -1; _tip.SetToolTip(this, ""); UpdateAccessibleName(); Invalidate();
    }
    private void AddDirectory(string prefix, int depth, IReadOnlyList<FileRow> files)
    {
        var groups = files.Where(x => x.Path[prefix.Length..].Contains('/'))
            .GroupBy(x => x.Path[prefix.Length..].Split('/')[0], StringComparer.OrdinalIgnoreCase);
        foreach (var group in groups)
        {
            var path = prefix + group.Key;
            var descendants = group.ToArray();
            _rows.Add(new(path, group.Key, depth, true, descendants.Select(x => x.Path).ToArray()));
            if (_query.Length > 0 || !_collapsed.Contains(path)) AddDirectory(path + "/", depth + 1, descendants);
        }
        foreach (var file in files.Where(x => !x.Path[prefix.Length..].Contains('/')))
            _rows.Add(new(file.Path, Path.GetFileName(file.Path), depth, false, [file.Path]));
    }
    private void Toggle(int index)
    {
        if (index < 0 || index >= _rows.Count) return;
        var paths = _rows[index].Files.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var check = !_files.Where(x => paths.Contains(x.Path)).All(x => x.Checked);
        for (var i = 0; i < _files.Count; i++) if (paths.Contains(_files[i].Path)) _files[i] = _files[i] with { Checked = check };
        Rebuild(); NotifySelectionChanged();
    }
    private void NotifySelectionChanged()
    {
        if (_notifyingSelection) return;
        _notifyingSelection = true;
        try { SelectionChanged?.Invoke(this, EventArgs.Empty); }
        finally { _notifyingSelection = false; }
    }
    private string RegistrationStatus(string path, bool selected) => _registered.Contains(path) ? "已引用" : selected ? "待加入" : "未选择";
    private void UpdateAccessibleName()
    {
        if (_rows.Count == 0) { AccessibleName = _files.Count == 0 ? EmptyText : "没有符合当前筛选条件的文件。"; return; }
        var row = _rows[_focused];
        var paths = row.Files.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var count = _files.Count(x => x.Checked && paths.Contains(x.Path));
        var selection = count == row.Files.Count ? "已选择" : count == 0 ? "未选择" : "部分选择";
        AccessibleName = $"{row.Path}，{selection}" + (row.Folder ? $"，文件夹，{row.Files.Count} 个文件" : ShowRegistrationStatus ? $"，{RegistrationStatus(row.Path, count > 0)}" : "");
        if (IsHandleCreated) AccessibilityNotifyClients(AccessibleEvents.NameChange, -1);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e); var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
        if (_rows.Count == 0)
        {
            TextRenderer.DrawText(g, _files.Count == 0 ? EmptyText : "没有符合当前筛选条件的文件。", Font,
                Rectangle.Inflate(ClientRectangle, -Sc(16), -Sc(16)), Color.FromArgb(120, 130, 146), TextFormatFlags.WordBreak);
            return;
        }
        var checkedPaths = CheckedPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var index = Math.Max(0, _offset / RowHeight); index < _rows.Count; index++)
        {
            var top = Sc(6) + index * RowHeight - _offset; if (top > Height) break;
            var row = _rows[index]; var indent = Sc(12) + row.Depth * Sc(16);
            if (_hover == index || Focused && _focused == index)
            {
                using var path = UiShape.Round(new Rectangle(Sc(5), top, Width - Sc(18), RowHeight), Sc(5));
                using var fill = new SolidBrush(Color.FromArgb(235, 241, 253)); g.FillPath(fill, path);
            }
            if (row.Folder)
                TextRenderer.DrawText(g, _collapsed.Contains(row.Path) && _query.Length == 0 ? "›" : "⌄", Font,
                    new Rectangle(indent, top, Sc(16), RowHeight), Color.FromArgb(100, 116, 139), TextFormatFlags.VerticalCenter);
            var size = Sc(15); var check = new RectangleF(indent + Sc(18), top + (RowHeight - size) / 2f, size, size);
            var checkedCount = row.Files.Count(checkedPaths.Contains); var all = checkedCount == row.Files.Count;
            using var checkShape = UiShape.Round(check, Sc(3));
            using var fillBox = new SolidBrush(checkedCount > 0 ? Color.FromArgb(37, 99, 235) : Color.White);
            using var pen = new Pen(checkedCount > 0 ? Color.FromArgb(37, 99, 235) : Color.FromArgb(186, 196, 211), UiScale);
            g.FillPath(fillBox, checkShape); g.DrawPath(pen, checkShape);
            if (checkedCount > 0)
            {
                using var mark = new Pen(Color.White, 1.6f * UiScale) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                if (all) g.DrawLines(mark, [new PointF(check.X + size * .23f, check.Y + size * .52f), new PointF(check.X + size * .43f, check.Y + size * .72f), new PointF(check.X + size * .8f, check.Y + size * .29f)]);
                else g.DrawLine(mark, check.X + size * .25f, check.Y + size * .5f, check.X + size * .75f, check.Y + size * .5f);
            }
            var badgeText = row.Folder ? row.Files.Count.ToString() : ShowRegistrationStatus ? RegistrationStatus(row.Path, checkedCount > 0) : Path.GetExtension(row.Path).TrimStart('.').ToUpperInvariant();
            var badgeWidth = Math.Max(Sc(58), TextRenderer.MeasureText(badgeText, Font).Width + Sc(12));
            var badge = new Rectangle(Width - Sc(18) - badgeWidth, top, badgeWidth, RowHeight);
            TextRenderer.DrawText(g, badgeText, Font, badge, _registered.Contains(row.Path) ? Color.FromArgb(31, 139, 100) : Color.FromArgb(120, 130, 146),
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
            TextRenderer.DrawText(g, row.Label, Font, new Rectangle((int)check.Right + Sc(10), top, Math.Max(1, badge.Left - (int)check.Right - Sc(14)), RowHeight), ForeColor,
                TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
        }
        if (MaxOffset > 0)
        {
            using var brush = new SolidBrush(Color.FromArgb(182, 192, 207)); using var path = UiShape.Round(Thumb, Sc(2)); g.FillPath(brush, path);
        }
    }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e); if (e.Button != MouseButtons.Left) return; Focus();
        if (MaxOffset > 0 && e.X >= Width - Sc(14))
        {
            if (Thumb.Contains(e.Location)) { _dragging = true; _dragY = e.Y; _dragOffset = _offset; Capture = true; }
            else { _offset = Math.Clamp(_offset + (e.Y < Thumb.Top ? -Height : Height), 0, MaxOffset); Invalidate(); }
            return;
        }
        if (e.Y < Sc(6)) return;
        var index = (e.Y - Sc(6) + _offset) / RowHeight; if (index < 0 || index >= _rows.Count) return;
        _focused = index; UpdateAccessibleName(); var row = _rows[index];
        if (row.Folder && e.X < Sc(30) + row.Depth * Sc(16))
        {
            if (!_collapsed.Add(row.Path)) _collapsed.Remove(row.Path); Rebuild();
        }
        else Toggle(index);
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging)
        {
            var travel = Math.Max(1, Height - Sc(12) - Thumb.Height);
            _offset = Math.Clamp(_dragOffset + (e.Y - _dragY) * MaxOffset / travel, 0, MaxOffset); Invalidate(); return;
        }
        var index = e.Y < Sc(6) ? -1 : (e.Y - Sc(6) + _offset) / RowHeight;
        if (index >= _rows.Count || e.X >= Width - Sc(14)) index = -1;
        if (index != _hover) { _hover = index; _tip.SetToolTip(this, index >= 0 ? _rows[index].Path : ""); Invalidate(); }
    }
    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); _dragging = false; Capture = false; }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = -1; Invalidate(); }
    protected override void OnMouseWheel(MouseEventArgs e)
    {
        _offset = Math.Clamp(_offset - e.Delta / 120 * RowHeight * 3, 0, MaxOffset);
        if (e is HandledMouseEventArgs handled) handled.Handled = true; Invalidate();
    }
    protected override void OnResize(EventArgs e) { base.OnResize(e); _offset = Math.Clamp(_offset, 0, MaxOffset); }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); UpdateAccessibleName(); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Up or Keys.Down or Keys.Left or Keys.Right or Keys.Space || base.IsInputKey(keyData);
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e); if (_rows.Count == 0) return;
        if (e.Control && e.KeyCode == Keys.A) SetAll(true);
        else if (e.KeyCode == Keys.Space) Toggle(_focused);
        else if (e.KeyCode is Keys.Left or Keys.Right && _rows[_focused].Folder)
        {
            if (e.KeyCode == Keys.Left) _collapsed.Add(_rows[_focused].Path); else _collapsed.Remove(_rows[_focused].Path); Rebuild();
        }
        else if (e.KeyCode is Keys.Up or Keys.Down)
        {
            _focused = Math.Clamp(_focused + (e.KeyCode == Keys.Down ? 1 : -1), 0, _rows.Count - 1);
            _offset = Math.Clamp(_offset, Math.Max(0, (_focused + 1) * RowHeight - Height + Sc(12)), Math.Max(0, _focused * RowHeight)); UpdateAccessibleName(); Invalidate();
        }
        else return;
        e.Handled = true;
    }
    protected override void Dispose(bool disposing) { if (disposing) _tip.Dispose(); base.Dispose(disposing); }
}
