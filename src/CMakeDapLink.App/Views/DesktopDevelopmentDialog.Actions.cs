using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using CMakeDapLink.Core;
using CMakeDapLink.ExtensionHost;

namespace CMakeDapLink.App;

internal sealed partial class DesktopDevelopmentDialog
{
    private CancellationToken Token => _operation?.Token ?? _lifetime.Token;

    private async Task RunAsync(string label, Func<Task> action)
    {
        if (_busy || IsDisposed) return;
        _busy = true; _operation?.Dispose(); _operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        foreach (var button in _actions) button.Enabled = false;
        EnableInputs(false); _cancel.Enabled = true; _progress.Value = 0; _status.Text = label + "…";
        try { await action(); if (!IsDisposed) { _status.Text = label + "完成。"; _progress.Value = 100; } }
        catch (OperationCanceledException) { if (!IsDisposed) _status.Text = "操作已取消；硬件操作中断后请确认目标状态。"; }
        catch (Exception error)
        {
            if (IsDisposed) return;
            _status.Text = "操作未完成：" + error.Message;
            _lastDiagnostic += Environment.NewLine + _status.Text;
            MessageBox.Show(this, error.Message, "操作未完成", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            _busy = false;
            if (!IsDisposed) { foreach (var button in _actions) button.Enabled = true; EnableInputs(true); _cancel.Enabled = false; }
        }
    }

    private void EnableInputs(bool enabled)
    {
        foreach (var control in new Control[] { _preset, _buildPreset, _configuration, _firmware, _probe, _reset, _syncSource, _syncTarget, _core, _buildDirectory, _compilationDirectory, _targetScript, _gdb, _debugName, _analysisElf, _map, _endpoint, _registers, _speed, _top }) control.Enabled = enabled;
    }

    private async Task InitializeAsync()
    {
        _project = await Task.Run(() => ProjectInspector.Inspect(_root, _initial?.ConfigurePreset, _initial == null || _initial.ConfigurePreset != null), Token);
        _updating = true;
        try
        {
            _preset.Items.Clear(); _preset.Items.Add(new ChoiceItem("不使用预设", null));
            foreach (var preset in _project.ConfigurePresets) _preset.Items.Add(new ChoiceItem(preset.DisplayName == preset.Name ? preset.Name : preset.DisplayName + " · " + preset.Name, preset.Name));
            Select(_preset, _initial?.ConfigurePreset ?? _project.ConfigurePreset);
            _configuration.SelectedItem = _initial?.BuildConfiguration ?? "Debug";
            _buildDirectory.Text = _initial?.BuildDirectory ?? _project.BuildDirectory ?? Path.Combine(_root, "build", "daplink-debug");
            _compilationDirectory.Text = _buildDirectory.Text;
            _targetScript.Text = _initial?.TargetScript ?? _project.TargetScript ?? "";
            _speed.Value = Math.Clamp(_initial?.AdapterSpeed ?? 1000, 1, 50000);
            Select(_probe, _initial?.ProbeType ?? "cmsis-dap"); Select(_reset, _initial?.ResetMode ?? "default");
            if (_tools.Compiler is { } compiler) _gdb.Text = Path.Combine(Path.GetDirectoryName(compiler)!, "arm-none-eabi-gdb.exe");
            FillBuildPresets(_initial?.BuildPreset ?? _project.BuildPreset);
            RefreshArtifacts(_initial?.FirmwareElfPath);
        }
        finally { _updating = false; }
        await _sync.InitializeAsync(Token); _syncReady = true;
        await UpdateSyncStateAsync();
        _watcher = new FileSystemWatcher(_root) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.DirectoryName, EnableRaisingEvents = true };
        _watcher.Changed += Changed; _watcher.Created += Changed; _watcher.Deleted += Changed; _watcher.Renamed += (_, e) => Changed(null, e);
        _watcher.Error += (_, _) => Dispatch(() => _syncNotice.Text = "配置监视缓冲已满，请点击“检查来源与变化”重新扫描。");
    }

