using System.Text.Json;

namespace CMakeDapLink.Core;

public sealed record ToolRepairSettings(string GitHubMirror = "https://ghfast.top/{url}");

public static class ManagedTools
{
    public const string Root = @"D:\CMake_Tools";
    public static string SettingsPath => Path.Combine(Root, "download-sources.json");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    public static ToolPaths LoadPaths()
    {
        try { return JsonSerializer.Deserialize<ToolPaths>(File.ReadAllText(Path.Combine(Root, "tool-paths.json"))) ?? new(null, null, null, null, null); }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return new(null, null, null, null, null); }
    }
    public static void SavePaths(ToolPaths paths) => SaveJson(Path.Combine(Root, "tool-paths.json"), paths);
    public static ToolRepairSettings LoadSettings()
    {
        if (!File.Exists(SettingsPath)) return new();
        return JsonSerializer.Deserialize<ToolRepairSettings>(File.ReadAllText(SettingsPath)) ?? new();
    }
    public static void SaveSettings(ToolRepairSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.GitHubMirror) &&
            (!settings.GitHubMirror.Contains("{url}", StringComparison.Ordinal) ||
             !Uri.TryCreate(settings.GitHubMirror.Replace("{url}", "https://github.com/example/file.zip"), UriKind.Absolute, out var uri) || uri.Scheme != "https"))
            throw new ArgumentException("镜像源需使用 HTTPS，并包含 {url}，例如 https://ghfast.top/{url}；留空则关闭加速镜像。");
        SaveJson(SettingsPath, settings);
    }
    private static void SaveJson<T>(string path, T value)
    {
        Directory.CreateDirectory(Root);
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(temp, JsonSerializer.Serialize(value, JsonOptions));
        File.Move(temp, path, true);
    }
}
