using System.Drawing.Drawing2D;

namespace CMakeDapLink.App;

internal enum StepState { Pending, Running, Complete, Attention, Cancelled }

internal sealed class WorkflowSteps : Control
{
    private string[] _labels = [];
    private StepState[] _states = [];
    private readonly System.Windows.Forms.Timer _animation = new() { Interval = 80 };
    private int _angle;
    private float _scale = 1;
    private int _columns = 5;
    public IReadOnlyList<StepState> States => _states;

    public WorkflowSteps()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        BackColor = Color.White; AccessibleRole = AccessibleRole.StatusBar;
        _animation.Tick += (_, _) => { _angle = (_angle + 24) % 360; Invalidate(); };
    }

    public void Reset(params string[] labels)
    {
        _labels = labels; _states = new StepState[labels.Length]; UpdateAnimation();
    }
    public void SetStep(int index, StepState state)
    {
        if (index < 0 || index >= _states.Length) return;
        _states[index] = state; UpdateAnimation();
    }
    public int Arrange(int width, float scale)
    {
        _scale = scale; _columns = Math.Max(1, Math.Min(_labels.Length, (int)(width / (140 * scale))));
        var rows = (_labels.Length + _columns - 1) / _columns;
        Size = new Size(width, Math.Max(1, rows) * (int)Math.Ceiling(48 * scale));
        return Height;
    }
    private void UpdateAnimation()
    {
        if (Visible && _states.Contains(StepState.Running)) _animation.Start(); else _animation.Stop();
        AccessibleName = string.Join("；", _labels.Select((label, i) => label + "：" + StatusText(_states[i])));
        Invalidate();
    }
    private static string StatusText(StepState state) => state switch
    { StepState.Running => "进行中", StepState.Complete => "已完成", StepState.Attention => "需处理", StepState.Cancelled => "已取消", _ => "待执行" };
    protected override void OnVisibleChanged(EventArgs e) { base.OnVisibleChanged(e); UpdateAnimation(); }
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(BackColor); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var cellWidth = Width / Math.Max(1, _columns); var cellHeight = 48 * _scale;
        for (var i = 0; i < _labels.Length; i++)
        {
            var x = i % _columns * cellWidth; var y = i / _columns * cellHeight;
            var state = _states[i];
            var color = state switch { StepState.Complete => Color.FromArgb(30, 145, 103), StepState.Running => Color.FromArgb(37, 99, 235),
                StepState.Attention => Color.FromArgb(177, 103, 30), _ => Color.FromArgb(135, 147, 164) };
            using var fill = new SolidBrush(state is StepState.Running or StepState.Complete ? Color.FromArgb(235, 243, 253) : Color.FromArgb(244, 246, 249));
            var circle = new RectangleF(x + 1, y + 10 * _scale, 26 * _scale, 26 * _scale);
            e.Graphics.FillEllipse(fill, circle);
            using var pen = new Pen(color, 1.7f * _scale) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            if (state == StepState.Running) e.Graphics.DrawArc(pen, RectangleF.Inflate(circle, -4 * _scale, -4 * _scale), _angle, 250);
            else if (state == StepState.Complete)
                e.Graphics.DrawLines(pen, new PointF[] { new(x + 8 * _scale, y + 23 * _scale), new(x + 12 * _scale, y + 27 * _scale), new(x + 20 * _scale, y + 18 * _scale) });
            else TextRenderer.DrawText(e.Graphics, state == StepState.Attention ? "!" : state == StepState.Cancelled ? "–" : (i + 1).ToString(), Font,
                Rectangle.Round(circle), color, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            var bounds = new Rectangle((int)(x + 35 * _scale), (int)(y + 3 * _scale), Math.Max(1, cellWidth - (int)(40 * _scale)), (int)(22 * _scale));
            TextRenderer.DrawText(e.Graphics, _labels[i], Font, bounds, Color.FromArgb(45, 58, 77), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            bounds.Y += (int)(21 * _scale);
            TextRenderer.DrawText(e.Graphics, StatusText(state), Font, bounds, color, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        }
    }
    protected override void Dispose(bool disposing) { if (disposing) _animation.Dispose(); base.Dispose(disposing); }
}
