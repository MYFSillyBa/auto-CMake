using CMakeDapLink.Core;

namespace CMakeDapLink.App;

public sealed partial class MainForm
{
    private readonly Button _conversionFreeRtosDownload = Button("下载并预览 FreeRTOS 补全…", false);
    private readonly Button _conversionFreeRtosImport = Button("本地移植层导入…", false);
    private readonly Button _conversionFreeRtosCancel = Button("取消补全", false);
    private CancellationTokenSource? _conversionFreeRtosCancellation;
    private bool _conversionFreeRtosApplying;

    private void BuildFreeRtosRepairControls()
    {
        _conversionActions.Controls.AddRange([_conversionFreeRtosDownload, _conversionFreeRtosImport]);
        _conversionOutput.Controls.Add(_conversionFreeRtosCancel);
        _conversionFreeRtosCancel.Visible = false;
        _conversionFreeRtosDownload.Click += async (_, _) => await RepairConversionFreeRtosAsync();
        _conversionFreeRtosImport.Click += async (_, _) =>
        {
            if (!CanRepairConversionFreeRtos) return;
            using var chooser = new FolderBrowserDialog
            {
                Description = "选择包含 port.c 和 portmacro.h 的 FreeRTOS GCC 移植层目录",
                UseDescriptionForTitle = true,
                SelectedPath = _conversionRoot ?? ""
            };
            if (chooser.ShowDialog(this) == DialogResult.OK) await RepairConversionFreeRtosAsync(chooser.SelectedPath);
        };
        _conversionFreeRtosCancel.Click += (_, _) =>
        {
            _conversionFreeRtosCancellation?.Cancel();
            _conversionFreeRtosCancel.Enabled = false;
            _conversionStage.Text = "正在取消 FreeRTOS 补全预览…";
            LayoutConversionPage();
        };
        const string hint = "按当前 MDK 工程与目标检查 FreeRTOS GCC 移植层；预览全部文件差异，确认后补入缺失文件。";
        _navTips.SetToolTip(_conversionFreeRtosDownload, hint);
        _navTips.SetToolTip(_conversionFreeRtosImport, hint + " 可选择本地移植层目录。");
    }

    private bool CanRepairConversionFreeRtos => !_busy &&
        SelectedConversionDirection == ConversionDirection.MdkToCMake &&
        _conversionRoot != null && SelectedMdkProject() != null && _conversionTarget.SelectedIndex >= 0;

    private void UpdateFreeRtosRepairActions()
    {
        _conversionFreeRtosDownload.Visible = _conversionFreeRtosImport.Visible =
            SelectedConversionDirection == ConversionDirection.MdkToCMake;
        _conversionFreeRtosDownload.Enabled = _conversionFreeRtosImport.Enabled = CanRepairConversionFreeRtos;
        _conversionFreeRtosCancel.Visible = _conversionFreeRtosCancellation != null && !_conversionFreeRtosApplying;
        _conversionFreeRtosCancel.Enabled = _conversionFreeRtosCancellation is { IsCancellationRequested: false } && !_conversionFreeRtosApplying;
    }

