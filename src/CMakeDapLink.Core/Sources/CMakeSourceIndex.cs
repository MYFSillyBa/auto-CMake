using System.Text.RegularExpressions;

namespace CMakeDapLink.Core;

public sealed record SourceRegistration(IReadOnlySet<string> ExistingFiles, bool HasUnresolvedExpressions);

public static class SourceFileTypes
{
    private static readonly HashSet<string> Headers = new([".h", ".hpp", ".hh", ".hxx"], StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> Sources = new([".c", ".cpp", ".cc", ".cxx", ".s"], StringComparer.OrdinalIgnoreCase);
    public static bool IsHeader(string path) => Headers.Contains(Path.GetExtension(path));
    public static bool IsSupported(string path) => IsHeader(path) || Sources.Contains(Path.GetExtension(path));
    public static bool IsCxx(string path) => new[] { ".cpp", ".cc", ".cxx" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
    public static bool IsAssembly(string path) => Path.GetExtension(path).Equals(".s", StringComparison.OrdinalIgnoreCase);
}

public static class CMakeSourceIndex
{
    private static readonly Regex Calls = new(@"(?is)\b(?<name>set|project|add_executable|add_library|target_sources|add_subdirectory)\s*\((?<args>[^)]*)\)");
    private static readonly Regex Tokens = new("\"(?<quoted>(?:\\\\.|[^\"])*)\"|(?<plain>[^\\s;]+)");
    private static readonly Regex Variable = new(@"\$\{(?<name>[A-Za-z_][A-Za-z0-9_]*)\}");
    private static readonly HashSet<string> Keywords = new(["PRIVATE", "PUBLIC", "INTERFACE", "STATIC", "SHARED", "MODULE", "OBJECT", "IMPORTED", "ALIAS", "WIN32", "MACOSX_BUNDLE", "EXCLUDE_FROM_ALL"], StringComparer.OrdinalIgnoreCase);

    public static SourceRegistration Inspect(string root, string target, string? rootContent = null)
    {
        root = Path.GetFullPath(root);
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unresolved = false;
        var pending = new Queue<string>(); pending.Enqueue(Path.Combine(root, "CMakeLists.txt"));
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (pending.TryDequeue(out var cmakePath))
        {
            if (!visited.Add(cmakePath) || !File.Exists(cmakePath)) continue;
            var directory = Path.GetDirectoryName(cmakePath)!;
            var content = cmakePath.Equals(Path.Combine(root, "CMakeLists.txt"), StringComparison.OrdinalIgnoreCase) && rootContent != null
                ? rootContent : File.ReadAllText(cmakePath);
            content = Regex.Replace(content, @"(?ms)^# BEGIN CMakeDapLink managed folders\r?\n.*?^# END CMakeDapLink managed folders", "");
            content = Regex.Replace(content, @"(?s)#\[(?<eq>=*)\[.*?\]\k<eq>\]", "");
            content = StripLineComments(content);
            // Do not infer execution of branches, functions, loops or included scripts.
            // Uncertain files remain selectable and are never removed as known duplicates.
            if (Regex.IsMatch(content, @"(?i)\b(if|function|macro|foreach|while|include)\s*\("))
            { unresolved = true; continue; }
            var variables = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["CMAKE_CURRENT_SOURCE_DIR"] = directory.Replace('\\', '/'), ["CMAKE_CURRENT_LIST_DIR"] = directory.Replace('\\', '/'),
                ["CMAKE_SOURCE_DIR"] = root.Replace('\\', '/'), ["PROJECT_SOURCE_DIR"] = root.Replace('\\', '/')
            };
            foreach (Match call in Calls.Matches(content))
            {
                var name = call.Groups["name"].Value.ToLowerInvariant();
                var args = Tokens.Matches(call.Groups["args"].Value).Select(x => x.Groups["quoted"].Success ? x.Groups["quoted"].Value : x.Groups["plain"].Value).ToArray();
                if (args.Length == 0) continue;
                if (name == "project") { variables["PROJECT_NAME"] = args[0]; variables["CMAKE_PROJECT_NAME"] = args[0]; continue; }
                if (name == "set") { variables[args[0]] = string.Join(";", args.Skip(1).TakeWhile(x => !x.Equals("CACHE", StringComparison.OrdinalIgnoreCase))); continue; }
                if (name == "add_subdirectory")
                {
                    var child = Expand(args[0], variables);
                    if (child.Contains('$')) { unresolved = true; continue; }
                    var childDirectory = Path.GetFullPath(Path.Combine(directory, child));
                    if (childDirectory.StartsWith(root.TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                        && Directory.Exists(childDirectory) && (File.GetAttributes(childDirectory) & FileAttributes.ReparsePoint) == 0)
                        pending.Enqueue(Path.Combine(childDirectory, "CMakeLists.txt"));
                    else unresolved = true;
                    continue;
                }
                var expandedTarget = Expand(target, variables);
                if (!Expand(args[0], variables).Equals(expandedTarget, StringComparison.Ordinal)) continue;
                foreach (var argument in args.Skip(1))
                {
                    foreach (var value in Expand(argument, variables).Split(';', StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (Keywords.Contains(value)) continue;
                        if (value.Contains("${") || value.Contains("$<") || value.Contains("$(")) { unresolved = true; continue; }
                        if (!SourceFileTypes.IsSupported(value)) continue;
                        try
                        {
                            var full = Path.GetFullPath(Path.IsPathRooted(value) ? value : Path.Combine(directory, value));
                            if (full.StartsWith(root.TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                                files.Add(Path.GetRelativePath(root, full).Replace('\\', '/'));
                        }
                        catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { unresolved = true; }
                    }
                }
            }
        }
        return new(files, unresolved);
    }

    private static string Expand(string value, Dictionary<string, string> variables)
    {
        for (var i = 0; i < 6 && value.Contains("${"); i++)
        {
            var updated = Variable.Replace(value, x => variables.GetValueOrDefault(x.Groups["name"].Value, x.Value));
            if (updated == value) break;
            value = updated;
        }
        return value;
    }

    private static string StripLineComments(string text)
    {
        var result = new System.Text.StringBuilder(); var quoted = false; var escaped = false; var comment = false;
        foreach (var character in text)
        {
            if (comment) { if (character == '\n') { comment = false; result.Append(character); } continue; }
            if (character == '#' && !quoted) { comment = true; continue; }
            result.Append(character);
            if (character == '"' && !escaped) quoted = !quoted;
            escaped = character == '\\' && !escaped;
        }
        return result.ToString();
    }

}
