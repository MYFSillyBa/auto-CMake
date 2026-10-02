using System.Reflection;
using CMakeDapLink.App;
using CMakeDapLink.Core;
using CMakeDapLink.TestFixtures;

namespace CMakeDapLink.UiTests;

internal static class ConversionUiFlow
{
    public static int Run(string[] args)
    {
        var output = args.FirstOrDefault() ?? Path.Combine(Path.GetTempPath(), "cmake-conversion-ui"); Directory.CreateDirectory(output);
        var root = Path.Combine(Path.GetTempPath(), "cmake-conversion-ui-" + Guid.NewGuid().ToString("N"));
        try
        {
            CubeMxFixture.Create(root);
            using var form = new MainForm { Size = new(1453, 1032), ShowInTaskbar = false };
            var exitCode = 1;
            form.Shown += (_, _) => form.BeginInvoke(() =>
            {
                try
                {
                    Until(() => !Field<bool>(form, "_busy"));
                    using (var help = new MainForm.CompletionDialog([new SetupIssue("Note", "保留原配置", "生成后实际编译检查。", false)], conversion: true) { ShowInTaskbar = false })
                    {
                        help.Show(form); Application.DoEvents();
                        Require(help.Controls.OfType<Label>().Any(x => x.Text.Contains("可以生成")) && !help.Controls.OfType<Label>().Any(x => x.Text.Contains("需要补全")), "转换提示与必须补全事项分开显示");
                        Require(help.Controls.OfType<Button>().Count(x => x.Visible) == 1 && help.Controls.OfType<Button>().Single(x => x.Visible).Text == "关闭", "转换说明只显示关闭按钮");
                        Screenshot(help, Path.Combine(output, "conversion-advisory.png")); help.Close();
                    }
                    using (var help = new MainForm.CompletionDialog([]) { ShowInTaskbar = false })
                    {
                        help.Show(form); Application.DoEvents(); var buttons = help.Controls.OfType<Button>().Where(x => x.Visible).ToArray();
                        Require(buttons.Length == 2 && !buttons[0].Bounds.IntersectsWith(buttons[1].Bounds), "环境说明按钮布局保持完整"); help.Close();
                    }
                    Field<Button>(form, "_navConversion").PerformClick();
                    Require(Field<Control>(form, "_conversionPage").Visible, "侧栏显示独立工程转换页面");
                    Screenshot(form, Path.Combine(output, "conversion-empty.png"));
                    Await(Invoke(form, "LoadConversionProjectAsync", root, true));
                    Require(Field<Control>(form, "_conversionDirection").GetType().GetProperty("SelectedIndex")!.GetValue(Field<Control>(form, "_conversionDirection"))!.Equals(0), "MDK 工程自动选择 MDK 转 CMake");
                    Require(Field<Button>(form, "_conversionApply").Enabled && Field<Label>(form, "_conversionSummary").Text.Contains("STM32"), "识别 CubeMX 芯片并选择唯一 MDK 目标");
                    Require(Field<Control>(form, "_conversionDirection").Height >= 30 && Field<Control>(form, "_conversionSource").Top > Field<Control>(form, "_conversionDirection").Bottom && Field<Control>(form, "_conversionTarget").Top > Field<Control>(form, "_conversionSource").Bottom, "转换设置首次显示时完整排列");
                    Screenshot(form, Path.Combine(output, "conversion-mdk-selection.png"));
                    var previews = 0;
                    var originalMdk = Path.Combine(root, "MDK-ARM", CubeMxFixture.Target + ".uvprojx");
                    var originalMdkBytes = File.ReadAllBytes(originalMdk);
                    using var confirm = new System.Windows.Forms.Timer { Interval = 150 };
                    confirm.Tick += (_, _) =>
                    {
                        foreach (var dialog in Application.OpenForms.Cast<Form>().Where(x => x.GetType().Name == "ChangePreviewDialog").ToArray())
                        {
                            confirm.Stop();
                            Require(previews == 0 ? !File.Exists(Path.Combine(root, "CMakeLists.txt")) : File.ReadAllBytes(originalMdk).SequenceEqual(originalMdkBytes), "预览确认前未写入目标配置");
                            previews++; Screenshot(dialog, Path.Combine(output, "conversion-preview.png")); dialog.AcceptButton!.PerformClick(); confirm.Start();
                        }
                    };
                    confirm.Start(); Await(Invoke(form, "ApplyConversionAsync")); confirm.Stop();
                    Require(previews == 1 && File.Exists(Path.Combine(root, "CMakeLists.txt")), "差异确认后生成 CMake 工程");
                    Require(Field<Label>(form, "_conversionResultTitle").Text.Contains("GCC 编译验证通过"), "MDK 转 CMake 后实际 GCC 编译通过：" + Field<Label>(form, "_conversionResultBody").Text);
                    Require(Field<bool>(form, "_conversionResultShown") && Field<Label>(form, "_conversionResultBody").Text.Contains(".elf"), "结果卡显示实际 ELF 输出");
                    Require(Field<Button>(form, "_conversionNext").Top > Field<Label>(form, "_conversionResultBody").Bottom && Field<Button>(form, "_conversionNext").Width > 100, "结果操作按钮位于完整说明下方");
                    Require(!Field<RichTextBox>(form, "_conversionLog").Visible, "转换详细日志默认折叠");
                    Require(ChangeHistory.List(root).Any(x => x.Label.Contains("工程转换")), "转换修改保存可恢复记录");
                    Screenshot(form, Path.Combine(output, "conversion-cmake-result.png"));
                    Field<Button>(form, "_conversionNext").PerformClick(); Until(() => !Field<bool>(form, "_busy"));
                    Require(Field<ProjectInfo>(form, "_project").Root == root && Field<ProjectInfo>(form, "_project").IsCMakeProject, "转到工程配置后使用转换工程根目录");
                    Field<Button>(form, "_navConversion").PerformClick();
                    Require(Field<Button>(form, "_conversionRestore").Visible && Field<Button>(form, "_conversionRestore").Enabled, "结果失效后仍可访问恢复记录");
                    var presetsPath = Path.Combine(root, "CMakePresets.json");
                    var presets = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(presetsPath))!;
                    var selectable = presets["configurePresets"]!.AsArray()[0]!.DeepClone();
                    selectable["name"] = "Selectable"; selectable["binaryDir"] = "${sourceDir}/build/Selectable";
                    selectable["cacheVariables"]!.AsObject().Remove("CMAKE_BUILD_TYPE");
                    presets["configurePresets"]!.AsArray().Add(selectable);
                    File.WriteAllText(presetsPath, presets.ToJsonString());
                    var direction = Field<Control>(form, "_conversionDirection"); direction.GetType().GetProperty("SelectedIndex")!.SetValue(direction, 1);
                    Until(() => !Field<bool>(form, "_busy"));
                    Require(!Field<bool>(form, "_conversionResultShown"), "更换方向清除之前的转换结果");
                    var presetPicker = Field<Control>(form, "_conversionPreset");
                    var presetNames = (List<string>)presetPicker.GetType().GetProperty("Items")!.GetValue(presetPicker)!;
                    presetPicker.GetType().GetProperty("SelectedIndex")!.SetValue(presetPicker, presetNames.IndexOf("Selectable"));
                    var configuration = Field<Control>(form, "_conversionConfiguration"); configuration.GetType().GetProperty("SelectedIndex")!.SetValue(configuration, 1);
                    Await(Invoke(form, "ReadConversionTargetsAsync"));
                    Require(Field<Button>(form, "_conversionApply").Enabled, "解析当前 CMake 配置的实际可执行目标");
                    Require(File.ReadAllText(Path.Combine(Field<string>(form, "_conversionBuildDirectory"), "CMakeCache.txt")).Contains("CMAKE_BUILD_TYPE:STRING=Release"), "未固定构建类型的预设按用户选择配置 Release");
                    var reread = Invoke(form, "ReadConversionTargetsAsync");
                    Require(Field<string?>(form, "_conversionBuildDirectory") == null && !Field<Button>(form, "_conversionApply").Enabled,
                        "重新解析时清除旧构建目标，读取完成前不能转换");
                    Await(reread);
                    Require(Field<Button>(form, "_conversionApply").Enabled, "重新解析完成后使用当前目标");
                    Screenshot(form, Path.Combine(output, "conversion-cmake-selection.png"));
                    confirm.Start(); Await(Invoke(form, "ApplyConversionAsync")); confirm.Stop();
                    Require(previews == 2 && Field<bool>(form, "_conversionResultShown"), "预览并写入反向转换的 MDK 工程：" + Field<Label>(form, "_conversionStage").Text + "\n" + string.Join("\n", Field<IReadOnlyList<ConversionIssue>>(form, "_conversionIssues").Select(x => x.Message + "：" + x.Action)));
                    var projectPath = Field<string>(form, "_conversionOutputPath");
                    Require(File.Exists(projectPath) && Path.GetExtension(projectPath) == ".uvprojx", "输出有效 MDK5 工程路径");
                    Require(!Field<Label>(form, "_conversionResultTitle").Text.Contains("未完成"), "MDK 工程检查或本机编译完成：" + Field<Label>(form, "_conversionResultBody").Text);
                    Require(StepStates(form).All(x => x == "Complete"), "五个转换步骤全部完成");
                    Require(!Directory.EnumerateFiles(root, "*.ps1", SearchOption.AllDirectories).Any(), "转换不生成 PowerShell 文件");
                    Screenshot(form, Path.Combine(output, "conversion-mdk-result.png"));
                    Field<Button>(form, "_conversionLogToggle").PerformClick(); Require(Field<RichTextBox>(form, "_conversionLog").Visible, "转换日志可以展开");
                    Console.WriteLine("PASS 普通窗口 CubeMX 双向转换流程；截图：" + output); exitCode = 0;
                }
                catch (Exception ex) { Console.Error.WriteLine(ex); }
                finally { form.Close(); Application.ExitThread(); }
            });
            Application.Run(form); return exitCode;
        }
        finally
        {
            var absolute = Path.GetFullPath(root);
            if (absolute.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) && Path.GetFileName(absolute).StartsWith("cmake-conversion-ui-", StringComparison.Ordinal) && Directory.Exists(absolute)) Directory.Delete(absolute, true);
        }
    }
    private static T Field<T>(Form form, string name) => (T)typeof(MainForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
    private static Task Invoke(Form form, string method, params object[] args) => (Task)typeof(MainForm).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, args)!;
    private static string[] StepStates(Form form) => ((System.Collections.IEnumerable)Field<Control>(form, "_conversionSteps").GetType().GetProperty("States")!.GetValue(Field<Control>(form, "_conversionSteps"))!).Cast<object>().Select(x => x.ToString()!).ToArray();
    private static void Require(bool value, string label) { if (!value) throw new Exception(label); Console.WriteLine("PASS " + label); }
    private static void Await(Task task) { Until(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
    private static void Until(Func<bool> ready) { var deadline = DateTime.UtcNow.AddMinutes(4); while (!ready() && DateTime.UtcNow < deadline) Pump(20); if (!ready()) throw new TimeoutException("转换界面操作超时"); }
    private static void Pump(int milliseconds) { var deadline = DateTime.UtcNow.AddMilliseconds(milliseconds); while (DateTime.UtcNow < deadline) { Application.DoEvents(); Thread.Sleep(10); } }
    private static void Screenshot(Form form, string path) { Pump(100); using var image = new Bitmap(form.Width, form.Height); form.DrawToBitmap(image, new(Point.Empty, image.Size)); image.Save(path); }
}
