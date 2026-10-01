using CMakeDapLink.Core;

internal static class InspectionEnhancementChecks
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "InspectionEnhancements-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var presets = Path.Combine(root, "presets");
            Directory.CreateDirectory(presets);
            Directory.CreateDirectory(Path.Combine(root, "cmake"));
            File.WriteAllText(Path.Combine(root, "startup_stm32h723xx.s"), ".syntax unified\n");
            File.WriteAllText(Path.Combine(root, "CMakeLists.txt"), "project(firmware C ASM)\nadd_compile_definitions(STM32H723xx)\nadd_executable(app startup_stm32h723xx.s main.c)\n");
            File.WriteAllText(Path.Combine(root, "board.ioc"), "Mcu.CPN=STM32H723VGT6\nMcu.Name=STM32H723VGTx\n");
            var toolchain = Path.Combine(root, "cmake", "gcc-arm-none-eabi.cmake");
            File.WriteAllText(toolchain, "set(CMAKE_SYSTEM_NAME Generic)\n");
            File.WriteAllText(Path.Combine(presets, "base.json"), """
                {"version":6,"configurePresets":[
                  {"name":"base","hidden":true,"binaryDir":"${sourceDir}/build/${presetName}",
                   "toolchainFile":"${fileDir}/../cmake/gcc-arm-none-eabi.cmake","generator":"Ninja",
                   "environment":{"OUTPUT":"${sourceDir}/user output"},"cacheVariables":{"CMAKE_BUILD_TYPE":"Debug","CMAKE_C_FLAGS":"-DSTM32H723xx"}},
                  {"name":"defaults","hidden":true,"binaryDir":"${sourceDir}/unused","cacheVariables":{"CMAKE_BUILD_TYPE":"Release"}},
                  {"name":"Release","inherits":"base","cacheVariables":{"CMAKE_BUILD_TYPE":"Release"}}
                ],"buildPresets":[{"name":"build-base","hidden":true,"configurePreset":"Release","configuration":"Release"},
                  {"name":"ReleaseBuild","inherits":"build-base"}]}
                """);
            File.WriteAllText(Path.Combine(root, "CMakePresets.json"), """
                {"version":6,"include":["presets/base.json"],"configurePresets":[
                  {"name":"Debug","displayName":"Debug firmware","inherits":["base","defaults"],"condition":{"type":"equals","lhs":"${hostSystemName}","rhs":"Windows"}}
                ],"buildPresets":[{"name":"DebugBuild","configurePreset":"Debug"}]}
                """);
            File.WriteAllText(Path.Combine(root, "CMakeUserPresets.json"), """
                {"version":6,"configurePresets":[{"name":"Local","inherits":"Debug","binaryDir":"$env{OUTPUT}/${presetName}"}]}
                """);
            var debug = ProjectInspector.Inspect(root);
            Assert(debug.Chip == "STM32H723VGT6" && debug.TargetScript == "target/stm32h7x.cfg", "IOC and compatible compile/startup evidence");
            Assert(debug.ChipCandidates.SequenceEqual(["STM32H723VGT6"]), "compatible clues produce one candidate");
            Assert(debug.ChipEvidence.Contains("board.ioc") && debug.ChipEvidence.Contains("编译缓存") && debug.ChipEvidence.Contains("启动"), "evidence identifies its sources");
            Assert(debug.ConfigurePresets.Count == 3 && debug.ConfigurePreset == "Debug", "visible includes and user presets");
            Assert(debug.ToolchainFile == toolchain && debug.BuildDirectory == Path.Combine(root, "build", "Debug") && debug.BuildPreset == "DebugBuild", "default Debug inherited paths");
            Assert(debug.ConfigurePresets.Single(p => p.Name == "Debug").BuildConfiguration == "Debug", "first parent wins in multiple inheritance");
            Assert(ProjectInspector.TargetForChip("STM32F407VGT6") == "target/stm32f4x.cfg", "manual chip confirmation mapping");
            var release = ProjectInspector.Inspect(root, "Release");
            Assert(release.ConfigurePreset == "Release" && release.BuildPreset == "ReleaseBuild" && release.BuildDirectory == Path.Combine(root, "build", "Release"), "explicit Release selection and inherited build preset");
            Assert(release.ConfigurePresets.Single(p => p.Name == "Release").BuildConfiguration == "Release", "Release configuration exposed");
            var local = ProjectInspector.Inspect(root, "Local");
            Assert(local.BuildDirectory == Path.Combine(root, "user output", "Local"), "inherited environment expansion");
            Directory.CreateDirectory(debug.BuildDirectory!);
            File.WriteAllText(Path.Combine(debug.BuildDirectory!, "compile_commands.json"), """
                [{"directory":".","file":"main.c","arguments":["arm-none-eabi-gcc","-DSTM32H723xx","-c","main.c"]}]
                """);
            Assert(ProjectInspector.Inspect(root).ChipEvidence.Contains("实际编译宏"), "selected output compile database evidence");

            var ambiguous = Path.Combine(root, "multiple boards");
            Directory.CreateDirectory(ambiguous);
            File.WriteAllText(Path.Combine(ambiguous, "CMakeLists.txt"), "project(multiboard C)\n");
            File.WriteAllText(Path.Combine(ambiguous, "first.ioc"), "Mcu.CPN=STM32F407VGT6\n");
            File.WriteAllText(Path.Combine(ambiguous, "second.ioc"), "Mcu.CPN=STM32H723VGT6\n");
            var boards = ProjectInspector.Inspect(ambiguous);
            Assert(boards.Chip == null && boards.TargetScript == null && boards.ChipCandidates.Count == 2 && boards.Notes.Any(n => n.Contains("冲突")), "multiple legitimate board targets remain explicit");

            var output = Path.Combine(root, "outputs");
            Directory.CreateDirectory(Path.Combine(output, "secondary"));
            File.WriteAllText(Path.Combine(output, "app.elf"), "ELF");
            Assert(CMakeBuildPlan.FindSingleElf(output) == Path.Combine(output, "app.elf"), "single ELF compatibility");
            File.WriteAllText(Path.Combine(output, "secondary", "loader.elf"), "ELF");
            Assert(CMakeBuildPlan.FindElfs(output).Count == 2, "normal multi-target ELF discovery");

            var options = new SetupOptions(root, "cmake", "ninja", "gcc", "openocd", "scripts", "target/stm32h7x.cfg", null, null, output)
                { BuildConfiguration = "Release" };
            var plan = CMakeBuildPlan.Create(options);
            Assert(plan.Configure.Arguments.Contains("-DCMAKE_BUILD_TYPE=Release") && plan.Build.Arguments.Contains("Release"), "manual Release build arguments");

            var cachePresetRoot = Path.Combine(root, "cache toolchain project");
            Directory.CreateDirectory(Path.Combine(cachePresetRoot, "cmake"));
            File.WriteAllText(Path.Combine(cachePresetRoot, "CMakeLists.txt"), "project(STM32F407xx C)\n");
            var cachedToolchain = Path.Combine(cachePresetRoot, "cmake", "arm-toolchain.cmake");
            File.WriteAllText(cachedToolchain, "set(CMAKE_SYSTEM_NAME Generic)\n");
            File.WriteAllText(Path.Combine(cachePresetRoot, "CMakeUserPresets.json"), """
                {"version":6,"configurePresets":[{"name":"Debug","binaryDir":"build/debug",
                  "cacheVariables":{"CMAKE_TOOLCHAIN_FILE":{"type":"FILEPATH","value":"cmake/arm-toolchain.cmake"}}}]}
                """);
            var cached = ProjectInspector.Inspect(cachePresetRoot);
            Assert(cached.ToolchainFile == cachedToolchain && cached.BuildDirectory == Path.Combine(cachePresetRoot, "build", "debug"), "user-only preset and typed toolchain cache variable");
            var legacy = new ProjectInfo(cachePresetRoot, true, "STM32F407XX", "target/stm32f4x.cfg", null, null, null, null, []);
            Assert(legacy.ConfigurePresets.Count == 0 && legacy.ChipCandidates.Count == 0 && legacy.ChipEvidence == "", "legacy positional ProjectInfo constructor");

            var sourceBuildRoot = Path.Combine(root, "source build presets");
            Directory.CreateDirectory(sourceBuildRoot);
            File.WriteAllText(Path.Combine(sourceBuildRoot, "CMakeLists.txt"), "cmake_minimum_required(VERSION 3.20)\nproject(STM32F407xx NONE)\n");
            File.WriteAllText(Path.Combine(sourceBuildRoot, "CMakePresets.json"), """
                {"version":3,"configurePresets":[{"name":"Debug","generator":"Ninja",
                  "environment":{"MODE":"Debug","OVERRIDE":"configured"}}],
                 "buildPresets":[
                  {"name":"base","hidden":true,"configurePreset":"Debug",
                   "environment":{"MODE":"Release","OVERRIDE":"inherited"}},
                  {"name":"DebugBuild","inherits":"base","configuration":"$env{MODE}",
                   "environment":{"OVERRIDE":"own"},
                   "condition":{"type":"allOf","conditions":[
                    {"type":"equals","lhs":"${generator}","rhs":"Ninja"},
                    {"type":"equals","lhs":"$env{OVERRIDE}","rhs":"own"}]}}
                 ]}
                """);
            var sourceBuild = ProjectInspector.Inspect(sourceBuildRoot);
            Assert(sourceBuild.BuildDirectory == sourceBuildRoot && !sourceBuild.Notes.Any(n => n.Contains("binaryDir")), "v3 omitted binaryDir uses source directory");
            var sourcePreset = sourceBuild.ConfigurePresets.Single();
            Assert(sourcePreset.BuildPreset == "DebugBuild" && sourcePreset.BuildConfiguration == "Debug", "build generator macro and environment precedence");
            var sourceOptions = options with { Root = sourceBuildRoot, ConfigurePreset = "Debug", BuildPreset = null, BuildDirectory = null, BuildConfiguration = "Debug" };
            Assert(CMakeBuildPlan.Create(sourceOptions).Build.Arguments.Contains(sourceBuildRoot), "preset build directory inferred when options omit it");
            var multiRoot = Path.Combine(root, "multi configuration"); Directory.CreateDirectory(multiRoot);
            File.WriteAllText(Path.Combine(multiRoot, "CMakeLists.txt"), "project(STM32H723xx C)\n");
            File.WriteAllText(Path.Combine(multiRoot, "CMakePresets.json"), """
                {"version":3,"configurePresets":[{"name":"Multi","generator":"Ninja Multi-Config","binaryDir":"build"}],
                 "buildPresets":[{"name":"DebugBuild","configurePreset":"Multi","configuration":"Debug"},
                  {"name":"ReleaseBuild","configurePreset":"Multi","configuration":"Release"}]}
                """);
            var multi = ProjectInspector.Inspect(multiRoot).ConfigurePresets.Single();
            Assert(multi.ConfigureConfiguration == null && multi.BuildPresets.Select(x => x.Configuration).SequenceEqual(["Debug", "Release"]), "all matching multi-config build presets exposed");
        }
        finally { Directory.Delete(root, true); }
    }

    private static void Assert(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException("Inspection check: " + name);
    }
}
