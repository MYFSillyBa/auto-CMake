using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CMakeDapLink.Core;

/// <summary>Creates reviewable editor configuration changes without touching disk or hardware.</summary>
public static class DevelopmentConfiguration
{
    public const string DebugName = "STM32 Project Helper: Debug";
    public const string DebugGroup = "STM32 Project Helper (managed)";

    public static IReadOnlyList<FileChange> PreviewClangd(SetupOptions options)
    {
        var directory = Path.GetFullPath(options.BuildDirectory ?? Path.Combine(options.Root, "build", "daplink-debug"), options.Root).Replace('\\', '/');
        var compiler = Path.GetFullPath(options.Compiler, options.Root).Replace('\\', '/');
        var changes = new List<FileChange>();
        Add(changes, Path.Combine(options.Root, ".clangd"), before => UpdateClangd(before, directory));
        Add(changes, Path.Combine(options.Root, ".vscode", "settings.json"), before => JsoncDevelopmentEditor.Edit(before, text =>
        {
            if (JsoncDevelopmentEditor.Parse(text) is not JsonObject settings) throw new InvalidDataException("现有 settings.json 必须是 JSON 对象。");
            var arguments = settings["clangd.arguments"] switch
            {
                null => new JsonArray(),
                JsonArray array => array,
                _ => throw new InvalidDataException("现有 clangd.arguments 必须是字符串数组。")
            };
            var desired = new JsonObject
            {
                ["clangd.arguments"] = ClangdArguments(arguments, directory, compiler),
                ["C_Cpp.intelliSenseEngine"] = "disabled"
            };
            return JsoncDevelopmentEditor.SetObject(text, desired);
        }));
        return changes;
    }

    public static IReadOnlyList<FileChange> PreviewDebug(SetupOptions options, string gdbPath) => PreviewDebugNamed(options, gdbPath, null);

    public static IReadOnlyList<FileChange> PreviewDebugNamed(SetupOptions options, string gdbPath, string? selectedName)
    {
        if (selectedName != null && (string.IsNullOrWhiteSpace(selectedName) || selectedName.Length > 100 || selectedName.Any(char.IsControl))) throw new ArgumentException("调试配置名称必须为 1–100 个字符，不能包含控制字符。");
        var probe = OpenOcdProbe.Resolve(options);
        var missing = OpenOcdScripts.MissingFiles(options.Scripts, options.TargetScript, probe.InterfaceScript);
        if (missing.Count > 0) throw new InvalidDataException("调试脚本或依赖缺少：" + string.Join("、", missing));
        if (options.FirmwareElfPath == null || !File.Exists(options.FirmwareElfPath)) throw new FileNotFoundException("请选择当前构建配置的 ELF 固件。");
        if (!Path.IsPathFullyQualified(gdbPath) || !File.Exists(gdbPath)) throw new FileNotFoundException("请选择 arm-none-eabi-gdb 的完整路径。", gdbPath);
        var server = Path.GetFullPath(options.OpenOcd, options.Root);
        if (!File.Exists(server)) throw new FileNotFoundException("OpenOCD 工具未找到。", server);
        ToolVersionManager.EnsureOrdinaryPath(gdbPath);
        ToolVersionManager.EnsureOrdinaryPath(server);
        var changes = new List<FileChange>();
        Add(changes, Path.Combine(options.Root, ".vscode", "launch.json"), before => JsoncDevelopmentEditor.Edit(before, text =>
        {
            if (JsoncDevelopmentEditor.Parse(text) is not JsonObject launch) throw new InvalidDataException("现有 launch.json 必须是 JSON 对象。");
            if (launch["configurations"] is not null and not JsonArray) throw new InvalidDataException("现有 launch.json 的 configurations 必须是数组。");
            var configurations = launch["configurations"] as JsonArray ?? new JsonArray();
            var owned = configurations.Select((node, index) => (node, index)).Where(x => IsManaged(x.node)).ToArray();
            if (owned.Length > 1) throw new InvalidDataException("launch.json 包含多个助手管理的调试配置，请先保留一个。");
            var names = configurations.Where(x => !IsManaged(x)).OfType<JsonObject>().Select(x => String(x, "name")).ToHashSet(StringComparer.Ordinal);
            var requestedName = selectedName?.Trim() ?? (owned.Length == 1 ? String((JsonObject)owned[0].node!, "name")! : DebugName);
            var name = requestedName;
            for (var suffix = 2; names.Contains(name); suffix++) name = requestedName + " (" + suffix + ")";
            var commands = new JsonArray("adapter speed " + options.AdapterSpeed);
            if (options.ResetMode == "connect-under-reset") commands.Add("reset_config srst_only srst_nogate connect_assert_srst");
            if (options.ResetMode == "sysresetreq") commands.Add("cortex_m reset_config sysresetreq");
            var desired = new JsonObject
            {
                ["name"] = name, ["type"] = "cortex-debug", ["request"] = "attach", ["servertype"] = "openocd",
                ["cwd"] = Path.GetFullPath(options.Root), ["executable"] = Path.GetFullPath(options.FirmwareElfPath),
                ["gdbPath"] = Path.GetFullPath(gdbPath), ["armToolchainPath"] = Path.GetDirectoryName(Path.GetFullPath(gdbPath)),
                ["serverpath"] = server, ["searchDir"] = new JsonArray(Path.GetFullPath(options.Scripts)),
                ["configFiles"] = new JsonArray(Path.GetFullPath(Path.Combine(options.Scripts, options.TargetScript))),
                ["openOCDPreConfigLaunchCommands"] = new JsonArray("source " + OpenOcdProbe.TclPath(Path.GetFullPath(Path.Combine(options.Scripts, probe.InterfaceScript))), "transport select " + probe.Transport),
                ["openOCDLaunchCommands"] = commands, ["rtos"] = "auto",
                ["presentation"] = new JsonObject { ["group"] = DebugGroup }
            };
            if (launch["configurations"] == null)
                return JsoncDevelopmentEditor.SetObject(text, new JsonObject { ["version"] = "0.2.0", ["configurations"] = new JsonArray(desired) });
            var (properties, _) = JsoncDevelopmentEditor.Scan(text, true);
            var span = properties.Single(x => x.Name == "configurations");
            var raw = text[span.Start..span.End];
            var replacements = new Dictionary<int, string?>();
            var additions = new List<string>();
            if (owned.Length == 1)
            {
                var entry = JsoncDevelopmentEditor.Scan(raw, false).Values[owned[0].index];
                replacements[owned[0].index] = JsoncDevelopmentEditor.SetObject(raw[entry.Start..entry.End], desired);
            }
            else additions.Add(desired.ToJsonString(JsoncDevelopmentEditor.PrintOptions));
            var updated = text[..span.Start] + JsoncDevelopmentEditor.RewriteArray(raw, replacements, additions) + text[span.End..];
            return launch["version"] == null ? JsoncDevelopmentEditor.SetObject(updated, new JsonObject { ["version"] = "0.2.0" }) : updated;
        }));
        return changes;
    }

