using System.Drawing.Drawing2D;

namespace CMakeDapLink.App;

internal static class UiShape
{
    public static void Icon(Graphics graphics, RectangleF bounds, int kind, Color color)
    {
        var state = graphics.Save();
        graphics.TranslateTransform(bounds.Left, bounds.Top);
        graphics.ScaleTransform(bounds.Width / 24, bounds.Height / 24);
        using var pen = new Pen(color, 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        if (kind == 3)
        {
            graphics.DrawLines(pen, [new(5, 3), new(14, 3), new(19, 8), new(19, 21), new(5, 21), new(5, 3)]);
            graphics.DrawLines(pen, [new(14, 3), new(14, 8), new(19, 8)]);
            graphics.DrawLine(pen, 8, 14, 16, 14); graphics.DrawLine(pen, 12, 10, 12, 18);
        }
        else if (kind == 2)
        {
            graphics.DrawLines(pen, [new(3, 19), new(3, 6), new(10, 6), new(12, 9), new(21, 9), new(21, 19), new(3, 19)]);
            graphics.DrawLine(pen, 3, 11, 21, 11);
        }
        else
        {
            graphics.DrawRectangle(pen, 6, 6, 12, 12);
            graphics.DrawRectangle(pen, 9, 9, 6, 6);
            foreach (var x in new[] { 8, 12, 16 })
            {
                graphics.DrawLine(pen, x, 3, x, 6); graphics.DrawLine(pen, x, 18, x, 21);
                graphics.DrawLine(pen, 3, x, 6, x); graphics.DrawLine(pen, 18, x, 21, x);
            }
        }
        graphics.Restore(state);
    }
    public static GraphicsPath Round(RectangleF rect, float radius)
    {
        radius = Math.Min(radius, Math.Min(rect.Width, rect.Height) / 2);
        var path = new GraphicsPath();
        var d = Math.Max(1, radius * 2);
        path.AddArc(rect.Left, rect.Top, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Top, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.Left, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}

internal class SoftButton : Button
{
    [System.ComponentModel.DefaultValue(0)]
    public int IconKind { get; set; }
    [System.ComponentModel.DefaultValue(false)]
    public bool IconOnly { get; set; }
    private bool _hover;
    private bool _pressed;
    public SoftButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        UseVisualStyleBackColor = false;
    }
    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; _pressed = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _pressed = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _pressed = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    public override void NotifyDefault(bool value) => base.NotifyDefault(false);
    protected override void OnPaintBackground(PaintEventArgs e)
        => e.Graphics.Clear(Parent?.BackColor ?? Color.White);
    protected override void OnPaint(PaintEventArgs e)
    {
        if (Width < 2 || Height < 2) return;
        // Button's flat-style adapter sets Opaque, which skips OnPaintBackground
        // during WM_PAINT. Fill the entire buffer here, including rounded corners.
        e.Graphics.Clear(Parent?.BackColor ?? Color.White);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var background = BackColor.A == 0 ? Parent?.BackColor ?? Color.White : BackColor;
        var dark = background.GetBrightness() < .55;
        var fill = Enabled ? background : Color.FromArgb(235, 239, 245);
        if (Enabled && (_hover || _pressed || Focused)) fill = Mix(fill, dark ? Color.White : Color.FromArgb(66, 101, 210), _pressed ? .16f : .07f);
        using var shape = UiShape.Round(new RectangleF(1, 1, Width - 2, Height - 2), 6 * DeviceDpi / 96f);
        using var brush = new SolidBrush(fill);
        e.Graphics.FillPath(brush, shape);
        var color = Enabled ? ForeColor : Color.FromArgb(134, 147, 166);
        var textRect = new Rectangle(12 * DeviceDpi / 96 + Padding.Left, 0,
            Math.Max(1, Width - 24 * DeviceDpi / 96 - Padding.Horizontal), Height);
        if (IconKind != 0)
        {
            var size = 18 * DeviceDpi / 96f;
            UiShape.Icon(e.Graphics, new RectangleF(IconOnly ? (Width - size) / 2 : 12 * DeviceDpi / 96f,
                (Height - size) / 2, size, size), IconKind, color);
            textRect.X += 27 * DeviceDpi / 96; textRect.Width = Math.Max(1, textRect.Width - 27 * DeviceDpi / 96);
        }
        if (IconOnly) return;
        var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis;
        flags |= TextAlign == ContentAlignment.MiddleLeft ? TextFormatFlags.Left : TextFormatFlags.HorizontalCenter;
        TextRenderer.DrawText(e.Graphics, Text, Font, textRect, color, flags);
    }
    private static Color Mix(Color color, Color other, float weight) => Color.FromArgb(
        (int)(color.R * (1 - weight) + other.R * weight), (int)(color.G * (1 - weight) + other.G * weight),
        (int)(color.B * (1 - weight) + other.B * weight));
}

internal sealed class TargetPicker : SoftButton
{
    private int _selectedIndex = -1;
    private ToolStripDropDown? _popup;
    public List<string> Items { get; } = [];
    public event EventHandler? SelectedIndexChanged;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            if (value < -1 || value >= Items.Count) throw new ArgumentOutOfRangeException(nameof(value));
            if (value == _selectedIndex) { Invalidate(); return; }
            _selectedIndex = value;
            Text = SelectedItem ?? "选择 CMake 目标";
            Invalidate(); SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
        }
    }
    public string? SelectedItem => _selectedIndex >= 0 && _selectedIndex < Items.Count ? Items[_selectedIndex] : null;
    public TargetPicker()
    {
        Text = "选择 CMake 目标";
        BackColor = Color.FromArgb(242, 245, 249);
        ForeColor = Color.FromArgb(44, 55, 71);
        FlatAppearance.BorderSize = 0;
        TextAlign = ContentAlignment.MiddleLeft;
        Cursor = Cursors.Hand;
        Padding = new Padding(8, 0, 32, 0);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var scale = DeviceDpi / 96f;
        var x = Width - 22 * scale; var y = Height / 2f;
        using var pen = new Pen(Color.FromArgb(91, 109, 147), 1.6f * scale) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        e.Graphics.DrawLines(pen, [new PointF(x - 4 * scale, y - 2 * scale), new PointF(x, y + 2 * scale), new PointF(x + 4 * scale, y - 2 * scale)]);
    }
    protected override void OnClick(EventArgs e) { base.OnClick(e); OpenPopup(); }
    internal void OpenPopup()
    {
        if (Items.Count == 0 || _popup?.Visible == true) return;
        _popup?.Dispose();
        var itemHeight = Math.Max(Font.Height + 16 * DeviceDpi / 96, 34 * DeviceDpi / 96);
        var list = new ListBox { BorderStyle = BorderStyle.None, DrawMode = DrawMode.OwnerDrawFixed,
            IntegralHeight = false, ItemHeight = itemHeight, Font = Font, BackColor = Color.White,
            Width = Math.Max(Width - 10, 100), Height = Math.Min(Items.Count, 7) * itemHeight + 4 };
        list.Items.AddRange(Items.Cast<object>().ToArray());
        list.SelectedIndex = SelectedIndex;
        list.DrawItem += (_, args) =>
        {
            if (args.Index < 0) return;
            var selected = (args.State & DrawItemState.Selected) != 0;
            using var fill = new SolidBrush(selected ? Color.FromArgb(234, 240, 255) : Color.White);
            args.Graphics.FillRectangle(fill, args.Bounds);
            var bounds = Rectangle.Inflate(args.Bounds, -12, 0);
            TextRenderer.DrawText(args.Graphics, Items[args.Index], Font, bounds, selected ? Color.FromArgb(62, 91, 189) : ForeColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
        };
        void Choose()
        {
            if (list.SelectedIndex < 0) return;
            SelectedIndex = list.SelectedIndex; _popup?.Close(); Focus();
        }
        list.MouseClick += (_, _) => Choose();
        list.KeyDown += (_, args) => { if (args.KeyCode == Keys.Enter) { Choose(); args.Handled = true; } };
        var host = new ToolStripControlHost(list) { Margin = Padding.Empty, Padding = Padding.Empty, AutoSize = false, Size = list.Size };
        _popup = new ToolStripDropDown { Padding = new Padding(5), BackColor = Color.White, DropShadowEnabled = true };
        _popup.Items.Add(host);
        _popup.Show(this, new Point(0, Height + 4));
        list.Focus();
    }
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData is Keys.Down or Keys.Up && Items.Count > 0)
        {
            SelectedIndex = Math.Clamp(SelectedIndex + (keyData == Keys.Down ? 1 : -1), 0, Items.Count - 1); return true;
        }
        if (keyData is Keys.Space or Keys.Enter || keyData == (Keys.Alt | Keys.Down)) { OpenPopup(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }
    protected override void Dispose(bool disposing) { if (disposing) _popup?.Dispose(); base.Dispose(disposing); }
}

internal sealed class LineIcon : Control
{
    private readonly int _kind;
    public LineIcon(int kind)
    {
        _kind = kind;
        SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.UserPaint |
            ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        UiShape.Icon(e.Graphics, new RectangleF(2, 2, Width - 4, Height - 4), _kind, Color.FromArgb(37, 99, 235));
    }
}

internal sealed class ToolTile : Panel
{
    private bool _available;
    private readonly Label _name;
    private readonly Label _value = new() { Text = "检测中", ForeColor = Color.FromArgb(46, 59, 77),
        BackColor = Color.Transparent, Font = new Font("Microsoft YaHei UI", 10, FontStyle.Bold) };
    public ToolTile(string name)
    {
        DoubleBuffered = true; BackColor = Color.White; ResizeRedraw = true;
        _name = new Label { Text = name, BackColor = Color.Transparent, ForeColor = Color.FromArgb(120, 130, 146),
            Font = new Font("Microsoft YaHei UI", 8.5f) };
        Controls.Add(_name); Controls.Add(_value);
    }
    public void SetStatus(string value, bool available)
    {
        _value.Text = value; _available = available;
        _value.ForeColor = available ? Color.FromArgb(42, 57, 77) : Color.FromArgb(165, 96, 39);
        Invalidate();
    }
    public int Arrange(int width, int padding)
    {
        Width = width;
        var textWidth = Math.Max(1, width - padding * 2);
        var flags = TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl;
        _name.SetBounds(padding, padding, Math.Max(1, textWidth - padding), TextRenderer.MeasureText(_name.Text, _name.Font,
            new Size(Math.Max(1, textWidth - padding), 10000), flags).Height + 2);
        _value.SetBounds(padding, _name.Bottom + padding / 3, textWidth, TextRenderer.MeasureText(_value.Text, _value.Font,
            new Size(textWidth, 10000), flags).Height + 2);
        Height = _value.Bottom + padding;
        return Height;
    }
    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(Color.White);
        if (Width < 3 || Height < 3) return;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = UiShape.Round(new RectangleF(1, 1, Width - 2, Height - 2), 6 * DeviceDpi / 96f);
        using var fill = new SolidBrush(Color.FromArgb(249, 250, 252));
        using var border = new Pen(Color.FromArgb(234, 238, 243));
        e.Graphics.FillPath(fill, path); e.Graphics.DrawPath(border, path);
        var d = 5 * DeviceDpi / 96f;
        using var dot = new SolidBrush(_available ? Color.FromArgb(40, 153, 112) : Color.FromArgb(180, 189, 204));
        e.Graphics.FillEllipse(dot, Width - 13 * DeviceDpi / 96f, 14 * DeviceDpi / 96f, d, d);
    }
}