    private void PresetChanged()
    {
        if (_project == null) return;
        _updating = true;
        try
        {
            var preset = _project.ConfigurePresets.FirstOrDefault(x => x.Name == Selected(_preset));
            _buildDirectory.Text = preset?.BinaryDirectory ?? Path.Combine(_root, "build", "daplink-debug");
            _compilationDirectory.Text = _buildDirectory.Text;
            var configuration = preset?.ConfigureConfiguration ?? preset?.BuildConfiguration;
            if (configuration != null) { if (!_configuration.Items.Contains(configuration)) _configuration.Items.Add(configuration); _configuration.SelectedItem = configuration; }
            FillBuildPresets(preset?.BuildPreset); RefreshArtifacts();
        }
        finally { _updating = false; }
    }

    private void FillBuildPresets(string? selected)
    {
        _buildPreset.Items.Clear(); _buildPreset.Items.Add(new ChoiceItem("不使用构建预设", null));
        var preset = _project?.ConfigurePresets.FirstOrDefault(x => x.Name == Selected(_preset));
        foreach (var build in preset?.BuildPresets ?? []) _buildPreset.Items.Add(new ChoiceItem(build.DisplayName == build.Name ? build.Name : build.DisplayName + " · " + build.Name, build.Name));
        Select(_buildPreset, selected);
    }

    private void RefreshArtifacts(string? preferred = null)
    {
        preferred ??= Selected(_firmware);
        _firmware.Items.Clear();
        try
        {
            var build = Path.GetFullPath(_buildDirectory.Text, _root);
            var artifacts = CMakeBuildPlan.FindElfs(build, _configuration.Text.Length > 0 ? _configuration.Text : "Debug", _root);
            _firmware.Items.Add(new ChoiceItem(artifacts.Count == 0 ? "请先编译当前配置" : "请选择当前配置的 ELF", null));
            foreach (var file in artifacts) _firmware.Items.Add(new ChoiceItem(Path.GetRelativePath(build, file), Path.GetFullPath(file)));
            Select(_firmware, preferred);
            if (Selected(_firmware) == null && artifacts.Count == 1) _firmware.SelectedIndex = 1;
        }
        catch (Exception error) when (error is IOException or ArgumentException or InvalidOperationException or JsonException)
        { _firmware.Items.Add(new ChoiceItem("目标读取失败，请重新配置并编译", null)); _firmware.SelectedIndex = 0; _status.Text = error.Message; }
    }

    private Dictionary<string, object?> Payload()
    {
        string? Existing(string? path) => path != null && File.Exists(path) ? path : null;
        var tools = new ToolPaths(Existing(_tools.CMake), Existing(_tools.Ninja), Existing(_tools.Compiler), Existing(_tools.OpenOcd), _tools.Scripts != null && Directory.Exists(_tools.Scripts) ? _tools.Scripts : null);
        return new()
        {
            ["tools"] = tools, ["configurePreset"] = Selected(_preset), ["buildPreset"] = Selected(_buildPreset),
            ["configuration"] = _configuration.Text, ["buildDirectory"] = _buildDirectory.Text,
            ["firmwareElfPath"] = Selected(_firmware), ["targetScript"] = _targetScript.Text,
            ["probeType"] = Selected(_probe) ?? "cmsis-dap", ["adapterSpeed"] = (int)_speed.Value, ["resetMode"] = Selected(_reset) ?? "default",
            ["gdbPath"] = string.IsNullOrWhiteSpace(_gdb.Text) ? null : _gdb.Text.Trim(), ["debugName"] = _debugName.Text.Trim(),
            ["compilationDatabaseDirectory"] = string.IsNullOrWhiteSpace(_compilationDirectory.Text) ? null : _compilationDirectory.Text.Trim(), ["language"] = "zh-CN"
        };
    }

    private async Task<JsonElement> CallAsync(string command, Dictionary<string, object?>? payload = null)
    {
        var request = JsonSerializer.SerializeToElement(payload ?? Payload(), Json);
        var token = Token;
        var result = await Task.Run(() => DesktopBackend.ExecuteAsync(_root, command, request, "zh-CN", (percent, stage, detail) => Dispatch(() =>
        {
            _progress.Value = Math.Clamp(percent, 0, 100); _status.Text = stage + (string.IsNullOrWhiteSpace(detail) ? "" : " · " + detail);
        }), token), token);
        _lastDiagnostic = FormatResult(command, result); return result;
    }

    private async Task PreviewApplyAsync(string command, RichTextBox output)
    {
        var preview = await CallAsync(command);
        await ReviewApplyAsync(preview, output);
    }

