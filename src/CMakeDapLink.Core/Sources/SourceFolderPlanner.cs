using System.Text;
using System.Text.RegularExpressions;

namespace CMakeDapLink.Core;

public sealed record SourceFolderPlan(string Root, string Target, IReadOnlyList<string> Folders,
    IReadOnlyList<string> SourceFiles, IReadOnlyList<string> HeaderFiles, IReadOnlyList<string> IncludeDirectories,
    string OriginalText, string UpdatedText)
{
    public IReadOnlyList<string> CandidateFiles { get; init; } = [];
}

public static class SourceFolderPlanner
{
    private const string Begin = "# BEGIN CMakeDapLink managed folders";
    private const string End = "# END CMakeDapLink managed folders";
    private static readonly Regex ManagedBlock = new(@"(?ms)^# BEGIN CMakeDapLink managed folders\r?\n.*?^# END CMakeDapLink managed folders", RegexOptions.Compiled);
    private static readonly Regex TargetCall = new(@"(?im)^\s*add_(?:executable|library)\s*\(\s*(\$\{[A-Za-z_][A-Za-z0-9_]*\}|[A-Za-z_][A-Za-z0-9_.+\-]*)", RegexOptions.Compiled);

    public static IReadOnlyList<string> FindTargets(string root)
    {
        var path = Path.Combine(Path.GetFullPath(root), "CMakeLists.txt");
        if (!File.Exists(path)) return [];
        return TargetCall.Matches(File.ReadAllText(path)).Select(x => x.Groups[1].Value)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static SourceFolderPlan Preview(string root, string selectedFolder, string? target = null,
        IReadOnlyCollection<string>? selectedFiles = null)
    {
        root = Path.GetFullPath(root);
        var path = Path.Combine(root, "CMakeLists.txt");
        if (!File.Exists(path)) throw new ArgumentException("工程根目录没有 CMakeLists.txt。", nameof(root));
        selectedFolder = Path.GetFullPath(selectedFolder);
        if (!Directory.Exists(selectedFolder) || !Inside(root, selectedFolder))
            throw new ArgumentException("所选文件夹必须位于当前工程目录内。", nameof(selectedFolder));
        if ((File.GetAttributes(selectedFolder) & FileAttributes.ReparsePoint) != 0)
            throw new ArgumentException("不能选择符号链接或联接目录。", nameof(selectedFolder));
        var original = File.ReadAllText(path);
        var targets = FindTargets(root);
        if (target == null && targets.Count == 1) target = targets[0];
        if (target == null || !targets.Contains(target, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("无法唯一确定 CMake 目标，请先选择要添加文件的目标。");
        if (original.Contains(Begin, StringComparison.Ordinal) != original.Contains(End, StringComparison.Ordinal))
            throw new InvalidDataException("CMakeLists.txt 中托管块标记不完整，请先修复该块。");

        var selectedRelative = Relative(root, selectedFolder);
        var previousFolders = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var existing = ManagedBlock.Match(original);
        if (existing.Success)
            foreach (Match match in Regex.Matches(existing.Value, @"(?m)^# folder: (.+)$"))
                previousFolders.Add(match.Groups[1].Value.Trim());
        var folders = new SortedSet<string>(previousFolders, StringComparer.OrdinalIgnoreCase) { selectedRelative };
        var cFiles = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var hFiles = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var activeFolders = new List<string>();
        foreach (var folder in folders)
        {
            var absolute = Path.GetFullPath(Path.Combine(root, folder.Replace('/', Path.DirectorySeparatorChar)));
            if (!Inside(root, absolute) || !Directory.Exists(absolute) || HasLink(root, absolute)) continue;
            activeFolders.Add(folder);
            foreach (var file in EnumerateSafe(root, absolute))
            {
                var relative = Relative(root, file);
                if (Path.GetExtension(file).Equals(".c", StringComparison.OrdinalIgnoreCase)) cFiles.Add(relative);
                else if (Path.GetExtension(file).Equals(".h", StringComparison.OrdinalIgnoreCase)) hFiles.Add(relative);
            }
        }
        if (cFiles.Count == 0 && hFiles.Count == 0)
            throw new InvalidOperationException("所选文件夹及已有托管文件夹中没有 .c 或 .h 文件。");
        var candidates = cFiles.Concat(hFiles).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        HashSet<string> chosen;
        if (selectedFiles != null)
        {
            chosen = new(selectedFiles.Select(x => x.Replace('\\', '/')), StringComparer.OrdinalIgnoreCase);
            if (chosen.Any(x => !candidates.Contains(x, StringComparer.OrdinalIgnoreCase)))
                throw new ArgumentException("勾选清单包含未扫描到的文件，请重新选择文件夹。", nameof(selectedFiles));
        }
        else if (existing.Success && existing.Value.Contains("# selection: explicit", StringComparison.Ordinal))
        {
            chosen = Regex.Matches(existing.Value, @"(?m)^# selected-file: (.+)$")
                .Select(x => x.Groups[1].Value.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            // New folders default to selected; previously excluded files stay excluded.
            foreach (var file in candidates)
                if (!previousFolders.Any(folder => folder == "." || file.StartsWith(folder.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase)))
                    chosen.Add(file);
        }
        else chosen = candidates.ToHashSet(StringComparer.OrdinalIgnoreCase);
        cFiles.RemoveWhere(x => !chosen.Contains(x));
        hFiles.RemoveWhere(x => !chosen.Contains(x));
        if (cFiles.Count + hFiles.Count == 0)
        {
            if (selectedFiles != null) throw new InvalidOperationException("请至少勾选一个 .c 或 .h 文件。");
            // Keep the checklist usable when all previously selected files were deleted.
            return new(root, target, activeFolders, [], [], [], original, original) { CandidateFiles = candidates };
        }
        var includeDirs = hFiles.Select(x => Path.GetDirectoryName(x.Replace('/', Path.DirectorySeparatorChar))?.Replace('\\', '/') ?? ".")
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        var newline = original.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var block = BuildBlock(target, activeFolders, cFiles, hFiles, includeDirs, newline);
        var updated = existing.Success ? original.Replace(existing.Value, block) : original.TrimEnd('\r', '\n') + newline + newline + block + newline;
        return new(root, target, activeFolders, cFiles.ToArray(), hFiles.ToArray(), includeDirs, original, updated) { CandidateFiles = candidates };
    }

    public static bool Apply(SourceFolderPlan plan)
    {
        var path = Path.Combine(plan.Root, "CMakeLists.txt");
        if (File.ReadAllText(path) != plan.OriginalText)
            throw new IOException("CMakeLists.txt 在预览后发生变化，请重新预览再写入。");
        if (plan.OriginalText == plan.UpdatedText) return false;
        File.Copy(path, path + ".bak-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"), false);
        var bytes = File.ReadAllBytes(path);
        var bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(temp, plan.UpdatedText, new UTF8Encoding(bom));
        File.Move(temp, path, true);
        return true;
    }

    private static string BuildBlock(string target, IReadOnlyList<string> folders, IEnumerable<string> sources,
        IEnumerable<string> headers, IEnumerable<string> includes, string newline)
    {
        var lines = new List<string> { Begin };
        lines.AddRange(folders.Select(x => "# folder: " + x));
        lines.Add("# selection: explicit");
        lines.AddRange(sources.Concat(headers).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Select(x => "# selected-file: " + x));
        lines.Add($"target_sources({target} PRIVATE");
        lines.AddRange(sources.Concat(headers).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Select(x => "    \"${CMAKE_CURRENT_SOURCE_DIR}/" + Escape(x) + "\""));
        lines.Add(")");
        var includeArray = includes.ToArray();
        if (includeArray.Length > 0)
        {
            lines.Add($"target_include_directories({target} PRIVATE");
            lines.AddRange(includeArray.Select(x => "    \"${CMAKE_CURRENT_SOURCE_DIR}/" + Escape(x) + "\""));
            lines.Add(")");
        }
        lines.Add(End);
        return string.Join(newline, lines);
    }

    private static IEnumerable<string> EnumerateSafe(string root, string start)
    {
        var stack = new Stack<string>(); stack.Push(start);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            foreach (var file in Directory.EnumerateFiles(current))
                if (Inside(root, file) && (File.GetAttributes(file) & FileAttributes.ReparsePoint) == 0 &&
                    (Path.GetExtension(file).Equals(".c", StringComparison.OrdinalIgnoreCase) ||
                     Path.GetExtension(file).Equals(".h", StringComparison.OrdinalIgnoreCase))) yield return file;
            foreach (var directory in Directory.EnumerateDirectories(current))
            {
                var name = Path.GetFileName(directory);
                if (name is ".git" or ".vscode" or "build" || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) continue;
                if (Inside(root, directory)) stack.Push(directory);
            }
        }
    }

    private static bool Inside(string root, string path) => path.Equals(root, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static bool HasLink(string root, string path)
    {
        var current = new DirectoryInfo(path);
        while (current != null && Inside(root, current.FullName))
        {
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0) return true;
            if (current.FullName.Equals(root, StringComparison.OrdinalIgnoreCase)) break;
            current = current.Parent;
        }
        return false;
    }
    private static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');
    private static string Escape(string value) => value.Replace("\\", "/").Replace("\"", "\\\"").Replace(";", "\\;").Replace("$", "\\$");
}
