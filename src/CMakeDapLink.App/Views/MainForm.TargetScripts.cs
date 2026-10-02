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
        if (requested.Length == 0 && (chip == null || ProjectInspector.TargetForChip(chip) == null)) return;
        _stage.Text = "检查本机 Target 配置与依赖脚本…";
        var previous = _tools;
        var resolution = await Task.Run(() => OpenOcdScripts.ResolveAsync(previous, chip,
            automatic ? null : requested, cancellation: _repairCancellation?.Token ?? CancellationToken.None));
        _targetScriptResolution = resolution;
        if (!resolution.Ready)
        {
            _toolFailures["Target"] = resolution.Details;
            return;
        }
        _tools = resolution.Tools;
        if (automatic)
        {
            _automaticTarget = resolution.Target;
            _target.Text = _automaticTarget ?? "";
        }
        _targetScriptResolution = resolution;
        _toolFailures.Remove("OpenOcd"); _toolFailures.Remove("Target");
        if (_tools.OpenOcd != previous.OpenOcd || _tools.Scripts != previous.Scripts)
        {
            Append("已采用本机配套 OpenOCD：" + _tools.OpenOcd + "\n脚本目录：" + _tools.Scripts);
            var version = await ProcessTools.RunAsync(_tools.OpenOcd!, ["--version"],
                _project?.Root ?? Environment.CurrentDirectory, TimeSpan.FromSeconds(8));
            var number = System.Text.RegularExpressions.Regex.Match(version.Output, @"\d+(?:\.\d+)+").Value;
            _toolTiles["OpenOCD"].SetStatus(number.Length > 0 ? number : "配置已验证", true);
        }
    }

    private bool ShouldCompleteTargetAutomatically() => _project?.IsCMakeProject == true &&
        _project.Chip != null && ProjectInspector.TargetForChip(_project.Chip) != null &&
        _targetScriptResolution?.Ready != true;
}
