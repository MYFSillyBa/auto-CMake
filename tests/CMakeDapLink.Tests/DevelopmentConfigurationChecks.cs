using System.Reflection;
using System.Text;
using System.Text.Json;
using CMakeDapLink.Core;

internal static class DevelopmentConfigurationChecks
{
    public static void Run(string parent, Action<string, Action> check)
    {
        check("clangd preview preserves YAML comments and JSONC user arguments without writing", () =>
        {
            var options = Fixture(parent);
            var yaml = "# project comment\r\nCompileFlags:\r\n  Add: [-DUSE_BOARD] # user flags\r\n  CompilationDatabase: old # db comment\r\nDiagnostics:\r\n  UnusedIncludes: Strict\r\n";
            var settings = "{\r\n // keep settings\r\n \"clangd.arguments\": [\"--header-insertion=never\", /* keep argument */ \"--compile-commands-dir=old\", \"--query-driver=old-gcc\"],\r\n \"editor.tabSize\": 8,\r\n}\r\n";
            File.WriteAllText(Path.Combine(options.Root, ".clangd"), yaml);
            File.WriteAllText(Path.Combine(options.Root, ".vscode", "settings.json"), settings);
            var changes = Call("PreviewClangd", options);
            var afterYaml = Text(changes.Single(x => x.Path.EndsWith(".clangd", StringComparison.Ordinal)));
            var afterSettings = Text(changes.Single(x => x.Path.EndsWith("settings.json", StringComparison.Ordinal)));
            Assert(afterYaml.Contains("# project comment\r\n") && afterYaml.Contains("Add: [-DUSE_BOARD] # user flags") && afterYaml.Contains("# db comment"), "YAML comments and flags are retained");
            Assert(afterSettings.Contains("// keep settings") && afterSettings.Contains("/* keep argument */") && afterSettings.Contains("\"editor.tabSize\": 8"), "JSONC comments and unrelated values retain their text");
            using var parsed = JsonDocument.Parse(afterSettings, new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            var arguments = parsed.RootElement.GetProperty("clangd.arguments").EnumerateArray().Select(x => x.GetString()!).ToArray();
            Assert(arguments.Contains("--header-insertion=never") && arguments.Count(x => x.StartsWith("--compile-commands-dir=")) == 1, "user clangd flags survive and stale database flag is replaced");
            Assert(arguments.Any(x => x.Contains(options.Compiler.Replace('\\', '/'))) && parsed.RootElement.GetProperty("C_Cpp.intelliSenseEngine").GetString() == "disabled", "selected compiler query driver and IntelliSense setting are explicit");
            Assert(File.ReadAllText(Path.Combine(options.Root, ".clangd")) == yaml && File.ReadAllText(Path.Combine(options.Root, ".vscode", "settings.json")) == settings, "preview does not mutate files");
        });
        check("debug preview updates only marked entry, preserves comments, and is stable", () =>
        {
            var options = Fixture(parent);
            var launch = "{\n // launch comment\n \"version\": \"0.2.0\",\n \"configurations\": [\n  {\"name\":\"STM32 Project Helper: Debug\",\"type\":\"cortex-debug\",\"request\":\"launch\",\"custom\":true}, // user config\n ],\n \"compounds\": [],\n}";
            var path = Path.Combine(options.Root, ".vscode", "launch.json");
            File.WriteAllText(path, launch);
            var first = Call("PreviewDebug", options, Path.Combine(Path.GetDirectoryName(options.Compiler)!, "arm-none-eabi-gdb.exe"));
            var text = Text(DebugChange(first));
            Assert(text.Contains("{\"name\":\"STM32 Project Helper: Debug\",\"type\":\"cortex-debug\",\"request\":\"launch\",\"custom\":true}") && text.Contains("// user config"), "same-name user configuration remains byte-for-byte");
            Assert(text.Contains("// launch comment") && text.Contains("\"compounds\": []"), "root comments and other fields remain");
            using (var parsed = JsonDocument.Parse(text, new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }))
            {
                var entries = parsed.RootElement.GetProperty("configurations").EnumerateArray().ToArray();
                Assert(entries.Length == 2 && entries[1].GetProperty("request").GetString() == "launch", "normal debugging launches and programs the selected firmware");
                Assert(Path.IsPathFullyQualified(entries[1].GetProperty("gdbPath").GetString()!) && Path.IsPathFullyQualified(entries[1].GetProperty("serverpath").GetString()!), "tool paths are absolute");
            }
            var managedText = text.Replace("\"adapter speed 1000\"", "/* speed comment */ \"adapter speed 1000\"");
            File.WriteAllText(path, managedText);
            foreach (var change in first.Where(x => !x.Path.EndsWith("launch.json"))) File.WriteAllBytes(change.Path, change.After!);
            var changed = Text(DebugChange(Call("PreviewDebug", options with { AdapterSpeed = 2000 }, Path.Combine(Path.GetDirectoryName(options.Compiler)!, "arm-none-eabi-gdb.exe"))));
            Assert(changed.Contains("/* speed comment */") && changed.Contains("adapter speed 2000"), "managed property edits retain nested JSONC comments");
            File.WriteAllText(path, changed);
            Assert(Call("PreviewDebug", options with { AdapterSpeed = 2000 }, Path.Combine(Path.GetDirectoryName(options.Compiler)!, "arm-none-eabi-gdb.exe")).Count == 0, "second identical preview has no changes");
        });
        check("normal debugging binds a real build dependency and refreshes the selected ELF", () =>
        {
            var options = Fixture(parent) with { TaskLanguage = "en", BuildConfiguration = "Profile" };
            var tasksPath = Path.Combine(options.Root, ".vscode", "tasks.json");
            File.WriteAllText(tasksPath, "{\"tasks\":[{\"label\":\"STM32 Project Helper: Build for Debug\",\"type\":\"shell\",\"command\":\"echo user\"}]}");
            var launchPath = Path.Combine(options.Root, ".vscode", "launch.json");
            File.WriteAllText(launchPath, "{\"configurations\":[{\"name\":\"old\",\"type\":\"cortex-debug\",\"servertype\":\"openocd\",\"request\":\"attach\",\"loadFiles\":[],\"presentation\":{\"group\":\"STM32 Project Helper (managed)\"}}]}");
            var changes = DevelopmentConfiguration.PreviewDebug(options, Path.Combine(Path.GetDirectoryName(options.Compiler)!, "arm-none-eabi-gdb.exe"));
            var config = DevelopmentConfiguration.Configuration(DebugChange(changes).After!);
            Assert(config.GetProperty("request").GetString() == "launch", "stale attach configuration is migrated to launch");
            Assert(config.GetProperty("loadFiles")[0].GetString() == Path.GetFullPath(options.FirmwareElfPath!), "empty loadFiles cannot silently suppress the selected ELF download");
            var buildLabel = config.GetProperty("preLaunchTask").GetString();
            using var tasks = JsonDocument.Parse(changes.Single(x => x.Path == tasksPath).After!);
            var build = tasks.RootElement.GetProperty("tasks").EnumerateArray().Single(x => x.GetProperty("label").GetString() == buildLabel);
            Assert(buildLabel != "STM32 Project Helper: Build for Debug" && build.GetProperty("type").GetString() == "process", "debugging depends on the managed build, never a same-name user task");
            var configureLabel = build.GetProperty("dependsOn").GetString();
            var configure = tasks.RootElement.GetProperty("tasks").EnumerateArray().Single(x => x.GetProperty("label").GetString() == configureLabel);
            var runner = configure.GetProperty("args").EnumerateArray().Last().GetString();
            Assert(changes.Any(x => x.Path == runner && Text(x).Contains("Profile")), "prelaunch configure runner is supplied with the selected custom configuration");
            Assert(File.ReadAllText(launchPath).Contains("\"request\":\"attach\"") && !File.Exists(runner), "preview never writes or runs the build");
        });
        check("attach-only is explicit and never schedules a build or a firmware download", () =>
        {
            var options = Fixture(parent);
            var property = typeof(SetupOptions).GetProperty("DebugMode") ?? throw new Exception("DebugMode selection is missing");
            var gdb = Path.Combine(Path.GetDirectoryName(options.Compiler)!, "arm-none-eabi-gdb.exe");
            var launchChanges = DevelopmentConfiguration.PreviewDebug(options, gdb);
            foreach (var change in launchChanges) File.WriteAllBytes(change.Path, change.After!);
            property.SetValue(options, "attach");
            var attach = DevelopmentConfiguration.PreviewDebug(options, gdb);
            var config = DevelopmentConfiguration.Configuration(DebugChange(attach).After!);
            Assert(config.GetProperty("request").GetString() == "attach", "attach-only request is preserved");
            Assert(config.GetProperty("preLaunchTask").GetString() == "" && config.GetProperty("loadFiles").GetArrayLength() == 0, "switching from launch clears build and download actions");
            Assert(attach.All(x => x.Path.EndsWith("launch.json")), "attach does not rewrite tasks or build helpers");
        });
        check("custom launch overrides cannot silently bypass firmware loading", () =>
        {
            var options = Fixture(parent);
            var path = Path.Combine(options.Root, ".vscode", "launch.json");
            var before = "{\"configurations\":[{\"name\":\"old\",\"type\":\"cortex-debug\",\"servertype\":\"openocd\",\"request\":\"attach\",\"overrideLaunchCommands\":[],\"presentation\":{\"group\":\"STM32 Project Helper (managed)\"}}]}";
            File.WriteAllText(path, before);
            bool rejected = false;
            try { DevelopmentConfiguration.PreviewDebug(options, Path.Combine(Path.GetDirectoryName(options.Compiler)!, "arm-none-eabi-gdb.exe")); }
            catch (InvalidDataException error) { rejected = error.Message.Contains("overrideLaunchCommands"); }
            Assert(rejected && File.ReadAllText(path) == before, "an empty override array suppresses Cortex-Debug defaults and must be reviewed explicitly");
        });
        check("debug builds do not retarget existing build and flash tasks", () =>
        {
            var options = Fixture(parent);
            foreach (var change in ConfigurationWriter.Preview(options)) File.WriteAllBytes(change.Path, change.After!);
            var tasksPath = Path.Combine(options.Root, ".vscode", "tasks.json");
            using var before = JsonDocument.Parse(File.ReadAllBytes(tasksPath));
            var changes = DevelopmentConfiguration.PreviewDebug(options with { BuildConfiguration = "Profile" }, Path.Combine(Path.GetDirectoryName(options.Compiler)!, "arm-none-eabi-gdb.exe"));
            using var after = JsonDocument.Parse(changes.Single(x => x.Path == tasksPath).After!);
            var next = after.RootElement.GetProperty("tasks").EnumerateArray().ToArray();
            foreach (var task in before.RootElement.GetProperty("tasks").EnumerateArray())
                Assert(next.Single(x => x.GetProperty("label").GetString() == task.GetProperty("label").GetString()).GetRawText() == task.GetRawText(), "existing build/flash command and its dependencies remain unchanged");
            var config = DevelopmentConfiguration.Configuration(DebugChange(changes).After!);
            var debugTask = next.Single(x => x.GetProperty("label").GetString() == config.GetProperty("preLaunchTask").GetString());
            Assert(!debugTask.TryGetProperty("group", out var group) || !group.TryGetProperty("isDefault", out var flag) || !flag.GetBoolean(), "debug build does not replace the default build task");
            Assert(!changes.Any(x => x.Path.EndsWith("stm32-daplink.cmake")), "existing flash runner is not rebound to another configuration");
            foreach (var change in changes) File.WriteAllBytes(change.Path, change.After!);
            foreach (var change in ConfigurationWriter.Preview(options)) File.WriteAllBytes(change.Path, change.After!);
            using var regenerated = JsonDocument.Parse(File.ReadAllBytes(tasksPath));
            Assert(regenerated.RootElement.GetProperty("tasks").EnumerateArray().Any(x => x.GetProperty("label").GetString() == config.GetProperty("preLaunchTask").GetString()), "writing normal tasks later must not remove the debug build dependency");
        });
        check("probe evidence cannot confuse script configuration with detected hardware", () =>
        {
            var type = typeof(SetupOptions).Assembly.GetType("CMakeDapLink.Core.DevelopmentHardware") ?? throw new Exception("DevelopmentHardware is missing");
            var method = type.GetMethod("ParseProbeCheck")!;
            var scriptsOnly = method.Invoke(null, new object[] { "Info : transport select swd\nInfo : adapter speed: 1000 kHz\n", 0 })!;
            Assert(!(bool)scriptsOnly.GetType().GetProperty("ProbeFound")!.GetValue(scriptsOnly)!, "scripts and speed output are not USB evidence");
            var connected = method.Invoke(null, new object[] { "Info : CMSIS-DAP: SWD supported\nInfo : SWD DPIDR 0x2ba01477\nSTM32_HELPER_CHIP_CONNECTED=1\n", 0 })!;
            Assert((bool)connected.GetType().GetProperty("ProbeFound")!.GetValue(connected)! && (bool)connected.GetType().GetProperty("ChipConnected")!.GetValue(connected)!, "live driver and target markers establish separate evidence");
        });
        check("RTOS snapshots retain unknown priority and stack values without inventing measurements", () =>
        {
            var snapshot = DevelopmentHardware.ParseRtos("STM32_HELPER_RTOS={\"tasks\":[{\"name\":\"Idle\",\"state\":\"ready\",\"priority\":null,\"stackHighWater\":null}],\"notes\":[],\"freeRtosAvailable\":false,\"detached\":true}");
            Assert(snapshot.Tasks.Count == 1 && snapshot.Tasks[0].Priority == null && snapshot.Tasks[0].StackHighWater == null && snapshot.Detached, "unknown FreeRTOS data stays null");
            Assert(DevelopmentHardware.LocalEndpoint("localhost", 3333) == "127.0.0.1:3333", "localhost is canonicalized");
            bool rejected = false;
            try { DevelopmentHardware.LocalEndpoint("example.com", 3333); } catch (ArgumentException) { rejected = true; }
            Assert(rejected, "remote endpoints are refused before running GDB");
        });
        check("debug preview honors the selected name and protects a same-name user configuration", () =>
        {
            var options = Fixture(parent);
            var path = Path.Combine(options.Root, ".vscode", "launch.json");
            File.WriteAllText(path, "{\"configurations\":[{\"name\":\"Board Debug\",\"type\":\"cppdbg\",\"request\":\"launch\"}]}");
            var changes = Call("PreviewDebugNamed", options, Path.Combine(Path.GetDirectoryName(options.Compiler)!, "arm-none-eabi-gdb.exe"), "Board Debug");
            Assert(DevelopmentConfiguration.ConfigurationName(DebugChange(changes).After!) == "Board Debug (2)", "selected debug name gains a suffix when a user config already uses it");
            foreach (var change in changes) File.WriteAllBytes(change.Path, change.After!);
            var renamed = Call("PreviewDebugNamed", options, Path.Combine(Path.GetDirectoryName(options.Compiler)!, "arm-none-eabi-gdb.exe"), "Other Board");
            Assert(DevelopmentConfiguration.ConfigurationName(DebugChange(renamed).After!) == "Other Board", "managed marker permits safe name changes");
        });
    }

