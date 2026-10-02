using CMakeDapLink.Core;

namespace CMakeDapLink.App;

public sealed partial class MainForm
{
    private IReadOnlyList<SetupIssue> CurrentIssues() => EnvironmentReport.Create(_project, _tools, _target.Text, _toolFailures);

    private bool ShowBuildIssues()
    {
        var issues = CurrentIssues().Where(x => x.Key is "Project" or "CMake" or "Ninja" or "Compiler" or "Preset").ToArray();
        if (issues.Length == 0) return false;
        FeedbackStep(0, StepState.Attention);
        Notify($"当前有 {issues.Length} 项构建配置需要补全。请查看说明后继续。", true, "查看补全说明", () => ShowEnvironmentHelp(issues), page: 1);
        return true;
    }

    private bool ShowEnvironmentHelp(IReadOnlyList<SetupIssue>? issues = null)
    {
        using var dialog = new CompletionDialog(issues ?? CurrentIssues(), _previewScale);
        if (dialog.ShowDialog(this) != DialogResult.OK) return false;
        ShowWorkspacePage(0);
        if (!_expanded) ToggleDetails();
        _flow.ScrollTo(_details.Top - Px(14));
        return true;
    }

    public sealed class CompletionDialog : Form
    {
        private readonly ScrollPage _page = new() { Name = "CompletionPage", BackColor = Background };
        private readonly Label _title;
        private readonly Label _subtitle;
        private readonly Button _locate = Button("打开配置详情", true);
        private readonly Button _close = Button("关闭", false);
        private readonly List<(RoundedPanel Card, Label Title, Label Instructions)> _items = [];
        private readonly float? _scale;
        private int Sc(float n) => Math.Max(1, (int)Math.Round(n * (_scale ?? DeviceDpi / 96f)));

        public CompletionDialog(IReadOnlyList<SetupIssue> issues, float? previewScale = null)
        {
            _scale = previewScale;
            AutoScaleMode = AutoScaleMode.None; StartPosition = FormStartPosition.CenterParent;
            Text = "环境补全说明"; BackColor = Background; Font = new Font("Microsoft YaHei UI", 9);
            Icon = AppIcon.Chip;
            MinimizeBox = false; MaximizeBox = false;
            MinimumSize = new Size(Sc(340), Sc(310));
            var area = Screen.PrimaryScreen!.WorkingArea;
            Size = new Size(Math.Min(Sc(660), area.Width - Sc(48)), Math.Min(Sc(580), area.Height - Sc(48)));
            _title = Label(issues.Count == 0 ? "环境检测已通过" : $"还有 {issues.Count} 项需要补全", 15, Ink, bold: true);
            _subtitle = Label(issues.Count == 0 ? "工具和脚本已就绪，可以进行配置与编译验证。" : "按下列步骤补齐后，回到工程页重新检测。", 9, Muted);
            Controls.AddRange([_title, _subtitle, _page, _locate, _close]);
            var entries = issues.Count == 0 ? new[] { new SetupIssue("Ready", "可以进行下一步", "点击“配置并验证”后，程序会实际执行 CMake 配置和编译，检查 OpenOCD 配置，并写入 VS Code 一键编译与一键烧录任务。", false) } : issues;
            foreach (var item in entries)
            {
                var card = new RoundedPanel();
                var title = Label(item.Title, 10, item.BlocksConfiguration ? Color.FromArgb(153, 98, 35) : Ink, bold: true);
                var body = Label(item.Instructions, 9, Ink);
                card.Controls.AddRange([title, body]);
                _page.Canvas.Controls.Add(card); _page.AttachCard(card);
                _items.Add((card, title, body));
            }
            _locate.DialogResult = DialogResult.OK; _close.DialogResult = DialogResult.Cancel;
            AcceptButton = _locate; CancelButton = _close;
            if (previewScale.HasValue)
            {
                var ratio = previewScale.Value / (DeviceDpi / 96f);
                foreach (var control in AllControls(this)) control.Font = new Font(control.Font.FontFamily, control.Font.Size * ratio, control.Font.Style);
            }
            Resize += (_, _) => Arrange();
            _page.Resize += (_, _) => ArrangeItems();
            Arrange();
        }

        private void PlaceLabel(Label label, int left, int top, int width)
            => label.SetBounds(left, top, width, MeasureLabel(label, width));
        private void Arrange()
        {
            var margin = Sc(20); var width = Math.Max(1, ClientSize.Width - margin * 2);
            PlaceLabel(_title, margin, Sc(18), width);
            PlaceLabel(_subtitle, margin, _title.Bottom + Sc(5), width);
            var height = Math.Max(Sc(34), _locate.Font.Height + Sc(16));
            _locate.SetBounds(ClientSize.Width - margin - Sc(142), ClientSize.Height - margin - height, Sc(142), height);
            _close.SetBounds(_locate.Left - Sc(88), _locate.Top, Sc(78), height);
            _page.SetBounds(margin - Sc(4), _subtitle.Bottom + Sc(14), width + Sc(8), Math.Max(1, _locate.Top - _subtitle.Bottom - Sc(28)));
            ArrangeItems();
        }
        private void ArrangeItems()
        {
            var y = Sc(2); var width = Math.Max(1, _page.ViewportWidth - Sc(8)); var inset = Sc(16);
            foreach (var item in _items)
            {
                PlaceLabel(item.Title, inset, Sc(14), Math.Max(1, width - inset * 2));
                PlaceLabel(item.Instructions, inset, item.Title.Bottom + Sc(8), Math.Max(1, width - inset * 2));
                item.Card.SetBounds(Sc(4), y, width, item.Instructions.Bottom + Sc(15));
                y = item.Card.Bottom + Sc(10);
            }
            _page.SetContentHeight(y);
        }
    }
}
