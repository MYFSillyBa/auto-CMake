using CMakeDapLink.Core;

namespace CMakeDapLink.App;

public sealed partial class MainForm
{
    private readonly List<Control> _importCards = [];
    private readonly List<string> _incomingFiles = [];
    private readonly RoundedTextField _importName = new();
    private readonly Label _importProject = Label("请先在“工程配置”选择当前工程。", 9, Muted);
    private readonly Label _importDestination = Label("目标位置将在输入文件夹名称后显示。", 9, Muted);
    private readonly Label _importCount = Label("支持多选，也可以拖入文件。", 9, Muted);
    private readonly Label _importStatus = Label("输入名称并选择文件，确认后复制到当前工程。", 9, Muted);
    private readonly Label _importNote = Label("同名文件夹会询问是否追加；同名文件自动改名并保留两份。加入后可去“添加源文件”选择需要参与编译的文件。", 9, Muted);
    private readonly FileSelectionList _importList = new() { EmptyText = "选择或拖入文件，在这里核对要加入工程的清单。" };
    private readonly Button _chooseImport = Button("选择文件", true);
    private readonly Button _clearImport = Button("清空", false);
    private readonly Button _copyFiles = Button("创建文件夹并加入", true);
    private readonly Button _openImported = Button("前往添加源文件", false);
    private readonly ProgressStrip _importProgress = new();
    private readonly RichTextBox _importLog = new() { ReadOnly = true, BorderStyle = BorderStyle.None,
        BackColor = Color.FromArgb(24, 32, 45), ForeColor = Color.FromArgb(205, 216, 231), Font = new Font("Consolas", 9), ScrollBars = RichTextBoxScrollBars.Vertical };
    private RoundedPanel _importHero = null!;
    private RoundedPanel _importDetails = null!;
    private RoundedPanel _importFiles = null!;
    private RoundedPanel _importResult = null!;
    private FileImportPlan? _importPlan;
    private string? _lastImportedFolder;
    private bool _layingOutImports;

