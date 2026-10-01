using System.IO.Compression;
using System.Security.Cryptography;
using CMakeDapLink.Core;

public static class ToolManagementChecks
{
    public static void Run(string root, Action<string, Action> check) => RunAsync(root, check).GetAwaiter().GetResult();

    public static async Task RunAsync(string root, Action<string, Action> check)
    {
        var detected = EnvironmentScanner.Scan();
        var existing = detected.Ninja;
        if (!File.Exists(existing)) throw new InvalidOperationException("正常工具管理检查需要本机已有的真实 Ninja 工具。");
        var originalHash = await HashAsync(existing!);
        var fixture = Path.Combine(root, "offline-tool-management"); Directory.CreateDirectory(fixture);
        var installRoot = Path.Combine(fixture, "managed tools");
        var firstZip = Path.Combine(fixture, "ninja-local.zip");
        var secondZip = Path.Combine(fixture, "ninja-local-with-note.zip");
        CreateZip(firstZip, existing!, false); CreateZip(secondZip, existing!, true);
        var manager = new ToolVersionManager(installRoot);
        var empty = new ToolPaths(null, null, null, null, null);
        var first = await manager.ImportZipAsync("Ninja", firstZip, empty);
        var repeated = await manager.ImportZipAsync("Ninja", firstZip, empty);
        var second = await manager.ImportZipAsync("Ninja", secondZip, empty);
        check("offline ZIP imports and validates a real existing Ninja without changing the original", () =>
        {
            if (!File.Exists(first.Executable) || first.Version == "" || first.SizeBytes <= 0 || !first.IsManaged)
                throw new Exception("offline tool inventory is incomplete");
            if (!first.Executable.StartsWith(installRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new Exception("offline tool was not installed in the isolated root");
        });
        check("identical ZIP keeps its stable version path and distinct packages coexist", () =>
        {
            if (first.InstallDirectory != repeated.InstallDirectory || first.InstallDirectory == second.InstallDirectory ||
                first.Version != second.Version || !Directory.Exists(first.InstallDirectory) || !Directory.Exists(second.InstallDirectory))
                throw new Exception("stable installation paths or version coexistence failed");
        });
        if (File.Exists(detected.CMake) && File.Exists(detected.Compiler))
        {
            var verified = await manager.ActivateAsync(first, detected, savePaths: false);
            check("offline Ninja integrates with available CMake and ARM GCC to produce a real ARM ELF", () =>
            {
                if (verified.Ninja != first.Executable || verified.Compiler != detected.Compiler || verified.CMake != detected.CMake)
                    throw new Exception("verified toolchain paths changed unexpectedly");
            });
        }
        if (File.Exists(detected.OpenOcd) && EnvironmentScanner.IsScripts(detected.Scripts))
        {
            var openOcd = new ToolInstallation("OpenOcd", "existing", detected.OpenOcd!, detected.Scripts,
                Path.GetDirectoryName(detected.OpenOcd!)!, new FileInfo(detected.OpenOcd!).Length, false, true);
            var verified = await manager.ActivateAsync(openOcd, detected, savePaths: false);
            check("existing OpenOCD validates CMSIS-DAP scripts using shutdown without hardware initialization", () =>
            {
                if (verified.OpenOcd != detected.OpenOcd || verified.Scripts != detected.Scripts)
                    throw new Exception("OpenOCD validation changed external paths");
            });
        }
        var enabled = await manager.ActivateAsync(first, empty, savePaths: false);
        enabled = await manager.ActivateAsync(second, enabled, savePaths: false);
        check("an installed version is validated before switching", () =>
        {
            if (enabled.Ninja != second.Executable) throw new Exception("second version was not activated");
        });
        enabled = await manager.ActivateAsync(first, enabled, savePaths: false);
        var inventory = await manager.ListAsync(enabled);
        check("version inventory lists path size and active rollback version", () =>
        {
            if (inventory.Count(x => x.Key == "Ninja") != 2 || inventory.Single(x => x.IsActive).Executable != first.Executable ||
                inventory.Any(x => x.Version != first.Version || x.SizeBytes <= 0)) throw new Exception("version inventory or rollback failed");
        });
        var external = empty with { Ninja = existing };
        var externalInventory = await manager.ListAsync(external);
        check("external Ninja appears as active and is never offered for deletion", () =>
        {
            var item = externalInventory.Single(x => x.IsActive);
            if (item.IsManaged || item.Executable != existing || manager.ListCleanup(external).Any(x => x.Path == existing))
                throw new Exception("external active tool was not preserved");
        });
        var cache = Path.Combine(installRoot, "_downloads"); Directory.CreateDirectory(cache);
        File.Copy(firstZip, Path.Combine(cache, "ninja-local.zip"));
        var candidates = manager.ListCleanup(enabled);
        check("cleanup lists named cache and inactive package while retaining active version", () =>
        {
            if (candidates.Count != 2 || candidates.Any(x => x.Path == first.InstallDirectory) ||
                !candidates.Any(x => x.Path == second.InstallDirectory) || candidates.Any(x => x.Name.Length == 0 || x.SizeBytes <= 0))
                throw new Exception("cleanup candidate list is incorrect");
        });
        manager.Cleanup(candidates, enabled);
        var afterHash = await HashAsync(existing!);
        check("confirmed cleanup removes only listed inactive files and original Ninja remains intact", () =>
        {
            if (!File.Exists(first.Executable) || Directory.Exists(second.InstallDirectory) || manager.ListCleanup(enabled).Count != 0 || originalHash != afterHash)
                throw new Exception("cleanup or original tool preservation failed");
        });
    }

    private static void CreateZip(string archive, string executable, bool note)
    {
        using var zip = ZipFile.Open(archive, ZipArchiveMode.Create);
        zip.CreateEntryFromFile(executable, "ninja/bin/ninja.exe");
        if (!note) return;
        using var writer = new StreamWriter(zip.CreateEntry("ninja/package-note.txt").Open());
        writer.Write("Local Ninja package with a separate package note.\n");
    }
    private static async Task<string> HashAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream));
    }
}
