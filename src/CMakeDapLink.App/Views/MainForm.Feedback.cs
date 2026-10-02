using System.Diagnostics;
using CMakeDapLink.Core;

namespace CMakeDapLink.App;

public sealed partial class MainForm
{
    private readonly WorkflowSteps _workflowSteps = new();
    private readonly WorkflowSteps _sourceSteps = new();
    private readonly WorkflowSteps _importSteps = new();
    private readonly Label _elapsed = Label("", 8.5f, Muted);
    private readonly Label _sourceElapsed = Label("", 8.5f, Muted);
    private readonly Label _importElapsed = Label("", 8.5f, Muted);
    private readonly Button _toggleLog = Button("展开详细日志", false);
    private readonly Button _toggleSourceLog = Button("展开详细日志", false);
    private readonly Button _toggleImportLog = Button("展开复制记录", false);
    private readonly Button _stopRepair = Button("停止下载", false);
    private readonly Button _sourceChooseProject = Button("选择工程", true);
    private readonly Button _importChooseProject = Button("选择工程", true);
    private readonly InlineNotice _notice = new();
    private readonly InlineNotice _sourceNotice = new();
    private readonly InlineNotice _importNotice = new();
    private readonly Stopwatch _operationClock = new();
    private readonly System.Windows.Forms.Timer _feedbackTimer = new() { Interval = 1000 };
    private Button? _activeButton;
    private string _activeButtonText = "";
    private string _operation = "";
    private bool _logExpanded, _sourceLogExpanded, _importLogExpanded;
    private bool _completionShown, _sourceCompletionShown;
    private RoundedPanel _completionCard = null!;
    private RoundedPanel _sourceCompletionCard = null!;
    private readonly Label _completionTitle = Label("", 11, Ink, bold: true);
    private readonly Label _completionBody = Label("", 9, Muted);
    private readonly Label _sourceCompletionTitle = Label("", 11, Ink, bold: true);
    private readonly Label _sourceCompletionBody = Label("", 9, Muted);
    private readonly Button _openProject = Button("打开工程", false);
    private readonly Button _openFirmware = Button("打开固件目录", false);
    private readonly Button _viewLastChanges = Button("查看本次修改", false);
    private readonly Button _completionHelp = Button("查看问题与日志", false);
    private readonly Button _sourceNext = Button("前往配置任务", true);
    private readonly Button _viewSourceChanges = Button("查看本次修改", false);
    private string? _completedFirmware;
    private IReadOnlyList<FileChange> _lastTaskChanges = [], _lastSourceChanges = [];
    private IReadOnlyList<string> _lastImportedFiles = [];

    private void BuildFeedback()
    {
        _workflowSteps.Reset("检查环境", "写入任务", "配置 CMake", "编译", "检查脚本");
        _sourceSteps.Reset("检查环境", "写入配置", "配置 CMake", "编译验证");
        _importSteps.Reset("核对文件", "复制校验", "完成");
        _output.Controls.AddRange([_workflowSteps, _elapsed, _toggleLog]);
        _sourceAction.Controls.AddRange([_sourceSteps, _sourceElapsed, _toggleSourceLog]);
        _importResult.Controls.AddRange([_importSteps, _importElapsed, _toggleImportLog]);
        _action.Controls.Add(_stopRepair); _stopRepair.Visible = false;
        _stopRepair.Click += (_, _) => { _repairCancellation?.Cancel(); _stopRepair.Enabled = false; _stopRepair.Text = "正在停止…"; _stage.Text = "正在停止下载…"; LayoutPage(); };
        _toggleLog.Click += (_, _) => { _logExpanded = !_logExpanded; _toggleLog.Text = _logExpanded ? "收起详细日志" : "展开详细日志"; LayoutPage(); };
        _toggleSourceLog.Click += (_, _) => { _sourceLogExpanded = !_sourceLogExpanded; _toggleSourceLog.Text = _sourceLogExpanded ? "收起详细日志" : "展开详细日志"; LayoutSourcePage(); };
        _toggleImportLog.Click += (_, _) => { _importLogExpanded = !_importLogExpanded; _toggleImportLog.Text = _importLogExpanded ? "收起复制记录" : "展开复制记录"; LayoutImportPage(); };
        _log.Visible = _sourceLog.Visible = _importLog.Visible = false;
        _sourceChooseProject.Click += (_, _) => { ShowWorkspacePage(0); BrowseProject(); };
        _importChooseProject.Click += (_, _) => { ShowWorkspacePage(0); BrowseProject(); };
        _sourceHero.Controls.Add(_sourceChooseProject); _importHero.Controls.Add(_importChooseProject);
        _completionCard = new RoundedPanel();
        _completionCard.Controls.AddRange([_completionTitle, _completionBody, _openProject, _openFirmware, _viewLastChanges, _completionHelp]);
        Add(_completionCard); _completionCard.Visible = false;
        _sourceCompletionCard = new RoundedPanel();
        _sourceCompletionCard.Controls.AddRange([_sourceCompletionTitle, _sourceCompletionBody, _sourceNext, _viewSourceChanges]);
        AddSource(_sourceCompletionCard); _sourceCompletionCard.Visible = false;
        Add(_notice); AddSource(_sourceNotice); AddImport(_importNotice);
        _notice.Dismissed += (_, _) => LayoutPage(); _sourceNotice.Dismissed += (_, _) => LayoutSourcePage(); _importNotice.Dismissed += (_, _) => LayoutImportPage();
        _openProject.Click += (_, _) => { if (!_busy && _project != null) OpenLocation(_project.Root); };
        _openFirmware.Click += (_, _) => { if (!_busy && _completedFirmware != null) OpenLocation(Path.GetDirectoryName(_completedFirmware)!); };
        _viewLastChanges.Click += (_, _) => ViewLastChanges(_lastTaskChanges);
        _viewSourceChanges.Click += (_, _) => ViewLastChanges(_lastSourceChanges);
        _completionHelp.Click += (_, _) => ShowBuildProblems();
        _sourceNext.Click += (_, _) => { ShowWorkspacePage(0); _flow.ScrollTo(_action.Top - Px(16)); _configure.Focus(); };
        _feedbackTimer.Tick += (_, _) => UpdateElapsed();
        RefreshEmptyStates();
    }

