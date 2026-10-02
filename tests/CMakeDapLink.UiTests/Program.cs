using CMakeDapLink.App;
using CMakeDapLink.Core;
using System.Reflection;

namespace CMakeDapLink.UiTests;

internal static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        if (args.FirstOrDefault() == "--conversion-ui") return ConversionUiFlow.Run(args.Skip(1).ToArray());
        if (args.FirstOrDefault() == "--software-ui") return SoftwareUiFlow.Run(args.Skip(1).ToArray());
        if (args.FirstOrDefault() == "--published-buttons") return PublishedButtons.Run(args.Skip(1).ToArray());
        if (args.FirstOrDefault() == "--live-buttons") return LiveButtons.Run(args.Skip(1).ToArray());
        if (args.FirstOrDefault() == "--normal-flow") return NormalFlow.Run(args.Skip(1).ToArray());
        try
        {
            FileImportUiChecks.Run();
            foreach (var scale in new[] { 1f, 1.25f, 1.5f, 2f })
            {
                using var candidate = new MainForm(scale) { Opacity = 0, ShowInTaskbar = false };
                candidate.Show();
                var setup = Page(candidate, "SetupPage");
                var sources = Page(candidate, "SourcePage");
                var imports = Page(candidate, "ImportPage");
                var toggle = Find<Button>(candidate, "_toggle");
                var details = toggle.Parent!;
                foreach (var width in new[] { 640, 900, 1453, 1900 })
                {
                    candidate.Size = new Size(width, 1032);
                    Application.DoEvents();
                    var collapsed = details.Height;
                    Validate(candidate, setup, scale);
                    if (details.Controls.OfType<TextBox>().Any(x => x.Visible)) throw new Exception("配置详情未默认折叠");
                    toggle.PerformClick(); Application.DoEvents();
                    if (details.Height <= collapsed) throw new Exception("配置展开失败");
                    Validate(candidate, setup, scale);
                    toggle.PerformClick(); Application.DoEvents();
                    if (details.Height != collapsed) throw new Exception("配置收起失败");
                    Find<Button>(candidate, "_navSources").PerformClick(); Application.DoEvents();
                    Validate(candidate, sources, scale);
                    Find<Button>(candidate, "_navImport").PerformClick(); Application.DoEvents();
                    Validate(candidate, imports, scale);
                    Find<Button>(candidate, "_navSetup").PerformClick(); Application.DoEvents();
                }
                setup.GetType().GetMethod("ScrollTo")!.Invoke(setup, [420]);
                candidate.Size = new Size(680, 820); Application.DoEvents();
                Validate(candidate, setup, scale);
                candidate.Size = new Size(1900, 580); Application.DoEvents();
                Validate(candidate, setup, scale);
                Find<Button>(candidate, "_navSources").PerformClick(); Application.DoEvents();
                Validate(candidate, sources, scale);
                Find<Button>(candidate, "_navImport").PerformClick(); Application.DoEvents();
                Validate(candidate, imports, scale);
                Find<Button>(candidate, "_navSetup").PerformClick(); Application.DoEvents();
                setup.GetType().GetMethod("ScrollTo")!.Invoke(setup, [0]);
                setup.GetType().GetMethod("OnMouseWheel", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(setup,
                    [new HandledMouseEventArgs(MouseButtons.None, 0, 10, 100, -120)]);
                if ((int)setup.GetType().GetProperty("Offset")!.GetValue(setup)! <= 0) throw new Exception("鼠标滚轮未滚动页面");
                setup.GetType().GetMethod("ScrollTo")!.Invoke(setup, [0]);
                var thumb = (Control)setup.GetType().GetField("_thumb", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(setup)!;
                var thumbHeight = (int)thumb.GetType().GetProperty("ThumbHeight", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(thumb)!;
                var startY = thumbHeight / 2;
                thumb.GetType().GetMethod("OnMouseDown", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(thumb,
                    [new MouseEventArgs(MouseButtons.Left, 1, thumb.Width / 2, startY, 0)]);
                thumb.GetType().GetMethod("OnMouseMove", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(thumb,
                    [new MouseEventArgs(MouseButtons.Left, 0, thumb.Width / 2, startY + 40, 0)]);
                thumb.GetType().GetMethod("OnMouseUp", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(thumb,
                    [new MouseEventArgs(MouseButtons.Left, 1, thumb.Width / 2, startY + 40, 0)]);
                if ((int)setup.GetType().GetProperty("Offset")!.GetValue(setup)! <= 0) throw new Exception("拖动滚动条未滚动页面");
                candidate.Hide();
                Console.WriteLine($"PASS {scale:P0} 缩放：640 / 900 / 1453 / 1900px，三页布局及折叠、滚动");
                using var completion = new MainForm.CompletionDialog(EnvironmentReport.Create(null, new(null, null, null, null, null), "", new Dictionary<string, string>()), scale)
                    { Opacity = 0, ShowInTaskbar = false };
                completion.Show();
                foreach (var width in new[] { 420, 650 })
                {
                    completion.Size = new Size(width, 580); Application.DoEvents();
                    Validate(completion, Page(completion, "CompletionPage"), scale, false);
                    foreach (var button in completion.Controls.OfType<Button>())
                        if (button.Left < 0 || button.Right > completion.ClientSize.Width || button.Bottom > completion.ClientSize.Height)
                            throw new Exception("补全弹窗操作被裁切");
                }
                completion.Hide();
            }

            var previewScale = args.Length >= 5 && float.TryParse(args[4], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var requestedScale) ? requestedScale : 1f;
            var native = args.Length >= 5 && args[4] == "native";
            using var form = new MainForm(native ? null : previewScale) { Opacity = 0, ShowInTaskbar = false };
            form.Show();
            if (native) previewScale = form.DeviceDpi / 96f;
            Console.WriteLine($"INFO Windows DPI={form.DeviceDpi}, 布局缩放={previewScale:P0}");
            if (args.Length >= 2 && Directory.Exists(args[1]))
            {
                var task = (Task)typeof(MainForm).GetMethod("LoadProjectAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, [args[1]])!;
                var deadline = DateTime.UtcNow.AddSeconds(30);
                while (!task.IsCompleted && DateTime.UtcNow < deadline) { Application.DoEvents(); Thread.Sleep(20); }
                if (!task.IsCompleted) throw new Exception("工程检查超时");
                task.GetAwaiter().GetResult();
                if (Find<TextBox>(form, "_target").Text != "target/stm32h7x.cfg" ||
                    !Descendants(form).OfType<Label>().Any(x => x.Text.Contains("STM32H723VGT6")))
                    throw new Exception("实际工程芯片或 Target 识别错误");
                Find<Label>(form, "_sourceFolder").Text = Path.Combine(args[1], "Core");
                typeof(MainForm).GetMethod("RefreshSourcePreview", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, null);
                if (!Find<Label>(form, "_sourceCount").Text.StartsWith("找到 ") || !Find<Button>(form, "_applySource").Enabled)
                    throw new Exception("实际工程文件夹预览失败");
                Console.WriteLine("PASS 实际工程：STM32H723VGT6、Target、Core 目录 .c/.h 预览");
                var checklist = Find<Control>(form, "_sourcePreview");
                checklist.GetType().GetMethod("SetAll")!.Invoke(checklist, [false]);
                Application.DoEvents();
                if (Find<Button>(form, "_applySource").Enabled) throw new Exception("全不选仍然允许写入 CMake");
                checklist.GetType().GetMethod("SetChecked")!.Invoke(checklist, [0, true]);
                Application.DoEvents();
                if (!Find<Button>(form, "_applySource").Enabled || !Find<Label>(form, "_sourceCount").Text.Contains("已勾选 1"))
                    throw new Exception("逐项勾选没有更新写入清单");
                typeof(MainForm).GetMethod("RefreshSourcePreview", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, null);
                if (!Find<Label>(form, "_sourceCount").Text.Contains("已勾选 1")) throw new Exception("重新预览丢失了当前勾选");
                checklist.GetType().GetMethod("SetAll")!.Invoke(checklist, [true]);
                Console.WriteLine("PASS 文件勾选：全不选禁止写入，逐项选择更新计数");
                var picker = Find<Button>(form, "_sourceTarget");
                Find<Button>(form, "_navSources").PerformClick();
                picker.PerformClick(); Application.DoEvents();
                var popup = (ToolStripDropDown?)picker.GetType().GetField("_popup", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(picker);
                if (popup?.Visible != true || popup.Items.Count == 0) throw new Exception("目标选择下拉菜单未打开");
                var list = (ListBox)((ToolStripControlHost)popup.Items[0]).Control;
                if (list.ItemHeight < list.Font.Height + 6) throw new Exception("下拉选项高度不足");
                typeof(Control).GetMethod("OnMouseClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(list,
                    [new MouseEventArgs(MouseButtons.Left, 1, 20, 15, 0)]);
                if (popup.Visible || picker.Text != "${CMAKE_PROJECT_NAME}") throw new Exception("下拉选项选择失败");
                Find<Button>(form, "_navSetup").PerformClick();
                Console.WriteLine("PASS 自定义下拉菜单可打开，选项高度适配文字");
                Find<Button>(form, "_navImport").PerformClick();
                Find<Control>(form, "_importName").Text = "NewDrivers";
                typeof(MainForm).GetMethod("AddImportFiles", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(form, [new[] { Path.Combine(args[1], "CMakeLists.txt") }]);
                Application.DoEvents();
                if (!Find<Button>(form, "_copyFiles").Enabled || !Find<Label>(form, "_importStatus").Text.Contains("1 个文件"))
                    throw new Exception("加入文件预览失败");
                Find<Button>(form, "_navSetup").PerformClick();
                Console.WriteLine("PASS 加入文件：工程内目标和选定文件预览，未写入实际工程");
            }
            if (args.Length >= 1)
            {
                if (args.Length >= 4 && args[3] == "help")
                {
                    using var help = new MainForm.CompletionDialog(EnvironmentReport.Create(null, new(null, null, null, null, null), "", new Dictionary<string, string>()), native ? null : previewScale)
                        { Opacity = 0, ShowInTaskbar = false };
                    help.Show(); Application.DoEvents();
                    using var preview = new Bitmap(help.Width, help.Height);
                    help.DrawToBitmap(preview, new Rectangle(Point.Empty, preview.Size)); preview.Save(args[0]);
                    help.Hide(); form.Hide(); return 0;
                }
                if (args.Length >= 4 && args[3] == "expanded") Find<Button>(form, "_toggle").PerformClick();
                if (args.Length >= 4 && args[3] == "sources") Find<Button>(form, "_navSources").PerformClick();
                if (args.Length >= 4 && args[3] == "import") Find<Button>(form, "_navImport").PerformClick();
                var width = args.Length >= 3 && int.TryParse(args[2], out var requestedWidth) ? requestedWidth : 1453;
                form.Size = new Size(width, 1032);
                Application.DoEvents();
                var visiblePage = Page(form, args.Length >= 4 && args[3] == "sources" ? "SourcePage" : args.Length >= 4 && args[3] == "import" ? "ImportPage" : "SetupPage");
                Validate(form, visiblePage, previewScale);
                using var bitmap = new Bitmap(form.Width, form.Height);
                form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                bitmap.Save(args[0]);
            }
            form.Hide();
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine("FAIL " + ex); return 1; }
    }

    private static Panel Page(Form form, string name) => Descendants(form).OfType<Panel>().Single(x => x.Name == name);
    private static T Find<T>(Form form, string field) where T : Control =>
        (T)typeof(MainForm).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
    private static void Validate(Form form, Panel page, float scale, bool includeSidebar = true)
    {
        var canvas = page.Controls.OfType<Panel>().Single();
        var cards = canvas.Controls.Cast<Control>().OrderBy(x => x.Top).ToArray();
        foreach (var card in cards)
        {
            if (card.Left < 0 || card.Right > canvas.Width || card.Width > 1101 * scale) throw new Exception($"{scale:P0} 卡片横向越界");
            foreach (var control in Descendants(card).Where(x => x.Visible))
            {
                if (control.Left < 0 || control.Top < 0 || control.Right > control.Parent!.ClientSize.Width || control.Bottom > control.Parent.ClientSize.Height)
                    throw new Exception($"{scale:P0} 子控件被裁切：{control.Text}");
                if (control is Label label && !label.AutoEllipsis)
                {
                    var needed = TextRenderer.MeasureText(label.Text, label.Font, new Size(label.Width, 10000),
                        TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl).Height;
                    if (needed > label.Height + 2) throw new Exception($"{scale:P0} 文字被裁切：{label.Text}");
                }
                if (control is Button button && button.Height < button.Font.Height + 5)
                    throw new Exception($"{scale:P0} 按钮文字高度不足：{button.Text}");
            }
            var labels = card.Controls.OfType<Label>().Where(x => x.Visible).ToArray();
            foreach (var label in labels)
                foreach (var button in card.Controls.OfType<Button>().Where(x => x.Visible))
                    if (label.Bounds.IntersectsWith(button.Bounds)) throw new Exception($"{scale:P0} 文字与按钮重叠：{label.Text} / {button.Text}");
            for (var i = 0; i < labels.Length; i++)
                for (var j = i + 1; j < labels.Length; j++)
                    if (labels[i].Bounds.IntersectsWith(labels[j].Bounds)) throw new Exception($"{scale:P0} 文字重叠：{labels[i].Text} / {labels[j].Text}");
        }
        for (var i = 0; i < cards.Length; i++)
            for (var j = i + 1; j < cards.Length; j++)
                if (cards[i].Bounds.IntersectsWith(cards[j].Bounds)) throw new Exception("滚动后面板重叠");
        if (!includeSidebar) return;
        var sidebar = Find<Panel>(form, "_sidebar");
        if (sidebar.Controls.Cast<Control>().Any(x => x.Visible && (x.Right > sidebar.Width || x.Bottom > sidebar.Height)))
            throw new Exception("侧栏控件越界");
        foreach (var label in sidebar.Controls.OfType<Label>().Where(x => x.Visible))
        {
            if (label.Right > sidebar.Width || label.Bottom > sidebar.Height) throw new Exception("侧栏越界");
            var needed = TextRenderer.MeasureText(label.Text, label.Font, new Size(label.Width, 10000), TextFormatFlags.WordBreak).Height;
            if (needed > label.Height + 2) throw new Exception("侧栏文字被裁切：" + label.Text);
        }
    }
    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
}
