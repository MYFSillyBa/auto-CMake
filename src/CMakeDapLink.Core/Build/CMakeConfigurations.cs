using System.Text.Json;
using System.Text.RegularExpressions;

namespace CMakeDapLink.Core;

public static class CMakeConfigurations
{
    public static string Validate(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 || value != value.Trim() || value.EndsWith('.') ||
            value.Any(char.IsControl) || value.IndexOfAny([';', '/', '\\', ':', '*', '?', '"', '<', '>', '|']) >= 0)
            throw new ArgumentException("配置名称须为 1–128 个安全字符，不能包含控制字符、分号或路径分隔符。");
        return value;
    }

    public static IReadOnlyList<string> Discover(ProjectInfo project, string? buildDirectory = null)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        void Add(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            try { names.Add(Validate(value)); } catch (ArgumentException) { }
        }
        var preset = project.ConfigurePresets.FirstOrDefault(x => x.Name == project.ConfigurePreset);
        if (preset != null)
        {
            Add(preset.ConfigureConfiguration);
            foreach (var name in preset.Configurations) Add(name);
            foreach (var build in preset.BuildPresets) Add(build.Configuration);
        }
        var directory = buildDirectory ?? project.BuildDirectory;
        if (directory != null)
        {
            try
            {
                var cache = Path.Combine(directory, "CMakeCache.txt");
                ToolVersionManager.EnsureOrdinaryPath(cache);
                if (File.Exists(cache) && new FileInfo(cache).Length <= 8 * 1024 * 1024)
                    foreach (var line in File.ReadLines(cache))
                    {
                        var match = Regex.Match(line, @"^(CMAKE_BUILD_TYPE|CMAKE_CONFIGURATION_TYPES):[^=]*=(.*)$", RegexOptions.CultureInvariant);
                        if (match.Success) foreach (var name in match.Groups[2].Value.Split(';')) Add(name);
                    }
                var reply = Path.Combine(directory, ".cmake", "api", "v1", "reply");
                var indexPath = Directory.Exists(reply) ? Directory.EnumerateFiles(reply, "index-*.json").OrderDescending(StringComparer.Ordinal).FirstOrDefault() : null;
                if (indexPath != null && new FileInfo(indexPath).Length <= 8 * 1024 * 1024)
                {
                    ToolVersionManager.EnsureOrdinaryPath(indexPath);
                    using var index = JsonDocument.Parse(File.ReadAllText(indexPath));
                    foreach (var item in index.RootElement.GetProperty("objects").EnumerateArray())
                    {
                        if (item.GetProperty("kind").GetString() != "codemodel" || item.GetProperty("version").GetProperty("major").GetInt32() != 2) continue;
                        var file = item.GetProperty("jsonFile").GetString();
                        if (file == null || Path.GetFileName(file) != file || file.Contains('/') || file.Contains('\\')) continue;
                        var path = Path.Combine(reply, file);
                        ToolVersionManager.EnsureOrdinaryPath(path);
                        if (!File.Exists(path) || new FileInfo(path).Length > 8 * 1024 * 1024) continue;
                        using var model = JsonDocument.Parse(File.ReadAllText(path));
                        var paths = model.RootElement.GetProperty("paths");
                        if (!Path.GetFullPath(paths.GetProperty("source").GetString()!).Equals(Path.GetFullPath(project.Root), StringComparison.OrdinalIgnoreCase) ||
                            !Path.GetFullPath(paths.GetProperty("build").GetString()!).Equals(Path.GetFullPath(directory), StringComparison.OrdinalIgnoreCase)) continue;
                        foreach (var configuration in model.RootElement.GetProperty("configurations").EnumerateArray()) Add(configuration.GetProperty("name").GetString());
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or KeyNotFoundException or ArgumentException) { }
        }
        return names.Order(StringComparer.Ordinal).ToArray();
    }
}