    private void BeginFeedback(string operation, Button button, string text)
    {
        _operation = operation; _activeButton = button; _activeButtonText = button.Text;
        button.Text = text; ((SoftButton)button).IsBusy = true;
        _operationClock.Restart(); _feedbackTimer.Start(); UpdateElapsed();
        if (operation == "configure")
        {
            _completionShown = false; _completionCard.Visible = false; _notice.Visible = false;
            _workflowSteps.Reset("检查环境", "写入任务", "配置 CMake", "编译", "检查脚本");
            _workflowSteps.SetStep(0, StepState.Running);
        }
        if (operation == "source")
        {
            _sourceCompletionShown = false; _sourceCompletionCard.Visible = false; _sourceNotice.Visible = false;
            _sourceSteps.Reset("检查环境", "写入配置", "配置 CMake", "编译验证"); _sourceSteps.SetStep(0, StepState.Running);
        }
        if (operation == "import")
        {
            _importNotice.Visible = false; _importSteps.Reset("核对文件", "复制校验", "完成"); _importSteps.SetStep(0, StepState.Complete); _importSteps.SetStep(1, StepState.Running);
        }
        if (operation == "load") ResetProjectFeedback();
        if (operation == "repair") InvalidateCompletion(resetWorkflow: false);
        if (operation is "configure" or "repair") { LayoutPage(); _flow.ScrollTo(_output.Top - Px(16)); }
        if (operation == "source") { LayoutSourcePage(); _sources.ScrollTo(_sourceAction.Top - Px(16)); }
    }

