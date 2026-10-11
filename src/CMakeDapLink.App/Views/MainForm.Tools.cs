using CMakeDapLink.Core;

namespace CMakeDapLink.App;

public sealed partial class MainForm
{
    private ToolDiscoveryResult? _toolDiscovery;
    private bool _toolSelectionLinksReady;

    private void PrepareToolSelectionLinks()
    {
        if (_toolSelectionLinksReady) return;
        _toolSelectionLinksReady = true;
        foreach (var (name, tile) in _toolTiles)
        {
            var key = ToolKey(name);
            tile.Cursor = Cursors.Hand;
            tile.AccessibleName = name + "：选择工具版本";
            tile.Click += async (_, _) => await BrowseToolAsync(key);
            foreach (Control child in tile.Controls)
            {
                child.Cursor = Cursors.Hand;
                child.Click += async (_, _) => await BrowseToolAsync(key);
            }
            _navTips.SetToolTip(tile, "点击选择检测到的版本或手动指定工具路径");
        }
    }

    private string ToolPathStatus(string key) => _toolDiscovery?.CandidatesFor(key).Count > 1 ? "待选择（检测到多个版本）" : "未找到";

    private async Task<bool> SelectToolPathAsync(string key, string executable)
    {
        try
        {
            // Keep automatically detected other tools for this session; persist only this key.
            var saved = await ManagedTools.SelectToolAsync(key, executable, _tools.Scripts, _repairCancellation?.Token ?? CancellationToken.None);
            _tools = key switch
            {
                "CMake" => _tools with { CMake = saved.CMake }, "Ninja" => _tools with { Ninja = saved.Ninja },
                "Compiler" => _tools with { Compiler = saved.Compiler },
                "OpenOcd" => _tools with { OpenOcd = saved.OpenOcd, Scripts = saved.Scripts },
                _ => throw new ArgumentException("未知工具：" + key)
            };
            Append("已保存 " + ToolVersionManager.DisplayName(key) + " 路径：" + executable);
            InvalidateCompletion();
            await RefreshEnvironmentAsync();
            Notify("工具路径已验证、保存并重新检测。", actionText: "查看配置详情", action: ShowDetails);
            return true;
        }
        catch (Exception ex)
        {
            Notify("工具选择未保存：" + ex.Message, true, "重新选择", () => _ = BrowseToolAsync(key));
            return false;
        }
    }
}
