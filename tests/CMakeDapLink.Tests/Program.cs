using System.Text.Json;
using System.Diagnostics;
using CMakeDapLink.Core;

var failures = new List<string>();
void Check(string name, Action test)
{
    try { test(); Console.WriteLine($"PASS {name}"); }
    catch (Exception ex) { failures.Add($"FAIL {name}: {ex.Message}"); }
}
var root = Path.Combine(Path.GetTempPath(), "CMakeDapLinkTests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    FileWorkflowTests.Run(root, Check);
    Check("detects CMake project and STM32H7 target", () =>
    {
        File.WriteAllText(Path.Combine(root, "CMakeLists.txt"), "project(STM32H723xx C ASM)\nadd_executable(app main.c)");
        var result = ProjectInspector.Inspect(root);
        if (result.TargetScript != "target/stm32h7x.cfg" || !result.IsCMakeProject) throw new Exception("unexpected project inspection");
    });
    Check("prefers exact CubeMX part number over stm32cubemx directory name", () =>
    {
        var cubeRoot = Path.Combine(root, "cubemx-identification"); Directory.CreateDirectory(cubeRoot);
        File.WriteAllText(Path.Combine(cubeRoot, "CMakeLists.txt"), "project(test C)\nadd_subdirectory(cmake/stm32cubemx)");
        File.WriteAllText(Path.Combine(cubeRoot, "board.ioc"), "Mcu.CPN=STM32H723VGT6\nMcu.Name=STM32H723VGTx\n");
        var found = ProjectInspector.Inspect(cubeRoot);
        if (found.Chip != "STM32H723VGT6" || found.TargetScript != "target/stm32h7x.cfg")
            throw new Exception($"wrong chip/target: {found.Chip} / {found.TargetScript}");
    });
    Check("uses a real STM32 define when no IOC exists", () =>
    {
        var fallbackRoot = Path.Combine(root, "cmake-fallback"); Directory.CreateDirectory(fallbackRoot);
        File.WriteAllText(Path.Combine(fallbackRoot, "CMakeLists.txt"), "add_subdirectory(cmake/stm32cubemx)\nadd_compile_definitions(STM32F407xx)");
        if (ProjectInspector.Inspect(fallbackRoot).Chip != "STM32F407XX") throw new Exception("chose non-chip text");
    });
    Check("rejects non CMake folder", () =>
    {
        var empty = Path.Combine(root, "empty"); Directory.CreateDirectory(empty);
        if (ProjectInspector.Inspect(empty).IsCMakeProject) throw new Exception("accepted empty folder");
    });
    Check("resolves build directory inherited from hidden preset", () =>
    {
        var presetRoot = Path.Combine(root, "preset project"); Directory.CreateDirectory(presetRoot);
        File.WriteAllText(Path.Combine(presetRoot, "CMakeLists.txt"), "project(STM32F407 C)");
        File.WriteAllText(Path.Combine(presetRoot, "CMakePresets.json"), """
            {"version": 4, "configurePresets": [
              {"name":"base","hidden":true,"binaryDir":"${sourceDir}/build/${presetName}"},
              {"name":"Debug","inherits":"base","generator":"Ninja"}
            ], "buildPresets": [{"name":"DebugBuild","configurePreset":"Debug"}]}
            """);
        var found = ProjectInspector.Inspect(presetRoot);
        if (found.ConfigurePreset != "Debug" || found.BuildPreset != "DebugBuild" ||
            found.BuildDirectory != Path.Combine(presetRoot, "build", "Debug")) throw new Exception("preset inheritance failed");
    });
    Check("finds OpenOCD scripts in xPack layout", () =>
    {
        var install = Path.Combine(root, "openocd install");
        var bin = Path.Combine(install, "bin"); var scripts = Path.Combine(install, "openocd", "scripts");
        Directory.CreateDirectory(bin); Directory.CreateDirectory(Path.Combine(scripts, "interface"));
        Directory.CreateDirectory(Path.Combine(scripts, "target"));
        var exe = Path.Combine(bin, "openocd.exe"); File.WriteAllText(exe, "");
        File.WriteAllText(Path.Combine(scripts, "interface", "cmsis-dap.cfg"), "");
        if (EnvironmentScanner.FindScripts(exe) != scripts) throw new Exception("scripts not detected");
    });
    Check("recognizes CubeMX ARM GCC toolchain file", () =>
    {
        var cube = Path.Combine(root, "cube project");
        Directory.CreateDirectory(Path.Combine(cube, "cmake"));
        File.WriteAllText(Path.Combine(cube, "CMakeLists.txt"), "project(STM32H723 C)");
        var toolchain = Path.Combine(cube, "cmake", "gcc-arm-none-eabi.cmake");
        File.WriteAllText(toolchain, "set(CMAKE_SYSTEM_NAME Generic)");
        if (ProjectInspector.Inspect(cube).ToolchainFile != toolchain) throw new Exception("ARM toolchain not recognized");
    });
    Check("preserves unrelated VS Code tasks and generates two named tasks", () =>
    {
        var vscode = Path.Combine(root, ".vscode"); Directory.CreateDirectory(vscode);
        File.WriteAllText(Path.Combine(vscode, "tasks.json"), "{\"version\":\"2.0.0\",\"tasks\":[{\"label\":\"keep me\",\"type\":\"shell\",\"command\":\"echo hi\"},{\"label\":\"一键启动（DAPLINK）\",\"type\":\"process\",\"command\":\"old\"}]}");
        File.WriteAllText(Path.Combine(vscode, "cmake-daplink.ps1"), "param([ValidateSet('build','flash')][string]$Action = 'build')");
        File.WriteAllText(Path.Combine(vscode, "cmake-daplink.json"), "{\"cmake\":\"old\",\"openocd\":\"old\"}");
        var elf = Path.Combine(root, "build", "daplink-debug", "app.elf");
        Directory.CreateDirectory(Path.GetDirectoryName(elf)!); File.WriteAllText(elf, "ELF");
        var options = new SetupOptions(root, "C:/cmake.exe", "C:/ninja.exe", "C:/arm-none-eabi-gcc.exe", "C:/openocd.exe", "C:/scripts", "target/stm32h7x.cfg", null, null, null, FirmwareElfPath: elf);
        ConfigurationWriter.Write(options);
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(vscode, "tasks.json")));
        var tasks = doc.RootElement.GetProperty("tasks").EnumerateArray().ToArray();
        var labels = tasks.Select(x => x.GetProperty("label").GetString()).ToArray();
        if (!labels.Contains("keep me") || !labels.Contains("一键编译") || !labels.Contains("一键烧录(DAPLINK)")) throw new Exception("missing task");
        if (labels.Contains("一键启动（DAPLINK）") || labels.Length != 4) throw new Exception("old task was not migrated");
        if (tasks.Single(x => x.GetProperty("label").GetString() == "一键编译").GetProperty("command").GetString() != "C:/cmake.exe")
            throw new Exception("build task still uses helper script");
        var flash = tasks.Single(x => x.GetProperty("label").GetString() == "一键烧录(DAPLINK)");
        if (flash.GetProperty("command").GetString() != "C:/openocd.exe" || flash.GetProperty("dependsOn").GetString() != "一键编译")
            throw new Exception("flash task does not call OpenOCD after build");
        if (File.Exists(Path.Combine(vscode, "cmake-daplink.ps1")) || File.Exists(Path.Combine(vscode, "cmake-daplink.json")))
            throw new Exception("legacy helper files were left behind");
        ConfigurationWriter.Write(options);
        using var again = JsonDocument.Parse(File.ReadAllText(Path.Combine(vscode, "tasks.json")));
        if (again.RootElement.GetProperty("tasks").GetArrayLength() != 4) throw new Exception("duplicate generated tasks");
    });
    Check("refuses unknown OpenOCD target path", () =>
    {
        var options = new SetupOptions(root, "cmake", "ninja", "gcc", "openocd", "scripts", "../bad.cfg", null, null, null);
        try { ConfigurationWriter.Write(options); throw new Exception("accepted traversal"); }
        catch (ArgumentException) { }
    });
    Check("previews and merges C and H files from selected project folders", () =>
    {
        var project = Path.Combine(root, "sources project"); Directory.CreateDirectory(project);
        File.WriteAllText(Path.Combine(project, "CMakeLists.txt"), "set(CMAKE_PROJECT_NAME demo)\nproject(${CMAKE_PROJECT_NAME})\nadd_executable(${CMAKE_PROJECT_NAME} main.c)\n");
        var module = Path.Combine(project, "module");
        Directory.CreateDirectory(Path.Combine(module, "private"));
        File.WriteAllText(Path.Combine(module, "driver.c"), "");
        File.WriteAllText(Path.Combine(module, "driver.h"), "");
        File.WriteAllText(Path.Combine(module, "private", "impl.c"), "");
        File.WriteAllText(Path.Combine(module, "private", "impl.h"), "");
        var plan = SourceFolderPlanner.Preview(project, module);
        if (plan.SourceFiles.Count != 2 || plan.HeaderFiles.Count != 2 || plan.IncludeDirectories.Count != 2 ||
            plan.Target != "${CMAKE_PROJECT_NAME}" || !plan.UpdatedText.Contains("target_sources(${CMAKE_PROJECT_NAME} PRIVATE"))
            throw new Exception("preview missing sources, headers, includes, or target");
        SourceFolderPlanner.Apply(plan);
        var cmake = File.ReadAllText(Path.Combine(project, "CMakeLists.txt"));
        if (!cmake.Contains("module/driver.c") || !cmake.Contains("module/private/impl.h") || !cmake.Contains("module/private"))
            throw new Exception("CMake source block incomplete");
        SourceFolderPlanner.Apply(SourceFolderPlanner.Preview(project, module));
        if (File.ReadAllText(Path.Combine(project, "CMakeLists.txt")) != cmake) throw new Exception("repeated apply changed CMake");
        var second = Path.Combine(project, "extra"); Directory.CreateDirectory(second);
        File.WriteAllText(Path.Combine(second, "extra.c"), "");
        SourceFolderPlanner.Apply(SourceFolderPlanner.Preview(project, second));
        var merged = File.ReadAllText(Path.Combine(project, "CMakeLists.txt"));
        if (!merged.Contains("module/driver.c") || !merged.Contains("extra/extra.c")) throw new Exception("existing managed folder lost");
    });
    Check("rejects source folders outside the project", () =>
    {
        var project = Path.Combine(root, "sources project");
        try { SourceFolderPlanner.Preview(project, root); throw new Exception("accepted outside folder"); }
        catch (ArgumentException) { }
    });
    Check("generated task configures and compiles a real ARM ELF in a path with spaces", () =>
    {
        var tools = EnvironmentScanner.Scan();
        if (tools.CMake == null || tools.Ninja == null || tools.Compiler == null || tools.OpenOcd == null || tools.Scripts == null)
        { Console.WriteLine("SKIP integration: ARM tools or OpenOCD scripts unavailable"); return; }
        var project = Path.Combine(root, "arm fixture with spaces"); Directory.CreateDirectory(project);
        File.WriteAllText(Path.Combine(project, "main.c"), "#include \"extra.h\"\nint main(void) { return extra(); }");
        var addedFolder = Path.Combine(project, "module"); Directory.CreateDirectory(addedFolder);
        File.WriteAllText(Path.Combine(addedFolder, "extra.h"), "int extra(void);");
        File.WriteAllText(Path.Combine(addedFolder, "extra.c"), "int extra(void) { return 0; }");
        File.WriteAllText(Path.Combine(addedFolder, "excluded.c"), "#error This unselected file must not be compiled\n");
        var gcc = tools.Compiler.Replace('\\', '/');
        File.WriteAllText(Path.Combine(project, "CMakeLists.txt"), $"""
            cmake_minimum_required(VERSION 3.20)
            set(CMAKE_SYSTEM_NAME Generic)
            set(CMAKE_TRY_COMPILE_TARGET_TYPE STATIC_LIBRARY)
            set(CMAKE_C_COMPILER "{gcc}")
            project(arm_fixture C)
            add_executable(probe main.c)
            set_target_properties(probe PROPERTIES SUFFIX ".elf")
            target_link_options(probe PRIVATE -nostdlib -Wl,-e,main -Wl,-Ttext=0x08000000)
            """);
        SourceFolderPlanner.Apply(SourceFolderPlanner.Preview(project, addedFolder, "probe", ["module/extra.c", "module/extra.h"]));
        var options = new SetupOptions(project, tools.CMake, tools.Ninja, tools.Compiler, tools.OpenOcd,
            tools.Scripts, "target/stm32h7x.cfg", null, null, null);
        var plan = CMakeBuildPlan.Create(options);
        void Run(ToolCommand command)
        {
            using var process = new Process { StartInfo = new ProcessStartInfo(command.Executable)
            { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = command.WorkingDirectory } };
            foreach (var argument in command.Arguments) process.StartInfo.ArgumentList.Add(argument);
            process.StartInfo.Environment["PATH"] = command.PathPrefix + Path.PathSeparator + process.StartInfo.Environment["PATH"];
            process.Start();
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(120000)) { process.Kill(true); throw new Exception("build timeout"); }
            if (process.ExitCode != 0) throw new Exception("build failed: " + stdout.Result + stderr.Result);
        }
        Run(plan.Configure); Run(plan.Build);
        var elf = CMakeBuildPlan.FindSingleElf(Path.Combine(project, "build", "daplink-debug"));
        if (Path.GetFileName(elf) != "probe.elf") throw new Exception("wrong ELF selected");
        ConfigurationWriter.Write(options with { FirmwareElfPath = elf });
        using var ocd = new Process { StartInfo = new ProcessStartInfo(tools.OpenOcd)
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = project } };
        foreach (var argument in new[] { "-s", tools.Scripts, "-f", "interface/cmsis-dap.cfg", "-c", "transport select swd", "-f", "target/stm32h7x.cfg", "-c", "shutdown" })
            ocd.StartInfo.ArgumentList.Add(argument);
        ocd.Start();
        var ocdOut = ocd.StandardOutput.ReadToEndAsync(); var ocdErr = ocd.StandardError.ReadToEndAsync();
        if (!ocd.WaitForExit(20000)) { ocd.Kill(true); throw new Exception("OpenOCD timeout"); }
        if (ocd.ExitCode != 0) throw new Exception("OpenOCD config failed: " + ocdOut.Result + ocdErr.Result);
    });
}
finally { Directory.Delete(root, true); }
foreach (var failure in failures) Console.Error.WriteLine(failure);
return failures.Count == 0 ? 0 : 1;
