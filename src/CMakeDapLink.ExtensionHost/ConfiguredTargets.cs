using System.Text.Json;

namespace CMakeDapLink.ExtensionHost;

// Core's target reader is internal; this adapter reads only the standard File API target names.
internal static class ConfiguredTargets
{
    public static IReadOnlyList<string> Read(string root, string buildDirectory, string configuration)
    {
        var reply = WorkspaceSafety.Child(root, Path.Combine(buildDirectory, ".cmake", "api", "v1", "reply"));
        var indexPath = Directory.EnumerateFiles(reply, "index-*.json").OrderDescending(StringComparer.Ordinal).First();
        using var index = ReadFile(root, indexPath);
        var reference = index.RootElement.GetProperty("objects").EnumerateArray().First(x => x.GetProperty("kind").GetString() == "codemodel" && x.GetProperty("version").GetProperty("major").GetInt32() == 2);
        using var model = ReadReference(root, reply, reference);
        var paths = model.RootElement.GetProperty("paths");
        foreach (var (property, expected) in new[] { ("source", root), ("build", buildDirectory) })
            if (!Path.TrimEndingDirectorySeparator(Path.GetFullPath(paths.GetProperty(property).GetString()!)).Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("CMake File API 与当前工程/构建目录不一致。");
        var configurations = model.RootElement.GetProperty("configurations").EnumerateArray().ToArray();
        var selected = configurations.FirstOrDefault(x => x.GetProperty("name").GetString()?.Equals(configuration, StringComparison.OrdinalIgnoreCase) == true);
        if (selected.ValueKind == JsonValueKind.Undefined && configurations.Length == 1 && string.IsNullOrEmpty(configurations[0].GetProperty("name").GetString())) selected = configurations[0];
        if (selected.ValueKind == JsonValueKind.Undefined) throw new InvalidDataException("CMake File API 中没有配置：" + configuration);
        var targets = new List<string>();
        foreach (var targetReference in selected.GetProperty("targets").EnumerateArray())
        {
            using var target = ReadReference(root, reply, targetReference);
            if (target.RootElement.GetProperty("type").GetString() == "EXECUTABLE") targets.Add(target.RootElement.GetProperty("name").GetString()!);
        }
        return targets;
    }
    private static JsonDocument ReadReference(string root, string reply, JsonElement reference)
    {
        var name = reference.GetProperty("jsonFile").GetString()!;
        if (name != Path.GetFileName(name) || name.Contains('/') || name.Contains('\\')) throw new InvalidDataException("CMake 返回无效目标信息路径。");
        return ReadFile(root, Path.Combine(reply, name));
    }
    private static JsonDocument ReadFile(string root, string path)
    {
        path = WorkspaceSafety.Child(root, path);
        if (new FileInfo(path).Length > 16 * 1024 * 1024) throw new InvalidDataException("CMake File API 文件过大。");
        return JsonDocument.Parse(File.ReadAllBytes(path));
    }
}
