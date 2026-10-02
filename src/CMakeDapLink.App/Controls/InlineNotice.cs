namespace CMakeDapLink.App;

internal sealed class InlineNotice : UserControl
{
    private readonly Label _message = new() { AutoSize = false, BackColor = Color.Transparent };
    private readonly SoftButton _action = new() { Visible = false, BackColor = Color.FromArgb(235, 241, 252), ForeColor = Color.FromArgb(37, 99, 235) };
    private readonly SoftButton _close = new() { Text = "×", BackColor = Color.Transparent, ForeColor = Color.FromArgb(110, 125, 144), AccessibleName = "关闭提示" };
    private Action? _callback;
    public event EventHandler? Dismissed;
    public InlineNotice()
    {
        DoubleBuffered = true; Visible = false; Font = new Font("Microsoft YaHei UI", 9);
        Controls.AddRange([_message, _action, _close]);
        _action.Click += (_, _) => _callback?.Invoke();
        _close.Click += (_, _) => { Visible = false; Dismissed?.Invoke(this, EventArgs.Empty); };
    }
    public void ShowMessage(string message, bool attention = false, string? actionText = null, Action? callback = null)
    {
        _message.Text = message; _message.ForeColor = attention ? Color.FromArgb(151, 89, 29) : Color.FromArgb(38, 87, 133);
        BackColor = attention ? Color.FromArgb(255, 247, 233) : Color.FromArgb(235, 244, 255);
        _action.Text = actionText ?? ""; _action.Visible = callback != null; _callback = callback;
        AccessibleName = message; Visible = true;
    }
    public int Arrange(int width, float scale)
    {
        Width = width; var inset = (int)(16 * scale); var closeSize = (int)(44 * scale);
        var actionWidth = _action.Visible ? _action.GetPreferredSize(Size.Empty).Width : 0;
        var inline = width > actionWidth + 340 * scale;
        var messageWidth = Math.Max(1, width - inset * 2 - closeSize - (_action.Visible && inline ? actionWidth + inset : 0));
        var messageHeight = TextRenderer.MeasureText(_message.Text, Font, new Size(messageWidth, 10000), TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl).Height + 4;
        _message.SetBounds(inset, inset, messageWidth, messageHeight);
        _close.SetBounds(width - inset - closeSize, inset - 3, closeSize, closeSize);
        _action.SetBounds(inline ? _close.Left - actionWidth - inset : inset, inline ? inset - 2 : _message.Bottom + inset / 2, actionWidth, Math.Max((int)(34 * scale), Font.Height + 14));
        Height = Math.Max(_message.Bottom, _action.Visible ? _action.Bottom : _close.Bottom) + inset;
        return Height;
    }
    protected override void OnPaintBackground(PaintEventArgs e) => PaintSurface(e.Graphics);
    protected override void OnPaint(PaintEventArgs e)
    {
        PaintSurface(e.Graphics);
        base.OnPaint(e);
    }
    private void PaintSurface(Graphics graphics)
    {
        var surface = Parent;
        while (surface != null && surface.BackColor.A != 255) surface = surface.Parent;
        graphics.Clear(surface?.BackColor ?? Color.White);
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        if (Width < 3 || Height < 3) return;
        using var shape = UiShape.Round(new RectangleF(1, 1, Width - 2, Height - 2), 7 * DeviceDpi / 96f);
        using var brush = new SolidBrush(BackColor); graphics.FillPath(brush, shape);
    }
}
