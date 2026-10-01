using System.Drawing.Drawing2D;

namespace CMakeDapLink.App;

internal sealed class RoundedTextField : UserControl
{
    private readonly TextBox _input = new() { BorderStyle = BorderStyle.None, BackColor = Color.White };
    [System.Diagnostics.CodeAnalysis.AllowNull]
    public override string Text { get => _input.Text; set => _input.Text = value ?? ""; }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string PlaceholderText { get => _input.PlaceholderText; set => _input.PlaceholderText = value; }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool ReadOnly { get => _input.ReadOnly; set => _input.ReadOnly = value; }
    public int PreferredHeight => _input.PreferredHeight + 18 * DeviceDpi / 96;
    public RoundedTextField()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw, true);
        BackColor = Color.White;
        Controls.Add(_input);
        _input.TextChanged += (_, e) => OnTextChanged(e);
        _input.GotFocus += (_, _) => Invalidate(); _input.LostFocus += (_, _) => Invalidate();
        FontChanged += (_, _) => { _input.Font = Font; Arrange(); };
        Resize += (_, _) => Arrange();
        Enter += (_, _) => _input.Focus();
        Click += (_, _) => _input.Focus();
        Arrange();
    }
    public void SelectAll() => _input.SelectAll();
    public void Clear() => _input.Clear();
    private void Arrange()
    {
        var inset = 12 * DeviceDpi / 96;
        _input.SetBounds(inset, Math.Max(1, (Height - _input.PreferredHeight) / 2), Math.Max(1, Width - inset * 2), _input.PreferredHeight);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor ?? Color.White); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        if (Width < 3 || Height < 3) return;
        using var path = UiShape.Round(new RectangleF(1, 1, Width - 3, Height - 3), 6 * DeviceDpi / 96f);
        using var fill = new SolidBrush(Color.White);
        using var pen = new Pen(_input.Focused ? Color.FromArgb(85, 131, 239) : Color.FromArgb(216, 223, 232), DeviceDpi / 96f);
        e.Graphics.FillPath(fill, path); e.Graphics.DrawPath(pen, path);
    }
}