    private static IReadOnlyList<FileChange> Call(string name, params object[] values)
    {
        var type = typeof(SetupOptions).Assembly.GetType("CMakeDapLink.Core.DevelopmentConfiguration") ?? throw new Exception("DevelopmentConfiguration is missing");
        try { return (IReadOnlyList<FileChange>)(type.GetMethod(name) ?? throw new Exception(name + " is missing")).Invoke(null, values)!; }
        catch (TargetInvocationException ex) { throw ex.InnerException!; }
    }
    private static string Text(FileChange change) => Encoding.UTF8.GetString(change.After!);
    private static FileChange DebugChange(IReadOnlyList<FileChange> changes) => changes.Single(x => x.Path.EndsWith("launch.json", StringComparison.OrdinalIgnoreCase));
    private static SetupOptions Fixture(string parent)
    {
        var root = Path.Combine(parent, "development-" + Guid.NewGuid().ToString("N"));
        var tools = Path.Combine(root, "tools");
        Directory.CreateDirectory(tools);
        Directory.CreateDirectory(Path.Combine(root, ".vscode"));
        Directory.CreateDirectory(Path.Combine(tools, "scripts", "interface"));
        Directory.CreateDirectory(Path.Combine(tools, "scripts", "target"));
        Directory.CreateDirectory(Path.Combine(root, "build"));
        foreach (var file in new[] { "cmake.exe", "ninja.exe", "arm-none-eabi-gcc.exe", "arm-none-eabi-gdb.exe", "openocd.exe" }) File.WriteAllText(Path.Combine(tools, file), "");
        File.WriteAllText(Path.Combine(root, "CMakeLists.txt"), "project(test C)\n");
        File.WriteAllText(Path.Combine(root, "build", "firmware.elf"), "fixture");
        File.WriteAllText(Path.Combine(tools, "scripts", "interface", "cmsis-dap.cfg"), "adapter driver cmsis-dap\n");
        File.WriteAllText(Path.Combine(tools, "scripts", "target", "stm32f4x.cfg"), "# test fixture\n");
        return new(root, Path.Combine(tools, "cmake.exe"), Path.Combine(tools, "ninja.exe"), Path.Combine(tools, "arm-none-eabi-gcc.exe"), Path.Combine(tools, "openocd.exe"), Path.Combine(tools, "scripts"), "target/stm32f4x.cfg", null, null, Path.Combine(root, "build"), FirmwareElfPath: Path.Combine(root, "build", "firmware.elf"));
    }
    private static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
}
