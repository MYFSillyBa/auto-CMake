using System.Text;
using System.Text.RegularExpressions;

namespace CMakeDapLink.Core;

public sealed record SourceFolderPlan(string Root, string Target, IReadOnlyList<string> Folders,
    IReadOnlyList<string> SourceFiles, IReadOnlyList<string> HeaderFiles, IReadOnlyList<string> IncludeDirectories,
    string OriginalText, string UpdatedText)
{
    public IReadOnlyList<string> CandidateFiles { get; init; } = [];
    public IReadOnlyList<string> AlreadyRegisteredFiles { get; init; } = [];
    public IReadOnlyList<string> Notes { get; init; } = [];
    public byte[]? OriginalBytes { get; init; }
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
        var originalBytes = File.ReadAllBytes(path);
        using var originalReader = new StreamReader(new MemoryStream(originalBytes), Encoding.UTF8, true);
        var original = originalReader.ReadToEnd();
        var targets = FindTargets(root);
        if (target == null && targets.Count == 1) target = targets[0];
        if (target == null || !targets.Contains(target, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("无法唯一确定 CMake 目标，请先选择要添加文件的目标。");
        if (original.Contains(Begin, StringComparison.Ordinal) != original.Contains(End, StringComparison.Ordinal))
            throw new InvalidDataException("CMakeLists.txt 中托管块标记不完整，请先修复该块。");

        var selectedRelative = Relative(root, selectedFolder);
        var previousFolders = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var existing = ManagedBlock.Matches(original).FirstOrDefault(x =>
            Regex.Match(x.Value, @"(?m)^# target: (.+)$").Groups[1].Value.Trim() == target ||
            !x.Value.Contains("# target:", StringComparison.Ordinal) &&
            Regex.IsMatch(x.Value, @"target_sources\s*\(\s*" + Regex.Escape(target) + @"\s")) ?? Match.Empty;
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
                if (SourceFileTypes.IsHeader(file)) hFiles.Add(relative);
                else cFiles.Add(relative);
            }
        }
        if (cFiles.Count == 0 && hFiles.Count == 0)
            throw new InvalidOperationException("所选文件夹中没有支持的 C/C++、汇编源文件或头文件。");
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
            if (selectedFiles != null) throw new InvalidOperationException("请至少勾选一个源文件或头文件。");
            // Keep the checklist usable when all previously selected files were deleted.
            return new(root, target, activeFolders, [], [], [], original, original) { CandidateFiles = candidates, OriginalBytes = originalBytes };
        }
        var includeDirs = hFiles.Select(x => Path.GetDirectoryName(x.Replace('/', Path.DirectorySeparatorChar))?.Replace('\\', '/') ?? ".")
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        var newline = original.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var registration = CMakeSourceIndex.Inspect(root, target, original);
        var already = candidates.Where(registration.ExistingFiles.Contains).ToArray();
        var newSources = cFiles.Where(x => !registration.ExistingFiles.Contains(x)).ToArray();
        var newHeaders = hFiles.Where(x => !registration.ExistingFiles.Contains(x)).ToArray();
        var block = BuildBlock(target, activeFolders, cFiles, hFiles, newSources, newHeaders, includeDirs, newline);
        var updated = existing.Success ? original[..existing.Index] + block + original[(existing.Index + existing.Length)..] : original.TrimEnd('\r', '\n') + newline + newline + block + newline;
        return new(root, target, activeFolders, cFiles.ToArray(), hFiles.ToArray(), includeDirs, original, updated)
        {
            CandidateFiles = candidates, AlreadyRegisteredFiles = already, OriginalBytes = originalBytes,
            Notes = registration.HasUnresolvedExpressions ? ["存在变量或生成表达式，部分现有文件是否已加入构建需要人工核对。"] : []
        };
    }

    public static bool Apply(SourceFolderPlan plan)
    {
        return ChangeHistory.Apply(plan.Root, "更新 CMake 源文件：" + plan.Target, Changes(plan)) != null;
    }

    public static IReadOnlyList<FileChange> Changes(SourceFolderPlan plan)
    {
        var path = Path.Combine(plan.Root, "CMakeLists.txt");
        if (plan.OriginalText == plan.UpdatedText) return [];
        var before = File.ReadAllBytes(path);
        using var reader = new StreamReader(new MemoryStream(before), Encoding.UTF8, true);
        var currentText = reader.ReadToEnd();
        if (plan.OriginalBytes != null ? !before.AsSpan().SequenceEqual(plan.OriginalBytes) : currentText != plan.OriginalText)
            throw new IOException("CMakeLists.txt 在预览后发生变化，请重新预览再写入。");
        var encoding = reader.CurrentEncoding;
        var preamble = encoding.GetPreamble();
        var prefix = preamble.Length > 0 && before.AsSpan().StartsWith(preamble) ? preamble : [];
        var after = prefix.Concat(encoding.GetBytes(plan.UpdatedText)).ToArray();
        return [new(path, before, after)];
    }

    private static string BuildBlock(string target, IReadOnlyList<string> folders, IEnumerable<string> sources,
        IEnumerable<string> headers, IReadOnlyList<string> newSources, IReadOnlyList<string> newHeaders,
        IEnumerable<string> includes, string newline)
    {
        var lines = new List<string> { Begin, "# target: " + target };
        lines.AddRange(folders.Select(x => "# folder: " + x));
        lines.Add("# selection: explicit");
        lines.AddRange(sources.Concat(headers).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Select(x => "# selected-file: " + x));
        foreach (var language in new[] { (Name: "CXX", Needed: newSources.Any(SourceFileTypes.IsCxx)), (Name: "ASM", Needed: newSources.Any(SourceFileTypes.IsAssembly)) })
            if (language.Needed)
            {
                lines.Add("get_property(_cmake_daplink_languages GLOBAL PROPERTY ENABLED_LANGUAGES)");
                lines.Add($"if(NOT \"{language.Name}\" IN_LIST _cmake_daplink_languages)");
                lines.Add($"    enable_language({language.Name})");
                lines.Add("endif()");
            }
        if (newSources.Count + newHeaders.Count > 0)
        {
            lines.Add($"target_sources({target} PRIVATE");
            lines.AddRange(newSources.Concat(newHeaders).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Select(x => "    \"${CMAKE_CURRENT_SOURCE_DIR}/" + Escape(x) + "\""));
            lines.Add(")");
        }
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
                    SourceFileTypes.IsSupported(file)) yield return file;
            foreach (var directory in Directory.EnumerateDirectories(current))
            {
                var name = Path.GetFileName(directory);
                if (name is ".git" or ".vscode" or "build" or "bin" or "obj" || name.StartsWith("build-", StringComparison.OrdinalIgnoreCase) ||
                    (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) continue;
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
