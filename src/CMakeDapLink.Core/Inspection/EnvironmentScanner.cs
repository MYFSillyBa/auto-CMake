namespace CMakeDapLink.Core;

public sealed record ToolPaths(string? CMake, string? Ninja, string? Compiler, string? OpenOcd, string? Scripts);

public static class EnvironmentScanner
{
    public static ToolPaths Scan(ToolPaths? preferred = null)
        => Discover(preferred).Selected;

    public static ToolDiscoveryResult Discover(ToolPaths? preferred = null, ToolDiscoveryOptions? options = null,
        CancellationToken cancellation = default) => ToolDiscovery.Discover(preferred, options ?? new(), cancellation);

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

}
