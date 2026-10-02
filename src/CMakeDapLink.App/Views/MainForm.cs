using System.Drawing.Drawing2D;
using System.Text.RegularExpressions;
using CMakeDapLink.Core;

namespace CMakeDapLink.App;

public sealed partial class MainForm : Form
{
    private static readonly Color Background = Color.FromArgb(246, 247, 249);
    private static readonly Color Surface = Color.White;
    private static readonly Color Ink = Color.FromArgb(34, 43, 57);
    private static readonly Color Muted = Color.FromArgb(116, 128, 146);
    private static readonly Color Border = Color.FromArgb(225, 230, 237);
    private static readonly Color Accent = Color.FromArgb(37, 99, 235);
    private static readonly Color AccentDark = Color.FromArgb(38, 82, 161);
    private readonly Panel _sidebar = new() { BackColor = Color.FromArgb(251, 252, 253) };
    private readonly Panel _workspace = new() { BackColor = Background };
    private readonly ScrollPage _flow = new() { Dock = DockStyle.Fill, BackColor = Background, Name = "SetupPage" };
    private readonly ScrollPage _sources = new() { Dock = DockStyle.Fill, BackColor = Background, Visible = false, Name = "SourcePage" };
    private readonly ScrollPage _imports = new() { Dock = DockStyle.Fill, BackColor = Background, Visible = false, Name = "ImportPage" };
    private readonly Button _navSetup = Button("工程配置", false);
    private readonly Button _navSources = Button("添加源文件", false);
    private readonly Button _navImport = Button("加入文件", false);
    private readonly ToolTip _navTips = new();
    private readonly Label _sideBrand = Label("STM32", 13, Ink, bold: true);
    private readonly Label _sideHint = Label("工程助手\nCMake · Keil · DAPLink", 8.5f, Muted);
    private readonly Label _sideFoot = Label("本地工作区", 8.5f, Muted);
    private readonly LineIcon _brandIcon = new(1) { BackColor = Color.Transparent };
    private readonly Panel _sideDivider = new() { BackColor = Border };
    private readonly List<Control> _cards = [];
    private readonly Dictionary<string, TextBox> _toolBoxes = [];
    private readonly Dictionary<string, Button> _toolButtons = [];
    private readonly Dictionary<string, Label> _toolLabels = [];
    private readonly Label _folder = Label("选择包含 CMakeLists.txt 的文件夹", 9, Muted);
    private readonly Label _result = Label("请选择工程，开始检查构建环境。", 9, Muted);
    private readonly Label _stage = Label("等待选择工程", 9, Muted);
    private readonly TextBox _target = Input(false);
    private readonly RichTextBox _log = new() { ReadOnly = true, BorderStyle = BorderStyle.None,
        BackColor = Color.FromArgb(24, 32, 45), ForeColor = Color.FromArgb(205, 216, 231), Font = new Font("Consolas", 9), ScrollBars = RichTextBoxScrollBars.Vertical };
    private readonly ProgressStrip _progress = new();
    private readonly Button _configure = Button("配置并验证", true);
    private readonly Button _repair = Button("手动指定路径", false);
    private readonly Button _environmentHelp = Button("查看补全说明", false);
    private readonly Label _readiness = Label("选择工程后显示环境状态", 9, Muted);
    private readonly Dictionary<string, string> _toolFailures = [];
    private readonly Button _rescan = Button("重新检测", false);
    private readonly Button _toggle = Button("展开配置详情", false);
    private readonly Label _heroTitle = Label("工程工作台", 18, Ink, bold: true);
    private readonly Label _heroSubtitle = Label("检查构建环境，配置 VS Code 一键编译与 DAPLink 烧录。", 9, Muted);
    private readonly Label _dropTitle = Label("打开 CMake 工程", 10.5f, Ink, bold: true);
    private readonly Button _browseProject = Button("选择工程", false);
    private readonly LineIcon _folderIcon = new(2) { BackColor = Color.Transparent };
    private readonly Label _actionNote = Label("生成一键编译和一键烧录任务。\n验证阶段只编译和检查配置。", 9, Muted);
    private readonly Dictionary<string, ToolTile> _toolTiles = [];
    private readonly Label _sourceProject = Label("先在“工程配置”中选择工程", 9, Muted);
    private readonly Label _sourceFolder = Label("尚未选择文件夹", 9, Muted);
    private readonly Label _sourceCount = Label("选择文件夹后预览源文件和头文件", 9, Muted);
    private readonly FileSelectionList _sourcePreview = new();
    private readonly Button _selectAllSources = Button("全选", false);
    private readonly Button _selectNoSources = Button("全不选", false);
    private readonly Label _sourceIncludes = Label("勾选 .h 文件后自动添加其所在目录。", 8.5f, Muted);
    private readonly TargetPicker _sourceTarget = new();
    private readonly Button _chooseSource = Button("选择工程内文件夹", true);
    private readonly Button _applySource = Button("添加到 CMake 并验证", true);
    private readonly Label _sourceStatus = Label("仅修改工程根目录的 CMakeLists.txt；写入前会生成备份。", 9, Muted);
    private readonly Label _sourceFolderHint = Label("在右侧勾选需要添加的 C/C++、汇编和头文件。头文件目录随勾选更新；工程原有 CMake 配置保留。", 9, Muted);
    private readonly ProgressStrip _sourceProgress = new();
    private readonly RichTextBox _sourceLog = new() { ReadOnly = true, BorderStyle = BorderStyle.None,
        BackColor = Color.FromArgb(24, 32, 45), ForeColor = Color.FromArgb(205, 216, 231), Font = new Font("Consolas", 9), ScrollBars = RichTextBoxScrollBars.Vertical };
    private readonly List<Control> _sourceCards = [];
    private SourceFolderPlan? _sourcePlan;
    private string? _sourcePreviewFolder;
    private RoundedPanel _sourceHero = null!;
    private RoundedPanel _sourcePick = null!;
    private RoundedPanel _sourceFiles = null!;
    private RoundedPanel _sourceAction = null!;
    private RoundedPanel _details = null!;
    private RoundedPanel _summary = null!;
    private RoundedPanel _drop = null!;
    private RoundedPanel _hero = null!;
    private RoundedPanel _action = null!;
    private RoundedPanel _output = null!;
    private Label _detailsHint = null!;
    private ToolPaths _tools = new(null, null, null, null, null);
    private ProjectInfo? _project;
    private bool _busy;
    private bool _expanded;
    private bool _layingOut;
    private bool _layingOutSources;
    private readonly float? _previewScale;

