using System.Diagnostics;
using System.Text.Json;
using CMakeDapLink.Core;

internal static class TaskBeforeBuildChecks
{
    public static void Run(string root, Action<string, Action> check)
    {
        check("first-build VS Code tasks are previewed and written before configure or compilation", () =>
        {
            var project = Path.Combine(root, "tasks first 中文 {firmware}"); Directory.CreateDirectory(project);
            var tools = EnvironmentScanner.Scan(); var compiler = tools.Compiler!.Replace('\\', '/');
            File.WriteAllText(Path.Combine(project, "toolchain.cmake"), $"set(CMAKE_SYSTEM_NAME Generic)\nset(CMAKE_SYSTEM_PROCESSOR arm)\nset(CMAKE_C_COMPILER \"{compiler}\")\nset(CMAKE_TRY_COMPILE_TARGET_TYPE STATIC_LIBRARY)\n");
            File.WriteAllText(Path.Combine(project, "CMakeLists.txt"), "cmake_minimum_required(VERSION 3.22)\nproject(actual_name C)\nadd_executable(actual_name main.c)\nset_target_properties(actual_name PROPERTIES OUTPUT_NAME actual_firmware SUFFIX .elf)\ntarget_compile_options(actual_name PRIVATE -mcpu=cortex-m7 -mthumb)\ntarget_link_options(actual_name PRIVATE -mcpu=cortex-m7 -mthumb -nostdlib -Wl,-e,main)\nif(EXISTS \"${CMAKE_SOURCE_DIR}/.vscode/tasks.json\")\n file(WRITE \"${CMAKE_BINARY_DIR}/tasks-first.txt\" \"tasks exist before configure\")\nendif()\n");
            File.WriteAllText(Path.Combine(project, "main.c"), "int main(void) { return 0; }\n");
            var options = new SetupOptions(project, tools.CMake!, tools.Ninja!, tools.Compiler!, tools.OpenOcd!, tools.Scripts ?? "", "target/stm32h7x.cfg", null, null, null, Path.Combine(project, "toolchain.cmake"));
            var preview = ConfigurationWriter.Preview(options);
            var plan = CMakeBuildPlan.Create(options);
            if (Directory.Exists(plan.BuildDirectory) || Directory.Exists(Path.Combine(project, ".vscode")) || preview.Count != 2) throw new Exception("preview should not configure/build or write files");
            ChangeHistory.Apply(project, "write tasks before build", preview);
            if (Directory.Exists(plan.BuildDirectory)) throw new Exception("writing tasks started a build");
            var tasksFile = Path.Combine(project, ".vscode", "tasks.json");
            using var tasks = JsonDocument.Parse(File.ReadAllText(tasksFile));
            var items = tasks.RootElement.GetProperty("tasks").EnumerateArray().ToArray();
            var configure = items.Single(x => x.TryGetProperty("hide", out var hidden) && hidden.ValueKind == JsonValueKind.True);
            var build = items.Single(x => x.GetProperty("label").GetString() == "一键编译");
            var flash = items.Single(x => x.GetProperty("label").GetString() == "一键烧录(DAPLINK)");
            string Execute(JsonElement task, bool resolve = false)
            {
                var arguments = task.GetProperty("args").EnumerateArray().Select(x => x.GetString()!).Select(x => resolve && x == "-DACTION=flash" ? "-DACTION=resolve" : x).ToArray();
                var path = task.GetProperty("options").GetProperty("env").GetProperty("PATH").GetString()!.Replace("${env:PATH}", Environment.GetEnvironmentVariable("PATH"));
                using var process = new Process { StartInfo = new(task.GetProperty("command").GetString()!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = project } };
                foreach (var arg in arguments) process.StartInfo.ArgumentList.Add(arg); process.StartInfo.Environment["PATH"] = path;
                process.Start(); var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(120000)) { process.Kill(true); throw new Exception("generated task timeout"); }
                var text = output.Result + errors.Result; if (process.ExitCode != 0) throw new Exception(text); return text;
            }
            Execute(configure);
            if (!File.Exists(Path.Combine(plan.BuildDirectory, "tasks-first.txt"))) throw new Exception("tasks absent when configure ran");
            var old = Path.Combine(plan.BuildDirectory, "Chassis.elf"); File.WriteAllText(old, "old unrelated ELF");
            Execute(build);
            var resolved = Execute(flash, resolve: true);
            if (!resolved.Contains("actual_firmware.elf") || resolved.Contains("Chassis.elf")) throw new Exception("runner guessed an old output");
            if (ConfigurationWriter.Preview(options).Count != 0 || Directory.EnumerateFiles(project, "*.ps1", SearchOption.AllDirectories).Any()) throw new Exception("runner configuration not stable");
            var snapshot = ChangeHistory.List(project).Single();
            File.AppendAllText(Path.Combine(project, "CMakeLists.txt"), "add_executable(other main.c)\nset_target_properties(other PROPERTIES OUTPUT_NAME other.v2 SUFFIX .elf)\ntarget_compile_options(other PRIVATE -mcpu=cortex-m7 -mthumb)\ntarget_link_options(other PRIVATE -mcpu=cortex-m7 -mthumb -nostdlib -Wl,-e,main)\n");
            Execute(configure); Execute(build);
            var selected = options with { FirmwareElfPath = Path.Combine(plan.BuildDirectory, "other.v2.elf") };
            var binding = ChangeHistory.Apply(project, "bind explicit current firmware", ConfigurationWriter.Preview(selected))!;
            using (var boundTasks = JsonDocument.Parse(File.ReadAllText(tasksFile)))
            {
                var boundFlash = boundTasks.RootElement.GetProperty("tasks").EnumerateArray().Single(x => x.GetProperty("label").GetString() == "一键烧录(DAPLINK)");
                var selectedElf = Execute(boundFlash, resolve: true);
                if (!selectedElf.Contains("other.v2.elf") || selectedElf.Contains("actual_firmware.elf")) throw new Exception("multiple-target selection was not honored");
            }
            ChangeHistory.Restore(project, binding.Id); ChangeHistory.Restore(project, snapshot.Id);
            if (File.Exists(tasksFile) || Directory.EnumerateFiles(Path.Combine(project, ".vscode"), "*.cmake").Any()) throw new Exception("tasks and runner were not restored together");
            var userScript = Path.Combine(project, ".vscode", "stm32-daplink.cmake"); File.WriteAllText(userScript, "# user's own CMake script\n");
            var collision = ConfigurationWriter.Preview(options);
            if (!collision.Any(x => Path.GetFileName(x.Path) == "stm32-daplink-2.cmake") || collision.Any(x => x.Path == userScript)) throw new Exception("user script filename not preserved");
            ChangeHistory.Apply(project, "collision-safe tasks", collision);
            if (File.ReadAllText(userScript) != "# user's own CMake script\n") throw new Exception("user script changed");
        });
    }
}
