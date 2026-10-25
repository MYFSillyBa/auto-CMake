using System.Text.Json;
using CMakeDapLink.Core;

namespace CMakeDapLink.ExtensionHost;

internal sealed partial class RequestHandler
{
    private async Task<object> PreviewFreeRtosRepairAsync(JsonElement payload, CancellationToken cancellation)
    {
        var sourceProject = Text(payload, "sourceProject");
        if (sourceProject != null) sourceProject = WorkspaceSafety.Child(root, sourceProject);
        var plan = await FreeRtosPortRepair.PreviewAsync(new(root, Direction(payload), Required(payload, "target"), sourceProject),
            Text(payload, "localDirectory"), new ImmediateProgress<FreeRtosRepairProgress>(item => progress(item.Percent, item.Stage, null)), cancellation).ConfigureAwait(false);
        WorkspaceSafety.Changes(root, plan.Changes);
        return new { plan.Changes, label = "补全 FreeRTOS GNU 移植层", canonicalLabel = "补全 FreeRTOS GNU 移植层", plan.Summary, plan.Notes, repairPlan = plan };
    }
    private object ApplyFreeRtosRepair(JsonElement payload, CancellationToken cancellation)
    {
        var plan = Read<FreeRtosRepairPlan>(payload, "plan");
        if (plan.Request == null || plan.Inputs == null || plan.Changes == null ||
            !Path.TrimEndingDirectorySeparator(Path.GetFullPath(plan.Request.Root)).Equals(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("FreeRTOS 补全计划与当前工程不匹配，请重新预览。");
        if (plan.Request.SourceProject != null) WorkspaceSafety.Child(root, plan.Request.SourceProject);
        foreach (var input in plan.Inputs) WorkspaceSafety.Child(root, input.Path);
        WorkspaceSafety.Changes(root, plan.Changes);
        cancellation.ThrowIfCancellationRequested();
        var record = FreeRtosPortRepair.Apply(plan);
        return new { record, changedFiles = record?.Changes.Select(x => WorkspaceSafety.Child(root, x.Path)).ToArray() ?? [] };
    }
}