    public MainForm(float? previewScale = null)
    {
        _previewScale = previewScale;
        AutoScaleMode = AutoScaleMode.None;
        Text = "STM32 工程助手 · CMake / Keil MDK / DAPLink";
        Icon = AppIcon.Chip;
        MinimumSize = new Size(640, 580);
        var workArea = Screen.FromControl(this).WorkingArea;
        Size = new Size(Math.Min(Px(1180), workArea.Width - Px(48)), Math.Min(Px(820), workArea.Height - Px(48)));
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Background;
        ForeColor = Ink;
        Font = new Font("Microsoft YaHei UI", 9);
        AllowDrop = true;
        DragEnter += OnDragEnter;
        DragDrop += OnDragDrop;
        _workspace.Controls.Add(_flow);
        _workspace.Controls.Add(_sources);
        _workspace.Controls.Add(_imports);
        _workspace.Controls.Add(_conversionPage);
        Controls.Add(_workspace);
        Controls.Add(_sidebar);
        BuildSidebar();
        BuildContent();
        BuildSourcePage();
        BuildImportPage();
        BuildConversionPage();
        _sourcePreview.ShowRegistrationStatus = true;
        BuildFeedback();
        if (previewScale.HasValue)
        {
            var ratio = previewScale.Value / (DeviceDpi / 96f);
            var fonts = AllControls(this).Select(x => (Control: x, Font: x.Font)).ToArray();
            foreach (var item in fonts) item.Control.Font = new Font(item.Font.FontFamily, item.Font.Size * ratio, item.Font.Style);
        }
        _flow.Resize += (_, _) => LayoutPage();
        _sources.Resize += (_, _) => LayoutSourcePage();
        _imports.Resize += (_, _) => LayoutImportPage();
        _conversionPage.Resize += (_, _) => LayoutConversionPage();
        Resize += (_, _) => LayoutShell();
        DpiChanged += (_, _) => BeginInvoke(LayoutShell);
        LayoutShell();
        Shown += async (_, _) => await RescanAsync(showIssues: false);
    }

    private void BuildSidebar()
    {
        foreach (var item in new Control[] { _brandIcon, _sideBrand, _sideHint, _navSetup, _navSources, _navImport, _navConversion, _navTools, _sideFoot, _sideDivider }) _sidebar.Controls.Add(item);
        _navSetup.Name = "NavSetup"; _navSources.Name = "NavSources";
        _navSetup.TextAlign = _navSources.TextAlign = ContentAlignment.MiddleLeft;
        ((SoftButton)_navSetup).IconKind = 1;
        ((SoftButton)_navSources).IconKind = 2;
        ((SoftButton)_navImport).IconKind = 3;
        _navImport.Name = "NavImport"; _navImport.AccessibleName = "加入文件";
        _navTools.Name = "NavTools"; _navTools.AccessibleName = "工具管理";
        ((SoftButton)_navTools).IconKind = 1;
        ((SoftButton)_navConversion).IconKind = 2;
        _navConversion.Name = "NavConversion"; _navConversion.AccessibleName = "工程转换";
        _navTools.ForeColor = Muted; _navTools.BackColor = _sidebar.BackColor;
        _navTools.Click += async (_, _) => await ShowToolsAsync();
        _navImport.FlatAppearance.BorderSize = 0;
        _navSetup.FlatAppearance.BorderSize = _navSources.FlatAppearance.BorderSize = 0;
        _navSetup.AccessibleName = "工程配置"; _navSources.AccessibleName = "添加源文件";
        _navSetup.Click += (_, _) => ShowPage(false);
        _navSources.Click += (_, _) => ShowPage(true);
        _navImport.Click += (_, _) => ShowWorkspacePage(2);
        _navConversion.Click += async (_, _) => { ShowWorkspacePage(3); if (_conversionRoot == null && _project?.IsCMakeProject == true) await LoadConversionProjectAsync(_project.Root); };
        foreach (var button in new[] { _navSetup, _navSources, _navImport, _navConversion, _navTools }) _navTips.SetToolTip(button, button.AccessibleName);
        ShowPage(false);
    }

    private void ShowPage(bool sources)
        => ShowWorkspacePage(sources ? 1 : 0);

    private void ShowWorkspacePage(int page)
    {
        var pages = new[] { _flow, _sources, _imports, _conversionPage };
        var buttons = new[] { _navSetup, _navSources, _navImport, _navConversion };
        for (var i = 0; i < pages.Length; i++)
        {
            pages[i].Visible = i == page;
            buttons[i].BackColor = i == page ? Color.FromArgb(232, 240, 255) : _sidebar.BackColor;
            buttons[i].ForeColor = i == page ? Accent : Muted;
        }
        pages[page].BringToFront();
        RefreshWorkspaceFooter();
        if (page == 1) LayoutSourcePage(); else if (page == 2) LayoutImportPage(); else if (page == 3) LayoutConversionPage(); else LayoutPage();
    }

    private void LayoutShell()
    {
        var compact = ClientSize.Width < Px(840);
        var sideWidth = compact ? Px(64) : Px(186);
        _sidebar.Bounds = new Rectangle(0, 0, sideWidth, ClientSize.Height);
        _workspace.Bounds = new Rectangle(sideWidth, 0, Math.Max(1, ClientSize.Width - sideWidth), ClientSize.Height);
        _brandIcon.SetBounds(Px(compact ? 17 : 17), Px(22), Px(30), Px(30));
        _sideBrand.Visible = !compact;
        Fit(_sideBrand, Px(58), Px(18), Math.Max(1, sideWidth - Px(70)));
        _sideHint.Visible = !compact;
        Fit(_sideHint, Px(18), Px(63), sideWidth - Px(32));
        _navSetup.SetBounds(Px(10), Px(114), sideWidth - Px(20), ButtonHeight(_navSetup, 39));
        _navSources.SetBounds(Px(10), _navSetup.Bottom + Px(7), sideWidth - Px(20), ButtonHeight(_navSources, 39));
        _navImport.SetBounds(Px(10), _navSources.Bottom + Px(7), sideWidth - Px(20), ButtonHeight(_navImport, 39));
        _navConversion.SetBounds(Px(10), _navImport.Bottom + Px(7), sideWidth - Px(20), ButtonHeight(_navConversion, 39));
        _navTools.SetBounds(Px(10), _navConversion.Bottom + Px(7), sideWidth - Px(20), ButtonHeight(_navTools, 39));
        _navSetup.Text = compact ? "" : "工程配置";
        _navSources.Text = compact ? "" : "添加源文件";
        _navImport.Text = compact ? "" : "加入文件";
        _navConversion.Text = compact ? "" : "工程转换";
        _navTools.Text = compact ? "" : "工具管理";
        foreach (var button in new[] { _navSetup, _navSources, _navImport, _navConversion, _navTools })
        {
            ((SoftButton)button).IconOnly = compact;
            button.TextAlign = compact ? ContentAlignment.MiddleCenter : ContentAlignment.MiddleLeft;
        }
        _sideFoot.Visible = !compact && ClientSize.Height > Px(340);
        Fit(_sideFoot, Px(24), Math.Max(1, ClientSize.Height - Px(50)), sideWidth - Px(48));
        _sideDivider.SetBounds(sideWidth - 1, 0, 1, ClientSize.Height);
        LayoutPage(); LayoutSourcePage(); LayoutImportPage(); LayoutConversionPage();
    }

