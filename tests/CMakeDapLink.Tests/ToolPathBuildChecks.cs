using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using CMakeDapLink.Core;

public static class ToolPathBuildChecks
{
    public static void Run(string root, Action<string, Action> check)
    {
        var tools = EnvironmentScanner.Discover(options: new(IncludeDefaultRoots: false)).Selected;
        if (!File.Exists(tools.CMake) || !File.Exists(tools.Ninja) || !File.Exists(tools.Compiler))
            throw new Exception("Tool path regression requires CMake, Ninja and ARM GCC.");
        check("switching tools replaces deleted paths in presets, toolchain and cache without editing the project", () =>
        {
            var project = Path.Combine(root, "tool switch with spaces");
            Directory.CreateDirectory(project);
            var oldBin = Path.Combine(project, "removed tools", "bin"); Directory.CreateDirectory(oldBin);
            var oldNinja = Path.Combine(oldBin, "ninja.exe"); File.Copy(tools.Ninja!, oldNinja);
            var chain = Path.Combine(project, "toolchain.cmake");
            WriteToolchain(chain, tools.Compiler!);
            File.WriteAllText(Path.Combine(project, "main.c"), "int main(void) { return 0; }\n");
            File.WriteAllText(Path.Combine(project, "extra.cpp"), "extern \"C\" int extra(void) { return 3; }\n");
            File.WriteAllText(Path.Combine(project, "support.S"), ".syntax unified\n.thumb\n.text\n.global support_fn\n.thumb_func\nsupport_fn:\n bx lr\n");
            File.WriteAllText(Path.Combine(project, "CMakeLists.txt"), "cmake_minimum_required(VERSION 3.22)\nproject(probe C CXX ASM)\nadd_executable(probe main.c extra.cpp support.S)\nset_target_properties(probe PROPERTIES SUFFIX .elf)\ntarget_compile_options(probe PRIVATE -mcpu=cortex-m7 -mthumb)\ntarget_link_options(probe PRIVATE -mcpu=cortex-m7 -mthumb -nostdlib -Wl,-e,main)\n");
            File.WriteAllText(Path.Combine(project, "CMakePresets.json"), JsonSerializer.Serialize(new
            {
                version = 3,
                configurePresets = new[] { new { name = "Debug", generator = "Ninja", binaryDir = "${sourceDir}/build/Debug", toolchainFile = "${sourceDir}/toolchain.cmake", cacheVariables = new Dictionary<string, string>
                    { ["CMAKE_BUILD_TYPE"] = "Debug", ["CMAKE_MAKE_PROGRAM"] = oldNinja.Replace('\\', '/') } } }
            }));
            var build = Path.Combine(project, "build", "Debug");
            var options = new SetupOptions(project, tools.CMake!, oldNinja, tools.Compiler!, tools.OpenOcd ?? "", tools.Scripts ?? "",
                "target/stm32h7x.cfg", "Debug", null, build, chain);
            var initial = CMakeBuildPlan.Create(options); initial.PrepareArtifactQuery(); Run(initial.Configure); Run(initial.Build);
            var cache = Path.Combine(build, "CMakeCache.txt");
            var oldCompiler = Path.Combine(oldBin, "arm-none-eabi-gcc.exe");
            WriteToolchain(chain, oldCompiler);
            var beforeChain = File.ReadAllBytes(chain);
            var beforePreset = File.ReadAllBytes(Path.Combine(project, "CMakePresets.json"));
            var poisoned = Regex.Replace(File.ReadAllText(cache), @"(?m)^CMAKE_(C|CXX|ASM)_COMPILER:[^=]*=.*$", m =>
                m.Value[..m.Value.IndexOf('=')] + "=" + oldCompiler.Replace('\\', '/'));
            File.WriteAllText(cache, poisoned);
            File.Delete(oldNinja);
            var sentinel = Path.Combine(build, "keep-user-output.txt"); File.WriteAllText(sentinel, "must survive");
            var selected = options with { Ninja = tools.Ninja! };
            var preview = ConfigurationWriter.Preview(selected);
            Require(File.ReadAllText(cache) == poisoned, "preview changed the cache");
            ChangeHistory.Apply(project, "tool switch regression", preview);
            var plan = CMakeBuildPlan.Create(selected); plan.PrepareArtifactQuery(); Run(plan.Configure); Run(plan.Build);
            Require(PathEqual(CacheValue(cache, "CMAKE_C_COMPILER"), tools.Compiler!), "selected compiler was not used");
            Require(PathEqual(CacheValue(cache, "CMAKE_MAKE_PROGRAM"), tools.Ninja!), "selected Ninja was not used");
            Require(File.ReadAllBytes(chain).SequenceEqual(beforeChain) && File.ReadAllBytes(Path.Combine(project, "CMakePresets.json")).SequenceEqual(beforePreset), "project configuration was edited");
            Require(File.ReadAllText(sentinel) == "must survive", "unrelated output was removed");
            var backups = Directory.EnumerateFiles(build, "CMakeCache.txt", SearchOption.AllDirectories).Where(x => x != cache).ToArray();
            Require(backups.Any(x => File.ReadAllText(x) == poisoned), "old cache was not backed up");
            var elf = CMakeBuildPlan.FindElfs(build).Single();
            var bytes = File.ReadAllBytes(elf);
            Require(bytes.Length > 20 && bytes[0] == 0x7f && bytes[1] == 'E' && bytes[18] == 40, "no ARM ELF produced");
            var firmwareTime = File.GetLastWriteTimeUtc(elf);
            var backupCount = backups.Length;
            // Generated tasks must prepare the same selection at execution time.
            var runner = Directory.GetFiles(Path.Combine(project, ".vscode"), "stm32-daplink*.cmake").Single();
            Run(new(tools.CMake!, ["-DACTION=configure", "-P", runner], project, plan.Configure.PathPrefix));
            Run(plan.Build);
            Require(File.GetLastWriteTimeUtc(elf) == firmwareTime, "unchanged selection broke incremental builds");
            Require(Directory.EnumerateFiles(build, "CMakeCache.txt", SearchOption.AllDirectories).Count(x => x != cache) == backupCount, "unchanged selection backed up cache again");
            // A stale cache appearing after task generation must also be repaired by VS Code.
            File.WriteAllText(cache, poisoned);
            Run(new(tools.CMake!, ["-DACTION=configure", "-P", runner], project, plan.Configure.PathPrefix));
            Run(plan.Build);
            Require(PathEqual(CacheValue(cache, "CMAKE_MAKE_PROGRAM"), tools.Ninja!), "generated task retained the deleted Ninja");
        });
    }

