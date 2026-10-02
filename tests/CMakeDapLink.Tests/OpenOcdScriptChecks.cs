using System.Security.Cryptography;
using System.Text.Json;
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
        check("extracts the complete bundled scripts and original notices with byte-identical source inventory", () =>
        {
            var scripts = BundledOpenOcdScripts.GetDirectoryAsync().GetAwaiter().GetResult();
            var files = Directory.EnumerateFiles(scripts, "*", SearchOption.AllDirectories).ToArray();
            var targets = Directory.EnumerateFiles(Path.Combine(scripts, "target"), "*", SearchOption.AllDirectories).ToArray();
            if (files.Length != 1033 || targets.Length != 361) throw new Exception("bundled scripts inventory incomplete");
            var bundleRoot = Path.GetDirectoryName(scripts)!;
            using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(bundleRoot, "bundle-manifest.json")));
            foreach (var file in manifest.RootElement.GetProperty("files").EnumerateArray())
            {
                var path = Path.Combine(bundleRoot, file.GetProperty("path").GetString()!);
                var expected = file.GetProperty("sha256").GetString()!;
                using var input = File.OpenRead(path);
                if (!Convert.ToHexString(SHA256.HashData(input)).Equals(expected, StringComparison.OrdinalIgnoreCase))
                    throw new Exception("bundled file differs from retained source inventory: " + path);
            }
            var source = @"D:\OpenOCD\openocd\scripts";
            if (Directory.Exists(source))
            {
                var original = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).ToArray();
                if (original.Length != files.Length) throw new Exception("bundled scripts differ from supplied source inventory");
                foreach (var file in original)
                    if (!File.ReadAllBytes(file).AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(scripts, Path.GetRelativePath(source, file)))))
                        throw new Exception("original script bytes were changed: " + file);
            }
            if (!File.Exists(Path.Combine(bundleRoot, "licenses", "openocd-0.12.0", "preferred", "GPL-2.0")))
                throw new Exception("original OpenOCD license missing");
            Console.WriteLine("Bundled scripts: " + scripts + "; files " + files.Length + "; targets " + targets.Length);
        });
        check("reuses verified bundled resources safely across concurrent provider calls", () =>
        {
            var directories = Task.WhenAll(Enumerable.Range(0, 3).Select(_ => BundledOpenOcdScripts.GetDirectoryAsync())).GetAwaiter().GetResult();
            if (directories.Distinct(StringComparer.OrdinalIgnoreCase).Count() != 1 ||
                OpenOcdScripts.MissingFiles(directories[0], "target/stm32h7x.cfg").Count != 0)
                throw new Exception("repeated extraction changed the content-keyed scripts path");
        });
        check("parses bundled H7 and exact manual target variant with the current executable and automatic WBA5 alias", () =>
        {
            var local = EnvironmentScanner.Scan();
            if (local.OpenOcd == null) throw new Exception("normal OpenOCD executable required for software parser check");
            var before = local;
            var scripts = BundledOpenOcdScripts.GetDirectoryAsync().GetAwaiter().GetResult();
            var tools = local with { Scripts = scripts };
            OpenOcdScripts.ValidateAsync(tools, "target/stm32h7x.cfg").GetAwaiter().GetResult();
            const string manualTarget = "target/stm32h7x_dual_bank.cfg";
            OpenOcdScripts.ValidateAsync(tools, manualTarget).GetAwaiter().GetResult();
            var target = OpenOcdScripts.AvailableTarget(scripts, OpenOcdScripts.TargetForChip("STM32WBA55CG")!);
            if (target != "target/stm32wbax.cfg") throw new Exception("embedded WBA5 legacy target was not selected");
            OpenOcdScripts.ValidateAsync(tools, target).GetAwaiter().GetResult();
            if (tools.CMake != before.CMake || tools.Ninja != before.Ninja || tools.Compiler != before.Compiler || tools.OpenOcd != before.OpenOcd)
                throw new Exception("bundled scripts changed an executable tool path");
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

}