    private void EndFeedback()
    {
        _operationClock.Stop(); _feedbackTimer.Stop(); UpdateElapsed();
        if (_activeButton != null)
        {
            ((SoftButton)_activeButton).IsBusy = false; _activeButton.Text = _activeButtonText;
        }
        _activeButton = null; _operation = "";
        _stopRepair.Text = "停止下载";
        UpdateActions(); LayoutPage(); LayoutSourcePage(); LayoutImportPage();
    }
    private void UpdateElapsed()
    {
        var time = _operationClock.Elapsed;
        var text = $"{(_operationClock.IsRunning ? "已用时" : "用时")} {time.Minutes + time.Hours * 60:00}:{time.Seconds:00}";
        if (_operation is "configure" or "repair" or "load" or "detect" or "preset") _elapsed.Text = text;
        if (_operation == "source") _sourceElapsed.Text = text;
        if (_operation == "import") _importElapsed.Text = text;
    }
    private void FeedbackStep(int index, StepState state)
    {
        if (_operation == "source") _sourceSteps.SetStep(index, state); else if (_operation == "configure") _workflowSteps.SetStep(index, state);
    }
    private void FinishActiveSteps(StepState state)
    {
        var steps = _operation == "source" ? _sourceSteps : _workflowSteps;
        for (var i = 0; i < steps.States.Count; i++) if (steps.States[i] == StepState.Running) steps.SetStep(i, state);
    }
    private void Notify(string text, bool attention = false, string? actionText = null, Action? action = null, int page = 0)
    {
        var notice = page == 1 ? _sourceNotice : page == 2 ? _importNotice : _notice;
        notice.ShowMessage(text, attention, actionText, action == null ? null : () => { if (!_busy) action(); });
        if (page == 1) { LayoutSourcePage(); _sources.ScrollTo(0); }
        else if (page == 2) { LayoutImportPage(); _imports.ScrollTo(0); }
        else { LayoutPage(); _flow.ScrollTo(0); }
    }
    private void ShowCompletion(string title, string body, string? firmware = null, bool attention = false, bool tasksReady = false)
    {
        _completionShown = true; _completionCard.Visible = true;
        _completionTitle.Text = (attention ? "!  " : "✓  ") + title;
        _completionTitle.ForeColor = attention ? Color.FromArgb(160, 91, 32) : Color.FromArgb(28, 136, 100);
        _completionBody.Text = body; _completedFirmware = firmware;
        _openFirmware.Visible = firmware != null; _viewLastChanges.Visible = _lastTaskChanges.Count > 0;
        _completionHelp.Visible = attention || _buildProblems.Count > 0;
        _openProject.Visible = _project != null;
        if (tasksReady) _completionBody.Text += "\n下一步：在 VS Code 的“终端 → 运行任务”选择“一键编译”或“一键烧录(DAPLINK)”。";
        LayoutPage(); _flow.ScrollTo(_completionCard.Top - Px(16));
    }
    private void ShowSourceCompletion(string title, string body, bool attention = false)
    {
        _sourceCompletionShown = true; _sourceCompletionCard.Visible = true;
        _sourceCompletionTitle.Text = (attention ? "!  " : "✓  ") + title;
        _sourceCompletionTitle.ForeColor = attention ? Color.FromArgb(160, 91, 32) : Color.FromArgb(28, 136, 100);
        _sourceCompletionBody.Text = body; _sourceNext.Visible = !attention; _viewSourceChanges.Visible = _lastSourceChanges.Count > 0;
        LayoutSourcePage(); _sources.ScrollTo(_sourceCompletionCard.Top - Px(16));
    }
    private string FirmwareSummary(string path, string configuration)
    {
        var file = new FileInfo(path);
        return $"配置：{configuration}\n固件：{file.Name}\n位置：{file.FullName}\n大小：{file.Length / 1024.0:N1} KB · 文件时间：{file.LastWriteTime:yyyy-MM-dd HH:mm:ss}";
    }
    private void ViewLastChanges(IReadOnlyList<FileChange> changes)
    {
        if (_busy || _project == null || changes.Count == 0) return;
        using var dialog = new ChangePreviewDialog(_project.Root, "查看本次修改", changes, "关闭"); dialog.ShowDialog(this);
    }
    private void OpenLocation(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { Notify("无法打开位置：" + ex.Message, true); }
    }
    private void RefreshEmptyStates()
    {
        var ready = _project?.IsCMakeProject == true;
        _sourceChooseProject.Visible = _importChooseProject.Visible = !ready;
        _sourcePick.Visible = _sourceFiles.Visible = _sourceAction.Visible = ready;
        _importDetails.Visible = _importFiles.Visible = _importResult.Visible = ready;
        _projectOptionsCard.Visible = _details.Visible = ready;
        _output.Visible = ready || _operation is "repair" or "detect";
        _completionCard.Visible = _completionShown;
        _sourceCompletionCard.Visible = ready && _sourceCompletionShown;
        _heroSubtitle.Text = ready ? "检查构建环境，配置 VS Code 一键编译与 DAPLink 烧录。" : "从选择工程开始；检测完成后，再决定补齐环境或配置任务。";
        _sourceHero.Controls.OfType<Label>().Skip(1).First().Text = ready ? "勾选 C/C++、汇编和头文件，预览后添加到 CMake 构建目标。" : "先选择一个 CMake 工程，随后可以浏览文件夹并逐项勾选需要参与构建的文件。";
        _importHero.Controls.OfType<Label>().Skip(1).First().Text = ready ? "创建工程内文件夹，将选定文件复制进去。" : "先选择工程，再输入目标文件夹名称并选择要加入的文件。";
    }
    private void UpdateSelectionSummary()
    {
        _sourceCount.Text = $"共 {_sourcePreview.Count} 个 · 已选 {_sourcePreview.CheckedPaths.Count} 个 · 待加入 {_sourcePreview.SelectedPendingCount} 个\n" +
            $"当前显示 {_sourcePreview.VisibleFileCount} 个 · 已引用 {_sourcePreview.RegisteredFileCount} 个" +
            (_sourcePreview.HiddenCheckedCount > 0 ? $" · 隐藏的已选 {_sourcePreview.HiddenCheckedCount} 个（仍会加入）" : "");
    }
    private void ContinueWithImportedFiles()
    {
        if (_busy || _lastImportedFolder == null || _project == null) return;
        ShowWorkspacePage(1); _sourceFolder.Text = _lastImportedFolder; _sourcePreviewFolder = null;
        _sourceSearch.Clear(); _sourceFilter.SelectedIndex = 0; RefreshSourcePreview();
        var copied = _lastImportedFiles.Select(x => Path.GetRelativePath(_project.Root, x).Replace('\\', '/')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidates = _sourcePlan?.CandidateFiles.ToArray() ?? [];
        // Retain the files previously configured by this tool when adding a new batch.
        if (_sourcePlan != null) copied.UnionWith(ManagedSelections(_sourcePlan));
        _sourcePreview.SetAll(false);
        for (var i = 0; i < _sourcePreview.Count; i++)
            if (i < candidates.Length && copied.Contains(candidates[i])) _sourcePreview.SetChecked(i, true);
        UpdateSourceSelection();
        Notify("已定位刚加入的文件，预选本次新增的源文件和头文件，保留之前已配置的文件。请核对目标和勾选清单。", page: 1);
    }

    private void ShowDetails()
    {
        ShowWorkspacePage(0); if (!_expanded) ToggleDetails(); _flow.ScrollTo(_details.Top - Px(14));
    }
    private void InvalidateCompletion(bool resetWorkflow = true)
    {
        if (!_conversionRunning && _project != null && _conversionRoot != null &&
            Path.GetFullPath(_project.Root).Equals(Path.GetFullPath(_conversionRoot), StringComparison.OrdinalIgnoreCase))
            InvalidateConversionTargets();
        _completionShown = _sourceCompletionShown = false; _completedFirmware = null;
        if (_completionCard == null) return;
        _completionCard.Visible = _sourceCompletionCard.Visible = false;
        _firmwareLabel.Text = "配置有变化，请重新配置并验证，以确认当前固件和 VS Code 任务。";
        if (resetWorkflow)
        {
            _workflowSteps.Reset("检查环境", "写入任务", "配置 CMake", "编译", "检查脚本");
            _progress.Value = 0; _elapsed.Text = "";
            if (_operation == "") { _sourceSteps.Reset("检查环境", "写入配置", "配置 CMake", "编译验证"); _sourceProgress.Value = 0; _sourceElapsed.Text = ""; }
        }
        LayoutPage(); LayoutSourcePage();
    }
    private void ResetProjectFeedback()
    {
        InvalidateCompletion(); _lastTaskChanges = _lastSourceChanges = [];
        _sourceSteps.Reset("检查环境", "写入配置", "配置 CMake", "编译验证");
        _importSteps.Reset("核对文件", "复制校验", "完成");
        _elapsed.Text = _sourceElapsed.Text = _importElapsed.Text = "";
        _sourceProgress.Value = _importProgress.Value = 0;
        _log.Clear(); _sourceLog.Clear(); _importLog.Clear(); _buildOutput.Clear(); _buildProblems.Clear();
    }
    private string ProjectConfigurationFingerprint()
    {
        if (_project == null) return "";
        return string.Join("|", new[] { Path.Combine(_project.Root, "CMakeLists.txt"), Path.Combine(_project.Root, ".vscode", "tasks.json") }
            .Select(path => File.Exists(path) ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))) : "missing"));
    }
    private static IEnumerable<string> ManagedSelections(SourceFolderPlan plan)
    {
        foreach (System.Text.RegularExpressions.Match block in System.Text.RegularExpressions.Regex.Matches(plan.OriginalText,
            @"(?ms)^# BEGIN CMakeDapLink managed folders\r?\n.*?^# END CMakeDapLink managed folders"))
        {
            var lines = block.Value.Split('\n').Select(x => x.TrimEnd('\r')).ToArray();
            if (!lines.Contains("# target: " + plan.Target, StringComparer.Ordinal)) continue;
            foreach (var line in lines.Where(x => x.StartsWith("# selected-file: ", StringComparison.Ordinal))) yield return line[17..];
        }
    }
    private async Task RescanAsync(bool showIssues = true)
    {
        if (_busy) return;
        BeginFeedback("detect", _rescan, "检测中…");
        _workflowSteps.Reset("检测工具", "检查脚本", "汇总结果"); _workflowSteps.SetStep(0, StepState.Running);
        SetBusy(true, "检测工具版本和配置…", 10);
        try
        {
            await RefreshEnvironmentAsync(showIssues);
            for (var i = 0; i < 3; i++) _workflowSteps.SetStep(i, StepState.Complete);
            _stage.Text = "环境检测完成，请选择下一步操作。"; _progress.Value = 100;
        }
        catch (Exception ex)
        {
            _workflowSteps.SetStep(0, StepState.Attention); _stage.Text = "检测未完成：" + ex.Message;
            Notify(_stage.Text, true, "查看配置详情", ShowDetails);
        }
        finally { _busy = false; EndFeedback(); }
    }
}
