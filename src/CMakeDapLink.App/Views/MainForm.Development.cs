using CMakeDapLink.Core;

namespace CMakeDapLink.App;

public sealed partial class MainForm
{
    private readonly Button _navDevelopment = Button("开发与分析", false);

    private void OpenDevelopmentTools()
    {
        if (_busy) return;
        var root = _project?.Root ?? _conversionRoot;
        if (root == null || !Directory.Exists(root))
        {
            using var picker = new FolderBrowserDialog { Description = "选择要配置、分析或调试的 STM32 工程", UseDescriptionForTitle = true };
            if (picker.ShowDialog(this) != DialogResult.OK) return;
            root = picker.SelectedPath;
        }
        SetupOptions? initial = null;
        if (_project?.Root == root)
        {
            initial = new(root, _tools.CMake ?? "", _tools.Ninja ?? "", _tools.Compiler ?? "", _tools.OpenOcd ?? "",
                _tools.Scripts ?? "", _target.Text.Trim().Replace('\\', '/'), _project.ConfigurePreset,
                _project.BuildPreset, _project.BuildDirectory, _project.ToolchainFile,
                _completedFirmware != null && File.Exists(_completedFirmware) ? _completedFirmware : null)
            { BuildConfiguration = SelectedFixedConfiguration() ?? _defaultConfiguration };
        }
        using var dialog = new DesktopDevelopmentDialog(root, _tools, initial, _log.Text);
        dialog.ShowDialog(this);
    }
}
