using CMakeDapLink.Core;

namespace CMakeDapLink.App;

public sealed partial class MainForm
{
    private readonly Button _autoRepair = Button("自动修复", false);
    private readonly Button _mirrorSource = Button("镜像源", false);
    private CancellationTokenSource? _repairCancellation;

    private async Task AutoRepairAsync()
    {
        if (_repairCancellation != null)
        {
            _repairCancellation.Cancel(); _autoRepair.Enabled = false;
            _stage.Text = "正在停止下载…"; return;
        }
        if (_busy) return;
        using var cancellation = new CancellationTokenSource();
        BeginFeedback("repair", _autoRepair, "自动修复中…");
        _repairCancellation = cancellation;
        _workflowSteps.Reset("检测缺失工具", "下载安装", "验证环境"); _workflowSteps.SetStep(0, StepState.Running);
        SetBusy(true, "检测缺失工具…", 0);
        try
        {
            Directory.CreateDirectory(ManagedTools.Root);
            await RefreshEnvironmentAsync();
            _workflowSteps.SetStep(0, StepState.Complete); _workflowSteps.SetStep(1, StepState.Running);
            using var installer = new ToolRepairInstaller();
            var progress = new Progress<ToolRepairProgress>(item =>
            {
                if (IsDisposed || Disposing) return;
                _stage.Text = item.Stage; _progress.Value = item.Percent;
                if (item.Message != null) Append(item.Message);
                LayoutPage();
            });
            var current = _tools; var broken = _toolFailures.Keys.ToArray(); var target = _target.Text.Trim().Replace('\\', '/');
            var result = await Task.Run(() => installer.RepairAsync(current, broken, target, progress, cancellation.Token), cancellation.Token);
            _tools = result.Tools;
            _workflowSteps.SetStep(1, StepState.Complete); _workflowSteps.SetStep(2, StepState.Running);
            await RefreshEnvironmentAsync();
            var remaining = CurrentIssues().Where(x => _project != null || x.Key != "Project" && x.Key != "Target").ToList();
            foreach (var problem in result.Problems)
                remaining.Add(new("Repair", "自动修复需要继续处理", problem + "\n查看构建输出中的下载源与检测结果。可在“镜像源”更换备用地址后重试，也可使用“手动指定路径”选择完整安装包中的工具。"));
            _stage.Text = remaining.Count == 0 ? "环境已补齐，工具验证通过" : "自动修复结束，还有待补全项";
            Append(_stage.Text);
            _workflowSteps.SetStep(2, remaining.Count == 0 ? StepState.Complete : StepState.Attention);
            Notify(remaining.Count == 0 ? "环境已补齐，工具验证通过。可以继续配置与编译。" :
                $"自动修复结束，还有 {remaining.Count} 项待处理。" + string.Join("；", remaining.Select(x => x.Title)),
                remaining.Count > 0, remaining.Count > 0 ? "查看补全说明" : "查看配置详情",
                remaining.Count > 0 ? () => ShowEnvironmentHelp(remaining) : ShowDetails);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            _tools = ManagedTools.LoadPaths();
            await RefreshEnvironmentAsync();
            _stage.Text = "已停止下载，已安装的工具和路径已保留。"; Append(_stage.Text);
            for (var i = 0; i < _workflowSteps.States.Count; i++) if (_workflowSteps.States[i] == StepState.Running) _workflowSteps.SetStep(i, StepState.Cancelled);
            Notify(_stage.Text);
        }
        catch (Exception ex)
        {
            _stage.Text = "自动修复未完成：" + ex.Message; Append(_stage.Text);
            for (var i = 0; i < _workflowSteps.States.Count; i++) if (_workflowSteps.States[i] == StepState.Running) _workflowSteps.SetStep(i, StepState.Attention);
            Notify(_stage.Text, true, "指定工具路径", () => _ = RepairAsync());
        }
        finally
        {
            _repairCancellation = null; _busy = false;
            if (!IsDisposed && !Disposing) EndFeedback();
        }
    }

