using System.Text.Json;

namespace CMakeDapLink.Core;

public static class CurrentFirmwareArtifacts
{
    public static void PrepareQuery(string buildDirectory)
    {
        var query = Path.Combine(Path.GetFullPath(buildDirectory), ".cmake", "api", "v1", "query", "client-cmake-daplink");
        Directory.CreateDirectory(query);
        File.WriteAllText(Path.Combine(query, "codemodel-v2"), "");
    }

    public static IReadOnlyList<string> Find(string buildDirectory, string configuration)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(buildDirectory));
        var reply = Path.Combine(root, ".cmake", "api", "v1", "reply");
        var indexPath = Directory.Exists(reply)
            ? Directory.EnumerateFiles(reply, "index-*.json").OrderDescending(StringComparer.Ordinal).FirstOrDefault()
            : null;
        if (indexPath == null)
            throw new InvalidOperationException("未找到 CMake 构建目标信息，请使用配置并验证重新生成。不会从旧 ELF 文件中猜测固件。");
        using var index = Read(indexPath);
        var modelReference = index.RootElement.GetProperty("objects").EnumerateArray()
            .FirstOrDefault(x => x.GetProperty("kind").GetString() == "codemodel" &&
                                 x.GetProperty("version").GetProperty("major").GetInt32() == 2);
        if (modelReference.ValueKind == JsonValueKind.Undefined)
            throw new InvalidOperationException("CMake 尚未返回 codemodel-v2，请重新配置工程。");
        using var model = Read(ReplyPath(reply, modelReference));
        var modelRoot = model.RootElement;
        if (!Path.TrimEndingDirectorySeparator(Path.GetFullPath(modelRoot.GetProperty("paths").GetProperty("build").GetString()!)).Equals(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("CMake 目标信息与当前构建目录不一致，请重新配置工程。");
        var configurations = modelRoot.GetProperty("configurations").EnumerateArray().ToArray();
        var selected = configurations.FirstOrDefault(x => string.Equals(x.GetProperty("name").GetString(), configuration, StringComparison.OrdinalIgnoreCase));
        if (selected.ValueKind == JsonValueKind.Undefined && configurations.Length == 1 &&
            string.IsNullOrEmpty(configurations[0].GetProperty("name").GetString()))
            selected = configurations[0];
        if (selected.ValueKind == JsonValueKind.Undefined)
            throw new InvalidOperationException($"CMake 构建目标中没有 {configuration} 配置，请重新配置并编译所选配置。");
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var reference in selected.GetProperty("targets").EnumerateArray())
        {
            using var target = Read(ReplyPath(reply, reference));
            var data = target.RootElement;
            if (data.GetProperty("type").GetString() != "EXECUTABLE" || !data.TryGetProperty("artifacts", out var artifacts)) continue;
            foreach (var artifact in artifacts.EnumerateArray())
            {
                var path = Path.GetFullPath(artifact.GetProperty("path").GetString()!, root);
                if (Path.GetExtension(path).Equals(".elf", StringComparison.OrdinalIgnoreCase) && File.Exists(path)) files.Add(path);
            }
        }
        return files.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static JsonDocument Read(string path) => JsonDocument.Parse(File.ReadAllText(path));

    private static string ReplyPath(string reply, JsonElement reference)
    {
        var file = reference.GetProperty("jsonFile").GetString();
        if (string.IsNullOrEmpty(file) || Path.GetFileName(file) != file || file.Contains('/') || file.Contains('\\'))
            throw new InvalidOperationException("CMake 返回了无效的目标信息路径。");
        return Path.Combine(reply, file);
    }
}