internal sealed class ScrollPage : Panel
{
    private readonly ScrollThumb _thumb = new();
    public Panel Canvas { get; } = new() { BackColor = Color.Transparent };
    private int _offset;
    private int _contentHeight;
    public int ViewportWidth => Math.Max(1, ClientSize.Width - ScrollbarWidth);
    private int ScrollbarWidth => Math.Max(14, 16 * DeviceDpi / 96);
    public int Offset => _offset;
    public ScrollPage()
    {
        DoubleBuffered = true; AutoScroll = false;
        Controls.Add(Canvas); Controls.Add(_thumb);
        Canvas.MouseWheel += (_, e) => ScrollWheel(e);
        _thumb.ValueChanged += (_, _) => ScrollTo(_thumb.Value);
    }
    public void SetContentHeight(int height)
    {
        _contentHeight = height;
        ScrollTo(_offset);
    }
    public void ScrollTo(int offset)
    {
        _offset = Math.Clamp(offset, 0, Math.Max(0, _contentHeight - ClientSize.Height));
        Canvas.SetBounds(0, -_offset, ViewportWidth, Math.Max(_contentHeight, ClientSize.Height));
        _thumb.SetBounds(ClientSize.Width - ScrollbarWidth, 8, ScrollbarWidth, Math.Max(1, ClientSize.Height - 16));
        _thumb.SetRange(_offset, _contentHeight, ClientSize.Height);
    }
    public void AttachCard(Control card)
    {
        void Attach(Control control)
        {
            control.Enter += (_, _) =>
            {
                if (!control.Visible) return;
                var top = Canvas.PointToClient(control.PointToScreen(Point.Empty)).Y;
                var margin = 12 * DeviceDpi / 96;
                if (top < _offset + margin) ScrollTo(top - margin);
                else if (top + control.Height > _offset + ClientSize.Height - margin)
                    ScrollTo(top + control.Height - ClientSize.Height + margin);
            };
            if (control is not RichTextBox)
                control.MouseWheel += (_, e) => ScrollWheel(e);
            foreach (Control child in control.Controls) Attach(child);
        }
        Attach(card);
    }
    private void ScrollWheel(MouseEventArgs e)
    {
        if (e is HandledMouseEventArgs handled && handled.Handled) return;
        ScrollTo(_offset - e.Delta * 54 * DeviceDpi / 96 / 120);
        if (e is HandledMouseEventArgs args) args.Handled = true;
    }
    protected override void OnResize(EventArgs e) { base.OnResize(e); ScrollTo(_offset); }
    protected override void OnMouseWheel(MouseEventArgs e)
    {
        ScrollWheel(e); base.OnMouseWheel(e);
    }
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData is Keys.PageDown or Keys.PageUp)
        {
            ScrollTo(_offset + (keyData == Keys.PageDown ? 1 : -1) * ClientSize.Height * 4 / 5); return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private sealed class ScrollThumb : Control
    {
        private int _total, _viewport, _value, _startY, _startValue;
        private bool _dragging, _hover;
        public int Value => _value;
        public event EventHandler? ValueChanged;
        private int Max => Math.Max(0, _total - _viewport);
        private int ThumbHeight => Math.Min(Height, Math.Max(36 * DeviceDpi / 96, (int)((double)Height * _viewport / Math.Max(1, _total))));
        private int ThumbTop => Max == 0 ? 0 : (int)((double)(Height - ThumbHeight) * _value / Max);
        public ScrollThumb() { DoubleBuffered = true; Cursor = Cursors.Hand; TabStop = false; }
        public void SetRange(int value, int total, int viewport)
        {
            _value = value; _total = total; _viewport = viewport; Visible = total > viewport; Invalidate();
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            if (Height < 2) return;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var thickness = (_hover || _dragging ? 7 : 4) * DeviceDpi / 96f;
            using var path = UiShape.Round(new RectangleF((Width - thickness) / 2, ThumbTop, thickness, ThumbHeight), thickness / 2);
            using var fill = new SolidBrush(_dragging ? Color.FromArgb(88, 113, 189) : Color.FromArgb(185, 197, 214));
            e.Graphics.FillPath(fill, path);
        }
        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Y >= ThumbTop && e.Y <= ThumbTop + ThumbHeight)
            { _dragging = true; _startY = e.Y; _startValue = _value; Capture = true; }
            else { _value = Math.Clamp(_value + (e.Y < ThumbTop ? -1 : 1) * _viewport * 4 / 5, 0, Max); ValueChanged?.Invoke(this, EventArgs.Empty); }
            Invalidate();
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!_dragging) return;
            _value = Math.Clamp(_startValue + (int)((double)(e.Y - _startY) * Max / Math.Max(1, Height - ThumbHeight)), 0, Max);
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }
        protected override void OnMouseUp(MouseEventArgs e) { _dragging = false; Capture = false; Invalidate(); base.OnMouseUp(e); }
    }
}