    private async Task<bool> ReviewApplyAsync(JsonElement preview, RichTextBox output, List<FileChange>? supplied = null, string? labelOverride = null)
    {
        output.Text = FormatResult("文件差异预览", preview);
        if (preview.TryGetProperty("canApply", out var allowed) && allowed.ValueKind == JsonValueKind.False) throw new InvalidOperationException("预览包含阻止写入的问题，请先处理结果中的说明。");
        var changes = supplied ?? (preview.TryGetProperty("changes", out var values) ? values.Deserialize<List<FileChange>>(Json) : null) ?? [];
        var label = labelOverride ?? ValueText(preview, "canonicalLabel") ?? ValueText(preview, "label") ?? "应用预览修改";
        if (changes.Count == 0) { output.AppendText("\n文件内容一致，无需写入。\n"); return true; }
        Token.ThrowIfCancellationRequested();
        using var dialog = new ChangePreviewDialog(_root, label, changes);
        if (dialog.ShowDialog(this) != DialogResult.OK) { output.AppendText("\n已取消写入。\n"); return false; }
        var payload = new Dictionary<string, object?> { ["changes"] = changes, ["label"] = label };
        var applied = await CallAsync("applyChanges", payload);
        output.AppendText("\n已写入经过确认的文件；可从主窗口恢复记录撤销。\n" + FormatResult("写入结果", applied));
        return true;
    }