    private async Task RepairConversionFreeRtosAsync(string? localDirectory = null)
    {
        if (!CanRepairConversionFreeRtos) return;
        var root = _conversionRoot!;
        var request = new ConversionRequest(root, ConversionDirection.MdkToCMake,
            _conversionTarget.Items[_conversionTarget.SelectedIndex], SelectedMdkProject());
        var button = localDirectory == null ? _conversionFreeRtosDownload : _conversionFreeRtosImport;
        var originalText = button.Text;
        var applied = false;
        var previewing = true;
        using var cancellation = new CancellationTokenSource();
        InvalidateConversionResult();
        _conversionFreeRtosCancellation = cancellation;
        BeginConversion(button, localDirectory == null ? "下载并检查中…" : "读取并检查中…");
        _conversionSteps.Reset("核对内核", localDirectory == null ? "下载移植层" : "读取本地移植层", "预览补全", "写入文件", "重新检测");
        _conversionSteps.SetStep(0, StepState.Running);
        _conversionProgress.Value = 0;
        _conversionStage.Text = "正在检查当前目标的 FreeRTOS 内核与缺失移植层…";
        AppendConversion(localDirectory == null ? "准备从官方来源获取与内核版本匹配的 FreeRTOS GCC 移植层。" : "读取本地 FreeRTOS 移植层：" + localDirectory);
        LayoutConversionPage();
        var progress = new Progress<FreeRtosRepairProgress>(value =>
        {
            if (!previewing || _conversionFreeRtosCancellation != cancellation || cancellation.IsCancellationRequested) return;
            _conversionProgress.Value = Math.Clamp(value.Percent, 0, 100);
            _conversionStage.Text = value.Stage;
            if (value.Percent > 0)
            {
                _conversionSteps.SetStep(0, StepState.Complete);
                _conversionSteps.SetStep(1, StepState.Running);
            }
            AppendConversion(value.Stage);
            LayoutConversionPage();
        });
        try
        {
            var plan = await Task.Run(() => FreeRtosPortRepair.PreviewAsync(request, localDirectory, progress, cancellation.Token), cancellation.Token);
            previewing = false;
            cancellation.Token.ThrowIfCancellationRequested();
            AppendConversion(plan.Summary);
            foreach (var note in plan.Notes) AppendConversion(note);
            _conversionSteps.SetStep(0, StepState.Complete);
            _conversionSteps.SetStep(1, StepState.Complete);
            if (plan.Changes.Count == 0)
            {
                _conversionStage.Text = "未发现需要补入的 FreeRTOS GCC 移植层文件。";
                _conversionProgress.Value = 100;
                _conversionNotice.ShowMessage(plan.Summary, false);
                return;
            }
            _conversionSteps.SetStep(2, StepState.Running);
            _conversionStage.Text = "FreeRTOS 补全计划已准备，请核对全部文件差异后确认写入。";
            // The preview dialog is the only confirmation before writing the plan.
            using var preview = new ChangePreviewDialog(root, "FreeRTOS GCC 移植层补全", plan.Changes);
            AddFreeRtosRepairPreviewDetails(preview, plan);
            if (preview.ShowDialog(this) != DialogResult.OK)
            {
                _conversionSteps.SetStep(2, StepState.Cancelled);
                _conversionStage.Text = "已取消 FreeRTOS 补全，工程未写入。";
                AppendConversion(_conversionStage.Text);
                return;
            }
            cancellation.Token.ThrowIfCancellationRequested();
            _conversionFreeRtosApplying = true;
            UpdateFreeRtosRepairActions();
            _conversionSteps.SetStep(2, StepState.Complete);
            _conversionSteps.SetStep(3, StepState.Running);
            _conversionStage.Text = "正在核对预览输入并写入缺失文件…";
            LayoutConversionPage();
            var record = await Task.Run(() => FreeRtosPortRepair.Apply(plan));
            applied = record != null;
            if (applied) InvalidateCompletion();
            _conversionSteps.SetStep(3, StepState.Complete);
            _conversionProgress.Value = 100;
            _conversionStage.Text = applied ? "FreeRTOS 移植层已补全，正在准备重新检测工程。" : "没有需要写入的 FreeRTOS 移植层文件。";
            AppendConversion(_conversionStage.Text);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            for (var i = 0; i < _conversionSteps.States.Count; i++)
                if (_conversionSteps.States[i] == StepState.Running) _conversionSteps.SetStep(i, StepState.Cancelled);
            _conversionStage.Text = "已取消 FreeRTOS 补全，工程未写入。";
            AppendConversion(_conversionStage.Text);
        }
        catch (Exception ex)
        {
            ConversionProblem("FreeRTOS 补全未完成", ex);
            for (var i = 0; i < _conversionSteps.States.Count; i++)
                if (_conversionSteps.States[i] == StepState.Running) _conversionSteps.SetStep(i, StepState.Attention);
        }
        finally
        {
            previewing = false;
            _conversionFreeRtosCancellation = null;
            _conversionFreeRtosApplying = false;
            EndConversion(button, originalText);
        }
        if (!applied) return;
        var repairLog = _conversionLog.Text;
        await LoadConversionProjectAsync(root, false);
        _conversionLog.Text = repairLog + _conversionLog.Text;
        if (_conversionInspection == null)
        {
            AppendConversion("FreeRTOS 补全已写入，但工程重新检测未完成。请重新检测并预览转换。");
            return;
        }
        if (!RestoreFreeRtosRepairSelection(request))
        {
            const string message = "FreeRTOS GCC 移植层已补全，原 MDK 工程或构建目标已不可用。请重新选择工程文件和目标，再预览转换。";
            _conversionStage.Text = message;
            _conversionNotice.ShowMessage(message, true);
            AppendConversion(message);
            UpdateConversionActions();
            LayoutConversionPage();
            return;
        }
        BeginConversion(button, "检查剩余阻断…");
        _conversionSteps.SetStep(0, StepState.Complete);
        _conversionSteps.SetStep(1, StepState.Running);
        _conversionStage.Text = "正在重新检查原 MDK 工程与目标的转换配置…";
        LayoutConversionPage();
        try
        {
            // Check the same request without writing any conversion files.
            var conversion = await Task.Run(() => CubeMxConverter.CreatePlan(request));
            _conversionIssues = conversion.Issues;
            AppendConversion(conversion.Summary);
            var remaining = _conversionIssues.Count(x => x.Blocking);
            var message = "FreeRTOS GCC 移植层已补全，已重新检查原 MDK 工程与目标，请重新预览转换。" +
                (remaining > 0 ? $" 仍有 {remaining} 项转换阻断，请查看转换说明。" : "");
            _conversionSteps.SetStep(1, remaining > 0 ? StepState.Attention : StepState.Complete);
            _conversionProgress.Value = 100;
            _conversionStage.Text = message;
            _conversionNotice.ShowMessage(message, remaining > 0, _conversionIssues.Count > 0 ? "查看转换说明" : null,
                _conversionIssues.Count > 0 ? ShowConversionIssues : null);
            AppendConversion(message);
        }
        catch (Exception ex)
        {
            _conversionSteps.SetStep(1, StepState.Attention);
            ConversionProblem("补全已写入，转换重新检查未完成", ex);
        }
        finally { EndConversion(button, originalText); }
    }