    public static string ConfigurationName(byte[] launchBytes)
    {
        var (text, _, _) = JsoncTaskEditor.Decode(launchBytes);
        var launch = JsoncDevelopmentEditor.Parse(text) as JsonObject;
        var managed = (launch?["configurations"] as JsonArray)?.OfType<JsonObject>().SingleOrDefault(IsManaged);
        return managed == null ? throw new InvalidDataException("助手管理的调试配置未找到。") : String(managed, "name")!;
    }

    public static JsonElement Configuration(byte[] launchBytes)
    {
        var name = ConfigurationName(launchBytes);
        var (text, _, _) = JsoncTaskEditor.Decode(launchBytes);
        using var document = JsonDocument.Parse(text, JsoncDevelopmentEditor.ReadOptions);
        return document.RootElement.GetProperty("configurations").EnumerateArray().Single(entry => entry.TryGetProperty("name", out var value) && value.GetString() == name).Clone();
    }

    private static bool IsManaged(JsonNode? node) => node is JsonObject value && String(value, "type") == "cortex-debug" && String(value, "servertype") == "openocd"
        && !string.IsNullOrWhiteSpace(String(value, "name"))
        && value["presentation"] is JsonObject presentation && String(presentation, "group") == DebugGroup;
    private static string? String(JsonObject value, string key) => value[key] is JsonValue node && node.TryGetValue<string>(out var text) ? text : null;

    private static JsonArray ClangdArguments(JsonArray original, string directory, string compiler)
    {
        var values = original.Select(x => x is JsonValue value && value.TryGetValue<string>(out var argument) ? argument : throw new InvalidDataException("现有 clangd.arguments 必须是字符串数组。")).ToArray();
        var result = new List<string>(); var drivers = new List<string>(); int queryIndex = -1; bool database = false;
        for (var index = 0; index < values.Length; index++)
        {
            var value = values[index];
            var db = value == "--compile-commands-dir" || value.StartsWith("--compile-commands-dir=", StringComparison.Ordinal);
            var query = value == "--query-driver" || value.StartsWith("--query-driver=", StringComparison.Ordinal);
            if (!db && !query) { result.Add(value); continue; }
            var argument = value.Contains('=') ? value[(value.IndexOf('=') + 1)..] : index + 1 < values.Length && !values[index + 1].StartsWith("--", StringComparison.Ordinal)
                ? values[++index] : throw new InvalidDataException("现有 clangd 参数缺少值：" + value);
            if (db) { if (!database) { result.Add("--compile-commands-dir=" + directory); database = true; } }
            else
            {
                drivers.AddRange(argument.Split(',', StringSplitOptions.RemoveEmptyEntries));
                if (queryIndex < 0) { queryIndex = result.Count; result.Add(""); }
            }
        }
        if (!database) result.Add("--compile-commands-dir=" + directory);
        drivers.Add(compiler);
        var cxx = compiler.Replace("arm-none-eabi-gcc", "arm-none-eabi-g++", StringComparison.OrdinalIgnoreCase);
        if (cxx != compiler && File.Exists(cxx)) drivers.Add(cxx);
        var queryValue = "--query-driver=" + string.Join(',', drivers.Distinct(StringComparer.OrdinalIgnoreCase));
        if (queryIndex < 0) result.Add(queryValue); else result[queryIndex] = queryValue;
        return new JsonArray(result.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray());
    }

