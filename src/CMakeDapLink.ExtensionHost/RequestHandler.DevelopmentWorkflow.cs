using System.Text.Json;
using CMakeDapLink.Core;

namespace CMakeDapLink.ExtensionHost;

internal sealed partial class RequestHandler
{
    private async Task<object> PrepareCompileDatabaseAsync(JsonElement payload, CancellationToken cancellation)
    {
        if (!Flag(payload, "explicitConfigure")) throw new ArgumentException("生成编译数据库前需要确认运行 CMake 配置。");
        var options = await OptionsAsync(payload, cancellation);
        var plan = BuildPlan(options);
        plan.PrepareArtifactQuery();
        progress(25, "生成编译数据库", plan.BuildDirectory);
        var result = await HostProcess.RunAsync(plan.Configure, TimeSpan.FromMinutes(5), cancellation);
        var database = WorkspaceSafety.Child(root, Path.Combine(plan.BuildDirectory, "compile_commands.json"));
        return new { success = result.ExitCode == 0 && File.Exists(database), compileCommands = database,
            output = result.Output, notes = File.Exists(database) ? Array.Empty<string>() : new[] { "CMake 未生成编译数据库，请检查生成器、工具链或配置错误。" } };
    }
}