    private async Task CompileDatabaseAsync()
    {
        if (MessageBox.Show(this, "将运行当前预设与构建目录的 CMake 配置并生成 compile_commands.json。此操作不会编译或连接硬件。", "导出编译数据库", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK) return;
        var payload = Payload(); payload["explicitConfigure"] = true;
        var result = await CallAsync("prepareCompileDatabase", payload);
        _developmentResult.Text = FormatResult("编译数据库", result);
        if (ValueText(result, "compileCommands") is { } file) _compilationDirectory.Text = Path.GetDirectoryName(file)!;
        RefreshArtifacts();
    }

    private async Task ConfigureDebugAsync()
    {
        RequireFirmware(); var preview = await CallAsync("previewDebug");
        if (await ReviewApplyAsync(preview, _developmentResult)) _debugConfigurationName = ValueText(preview, "configurationName");
    }

    private async Task OpenWorkspaceAsync()
    {
        var folder = new Uri(_root.EndsWith(Path.DirectorySeparatorChar) ? _root : _root + Path.DirectorySeparatorChar).AbsoluteUri;
        var uri = "vscode://file/" + Uri.EscapeDataString(_root.Replace('\\', '/')).Replace("%2F", "/", StringComparison.OrdinalIgnoreCase).Replace("%3A", ":", StringComparison.OrdinalIgnoreCase);
        try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception)
        {
            Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true, ArgumentList = { _root } });
            _developmentResult.AppendText("\n未找到 VS Code 协议关联，已打开工程文件夹；请在 VS Code 中打开此目录。\n");
        }
        _developmentResult.AppendText("\n在 VS Code 安装 Cortex-Debug，打开“运行和调试”，选择：" + (_debugConfigurationName ?? _debugName.Text) + "。连接前确认所选硬件；attach 可能暂停目标。\n");
        await Task.CompletedTask;
    }

    private bool HardwareConfirmation(string detail) => MessageBox.Show(this, detail + "\n\n操作可能暂停或复位芯片，不写入固件。请先关闭占用探针或 GDB 服务的其他调试会话。", "确认连接硬件", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) == DialogResult.OK;
    private async Task ProbeAsync()
    {
        if (!HardwareConfirmation("将使用当前下载器、芯片脚本与速度检查探针和芯片连接。")) return;
        var payload = Payload(); payload["explicitHardware"] = true;
        var result = await CallAsync("probeCheck", payload);
        _developmentResult.Text = "脚本就绪：" + Yes(result, "scriptFound") + "    探针已确认：" + Yes(result, "probeFound") + "    芯片已连接：" + Yes(result, "chipConnected") + "\n\n" + FormatResult("硬件检查", result);
    }

    private async Task BuildAsync(bool flash)
    {
        if (flash)
        {
            RequireFirmware();
            if (MessageBox.Show(this, "将配置并编译当前工程，再把所选固件写入目标芯片。\n\n固件：" + Selected(_firmware) + "\n下载器：" + _probe.Text + "\n芯片脚本：" + _targetScript.Text + "\n\n烧录会修改芯片 Flash，并可能复位目标。", "确认烧录固件", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
        }
        var payload = Payload(); if (flash) payload["explicitFlash"] = true;
        var result = await CallAsync(flash ? "flash" : "build", payload);
        _developmentResult.Text = FormatResult(flash ? "烧录结果" : "编译结果", result); _tabs.SelectedIndex = 0; RefreshArtifacts();
    }

    private async Task AnalyzeAsync()
    {
        var payload = AnalysisPayload(); payload["top"] = (int)_top.Value;
        if (string.IsNullOrWhiteSpace((string?)payload["firmwareElfPath"]) && string.IsNullOrWhiteSpace(_map.Text)) throw new ArgumentException("请先选择当前 ELF，或明确选择工程内的 ELF / MAP 文件。");
        var result = await CallAsync("analyzeFirmware", payload);
        _analysisResult.Text = FormatResult("固件分析", result);
        SetGrid(result, result.TryGetProperty("symbols", out var symbols) && symbols.GetArrayLength() > 0 ? "symbols" : "mapSections",
            ("name", "符号 / 段"), ("address", "地址"), ("size", "大小 / B"), ("kind", "类型"), ("section", "段"), ("objectFile", "目标文件"));
    }
    private async Task MemoryAsync()
    {
        var result = await CallAsync("memory", AnalysisPayload()); _analysisResult.Text = FormatResult("Flash / RAM", result);
        SetGrid(result, "regions", ("name", "内存区域"), ("origin", "起始地址"), ("length", "容量 / B"), ("used", "已用 / B"), ("remaining", "剩余 / B"), ("percent", "占用 / %"));
    }
    private async Task HardFaultAsync()
    {
        var payload = AnalysisPayload(); payload.Remove("mapFilePath"); payload["registers"] = DesktopDevelopmentModel.ParseRegisters(_registers.Text); payload["core"] = _core.SelectedIndex <= 0 ? null : _core.Text;
        var result = await CallAsync("hardFault", payload); _analysisResult.Text = FormatResult("HardFault 诊断", result);
        SetGrid(result, "findings", ("code", "故障位"), ("title", "诊断"), ("detail", "说明"));
    }
    private async Task RtosAsync()
    {
        RequireFirmware();
        var endpoint = _endpoint.Text.Trim(); var separator = endpoint.LastIndexOf(':');
        if (separator <= 0 || !int.TryParse(endpoint[(separator + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var port)) throw new ArgumentException("请使用 127.0.0.1:3333 或 localhost:3333 格式的本机 GDB 地址。");
        var host = endpoint[..separator]; DevelopmentHardware.LocalEndpoint(host, port);
        if (!HardwareConfirmation("将连接已有的本机 GDB 服务读取 RTOS 任务，完成后尝试分离。此窗口不会启动 GDB 服务。")) return;
        var payload = Payload(); payload["gdbHost"] = host; payload["gdbPort"] = port; payload["explicitHardware"] = true;
        var result = await CallAsync("rtos", payload); _analysisResult.Text = FormatResult("RTOS 任务", result);
        SetGrid(result, "tasks", ("name", "任务 / 线程"), ("state", "状态"), ("priority", "优先级"), ("stackHighWater", "栈高水位 / 栈单位"));
    }

    private Dictionary<string, object?> AnalysisPayload()
    {
        var payload = Payload();
        if (!string.IsNullOrWhiteSpace(_analysisElf.Text)) payload["firmwareElfPath"] = _analysisElf.Text.Trim();
        payload["mapFilePath"] = string.IsNullOrWhiteSpace(_map.Text) ? null : _map.Text.Trim(); return payload;
    }
    private void RequireFirmware() { if (Selected(_firmware) == null) throw new ArgumentException("请先编译当前配置，并在上方选择实际生成的 ELF 目标。"); }

    private async Task InspectSyncAsync()
    {
        await UpdateSyncStateAsync();
        var result = await CallAsync("inspectConversion", new Dictionary<string, object?> { ["direction"] = "MdkToCMake" });
        var previousSource = Selected(_syncSource); var previousTarget = Selected(_syncTarget);
        _syncSource.Items.Clear(); _syncTarget.Items.Clear();
        _syncSource.Items.Add(new ChoiceItem("选择 MDK 来源工程", null)); _syncTarget.Items.Add(new ChoiceItem("选择同步目标", null));
        foreach (var source in Array(result, "sourceProjects")) if (source.ValueKind == JsonValueKind.String && source.GetString() is { } path) _syncSource.Items.Add(new ChoiceItem(Path.GetRelativePath(_root, Path.GetFullPath(path, _root)), path));
        foreach (var target in Array(result, "targets")) if (target.ValueKind == JsonValueKind.String && target.GetString() is { } name) _syncTarget.Items.Add(new ChoiceItem(name, name));
        Select(_syncSource, previousSource); Select(_syncTarget, previousTarget);
        if (_syncSource.Items.Count == 2) _syncSource.SelectedIndex = 1;
        if (_syncTarget.Items.Count == 2) _syncTarget.SelectedIndex = 1;
        _syncResult.AppendText("\n" + FormatResult("CubeMX 来源检查", result));
    }

    private async Task SyncAsync()
    {
        if (_syncSource.Items.Count == 0) await InspectSyncAsync();
        JsonElement preview;
        if (_syncSource.Items.Count > 1)
        {
            if (Selected(_syncSource) == null || Selected(_syncTarget) == null) throw new ArgumentException("请明确选择 MDK 来源工程与同步目标，再预览同步。");
            var payload = Payload(); payload["direction"] = "MdkToCMake"; payload["sourceProject"] = Selected(_syncSource); payload["target"] = Selected(_syncTarget);
            preview = await CallAsync("previewConversion", payload);
        }
        else
        {
            if (!File.Exists(Path.Combine(_root, "CMakeLists.txt"))) throw new InvalidOperationException("工程中没有可同步的 MDK 来源或 CMakeLists.txt。");
            preview = JsonSerializer.SerializeToElement(new { changes = System.Array.Empty<FileChange>(), label = "同步 CubeMX 工程配置", summary = "没有 MDK 来源，本次保留 CubeMX 配置并恢复助手管理的源文件块。", notes = new[] { "不会生成外设代码或推测编译参数。" } }, Json);
        }
        var changes = preview.TryGetProperty("changes", out var values) ? values.Deserialize<List<FileChange>>(Json) ?? [] : [];
        var currentBlocks = DesktopDevelopmentModel.ReadBlocks(_root);
        changes = DesktopDevelopmentModel.PreserveManagedBlocks(_root, changes, currentBlocks.Length > 0 ? currentBlocks : _sync.Blocks);
        if (await ReviewApplyAsync(preview, _syncResult, changes, "同步 CubeMX 工程配置")) { await _sync.AcceptBaselineAsync(Token); await UpdateSyncStateAsync(); }
    }
    private async Task AcceptSyncAsync() { await _sync.AcceptBaselineAsync(Token); await UpdateSyncStateAsync(); }
    private async Task UpdateSyncStateAsync()
    {
        var files = await _sync.ChangedAsync(Token);
        _syncNotice.Text = files.Length == 0 ? "当前配置与记录基线一致。" : $"检测到 {files.Length} 个配置文件变化，请预览同步差异。";
        _syncNotice.ForeColor = files.Length == 0 ? Muted : Color.FromArgb(170, 100, 25);
        _syncResult.Text = _syncNotice.Text + (files.Length == 0 ? "" : "\n\n" + string.Join("\n", files)) + "\n\n可恢复的助手管理块：" + _sync.Blocks.Length;
    }
    private async Task CheckSyncQuietlyAsync()
    {
        try { await UpdateSyncStateAsync(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { if (!IsDisposed) _syncNotice.Text = "同步扫描未完成：" + error.Message; }
    }
    private void Changed(object? sender, FileSystemEventArgs e)
    {
        if (!DesktopDevelopmentModel.Relevant(Path.GetRelativePath(_root, e.FullPath))) return;
        Dispatch(() => { _watchTimer.Stop(); _watchTimer.Start(); });
    }

    private void PickFolder(TextBox target, bool artifacts = false)
    {
        if (_busy) return;
        using var dialog = new FolderBrowserDialog { Description = "选择当前工程内的目录", UseDescriptionForTitle = true, SelectedPath = Directory.Exists(target.Text) ? target.Text : _root };
        if (dialog.ShowDialog(this) == DialogResult.OK) { target.Text = dialog.SelectedPath; if (artifacts) RefreshArtifacts(); }
    }
    private void PickFile(TextBox target, string filter)
    {
        if (_busy) return;
        using var dialog = new OpenFileDialog { Filter = filter + "|所有文件|*.*", InitialDirectory = _root, CheckFileExists = true, Multiselect = false };
        if (dialog.ShowDialog(this) == DialogResult.OK) target.Text = dialog.FileName;
    }
    private void SetGrid(JsonElement result, string property, params (string Key, string Title)[] columns)
    {
        _analysisGrid.Columns.Clear(); _analysisGrid.Rows.Clear();
        foreach (var column in columns) _analysisGrid.Columns.Add(column.Key, column.Title);
        foreach (var row in Array(result, property)) _analysisGrid.Rows.Add(columns.Select(column => Cell(row, column.Key)).ToArray());
    }
    private static string Cell(JsonElement row, string key)
    {
        if (!row.TryGetProperty(key, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return "—";
        var text = value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.GetRawText();
        if (key == "state") return text switch { "running" => "运行", "ready" => "就绪", "blocked" => "阻塞", "suspended" => "挂起", "suspended-or-blocked" => "挂起或阻塞", "deleted" => "待删除", "stopped" => "已停止", "exited" => "已退出", _ => text };
        return text;
    }
    private string DiagnosticText() => "STM32 工程助手 · 桌面诊断\n工程：" + _root + "\n配置：" + _configuration.Text + "\n构建目录：" + _buildDirectory.Text + "\nELF：" + (Selected(_firmware) ?? "未选择") + "\n\n" + _existingLog + "\n\n" + _lastDiagnostic + "\n\n" + _developmentResult.Text + "\n\n" + _analysisResult.Text + "\n\n" + _syncResult.Text;
    private static string FormatResult(string title, JsonElement result)
    {
        var builder = new StringBuilder(title).AppendLine().AppendLine();
        foreach (var key in new[] { "summary", "configurationName", "compileCommands", "firmwareElfPath", "mapFilePath", "endpoint" }) if (ValueText(result, key) is { Length: > 0 } value) builder.AppendLine(value);
        foreach (var note in Array(result, "notes")) builder.Append("• ").AppendLine(note.ValueKind == JsonValueKind.String ? note.GetString() : note.GetRawText());
        foreach (var issue in Array(result, "issues")) builder.Append("• ").Append(ValueText(issue, "message")).Append(' ').AppendLine(ValueText(issue, "action"));
        foreach (var finding in Array(result, "findings")) builder.Append(ValueText(finding, "code")).Append(" · ").AppendLine(ValueText(finding, "title")).AppendLine(ValueText(finding, "detail"));
        foreach (var location in Array(result, "locations")) builder.Append(ValueText(location, "register")).Append(" @ ").Append(ValueText(location, "address")).Append(" · ").Append(ValueText(location, "function") ?? "函数未知").Append(" · ").AppendLine(ValueText(location, "file") ?? "源码位置未知");
        if (result.TryGetProperty("success", out var success)) builder.AppendLine(success.ValueKind == JsonValueKind.True ? "操作成功。" : "操作未通过，请检查日志与说明。");
        if (ValueText(result, "output") is { Length: > 0 } output) builder.AppendLine().AppendLine(output);
        var detail = JsonSerializer.Serialize(result, Json);
        // File snapshots are reviewed in the diff dialog rather than printed as base64 logs.
        if (!result.TryGetProperty("changes", out _) && detail.Length <= 512000) builder.AppendLine().AppendLine("完整结构化结果：").AppendLine(detail);
        return builder.ToString();
    }
    private static JsonElement[] Array(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var property) && property.ValueKind == JsonValueKind.Array ? property.EnumerateArray().ToArray() : [];
    private static string? ValueText(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    private static string Yes(JsonElement value, string key) => value.TryGetProperty(key, out var flag) && flag.ValueKind == JsonValueKind.True ? "是" : "未确认";
    private static string? Selected(ComboBox combo) => (combo.SelectedItem as ChoiceItem)?.Value;
    private static void Select(ComboBox combo, string? value) { combo.SelectedIndex = 0; for (var index = 0; index < combo.Items.Count; index++) if (combo.Items[index] is ChoiceItem item && string.Equals(item.Value, value, StringComparison.OrdinalIgnoreCase)) { combo.SelectedIndex = index; return; } }
    private void Dispatch(Action action) { if (!IsDisposed && IsHandleCreated) { if (InvokeRequired) { try { BeginInvoke(action); } catch (InvalidOperationException) { } } else action(); } }
}
