using System.Diagnostics;
using System.Xml.Linq;
using CMakeDapLink.Core;

namespace CMakeDapLink.App;

public sealed partial class MainForm
{
    private readonly ScrollPage _conversionPage = new() { Dock = DockStyle.Fill, BackColor = Background, Visible = false, Name = "ConversionPage" };
    private readonly Button _navConversion = Button("工程转换", false);
    private readonly Button _conversionChoose = Button("选择工程", false);
    private readonly Button _conversionRefresh = Button("重新检测", false);
    private readonly Button _conversionRead = Button("解析 CMake 构建目标", false);
    private readonly Button _conversionApply = Button("预览并转换", true);
    private readonly Button _conversionHelp = Button("查看转换说明", false);
    private readonly Button _conversionLogToggle = Button("展开详细日志", false);
    private readonly Button _conversionProblemsButton = Button("编译问题与建议", false);
    private readonly Button _conversionOpen = Button("打开输出目录", false);
    private readonly Button _conversionNext = Button("转到工程配置", true);
    private readonly Button _conversionRestore = Button("恢复转换修改", false);
    private readonly TargetPicker _conversionDirection = new();
    private readonly TargetPicker _conversionSource = new();
    private readonly TargetPicker _conversionPreset = new();
    private readonly TargetPicker _conversionConfiguration = new();
    private readonly TargetPicker _conversionTarget = new();
    private readonly Label _conversionRootLabel = Label("选择包含 .ioc 的 STM32CubeMX 工程根目录，也可以拖入文件夹。", 9, Muted);
    private readonly Label _conversionSummary = Label("支持 MDK5 与 ARM GCC CMake 双向转换，不需要启动或安装 CubeMX。", 9, Muted);
    private readonly Label _conversionHint = Label("先选择工程，再核对转换方向和构建目标。", 9, Muted);
    private readonly Label _conversionStage = Label("等待选择工程", 9, Muted);
    private readonly Label _conversionElapsed = Label("", 8.5f, Muted);
    private readonly Label _conversionResultTitle = Label("", 11, Ink, bold: true);
    private readonly Label _conversionResultBody = Label("", 9, Muted);
    private readonly RichTextBox _conversionLog = new() { ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = Color.FromArgb(24, 32, 45), ForeColor = Color.FromArgb(205, 216, 231), Font = new Font("Consolas", 9), ScrollBars = RichTextBoxScrollBars.Vertical, Visible = false };
    private readonly WorkflowSteps _conversionSteps = new();
    private readonly ProgressStrip _conversionProgress = new();
    private readonly InlineNotice _conversionNotice = new();
    private readonly Stopwatch _conversionClock = new();
    private readonly System.Windows.Forms.Timer _conversionTimer = new() { Interval = 1000 };
    private readonly List<Control> _conversionCards = [];
    private readonly List<(Label Label, Control Field)> _conversionFields = [];
    private RoundedPanel _conversionHero = null!, _conversionSelection = null!, _conversionOptions = null!, _conversionActions = null!, _conversionOutput = null!, _conversionResult = null!;
    private ConversionInspection? _conversionInspection;
    private ConversionPlan? _conversionPlan;
    private ProjectInfo? _conversionCMakeProject;
    private IReadOnlyList<string> _conversionSourcePaths = [];
    private IReadOnlyList<ConversionIssue> _conversionIssues = [];
    private readonly List<BuildProblem> _conversionProblems = [];
    private string? _conversionRoot, _conversionOutputPath, _conversionBuildDirectory;
    private bool _conversionUpdating, _conversionResultShown, _conversionLogExpanded, _conversionRunning, _conversionLayingOut, _conversionHasHistory;
    private ConversionDirection SelectedConversionDirection => _conversionDirection.SelectedIndex == 1 ? ConversionDirection.CMakeToMdk : ConversionDirection.MdkToCMake;
    private void RefreshWorkspaceFooter()
    {
        var root = _conversionPage.Visible ? _conversionRoot : _project?.Root;
        _sideFoot.Text = _busy ? "操作进行中\n完成后可切换页面" : root == null ? "本地工作区" : "当前工程\n" + Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar));
    }

    private void BuildConversionPage()
    {
        _conversionDirection.Items.AddRange(["MDK-ARM → CMake", "CMake → MDK-ARM"]); _conversionDirection.SelectedIndex = 0;
        _conversionConfiguration.Items.AddRange(["Debug", "Release"]); _conversionConfiguration.SelectedIndex = 0;
        _conversionHero = new RoundedPanel(hero: true);
        _conversionHero.Controls.AddRange([Label("工程转换", 18, Ink, bold: true), Label("STM32CubeMX 工程 · MDK5 与 ARM GCC CMake", 9, Muted)]);
        _conversionSelection = new RoundedPanel();
        _conversionSelection.Controls.AddRange([Label("选择 CubeMX 工程", 11, Ink, bold: true), _conversionChoose, _conversionRootLabel]);
        _conversionSelection.AllowDrop = true; _conversionSelection.DragEnter += OnDragEnter; _conversionSelection.DragDrop += OnDragDrop;
        foreach (Control child in _conversionSelection.Controls) { child.AllowDrop = true; child.DragEnter += OnDragEnter; child.DragDrop += OnDragDrop; }
        _conversionOptions = new RoundedPanel(); _conversionOptions.Controls.Add(Label("转换设置", 11, Ink, bold: true));
        foreach (var pair in new (string Title, Control Field)[] { ("转换方向", _conversionDirection), ("MDK 工程文件", _conversionSource), ("CMake 配置预设", _conversionPreset), ("构建配置", _conversionConfiguration), ("构建目标", _conversionTarget) })
        {
            var label = Label(pair.Title, 9, Muted); _conversionFields.Add((label, pair.Field)); _conversionOptions.Controls.AddRange([label, pair.Field]);
        }
        _conversionOptions.Controls.AddRange([_conversionRead, _conversionSummary]);
        _conversionActions = new RoundedPanel();
        _conversionActions.Controls.AddRange([Label("预览并生成工程", 11, Ink, bold: true), _conversionHint, _conversionApply, _conversionRefresh, _conversionHelp, _conversionRestore]);
        _conversionOutput = new RoundedPanel();
        _conversionOutput.Controls.AddRange([Label("转换进度", 11, Ink, bold: true), _conversionSteps, _conversionStage, _conversionElapsed, _conversionProgress, _conversionLogToggle, _conversionProblemsButton, _conversionLog]);
        _conversionResult = new RoundedPanel();
        _conversionResult.Controls.AddRange([_conversionResultTitle, _conversionResultBody, _conversionOpen, _conversionNext]);
        foreach (var card in new Control[] { _conversionHero, _conversionNotice, _conversionSelection, _conversionOptions, _conversionActions, _conversionOutput, _conversionResult })
        { _conversionCards.Add(card); _conversionPage.Canvas.Controls.Add(card); _conversionPage.AttachCard(card); }
        _conversionChoose.Click += (_, _) => BrowseConversionProject();
        _conversionRefresh.Click += async (_, _) => { if (_conversionRoot != null) await LoadConversionProjectAsync(_conversionRoot, false); };
        _conversionDirection.SelectedIndexChanged += async (_, _) => { if (!_conversionUpdating && !_busy && _conversionRoot != null) await LoadConversionProjectAsync(_conversionRoot, false); };
        _conversionSource.SelectedIndexChanged += (_, _) => { if (!_conversionUpdating) { InvalidateConversionResult(); PopulateMdkTargets(); UpdateConversionActions(); LayoutConversionPage(); } };
        _conversionPreset.SelectedIndexChanged += (_, _) => { if (!_conversionUpdating) { RefreshConversionPreset(); InvalidateConversionTargets(); } };
        _conversionConfiguration.SelectedIndexChanged += (_, _) => { if (!_conversionUpdating) InvalidateConversionTargets(); };
        _conversionTarget.SelectedIndexChanged += (_, _) => { if (!_conversionUpdating) { InvalidateConversionResult(); UpdateConversionActions(); } };
        _conversionRead.Click += async (_, _) => await ReadConversionTargetsAsync();
        _conversionApply.Click += async (_, _) => await ApplyConversionAsync();
        _conversionHelp.Click += (_, _) => ShowConversionIssues();
        _conversionProblemsButton.Click += (_, _) => { using var problems = new BuildProblemsDialog(_conversionProblems.Distinct().ToArray(), _conversionLog.Text); problems.ShowDialog(this); };
        _conversionLogToggle.Click += (_, _) => { _conversionLogExpanded = !_conversionLogExpanded; _conversionLogToggle.Text = _conversionLogExpanded ? "收起详细日志" : "展开详细日志"; LayoutConversionPage(); };
        _conversionNotice.Dismissed += (_, _) => LayoutConversionPage();
        _conversionOpen.Click += (_, _) => { if (!_busy && _conversionOutputPath != null) OpenLocation(Path.GetDirectoryName(_conversionOutputPath)!); };
        _conversionNext.Click += async (_, _) => { if (!_busy && _conversionRoot != null) { ShowWorkspacePage(0); await LoadProjectAsync(_conversionRoot); } };
        _conversionRestore.Click += async (_, _) =>
        {
            if (_busy || _conversionRoot == null) return;
            using var history = new RestoreHistoryDialog(_conversionRoot); history.ShowDialog(this);
            InvalidateCompletion(); await LoadConversionProjectAsync(_conversionRoot, false);
        };
        _conversionTimer.Tick += (_, _) => { _conversionElapsed.Text = $"{(_conversionClock.IsRunning ? "已用时" : "用时")} {(int)_conversionClock.Elapsed.TotalMinutes:00}:{_conversionClock.Elapsed.Seconds:00}"; };
        ResetConversionSteps(); UpdateConversionActions();
    }

    private void BrowseConversionProject()
    {
        if (_busy) return;
        using var chooser = new FolderBrowserDialog { Description = "选择包含 .ioc 的 STM32CubeMX 工程根目录", UseDescriptionForTitle = true, SelectedPath = _conversionRoot ?? _project?.Root ?? "" };
        if (chooser.ShowDialog(this) == DialogResult.OK) _ = LoadConversionProjectAsync(chooser.SelectedPath);
    }
    private async Task LoadConversionProjectAsync(string root, bool chooseDirection = true)
    {
        if (_busy) return;
        _conversionRoot = Path.GetFullPath(root); _conversionRootLabel.Text = _conversionRoot;
        _conversionInspection = null; _conversionCMakeProject = null; _conversionBuildDirectory = null;
        _conversionHasHistory = false;
        _conversionIssues = []; _conversionSourcePaths = []; _conversionPlan = null;
        _conversionUpdating = true;
        if (chooseDirection) _conversionDirection.SelectedIndex = File.Exists(Path.Combine(root, "CMakeLists.txt")) ? 1 : 0;
        foreach (var picker in new[] { _conversionSource, _conversionPreset, _conversionTarget }) { picker.SelectedIndex = -1; picker.Items.Clear(); picker.Invalidate(); }
        _conversionUpdating = false; InvalidateConversionResult(); _conversionNotice.Visible = false; _conversionLog.Clear();
        BeginConversion(_conversionChoose, "读取工程…"); _conversionSteps.SetStep(0, StepState.Running);
        try
        {
            var direction = SelectedConversionDirection;
            _conversionInspection = await Task.Run(() => CubeMxConverter.Inspect(_conversionRoot, direction));
            _conversionHasHistory = await Task.Run(() => ChangeHistory.List(_conversionRoot).Count > 0);
            _conversionIssues = _conversionInspection.Issues;
            _conversionUpdating = true;
            _conversionSourcePaths = _conversionInspection.SourceProjects;
            _conversionSource.Items.AddRange(_conversionSourcePaths.Select(x => Path.GetRelativePath(_conversionRoot, Path.GetFullPath(x, _conversionRoot))));
            if (_conversionSource.Items.Count == 1) _conversionSource.SelectedIndex = 0;
            if (direction == ConversionDirection.CMakeToMdk)
            {
                _conversionCMakeProject = ProjectInspector.Inspect(_conversionRoot);
                _conversionPreset.Items.AddRange(_conversionCMakeProject.ConfigurePresets.Select(x => x.Name));
                _conversionPreset.Items.Add("不使用预设");
                _conversionPreset.SelectedIndex = Math.Max(0, _conversionPreset.Items.IndexOf(_conversionCMakeProject.ConfigurePreset ?? "不使用预设"));
                RefreshConversionPreset();
            }
            else PopulateMdkTargets();
            _conversionSummary.Text = $"芯片：{_conversionInspection.Chip}\n" + (direction == ConversionDirection.MdkToCMake ? "读取 MDK 已启用文件和目标设置，生成 GCC CMake 配置。" : "先解析所选 CMake 配置，随后选择需要导出的实际构建目标。") + "\n转换全程不调用 CubeMX，不修改原有源文件。";
            _conversionSteps.SetStep(0, _conversionIssues.Any(x => x.Blocking) ? StepState.Attention : StepState.Complete);
            _conversionStage.Text = "工程读取完成，请核对转换设置。";
        }
        catch (Exception ex) { ConversionProblem("工程读取未完成", ex); _conversionSteps.SetStep(0, StepState.Attention); }
        finally { _conversionUpdating = false; EndConversion(_conversionChoose, "选择工程"); _conversionPage.ScrollTo(0); }
    }
    private void PopulateMdkTargets()
    {
        if (SelectedConversionDirection != ConversionDirection.MdkToCMake) return;
        _conversionUpdating = true; _conversionTarget.SelectedIndex = -1; _conversionTarget.Items.Clear();
        try
        {
            var source = SelectedMdkProject();
            var names = source == null ? Array.Empty<string>() : XDocument.Load(source).Descendants("Target").Elements("TargetName").Select(x => x.Value).Distinct(StringComparer.Ordinal).ToArray();
            _conversionTarget.Items.AddRange(names); if (names.Length == 1) _conversionTarget.SelectedIndex = 0;
        }
        catch (Exception ex) { ConversionProblem("MDK 目标读取未完成", ex); }
        finally { _conversionUpdating = false; }
    }
    private string? SelectedMdkProject() => _conversionRoot == null || _conversionSource.SelectedIndex < 0 || _conversionSource.SelectedIndex >= _conversionSourcePaths.Count ? null : Path.GetFullPath(_conversionSourcePaths[_conversionSource.SelectedIndex], _conversionRoot);
    private void RefreshConversionPreset()
    {
        if (_conversionRoot == null) return;
        _conversionUpdating = true;
        var name = _conversionPreset.SelectedIndex < 0 ? null : _conversionPreset.Items[_conversionPreset.SelectedIndex];
        _conversionCMakeProject = ProjectInspector.Inspect(_conversionRoot, name == "不使用预设" ? null : name, usePresets: name != "不使用预设");
        var preset = _conversionCMakeProject.ConfigurePresets.FirstOrDefault(x => x.Name == name);
        var fixedConfiguration = preset?.ConfigureConfiguration;
        if (!string.IsNullOrWhiteSpace(fixedConfiguration) && !_conversionConfiguration.Items.Contains(fixedConfiguration)) _conversionConfiguration.Items.Add(fixedConfiguration);
        _conversionConfiguration.SelectedIndex = _conversionConfiguration.Items.IndexOf(fixedConfiguration ?? "Debug");
        _conversionUpdating = false;
    }
    private void InvalidateConversionResult()
    {
        _conversionResultShown = false; _conversionOutputPath = null; _conversionPlan = null;
        _conversionProblems.Clear();
        _conversionIssues = _conversionInspection?.Issues ?? [];
        if (!_conversionRunning) _conversionNotice.Visible = false;
        if (!_conversionRunning) { ResetConversionSteps(); _conversionProgress.Value = 0; _conversionElapsed.Text = ""; _conversionStage.Text = "设置有变化，请重新预览并转换。"; }
        LayoutConversionPage();
    }
    private void InvalidateConversionTargets()
    {
        if (SelectedConversionDirection == ConversionDirection.CMakeToMdk)
        { _conversionBuildDirectory = null; _conversionTarget.SelectedIndex = -1; _conversionTarget.Items.Clear(); _conversionTarget.Invalidate(); }
        InvalidateConversionResult(); UpdateConversionActions(); LayoutConversionPage();
    }
    private string ConversionConfiguration => _conversionConfiguration.SelectedIndex < 0 ? "Debug" : _conversionConfiguration.Items[_conversionConfiguration.SelectedIndex];
    private CMakeBuildPlan ConversionBuildPlan(bool converted = false)
    {
        var root = _conversionRoot!;
        var name = converted ? "Debug" : _conversionPreset.SelectedIndex < 0 ? null : _conversionPreset.Items[_conversionPreset.SelectedIndex];
        if (name == "不使用预设") name = null;
        var inspected = ProjectInspector.Inspect(root, name, usePresets: name != null);
        var options = new SetupOptions(root, _tools.CMake!, _tools.Ninja!, _tools.Compiler!, "", "", "", name, null,
            name == null ? Path.Combine(root, "build", "conversion-debug") : inspected.BuildDirectory, inspected.ToolchainFile) { BuildConfiguration = converted ? "Debug" : ConversionConfiguration };
        var plan = CMakeBuildPlan.Create(options);
        var preset = inspected.ConfigurePresets.FirstOrDefault(x => x.Name == name);
        if (name != null && string.IsNullOrWhiteSpace(preset?.ConfigureConfiguration))
            plan = plan with { Configure = plan.Configure with { Arguments = plan.Configure.Arguments.Append("-DCMAKE_BUILD_TYPE=" + options.BuildConfiguration).ToArray() } };
        return plan;
    }
    private bool ConversionToolsReady => _tools.CMake != null && _tools.Ninja != null && _tools.Compiler != null;
    private async Task ConfigureConversionCMakeAsync(CMakeBuildPlan build)
    {
        build.PrepareArtifactQuery(); _conversionStage.Text = "正在解析 CMake 构建配置…"; LayoutConversionPage();
        var result = await ProcessTools.RunAsync(build.Configure.Executable, build.Configure.Arguments, build.Configure.WorkingDirectory, TimeSpan.FromMinutes(5), AppendConversion, build.Configure.PathPrefix);
        _conversionProblems.AddRange(BuildDiagnostics.Parse(result.Output, build.Configure.WorkingDirectory, "CMake 配置"));
        if (result.ExitCode != 0) throw new InvalidOperationException("CMake 配置未通过。请展开日志，修正源码依赖或配置后重新解析。");
        _conversionBuildDirectory = build.BuildDirectory;
    }
    private async Task ReadConversionTargetsAsync()
    {
        if (_busy || _conversionRoot == null || !ConversionToolsReady) return;
        InvalidateConversionTargets();
        BeginConversion(_conversionRead, "解析目标中…"); _conversionSteps.SetStep(0, StepState.Complete); _conversionSteps.SetStep(1, StepState.Running);
        try
        {
            await ConfigureConversionCMakeAsync(ConversionBuildPlan());
            var inspected = await Task.Run(() => CubeMxConverter.Inspect(_conversionRoot, ConversionDirection.CMakeToMdk));
            _conversionInspection = inspected; _conversionIssues = inspected.Issues;
            _conversionUpdating = true; _conversionTarget.SelectedIndex = -1; _conversionTarget.Items.Clear();
            _conversionTarget.Items.AddRange(ReadConfiguredConversionTargets()); if (_conversionTarget.Items.Count == 1) _conversionTarget.SelectedIndex = 0;
            _conversionUpdating = false;
            _conversionSteps.SetStep(1, StepState.Complete); _conversionStage.Text = "构建目标已解析，请核对目标并预览转换。";
            _conversionNotice.ShowMessage("已读取当前配置的实际目标。选择目标后即可预览 MDK 工程。", actionText: null);
        }
        catch (Exception ex) { ConversionProblem("解析未完成", ex); _conversionSteps.SetStep(1, StepState.Attention); }
        finally { _conversionUpdating = false; EndConversion(_conversionRead, "解析 CMake 构建目标"); }
    }
    private string[] ReadConfiguredConversionTargets()
    {
        var reply = Path.Combine(_conversionBuildDirectory!, ".cmake", "api", "v1", "reply");
        var indexPath = Directory.EnumerateFiles(reply, "index-*.json").OrderDescending(StringComparer.Ordinal).First();
        using var index = System.Text.Json.JsonDocument.Parse(File.ReadAllText(indexPath));
        var reference = index.RootElement.GetProperty("objects").EnumerateArray().First(x => x.GetProperty("kind").GetString() == "codemodel");
        using var model = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(reply, reference.GetProperty("jsonFile").GetString()!)));
        var configs = model.RootElement.GetProperty("configurations").EnumerateArray().ToArray();
        var selected = configs.FirstOrDefault(x => x.GetProperty("name").GetString() == ConversionConfiguration);
        if (selected.ValueKind == System.Text.Json.JsonValueKind.Undefined && configs.Length == 1 && string.IsNullOrEmpty(configs[0].GetProperty("name").GetString())) selected = configs[0];
        if (selected.ValueKind == System.Text.Json.JsonValueKind.Undefined) throw new InvalidOperationException("当前 CMake 配置中没有所选构建类型，请重新选择配置。");
        var names = new List<string>();
        foreach (var target in selected.GetProperty("targets").EnumerateArray())
        {
            using var data = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(reply, target.GetProperty("jsonFile").GetString()!)));
            if (data.RootElement.GetProperty("type").GetString() == "EXECUTABLE") names.Add(data.RootElement.GetProperty("name").GetString()!);
        }
        return names.Order(StringComparer.Ordinal).ToArray();
    }
    private async Task ApplyConversionAsync()
    {
        if (_busy || _conversionRoot == null || _conversionTarget.SelectedIndex < 0) return;
        var root = _conversionRoot; var direction = SelectedConversionDirection;
        var request = new ConversionRequest(root, direction, _conversionTarget.Items[_conversionTarget.SelectedIndex], SelectedMdkProject(), _conversionBuildDirectory, ConversionConfiguration);
        InvalidateConversionResult(); BeginConversion(_conversionApply, "转换验证中…"); _conversionSteps.SetStep(0, StepState.Complete); _conversionSteps.SetStep(1, StepState.Running);
        try
        {
            var plan = await Task.Run(() => CubeMxConverter.CreatePlan(request)); _conversionPlan = plan; _conversionIssues = plan.Issues;
            AppendConversion(plan.Summary);
            if (!plan.CanApply)
            {
                _conversionSteps.SetStep(1, StepState.Attention); _conversionStage.Text = plan.Changes.Count == 0 && !plan.Issues.Any(x => x.Blocking) ? "输出配置已一致，无需写入。" : "请先补全转换所需信息。";
                _conversionNotice.ShowMessage(_conversionStage.Text, true, _conversionIssues.Count > 0 ? "查看具体步骤" : null, _conversionIssues.Count > 0 ? ShowConversionIssues : null); return;
            }
            _conversionSteps.SetStep(1, StepState.Complete); _conversionSteps.SetStep(2, StepState.Running); _conversionProgress.Value = 35;
            using var preview = new ChangePreviewDialog(root, direction == ConversionDirection.MdkToCMake ? "MDK-ARM 转换为 CMake" : "CMake 转换为 MDK-ARM", plan.Changes);
            if (preview.ShowDialog(this) != DialogResult.OK) { _conversionSteps.SetStep(2, StepState.Cancelled); _conversionStage.Text = "已取消写入，原工程保留。"; return; }
            _conversionSteps.SetStep(2, StepState.Complete); _conversionSteps.SetStep(3, StepState.Running);
            ChangeHistory.Apply(root, "工程转换 · " + direction, plan.Changes); InvalidateCompletion();
            _conversionHasHistory = true;
            _conversionOutputPath = Path.GetFullPath(plan.OutputProject, root);
            _conversionSteps.SetStep(3, StepState.Complete); _conversionSteps.SetStep(4, StepState.Running); _conversionProgress.Value = 70;
            var details = plan.Summary + $"\n输出：{_conversionOutputPath}";
            if (direction == ConversionDirection.MdkToCMake && ConversionToolsReady)
            {
                var build = ConversionBuildPlan(converted: true); await ConfigureConversionCMakeAsync(build);
                _conversionStage.Text = "正在实际编译转换后的 GCC 工程…"; LayoutConversionPage();
                var compiled = await ProcessTools.RunAsync(build.Build.Executable, build.Build.Arguments, root, TimeSpan.FromMinutes(15), AppendConversion, build.Build.PathPrefix);
                _conversionProblems.AddRange(BuildDiagnostics.Parse(compiled.Output, root, "源码编译"));
                if (compiled.ExitCode != 0) throw new InvalidOperationException("配置已写入，但编译未通过。点击“编译问题与建议”查看文件位置和修正方法；也可以恢复本次转换。");
                var firmware = CMakeBuildPlan.FindElfs(build.BuildDirectory, build.BuildConfiguration);
                details += "\nGCC 实际编译通过。" + (firmware.Count == 1 ? "\n" + FirmwareSummary(firmware[0], build.BuildConfiguration) : $"\n生成 {firmware.Count} 个当前目标固件。");
                var warnings = _conversionProblems.Distinct().Count(x => x.Severity == "警告");
                if (warnings > 0) details += $"\n有 {warnings} 条编译/链接警告，请查看“编译问题与建议”，核对后再使用固件。";
                _conversionResultTitle.Text = "转换完成，GCC 编译验证通过";
            }
            else if (direction == ConversionDirection.MdkToCMake)
            {
                details += "\n已生成 CMake 配置；尚未运行编译。请在工程配置补齐 CMake、Ninja、ARM GCC，然后配置并验证。";
                _conversionResultTitle.Text = "CMake 工程已生成，待编译验证";
            }
            else
            {
                var validation = await VerifyConvertedMdkAsync(_conversionOutputPath);
                details += "\n" + validation.Details;
                _conversionResultTitle.Text = validation.Compiled ? "转换完成，Keil 编译验证通过" : "MDK 工程已生成，待 Keil 编译验证";
            }
            if (_conversionIssues.Count > 0) details += $"\n有 {_conversionIssues.Count} 项转换提示，可查看转换说明。";
            _conversionResultBody.Text = details; _conversionResultTitle.ForeColor = Color.FromArgb(28, 136, 100);
            _conversionResultShown = true; _conversionSteps.SetStep(4, StepState.Complete); _conversionProgress.Value = 100;
            _conversionStage.Text = "转换完成，原有源文件和原格式工程保留；替换的配置可恢复。";
        }
        catch (Exception ex)
        {
            ConversionProblem("转换未完成", ex);
            if (_conversionProblems.Any(x => x.Severity == "错误")) _conversionNotice.ShowMessage("实际编译发现需要处理的源码或链接问题。", true, "查看编译问题", () => _conversionProblemsButton.PerformClick());
            for (var i = 0; i < _conversionSteps.States.Count; i++) if (_conversionSteps.States[i] == StepState.Running) _conversionSteps.SetStep(i, StepState.Attention);
            if (_conversionOutputPath != null) { _conversionResultShown = true; _conversionResultTitle.Text = "配置已写入，验证未完成"; _conversionResultTitle.ForeColor = Color.FromArgb(160, 91, 32); _conversionResultBody.Text = ex.Message + "\n输出：" + _conversionOutputPath + "\n可查看详细日志，或恢复本次转换。"; }
        }
        finally { EndConversion(_conversionApply, "预览并转换"); if (_conversionResultShown) _conversionPage.ScrollTo(_conversionResult.Top - Px(16)); }
    }
    private void BeginConversion(Button button, string text)
    {
        _conversionRunning = _busy = true; _conversionResultShown = false; _conversionNotice.Visible = false;
        button.Text = text; ((SoftButton)button).IsBusy = true; _conversionClock.Restart(); _conversionTimer.Start(); ResetConversionSteps();
        UpdateActions(); LayoutConversionPage();
    }
    private void EndConversion(Button button, string text)
    {
        _conversionClock.Stop(); _conversionTimer.Stop(); _conversionElapsed.Text = $"用时 {(int)_conversionClock.Elapsed.TotalMinutes:00}:{_conversionClock.Elapsed.Seconds:00}";
        ((SoftButton)button).IsBusy = false; button.Text = text; _conversionRunning = _busy = false; UpdateActions(); LayoutConversionPage();
    }
    private void ResetConversionSteps() => _conversionSteps.Reset("检查工程", "读取构建信息", "预览修改", "写入配置", "验证结果");
    private void AppendConversion(string line)
    {
        if (IsDisposed || Disposing) return;
        if (InvokeRequired) { BeginInvoke(() => AppendConversion(line)); return; }
        _conversionLog.AppendText(line + Environment.NewLine);
    }
    private void ConversionProblem(string title, Exception ex)
    {
        AppendConversion(ex.ToString()); _conversionStage.Text = title + "：" + ex.Message;
        _conversionNotice.ShowMessage(_conversionStage.Text, true, "查看日志", () => { _conversionLogExpanded = true; _conversionLogToggle.Text = "收起详细日志"; LayoutConversionPage(); _conversionPage.ScrollTo(_conversionOutput.Top - Px(16)); });
    }
    private void ShowConversionIssues()
    {
        if (_conversionIssues.Count == 0) return;
        using var help = new CompletionDialog(_conversionIssues.Select((x, i) => new SetupIssue("Conversion" + i, x.Message, x.Action, x.Blocking)).ToArray(), conversion: true); help.ShowDialog(this);
    }
    private void UpdateConversionActions()
    {
        if (_conversionActions == null) return;
        var mdk = SelectedConversionDirection == ConversionDirection.MdkToCMake;
        var ready = _conversionInspection != null && !_conversionIssues.Any(x => x.Blocking);
        _navConversion.Enabled = !_busy;
        _conversionChoose.Enabled = _conversionDirection.Enabled = !_busy;
        _conversionRefresh.Enabled = !_busy && _conversionRoot != null;
        _conversionSource.Enabled = !_busy && mdk && _conversionSource.Items.Count > 0;
        _conversionPreset.Enabled = !_busy && !mdk && _conversionPreset.Items.Count > 0;
        var fixedConfig = _conversionCMakeProject?.ConfigurePresets.FirstOrDefault(x => x.Name == (_conversionPreset.SelectedIndex < 0 ? "" : _conversionPreset.Items[_conversionPreset.SelectedIndex]))?.ConfigureConfiguration;
        _conversionConfiguration.Enabled = !_busy && !mdk && string.IsNullOrEmpty(fixedConfig);
        _conversionTarget.Enabled = !_busy && _conversionTarget.Items.Count > 0;
        _conversionRead.Enabled = !_busy && !mdk && ready && ConversionToolsReady;
        _conversionApply.Enabled = !_busy && ready && _conversionTarget.SelectedIndex >= 0 && (mdk ? SelectedMdkProject() != null : _conversionBuildDirectory != null);
        _conversionApply.BackColor = _conversionApply.Enabled || ((SoftButton)_conversionApply).IsBusy ? Accent : Color.FromArgb(226, 234, 242);
        _conversionHelp.Enabled = !_busy && _conversionIssues.Count > 0;
        _conversionProblemsButton.Enabled = !_busy;
        _conversionOpen.Enabled = _conversionNext.Enabled = !_busy;
        _conversionRestore.Enabled = !_busy && _conversionRoot != null && _conversionHasHistory;
        _conversionNext.Visible = mdk;
        foreach (var pair in _conversionFields) { pair.Field.Visible = pair.Label.Visible = pair.Field != _conversionSource && pair.Field != _conversionPreset && pair.Field != _conversionConfiguration || (pair.Field == _conversionSource ? mdk : !mdk); }
        _conversionRead.Visible = !mdk;
        _conversionHint.Text = _busy ? "正在执行转换，请等待当前操作完成。" : _conversionInspection == null ? "先选择含 .ioc 的工程根目录。" : _conversionIssues.Any(x => x.Blocking) ? "请先处理补全说明中列出的事项，再重新检测。" : !mdk && !ConversionToolsReady ? "解析 CMake 需要 CMake、Ninja 和 ARM GCC；请到工程配置补齐工具。" : _conversionTarget.SelectedIndex < 0 ? mdk ? "请选择 MDK 工程文件和需要转换的构建目标。" : "先解析 CMake 构建目标，再选择要导出的目标。" : "写入前会显示差异；原有源文件保留，配置修改可恢复。";
        _navTips.SetToolTip(_conversionApply, _conversionHint.Text);
    }
}
