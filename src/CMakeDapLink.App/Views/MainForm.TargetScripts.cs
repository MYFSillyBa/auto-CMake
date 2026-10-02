using CMakeDapLink.Core;

namespace CMakeDapLink.App;

public sealed partial class MainForm
{
    private OpenOcdScriptResolution? _targetScriptResolution;
    private string? _automaticTarget;

    private void SetAutomaticTarget(string? target, bool preserveManual = false)
    {
        var manual = preserveManual && !string.IsNullOrWhiteSpace(_target.Text) &&
            !string.Equals(_target.Text.Trim().Replace('\\', '/'), _automaticTarget, StringComparison.OrdinalIgnoreCase);
        _automaticTarget = target;
        if (!manual) _target.Text = target ?? "";
        _targetScriptResolution = null;
        _toolFailures.Remove("Target");
    }

    private async Task RefreshTargetScriptsAsync()
    {
        _targetScriptResolution = null;
        var requested = _target.Text.Trim().Replace('\\', '/');
        var automatic = requested.Length == 0 || string.Equals(requested, _automaticTarget, StringComparison.OrdinalIgnoreCase);
        var chip = _project?.Chip;
        var cancellation = _repairCancellation?.Token ?? CancellationToken.None;
        _stage.Text = "准备内置 Target 配置与依赖脚本…";
        var scripts = _tools.Scripts ?? throw new InvalidOperationException("内置脚本尚未准备。");
        var target = automatic && chip != null ? ProjectInspector.TargetForChip(chip) : requested;
        if (string.IsNullOrWhiteSpace(target)) return;
        if (automatic)
        {
            _automaticTarget = OpenOcdScripts.AvailableTarget(scripts, target);
            _target.Text = _automaticTarget ?? "";
            target = _automaticTarget!;
        }
        var missing = OpenOcdScripts.MissingFiles(scripts, target);
        if (missing.Count > 0)
        {
            _toolFailures["Target"] = "内置脚本不包含当前配置或依赖：" + string.Join("、", missing);
            _targetScriptResolution = new(_tools, target, false, _toolFailures["Target"]);
            return;
        }
        if (!File.Exists(_tools.OpenOcd) || _toolFailures.ContainsKey("OpenOcd"))
        {
            _targetScriptResolution = new(_tools, target, false, "内置 Target 已准备，需要可运行的 OpenOCD 验证软件配置。");
            return;
        }
        try
        {
            await OpenOcdScripts.ValidateAsync(_tools, target, cancellation);
            _targetScriptResolution = new(_tools, target, true, "内置 Target 与依赖已通过 OpenOCD 软件解析（未连接硬件）。");
            _toolFailures.Remove("Target");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _toolFailures["Target"] = "内置脚本解析未通过：" + ex.Message;
            _targetScriptResolution = new(_tools, target, false, _toolFailures["Target"]);
        }
    }
}
