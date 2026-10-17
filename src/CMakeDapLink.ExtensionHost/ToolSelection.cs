using System.Text.Json;
using CMakeDapLink.Core;

namespace CMakeDapLink.ExtensionHost;

internal sealed partial class RequestHandler
{
    private sealed record PendingTool(string Key, string Title, string Instructions, IReadOnlyList<ToolCandidate> Candidates);

    private async Task<ToolDiscoveryResult> DiscoverToolsAsync(JsonElement payload, CancellationToken cancellation, bool enumerateAll = false)
    {
        var preferred = payload.TryGetProperty("tools", out var value) && value.ValueKind == JsonValueKind.Object ? value.Deserialize<ToolPaths>(json) : null;
        if (preferred != null) WorkspaceSafety.Tools(preferred);
        progress(5, "检测本地工具安装", null);
        // Explicit workspace selections need existence checks, not another installation-tree scan.
        // Inspection and the version picker still enumerate installations; missing paths fall back to discovery.
        var completeSelection = !enumerateAll && preferred != null && ToolVersionManager.Keys.All(key =>
            File.Exists(ToolVersionManager.GetPath(preferred, key)));
        var options = completeSelection
            ? new ToolDiscoveryOptions(PathValue: "", IncludeDefaultRoots: false, LoadSavedPaths: false)
            : new ToolDiscoveryOptions(ManagedRoot: Manager(payload).Root);
        var discovery = await Task.Run(() => EnvironmentScanner.Discover(preferred, options, cancellation), cancellation);
        var tools = discovery.Selected with { Scripts = await BundledOpenOcdScripts.GetDirectoryAsync(cancellation) };
        WorkspaceSafety.Tools(tools);
        return discovery with { Selected = tools };
    }

    private static PendingTool[] PendingTools(ToolDiscoveryResult discovery) => ToolVersionManager.Keys
        .Where(key => ToolVersionManager.GetPath(discovery.Selected, key) == null && discovery.CandidatesFor(key).Count > 1)
        .Select(key => new PendingTool(key, ToolVersionManager.DisplayName(key) + " 检测到多个安装，待选择",
            "在工程配置或工具管理中点击“选择版本”，根据版本号和完整路径选择要使用的安装。验证通过后保存在当前工作区设置中。无需重新下载。",
            discovery.CandidatesFor(key))).ToArray();

    private async Task<object> SelectToolAsync(JsonElement payload, CancellationToken cancellation)
        => await ValidateToolAsync(WorkspaceSafety.ToolKey(Required(payload, "key")), Required(payload, "executable"), Manager(payload), cancellation);

    private async Task<object> ValidateToolAsync(string key, string executable, ToolVersionManager manager, CancellationToken cancellation)
    {
        var scripts = await BundledOpenOcdScripts.GetDirectoryAsync(cancellation);
        var selected = new ToolPaths(key == "CMake" ? executable : null, key == "Ninja" ? executable : null,
            key == "Compiler" ? executable : null, key == "OpenOcd" ? executable : null, scripts);
        WorkspaceSafety.Tools(selected);
        progress(25, "验证 " + ToolVersionManager.DisplayName(key), executable);
        // Validate only this choice. Workspace settings are saved by the extension after success.
        // Unrelated stale paths and global desktop settings must not affect this selection.
        var installation = new ToolInstallation(key, "", executable, scripts, Path.GetDirectoryName(executable)!, 0, false, false);
        var tools = await manager.ActivateAsync(installation, new(null, null, null, null, scripts), savePaths: false, cancellation: cancellation);
        var version = (await VersionsAsync(tools, cancellation)).Single(item => item.Key == key);
        if (!version.Available) throw new InvalidDataException(version.Problem ?? "工具版本验证未通过。");
        progress(90, "工具路径验证通过", executable);
        return new { key, tools, version = version.Version };
    }
}