    private int Px(float value) => Math.Max(1, (int)Math.Round(value * (_previewScale ?? DeviceDpi / 96f)));
    private int Fit(Label label, int left, int top, int width)
    {
        var height = MeasureLabel(label, width);
        label.SetBounds(left, top, Math.Max(1, width), height);
        return label.Bottom;
    }
    private int ButtonHeight(Button button, int minimum = 36) => Math.Max(Px(minimum),
        TextRenderer.MeasureText(button.Text, button.Font, new Size(10000, 10000), TextFormatFlags.SingleLine).Height + Px(16));
    private int ButtonWidth(Button button, int minimum = 100) => Math.Max(Px(minimum),
        TextRenderer.MeasureText(button.Text, button.Font, new Size(10000, 10000), TextFormatFlags.SingleLine).Width +
        Px(button is SoftButton { IsBusy: true } ? 55 : 30));
    private static IEnumerable<Control> AllControls(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (var descendant in AllControls(child)) yield return descendant;
        }
    }

    private void BuildContent()
    {
        _hero = new RoundedPanel(hero: true) { Height = 76 };
        _heroTitle.Location = new Point(26, 20); _hero.Controls.Add(_heroTitle);
        _heroSubtitle.Left = 28; _hero.Controls.Add(_heroSubtitle);
        Add(_hero);

        _drop = new RoundedPanel(dashed: true) { Height = 86, Cursor = Cursors.Hand, AllowDrop = true };
        _dropTitle.Location = new Point(82, 24); _dropTitle.Height = 31;
        _folder.Location = new Point(84, 65); _folder.Height = 30; _folder.AutoEllipsis = true;
        _drop.Controls.Add(_folderIcon); _drop.Controls.Add(_dropTitle); _drop.Controls.Add(_folder); _drop.Controls.Add(_browseProject);
        _drop.Click += (_, _) => BrowseProject();
        _drop.DragEnter += OnDragEnter; _drop.DragDrop += OnDragDrop;
        foreach (Control child in _drop.Controls)
        {
            child.Click += (_, _) => BrowseProject();
            child.AllowDrop = true;
            child.DragEnter += OnDragEnter; child.DragDrop += OnDragDrop;
        }
        Add(_drop);
        BuildProjectOptions();

        _summary = new RoundedPanel { Height = 196 };
        _summary.Controls.Add(Label("环境检查", 11, Ink, new Point(24, 19), new Size(250, 31), bold: true));
        _rescan.Size = new Size(112, 32); _rescan.Top = 18;
        _rescan.Click += async (_, _) => await RescanAsync();
        _summary.Controls.Add(_rescan);
        _result.Location = new Point(24, 65); _result.Height = 108;
        _summary.Controls.Add(_result);
        foreach (var name in new[] { "CMake", "Ninja", "ARM GCC", "OpenOCD" })
        {
            var tile = new ToolTile(name);
            _toolTiles[name] = tile;
            _summary.Controls.Add(tile);
        }
        Add(_summary);

        _details = new RoundedPanel { Height = 72 };
        _details.Controls.Add(Label("工具路径与烧录设置", 11, Ink, new Point(24, 12), new Size(260, 28), bold: true));
        _detailsHint = Label("检查或替换工具路径，调整 OpenOCD 目标。", 9, Muted, new Point(24, 44), new Size(480, 22));
        _details.Controls.Add(_detailsHint);
        _toggle.Size = new Size(140, 32); _toggle.Top = 19;
        _toggle.Click += (_, _) => ToggleDetails();
        _details.Controls.Add(_toggle);
        var rows = new[] { ("CMake", "CMake"), ("Ninja", "Ninja"), ("ARM GCC", "Compiler"), ("OpenOCD", "OpenOcd"), ("脚本目录", "Scripts") };
        foreach (var (name, key) in rows)
        {
            var label = Label(name, 9, Muted, bounds: new Size(100, 26));
            var box = Input(true);
            var browse = Button("选择", false);
            browse.Size = new Size(80, 30);
            browse.Click += async (_, _) => await BrowseToolAsync(key);
            _toolLabels[key] = label; _toolBoxes[key] = box; _toolButtons[key] = browse;
            _details.Controls.Add(label); _details.Controls.Add(box); _details.Controls.Add(browse);
        }
        var targetLabel = Label("Target 脚本", 9, Muted, bounds: new Size(100, 26));
        targetLabel.Name = "TargetLabel";
        _details.Controls.Add(targetLabel);
        _details.Controls.Add(_target);
        _target.TextChanged += (_, _) => { if (_action != null) { if (!_busy) { _targetScriptResolution = null; _toolFailures.Remove("Target"); InvalidateCompletion(); } UpdateActions(); LayoutPage(); } };
        SetDetailChildrenVisible(false);
        Add(_details);

        _action = new RoundedPanel { Height = 138 };
        _action.Controls.Add(Label("配置与验证", 11, Ink, new Point(24, 17), new Size(240, 31), bold: true));
        _actionNote.Location = new Point(24, 51); _actionNote.Height = 24; _action.Controls.Add(_actionNote);
        _configure.Location = new Point(24, 89); _configure.Size = new Size(154, 34);
        _configure.Click += async (_, _) => await ConfigureAsync(); _action.Controls.Add(_configure);
        _repair.Location = new Point(190, 89); _repair.Size = new Size(150, 34);
        _repair.Click += async (_, _) => await RepairAsync(); _action.Controls.Add(_repair);
        _action.Controls.Add(_readiness);
        _environmentHelp.Click += (_, _) => ShowEnvironmentHelp();
        _action.Controls.Add(_environmentHelp);
        _action.Controls.AddRange([_autoRepair, _mirrorSource]);
        _autoRepair.Name = "AutoRepair"; _mirrorSource.Name = "MirrorSource";
        _autoRepair.Click += async (_, _) => await AutoRepairAsync();
        _mirrorSource.Click += (_, _) => ShowMirrorSettings();
        Add(_action);

        _output = new RoundedPanel { Height = 250 };
        _output.Controls.Add(Label("构建输出", 11, Ink, new Point(24, 16), new Size(220, 31), bold: true));
        _output.Controls.AddRange([_restoreChanges, _showProblems]);
        _restoreChanges.Click += (_, _) => ShowChangeHistory();
        _showProblems.Click += (_, _) => ShowBuildProblems();
        _stage.Location = new Point(24, 53); _stage.Height = 24; _output.Controls.Add(_stage);
        _progress.Location = new Point(24, 85); _progress.Height = 8; _output.Controls.Add(_progress);
        _log.Location = new Point(24, 108); _log.Height = 119; _output.Controls.Add(_log);
        Add(_output);
        _configure.Enabled = false;
        LayoutPage();
    }

    private void BuildSourcePage()
    {
        _sourceHero = new RoundedPanel(hero: true) { Height = 138 };
        _sourceHero.Controls.Add(Label("源文件管理", 18, Ink, new Point(26, 23), new Size(700, 45), bold: true));
        _sourceHero.Controls.Add(Label("勾选 C/C++、汇编和头文件，预览后添加到 CMake 构建目标。", 9,
            Muted, new Point(28, 80), new Size(700, 30)));
        AddSource(_sourceHero);

        _sourcePick = new RoundedPanel { Height = 184 };
        _sourcePick.Controls.Add(Label("文件夹与构建目标", 11, Ink, new Point(24, 18), new Size(500, 34), bold: true));
        _sourceProject.Location = new Point(24, 57); _sourcePick.Controls.Add(_sourceProject);
        _chooseSource.Location = new Point(24, 96); _chooseSource.Size = new Size(182, 36);
        _chooseSource.Click += (_, _) => BrowseSourceFolder(); _sourcePick.Controls.Add(_chooseSource);
        _sourceFolder.Location = new Point(220, 99); _sourceFolder.AutoEllipsis = true; _sourcePick.Controls.Add(_sourceFolder);
        _sourcePick.Controls.Add(Label("CMake 目标", 9, Muted, new Point(24, 146), new Size(95, 28)));
        _sourceTarget.Location = new Point(125, 142); _sourceTarget.Height = 30;
        _sourceTarget.SelectedIndexChanged += (_, _) => RefreshSourcePreview();
        _sourcePick.Controls.Add(_sourceTarget);
        _sourcePick.Controls.Add(_sourceFolderHint);
        AddSource(_sourcePick);

        _sourceFiles = new RoundedPanel { Height = 320 };
        _sourceFiles.Controls.Add(Label("勾选文件", 11, Ink, new Point(24, 17), new Size(400, 34), bold: true));
        _sourceCount.Location = new Point(24, 53); _sourceFiles.Controls.Add(_sourceCount);
        _sourcePreview.Location = new Point(24, 87); _sourceFiles.Controls.Add(_sourcePreview);
        _sourcePreview.SelectionChanged += (_, _) => UpdateSourceSelection();
        _selectAllSources.Click += (_, _) => _sourcePreview.SetAll(true);
        _selectNoSources.Click += (_, _) => _sourcePreview.SetAll(false);
        _sourceFiles.Controls.Add(_selectAllSources); _sourceFiles.Controls.Add(_selectNoSources);
        _sourceFiles.Controls.Add(_sourceIncludes);
        BuildSourceFilters();
        AddSource(_sourceFiles);

        _sourceAction = new RoundedPanel { Height = 300 };
        _sourceAction.Controls.Add(Label("写入与构建验证", 11, Ink, new Point(24, 18), new Size(400, 34), bold: true));
        _sourceStatus.Location = new Point(24, 56); _sourceAction.Controls.Add(_sourceStatus);
        _applySource.Location = new Point(24, 105); _applySource.Size = new Size(210, 36);
        _applySource.Enabled = false;
        _applySource.Click += async (_, _) => await ApplySourceAsync();
        _sourceAction.Controls.Add(_applySource);
        _sourceProgress.Location = new Point(24, 155); _sourceProgress.Height = 8;
        _sourceAction.Controls.Add(_sourceProgress);
        _sourceLog.Location = new Point(24, 180); _sourceLog.Height = 96;
        _sourceAction.Controls.Add(_sourceLog);
        _sourceAction.Controls.AddRange([_sourceProblems, _sourceRestore]);
        _sourceProblems.Click += (_, _) => ShowBuildProblems();
        _sourceRestore.Click += (_, _) => ShowChangeHistory();
        AddSource(_sourceAction);
        LayoutSourcePage();
    }

    private void AddSource(Control card) { _sourceCards.Add(card); _sources.Canvas.Controls.Add(card); _sources.AttachCard(card); }

    private void BrowseSourceFolder()
    {
        if (_project?.IsCMakeProject != true)
        {
            _sourceStatus.Text = "请先切换到工程配置并选择 CMake 工程。";
            LayoutSourcePage(); return;
        }
        using var dialog = new FolderBrowserDialog { Description = "选择当前工程内的 C/C++、汇编或头文件文件夹",
            UseDescriptionForTitle = true, InitialDirectory = _project.Root };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        _sourceFolder.Text = dialog.SelectedPath;
        RefreshSourcePreview();
    }

    private void RefreshSourcePreview()
    {
        _sourcePlan = null;
        _applySource.Enabled = false;
        if (_project?.IsCMakeProject != true || !Directory.Exists(_sourceFolder.Text)) return;
        try
        {
            var selected = _sourcePreview.Count > 0 && string.Equals(_sourcePreviewFolder, _sourceFolder.Text, StringComparison.OrdinalIgnoreCase)
                ? _sourcePreview.CheckedPaths : null;
            _sourcePlan = SourceFolderPlanner.Preview(_project.Root, _sourceFolder.Text, _sourceTarget.SelectedItem?.ToString());
            _sourcePreview.SetFiles(_sourcePlan.CandidateFiles.Select(x => (x, x)), selected ?? _sourcePlan.SourceFiles.Concat(_sourcePlan.HeaderFiles));
            _sourcePreview.SetRegisteredPaths(_sourcePlan.AlreadyRegisteredFiles.Concat(ManagedSelections(_sourcePlan)));
            _sourcePreviewFolder = _sourceFolder.Text;
            UpdateSourceSelection();
        }
        catch (Exception ex)
        {
            _sourceCount.Text = "无法预览文件";
            _sourcePreview.SetFiles([]);
            _sourceStatus.Text = ex.Message;
        }
        UpdateActions();
        LayoutSourcePage();
    }

    private void UpdateSourceSelection()
    {
        _sourcePlan = null;
        var selected = _sourcePreview.CheckedPaths;
        UpdateSelectionSummary();
        _sourceIncludes.Text = "头文件补充 include 目录；C++/汇编按需启用对应编译语言。";
        if (selected.Count == 0) _sourceStatus.Text = "请至少勾选一个文件后再写入 CMakeLists.txt。";
        else if (_project != null)
        {
            try
            {
                _sourcePlan = SourceFolderPlanner.Preview(_project.Root, _sourceFolder.Text, _sourceTarget.SelectedItem, selected);
                _sourceIncludes.Text = _sourcePlan.IncludeDirectories.Count == 0 ? "未勾选头文件，不新增 include 目录。" :
                    $"头文件目录 {_sourcePlan.IncludeDirectories.Count} 个：" + string.Join("、", _sourcePlan.IncludeDirectories.Take(2)) +
                    (_sourcePlan.IncludeDirectories.Count > 2 ? " 等" : "");
                _sourceStatus.Text = _sourcePlan.OriginalText == _sourcePlan.UpdatedText ? "所选文件已写入，可重新验证编译。" :
                    $"已选 {_sourcePlan.SourceFiles.Count} 个源文件、{_sourcePlan.HeaderFiles.Count} 个头文件；明确已引用 {_sourcePlan.AlreadyRegisteredFiles.Count} 个。写入前可预览差异。";
                if (_sourcePlan.Notes.Count > 0) _sourceStatus.Text += "\n" + string.Join("；", _sourcePlan.Notes);
            }
            catch (Exception ex) { _sourceStatus.Text = ex.Message; }
        }
        UpdateActions(); LayoutSourcePage();
    }

    private async Task ApplySourceAsync()
    {
        if (_busy || _sourcePlan == null || _project == null) return;
        var plan = _sourcePlan;
        BeginFeedback("source", _applySource, "编译验证中…");
        _lastSourceChanges = [];
        _busy = true; UpdateActions();
        try
        {
            if (ShowBuildIssues()) return;
            FeedbackStep(0, StepState.Complete);
            FeedbackStep(1, StepState.Running);
            var options = CreateOptions(requireOpenOcd: false);
            var changes = SourceFolderPlanner.Changes(plan);
            if (changes.Count > 0)
            {
                using var preview = new ChangePreviewDialog(plan.Root, "添加源文件：" + plan.Target, changes);
                if (preview.ShowDialog(this) != DialogResult.OK)
                {
                    FinishActiveSteps(StepState.Cancelled);
                    _sourceStatus.Text = "已取消，未写入 CMakeLists.txt。";
                    Notify(_sourceStatus.Text, page: 1); return;
                }
            }
            _sourceStatus.Text = "正在写入 CMakeLists.txt…"; _sourceProgress.Value = 15; LayoutSourcePage();
            var changed = await Task.Run(() => ChangeHistory.Apply(plan.Root, "更新 CMake 源文件：" + plan.Target, changes) != null);
            if (changed) InvalidateCompletion();
            _lastSourceChanges = changes;
            FeedbackStep(1, StepState.Complete);
            Append(changed ? "已更新 CMakeLists.txt；可通过“恢复修改”撤回本次操作。" : "CMakeLists.txt 已包含所选文件。" );
            _sourceStatus.Text = "正在实际配置并编译工程…";
            var build = CMakeBuildPlan.Create(options);
            await RunBuildAsync(build);
            _sourceProgress.Value = 95;
            RefreshSourcePreview();
            _sourceStatus.Text = "验证通过：CMake 配置和编译成功。";
            _sourceProgress.Value = 100;
            Append(_sourceStatus.Text);
            var outputs = CMakeBuildPlan.FindElfs(build.BuildDirectory, build.BuildConfiguration);
            ShowSourceCompletion("源文件配置完成", $"CMake 配置和编译已通过。目标：{plan.Target}\n" +
                (outputs.Count == 1 ? FirmwareSummary(outputs[0], build.BuildConfiguration) : $"当前配置有 {outputs.Count} 个有效 ELF 产物。") +
                "\n下一步：回到工程配置，生成 VS Code 编译和烧录任务。");
        }
        catch (Exception ex)
        {
            _sourceStatus.Text = "写入或编译未通过：" + ex.Message;
            _sourceProgress.Value = 0;
            Append("添加源文件：" + ex);
            FinishActiveSteps(StepState.Attention);
            ShowSourceCompletion("源文件操作需要处理", ex.Message + "\n已写入的修改可在“恢复修改”中撤回。", attention: true);
            Notify(_sourceStatus.Text, true, "查看问题与日志", ShowBuildProblems, page: 1);
        }
        finally { _busy = false; EndFeedback(); }
    }

    private void Add(Control card) { _cards.Add(card); _flow.Canvas.Controls.Add(card); _flow.AttachCard(card); }

    private static int MeasureLabel(Label label, int width) => Math.Max(24,
        TextRenderer.MeasureText(label.Text, label.Font, new Size(Math.Max(1, width), 10000),
            TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl).Height + 3);

    private static int FontLineHeight(Label label) => TextRenderer.MeasureText("Ag国", label.Font, new Size(10000, 10000),
        TextFormatFlags.SingleLine).Height + 4;

    private int LayoutDetailRows(int width, int start)
    {
        var narrow = width < Px(780);
        var inset = Px(20);
        var rowTop = start;
        for (var i = 0; i < _toolBoxes.Count; i++)
        {
            var key = new[] { "CMake", "Ninja", "Compiler", "OpenOcd", "Scripts" }[i];
            var label = _toolLabels[key]; var box = _toolBoxes[key]; var browse = _toolButtons[key];
            browse.Size = new Size(ButtonWidth(browse, 70), ButtonHeight(browse, 34));
            if (narrow)
            {
                Fit(label, inset, rowTop, width - inset * 2);
                var y = label.Bottom + Px(6);
                browse.Location = new Point(width - inset - browse.Width, y);
                box.SetBounds(inset, y + Math.Max(0, (browse.Height - box.PreferredHeight) / 2),
                    Math.Max(1, browse.Left - inset - Px(12)), box.PreferredHeight);
                rowTop = Math.Max(box.Bottom, browse.Bottom) + Px(14);
            }
            else
            {
                browse.Location = new Point(width - inset - browse.Width, rowTop);
                Fit(label, inset, rowTop + Px(4), Px(98));
                box.SetBounds(inset + Px(112), rowTop + Math.Max(0, (browse.Height - box.PreferredHeight) / 2),
                    Math.Max(1, browse.Left - inset - Px(124)), box.PreferredHeight);
                rowTop = Math.Max(Math.Max(box.Bottom, browse.Bottom), label.Bottom) + Px(12);
            }
        }
        var targetLabel = _details.Controls.OfType<Label>().Single(x => x.Name == "TargetLabel");
        if (narrow)
        {
            Fit(targetLabel, inset, rowTop, width - inset * 2);
            _target.SetBounds(inset, targetLabel.Bottom + Px(6), width - inset * 2, _target.PreferredHeight);
        }
        else
        {
            Fit(targetLabel, inset, rowTop + Px(3), Px(98));
            _target.SetBounds(inset + Px(112), rowTop, width - inset * 2 - Px(112), _target.PreferredHeight);
        }
        return Math.Max(targetLabel.Bottom, _target.Bottom);
    }

    private void ToggleDetails()
    {
        _expanded = !_expanded;
        _toggle.Text = _expanded ? "收起配置详情" : "展开配置详情";
        SetDetailChildrenVisible(_expanded);
        LayoutPage();
    }

    private void SetDetailChildrenVisible(bool visible)
    {
        foreach (var box in _toolBoxes.Values) box.Visible = visible;
        foreach (var button in _toolButtons.Values) button.Visible = visible;
        foreach (var label in _toolLabels.Values) label.Visible = visible;
        _details.Controls.OfType<Label>().Single(x => x.Name == "TargetLabel").Visible = visible;
        _target.Visible = visible;
    }

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        if (_busy) { e.Effect = DragDropEffects.None; return; }
        if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true && e.Data.GetData(DataFormats.FileDrop) is string[] files &&
            files.Length == 1 && Directory.Exists(files[0])) e.Effect = DragDropEffects.Copy;
    }
    private async void OnDragDrop(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetData(DataFormats.FileDrop) is string[] files && files.Length == 1 && Directory.Exists(files[0]))
        {
            if (_conversionPage.Visible) await LoadConversionProjectAsync(files[0]);
            else await LoadProjectAsync(files[0]);
        }
    }
    private async void BrowseProject()
    {
        if (_busy) return;
        using var dialog = new FolderBrowserDialog { Description = "选择包含 CMakeLists.txt 的工程根目录", UseDescriptionForTitle = true };
        if (dialog.ShowDialog(this) == DialogResult.OK) await LoadProjectAsync(dialog.SelectedPath);
    }
    private async Task LoadProjectAsync(string path)
    {
        if (_busy) return;
        var completeTarget = false;
        BeginFeedback("load", _browseProject, "检查工程中…");
        _completionShown = _sourceCompletionShown = false;
        _lastTaskChanges = _lastSourceChanges = []; _completedFirmware = null;
        _notice.Visible = _sourceNotice.Visible = _importNotice.Visible = false;
        SetBusy(true, "正在检查工程结构…", 8);
        try
        {
            _project = await Task.Run(() => ProjectInspector.Inspect(path));
            _confirmedChip = null; _defaultConfiguration = "Debug";
            _firmwareLabel.Text = "构建后确认固件；多个 ELF 时可选择目标。";
            RefreshProjectOptions();
            _folder.Text = _project.Root;
            _dropTitle.Text = Path.GetFileName(_project.Root.TrimEnd(Path.DirectorySeparatorChar));
            SetAutomaticTarget(_project.TargetScript);
            _sourceProject.Text = "当前工程  ·  " + _project.Root;
            _sourceFolder.Text = "尚未选择文件夹";
            _sourcePlan = null;
            _sourcePreviewFolder = null;
            _sourceTarget.SelectedIndex = -1;
            _sourceTarget.Items.Clear();
            foreach (var target in SourceFolderPlanner.FindTargets(_project.Root)) _sourceTarget.Items.Add(target);
            if (_sourceTarget.Items.Count > 0) _sourceTarget.SelectedIndex = 0;
            _sourceCount.Text = "选择文件夹后预览 C/C++、汇编和头文件";
            _sourceIncludes.Text = "勾选 .h 文件后自动添加其所在目录。";
            _sourceStatus.Text = "选择工程内文件夹，并勾选要写入的文件。";
            _sourcePreview.SetFiles([]);
            _sourceSearch.Clear(); _sourceFilter.SelectedIndex = 0;
            ResetImportProject();
            LayoutSourcePage();
            Append("已选择工程：" + _project.Root);
            await RefreshEnvironmentAsync(showIssues: true);
            completeTarget = ShouldCompleteTargetAutomatically();
        }
        catch (Exception ex) { _result.Text = "工程检查失败：" + ex.Message; Append(_result.Text); Notify(_result.Text, true, "选择工程", BrowseProject); }
        finally { SetBusy(false, "工程检查结束，请选择下一步操作", 0); EndFeedback(); _browseProject.Text = _project == null ? "选择工程" : "更换工程"; }
        if (completeTarget) await AutoRepairAsync(openOcdOnly: true);
    }

    private async Task RefreshEnvironmentAsync(bool showIssues = false)
    {
        _tools = EnvironmentScanner.Scan(_tools);
        _toolBoxes["CMake"].Text = _tools.CMake ?? "未找到";
        _toolBoxes["Ninja"].Text = _tools.Ninja ?? "未找到";
        _toolBoxes["Compiler"].Text = _tools.Compiler ?? "未找到";
        _toolBoxes["OpenOcd"].Text = _tools.OpenOcd ?? "未找到";
        _toolBoxes["Scripts"].Text = _tools.Scripts ?? "未找到";
        var checks = new (string Name, string? Path)[]
        {
            ("CMake", _tools.CMake), ("Ninja", _tools.Ninja), ("ARM GCC", _tools.Compiler), ("OpenOCD", _tools.OpenOcd)
        };
        _toolFailures.Clear();
        foreach (var check in checks)
        {
            if (check.Path == null) { _toolTiles[check.Name].SetStatus("未找到", false); continue; }
            try
            {
                var result = await ProcessTools.RunAsync(check.Path, ["--version"], _project?.Root ?? Environment.CurrentDirectory, TimeSpan.FromSeconds(8));
                var version = Regex.Match(result.Output, @"\d+(?:\.\d+)+").Value;
                _toolTiles[check.Name].SetStatus(result.ExitCode == 0 ? (version.Length == 0 ? "可运行" : version) : "运行失败", result.ExitCode == 0);
                if (result.ExitCode != 0) _toolFailures[ToolKey(check.Name)] = "--version 退出码 " + result.ExitCode;
            }
            catch (Exception ex) { _toolTiles[check.Name].SetStatus("运行失败", false); _toolFailures[ToolKey(check.Name)] = ex.Message; }
        }
        await RefreshTargetScriptsAsync();
        _toolBoxes["OpenOcd"].Text = _tools.OpenOcd ?? "未找到";
        _toolBoxes["Scripts"].Text = _tools.Scripts ?? "未找到";
        var projectStatus = _project == null ? "尚未选择工程" : _project.IsCMakeProject ?
            $"{_project.Chip ?? "芯片待确认"}  ·  {_project.ConfigurePreset ?? "默认构建"}" : "不是有效 CMake 根目录";
        var targetStatus = _targetScriptResolution?.Ready == true ? "Target 配置已验证：" + Path.GetFileName(_target.Text) :
            !string.IsNullOrWhiteSpace(_target.Text) ? "Target 脚本待补全：" + Path.GetFileName(_target.Text) : "Target 待确认";
        _result.Text = _project == null ? "工具检测结果如下。选择工程后识别芯片、构建预设和烧录配置。" : projectStatus + Environment.NewLine +
            (_tools.Scripts == null ? "CMSIS-DAP 配置缺失" : "CMSIS-DAP 配置已找到") + "  ·  " + targetStatus +
            (_project?.Notes.Count > 0 ? Environment.NewLine + string.Join("；", _project.Notes) : "");
        UpdateActions(); LayoutPage();
        if (showIssues && _project != null)
        {
            var issues = CurrentIssues();
            Notify(issues.Count == 0 ? "环境检测已完成，工具和脚本已就绪。可以进行配置与编译验证。" :
                $"工程已识别，还有 {issues.Count} 项需要补全。查看说明可以了解具体处理步骤。",
                issues.Count > 0, issues.Count > 0 ? "查看补全说明" : null,
                issues.Count > 0 ? () => ShowEnvironmentHelp() : null);
        }
    }

    private async Task BrowseToolAsync(string key)
    {
        if (key == "Scripts")
        {
            using var folder = new FolderBrowserDialog { Description = "选择含 interface 和 target 的 OpenOCD scripts 目录" };
            if (folder.ShowDialog(this) != DialogResult.OK) return;
            if (!EnvironmentScanner.IsScripts(folder.SelectedPath)) { Notify("该目录不含 interface/cmsis-dap.cfg 或 target。请选择完整的 OpenOCD scripts 目录。", true, "指定脚本目录", () => _ = BrowseToolAsync("Scripts")); return; }
            _tools = _tools with { Scripts = folder.SelectedPath };
        }
        else
        {
            var expected = key switch { "CMake" => "cmake.exe", "Ninja" => "ninja.exe", "Compiler" => "arm-none-eabi-gcc.exe", _ => "openocd.exe" };
            using var file = new OpenFileDialog { Title = "选择 " + expected, Filter = "可执行文件 (*.exe)|*.exe", FileName = expected };
            if (file.ShowDialog(this) != DialogResult.OK) return;
            if (!string.Equals(Path.GetFileName(file.FileName), expected, StringComparison.OrdinalIgnoreCase))
            { Notify("文件不匹配，请选择 " + expected + "。", true, "重新选择", () => _ = BrowseToolAsync(key)); return; }
            _tools = key switch
            {
                "CMake" => _tools with { CMake = file.FileName }, "Ninja" => _tools with { Ninja = file.FileName },
                "Compiler" => _tools with { Compiler = file.FileName }, _ => _tools with { OpenOcd = file.FileName, Scripts = null }
            };
        }
        Append("已更新 " + key + " 路径。");
        InvalidateCompletion();
        await RefreshEnvironmentAsync();
        Notify("工具路径已更新并重新检测。", actionText: "查看配置详情", action: ShowDetails);
    }
    private async Task RepairAsync()
    {
        var issues = CurrentIssues();
        if (!ShowEnvironmentHelp()) return;
        if (!_expanded) ToggleDetails();
        var missing = issues.FirstOrDefault(x => _toolBoxes.ContainsKey(x.Key))?.Key;
        if (missing == null) return;
        await BrowseToolAsync(missing);
    }
    private async Task ConfigureAsync()
    {
        if (_busy || _project?.IsCMakeProject != true) return;
        if (CurrentIssues().Any(x => x.BlocksConfiguration)) { Notify("配置尚未补齐，请处理待补全项后再进行编译验证。", true, "查看补全说明", () => ShowEnvironmentHelp()); return; }
        var target = _target.Text.Trim().Replace('\\', '/');
        if (OpenOcdScripts.MissingFiles(_tools.Scripts, target).Count > 0)
        {
            if (!_expanded) ToggleDetails();
            Notify("Target 或依赖脚本尚未补齐。请自动修复，或指定完整的配套 OpenOCD。", true, "自动修复", () => _ = AutoRepairAsync(openOcdOnly: true));
            return;
        }
        if (_tools.CMake == null || _tools.Ninja == null || _tools.Compiler == null || _tools.OpenOcd == null || _tools.Scripts == null) return;
        BeginFeedback("configure", _configure, "配置验证中…"); _lastTaskChanges = [];
        SetBusy(true, "准备配置工程…", 15);
        try
        {
            var options = CreateOptions();
            FeedbackStep(0, StepState.Complete);
            var plan = CMakeBuildPlan.Create(options);
            await RunBuildAsync(plan);
            var elf = ChooseFirmware(CMakeBuildPlan.FindElfs(plan.BuildDirectory, plan.BuildConfiguration), options.Root);
            if (elf == null) { _stage.Text = "已取消固件选择，未写入任务。"; FeedbackStep(3, StepState.Cancelled); Notify(_stage.Text); return; }
            _firmwareLabel.Text = "固件：" + Path.GetRelativePath(options.Root, elf);
            Append("已确认固件：" + elf);
            SetBusy(true, "检查 OpenOCD 脚本解析…", 88);
            FeedbackStep(3, StepState.Running);
            var openocd = await ProcessTools.RunAsync(_tools.OpenOcd, ["-s", _tools.Scripts, "-f", "interface/cmsis-dap.cfg", "-c", "transport select swd",
                "-f", target, "-c", "shutdown"], _project.Root, TimeSpan.FromSeconds(20), Append);
            if (openocd.ExitCode != 0) throw new InvalidOperationException("编译通过，但 OpenOCD 配置检查失败，退出码 " + openocd.ExitCode + "。");
            FeedbackStep(3, StepState.Complete);
            FeedbackStep(4, StepState.Running);
            SetBusy(true, "正在写入 VS Code 任务…", 95);
            var configuredOptions = options with { FirmwareElfPath = elf };
            var changes = ConfigurationWriter.Preview(configuredOptions);
            if (changes.Count > 0)
            {
                using var preview = new ChangePreviewDialog(options.Root, "配置 VS Code 编译与烧录任务", changes);
                _stage.Text = "等待确认任务修改，确认后写入。";
                if (preview.ShowDialog(this) != DialogResult.OK)
                {
                    _stage.Text = "已取消写入任务。"; FeedbackStep(4, StepState.Cancelled);
                    ShowCompletion("编译通过，已取消写入任务", FirmwareSummary(elf, options.BuildConfiguration), elf); return;
                }
                ChangeHistory.Apply(options.Root, "配置 VS Code 编译与烧录任务", changes);
            }
            _lastTaskChanges = changes; FeedbackStep(4, StepState.Complete);
            Append("VS Code 任务已配置；本次变更可通过“恢复修改”撤回。");
            SetBusy(true, "配置完成：编译与 OpenOCD 配置检查通过。", 100);
            Append("验证通过。VS Code 中可运行“一键编译”和“一键烧录(DAPLINK)”；烧录任务会编译、下载、校验并复位。未执行硬件烧录。");
            ShowCompletion("配置完成，编译和脚本检查通过", FirmwareSummary(elf, options.BuildConfiguration) +
                "\nVS Code 编译与烧录任务已就绪。本次未执行硬件烧录。", elf, tasksReady: true);
        }
        catch (Exception ex)
        {
            _stage.Text = "验证未通过：" + ex.Message; Append("错误：" + ex); FinishActiveSteps(StepState.Attention);
            ShowCompletion("配置需要处理", ex.Message + "\n查看问题与日志了解具体位置和处理建议。", attention: true);
        }
        finally { _busy = false; EndFeedback(); }
    }

    private SetupOptions CreateOptions(bool requireOpenOcd = true)
    {
        if (_project?.IsCMakeProject != true || _tools.CMake == null || _tools.Ninja == null || _tools.Compiler == null ||
            (requireOpenOcd && (_tools.OpenOcd == null || _tools.Scripts == null)))
            throw new InvalidOperationException("请先选择工程，并确认 CMake、Ninja 和 ARM GCC 环境。");
        return new(_project.Root, _tools.CMake, _tools.Ninja, _tools.Compiler, _tools.OpenOcd ?? "",
            _tools.Scripts ?? "", _target.Text.Trim().Replace('\\', '/'), _project.ConfigurePreset,
            _project.BuildPreset, _project.BuildDirectory, _project.ToolchainFile)
        { BuildConfiguration = SelectedFixedConfiguration() ?? _defaultConfiguration };
    }

    private async Task RunBuildAsync(CMakeBuildPlan plan)
    {
        _buildProblems.Clear(); _buildOutput.Clear();
        plan.PrepareArtifactQuery();
        void Capture(string line) { _buildOutput.Enqueue(line); Append(line); }
        _buildOutput.Enqueue("=== CMake 配置 ===");
        SetBusy(true, "CMake 正在配置工程…", 40);
        FeedbackStep(_operation == "source" ? 2 : 1, StepState.Running);
        var configure = await ProcessTools.RunAsync(plan.Configure.Executable, plan.Configure.Arguments,
            plan.Configure.WorkingDirectory, TimeSpan.FromMinutes(5), Capture, plan.Configure.PathPrefix);
        _buildProblems.AddRange(BuildDiagnostics.Parse(configure.Output, plan.Configure.WorkingDirectory, "CMake 配置"));
        if (configure.ExitCode != 0) throw new InvalidOperationException("CMake 配置失败，退出码 " + configure.ExitCode + "。请查看执行日志。");
        FeedbackStep(_operation == "source" ? 2 : 1, StepState.Complete);
        SetBusy(true, "正在实际编译工程…", 68);
        FeedbackStep(_operation == "source" ? 3 : 2, StepState.Running);
        _buildOutput.Enqueue("=== 编译与链接 ===");
        var build = await ProcessTools.RunAsync(plan.Build.Executable, plan.Build.Arguments,
            plan.Build.WorkingDirectory, TimeSpan.FromMinutes(15), Capture, plan.Build.PathPrefix);
        _buildProblems.AddRange(BuildDiagnostics.Parse(build.Output, plan.Build.WorkingDirectory, "源码编译"));
        if (build.ExitCode != 0) throw new InvalidOperationException("实际编译失败，退出码 " + build.ExitCode + "。请查看执行日志。");
        FeedbackStep(_operation == "source" ? 3 : 2, StepState.Complete);
        SetBusy(true, "CMake 编译已通过。", 82);
    }

    private void SetBusy(bool busy, string stage, int progress)
    {
        _busy = busy; _stage.Text = stage; _progress.Value = progress;
        if (_sources.Visible) { _sourceProgress.Value = progress; _sourceStatus.Text = stage; LayoutSourcePage(); }
        UpdateActions();
        LayoutPage();
    }
    private void UpdateActions()
    {
        _configure.Enabled = !_busy && _project?.IsCMakeProject == true && _tools.CMake != null && _tools.Ninja != null &&
            _tools.Compiler != null && _tools.OpenOcd != null && _tools.Scripts != null && _toolFailures.Count == 0;
        _repair.Enabled = !_busy; _rescan.Enabled = !_busy; _toggle.Enabled = !_busy;
        _autoRepair.Enabled = !_busy;
        _mirrorSource.Enabled = !_busy;
        _browseProject.Enabled = !_busy;
        _target.Enabled = !_busy;
        foreach (var button in _toolButtons.Values) button.Enabled = !_busy;
        _chooseSource.Enabled = !_busy && _project?.IsCMakeProject == true;
        _applySource.Enabled = !_busy && _sourcePlan != null;
        _sourceTarget.Enabled = !_busy && _sourceTarget.Items.Count > 0;
        _sourcePreview.Enabled = !_busy;
        _selectAllSources.Enabled = _selectNoSources.Enabled = !_busy && _sourcePreview.Count > 0;
        _environmentHelp.Enabled = !_busy;
        _navTools.Enabled = !_busy;
        _presetPicker.Enabled = !_busy && _project?.IsCMakeProject == true;
        _configurationPicker.Enabled = !_busy && SelectedFixedConfiguration() == null;
        _buildPresetPicker.Enabled = !_busy && _project?.IsCMakeProject == true;
        _identificationDetails.Enabled = !_busy && _project != null;
        _restoreChanges.Enabled = _sourceRestore.Enabled = !_busy && _project != null;
        _showProblems.Enabled = _sourceProblems.Enabled = !_busy;
        _sourceSearch.Enabled = _sourceFilter.Enabled = !_busy;
        _expandSources.Enabled = _collapseSources.Enabled = !_busy && _sourcePreview.Count > 0;
        var issues = CurrentIssues();
        var buildIssues = issues.Where(x => x.Key is "Project" or "CMake" or "Ninja" or "Compiler" or "Preset").ToArray();
        _applySource.Enabled = _applySource.Enabled && buildIssues.Length == 0;
        if (!_busy && _sourcePlan != null && buildIssues.Length > 0)
            _sourceStatus.Text = "需先补全构建环境：" + string.Join("、", buildIssues.Select(x => x.Title)) + "。可回到工程配置查看补全说明。";
        _configure.Enabled = _configure.Enabled && !issues.Any(x => x.BlocksConfiguration);
        _readiness.Text = _busy ? "正在执行操作，完成后可继续。" : _project == null ? "请先选择工程，才能配置任务。" :
            _project.IsCMakeProject == false ? "所选文件夹不包含 CMakeLists.txt，请更换工程。" : issues.Count == 0 ? "环境已就绪，可以配置" : $"有 {issues.Count} 项需要补全，查看具体步骤";
        _readiness.ForeColor = issues.Count == 0 ? Color.FromArgb(35, 139, 101) : Color.FromArgb(162, 106, 33);
        UpdateImportActions();
        UpdateConversionActions();
        foreach (var button in new[] { _configure, _chooseSource, _applySource })
            button.BackColor = button.Enabled || button is SoftButton { IsBusy: true } ? Accent : Color.FromArgb(226, 234, 242);
        if (_completionCard != null)
        {
            _navSetup.Enabled = _navSources.Enabled = _navImport.Enabled = !_busy;
            RefreshWorkspaceFooter();
            _stopRepair.Visible = _repairCancellation != null;
            _stopRepair.Enabled = _repairCancellation != null && !_repairCancellation.IsCancellationRequested;
            RefreshEmptyStates();
            foreach (var button in new[] { _openProject, _openFirmware, _viewLastChanges, _completionHelp, _sourceNext, _viewSourceChanges, _sourceChooseProject, _importChooseProject }) button.Enabled = !_busy;
            _navTips.SetToolTip(_configure, _configure.Enabled ? "实际编译并生成 VS Code 任务" : _readiness.Text);
            _navTips.SetToolTip(_applySource, _applySource.Enabled ? "预览修改，再写入并编译" : _sourcePreview.Count == 0 ? "先选择文件夹" : "请至少勾选一个文件，并确认可用构建目标和工具");
        }
    }
    private static string ToolKey(string name) => name == "ARM GCC" ? "Compiler" : name == "OpenOCD" ? "OpenOcd" : name;
    protected override void Dispose(bool disposing)
    {
        if (disposing) { _navTips.Dispose(); _feedbackTimer.Dispose(); _conversionTimer.Dispose(); }
        base.Dispose(disposing);
    }
    private void Append(string text)
    {
        if (InvokeRequired) { BeginInvoke(() => Append(text)); return; }
        _log.AppendText("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + text + Environment.NewLine);
        _log.SelectionStart = _log.TextLength; _log.ScrollToCaret();
        _sourceLog.AppendText("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + text + Environment.NewLine);
        _sourceLog.SelectionStart = _sourceLog.TextLength; _sourceLog.ScrollToCaret();
    }
    private static Label Label(string text, float size, Color color, Point? point = null, Size? bounds = null, bool bold = false) =>
        new() { Text = text, ForeColor = color, BackColor = Color.Transparent,
            Font = new Font("Microsoft YaHei UI", size, bold ? FontStyle.Bold : FontStyle.Regular),
            Location = point ?? Point.Empty, Size = bounds ?? new Size(700, 30) };
    private static TextBox Input(bool readOnly) => new() { ReadOnly = readOnly, TabStop = !readOnly, BorderStyle = BorderStyle.FixedSingle,
        BackColor = Color.FromArgb(250, 252, 255), ForeColor = Ink, Font = new Font("Segoe UI", 10), Height = 29 };
    private static Button Button(string text, bool primary) => new SoftButton { Text = text, FlatStyle = FlatStyle.Flat,
        BackColor = primary ? Accent : Color.FromArgb(241, 244, 248), ForeColor = primary ? Color.White : Ink,
        Font = new Font("Microsoft YaHei UI", 9, primary ? FontStyle.Bold : FontStyle.Regular), Cursor = Cursors.Hand,
        FlatAppearance = { BorderSize = 0 } };

    private class RoundedPanel : Panel
    {
        private readonly bool _hero;
        private readonly bool _dashed;
        public RoundedPanel(bool hero = false, bool dashed = false)
        {
            _hero = hero; _dashed = dashed; BackColor = hero ? Background : Surface;
            DoubleBuffered = true; ResizeRedraw = true;
        }
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.Clear(Background);
            if (_hero || Width < 24 || Height < 24) return;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var scale = DeviceDpi / 96f;
            using var path = UiShape.Round(new RectangleF(1, 1, Width - 3, Height - 3), 7 * scale);
            using var brush = new SolidBrush(Surface);
            e.Graphics.FillPath(brush, path);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (_hero || Width < 24 || Height < 24) return;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var scale = DeviceDpi / 96f;
            using var pen = new Pen(_dashed ? Color.FromArgb(213, 224, 242) : Border, 1f * scale);
            using var path = UiShape.Round(new RectangleF(1, 1, Width - 3, Height - 3), 7 * scale);
            e.Graphics.DrawPath(pen, path);
        }
    }
    private sealed class ProgressStrip : Control
    {
        private int _value;
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public int Value { get => _value; set { _value = Math.Clamp(value, 0, 100); Invalidate(); } }
        public ProgressStrip() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true); }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Surface);
            using var track = new SolidBrush(Color.FromArgb(224, 234, 242));
            using var fill = new SolidBrush(Accent);
            e.Graphics.FillRectangle(track, 0, 0, Width, Height);
            if (_value > 0) e.Graphics.FillRectangle(fill, 0, 0, Width * _value / 100, Height);
        }
    }
}