    private static void WriteToolchain(string path, string compiler)
    {
        var bin = Path.GetDirectoryName(compiler)!.Replace('\\', '/');
        File.WriteAllText(path, $"set(CMAKE_SYSTEM_NAME Generic)\nset(CMAKE_SYSTEM_PROCESSOR arm)\nset(CMAKE_TRY_COMPILE_TARGET_TYPE STATIC_LIBRARY)\nset(CMAKE_C_COMPILER \"{compiler.Replace('\\', '/')}\")\nset(CMAKE_CXX_COMPILER \"{bin}/arm-none-eabi-g++.exe\")\nset(CMAKE_ASM_COMPILER \"{compiler.Replace('\\', '/')}\")\n");
    }
    private static string CacheValue(string path, string key) => Regex.Match(File.ReadAllText(path), @"(?m)^" + key + @":[^=]*=([^\r\n]*)").Groups[1].Value;
    private static bool PathEqual(string left, string right) => Path.GetFullPath(left).Equals(Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Run(ToolCommand command)
    {
        var info = new ProcessStartInfo(command.Executable) { WorkingDirectory = command.WorkingDirectory, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in command.Arguments) info.ArgumentList.Add(arg);
        info.Environment["PATH"] = command.PathPrefix + Path.PathSeparator + info.Environment["PATH"];
        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(120000)) { process.Kill(true); throw new Exception("command timed out"); }
        var output = stdout.Result + stderr.Result;
        if (process.ExitCode != 0) throw new Exception($"{command.Executable} exited {process.ExitCode}: {output}");
    }
}
