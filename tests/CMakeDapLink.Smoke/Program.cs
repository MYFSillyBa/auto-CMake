using CMakeDapLink.Core;

using var installer = new ToolRepairInstaller();
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
var current = args.Contains("--fresh-install") ? new ToolPaths(null, null, null, null, null) : EnvironmentScanner.Scan();
if (args.Contains("--refresh-catalog")) current = new(null, null, ManagedTools.LoadPaths().Compiler, null, null);
var result = await installer.RepairAsync(current, [], "target/stm32h7x.cfg", new ConsoleProgress(), cancellation.Token);
foreach (var problem in result.Problems) Console.WriteLine("待补全：" + problem);
Console.WriteLine("RESULT " + System.Text.Json.JsonSerializer.Serialize(result));
return result.Problems.Count == 0 ? 0 : 1;

sealed class ConsoleProgress : IProgress<ToolRepairProgress>
{
    private string _lastStage = ""; private DateTime _last = DateTime.MinValue;
    public void Report(ToolRepairProgress progress)
    {
        if (progress.Message == null && _lastStage == progress.Stage && DateTime.UtcNow - _last < TimeSpan.FromSeconds(3)) return;
        if (progress.Message == null && progress.Stage.StartsWith("下载 ") && DateTime.UtcNow - _last < TimeSpan.FromSeconds(3)) return;
        _lastStage = progress.Stage; _last = DateTime.UtcNow;
        Console.WriteLine($"[{progress.Percent,3}%] {progress.Stage}" + (progress.Message == null ? "" : "\n" + progress.Message));
    }
}
