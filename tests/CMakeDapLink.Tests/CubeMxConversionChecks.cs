using System.Diagnostics;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;
using CMakeDapLink.Core;
using CMakeDapLink.TestFixtures;

internal static class CubeMxConversionChecks
{
    public static void Run(string root, Action<string, Action> check)
    {
        var project = Path.Combine(root, "CubeMX firmware"); CubeMxFixture.Create(project);
        ConversionPlan? forward = null; CMakeBuildPlan? build = null; ConversionPlan? reverse = null;
        check("CubeMX MDK inspection and preview preserve all existing inputs without writing", () =>
        {
            var inspection = CubeMxConverter.Inspect(project, ConversionDirection.MdkToCMake);
            Require(inspection.Chip == "STM32F103RCT6" && inspection.Targets.SequenceEqual([CubeMxFixture.Target]) && inspection.Issues.All(x => !x.Blocking), "inspection", inspection.Issues);
            var originals = Directory.EnumerateFiles(project, "*", SearchOption.AllDirectories).ToDictionary(x => x, File.ReadAllBytes);
            forward = CubeMxConverter.CreatePlan(new(project, ConversionDirection.MdkToCMake, CubeMxFixture.Target, inspection.SourceProjects.Single()));
            Require(forward.CanApply && forward.SourceCount == 5 && forward.IncludeCount == 1, "MDK preview", forward.Issues);
            Require(!File.Exists(forward.OutputProject) && originals.All(x => File.ReadAllBytes(x.Key).SequenceEqual(x.Value)), "preview wrote files");
            ChangeHistory.Apply(project, "MDK 转 CMake", forward.Changes);
            var toolchainPath = Path.Combine(project, "cmake", "gcc-arm-none-eabi.cmake");
            File.AppendAllText(toolchainPath, "set(CMAKE_ASM_FLAGS \"${CMAKE_ASM_FLAGS} -x assembler-with-cpp -MMD -MP\")\nset(CMAKE_C_FLAGS \"${CMAKE_C_FLAGS} -fstack-usage\")\nset(CMAKE_CXX_FLAGS \"${CMAKE_CXX_FLAGS} -fno-exceptions -fno-threadsafe-statics\")\n");
            File.AppendAllText(Path.Combine(project, "CMakeLists.txt"), "set_source_files_properties(Core/Src/support.cpp PROPERTIES COMPILE_OPTIONS -fno-rtti)\n");
            Require(originals.All(x => File.ReadAllBytes(x.Key).SequenceEqual(x.Value)), "source input changed");
            using var presets = JsonDocument.Parse(File.ReadAllText(Path.Combine(project, "CMakePresets.json")));
            Require(presets.RootElement.GetProperty("configurePresets").EnumerateArray().Select(x => x.GetProperty("name").GetString()).SequenceEqual(["Debug", "Release"]), "missing configurations");
        });
        check("generated GCC CMake actually compiles ELF HEX BIN with correct vector and memory addresses", () =>
        {
            Require(forward?.CanApply == true, "forward preview unavailable"); var tools = EnvironmentScanner.Scan();
            Require(File.Exists(tools.CMake) && File.Exists(tools.Ninja) && File.Exists(tools.Compiler), "installed CMake/Ninja/ARM GCC required");
            var options = new SetupOptions(project, tools.CMake!, tools.Ninja!, tools.Compiler!, "", "", "target/stm32f1x.cfg", "Debug", "Debug", null, Path.Combine(project, "cmake", "gcc-arm-none-eabi.cmake"));
            build = CMakeBuildPlan.Create(options); build.PrepareArtifactQuery(); Run(build.Configure); Run(build.Build);
            var elf = CurrentFirmwareArtifacts.Find(build.BuildDirectory, "Debug").Single(); var bin = Path.ChangeExtension(elf, ".bin");
            Require(File.Exists(Path.ChangeExtension(elf, ".hex")) && File.Exists(bin), "firmware artifacts missing");
            var data = File.ReadAllBytes(bin); Require(BitConverter.ToUInt32(data, 0) == 0x2000C000 && (BitConverter.ToUInt32(data, 4) & 1) == 1, "vector initial SP/reset mismatch");
            var toolDir = Path.GetDirectoryName(tools.Compiler)!;
            var objdump = Path.Combine(toolDir, "arm-none-eabi-objdump.exe"); var sections = Run(new(objdump, ["-h", elf], project, build.Configure.PathPrefix));
            Require(sections.Contains(".isr_vector") && sections.Contains("08000000") && sections.Contains("20000000"), "ELF sections/memory mismatch");
            RequireRamFunction(elf, project, tools.Compiler!, build.Configure.PathPrefix);
        });
        check("configured CMake File API preview preserves compile settings and emits schema-valid MDK Arm Compiler 6", () =>
        {
            Require(build != null, "GCC configure unavailable");
            reverse = CubeMxConverter.CreatePlan(new(project, ConversionDirection.CMakeToMdk, CubeMxFixture.Target, BuildDirectory: build!.BuildDirectory));
            Require(reverse.CanApply, "CMake preview", reverse.Issues);
            var originalMdk = File.ReadAllBytes(reverse.OutputProject); Require(reverse.Changes.Single(x => x.Path == reverse.OutputProject).Before!.SequenceEqual(originalMdk), "overwrite preview before image missing");
            ChangeHistory.Apply(project, "CMake 转 MDK", reverse.Changes);
            var xml = XDocument.Load(reverse.OutputProject);
            Require(xml.Descendants("uAC6").Single().Value == "1" && xml.Descendants("Device").Single().Value == "STM32F103RC", "target device/compiler mismatch");
            Require(xml.Descendants("Define").Any(x => x.Value.Contains("STM32F103xE") && x.Value.Contains("USE_HAL_DRIVER")), "macro settings lost");
            Require(xml.Descendants("MiscControls").Any(x => x.Value.Contains("-fno-threadsafe-statics")), "C++ static initialization option lost");
            var rttiGroup = xml.Descendants("Group").Single(x => x.Descendants("FileName").Any(f => f.Value == "rtti.cpp"));
            var noRttiGroup = xml.Descendants("Group").Single(x => x.Descendants("FileName").Any(f => f.Value == "support.cpp"));
            Require(rttiGroup.Descendants("v6Rtti").Single().Value == "1" && noRttiGroup.Descendants("v6Rtti").Single().Value == "0", "effective C++ RTTI settings lost");
            foreach (var source in xml.Descendants("FilePath")) Require(File.Exists(Path.GetFullPath(source.Value, Path.GetDirectoryName(reverse.OutputProject)!)), "source reference missing: " + source.Value);
            foreach (var include in xml.Descendants("IncludePath").SelectMany(x => x.Value.Split(';', StringSplitOptions.RemoveEmptyEntries))) Require(Directory.Exists(Path.GetFullPath(include, Path.GetDirectoryName(reverse.OutputProject)!)), "include reference missing: " + include);
            const string schema = "D:/Keil_v5/UV4/project_projx.xsd";
            if (File.Exists(schema))
            {
                var schemas = new XmlSchemaSet(); schemas.Add(null, schema); var errors = new List<string>();
                xml.Validate(schemas, (_, e) => errors.Add(e.Message)); Require(errors.Count == 0, "MDK schema: " + string.Join("; ", errors));
            }
            else Console.WriteLine("INFO Keil schema unavailable; XML and source references verified.");
            var readBack = CubeMxConverter.Inspect(project, ConversionDirection.MdkToCMake); Require(readBack.Targets.Contains(CubeMxFixture.Target) && readBack.Issues.All(x => !x.Blocking), "MDK inspection after roundtrip", readBack.Issues);
            var reimport = CubeMxConverter.CreatePlan(new(project, ConversionDirection.MdkToCMake, CubeMxFixture.Target, reverse.OutputProject));
            Require(reimport.CanApply, "converted MDK reimport", reimport.Issues);
        });
        check("optional installed Keil builds converted project without hardware", () =>
        {
            Require(reverse?.CanApply == true, "reverse preview unavailable"); const string uv4 = "D:/Keil_v5/UV4/UV4.exe";
            if (!File.Exists(uv4)) { Console.WriteLine("INFO Keil not installed; conversion verified structurally and GCC built."); return; }
            var log = Path.Combine(root, "keil-build.log");
            using var process = new Process { StartInfo = new(uv4) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = project } };
            foreach (var arg in new[] { "-b", reverse!.OutputProject, "-j0", "-t", CubeMxFixture.Target, "-o", log }) process.StartInfo.ArgumentList.Add(arg);
            process.Start(); if (!process.WaitForExit(120000)) { process.Kill(true); throw new Exception("Keil build timed out"); }
            var output = File.Exists(log) ? File.ReadAllText(log) : "no log"; Console.WriteLine(output);
            Require(process.ExitCode <= 1 && File.Exists(Path.Combine(project, "MDK-ARM", "Objects", CubeMxFixture.Target, CubeMxFixture.Target + ".axf")), "Keil compile: " + output);
            var tools = EnvironmentScanner.Scan();
            RequireRamFunction(Path.Combine(project, "MDK-ARM", "Objects", CubeMxFixture.Target, CubeMxFixture.Target + ".axf"), project, tools.Compiler!, build!.Configure.PathPrefix);
        });
        check("Release File API keeps configuration macros in converted MDK groups", () =>
        {
            var tools = EnvironmentScanner.Scan();
            var options = new SetupOptions(project, tools.CMake!, tools.Ninja!, tools.Compiler!, "", "", "target/stm32f1x.cfg", "Release", "Release", null, Path.Combine(project, "cmake", "gcc-arm-none-eabi.cmake")) { BuildConfiguration = "Release" };
            var release = CMakeBuildPlan.Create(options); release.PrepareArtifactQuery(); Run(release.Configure); Run(release.Build);
            var plan = CubeMxConverter.CreatePlan(new(project, ConversionDirection.CMakeToMdk, CubeMxFixture.Target, BuildDirectory: release.BuildDirectory, Configuration: "Release"));
            Require(plan.CanApply, "Release preview", plan.Issues);
            var xmlChange = plan.Changes.Single(x => x.Path == plan.OutputProject); var xml = XDocument.Parse(System.Text.Encoding.UTF8.GetString(xmlChange.After!));
            Require(xml.Descendants("Define").Any(x => x.Value.Split(',').Contains("NDEBUG", StringComparer.Ordinal)), "Release NDEBUG lost");
        });
    }
    private static void RequireRamFunction(string elf, string project, string compiler, string pathPrefix)
    {
        var nm = Path.Combine(Path.GetDirectoryName(compiler)!, "arm-none-eabi-nm.exe");
        var symbols = Run(new(nm, ["-n", elf], project, pathPrefix));
        var match = System.Text.RegularExpressions.Regex.Match(symbols, @"(?m)^([0-9a-fA-F]+)\s+\w\s+ram_value\s*$");
        Require(match.Success && Convert.ToUInt64(match.Groups[1].Value, 16) is >= 0x20000000 and < 0x2000C000, "RAM function execution address lost");
    }
    private static void Require(bool condition, string message, IReadOnlyList<ConversionIssue>? issues = null)
    {
        if (!condition) throw new Exception(message + (issues == null ? "" : ": " + string.Join("; ", issues.Select(x => x.Message + " " + x.Action))));
    }
    private static string Run(ToolCommand command)
    {
        using var process = new Process { StartInfo = new(command.Executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = command.WorkingDirectory } };
        foreach (var arg in command.Arguments) process.StartInfo.ArgumentList.Add(arg);
        process.StartInfo.Environment["PATH"] = command.PathPrefix + Path.PathSeparator + process.StartInfo.Environment["PATH"];
        process.Start(); var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(120000)) { process.Kill(true); throw new Exception("compiler timeout"); }
        var text = output.Result + errors.Result; if (process.ExitCode != 0) throw new Exception(text); return text;
    }
}
