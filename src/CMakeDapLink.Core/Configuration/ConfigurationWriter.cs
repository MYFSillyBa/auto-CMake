using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CMakeDapLink.Core;

public sealed record SetupOptions(string Root, string CMake, string Ninja, string Compiler, string OpenOcd,
    string Scripts, string TargetScript, string? ConfigurePreset, string? BuildPreset, string? BuildDirectory,
    string? ToolchainFile = null, string? FirmwareElfPath = null);

public static class ConfigurationWriter
{
    private const string ConfigureLabel = "CMake 配置（自动）";
    private const string BuildLabel = "一键编译";
    private const string FlashLabel = "一键烧录(DAPLINK)";

    public static void Write(SetupOptions options)
    {
        if (!Regex.IsMatch(options.TargetScript, @"\Atarget/[a-zA-Z0-9_.-]+\.cfg\z"))
            throw new ArgumentException("OpenOCD target 必须是 target/xxx.cfg。", nameof(options));
        var root = Path.GetFullPath(options.Root);
        if (!File.Exists(Path.Combine(root, "CMakeLists.txt"))) throw new ArgumentException("缺少 CMakeLists.txt。", nameof(options));
        if (options.ConfigurePreset != null && options.BuildDirectory == null)
            throw new ArgumentException("预设没有可用构建目录。", nameof(options));
        if (options.FirmwareElfPath == null || !File.Exists(options.FirmwareElfPath))
            throw new ArgumentException("请先成功编译并确认唯一的 ELF 文件。", nameof(options));
        var elf = Path.GetFullPath(options.FirmwareElfPath);
        var buildDirectory = Path.GetFullPath(options.BuildDirectory ?? Path.Combine(root, "build", "daplink-debug"));
        if (!IsInside(buildDirectory, elf)) throw new ArgumentException("ELF 不在当前构建目录内。", nameof(options));

        var vscode = Path.Combine(root, ".vscode");
        var tasksPath = Path.Combine(vscode, "tasks.json");
        JsonObject tasks;
        if (File.Exists(tasksPath))
        {
            tasks = JsonNode.Parse(File.ReadAllText(tasksPath), nodeOptions: null,
                documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject
                ?? throw new InvalidDataException("现有 .vscode/tasks.json 不是 JSON 对象。");
        }
        else tasks = new JsonObject { ["version"] = "2.0.0" };
        if (tasks["tasks"] is not null && tasks["tasks"] is not JsonArray)
            throw new InvalidDataException("现有 tasks.json 的 tasks 不是数组。");
        var preserved = new JsonArray();
        foreach (var task in tasks["tasks"] as JsonArray ?? new JsonArray())
        {
            var label = task is JsonObject obj ? obj["label"]?.GetValue<string>() : null;
            if (label is not (ConfigureLabel or BuildLabel or FlashLabel or "一键启动（DAPLINK）" or "一键启动(DAPLINK)"))
                preserved.Add(task?.DeepClone());
        }

        var plan = CMakeBuildPlan.Create(options);
        preserved.Add(MakeProcessTask(ConfigureLabel, plan.Configure, hidden: true));
        var build = MakeProcessTask(BuildLabel, plan.Build);
        build["dependsOn"] = ConfigureLabel;
        build["dependsOrder"] = "sequence";
        build["group"] = new JsonObject { ["kind"] = "build", ["isDefault"] = true };
        build["problemMatcher"] = new JsonArray("$gcc");
        preserved.Add(build);

        var flashArgs = new List<string> { "-s", options.Scripts, "-f", "interface/cmsis-dap.cfg", "-c", "transport select swd",
            "-f", options.TargetScript, "-c", "adapter speed 1000; program {" + TclPath(elf) + "} verify reset exit" };
        var flash = MakeProcessTask(FlashLabel, new(options.OpenOcd, flashArgs, root, plan.Configure.PathPrefix));
        flash["dependsOn"] = BuildLabel;
        flash["dependsOrder"] = "sequence";
        preserved.Add(flash);
        tasks["tasks"] = preserved;
        Directory.CreateDirectory(vscode);
        Save(tasksPath, tasks.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        RemoveLegacyHelpers(vscode);
    }

    private static JsonObject MakeProcessTask(string label, ToolCommand command, bool hidden = false)
    {
        var args = new JsonArray();
        foreach (var argument in command.Arguments) args.Add(argument);
        var envPath = string.IsNullOrEmpty(command.PathPrefix) ? "${env:PATH}" : command.PathPrefix + Path.PathSeparator + "${env:PATH}";
        var task = new JsonObject
        {
            ["label"] = label, ["type"] = "process", ["command"] = command.Executable, ["args"] = args,
            ["options"] = new JsonObject { ["cwd"] = "${workspaceFolder}", ["env"] = new JsonObject { ["PATH"] = envPath } },
            ["presentation"] = new JsonObject { ["reveal"] = "always", ["panel"] = "shared", ["clear"] = true },
            ["problemMatcher"] = new JsonArray()
        };
        if (hidden) task["hide"] = true;
        return task;
    }

    private static string TclPath(string path) => path.Replace('\\', '/').Replace("{", "\\{").Replace("}", "\\}");
    private static bool IsInside(string directory, string path) =>
        path.StartsWith(directory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static void RemoveLegacyHelpers(string vscode)
    {
        var script = Path.Combine(vscode, "cmake-daplink.ps1");
        if (!File.Exists(script)) return;
        var content = File.ReadAllText(script);
        if (!content.StartsWith("param([ValidateSet('build','flash')][string]$Action = 'build')", StringComparison.Ordinal)) return;
        File.Delete(script);
        var config = Path.Combine(vscode, "cmake-daplink.json");
        if (!File.Exists(config)) return;
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(config));
            if (json.RootElement.TryGetProperty("cmake", out _) && json.RootElement.TryGetProperty("openocd", out _)) File.Delete(config);
        }
        catch (JsonException) { }
    }

    private static void Save(string path, string contents)
    {
        if (File.Exists(path))
        {
            if (File.ReadAllText(path) == contents) return;
            File.Copy(path, path + ".bak-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"), false);
        }
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(temp, contents, new UTF8Encoding(false));
        File.Move(temp, path, true);
    }
}
