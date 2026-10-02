using System.Text.RegularExpressions;

namespace CMakeDapLink.Core;

public sealed record OpenOcdScriptResolution(ToolPaths Tools, string? Target, bool Ready, string Details);

public static class OpenOcdScripts
{
    private static readonly IReadOnlyDictionary<string, string> Families = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["C0"] = "stm32c0x", ["F0"] = "stm32f0x", ["F1"] = "stm32f1x", ["F2"] = "stm32f2x",
        ["F3"] = "stm32f3x", ["F4"] = "stm32f4x", ["F7"] = "stm32f7x", ["G0"] = "stm32g0x",
        ["G4"] = "stm32g4x", ["H7"] = "stm32h7x", ["L0"] = "stm32l0", ["L1"] = "stm32l1",
        ["L4"] = "stm32l4x", ["L5"] = "stm32l5x", ["U0"] = "stm32u0x", ["U3"] = "stm32u3x",
        ["U5"] = "stm32u5x", ["WB"] = "stm32wbx", ["WL"] = "stm32wlx"
    };
    private static readonly Regex LiteralSource = new(@"\bsource\s+\[\s*find\s+(?:\{([^{}\r\n]+)\}|""([^""\r\n]+)""|([^\s\]\[${}"";]+))\s*\]", RegexOptions.CultureInvariant);

    public static string? TargetForChip(string chip)
    {
        var part = chip.Trim().ToUpperInvariant();
        if (!Regex.IsMatch(part, @"\ASTM32[A-Z0-9]+\z", RegexOptions.CultureInvariant)) return null;
        if (part.StartsWith("STM32H7R", StringComparison.Ordinal) || part.StartsWith("STM32H7S", StringComparison.Ordinal)) return "target/stm32h7rsx.cfg";
        if (part.StartsWith("STM32WBA", StringComparison.Ordinal))
            return part.Length > 8 && part[8] is '2' or '5' or '6' ? "target/stm32wba" + part[8] + "x.cfg" : null;
        if (part.Length < 7 || !Families.TryGetValue(part[5..7], out var script)) return null;
        return "target/" + script + ".cfg";
    }

    public static IReadOnlyList<string> MissingFiles(string? scripts, string? target)
    {
        var missing = new List<string>();
        var pending = new Queue<string>(); pending.Enqueue("interface/cmsis-dap.cfg");
        if (string.IsNullOrWhiteSpace(target) || !ValidTarget(target)) missing.Add("target/*.cfg（需要匹配芯片的 Target）");
        else pending.Enqueue(target.Replace('\\', '/'));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase); long bytes = 0;
        while (pending.TryDequeue(out var relative))
        {
            if (!seen.Add(relative)) continue;
            if (seen.Count > 256) { missing.Add(relative + "（依赖数量超出检查限制）"); break; }
            try
            {
                if (scripts == null || !Directory.Exists(scripts)) { missing.Add(relative); continue; }
                var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(scripts));
                var file = Path.GetFullPath(Path.Combine(root, relative));
                if (!file.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                { missing.Add(relative + "（依赖超出 scripts 目录）"); continue; }
                ToolVersionManager.EnsureOrdinaryPath(file);
                if (!File.Exists(file)) { missing.Add(relative); continue; }
                var size = new FileInfo(file).Length; bytes += size;
                if (size > 1024 * 1024 || bytes > 4 * 1024 * 1024) { missing.Add(relative + "（脚本大小超出检查限制）"); continue; }
                var content = Regex.Replace(File.ReadAllText(file), @"(?m)^\s*#.*$", "");
                foreach (Match match in LiteralSource.Matches(content))
                {
                    var dependency = match.Groups.Cast<Group>().Skip(1).First(g => g.Success).Value.Replace('\\', '/');
                    pending.Enqueue(dependency);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            { missing.Add(relative + "（无法读取：" + ex.Message + "）"); }
        }
        return missing;
    }

    public static async Task<string> ValidateAsync(ToolPaths tools, string target, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        var missing = MissingFiles(tools.Scripts, target);
        if (missing.Count != 0) throw new InvalidDataException("OpenOCD 脚本或依赖缺失：" + string.Join("、", missing));
        if (!File.Exists(tools.OpenOcd)) throw new FileNotFoundException("OpenOCD 可执行文件缺失。", tools.OpenOcd);
        ToolVersionManager.EnsureOrdinaryPath(tools.OpenOcd!);
        return await ToolRepairInstaller.RunAsync(tools.OpenOcd!,
            ["-s", tools.Scripts!, "-f", "interface/cmsis-dap.cfg", "-c", "transport select swd", "-f", target.Replace('\\', '/'), "-c", "shutdown"],
            Path.GetDirectoryName(tools.OpenOcd!)!, tools, cancellation);
    }

    public static async Task<OpenOcdScriptResolution> ResolveAsync(ToolPaths current, string? chip,
        string? requestedTarget = null, IEnumerable<string>? searchRoots = null, CancellationToken cancellation = default,
        bool allowTargetAlias = false)
    {
        var automatic = requestedTarget == null || allowTargetAlias;
        var target = requestedTarget ?? (chip == null ? null : TargetForChip(chip));
        if (string.IsNullOrWhiteSpace(target)) return new(current, target, false, "芯片型号尚未支持自动映射，请手动选择匹配芯片的 target/*.cfg。");
        var details = new List<string>(); var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (executable, scripts) in Candidates(current, searchRoots))
        {
            cancellation.ThrowIfCancellationRequested();
            if (!seen.Add(executable + "|" + scripts)) continue;
            var chosenTarget = automatic ? AvailableTarget(scripts, target) : target;
            var tools = current with { OpenOcd = executable, Scripts = scripts };
            try
            {
                await ValidateAsync(tools, chosenTarget, cancellation);
                return new(tools, chosenTarget, true, "已解析 " + chosenTarget + " 及 CMSIS-DAP/SWD 脚本：" + scripts + "（未连接硬件）。");
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or OperationCanceledException)
            { if (details.Count < 8) details.Add(executable + "：" + ex.Message); }
        }
        return new(current, target, false, "未找到可解析 " + target + " 的本机完整 OpenOCD 包；可自动补全或下载后重试。" +
            (details.Count == 0 ? "" : Environment.NewLine + string.Join(Environment.NewLine, details)));
    }

    internal static bool ValidTarget(string target) => Regex.IsMatch(target.Replace('\\', '/'), @"\Atarget/[a-zA-Z0-9_.-]+\.cfg\z", RegexOptions.CultureInvariant);

    // Upstream c36f59d9 renamed stm32wbax to stm32wba5x when adding the distinct WBA6 configuration.
    // No alias is valid for WBA2/WBA6.
    internal static string AvailableTarget(string? scripts, string target)
    {
        if (scripts != null && target == "target/stm32wba5x.cfg" && !File.Exists(Path.Combine(scripts, target)) &&
            File.Exists(Path.Combine(scripts, "target", "stm32wbax.cfg"))) return "target/stm32wbax.cfg";
        if (scripts != null && target == "target/stm32wbax.cfg" && !File.Exists(Path.Combine(scripts, target)) &&
            File.Exists(Path.Combine(scripts, "target", "stm32wba5x.cfg"))) return "target/stm32wba5x.cfg";
        return target;
    }

    private static IEnumerable<(string Executable, string? Scripts)> Candidates(ToolPaths current, IEnumerable<string>? searchRoots)
    {
        if (File.Exists(current.OpenOcd))
        {
            if (current.Scripts != null) yield return (current.OpenOcd!, current.Scripts);
            foreach (var scripts in EnvironmentScanner.PairedScriptDirectories(current.OpenOcd!)) yield return (current.OpenOcd!, scripts);
            var environmentScripts = Environment.GetEnvironmentVariable("OPENOCD_SCRIPTS");
            if (!string.IsNullOrWhiteSpace(environmentScripts)) yield return (current.OpenOcd!, environmentScripts);
        }
        IEnumerable<string> Executables()
        {
            if (searchRoots == null)
            {
                foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
                {
                    string? file = null;
                    try { file = Path.Combine(directory.Trim('"'), "openocd.exe"); } catch (ArgumentException) { }
                    if (file != null && File.Exists(file)) yield return file;
                }
                var managed = ManagedTools.LoadPaths();
                if (File.Exists(managed.OpenOcd)) yield return managed.OpenOcd!;
            }
            foreach (var root in searchRoots ?? KnownRoots())
                foreach (var executable in SearchRoot(root)) yield return executable;
        }
        foreach (var executable in Executables())
            foreach (var scripts in EnvironmentScanner.PairedScriptDirectories(executable)) yield return (executable, scripts);
    }

    private static IEnumerable<string> KnownRoots()
    {
        yield return Path.Combine(ManagedTools.Root, "OpenOcd");
        foreach (var parent in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "xPacks") }
            .Concat(DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed).Select(d => d.RootDirectory.FullName)))
        {
            if (!Directory.Exists(parent)) continue;
            if (Path.GetFileName(parent).Equals("xPacks", StringComparison.OrdinalIgnoreCase)) { yield return parent; continue; }
            foreach (var child in Children(parent).Where(path => Regex.IsMatch(Path.GetFileName(path), @"(?i)^(?:openocd|xpack|stm32cubeclt|st$)"))) yield return child;
        }
    }

    private static IEnumerable<string> SearchRoot(string root)
    {
        var pending = new Queue<(string Path, int Depth)>(); pending.Enqueue((root, 0)); var count = 0;
        while (pending.TryDequeue(out var item) && count++ < 512)
        {
            if (!Directory.Exists(item.Path)) continue;
            try { ToolVersionManager.EnsureOrdinaryPath(item.Path); } catch (IOException) { continue; }
            var executable = Path.Combine(item.Path, "openocd.exe");
            if (File.Exists(executable)) yield return executable;
            if (item.Depth >= 6) continue;
            foreach (var child in Children(item.Path))
            {
                var name = Path.GetFileName(child);
                if (name is "scripts" or "target" or "interface" or "_downloads" or "_verification") continue;
                if (pending.Count + count >= 512) break;
                pending.Enqueue((child, item.Depth + 1));
            }
        }
    }

    private static string[] Children(string root)
    {
        try { return Directory.EnumerateDirectories(root).Take(512).Order(StringComparer.OrdinalIgnoreCase).ToArray(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }
}
