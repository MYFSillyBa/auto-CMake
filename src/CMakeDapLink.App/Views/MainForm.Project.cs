using CMakeDapLink.Core;

namespace CMakeDapLink.App;

public sealed partial class MainForm
{
    private RoundedPanel _projectOptionsCard = null!;
    private readonly TargetPicker _presetPicker = new();
    private readonly Label _presetLabel = Label("构建预设", 9, Muted);
    private readonly TargetPicker _configurationPicker = new();
    private readonly TargetPicker _buildPresetPicker = new();
    private readonly Label _buildPresetLabel = Label("编译预设", 9, Muted);
    private readonly Label _chipEvidence = Label("选择工程后显示芯片识别依据和可用预设。", 9, Muted);
    private readonly Label _firmwareLabel = Label("构建后确认固件；多个 ELF 时可选择目标。", 8.5f, Muted);
    private readonly Button _identificationDetails = Button("识别详情", false);
    private readonly Button _restoreChanges = Button("恢复修改", false);
    private readonly Button _showProblems = Button("问题与日志", false);
    private readonly Button _sourceProblems = Button("问题与日志", false);
    private readonly Button _sourceRestore = Button("恢复修改", false);
    private readonly Button _navTools = Button("工具管理", false);
    private readonly RoundedTextField _sourceSearch = new();
    private readonly TargetPicker _sourceFilter = new();
    private readonly Button _expandSources = Button("展开目录", false);
    private readonly Button _collapseSources = Button("折叠目录", false);
    private readonly List<BuildProblem> _buildProblems = [];
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _buildOutput = new();
    private bool _updatingProjectOptions;
    private string? _confirmedChip;
    private string _defaultConfiguration = "Debug";

