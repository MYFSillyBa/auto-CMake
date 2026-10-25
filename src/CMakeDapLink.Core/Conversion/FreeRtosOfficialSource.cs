using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CMakeDapLink.Core;

internal static class FreeRtosOfficialSource
{
    internal sealed record Download(Dictionary<string, byte[]> Files, string Tag, string Commit);
    private static readonly HttpClient DefaultClient = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(25) };
    private const string Repository = "https://api.github.com/repos/FreeRTOS/FreeRTOS-Kernel/";
    internal static async Task<Download> GetAsync(string version, string port, HttpClient? client, CancellationToken cancellation)
    {
        client ??= DefaultClient;
        string? commit = null; string? tag = null;
        foreach (var candidate in new[] { "V" + version + "-kernel-only", "V" + version, version })
        {
            using var reference = await JsonAsync(client, "git/ref/tags/" + Uri.EscapeDataString(candidate), true, cancellation).ConfigureAwait(false);
            if (reference == null) continue;
            var item = reference.RootElement.GetProperty("object");
            var type = item.GetProperty("type").GetString(); var sha = Sha(item);
            for (var depth = 0; type == "tag" && depth < 4; depth++)
            {
                using var annotated = await JsonAsync(client, "git/tags/" + sha, false, cancellation).ConfigureAwait(false);
                item = annotated!.RootElement.GetProperty("object"); type = item.GetProperty("type").GetString(); sha = Sha(item);
            }
            if (type != "commit") throw new InvalidDataException("官方 FreeRTOS 标签未指向可确认的提交。");
            commit = sha; tag = candidate; break;
        }
        if (commit == null) throw new InvalidDataException($"官方仓库未找到 FreeRTOS V{version} 标签，请使用同版本本地移植层导入。");
        var files = new Dictionary<string, byte[]>();
        foreach (var name in new[] { "port.c", "portmacro.h" })
        {
            var path = "contents/portable/GCC/" + port + "/" + name + "?ref=" + commit;
            using var content = await JsonAsync(client, path, true, cancellation).ConfigureAwait(false);
            if (content == null) throw new InvalidDataException($"官方版本缺少所需移植文件：{port}/{name}。请手工核对完整移植目录。");
            var json = content.RootElement;
            if (json.GetProperty("type").GetString() != "file" || json.GetProperty("encoding").GetString() != "base64")
                throw new InvalidDataException("官方 FreeRTOS 文件响应格式无效。");
            var bytes = Convert.FromBase64String(json.GetProperty("content").GetString()!);
            if (bytes.Length > FreeRtosPortRepair.MaximumFileBytes) throw new InvalidDataException("官方 FreeRTOS 文件超过大小限制。");
            var prefix = Encoding.ASCII.GetBytes($"blob {bytes.Length}\0");
            var actual = Convert.ToHexString(SHA1.HashData(prefix.Concat(bytes).ToArray()));
            if (!actual.Equals(Sha(json), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("官方 FreeRTOS 文件完整性校验失败，未写入工程。");
            files.Add(name, bytes);
        }
        return new(files, tag!, commit);
    }
    private static string Sha(JsonElement json)
    {
        var sha = json.GetProperty("sha").GetString();
        if (sha == null || !Regex.IsMatch(sha, @"\A[0-9a-fA-F]{40}\z")) throw new InvalidDataException("官方 FreeRTOS 提交或文件标识无效。");
        return sha;
    }
    private static async Task<JsonDocument?> JsonAsync(HttpClient client, string path, bool allowMissing, CancellationToken cancellation)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Repository + path);
        request.Headers.UserAgent.ParseAdd("STM32ProjectHelper-FreeRTOS-Repair/1.0");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
        if (allowMissing && response.StatusCode == HttpStatusCode.NotFound) return null;
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            throw new HttpRequestException("官方源码接口受限，请稍后重试或使用本地移植层导入。");
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"官方源码下载失败（HTTP {(int)response.StatusCode}），可使用本地移植层导入。");
        const int maximum = 2 * 1024 * 1024;
        if (response.Content.Headers.ContentLength > maximum) throw new InvalidDataException("官方 FreeRTOS 响应超过大小限制。");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);
        using var bytes = new MemoryStream(); var buffer = new byte[16384]; int count;
        while ((count = await stream.ReadAsync(buffer, cancellation).ConfigureAwait(false)) > 0)
        {
            if (bytes.Length + count > maximum) throw new InvalidDataException("官方 FreeRTOS 响应超过大小限制。");
            bytes.Write(buffer, 0, count);
        }
        return JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
    }
}
