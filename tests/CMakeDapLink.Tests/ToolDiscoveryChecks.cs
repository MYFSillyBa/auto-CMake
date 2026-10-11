using CMakeDapLink.Core;

public static class ToolDiscoveryChecks
{
    public static void Run(string root, Action<string, Action> check)
    {
        var source = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Select(x => Path.Combine(x.Trim('"'), "ninja.exe")).First(File.Exists);
        var fixture = Path.Combine(root, "tool-discovery");
        var first = Path.Combine(fixture, "first", "ninja.exe");
        var second = Path.Combine(fixture, "second", "ninja.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(first)!); Directory.CreateDirectory(Path.GetDirectoryName(second)!);
        File.Copy(source, first); File.Copy(source, second);
        var previousPath = Environment.GetEnvironmentVariable("PATH");
        var saved = File.Exists(ManagedTools.PathsPath) ? File.ReadAllBytes(ManagedTools.PathsPath) : null;
        try
        {
            ManagedTools.SavePaths(new(null, null, null, null, null));
            Environment.SetEnvironmentVariable("PATH", Path.GetDirectoryName(first) + Path.PathSeparator + Path.GetDirectoryName(second));
            check("multiple PATH installations require explicit selection", () =>
            {
                var elapsed = System.Diagnostics.Stopwatch.StartNew();
                if (EnvironmentScanner.Scan().Ninja != null) throw new Exception("scanner silently selected the first PATH installation");
                Console.WriteLine("INFO default discovery elapsed " + elapsed.Elapsed.TotalSeconds.ToString("F2") + "s");
            });
            var isolated = new ToolDiscoveryOptions(Environment.GetEnvironmentVariable("PATH"), [fixture], IncludeDefaultRoots: false, LoadSavedPaths: false);
            check("discovery lists all PATH installations with deduplicated full paths", () =>
            {
                var result = EnvironmentScanner.Discover(options: isolated with { PathValue = isolated.PathValue + Path.PathSeparator + Path.GetDirectoryName(first) });
                var found = result.CandidatesFor("Ninja");
                Require(found.Count == 2 && found.Any(x => x.Executable == first) && found.Any(x => x.Executable == second), "missing or duplicate installation");
                Require(result.Selected.Ninja == null, "ambiguous installations silently selected");
            });
            check("valid preferred selection survives duplicates and deleted candidates are ignored", () =>
            {
                var preferred = EnvironmentScanner.Discover(new(null, second, null, null, null), isolated);
                Require(preferred.Selected.Ninja == second, "preferred path was lost");
                File.Delete(first);
                var unique = EnvironmentScanner.Discover(new(null, first, null, null, null), isolated);
                Require(unique.Selected.Ninja == second && unique.CandidatesFor("Ninja").Count == 1, "deleted installation was retained");
                File.Copy(source, first);
            });
            check("explicit search roots discover an installation absent from PATH", () =>
            {
                var nested = Path.Combine(fixture, "arbitrary location", "tool version", "bin", "ninja.exe");
                Directory.CreateDirectory(Path.GetDirectoryName(nested)!); File.Copy(source, nested);
                var found = EnvironmentScanner.Discover(options: new("", [Path.Combine(fixture, "arbitrary location")], false, false));
                Require(found.Selected.Ninja == nested && found.CandidatesFor("Ninja").Count == 1, "non-PATH installation not discovered");
            });
            check("manual selection is validated and persisted across a fresh scan without overwriting other keys", () =>
            {
                var stale = Path.Combine(fixture, "removed", "cmake.exe");
                ManagedTools.SavePaths(new(stale, first, "another-deleted-compiler.exe", null, "another-scripts"));
                var merged = ManagedTools.SelectToolAsync("Ninja", second).GetAwaiter().GetResult();
                var restarted = EnvironmentScanner.Discover(options: isolated with { LoadSavedPaths = true });
                Require(merged.CMake == stale && merged.Compiler == "another-deleted-compiler.exe" && merged.Scripts == "another-scripts", "unrelated selections were overwritten");
                Require(ManagedTools.LoadPaths().Ninja == second && restarted.Selected.Ninja == second && EnvironmentScanner.Scan().Ninja == second, "manual choice was lost on restart");
            });
            check("wrong executable name or invalid version never changes saved selections", () =>
            {
                var before = File.ReadAllBytes(ManagedTools.PathsPath);
                var invalid = Path.Combine(fixture, "invalid", "ninja.exe");
                Directory.CreateDirectory(Path.GetDirectoryName(invalid)!); File.WriteAllText(invalid, "invalid executable");
                foreach (var path in new[] { invalid, Path.Combine(fixture, "missing", "ninja.exe"), source.Replace("ninja.exe", "cmake.exe") })
                {
                    try { ManagedTools.SelectToolAsync("Ninja", path).GetAwaiter().GetResult(); throw new Exception("invalid tool accepted"); }
                    catch (Exception ex) when (ex is InvalidDataException or IOException or System.ComponentModel.Win32Exception) { }
                    Require(before.AsSpan().SequenceEqual(File.ReadAllBytes(ManagedTools.PathsPath)), "failed validation changed saved selections");
                }
            });
            check("version activation can replace a selection while unrelated saved tools are deleted", () =>
            {
                var manager = new ToolVersionManager(Path.Combine(fixture, "managed"));
                var installation = new ToolInstallation("Ninja", "real", first, null, Path.GetDirectoryName(first)!, new FileInfo(first).Length, false, false);
                var latest = manager.ActivateAsync(installation, new(null, second, null, null, null)).GetAwaiter().GetResult();
                Require(latest.Ninja == first && latest.CMake == ManagedTools.LoadPaths().CMake, "activation overwrote unrelated saved selections");
            });
            check("discovery respects cancellation", () =>
            {
                using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
                try { EnvironmentScanner.Discover(options: isolated, cancellation: cancellation.Token); throw new Exception("cancellation ignored"); }
                catch (OperationCanceledException) { }
            });
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", previousPath);
            if (saved == null) File.Delete(ManagedTools.PathsPath); else File.WriteAllBytes(ManagedTools.PathsPath, saved);
        }
    }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
}
