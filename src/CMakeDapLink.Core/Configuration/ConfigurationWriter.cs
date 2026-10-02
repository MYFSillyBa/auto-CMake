using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CMakeDapLink.Core;

public sealed record SetupOptions(string Root, string CMake, string Ninja, string Compiler, string OpenOcd,
    string Scripts, string TargetScript, string? ConfigurePreset, string? BuildPreset, string? BuildDirectory,
    string? ToolchainFile = null, string? FirmwareElfPath = null)
{
    public string BuildConfiguration { get; init; } = "Debug";
}

public static class ConfigurationWriter
{
    private const string ConfigureLabel = "CMake 配置（自动）";
    private const string BuildLabel = "一键编译";
    private const string FlashLabel = "一键烧录(DAPLINK)";

    public static void Write(SetupOptions options) => ChangeHistory.Apply(options.Root, "配置 VS Code 编译与烧录任务", Preview(options));

    public static IReadOnlyList<FileChange> Preview(SetupOptions options)
    {
        if (!Regex.IsMatch(options.TargetScript, @"\Atarget/[a-zA-Z0-9_.-]+\.cfg\z"))
            throw new ArgumentException("OpenOCD target 必须是 target/xxx.cfg。", nameof(options));
        var root = Path.GetFullPath(options.Root);
        if (!File.Exists(Path.Combine(root, "CMakeLists.txt"))) throw new ArgumentException("缺少 CMakeLists.txt。", nameof(options));
        if (options.ConfigurePreset != null && options.BuildDirectory == null)
            throw new ArgumentException("预设没有可用构建目录。", nameof(options));
        var plan = CMakeBuildPlan.Create(options);
        var elf = options.FirmwareElfPath == null ? null : Path.GetFullPath(options.FirmwareElfPath);
        if (elf != null && (!File.Exists(elf) || !IsInside(plan.BuildDirectory, elf)))
            throw new ArgumentException("所选 ELF 不存在或不在当前构建目录内。", nameof(options));

        var vscode = Path.Combine(root, ".vscode");
        var tasksPath = Path.Combine(vscode, "tasks.json");
        var before = File.Exists(tasksPath) ? File.ReadAllBytes(tasksPath) : null;
        var userLabels = JsoncTaskEditor.UserLabels(before);
        string AvailableLabel(string label)
        {
            if (!userLabels.Contains(label)) return label;
            var candidate = label + " · CMake DAPLink";
            for (var suffix = 2; userLabels.Contains(candidate); suffix++) candidate = label + " · CMake DAPLink " + suffix;
            return candidate;
        }
        var configureLabel = AvailableLabel(ConfigureLabel);
        var buildLabel = AvailableLabel(BuildLabel);
        var flashLabel = AvailableLabel(FlashLabel);
        var managed = new List<JsonObject>();
        var runnerPath = DapLinkTaskScript.AvailablePath(root);
        managed.Add(MakeProcessTask(configureLabel, plan.Configure with { Arguments = ["-DACTION=configure", "-P", runnerPath] }, hidden: true));
        var build = MakeProcessTask(buildLabel, plan.Build);
        build["dependsOn"] = configureLabel;
        build["dependsOrder"] = "sequence";
        build["group"] = new JsonObject { ["kind"] = "build", ["isDefault"] = true };
        build["problemMatcher"] = new JsonArray("$gcc");
        managed.Add(build);

        var flashArgs = new List<string> { "-DACTION=flash" };
        if (elf != null) flashArgs.Add("-DFIRMWARE_ELF=" + elf);
        flashArgs.AddRange(["-P", runnerPath]);
        var flash = MakeProcessTask(flashLabel, new(options.CMake, flashArgs, root, plan.Configure.PathPrefix));
        flash["dependsOn"] = buildLabel;
        flash["dependsOrder"] = "sequence";
        managed.Add(flash);
        var after = JsoncTaskEditor.Update(before, managed);
        var changes = new List<FileChange>();
        if (before == null || !before.AsSpan().SequenceEqual(after)) changes.Add(new(tasksPath, before, after));
        var runnerBefore = File.Exists(runnerPath) ? File.ReadAllBytes(runnerPath) : null;
        var runnerAfter = DapLinkTaskScript.Create(options, plan);
        if (runnerBefore == null || !runnerBefore.AsSpan().SequenceEqual(runnerAfter)) changes.Add(new(runnerPath, runnerBefore, runnerAfter));
        AddLegacyRemoval(vscode, changes);
        return changes;
    }

    private static JsonObject MakeProcessTask(string label, ToolCommand command, bool hidden = false)
    {
        var args = new JsonArray();
        foreach (var argument in command.Arguments) args.Add(argument);
        var envPath = string.IsNullOrEmpty(command.PathPrefix) ? "${env:PATH}" : command.PathPrefix + Path.PathSeparator + "${env:PATH}";
        var task = new JsonObject
        {
            ["label"] = label, ["detail"] = JsoncTaskEditor.ManagedDetail, ["type"] = "process", ["command"] = command.Executable, ["args"] = args,
            ["options"] = new JsonObject { ["cwd"] = "${workspaceFolder}", ["env"] = new JsonObject { ["PATH"] = envPath } },
            ["presentation"] = new JsonObject { ["reveal"] = "always", ["panel"] = "shared", ["clear"] = true },
            ["problemMatcher"] = new JsonArray()
        };
        if (hidden) task["hide"] = true;
        return task;
    }

    private static bool IsInside(string directory, string path) =>
        path.StartsWith(directory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static void AddLegacyRemoval(string vscode, List<FileChange> changes)
    {
        var script = Path.Combine(vscode, "cmake-daplink.ps1");
        if (!File.Exists(script)) return;
        var content = File.ReadAllText(script);
        if (!content.StartsWith("param([ValidateSet('build','flash')][string]$Action = 'build')", StringComparison.Ordinal)) return;
        changes.Add(new(script, File.ReadAllBytes(script), null));
        var config = Path.Combine(vscode, "cmake-daplink.json");
        if (!File.Exists(config)) return;
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(config));
            if (json.RootElement.TryGetProperty("cmake", out _) && json.RootElement.TryGetProperty("openocd", out _))
                changes.Add(new(config, File.ReadAllBytes(config), null));
        }
        catch (JsonException) { }
    }
}
