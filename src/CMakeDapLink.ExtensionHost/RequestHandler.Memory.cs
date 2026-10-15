using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CMakeDapLink.Core;

namespace CMakeDapLink.ExtensionHost;

internal sealed partial class RequestHandler
{
    private Task<object> MemoryAsync(JsonElement payload, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var project = Project(payload);
        var build = WorkspaceSafety.Child(root, Text(payload, "buildDirectory") ?? project.BuildDirectory ?? Path.Combine(root, "build", "daplink-debug"));
        if (!Directory.Exists(build)) throw new DirectoryNotFoundException("请先编译所选配置，再分析生成的 ELF。");
        var configuration = Configuration(payload);
        var selected = Text(payload, "firmwareElfPath");
        if (string.IsNullOrWhiteSpace(selected))
        {
            var firmware = CMakeBuildPlan.FindElfs(build, configuration);
            if (firmware.Count != 1) throw new ArgumentException("请选择一个实际编译生成的 ELF，再分析内存。");
            selected = firmware[0];
        }
        var elf = WorkspaceSafety.Child(build, Path.GetFullPath(selected, root));
        if (!File.Exists(elf) || !Path.GetExtension(elf).Equals(".elf", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("内存分析需要当前构建目录内现有的 ELF 文件。");
        var notes = new List<string>();
        ValidateMemoryCache(build);
        var fragments = MemoryLinkFragments(build, elf, configuration, notes, out var workingDirectory);
        var scripts = MemoryScriptPaths(build, workingDirectory, fragments, notes);
        cancellation.ThrowIfCancellationRequested();
        progress(45, "分析 ELF 静态内存", elf);
        var report = FirmwareMemoryAnalyzer.Analyze(elf, scripts);
        cancellation.ThrowIfCancellationRequested();
        return Task.FromResult<object>(report with { Notes = notes.Concat(report.Notes).Distinct().ToArray() });
    }

    private void ValidateMemoryCache(string build)
    {
        var cache = WorkspaceSafety.Child(build, Path.Combine(build, "CMakeCache.txt"));
        if (!File.Exists(cache)) return;
        foreach (var line in MemoryRead(cache).Split('\n'))
        {
            foreach (var (key, expected) in new[] { ("CMAKE_HOME_DIRECTORY:INTERNAL=", root), ("CMAKE_CACHEFILE_DIR:INTERNAL=", build) })
                if (line.StartsWith(key, StringComparison.Ordinal) && !MemoryPathEquals(line[key.Length..].TrimEnd('\r'), expected))
                    throw new InvalidDataException("构建缓存与当前工程或目录不一致；请重新配置并编译后分析内存。");
        }
    }

    private IReadOnlyList<string> MemoryLinkFragments(string build, string elf, string configuration, List<string> notes, out string workingDirectory)
    {
        workingDirectory = build;
        var reply = WorkspaceSafety.Child(build, Path.Combine(build, ".cmake", "api", "v1", "reply"));
        var indexPath = Directory.Exists(reply) ? Directory.EnumerateFiles(reply, "index-*.json").OrderDescending(StringComparer.Ordinal).FirstOrDefault() : null;
        if (indexPath != null)
        {
            using var index = JsonDocument.Parse(MemoryRead(WorkspaceSafety.Child(build, indexPath)));
            var reference = MemoryArray(index.RootElement, "objects").FirstOrDefault(item => Text(item, "kind") == "codemodel");
            if (reference.ValueKind != JsonValueKind.Undefined)
            {
                using var model = Reply(reference);
                var paths = model.RootElement.GetProperty("paths");
                if (!MemoryPathEquals(Required(paths, "source"), root) || !MemoryPathEquals(Required(paths, "build"), build))
                    throw new InvalidDataException("File API 工程或构建目录不匹配；请重新配置并编译后分析内存。");
                var configurations = MemoryArray(model.RootElement, "configurations");
                var chosen = configurations.FirstOrDefault(item => Text(item, "name") == configuration);
                if (chosen.ValueKind == JsonValueKind.Undefined && configurations.Length == 1 && string.IsNullOrEmpty(Text(configurations[0], "name"))) chosen = configurations[0];
                if (chosen.ValueKind == JsonValueKind.Undefined) throw new InvalidDataException("File API 中没有当前配置；请编译所选配置后分析内存。");
                var matches = new List<(string[] Fragments, string Directory)>();
                var cache = WorkspaceSafety.Child(build, Path.Combine(build, "CMakeCache.txt"));
                var makefiles = File.Exists(cache) && Regex.IsMatch(MemoryRead(cache), @"(?m)^CMAKE_GENERATOR:INTERNAL=.*Makefiles\s*$");
                foreach (var target in MemoryArray(chosen, "targets"))
                {
                    using var document = Reply(target); var data = document.RootElement;
                    if (Text(data, "type") != "EXECUTABLE" || !MemoryArray(data, "artifacts").Any(artifact =>
                        MemoryPathEquals(Path.GetFullPath(Required(artifact, "path"), build), elf))) continue;
                    if (data.TryGetProperty("link", out var link))
                    {
                        var directory = makefiles && data.TryGetProperty("paths", out var targetPaths)
                            ? WorkspaceSafety.Child(build, Path.GetFullPath(Text(targetPaths, "build") ?? ".", build), true) : build;
                        matches.Add((MemoryArray(link, "commandFragments").Select(fragment => Text(fragment, "fragment") ?? "").ToArray(), directory));
                    }
                }
                if (matches.Count == 1) { workingDirectory = matches[0].Directory; return matches[0].Fragments; }
                if (matches.Count > 1) { notes.Add("ELF 对应多个链接目标；不推测链接脚本和区域容量。"); return []; }
            }
            JsonDocument Reply(JsonElement targetReference)
            {
                var name = Required(targetReference, "jsonFile");
                if (name != Path.GetFileName(name)) throw new InvalidDataException("CMake 返回了无效的 File API 引用文件。");
                return JsonDocument.Parse(MemoryRead(WorkspaceSafety.Child(build, Path.Combine(reply, name))));
            }
        }
        // Existing build trees may predate the File API query. Read only the matching link rule.
        foreach (var ninjaName in new[] { "build.ninja", Path.Combine("CMakeFiles", "impl-" + configuration + ".ninja") })
        {
            var ninja = WorkspaceSafety.Child(build, Path.Combine(build, ninjaName));
            if (!File.Exists(ninja)) continue;
            var lines = MemoryRead(ninja).Replace("$\r\n", "").Replace("$\n", "").Split('\n');
            for (var index = 0; index < lines.Length; index++)
            {
                if (!lines[index].StartsWith("build ", StringComparison.Ordinal)) continue;
                var separator = Regex.Match(lines[index], @"(?<!\$):\s+");
                if (!separator.Success || !lines[index][separator.Index..].Contains("EXECUTABLE_LINKER", StringComparison.Ordinal)) continue;
                var outputs = Regex.Matches(lines[index][6..separator.Index], @"(?:\$[ :$]|[^\s])+")
                    .Select(match => match.Value.Replace("$ ", " ").Replace("$:", ":").Replace("$$", "$"));
                if (!outputs.Any(output => output != "|" && MemoryPathEquals(Path.GetFullPath(output, build), elf))) continue;
                var fragments = new List<string>();
                for (var next = index + 1; next < lines.Length && !string.IsNullOrWhiteSpace(lines[next]) && char.IsWhiteSpace(lines[next][0]); next++)
                {
                    var binding = Regex.Match(lines[next], @"^\s*(?:LINK_FLAGS|LINK_PATH)\s*=\s*(?<value>.*)");
                    if (binding.Success) fragments.Add(binding.Groups["value"].Value.Replace("$ ", " ").Replace("$:", ":").Replace("$$", "$"));
                }
                return fragments;
            }
        }
        foreach (var directory in new[] { build, Path.GetDirectoryName(elf)! }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var cmake = WorkspaceSafety.Child(build, Path.Combine(directory, "CMakeFiles"));
            if (!Directory.Exists(cmake)) continue;
            foreach (var target in Directory.EnumerateDirectories(cmake, "*.dir").Take(256))
            {
                var command = WorkspaceSafety.Child(build, Path.Combine(target, "link.txt"));
                if (!File.Exists(command)) continue;
                var content = MemoryRead(command); var tokens = MemoryTokens(content);
                for (var index = 0; index + 1 < tokens.Count; index++)
                    if (tokens[index] == "-o" && MemoryPathEquals(Path.GetFullPath(tokens[index + 1], directory), elf))
                    { workingDirectory = directory; return [content]; }
            }
        }
        notes.Add("未找到与所选 ELF 唯一匹配的实际链接命令；链接区域容量显示为未知。");
        return [];
    }

    private static IReadOnlyList<string> MemoryScriptPaths(string build, string workingDirectory, IReadOnlyList<string> fragments, List<string> notes)
    {
        var tokens = fragments.SelectMany(MemoryTokens).ToList(); var responseCount = 0;
        for (var index = 0; index < tokens.Count; index++)
        {
            if (!tokens[index].StartsWith('@')) continue;
            var response = WorkspaceSafety.Child(build, Path.GetFullPath(tokens[index][1..], workingDirectory));
            if (index > 8192 || !File.Exists(response)) { notes.Add("链接响应文件无法读取；未解析部分保持未知。"); return []; }
            var expanded = MemoryTokens(MemoryRead(response));
            // Keep expansion bounded and never execute a response file.
            if (tokens.Count + expanded.Count > 32768) { notes.Add("链接响应文件超过分析范围；未解析部分保持未知。"); return []; }
            tokens.RemoveAt(index); tokens.InsertRange(index, expanded); index--;
            if (++responseCount > 32) { notes.Add("链接响应文件超过分析范围；未解析部分保持未知。"); return []; }
        }
        var flat = tokens.Where(token => token != "-Xlinker").SelectMany(token => token.StartsWith("-Wl,", StringComparison.Ordinal) ? token[4..].Split(',') : [token]).ToArray();
        var scripts = new List<string>(); var search = new List<string> { workingDirectory };
        for (var index = 0; index < flat.Length; index++)
        {
            var token = flat[index];
            if (token == "-L" && index + 1 < flat.Length) search.Add(Path.GetFullPath(flat[++index], workingDirectory));
            else if (token.StartsWith("-L", StringComparison.Ordinal) && token.Length > 2) search.Add(Path.GetFullPath(token[2..], workingDirectory));
            else if (token == "-T" || token == "--script") { if (index + 1 < flat.Length) scripts.Add(flat[++index]); }
            else if (token.StartsWith("-T", StringComparison.Ordinal) && token.Length > 2) scripts.Add(token[2..]);
            else if (token.StartsWith("--script=", StringComparison.Ordinal)) scripts.Add(token[9..]);
        }
        var result = new List<string>();
        foreach (var script in scripts)
        {
            var candidates = search.Select(directory => Path.GetFullPath(script, directory)).Distinct(StringComparer.OrdinalIgnoreCase).Where(File.Exists).ToArray();
            if (candidates.Length != 1) { notes.Add("实际链接命令中的脚本路径无法唯一解析；未解析区域容量保持未知。"); return []; }
            WorkspaceSafety.Ordinary(candidates[0]); result.Add(candidates[0]);
        }
        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static IReadOnlyList<string> MemoryTokens(string command)
    {
        var tokens = new List<string>(); var token = new StringBuilder(); char quote = '\0';
        for (var index = 0; index < command.Length; index++)
        {
            var value = command[index];
            if (value == '"' || value == '\'' && (quote == '\'' || quote == '\0' && token.Length == 0))
            {
                if (quote == value) quote = '\0'; else if (quote == '\0') quote = value; else token.Append(value);
            }
            else if (char.IsWhiteSpace(value) && quote == '\0') { if (token.Length > 0) { tokens.Add(token.ToString()); token.Clear(); } }
            else if (value == '\\' && quote == '"' && index + 1 < command.Length && command[index + 1] == '"') { token.Append('"'); index++; }
            else token.Append(value);
        }
        if (token.Length > 0) tokens.Add(token.ToString());
        return tokens;
    }

    private static JsonElement[] MemoryArray(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().ToArray() : [];
    private static bool MemoryPathEquals(string left, string right) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)).Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)), StringComparison.OrdinalIgnoreCase);
    private static string MemoryRead(string path) => new FileInfo(path).Length <= 16 * 1024 * 1024 ? File.ReadAllText(path) : throw new InvalidDataException("构建元数据超过内存分析读取范围。");
}
