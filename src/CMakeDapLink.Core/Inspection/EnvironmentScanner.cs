namespace CMakeDapLink.Core;

public sealed record ToolPaths(string? CMake, string? Ninja, string? Compiler, string? OpenOcd, string? Scripts);

public static class EnvironmentScanner
{
    public static ToolPaths Scan(ToolPaths? preferred = null)
    {
        var managed = ManagedTools.LoadPaths();
        var cmake = Valid(preferred?.CMake) ?? Valid(managed.CMake) ?? Find("cmake.exe");
        var ninja = Valid(preferred?.Ninja) ?? Valid(managed.Ninja) ?? Find("ninja.exe");
        var compiler = Valid(preferred?.Compiler) ?? Valid(managed.Compiler) ?? Find("arm-none-eabi-gcc.exe");
        var openocd = Valid(preferred?.OpenOcd) ?? Valid(managed.OpenOcd) ?? Find("openocd.exe");
        return new(cmake, ninja, compiler, openocd, FindScripts(openocd, preferred?.Scripts ?? (openocd == managed.OpenOcd ? managed.Scripts : null)));
    }

    public static string? FindScripts(string? openocd, string? preferred = null)
    {
        if (IsScripts(preferred)) return Path.GetFullPath(preferred!);
        if (openocd == null) return null;
        return PairedScriptDirectories(openocd).FirstOrDefault(IsScripts);
    }

    internal static IEnumerable<string> PairedScriptDirectories(string openocd)
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(openocd))!);
        for (var i = 0; dir != null && i < 4; i++, dir = dir.Parent)
        {
            if (dir.Parent == null) yield break;
            var found = false;
            foreach (var candidate in new[] { Path.Combine(dir.FullName, "scripts"), Path.Combine(dir.FullName, "openocd", "scripts"), Path.Combine(dir.FullName, "share", "openocd", "scripts") })
                if (Directory.Exists(candidate)) { found = true; yield return candidate; }
            if (found) yield break;
        }
    }

    public static bool IsScripts(string? path) => path != null &&
        File.Exists(Path.Combine(path, "interface", "cmsis-dap.cfg")) && Directory.Exists(Path.Combine(path, "target"));

    private static string? Valid(string? path) => path != null && File.Exists(path) ? Path.GetFullPath(path) : null;
    private static string? Find(string name)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            try { var path = Path.Combine(dir.Trim('"'), name); if (File.Exists(path)) return Path.GetFullPath(path); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { }
        }
        return null;
    }
}