    private bool RestoreFreeRtosRepairSelection(ConversionRequest request)
    {
        _conversionUpdating = true;
        try
        {
            var sourceIndex = -1;
            if (request.SourceProject != null)
            {
                var expectedSource = Path.GetFullPath(request.SourceProject, request.Root);
                for (var i = 0; i < _conversionSourcePaths.Count; i++)
                    if (Path.GetFullPath(_conversionSourcePaths[i], request.Root).Equals(expectedSource, StringComparison.OrdinalIgnoreCase))
                    { sourceIndex = i; break; }
            }
            _conversionSource.SelectedIndex = sourceIndex;
            _conversionTarget.SelectedIndex = -1;
            _conversionTarget.Items.Clear();
            if (sourceIndex < 0) return false;
            PopulateMdkTargets();
            _conversionUpdating = true;
            _conversionTarget.SelectedIndex = _conversionTarget.Items.IndexOf(request.Target);
            return _conversionTarget.SelectedIndex >= 0;
        }
        finally { _conversionUpdating = false; }
    }

    private static void AddFreeRtosRepairPreviewDetails(ChangePreviewDialog preview, FreeRtosRepairPlan plan)
    {
        var layout = preview.Controls.OfType<TableLayoutPanel>().Single();
        var diff = layout.GetControlFromPosition(0, 1)!;
        var footer = layout.GetControlFromPosition(0, 2)!;
        layout.SuspendLayout();
        layout.RowCount = 4;
        layout.RowStyles.Insert(1, new RowStyle(SizeType.Absolute, 128));
        layout.SetRow(footer, 3);
        layout.SetRow(diff, 2);
        layout.Controls.Add(new TextBox
        {
            ReadOnly = true, Multiline = true, ScrollBars = ScrollBars.Vertical,
            BorderStyle = BorderStyle.None, BackColor = preview.BackColor, ForeColor = preview.ForeColor,
            Dock = DockStyle.Fill, TabStop = true,
            Text = plan.Summary + Environment.NewLine + string.Join(Environment.NewLine, plan.Notes)
        }, 0, 1);
        layout.ResumeLayout();
    }
}
