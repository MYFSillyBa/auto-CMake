using System.Text.Json;
using System.Text.RegularExpressions;

namespace CMakeDapLink.Core;

public sealed record BuildPresetInfo(string Name, string DisplayName, string? Configuration);
public sealed record ConfigurePresetInfo(string Name, string DisplayName, string? Description,
    string? BinaryDirectory, string? ToolchainFile, string? BuildPreset, string? BuildConfiguration, string SourceFile)
{
    public string? ConfigureConfiguration { get; init; }
    public IReadOnlyList<BuildPresetInfo> BuildPresets { get; init; } = [];
}

internal sealed class CMakePresetReader(string root, List<string> notes)
{
    private sealed record Field(JsonElement Value, string File);
    private sealed record Entry(string Name, string File, Dictionary<string, Field> Fields);
    private sealed record Resolved(Entry Entry, Dictionary<string, Field> Fields,
        Dictionary<string, Field> Cache, Dictionary<string, Field> Environment);
    private readonly Dictionary<string, Entry> configure = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Entry> build = new(StringComparer.Ordinal);
    private readonly HashSet<string> loaded = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> includes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> versions = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<(string Value, string Source)> ChipDefinitions(string presetName)
    {
        if (!configure.TryGetValue(presetName, out var entry)) return [];
        var preset = Resolve(entry, configure, new(StringComparer.Ordinal));
        if (preset == null) return [];
        var values = new List<(string Value, string Source)>();
        foreach (var pair in preset.Cache)
        {
            var value = Expand(CacheText(pair.Value.Value), preset, pair.Value.File, new(StringComparer.Ordinal));
            if (value != null) values.Add((value, Path.GetRelativePath(root, pair.Value.File) + " / " + presetName + " 编译缓存 " + pair.Key));
        }
        return values;
    }

    public IReadOnlyList<ConfigurePresetInfo> Read()
    {
        var projectFile = Path.Combine(root, "CMakePresets.json");
        var userFile = Path.Combine(root, "CMakeUserPresets.json");
        Load(projectFile, new(StringComparer.OrdinalIgnoreCase));
        Load(userFile, new(StringComparer.OrdinalIgnoreCase));
        if (File.Exists(userFile) && File.Exists(projectFile)) includes[userFile].Add(projectFile);
        var result = new List<ConfigurePresetInfo>();
        foreach (var entry in configure.Values.Where(e => !IsHidden(e)))
        {
            var resolved = Resolve(entry, configure, new(StringComparer.Ordinal));
            if (resolved == null || !MatchesCondition(resolved)) continue;
            var binaryDir = PathValue(resolved, "binaryDir");
            if (!resolved.Fields.ContainsKey("binaryDir") && versions.GetValueOrDefault(entry.File) >= 3)
                binaryDir = root;
            var chainField = resolved.Fields.GetValueOrDefault("toolchainFile") ?? resolved.Cache.GetValueOrDefault("CMAKE_TOOLCHAIN_FILE");
            var chain = chainField == null ? null : Expand(CacheText(chainField.Value), resolved, chainField.File, new(StringComparer.Ordinal));
            if (!string.IsNullOrWhiteSpace(chain))
            {
                if (Path.IsPathRooted(chain)) chain = Path.GetFullPath(chain);
                else
                {
                    var inBuild = binaryDir == null ? null : Path.GetFullPath(Path.Combine(binaryDir, chain));
                    chain = inBuild != null && File.Exists(inBuild) ? inBuild : Path.GetFullPath(Path.Combine(root, chain));
                }
            }
            var configField = resolved.Cache.GetValueOrDefault("CMAKE_BUILD_TYPE");
            var configuration = configField == null ? null : Expand(CacheText(configField.Value), resolved, configField.File, new(StringComparer.Ordinal));
            var configureConfiguration = configuration;
            var buildChoices = new List<BuildPresetInfo>();
            string? matchingBuild = null;
            foreach (var buildEntry in build.Values.Where(e => !IsHidden(e)))
            {
                var resolvedBuild = Resolve(buildEntry, build, new(StringComparer.Ordinal));
                if (resolvedBuild == null || Text(resolvedBuild.Fields, "configurePreset") != entry.Name ||
                    !CanSee(buildEntry.File, entry.File, new(StringComparer.OrdinalIgnoreCase))) continue;
                if (resolved.Fields.TryGetValue("generator", out var generator)) resolvedBuild.Fields["generator"] = generator;
                // Configure environment overrides inherited build values, then explicit build values override both.
                if (!resolvedBuild.Fields.TryGetValue("inheritConfigureEnvironment", out var inheritEnvironment) || inheritEnvironment.Value.ValueKind != JsonValueKind.False)
                {
                    foreach (var pair in resolved.Environment) resolvedBuild.Environment[pair.Key] = pair.Value;
                    MergeMap(buildEntry, "environment", resolvedBuild.Environment);
                }
                if (!MatchesCondition(resolvedBuild)) continue;
                var buildConfig = resolvedBuild.Fields.GetValueOrDefault("configuration");
                var buildConfiguration = buildConfig == null ? null : Expand(CacheText(buildConfig.Value), resolvedBuild, buildConfig.File, new(StringComparer.Ordinal));
                buildChoices.Add(new(buildEntry.Name, Text(buildEntry.Fields, "displayName") ?? buildEntry.Name, buildConfiguration));
                if (matchingBuild == null) { matchingBuild = buildEntry.Name; configuration = buildConfiguration ?? configureConfiguration; }
            }
            if (binaryDir == null) notes.Add($"预设 {entry.Name} 没有可解析的 binaryDir，请核对 {Path.GetFileName(entry.File)}。");
            result.Add(new(entry.Name, Text(entry.Fields, "displayName") ?? entry.Name,
                Text(entry.Fields, "description"), binaryDir, chain, matchingBuild, configuration, entry.File)
                { ConfigureConfiguration = configureConfiguration, BuildPresets = buildChoices });
        }
        return result;
    }

