using System.Diagnostics;
using System.Text;
using CMakeDapLink.Core;

internal static class SoftwareWorkflowChecks
{
    public static void Run(string root, Action<string, Action> check)
    {
        check("extracts GCC, CMake and linker diagnostic locations and deduplicates repeated output", () =>
        {
            var log = "module/motor.c:12:4: error: missing.h: No such file or directory\n" +
                "module/motor.c:12:4: error: missing.h: No such file or directory\n" +
                "CMake Error at CMakeLists.txt:24 (add_executable):\n  Cannot find source file:\n    source.c\n\n" +
                "main.c:45: undefined reference to `Motor_Init'\nld.exe: multiple definition of `main'\n";
            var result = BuildDiagnostics.Parse(log, root, "源码编译");
            if (result.Count != 4 || result[0].Line != 12 || result[0].Column != 4 || !result[0].Suggestion.Contains("搜索目录"))
                throw new Exception("diagnostic details mismatch");
            if (!result.Any(x => x.Stage == "链接" && x.Line == 45) || !result.Any(x => x.Stage == "CMake 配置" && x.Line == 24))
                throw new Exception("missing CMake/linker location");
        });
        check("scans C, C++, ASM and headers, skips known registered sources and preserves manual CMake text", () =>
        {
            var project = Path.Combine(root, "source languages"); var module = Path.Combine(project, "module"); Directory.CreateDirectory(module);
            var original = "cmake_minimum_required(VERSION 3.22)\nproject(probe C)\n# manual settings\nadd_executable(probe main.c module/existing.c)\n";
            File.WriteAllText(Path.Combine(project, "CMakeLists.txt"), original, new UTF8Encoding(true));
            foreach (var path in new[] { "existing.c", "motor.c", "motor.cpp", "startup.S", "motor.hpp" }) File.WriteAllText(Path.Combine(module, path), "");
            var plan = SourceFolderPlanner.Preview(project, module, "probe");
            if (plan.CandidateFiles.Count != 5 || plan.SourceFiles.Count != 4 || plan.HeaderFiles.Count != 1 || plan.AlreadyRegisteredFiles.Single() != "module/existing.c")
                throw new Exception("wrong candidate classification");
            if (!plan.UpdatedText.Contains("enable_language(CXX)") || !plan.UpdatedText.Contains("enable_language(ASM)") || !plan.UpdatedText.Contains(original.TrimEnd()))
                throw new Exception("languages or manual settings not preserved");
            if (plan.UpdatedText.Contains("\"${CMAKE_CURRENT_SOURCE_DIR}/module/existing.c\"")) throw new Exception("existing source duplicated");
            var changes = SourceFolderPlanner.Changes(plan); ChangeHistory.Apply(project, "添加模块", changes);
            if (!File.ReadAllBytes(Path.Combine(project, "CMakeLists.txt")).AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) throw new Exception("BOM lost");
            var repeat = SourceFolderPlanner.Preview(project, module, "probe");
            if (repeat.UpdatedText != plan.UpdatedText) throw new Exception("second preview not stable");
            ChangeHistory.Restore(project, ChangeHistory.List(project).First(x => x.Label == "添加模块").Id);
            if (File.ReadAllText(Path.Combine(project, "CMakeLists.txt")) != original) throw new Exception("source restore mismatch");
        });
        check("maintains independent source lists for multiple CMake targets", () =>
        {
            var project = Path.Combine(root, "multiple source targets"); var first = Path.Combine(project, "first"); var second = Path.Combine(project, "second");
            Directory.CreateDirectory(first); Directory.CreateDirectory(second);
            File.WriteAllText(Path.Combine(project, "CMakeLists.txt"), "project(example C)\nadd_library(first STATIC)\nadd_library(second STATIC)\n");
            File.WriteAllText(Path.Combine(first, "a.c"), ""); File.WriteAllText(Path.Combine(second, "b.c"), "");
            SourceFolderPlanner.Apply(SourceFolderPlanner.Preview(project, first, "first"));
            SourceFolderPlanner.Apply(SourceFolderPlanner.Preview(project, second, "second"));
            var text = File.ReadAllText(Path.Combine(project, "CMakeLists.txt"));
            if (!text.Contains("target_sources(first PRIVATE") || !text.Contains("target_sources(second PRIVATE")) throw new Exception("lost source target");
        });
        check("keeps conditional and unused child sources selectable instead of treating them as duplicates", () =>
        {
            var project = Path.Combine(root, "conditional sources"); var module = Path.Combine(project, "module"); Directory.CreateDirectory(module);
            File.WriteAllText(Path.Combine(module, "needed.c"), "int needed(void) { return 1; }\n");
            File.WriteAllText(Path.Combine(project, "CMakeLists.txt"), "project(probe C)\nadd_executable(probe main.c)\nif(FALSE)\n target_sources(probe PRIVATE module/needed.c)\nendif()\n");
            File.WriteAllText(Path.Combine(module, "CMakeLists.txt"), "target_sources(probe PRIVATE needed.c)\n");
            var plan = SourceFolderPlanner.Preview(project, module, "probe");
            if (plan.AlreadyRegisteredFiles.Count != 0 || !plan.UpdatedText.Contains("\"${CMAKE_CURRENT_SOURCE_DIR}/module/needed.c\""))
                throw new Exception("conditional or unused source wrongly excluded");
            if (plan.Notes.Count == 0) throw new Exception("uncertain registration not disclosed");
        });
        check("extracts linker memory overflow diagnostics and advice", () =>
        {
            var issues = BuildDiagnostics.Parse("ld.exe: region `FLASH' overflowed by 32 bytes\nld.exe: section .bss will not fit in region `RAM'\ncollect2: error: ld returned 1 exit status", root, "源码编译");
            if (issues.Count != 3 || issues.Any(x => x.Stage != "链接") || !issues[0].Suggestion.Contains("FLASH/RAM"))
                throw new Exception("linker overflow not classified");
        });
        check("actually configures and compiles a mixed C/C++/ASM firmware with the installed toolchain", () =>
        {
            var tools = EnvironmentScanner.Scan();
            if (!File.Exists(tools.CMake) || !File.Exists(tools.Ninja) || !File.Exists(tools.Compiler)) throw new Exception("normal flow needs installed CMake/Ninja/ARM GCC");
            var project = Path.Combine(root, "mixed firmware"); var module = Path.Combine(project, "module"); Directory.CreateDirectory(module);
            File.WriteAllText(Path.Combine(project, "main.c"), "extern int motor_value(void); int main(void) { return motor_value(); }\n");
            File.WriteAllText(Path.Combine(module, "motor.cpp"), "extern \"C\" int motor_value(void) { return 7; }\n");
            File.WriteAllText(Path.Combine(module, "motor.hpp"), "#pragma once\n");
            File.WriteAllText(Path.Combine(module, "support.S"), ".syntax unified\n.thumb\n.text\n.global support_fn\n.thumb_func\nsupport_fn:\n bx lr\n");
            var compilerDirectory = Path.GetDirectoryName(tools.Compiler)!.Replace('\\', '/');
            File.WriteAllText(Path.Combine(project, "toolchain.cmake"), $"set(CMAKE_SYSTEM_NAME Generic)\nset(CMAKE_SYSTEM_PROCESSOR arm)\nset(CMAKE_C_COMPILER \"{compilerDirectory}/arm-none-eabi-gcc.exe\")\nset(CMAKE_CXX_COMPILER \"{compilerDirectory}/arm-none-eabi-g++.exe\")\nset(CMAKE_ASM_COMPILER \"{compilerDirectory}/arm-none-eabi-gcc.exe\")\nset(CMAKE_TRY_COMPILE_TARGET_TYPE STATIC_LIBRARY)\n");
            File.WriteAllText(Path.Combine(project, "CMakeLists.txt"), "cmake_minimum_required(VERSION 3.22)\nproject(probe C)\nadd_executable(probe main.c)\nset_target_properties(probe PROPERTIES SUFFIX .elf)\ntarget_compile_options(probe PRIVATE -mcpu=cortex-m4 -mthumb)\ntarget_link_options(probe PRIVATE -mcpu=cortex-m4 -mthumb -nostdlib -Wl,-e,main)\n");
            SourceFolderPlanner.Apply(SourceFolderPlanner.Preview(project, module, "probe"));
            var options = new SetupOptions(project, tools.CMake!, tools.Ninja!, tools.Compiler!, tools.OpenOcd ?? "", tools.Scripts ?? "", "target/stm32f4x.cfg", null, null, null, Path.Combine(project, "toolchain.cmake"));
            var build = CMakeBuildPlan.Create(options);
            build.PrepareArtifactQuery();
            Directory.CreateDirectory(build.BuildDirectory);
            var stale = Path.Combine(build.BuildDirectory, "Chassis.elf");
            File.WriteAllText(stale, "leftover firmware from an old target");
            File.SetLastWriteTimeUtc(stale, new DateTime(2026, 6, 4, 0, 0, 0, DateTimeKind.Utc));
            Run(build.Configure); Run(build.Build);
            var elf = CMakeBuildPlan.FindSingleElf(Path.Combine(project, "build", "daplink-debug"));
            if (Path.GetFileName(elf) != "probe.elf" || !File.Exists(stale)) throw new Exception("selected or removed old firmware");
            var timestamp = File.GetLastWriteTimeUtc(elf);
            Run(build.Build);
            if (CMakeBuildPlan.FindElfs(build.BuildDirectory).Single() != elf || File.GetLastWriteTimeUtc(elf) != timestamp)
                throw new Exception("up-to-date build lost its valid firmware");
            var change = ConfigurationWriter.Preview(options with { FirmwareElfPath = elf });
            ChangeHistory.Apply(project, "配置任务", change);
            var taskText = File.ReadAllText(Path.Combine(project, ".vscode", "tasks.json"));
            if (!taskText.Contains("一键编译") || !taskText.Contains("一键烧录(DAPLINK)")) throw new Exception("missing tasks after real mixed-language build");
            if (!taskText.Contains("probe.elf") || taskText.Contains("Chassis.elf")) throw new Exception("flash task points to old firmware");
            ChangeHistory.Restore(project, ChangeHistory.List(project).First(x => x.Label == "配置任务").Id);
            if (File.Exists(Path.Combine(project, ".vscode", "tasks.json"))) throw new Exception("new task file was not restored to absent state");
        });
    }
    private static void Run(ToolCommand command)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo(command.Executable) { UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = command.WorkingDirectory, CreateNoWindow = true } };
        foreach (var argument in command.Arguments) process.StartInfo.ArgumentList.Add(argument);
        process.StartInfo.Environment["PATH"] = command.PathPrefix + Path.PathSeparator + process.StartInfo.Environment["PATH"];
        process.Start(); var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(120000)) { process.Kill(true); throw new Exception("normal build timed out"); }
        if (process.ExitCode != 0) throw new Exception("normal build failed: " + output.Result + error.Result);
    }
}