    private void BuildImportPage()
    {
        _importHero = new RoundedPanel(hero: true);
        _importHero.Controls.Add(Label("加入文件", 18, Ink, bold: true));
        _importHero.Controls.Add(Label("创建工程内文件夹，将选定文件复制进去。", 9, Muted));
        AddImport(_importHero);
        _importDetails = new RoundedPanel();
        _importDetails.Controls.Add(Label("目标文件夹", 11, Ink, bold: true));
        _importDetails.Controls.Add(_importProject); _importProject.AutoEllipsis = true;
        _importDetails.Controls.Add(Label("文件夹名称", 9, Muted));
        _importName.PlaceholderText = "例如 BSP、Modules 或 Utilities";
        _importName.Font = new Font("Microsoft YaHei UI", 9);
        _importName.TextChanged += (_, _) => RefreshImportPreview();
        _importDetails.Controls.Add(_importName);
        _importDestination.AutoEllipsis = true;
        _importDetails.Controls.Add(_importDestination); _importDetails.Controls.Add(_importNote);
        AddImport(_importDetails);

        _importFiles = new RoundedPanel { AllowDrop = true };
        _importFiles.Controls.Add(Label("要加入的文件", 11, Ink, bold: true));
        _chooseImport.Click += (_, _) => BrowseImportFiles();
        _clearImport.Click += (_, _) => { _incomingFiles.Clear(); _importList.SetFiles([]); RefreshImportPreview(); };
        _importFiles.Controls.Add(_chooseImport); _importFiles.Controls.Add(_clearImport);
        _importFiles.Controls.Add(_importCount); _importFiles.Controls.Add(_importList);
        _importList.SelectionChanged += (_, _) => RefreshImportPreview();
        foreach (var control in new Control[] { _importFiles, _importList, _importCount })
        {
            control.AllowDrop = true;
            control.DragEnter += (_, e) =>
            {
                if (!_busy && _project?.IsCMakeProject == true && e.Data?.GetData(DataFormats.FileDrop) is string[] files && files.All(File.Exists))
                    e.Effect = DragDropEffects.Copy;
            };
            control.DragDrop += (_, e) => { if (e.Data?.GetData(DataFormats.FileDrop) is string[] files) AddImportFiles(files); };
        }
        AddImport(_importFiles);

        _importResult = new RoundedPanel();
        _importResult.Controls.Add(Label("确认并加入", 11, Ink, bold: true));
        _importResult.Controls.Add(_importStatus);
        _copyFiles.Click += async (_, _) => await CopyImportFilesAsync();
        _importResult.Controls.Add(_copyFiles);
        _openImported.Click += (_, _) => ContinueWithImportedFiles();
        _importResult.Controls.Add(_openImported); _importResult.Controls.Add(_importLog); _importResult.Controls.Add(_importProgress);
        AddImport(_importResult);
        UpdateImportActions();
        LayoutImportPage();
    }
    private void AddImport(Control card) { _importCards.Add(card); _imports.Canvas.Controls.Add(card); _imports.AttachCard(card); }
    private void ResetImportProject()
    {
        _incomingFiles.Clear(); _importList.SetFiles([]); _importPlan = null; _lastImportedFolder = null;
        _lastImportedFiles = [];
        _importProject.Text = "当前工程 · " + _project?.Root;
        _importName.Text = ""; _importLog.Clear(); RefreshImportPreview();
    }
    private void BrowseImportFiles()
    {
        using var dialog = new OpenFileDialog { Title = "选择要加入当前工程的文件", Multiselect = true,
            Filter = "所有文件 (*.*)|*.*", CheckFileExists = true };
        if (dialog.ShowDialog(this) == DialogResult.OK) AddImportFiles(dialog.FileNames);
    }
    private void AddImportFiles(string[] paths)
    {
        if (_busy || _project?.IsCMakeProject != true) return;
        var selected = _importList.CheckedPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths.Where(File.Exists).Select(Path.GetFullPath))
            if (!_incomingFiles.Contains(path, StringComparer.OrdinalIgnoreCase)) { _incomingFiles.Add(path); selected.Add(path); }
        _importList.SetFiles(_incomingFiles.Select(x => (x, Path.GetFileName(x))), selected);
        RefreshImportPreview();
    }
    private void RefreshImportPreview()
    {
        _importPlan = null;
        if (_importDetails == null) return;
        var selected = _importList.CheckedPaths;
        _importCount.Text = $"清单共 {_incomingFiles.Count} 个 · 已选 {selected.Count} 个 · 将复制 {selected.Count} 个";
        _importDestination.Text = "目标位置将在输入文件夹名称后显示。";
        _importStatus.Text = "输入文件夹名称并勾选文件后，即可加入当前工程。";
        if (_project?.IsCMakeProject == true && _importName.Text.Length > 0 && selected.Count > 0)
        {
            try
            {
                _importPlan = ProjectFileImporter.Preview(_project.Root, _importName.Text, selected);
                _importDestination.Text = "目标：" + _importPlan.FolderPath;
                var renamed = _importPlan.Files.Count(x => Path.GetFileName(x.SourcePath) != Path.GetFileName(x.DestinationPath));
                _importStatus.Text = $"将复制 {selected.Count} 个文件" + (_importPlan.FolderExisted ? "到已有文件夹（执行时确认）" : "到新文件夹") +
                    (renamed > 0 ? $"，{renamed} 个同名文件会自动改名。" : "，原文件保留。");
                var mapping = _importPlan.Files.ToDictionary(x => x.SourcePath, StringComparer.OrdinalIgnoreCase);
                _importList.SetFiles(_incomingFiles.Select(x => (x, mapping.TryGetValue(x, out var file) && Path.GetFileName(x) != Path.GetFileName(file.DestinationPath)
                    ? Path.GetFileName(x) + " → " + Path.GetFileName(file.DestinationPath) : Path.GetFileName(x))), selected, preservePosition: true);
            }
            catch (Exception ex) { _importStatus.Text = ex.Message; }
        }
        UpdateImportActions(); LayoutImportPage();
    }
    private async Task CopyImportFilesAsync()
    {
        if (_busy || _importPlan == null) return;
        var plan = _importPlan;
        var confirmed = false;
        if (plan.FolderExisted)
        {
            var answer = MessageBox.Show(this, $"文件夹“{Path.GetFileName(plan.FolderPath)}”已存在。\n\n是否在已有文件夹中新增选定文件？\n选择“否”后可更改文件夹名称。", "文件夹已存在", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (answer != DialogResult.Yes)
            {
                _importStatus.Text = "请更改文件夹名称后再加入文件。"; _importName.Focus(); _importName.SelectAll(); LayoutImportPage(); return;
            }
            confirmed = true;
        }
        _busy = true; UpdateActions();
        BeginFeedback("import", _copyFiles, "复制校验中…");
        try
        {
            _importStatus.Text = "正在创建文件夹并复制文件…"; _importProgress.Value = 10; LayoutImportPage();
            var copied = await Task.Run(() => ProjectFileImporter.Apply(plan, confirmed, (done, total, file) => BeginInvoke(() =>
            {
                _importProgress.Value = 10 + done * 85 / total;
                _importStatus.Text = $"正在复制并校验 {done}/{total}：{Path.GetFileName(file)}";
                LayoutImportPage();
            })));
            _lastImportedFolder = plan.FolderPath;
            _lastImportedFiles = copied.ToArray();
            foreach (var file in copied) _importLog.AppendText("已加入 " + Path.GetRelativePath(plan.Root, file) + Environment.NewLine);
            _incomingFiles.Clear(); _importList.SetFiles([]); _importPlan = null;
            _importCount.Text = $"已完成：{copied.Count} 个文件已加入工程";
            _importProgress.Value = 100;
            _importStatus.Text = $"已完成：{copied.Count} 个文件已复制到“{Path.GetFileName(plan.FolderPath)}”。可继续选择文件，或前往“添加源文件”。";
            _importSteps.SetStep(1, StepState.Complete); _importSteps.SetStep(2, StepState.Complete);
            Notify($"已加入 {copied.Count} 个文件。下一步可以选择哪些文件参与 CMake 构建。", actionText: "选择参与构建的文件", action: ContinueWithImportedFiles, page: 2);
        }
        catch (Exception ex)
        {
            _importProgress.Value = 0; _importStatus.Text = "加入未完成：" + ex.Message; _importLog.AppendText(ex.Message + Environment.NewLine); _importPlan = null;
            _importSteps.SetStep(1, StepState.Attention);
            Notify(_importStatus.Text, true, "展开复制记录", () => { _importLogExpanded = true; _toggleImportLog.Text = "收起复制记录"; LayoutImportPage(); }, page: 2);
        }
        finally { _busy = false; EndFeedback(); }
    }
    private void UpdateImportActions()
    {
        if (_importDetails == null) return;
        _chooseImport.Enabled = !_busy && _project?.IsCMakeProject == true;
        _clearImport.Enabled = !_busy && _incomingFiles.Count > 0;
        _copyFiles.Enabled = !_busy && _importPlan != null;
        _importList.Enabled = !_busy; _importName.Enabled = !_busy;
        _importNote.Text = _project?.IsCMakeProject == true ? "输入名称并选择文件。复制前可核对清单，同名文件夹需要确认；复制后可直接选择参与构建的文件。" : "请先选择工程。";
        _openImported.Enabled = !_busy && _lastImportedFolder != null;
    }
}