    private void Load(string path, HashSet<string> active)
    {
        path = Path.GetFullPath(path);
        if (active.Contains(path)) { notes.Add("CMakePresets.json include 存在循环：" + path); return; }
        if (!loaded.Add(path)) return;
        includes[path] = new(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path)) return;
        active.Add(path);
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions
                { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (document.RootElement.TryGetProperty("version", out var version) && version.TryGetInt32(out var number)) versions[path] = number;
            if (document.RootElement.TryGetProperty("include", out var include) && include.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in include.EnumerateArray().Where(i => i.ValueKind == JsonValueKind.String))
                {
                    var value = ExpandInclude(item.GetString()!, path);
                    if (value == null) { notes.Add("CMakePresets.json include 路径无法解析：" + item.GetString()); continue; }
                    var includedPath = Path.GetFullPath(Path.IsPathRooted(value) ? value : Path.Combine(Path.GetDirectoryName(path)!, value));
                    includes[path].Add(includedPath);
                    if (!File.Exists(includedPath)) notes.Add("CMakePresets.json include 文件不存在：" + includedPath);
                    Load(includedPath, active);
                }
            }
            Add(document.RootElement, "configurePresets", configure, path);
            Add(document.RootElement, "buildPresets", build, path);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        { notes.Add(Path.GetFileName(path) + " 无法读取：" + ex.Message); }
        finally { active.Remove(path); }
    }

    private void Add(JsonElement document, string property, Dictionary<string, Entry> entries, string file)
    {
        if (!document.TryGetProperty(property, out var array) || array.ValueKind != JsonValueKind.Array) return;
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String) continue;
            var key = name.GetString()!;
            if (!entries.TryAdd(key, new(key, file, item.EnumerateObject().ToDictionary(p => p.Name, p => new Field(p.Value.Clone(), file), StringComparer.Ordinal))))
                notes.Add($"CMakePresets.json 预设名称重复：{key}（{file}）。");
        }
    }

    private Resolved? Resolve(Entry entry, Dictionary<string, Entry> entries, HashSet<string> active)
    {
        if (!active.Add(entry.Name)) { notes.Add("CMakePresets.json 预设继承存在循环：" + entry.Name); return null; }
        try
        {
            var fields = new Dictionary<string, Field>(StringComparer.Ordinal);
            var cache = new Dictionary<string, Field>(StringComparer.Ordinal);
            var environment = new Dictionary<string, Field>(StringComparer.Ordinal);
            if (entry.Fields.TryGetValue("inherits", out var inheritance))
            {
                var parents = inheritance.Value.ValueKind == JsonValueKind.String ? [inheritance.Value.GetString()!] :
                    inheritance.Value.ValueKind == JsonValueKind.Array ? inheritance.Value.EnumerateArray().Where(i => i.ValueKind == JsonValueKind.String).Select(i => i.GetString()!).ToArray() : [];
                foreach (var name in parents)
                {
                    if (!entries.TryGetValue(name, out var parent) || !CanSee(entry.File, parent.File, new(StringComparer.OrdinalIgnoreCase)))
                    { notes.Add($"CMakePresets.json 预设 {entry.Name} 无法继承 {name}，请检查 include。"); return null; }
                    var resolved = Resolve(parent, entries, active);
                    if (resolved == null) return null;
                    foreach (var pair in resolved.Fields.Where(p => p.Key is not ("name" or "hidden" or "inherits" or "displayName" or "description")
                        && !(p.Key == "condition" && p.Value.Value.ValueKind == JsonValueKind.Null))) fields.TryAdd(pair.Key, pair.Value);
                    foreach (var pair in resolved.Cache) cache.TryAdd(pair.Key, pair.Value);
                    foreach (var pair in resolved.Environment) environment.TryAdd(pair.Key, pair.Value);
                }
            }
            foreach (var pair in entry.Fields) fields[pair.Key] = pair.Value;
            MergeMap(entry, "cacheVariables", cache);
            MergeMap(entry, "environment", environment);
            return new(entry, fields, cache, environment);
        }
        finally { active.Remove(entry.Name); }
    }

    private bool CanSee(string child, string parent, HashSet<string> visited) => child == parent ||
        visited.Add(child) && includes.TryGetValue(child, out var files) && files.Any(file => CanSee(file, parent, visited));

    private static void MergeMap(Entry entry, string name, Dictionary<string, Field> target)
    {
        if (!entry.Fields.TryGetValue(name, out var field) || field.Value.ValueKind != JsonValueKind.Object) return;
        foreach (var property in field.Value.EnumerateObject()) target[property.Name] = new(property.Value, field.File);
    }

    private string? PathValue(Resolved preset, string key)
    {
        if (!preset.Fields.TryGetValue(key, out var field)) return null;
        var value = Expand(CacheText(field.Value), preset, field.File, new(StringComparer.Ordinal));
        return string.IsNullOrWhiteSpace(value) ? null : Path.GetFullPath(Path.IsPathRooted(value) ? value : Path.Combine(root, value));
    }

    private string? Expand(string? value, Resolved preset, string file, HashSet<string> expanding)
    {
        if (value == null) return null;
        value = ReplaceMacros(value, preset.Entry.Name, file, Text(preset.Fields, "generator") ?? "");
        var unresolved = false;
        value = Regex.Replace(value, @"\$(p?env)\{([^}]+)\}", match =>
        {
            var name = match.Groups[2].Value;
            if (match.Groups[1].Value == "penv" || !preset.Environment.TryGetValue(name, out var field)) return Environment.GetEnvironmentVariable(name) ?? "";
            if (field.Value.ValueKind == JsonValueKind.Null) return "";
            if (!expanding.Add(name)) { unresolved = true; return match.Value; }
            var expanded = Expand(CacheText(field.Value), preset, field.File, expanding);
            expanding.Remove(name);
            if (expanded == null) unresolved = true;
            return expanded ?? match.Value;
        });
        return unresolved || value.Contains("${", StringComparison.Ordinal) || Regex.IsMatch(value, @"\$\w+\{") ? null : value;
    }

    private string? ExpandInclude(string value, string file)
    {
        value = ReplaceMacros(value, "", file, "");
        value = Regex.Replace(value, @"\$penv\{([^}]+)\}", m => Environment.GetEnvironmentVariable(m.Groups[1].Value) ?? "");
        return value.Contains("${", StringComparison.Ordinal) || Regex.IsMatch(value, @"\$\w+\{") ? null : value;
    }

    private string ReplaceMacros(string value, string name, string file, string generator) => value
        .Replace("${sourceDir}", root).Replace("${sourceParentDir}", Directory.GetParent(root)?.FullName ?? root)
        .Replace("${sourceDirName}", Path.GetFileName(root)).Replace("${presetName}", name)
        .Replace("${fileDir}", Path.GetDirectoryName(file)!).Replace("${generator}", generator)
        .Replace("${hostSystemName}", OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "Darwin" : "Linux")
        .Replace("${pathListSep}", Path.PathSeparator.ToString()).Replace("${dollar}", "$", StringComparison.Ordinal);

    private bool MatchesCondition(Resolved preset)
    {
        if (!preset.Fields.TryGetValue("condition", out var field)) return true;
        bool? Evaluate(JsonElement condition)
        {
            if (condition.ValueKind is JsonValueKind.Null or JsonValueKind.True) return true;
            if (condition.ValueKind == JsonValueKind.False) return false;
            if (condition.ValueKind != JsonValueKind.Object || !condition.TryGetProperty("type", out var type)) return null;
            string? Argument(string key) => condition.TryGetProperty(key, out var item) ? Expand(CacheText(item), preset, field.File, new(StringComparer.Ordinal)) : null;
            switch (type.GetString())
            {
                case "const": return condition.TryGetProperty("value", out var constant) && constant.ValueKind == JsonValueKind.True;
                case "equals": case "notEquals":
                    var lhs = Argument("lhs"); var rhs = Argument("rhs");
                    return lhs == null || rhs == null ? null : type.GetString() == "equals" ? lhs == rhs : lhs != rhs;
                case "inList": case "notInList":
                    var value = Argument("string");
                    if (value == null || !condition.TryGetProperty("list", out var list) || list.ValueKind != JsonValueKind.Array) return null;
                    var items = list.EnumerateArray().Select(i => Expand(CacheText(i), preset, field.File, new(StringComparer.Ordinal))).ToArray();
                    return items.Any(i => i == null) ? null : type.GetString() == "inList" ? items.Contains(value) : !items.Contains(value);
                case "anyOf": case "allOf":
                    if (!condition.TryGetProperty("conditions", out var conditions) || conditions.ValueKind != JsonValueKind.Array) return null;
                    var values = conditions.EnumerateArray().Select(Evaluate).ToArray();
                    return type.GetString() == "anyOf" ? values.Any(v => v == true) ? true : values.Any(v => v == null) ? null : false :
                        values.Any(v => v == false) ? false : values.Any(v => v == null) ? null : true;
                case "not": return condition.TryGetProperty("condition", out var nested) ? !Evaluate(nested) : null;
                case "matches": case "notMatches":
                    var input = Argument("string"); var pattern = Argument("regex");
                    if (input == null || pattern == null) return null;
                    try { return type.GetString() == "matches" ? Regex.IsMatch(input, pattern, RegexOptions.None, TimeSpan.FromSeconds(1)) : !Regex.IsMatch(input, pattern, RegexOptions.None, TimeSpan.FromSeconds(1)); }
                    catch (ArgumentException) { return null; }
                    catch (RegexMatchTimeoutException) { return null; }
                default: return null;
            }
        }
        var result = Evaluate(field.Value);
        if (result == null) notes.Add($"CMakePresets.json 预设 {preset.Entry.Name} 的 condition 无法解析，未列入可选预设。");
        return result == true;
    }

    private static bool IsHidden(Entry entry) => entry.Fields.TryGetValue("hidden", out var field) && field.Value.ValueKind == JsonValueKind.True;
    private static string? Text(Dictionary<string, Field> fields, string name) => fields.TryGetValue(name, out var field) ? CacheText(field.Value) : null;
    private static string? CacheText(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString() :
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty("value", out var nested) ? CacheText(nested) : null;
}
