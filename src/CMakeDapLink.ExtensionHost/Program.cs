using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CMakeDapLink.Core;
using CMakeDapLink.ExtensionHost;

Console.InputEncoding = new UTF8Encoding(false);
Console.OutputEncoding = new UTF8Encoding(false);
var wire = Console.Out;
// Library diagnostics must never become unframed protocol output.
Console.SetOut(Console.Error);
var json = new JsonSerializerOptions(JsonSerializerDefaults.Web)
{
    PropertyNamingPolicy = new WireNamingPolicy(),
    Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
};
var outputGate = new object();
string id = "";
void Emit(object value)
{
    lock (outputGate) { wire.WriteLine(JsonSerializer.Serialize(value, json)); wire.Flush(); }
}
using var cancellation = new CancellationTokenSource();
try
{
    var line = await Console.In.ReadLineAsync();
    if (string.IsNullOrWhiteSpace(line)) throw new ArgumentException("请提供一行 JSON 请求。");
    if (line.Length > 96 * 1024 * 1024) throw new ArgumentException("请求超过大小限制。");
    using var input = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 64 });
    var request = input.RootElement;
    id = request.GetProperty("id").GetString() ?? throw new ArgumentException("请求 id 不能为空。");
    var command = request.GetProperty("command").GetString() ?? throw new ArgumentException("命令不能为空。");
    var payload = request.TryGetProperty("payload", out var value) ? value.Clone() : JsonSerializer.SerializeToElement(new { });
    if (payload.ValueKind != JsonValueKind.Object) throw new ArgumentException("payload 必须是 JSON 对象。");
    if (request.TryGetProperty("trusted", out var trusted) && trusted.ValueKind == JsonValueKind.False ||
        payload.TryGetProperty("trusted", out trusted) && trusted.ValueKind == JsonValueKind.False)
        throw new InvalidOperationException("请先信任当前 VS Code 工作区，再使用工程工具。");
    if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("本扩展宿主仅支持本机 Windows 工作区。");
    if (command == "capabilities")
    {
        Emit(new { id, type = "result", data = new { supported = true, platform = "win32", commands = RequestHandler.Commands } });
        return;
    }
    var root = WorkspaceSafety.Root(request.GetProperty("root").GetString() ?? "");
    // Console.In uses a synchronized reader whose async read can block before returning.
    // Keep its cancellation read off the request thread while the pipe remains open.
    _ = Task.Run(() => MonitorCancellationAsync(cancellation));
    var handler = new RequestHandler(root, json, (percent, stage, message) =>
        Emit(new { id, type = "progress", data = new { percent, stage, message } }));
    var result = await handler.ExecuteAsync(command, payload, cancellation.Token);
    Emit(new { id, type = "result", data = result });
}
catch (OperationCanceledException)
{
    Emit(new { id, type = "error", message = "操作已取消。", data = new { cancelled = true } });
    Environment.ExitCode = 2;
}
catch (Exception ex)
{
    var data = ex is ChangeConflictException conflict ? (object)new { conflicts = conflict.Conflicts } : new { code = ex.GetType().Name };
    Emit(new { id, type = "error", message = ex.Message, data });
    Environment.ExitCode = 1;
}

static async Task MonitorCancellationAsync(CancellationTokenSource cancellation)
{
    try
    {
        while (await Console.In.ReadLineAsync() is { } line)
        {
            using var message = JsonDocument.Parse(line);
            if (message.RootElement.TryGetProperty("cancel", out var value) && value.ValueKind == JsonValueKind.True)
            { cancellation.Cancel(); return; }
        }
    }
    catch (Exception ex) when (ex is JsonException or IOException or ObjectDisposedException) { }
}

internal sealed class WireNamingPolicy : JsonNamingPolicy
{
    public override string ConvertName(string name) => name == "CMake" ? "cmake" : CamelCase.ConvertName(name);
}
