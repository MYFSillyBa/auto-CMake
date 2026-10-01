using System.Drawing.Drawing2D;

namespace CMakeDapLink.App;

internal sealed class FileSelectionList : Control
{
    private sealed record Row(string Path, string Label, bool Checked);
    private readonly List<Row> _rows = [];
    private readonly ToolTip _tip = new();
    private int _offset;
    private int _focused;
    private int _hover = -1;
    private bool _dragging;
    private int _dragY;
    private int _dragOffset;
    public event EventHandler? SelectionChanged;
    public int Count => _rows.Count;
    public IReadOnlyList<string> CheckedPaths => _rows.Where(x => x.Checked).Select(x => x.Path).ToArray();
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string EmptyText { get; set; } = "选择文件夹后，在这里勾选需要加入构建的文件。";
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
            var y = Sc(6) + (MaxOffset == 0 ? 0 : (track - height) * _offset / MaxOffset);
            return new Rectangle(Width - Sc(9), y, Sc(4), height);
        }
    }
    public FileSelectionList()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.Selectable | ControlStyles.ResizeRedraw, true);
        BackColor = Color.FromArgb(249, 250, 252); ForeColor = Color.FromArgb(46, 59, 77);
        TabStop = true; Cursor = Cursors.Hand; AccessibleRole = AccessibleRole.List;
        Font = new Font("Microsoft YaHei UI", 9);
    }
    public void SetFiles(IEnumerable<(string Path, string Label)> files, IEnumerable<string>? selected = null, bool preservePosition = false)
    {
        var chosen = selected?.ToHashSet(StringComparer.OrdinalIgnoreCase);
        _rows.Clear();
        _rows.AddRange(files.Select(x => new Row(x.Path, x.Label, chosen == null || chosen.Contains(x.Path))));
        if (!preservePosition) { _offset = 0; _focused = 0; _hover = -1; }
        _offset = Math.Clamp(_offset, 0, MaxOffset); _focused = Math.Clamp(_focused, 0, Math.Max(0, _rows.Count - 1)); Invalidate();
    }
    public void SetAll(bool check)
    {
        for (var i = 0; i < _rows.Count; i++) _rows[i] = _rows[i] with { Checked = check };
        Invalidate(); SelectionChanged?.Invoke(this, EventArgs.Empty);
    }
    public void SetChecked(int index, bool check)
    {
        if (index < 0 || index >= _rows.Count) throw new ArgumentOutOfRangeException(nameof(index));
        _rows[index] = _rows[index] with { Checked = check };
        Invalidate(); SelectionChanged?.Invoke(this, EventArgs.Empty);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
        if (_rows.Count == 0)
        {
            TextRenderer.DrawText(g, EmptyText, Font, Rectangle.Inflate(ClientRectangle, -Sc(16), -Sc(16)),
                Color.FromArgb(120, 130, 146), TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl);
            return;
        }
        for (var index = Math.Max(0, _offset / RowHeight); index < _rows.Count; index++)
        {
            var top = Sc(6) + index * RowHeight - _offset;
            if (top > Height) break;
            var row = _rows[index];
            var bounds = new Rectangle(Sc(5), top, Width - Sc(18), RowHeight);
            if (_hover == index || Focused && _focused == index)
            {
                using var path = UiShape.Round(bounds, Sc(5));
                using var fill = new SolidBrush(Color.FromArgb(235, 241, 253));
                g.FillPath(fill, path);
            }
            var size = Sc(15);
            var check = new RectangleF(Sc(13), top + (RowHeight - size) / 2f, size, size);
            using var checkShape = UiShape.Round(check, Sc(3));
            using var boxFill = new SolidBrush(row.Checked ? Color.FromArgb(37, 99, 235) : Color.White);
            using var border = new Pen(row.Checked ? Color.FromArgb(37, 99, 235) : Color.FromArgb(186, 196, 211), UiScale);
            g.FillPath(boxFill, checkShape); g.DrawPath(border, checkShape);
            if (row.Checked)
            {
                using var pen = new Pen(Color.White, 1.6f * UiScale) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                g.DrawLines(pen, [new PointF(check.X + size * .23f, check.Y + size * .52f), new PointF(check.X + size * .43f, check.Y + size * .72f), new PointF(check.X + size * .8f, check.Y + size * .29f)]);
            }
            var extension = Path.GetExtension(row.Path).TrimStart('.').ToUpperInvariant();
            var badgeWidth = Sc(29);
            var badge = new Rectangle(Width - Sc(18) - badgeWidth, top, badgeWidth, RowHeight);
            TextRenderer.DrawText(g, extension, Font, badge, Color.FromArgb(120, 130, 146),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, row.Label, Font, new Rectangle(Sc(39), top, Math.Max(1, badge.Left - Sc(44)), RowHeight), ForeColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
        }
        if (MaxOffset > 0)
        {
            using var brush = new SolidBrush(Color.FromArgb(182, 192, 207));
            using var path = UiShape.Round(Thumb, Sc(2)); g.FillPath(brush, path);
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
        var index = (e.Y - Sc(6) + _offset) / RowHeight;
        if (index < 0 || index >= _rows.Count) return;
        _focused = index; SetChecked(index, !_rows[index].Checked);
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging)
        {
            var travel = Math.Max(1, Height - Sc(12) - Thumb.Height);
            _offset = Math.Clamp(_dragOffset + (e.Y - _dragY) * MaxOffset / travel, 0, MaxOffset); Invalidate(); return;
        }
        var index = (e.Y - Sc(6) + _offset) / RowHeight;
        index = index < _rows.Count && e.X < Width - Sc(14) ? index : -1;
        if (index != _hover)
        {
            _hover = index; _tip.SetToolTip(this, index >= 0 ? _rows[index].Label + "\n" + _rows[index].Path : ""); Invalidate();
        }
    }
    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); _dragging = false; Capture = false; }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = -1; Invalidate(); }
    protected override void OnMouseWheel(MouseEventArgs e)
    {
        _offset = Math.Clamp(_offset - e.Delta / 120 * RowHeight * 3, 0, MaxOffset);
        if (e is HandledMouseEventArgs handled) handled.Handled = true;
        Invalidate();
    }
    protected override void OnResize(EventArgs e) { base.OnResize(e); _offset = Math.Clamp(_offset, 0, MaxOffset); }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Up or Keys.Down or Keys.Space || base.IsInputKey(keyData);
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e); if (_rows.Count == 0) return;
        if (e.Control && e.KeyCode == Keys.A) { SetAll(true); e.Handled = true; }
        else if (e.KeyCode == Keys.Space) { SetChecked(_focused, !_rows[_focused].Checked); e.Handled = true; }
        else if (e.KeyCode is Keys.Up or Keys.Down)
        {
            _focused = Math.Clamp(_focused + (e.KeyCode == Keys.Down ? 1 : -1), 0, _rows.Count - 1);
            _offset = Math.Clamp(_offset, Math.Max(0, (_focused + 1) * RowHeight - Height + Sc(12)), Math.Max(0, _focused * RowHeight));
            Invalidate(); e.Handled = true;
        }
    }
    protected override void Dispose(bool disposing) { if (disposing) _tip.Dispose(); base.Dispose(disposing); }
}
