using System.Text;
using CMakeDapLink.Core;

namespace CMakeDapLink.ExtensionHost;

internal static class TaskPreview
{
    private const string Marker = "# STM32 Project Helper managed task runner v1\n";

    public static IReadOnlyList<FileChange> Create(SetupOptions options, CMakeBuildPlan effectivePlan)
    {
        var originalPlan = CMakeBuildPlan.Create(options);
        var changes = ConfigurationWriter.Preview(options).ToList();
        if (originalPlan.Configure.Arguments.SequenceEqual(effectivePlan.Configure.Arguments)) return changes;
        var originalCommand = ConfigureCommand(originalPlan.Configure);
        var effectiveCommand = ConfigureCommand(effectivePlan.Configure);
        var replaced = false;
        for (var index = 0; index < changes.Count; index++)
        {
            var change = changes[index];
            if (change.After == null || !Path.GetExtension(change.Path).Equals(".cmake", StringComparison.OrdinalIgnoreCase)) continue;
            var content = Encoding.UTF8.GetString(change.After);
            if (!content.StartsWith(Marker, StringComparison.Ordinal)) continue;
            if (!content.Contains(originalCommand, StringComparison.Ordinal))
                throw new InvalidDataException("任务运行器的配置格式已变化，请更新扩展宿主。");
            changes[index] = change with { After = new UTF8Encoding(false).GetBytes(content.Replace(originalCommand, effectiveCommand, StringComparison.Ordinal)) };
            replaced = true;
        }
        if (!replaced)
        {
            // Core may omit an unchanged runner; its current bytes still need the effective arguments.
            var vscode = Path.Combine(options.Root, ".vscode");
            if (Directory.Exists(vscode))
                foreach (var path in Directory.EnumerateFiles(vscode, "stm32-daplink*.cmake"))
                {
                    WorkspaceSafety.Child(options.Root, path);
                    var before = File.ReadAllBytes(path);
                    var content = Encoding.UTF8.GetString(before);
                    if (!content.StartsWith(Marker, StringComparison.Ordinal) || !content.Contains(originalCommand, StringComparison.Ordinal)) continue;
                    changes.Add(new(path, before, new UTF8Encoding(false).GetBytes(content.Replace(originalCommand, effectiveCommand, StringComparison.Ordinal))));
                    replaced = true;
                    break;
                }
        }
        if (!replaced) throw new InvalidDataException("未找到当前托管任务运行器，请重新预览工程任务。");
        return changes.Where(x => x.Before == null || x.After == null || !x.Before.AsSpan().SequenceEqual(x.After)).ToArray();
    }

    private static string ConfigureCommand(ToolCommand command) =>
        "  execute_process(COMMAND " + Quote(command.Executable) + " " + string.Join(" ", command.Arguments.Select(Quote)) + "\n";

    private static string Quote(string text)
    {
        var delimiter = "=";
        while (text.Contains("]" + delimiter + "]", StringComparison.Ordinal)) delimiter += "=";
        return "[" + delimiter + "[" + text.Replace('\\', '/') + "]" + delimiter + "]";
    }
}
