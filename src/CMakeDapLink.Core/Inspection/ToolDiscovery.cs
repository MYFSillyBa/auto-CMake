using Microsoft.Win32;

namespace CMakeDapLink.Core;

public sealed record ToolCandidate(string Key, string Executable);
public sealed record ToolDiscoveryOptions(string? PathValue = null, IReadOnlyList<string>? SearchRoots = null,
    bool IncludeDefaultRoots = true, bool LoadSavedPaths = true, string? ManagedRoot = null);
public sealed record ToolDiscoveryResult(ToolPaths Selected, IReadOnlyList<ToolCandidate> Candidates)
{
    public IReadOnlyList<ToolCandidate> CandidatesFor(string key) => Candidates.Where(x => x.Key == key).ToArray();
}

internal static class ToolDiscovery
{
    internal static string ExecutableName(string key) => key switch
    {
        "CMake" => "cmake.exe", "Ninja" => "ninja.exe", "Compiler" => "arm-none-eabi-gcc.exe",
        "OpenOcd" => "openocd.exe", _ => throw new ArgumentException("未知工具：" + key)
    };

    internal static ToolDiscoveryResult Discover(ToolPaths? preferred, ToolDiscoveryOptions options, CancellationToken cancellation)
    {
        var saved = options.LoadSavedPaths ? ManagedTools.LoadPaths() : new(null, null, null, null, null);
        var candidates = new Dictionary<string, ToolCandidate>(StringComparer.OrdinalIgnoreCase);
        void Add(string key, string? path)
        {
            cancellation.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(path)) return;
            try
            {
                var absolute = Path.GetFullPath(path);
                if (File.Exists(absolute) && Path.GetFileName(absolute).Equals(ExecutableName(key), StringComparison.OrdinalIgnoreCase))
                {
                    ToolVersionManager.EnsureOrdinaryPath(absolute);
                    candidates.TryAdd(absolute, new(key, absolute));
                }
            }
            catch (Exception ex) when (Ignorable(ex)) { }
        }
        void Probe(string directory)
        {
            foreach (var key in ToolVersionManager.Keys) Add(key, Path.Combine(directory, ExecutableName(key)));
        }
        foreach (var key in ToolVersionManager.Keys)
        {
            Add(key, preferred == null ? null : ToolVersionManager.GetPath(preferred, key));
            Add(key, ToolVersionManager.GetPath(saved, key));
        }
        foreach (var entry in (options.PathValue ?? Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            var directory = entry.Trim().Trim('"');
            if (directory.Length == 0) continue;
            try { Probe(directory); } catch (Exception ex) when (Ignorable(ex)) { }
        }
        var roots = new List<string>(options.SearchRoots ?? []);
        if (options.ManagedRoot != null) roots.Add(options.ManagedRoot);
        if (options.IncludeDefaultRoots) roots.AddRange(DefaultRoots());
        // Inspect installation trees, never recursively enumerate a drive or a broad Programs folder.
        var seenDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var pending = new Stack<(string Directory, int Depth)>(); pending.Push((root, 0));
            var visited = 0;
            while (pending.Count > 0 && visited++ < 12000)
            {
                cancellation.ThrowIfCancellationRequested();
                var (directory, depth) = pending.Pop();
                try
                {
                    var absolute = Path.GetFullPath(directory);
                    if (Path.TrimEndingDirectorySeparator(absolute) == Path.TrimEndingDirectorySeparator(Path.GetPathRoot(absolute)!)) continue;
                    if (!Directory.Exists(absolute) || !seenDirectories.Add(absolute)) continue;
                    ToolVersionManager.EnsureOrdinaryPath(absolute);
                    Probe(absolute);
                    if (depth >= 8) continue;
                    foreach (var child in Directory.GetDirectories(absolute))
                        if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0 &&
                            !new[] { ".git", "node_modules", "_downloads", "include", "lib", "libexec", "share", "doc", "docs", "examples", "src" }.Contains(Path.GetFileName(child), StringComparer.OrdinalIgnoreCase))
                            pending.Push((child, depth + 1));
                }
                catch (Exception ex) when (Ignorable(ex)) { }
            }
        }
        var all = candidates.Values.OrderBy(x => x.Key).ThenBy(x => x.Executable, StringComparer.OrdinalIgnoreCase).ToArray();
        string? Pick(string key)
        {
            foreach (var path in new[] { preferred == null ? null : ToolVersionManager.GetPath(preferred, key), ToolVersionManager.GetPath(saved, key) })
                if (path != null)
                    try { if (candidates.TryGetValue(Path.GetFullPath(path), out var found) && found.Key == key) return found.Executable; }
                    catch (Exception ex) when (Ignorable(ex)) { }
            var matches = all.Where(x => x.Key == key).ToArray();
            return matches.Length == 1 ? matches[0].Executable : null;
        }
        var openOcd = Pick("OpenOcd");
        var scripts = EnvironmentScanner.FindScripts(openOcd, preferred?.Scripts ?? (openOcd == saved.OpenOcd ? saved.Scripts : null));
        return new(new(Pick("CMake"), Pick("Ninja"), Pick("Compiler"), openOcd, scripts), all);
    }

    private static bool Ignorable(Exception ex) => ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException;
    private static bool Relevant(string name) => new[] { "STM32CubeCLT", "STM32CubeIDE", "STMicroelectronics", "CMake", "Ninja", "OpenOCD", "Arm", "GNU", "xPack", "GCC" }
        .Any(x => name.StartsWith(x, StringComparison.OrdinalIgnoreCase));
    private static IEnumerable<string> Children(string parent)
    {
        try { return Directory.Exists(parent) ? Directory.GetDirectories(parent).Where(x => Relevant(Path.GetFileName(x))).ToArray() : []; }
        catch (Exception ex) when (Ignorable(ex)) { return []; }
    }
    private static IEnumerable<string> DefaultRoots()
    {
        var roots = new List<string> { ManagedTools.Root,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "xPacks"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "Roaming", "xPacks") };
        foreach (var parent in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) })
            if (!string.IsNullOrWhiteSpace(parent)) roots.AddRange(Children(parent));
        foreach (var drive in DriveInfo.GetDrives())
            try { if (drive.DriveType == DriveType.Fixed && drive.IsReady) roots.AddRange(Children(drive.RootDirectory.FullName)); }
            catch (Exception ex) when (Ignorable(ex)) { }
        if (OperatingSystem.IsWindows())
            foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
                foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
                    try
                    {
                        using var registry = RegistryKey.OpenBaseKey(hive, view);
                        using var uninstall = registry.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                        foreach (var name in uninstall?.GetSubKeyNames() ?? [])
                        {
                            using var entry = uninstall!.OpenSubKey(name);
                            if (entry?.GetValue("InstallLocation") is string location && !string.IsNullOrWhiteSpace(location) &&
                                (Relevant(entry.GetValue("DisplayName") as string ?? "") || Relevant(Path.GetFileName(Path.TrimEndingDirectorySeparator(location))))) roots.Add(location);
                        }
                    }
                    catch (Exception ex) when (Ignorable(ex)) { }
        return roots;
    }
}