    private static byte[] UpdateClangd(byte[]? before, string directory)
    {
        var (text, encoding, preamble) = before == null ? ("", new UTF8Encoding(false, true), Array.Empty<byte>()) : JsoncTaskEditor.Decode(before);
        var newline = JsoncDevelopmentEditor.Newline(text);
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();
        var boundaries = lines.Select((value, index) => (value, index)).Where(x => Regex.IsMatch(x.value, @"\A---\s*(?:#.*)?\z")).Select(x => x.index).Prepend(-1).Append(lines.Count).ToArray();
        var candidates = new List<(int Start, int End, int Mapping)>();
        for (var part = 0; part + 1 < boundaries.Length; part++)
        {
            var start = boundaries[part] + 1; var end = boundaries[part + 1];
            if (lines.Skip(start).Take(end - start).Any(x => Regex.IsMatch(x, @"\AIf\s*:"))) continue;
            var mappings = Enumerable.Range(start, end - start).Where(i => Regex.IsMatch(lines[i], @"\ACompileFlags\s*:")).ToArray();
            if (mappings.Length > 1) throw new InvalidDataException(".clangd 包含重复的 CompileFlags，无法安全合并。");
            candidates.Add((start, end, mappings.FirstOrDefault(-1)));
        }
        if (candidates.Count(x => x.Mapping >= 0) > 1) throw new InvalidDataException(".clangd 包含多个全局 CompileFlags，请先明确要使用的配置。");
        var selected = candidates.FirstOrDefault(x => x.Mapping >= 0, candidates.FirstOrDefault((Start: -1, End: -1, Mapping: -1)));
        var quoted = JsonSerializer.Serialize(directory, JsoncDevelopmentEditor.PrintOptions);
        if (selected.Start < 0)
        {
            if (text.Length > 0 && !text.EndsWith('\n')) lines.Add("");
            lines.AddRange(["---", "CompileFlags:", "  CompilationDatabase: " + quoted]);
        }
        else if (selected.Mapping < 0)
        {
            lines.InsertRange(selected.End, ["CompileFlags:", "  CompilationDatabase: " + quoted]);
        }
        else
        {
            if (WithoutYamlComment(lines[selected.Mapping]).Trim() != "CompileFlags:") throw new InvalidDataException(".clangd 的 CompileFlags 使用紧凑或特殊 YAML 语法，请先改为普通映射。");
            var end = selected.Mapping + 1;
            while (end < selected.End && (string.IsNullOrWhiteSpace(lines[end]) || lines[end].TrimStart().StartsWith('#') || char.IsWhiteSpace(lines[end][0]))) end++;
            var databases = Enumerable.Range(selected.Mapping + 1, end - selected.Mapping - 1).Where(i => Regex.IsMatch(lines[i], @"\A +CompilationDatabase\s*:")).ToArray();
            if (databases.Length > 1) throw new InvalidDataException(".clangd 包含重复的 CompilationDatabase，无法安全合并。");
            if (databases.Length == 0) lines.Insert(selected.Mapping + 1, "  CompilationDatabase: " + quoted);
            else
            {
                var index = databases[0]; var raw = lines[index]; var colon = raw.IndexOf(':');
                var content = WithoutYamlComment(raw); var previous = content[(content.IndexOf(':') + 1)..].Trim();
                if (previous.Length == 0 || "|>{[&*!".Contains(previous[0])) throw new InvalidDataException(".clangd 的 CompilationDatabase 使用特殊 YAML 语法，无法安全合并。");
                var comment = raw[content.Length..];
                lines[index] = raw[..(colon + 1)] + " " + quoted + (comment.Length > 0 ? " " + comment.TrimStart() : "");
            }
        }
        var updated = string.Join(newline, lines);
        if (!updated.EndsWith(newline, StringComparison.Ordinal)) updated += newline;
        return preamble.Concat(encoding.GetBytes(updated)).ToArray();
    }
    private static string WithoutYamlComment(string line)
    {
        char quote = '\0';
        for (var index = 0; index < line.Length; index++)
        {
            var c = line[index];
            if (quote == '"' && c == '\\') { index++; continue; }
            if (c is '\'' or '"') { if (quote == '\0') quote = c; else if (quote == c) quote = '\0'; }
            if (quote == '\0' && c == '#' && (index == 0 || char.IsWhiteSpace(line[index - 1]))) return line[..index];
        }
        return line;
    }
    private static void Add(List<FileChange> changes, string path, Func<byte[]?, byte[]> create)
    {
        ToolVersionManager.EnsureOrdinaryPath(path);
        if (File.Exists(path) && new FileInfo(path).Length > 4 * 1024 * 1024) throw new InvalidDataException("编辑器配置文件超过 4 MB。");
        var before = File.Exists(path) ? File.ReadAllBytes(path) : null;
        var after = create(before);
        if (before == null || !before.AsSpan().SequenceEqual(after)) changes.Add(new(path, before, after));
    }
}