    private void ShowMirrorSettings()
    {
        try
        {
            using var dialog = new MirrorSettingsDialog(ManagedTools.LoadSettings());
            if (dialog.ShowDialog(this) == DialogResult.OK) { Append("镜像源设置已保存：" + ManagedTools.SettingsPath); Notify("镜像源设置已保存，下次下载时使用。", actionText: "查看配置详情", action: ShowDetails); }
        }
        catch (Exception ex) { Notify("镜像源设置：" + ex.Message, true); }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Keep the form alive until the ongoing operation has released its files.
        if (_repairCancellation != null)
        {
            e.Cancel = true; _repairCancellation.Cancel();
            _stage.Text = "正在停止下载，请稍后关闭窗口。";
        }
        else if (_busy)
        {
            e.Cancel = true;
            _stage.Text = "正在完成当前操作，请结束后再关闭窗口。";
            if (_operation == "source") _sourceStatus.Text = _stage.Text;
            if (_operation == "import") _importStatus.Text = _stage.Text;
            if (_conversionRunning) _conversionStage.Text = _stage.Text;
            LayoutPage(); LayoutSourcePage(); LayoutImportPage(); LayoutConversionPage();
        }
        base.OnFormClosing(e);
    }

    private sealed class MirrorSettingsDialog : Form
    {
        private readonly RoundedTextField _address = new();
        private readonly Label _error = Label("", 9, Color.FromArgb(166, 72, 58));
        private readonly Button _save = Button("保存设置", true);
        private readonly Button _cancel = Button("取消", false);
        private readonly Label _title = Label("下载源", 16, Ink, bold: true);
        private readonly Label _subtitle = Label("优先官方源，备用源可随时更换。", 9, Muted);
        private readonly Label _heading = Label("GitHub 加速镜像", 10, Ink, bold: true);
        private readonly Label _hint = Label("地址中用 {url} 代表完整的 GitHub 下载地址；留空可关闭加速镜像。", 9, Muted);
        private readonly Label _note = Label($"备用来源还包括 SourceForge 发布镜像与 xPack 发行版；索引备用使用国内 npmmirror。xPack 与官方最新版本可能不同，实际版本会显示在日志中。\n\n下载后校验 SHA-256，再解压到 {ManagedTools.Root}。镜像可用性受网络和服务状态影响。", 9, Muted);
        private readonly RoundedPanel _card = new();
        private int Sc(float value) => Math.Max(1, (int)Math.Round(value * DeviceDpi / 96f));

        public MirrorSettingsDialog(ToolRepairSettings settings)
        {
            AutoScaleMode = AutoScaleMode.None; Font = new Font("Microsoft YaHei UI", 9);
            Text = "镜像源"; BackColor = Background; StartPosition = FormStartPosition.CenterParent;
            Icon = AppIcon.Chip;
            FormBorderStyle = FormBorderStyle.FixedDialog; MinimizeBox = false; MaximizeBox = false;
            ClientSize = new Size(Sc(570), Sc(445));
            _address.Text = settings.GitHubMirror; _address.Name = "MirrorAddress";
            Controls.AddRange([_title, _subtitle, _card, _error, _save, _cancel]);
            _card.Controls.AddRange([_heading, _address, _hint, _note]);
            _cancel.DialogResult = DialogResult.Cancel; AcceptButton = _save; CancelButton = _cancel;
            _save.Click += (_, _) =>
            {
                try { ManagedTools.SaveSettings(new(_address.Text.Trim())); DialogResult = DialogResult.OK; Close(); }
                catch (Exception ex) { _error.Text = ex.Message; Arrange(); }
            };
            Shown += (_, _) => Arrange(); Arrange();
        }
        private void Arrange()
        {
            var margin = Sc(22); var inner = ClientSize.Width - margin * 2;
            int Place(Label label, int left, int top, int width)
            {
                label.SetBounds(left, top, width, MeasureLabel(label, width)); return label.Bottom;
            }
            Place(_title, margin, Sc(20), inner); Place(_subtitle, margin, _title.Bottom + Sc(6), inner);
            var inset = Sc(18); var cardInner = inner - inset * 2;
            Place(_heading, inset, Sc(18), cardInner);
            _address.SetBounds(inset, _heading.Bottom + Sc(12), cardInner, Sc(40));
            Place(_hint, inset, _address.Bottom + Sc(10), cardInner);
            Place(_note, inset, _hint.Bottom + Sc(20), cardInner);
            _card.SetBounds(margin, _subtitle.Bottom + Sc(18), inner, _note.Bottom + Sc(18));
            Place(_error, margin, _card.Bottom + Sc(10), inner);
            var buttonY = _error.Bottom + Sc(12); var height = Sc(36);
            _save.SetBounds(ClientSize.Width - margin - Sc(112), buttonY, Sc(112), height);
            _cancel.SetBounds(_save.Left - Sc(90), buttonY, Sc(80), height);
            ClientSize = new Size(ClientSize.Width, _save.Bottom + margin);
        }
    }
}