    private void BuildProjectOptions()
    {
        _projectOptionsCard = new RoundedPanel();
        _projectOptionsCard.Controls.Add(Label("工程识别与构建目标", 11, Ink, bold: true));
        _projectOptionsCard.Controls.AddRange([_chipEvidence, _presetLabel, _presetPicker, _configurationPicker, _buildPresetPicker, _buildPresetLabel, _identificationDetails, _firmwareLabel]);
        _buildPresetPicker.SelectedIndexChanged += (_, _) =>
        {
            if (_updatingProjectOptions || _busy || _project == null) return;
            InvalidateCompletion();
            var info = SelectedConfigurePreset();
            _project = _project with { BuildPreset = _buildPresetPicker.SelectedIndex <= 0 ? null : info?.BuildPresets[_buildPresetPicker.SelectedIndex - 1].Name };
            RefreshProjectOptions(); UpdateActions();
        };
        _configurationPicker.Items.AddRange(["Debug", "Release", "RelWithDebInfo", "MinSizeRel"]);
        _configurationPicker.SelectedIndexChanged += (_, _) =>
        {
            if (_updatingProjectOptions || _busy) return;
            InvalidateCompletion();
            _defaultConfiguration = _configurationPicker.SelectedItem ?? "Debug";
        };
        _identificationDetails.Click += (_, _) => ShowIdentificationDetails();
        _presetPicker.SelectedIndexChanged += async (_, _) =>
        {
            if (_updatingProjectOptions || _busy || _project == null) return;
            InvalidateCompletion();
            if (_project.ConfigurePresets.Count == 0)
            {
                _defaultConfiguration = _presetPicker.SelectedIndex == 1 ? "Release" : "Debug";
                _project = _project with { BuildDirectory = Path.Combine(_project.Root, "build", "daplink-" + _defaultConfiguration.ToLowerInvariant()) };
                await RescanAsync(showIssues: false); return;
            }
            var name = _project.ConfigurePresets.FirstOrDefault(x => PresetLabel(x) == _presetPicker.SelectedItem)?.Name;
            var root = _project.Root;
            SetBusy(true, "读取所选构建预设…", 8);
            try
            {
                _project = await Task.Run(() => ProjectInspector.Inspect(root, name));
                _confirmedChip = null;
                _target.Text = _project.TargetScript ?? "";
                _firmwareLabel.Text = "构建后确认固件；多个 ELF 时可选择目标。";
                RefreshProjectOptions(); await RefreshEnvironmentAsync();
            }
            catch (Exception ex) { Append("预设读取：" + ex); Notify("预设读取未完成：" + ex.Message, true, "查看问题与日志", ShowBuildProblems); }
            finally { SetBusy(false, "构建预设已更新，请核对识别信息。", 0); }
        };
        Add(_projectOptionsCard);
    }
    private static string PresetLabel(ConfigurePresetInfo preset) => preset.Name == preset.DisplayName ? preset.Name : preset.DisplayName + " · " + preset.Name;
    private void RefreshProjectOptions()
    {
        _updatingProjectOptions = true;
        try
        {
            _presetPicker.Items.Clear();
            if (_project?.ConfigurePresets.Count > 0)
            {
                foreach (var preset in _project.ConfigurePresets) _presetPicker.Items.Add(PresetLabel(preset));
                _presetPicker.SelectedIndex = Math.Max(0, _project.ConfigurePresets.ToList().FindIndex(x => x.Name == _project.ConfigurePreset));
            }
            else { _presetPicker.Items.AddRange(["Debug · 默认构建", "Release · 默认构建"]); _presetPicker.SelectedIndex = _defaultConfiguration == "Release" ? 1 : 0; }
            _chipEvidence.Text = _project == null ? "选择工程后显示芯片识别依据和可用预设。" :
                (_project.Chip ?? "芯片待确认") + "  ·  " + _project.ChipEvidence;
            var info = SelectedConfigurePreset();
            _buildPresetPicker.Items.Clear(); _buildPresetPicker.Items.Add("直接构建目录");
            foreach (var choice in info?.BuildPresets ?? []) _buildPresetPicker.Items.Add(choice.DisplayName == choice.Name ? choice.Name : choice.DisplayName + " · " + choice.Name);
            _buildPresetPicker.SelectedIndex = _project?.BuildPreset == null ? 0 : Math.Max(0, (info?.BuildPresets.ToList().FindIndex(x => x.Name == _project.BuildPreset) ?? -1) + 1);
            _buildPresetPicker.Visible = _buildPresetLabel.Visible = info?.BuildPresets.Count > 0;
            var configuration = SelectedFixedConfiguration() ?? _defaultConfiguration;
            if (!_configurationPicker.Items.Contains(configuration)) _configurationPicker.Items.Add(configuration);
            _configurationPicker.SelectedIndex = _configurationPicker.Items.IndexOf(configuration);
            _configurationPicker.Visible = _project?.ConfigurePresets.Count > 0;
        }
        finally { _updatingProjectOptions = false; }
        LayoutPage();
    }
    private int LayoutProjectOptions(int width, int inset)
    {
        _projectOptionsCard.Width = width;
        var title = _projectOptionsCard.Controls.OfType<Label>().First();
        var top = HeaderRow(title, _identificationDetails, width, inset, Px(16));
        Fit(_chipEvidence, inset, top + Px(8), width - inset * 2);
        Fit(_presetLabel, inset, _chipEvidence.Bottom + Px(12), Px(96));
        var pickerWidth = width - inset * 2 - Px(106);
        var configurationWidth = _configurationPicker.Visible ? Math.Min(Px(170), pickerWidth / 3) : 0;
        _presetPicker.SetBounds(inset + Px(106), _presetLabel.Top - Px(3), Math.Max(Px(100), pickerWidth - configurationWidth - (_configurationPicker.Visible ? Px(8) : 0)), ButtonHeight(_presetPicker, 34));
        _configurationPicker.SetBounds(_presetPicker.Right + Px(8), _presetPicker.Top, configurationWidth, _presetPicker.Height);
        var pickerBottom = _presetPicker.Bottom;
        if (_buildPresetPicker.Visible)
        {
            Fit(_buildPresetLabel, inset, pickerBottom + Px(16), Px(96));
            _buildPresetPicker.SetBounds(inset + Px(106), _buildPresetLabel.Top - Px(3), width - inset * 2 - Px(106), ButtonHeight(_buildPresetPicker, 34));
            pickerBottom = _buildPresetPicker.Bottom;
        }
        Fit(_firmwareLabel, inset, pickerBottom + Px(8), width - inset * 2);
        return _projectOptionsCard.Height = _firmwareLabel.Bottom + Px(16);
    }
    private void ApplyConfirmedChip()
    {
        if (_project == null || _confirmedChip == null) return;
        _project = _project with { Chip = _confirmedChip, TargetScript = ProjectInspector.TargetForChip(_confirmedChip), ChipEvidence = "用户在识别详情中确认" };
    }
    private ConfigurePresetInfo? SelectedConfigurePreset() => _project?.ConfigurePresets.FirstOrDefault(x => x.Name == _project.ConfigurePreset);
    private string? SelectedFixedConfiguration()
    {
        var info = SelectedConfigurePreset();
        return info?.BuildPresets.FirstOrDefault(x => x.Name == _project?.BuildPreset)?.Configuration ?? info?.ConfigureConfiguration;
    }
    private void ShowIdentificationDetails()
    {
        if (_project == null) return;
        using var dialog = new ProjectIdentificationDialog(_project);
        if (dialog.ShowDialog(this) == DialogResult.OK && dialog.ConfirmedChip != null)
        {
            InvalidateCompletion();
            _confirmedChip = dialog.ConfirmedChip; ApplyConfirmedChip();
            _target.Text = _project!.TargetScript ?? _target.Text; RefreshProjectOptions();
            _ = RescanAsync(showIssues: false);
        }
    }
    private void ShowChangeHistory()
    {
        if (_busy || _project == null) return;
        var before = ProjectConfigurationFingerprint();
        using var dialog = new RestoreHistoryDialog(_project.Root);
        dialog.ShowDialog(this);
        if (before != ProjectConfigurationFingerprint()) InvalidateCompletion();
        _project = ProjectInspector.Inspect(_project.Root, _project.ConfigurePreset); ApplyConfirmedChip();
        RefreshProjectOptions(); RefreshSourcePreview(); _ = RescanAsync(showIssues: false);
    }
    private void ShowBuildProblems()
    {
        using var dialog = new BuildProblemsDialog(_buildProblems.Distinct().ToArray(), string.Join(Environment.NewLine, _buildOutput));
        dialog.ShowDialog(this);
    }
    private async Task ShowToolsAsync()
    {
        if (_busy) return;
        using var dialog = new ToolManagementDialog(_tools);
        var before = _tools;
        dialog.ShowDialog(this); _tools = dialog.Tools;
        if (_tools != before) InvalidateCompletion();
        await RescanAsync(showIssues: false);
    }
    private void BuildSourceFilters()
    {
        _sourceSearch.PlaceholderText = "搜索文件名或相对路径";
        _sourceSearch.TextChanged += (_, _) => FilterSources();
        _sourceFilter.Items.AddRange(["全部文件", "仅已勾选", "仅未勾选", "原有配置已引用"]);
        _sourceFilter.SelectedIndex = 0;
        _sourceFilter.SelectedIndexChanged += (_, _) => FilterSources();
        _expandSources.Click += (_, _) => _sourcePreview.ExpandAll();
        _collapseSources.Click += (_, _) => _sourcePreview.CollapseAll();
        _sourceFiles.Controls.AddRange([_sourceSearch, _sourceFilter, _expandSources, _collapseSources]);
    }
    private void FilterSources()
    {
        _sourcePreview.SetFilter(_sourceSearch.Text, Math.Max(0, _sourceFilter.SelectedIndex));
        UpdateSelectionSummary();
        LayoutSourcePage();
    }
    private string? ChooseFirmware(IReadOnlyList<string> files, string root)
    {
        if (files.Count == 0) throw new InvalidOperationException("构建目录中没有 ELF 固件。请核对目标输出扩展名与构建目录。");
        if (files.Count == 1) return files[0];
        using var dialog = new FirmwarePickerDialog(files, root);
        return dialog.ShowDialog(this) == DialogResult.OK ? dialog.SelectedPath : null;
    }
}
