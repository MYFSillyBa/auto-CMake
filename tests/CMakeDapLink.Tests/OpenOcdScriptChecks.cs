using CMakeDapLink.Core;

internal static class OpenOcdScriptChecks
{
    public static void Run(string root, Action<string, Action> check, bool download = false)
    {
        check("maps known STM32 families to their own OpenOCD targets", () =>
        {
            var expected = new Dictionary<string, string>
            {
                ["STM32C031C6"] = "stm32c0x", ["STM32F030C6"] = "stm32f0x", ["STM32F103CB"] = "stm32f1x",
                ["STM32F205RG"] = "stm32f2x", ["STM32F303VC"] = "stm32f3x", ["STM32F407VG"] = "stm32f4x",
                ["STM32F767ZI"] = "stm32f7x", ["STM32G071RB"] = "stm32g0x", ["STM32G474RE"] = "stm32g4x",
                ["STM32H723VGT6"] = "stm32h7x", ["STM32L072CZ"] = "stm32l0", ["STM32L152RE"] = "stm32l1",
                ["STM32L476RG"] = "stm32l4x", ["STM32L552ZE"] = "stm32l5x", ["STM32U083RC"] = "stm32u0x",
                ["STM32U385RG"] = "stm32u3x", ["STM32U575ZI"] = "stm32u5x", ["STM32WB55RG"] = "stm32wbx",
                ["STM32WBA55CG"] = "stm32wba5x", ["STM32WL55JC"] = "stm32wlx",
                ["STM32WBA25CE"] = "stm32wba2x", ["STM32WBA65CI"] = "stm32wba6x",
                ["STM32H7R3L8"] = "stm32h7rsx", ["STM32H7S3L8"] = "stm32h7rsx"
            };
            foreach (var (chip, script) in expected)
            {
                if (OpenOcdScripts.TargetForChip(chip) != "target/" + script + ".cfg" ||
                    ProjectInspector.TargetForChip(chip) != OpenOcdScripts.TargetForChip(chip))
                    throw new Exception("family mapping differs: " + chip);
                var project = Path.Combine(root, "chip inspection", chip);
                Directory.CreateDirectory(project);
                File.WriteAllText(Path.Combine(project, "CMakeLists.txt"), "project(firmware C)\n");
                File.WriteAllText(Path.Combine(project, "firmware.ioc"), "Mcu.CPN=" + chip + "\n");
                var inspected = ProjectInspector.Inspect(project);
                if (inspected.Chip != chip || inspected.TargetScript != "target/" + script + ".cfg")
                    throw new Exception("actual project identification differs: " + chip);
            }
        });
        check("reads a complete literal dependency tree within one scripts directory", () =>
        {
            var scripts = Path.Combine(root, "script dependency tree");
            Directory.CreateDirectory(Path.Combine(scripts, "interface")); Directory.CreateDirectory(Path.Combine(scripts, "target"));
            File.WriteAllText(Path.Combine(scripts, "interface", "cmsis-dap.cfg"), "# adapter configuration\n");
            File.WriteAllText(Path.Combine(scripts, "target", "stm32h7x.cfg"), "source [find target/swj-dp.tcl]\nsource [find {target/common.cfg}]\n");
            File.WriteAllText(Path.Combine(scripts, "target", "swj-dp.tcl"), "source [find memory.tcl]\n");
            File.WriteAllText(Path.Combine(scripts, "target", "common.cfg"), "source [find \"memory.tcl\"]\n");
            File.WriteAllText(Path.Combine(scripts, "memory.tcl"), "# complete leaf\n");
            if (OpenOcdScripts.MissingFiles(scripts, "target/stm32h7x.cfg").Count != 0) throw new Exception("complete dependency tree reported missing files");
        });
        check("discovers and parses a complete local paired package outside PATH, preserving other tools and manual targets", () =>
        {
            var local = EnvironmentScanner.Scan();
            if (local.OpenOcd == null || local.Scripts == null) throw new Exception("normal local OpenOCD installation required");
            OpenOcdScripts.ValidateAsync(local, "target/stm32h7x.cfg").GetAwaiter().GetResult();
            var executableDirectory = Path.GetDirectoryName(local.OpenOcd)!;
            var installation = new DirectoryInfo(executableDirectory);
            while (installation.Parent != null && !Path.GetFullPath(local.Scripts).StartsWith(installation.FullName + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) installation = installation.Parent;
            if (installation.Parent == null) throw new Exception("expected paired local installation");
            var copy = Path.Combine(root, "paired discovery", "openocd-local");
            CopyDirectory(installation.FullName, copy);
            var before = new ToolPaths("cmake preserved", "ninja preserved", "compiler preserved", null, null);
            var resolved = OpenOcdScripts.ResolveAsync(before, "STM32H723VGT6", searchRoots: [Path.GetDirectoryName(copy)!]).GetAwaiter().GetResult();
            if (!resolved.Ready || resolved.Target != "target/stm32h7x.cfg" || resolved.Tools.OpenOcd == null ||
                !resolved.Tools.OpenOcd.StartsWith(copy, StringComparison.OrdinalIgnoreCase) || resolved.Tools.CMake != before.CMake ||
                resolved.Tools.Ninja != before.Ninja || resolved.Tools.Compiler != before.Compiler) throw new Exception("paired discovery changed unrelated tools or did not parse selected target: " + resolved.Details);
            var manual = OpenOcdScripts.ResolveAsync(resolved.Tools, "STM32H723VGT6", "target/stm32f4x.cfg", searchRoots: []).GetAwaiter().GetResult();
            if (!manual.Ready || manual.Target != "target/stm32f4x.cfg") throw new Exception("manual target was not preserved: " + manual.Details);
            var wba = OpenOcdScripts.ResolveAsync(resolved.Tools, "STM32WBA55CG", searchRoots: []).GetAwaiter().GetResult();
            var expectedWba = File.Exists(Path.Combine(resolved.Tools.Scripts!, "target", "stm32wba5x.cfg")) ? "target/stm32wba5x.cfg" : "target/stm32wbax.cfg";
            if (!wba.Ready || wba.Target != expectedWba) throw new Exception("confirmed WBA5 alias did not parse: " + wba.Details);
            var requestedWba = OpenOcdScripts.ResolveAsync(before, "STM32WBA55CG", "target/stm32wba5x.cfg",
                searchRoots: [Path.GetDirectoryName(copy)!], allowTargetAlias: true).GetAwaiter().GetResult();
            if (!requestedWba.Ready || requestedWba.Target != expectedWba || requestedWba.Tools.OpenOcd != resolved.Tools.OpenOcd)
                throw new Exception("explicitly allowed automatic WBA5 alias did not resolve the complete discovered package: " + requestedWba.Details);
            var canonical = Path.Combine(resolved.Tools.Scripts!, "target", "stm32wba5x.cfg");
            if (!File.Exists(canonical)) File.Copy(Path.Combine(resolved.Tools.Scripts!, "target", "stm32wbax.cfg"), canonical);
            var manualWba = OpenOcdScripts.ResolveAsync(resolved.Tools, "STM32WBA55CG", "target/stm32wba5x.cfg", searchRoots: []).GetAwaiter().GetResult();
            if (!manualWba.Ready || manualWba.Target != "target/stm32wba5x.cfg") throw new Exception("manual canonical WBA5 target was changed: " + manualWba.Details);
        });
        if (download) check("downloads SHA-verified official paired OpenOCD package into Temp and parses STM32H7 without activation", () =>
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("CMakeDapLink-PositiveChecks/1.0");
            var catalog = new ToolDownloadCatalog(http, ManagedTools.LoadSettings(), Console.WriteLine);
            var package = catalog.OfficialAsync("OpenOcd", CancellationToken.None).GetAwaiter().GetResult();
            var saved = ManagedTools.LoadPaths();
            using var installer = new ToolRepairInstaller(Path.Combine(root, "official paired installation"));
            var installed = installer.InstallPackageAsync(package, new(null, null, null, null, null)).GetAwaiter().GetResult();
            var tools = new ToolPaths(null, null, null, installed.Executable, installed.Scripts);
            OpenOcdScripts.ValidateAsync(tools, "target/stm32h7x.cfg").GetAwaiter().GetResult();
            if (ManagedTools.LoadPaths() != saved) throw new Exception("isolated package check changed saved active tools");
            Console.WriteLine("Official package: " + package.Version + " SHA-256 " + package.Sha256);
            Console.WriteLine("Official STM32 targets: " + string.Join(", ", Directory.EnumerateFiles(Path.Combine(installed.Scripts!, "target"), "stm32*.cfg").Select(Path.GetFileName).Order(StringComparer.OrdinalIgnoreCase)));
        });
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source)) File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new IOException("local package contains links");
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }
    }
}
