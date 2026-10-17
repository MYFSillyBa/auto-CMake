using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace CMakeDapLink.ExtensionHost;

/// <summary>Shares the software and explicitly requested hardware workflows with the desktop UI.</summary>
public static class DesktopBackend
{
    private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal)
    {
        "inspect", "previewTasks", "applyChanges", "verify", "build", "flash", "memory", "history", "previewRestore", "restore",
        "inspectConversion", "previewConversion", "previewClangd", "previewDebug", "prepareCompileDatabase", "probeCheck", "rtos", "analyzeFirmware", "hardFault"
    };

    public static async Task<JsonElement> ExecuteAsync(string root, string command, JsonElement payload,
        string language = "zh-CN", Action<int, string, string?>? progress = null, CancellationToken cancellation = default)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Desktop tools require Windows.");
        if (!Allowed.Contains(command)) throw new ArgumentException("Unsupported desktop operation.", nameof(command));
        if (payload.ValueKind != JsonValueKind.Object) throw new ArgumentException("Payload must be an object.", nameof(payload));
        var directory = WorkspaceSafety.Root(root);
        var localization = new DisplayLocalization { Language = language == "en" ? "en" : "zh-CN" };
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNamingPolicy = new WireNamingPolicy(),
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
        };
        var handler = new RequestHandler(directory, options,
            (percent, stage, message) => progress?.Invoke(percent, localization.Display(stage), message == null ? null : localization.Display(message)), localization.Display);
        try
        {
            var input = payload.Clone();
            var result = await Task.Run(() => handler.ExecuteAsync(command, input, cancellation), cancellation).ConfigureAwait(false);
            var envelope = new JsonObject { ["data"] = JsonSerializer.SerializeToNode(result, options) };
            localization.Apply(envelope);
            return JsonSerializer.SerializeToElement(envelope["data"], options);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error)
        {
            var text = localization.Display(error.Message);
            if (text == error.Message) throw;
            throw new InvalidOperationException(text, error);
        }
    }
}
