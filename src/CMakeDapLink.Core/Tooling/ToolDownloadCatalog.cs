using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CMakeDapLink.Core;

public sealed record ToolPackage(string Key, string Version, string FileName, string Sha256,
    string Publisher, IReadOnlyList<string> Urls);

public sealed class ToolDownloadCatalog(HttpClient http, ToolRepairSettings settings, Action<string> log)
{
    public async Task<ToolPackage> OfficialAsync(string key, CancellationToken cancellation)
    {
        if (key == "Compiler") return await ArmAsync(cancellation);
        if (key == "CMake") return await CMakeAsync(cancellation);
        var repo = key switch { "CMake" => "Kitware/CMake", "Ninja" => "ninja-build/ninja", _ => "xpack-dev-tools/openocd-xpack" };
        var suffix = key switch { "CMake" => "windows-x86_64.zip", "Ninja" => "ninja-win.zip", _ => "win32-x64.zip" };
        string metadata;
        try { metadata = await TextAsync($"https://api.github.com/repos/{repo}/releases/latest", cancellation); }
        catch (HttpRequestException)
        {
            log("GitHub API 暂不可用，改读同一发布方的正式发布页。");
            return await GitHubPageAsync(key, repo, suffix, cancellation);
        }
        using var release = JsonDocument.Parse(metadata);
        var root = release.RootElement;
        if (root.GetProperty("prerelease").GetBoolean() || root.GetProperty("draft").GetBoolean())
            throw new InvalidDataException("发布页未返回稳定版本。");
        var asset = root.GetProperty("assets").EnumerateArray().FirstOrDefault(x => x.GetProperty("name").GetString()!.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
        if (asset.ValueKind == JsonValueKind.Undefined) throw new InvalidDataException("最新版本没有对应的 Windows ZIP 安装包。");
        var name = asset.GetProperty("name").GetString()!;
        var tag = root.GetProperty("tag_name").GetString()!;
        var url = asset.GetProperty("browser_download_url").GetString()!;
        var digest = asset.TryGetProperty("digest", out var d) ? d.GetString() : null;
        var hash = digest?.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) == true ? digest[7..] : "";
        if (!Regex.IsMatch(hash, @"\A[0-9a-fA-F]{64}\z"))
        {
            var checksum = root.GetProperty("assets").EnumerateArray().FirstOrDefault(x =>
                x.GetProperty("name").GetString() == name + ".sha" || x.GetProperty("name").GetString()!.EndsWith("-SHA-256.txt"));
            if (checksum.ValueKind == JsonValueKind.Undefined) throw new InvalidDataException("发布方未提供该文件的 SHA-256，不能验证安装包。");
            hash = ReadHash(await TextAsync(checksum.GetProperty("browser_download_url").GetString()!, cancellation), name);
        }
        var urls = new List<string> { url };
        AddProxy(urls, url);
        if (key == "CMake") urls.Add($"https://downloads.sourceforge.net/project/cmake.mirror/{tag}/{name}");
        if (key == "OpenOcd") urls.Add($"https://downloads.sourceforge.net/project/openocd-xpack/{tag}/{name}");
        return new(key, tag.TrimStart('v'), name, hash, key == "OpenOcd" ? "xPack OpenOCD（OpenOCD 官网列出的 Windows 发行包）" : repo, urls);
    }

    private async Task<ToolPackage> CMakeAsync(CancellationToken cancellation)
    {
        const string root = "https://cmake.org/files/LatestRelease/";
        using var doc = JsonDocument.Parse(await TextAsync(root + "cmake-latest-files-v1.json", cancellation));
        var version = doc.RootElement.GetProperty("version").GetProperty("string").GetString()!;
        var name = doc.RootElement.GetProperty("files").EnumerateArray().First(x =>
            x.GetProperty("name").GetString()!.EndsWith("windows-x86_64.zip")).GetProperty("name").GetString()!;
        var checksum = doc.RootElement.GetProperty("hashFiles").EnumerateArray().First(x =>
            x.GetProperty("algorithm").EnumerateArray().Any(a => a.GetString() == "sha256")).GetProperty("name").GetString()!;
        var hash = ReadHash(await TextAsync(root + checksum, cancellation), name);
        var url = $"https://github.com/Kitware/CMake/releases/download/v{version}/{name}";
        var urls = new List<string> { url, root + name }; AddProxy(urls, url);
        urls.Add($"https://downloads.sourceforge.net/project/cmake.mirror/v{version}/{name}");
        return new("CMake", version, name, hash, "CMake 官方发布索引", urls);
    }

    private async Task<ToolPackage> GitHubPageAsync(string key, string repo, string suffix, CancellationToken cancellation)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation); timeout.CancelAfter(TimeSpan.FromSeconds(25));
        using var response = await http.GetAsync($"https://github.com/{repo}/releases/latest", timeout.Token);
        response.EnsureSuccessStatusCode();
        var uri = response.RequestMessage!.RequestUri!;
        var tag = Uri.UnescapeDataString(uri.AbsolutePath.Split('/').Last());
        if (!uri.AbsolutePath.StartsWith($"/{repo}/releases/tag/", StringComparison.Ordinal)) throw new InvalidDataException("正式发布页没有返回版本号。");
        var assets = await TextAsync($"https://github.com/{repo}/releases/expanded_assets/{Uri.EscapeDataString(tag)}", cancellation);
        foreach (Match link in Regex.Matches(assets, "href=\"(?<url>/" + Regex.Escape(repo) + "/releases/download/[^\"]+)\""))
        {
            var url = "https://github.com" + WebUtility.HtmlDecode(link.Groups["url"].Value);
            var name = Path.GetFileName(new Uri(url).AbsolutePath);
            if (!name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;
            var end = assets.IndexOf("</li>", link.Index, StringComparison.Ordinal);
            var row = end < 0 ? assets[link.Index..] : assets[link.Index..end];
            var digest = Regex.Match(row, @"sha256:([0-9a-fA-F]{64})");
            if (!digest.Success) throw new InvalidDataException("正式发布页没有该安装包的 SHA-256。");
            var urls = new List<string> { url }; AddProxy(urls, url);
            if (key == "OpenOcd") urls.Add($"https://downloads.sourceforge.net/project/openocd-xpack/{tag}/{name}");
            return new(key, tag.TrimStart('v'), name, digest.Groups[1].Value, repo + " 官方发布页", urls);
        }
        throw new InvalidDataException("正式发布页没有对应 Windows ZIP。");
    }

    private async Task<ToolPackage> ArmAsync(CancellationToken cancellation)
    {
        const string api = "https://gitlab.arm.com/api/v4/projects/10698";
        var index = await TextAsync(api + "/repository/files/README.md/raw?ref=main", cancellation);
        var versions = Regex.Matches(index, @"releases/(\d+\.\d+\.rel\d+)", RegexOptions.IgnoreCase)
            .Select(x => x.Groups[1].Value).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(x => Version.Parse(Regex.Replace(x, @"\.rel", ".", RegexOptions.IgnoreCase))).ToArray();
        if (versions.Length == 0) throw new InvalidDataException("Arm 官方发布索引中没有可识别的稳定版本。");
        var version = versions[0];
        var release = await TextAsync(api + "/repository/files/README.md/raw?ref=" + Uri.EscapeDataString("releases/" + version), cancellation);
        var urls = Regex.Matches(release, @"https://[^\s)<>""]+mingw-w64-(?:x86_64|i686)-arm-none-eabi\.zip\b")
            .Select(x => x.Value).Distinct().OrderByDescending(x => x.Contains("x86_64", StringComparison.Ordinal)).ToArray();
        if (urls.Length == 0) throw new InvalidDataException("Arm 最新版本未提供 Windows arm-none-eabi ZIP。");
        var official = urls[0];
        var name = Path.GetFileName(new Uri(official).AbsolutePath);
        // Package metadata is supplied by Arm GitLab and carries the SHA-256 of the ZIP.
        using var packages = JsonDocument.Parse(await TextAsync(api + "/packages?package_type=generic&package_name=gnu-toolchain&package_version=" + Uri.EscapeDataString(version), cancellation));
        var package = packages.RootElement.EnumerateArray().FirstOrDefault(x => x.GetProperty("version").GetString() == version);
        string hash = "";
        if (package.ValueKind != JsonValueKind.Undefined)
        {
            for (var page = 1; page <= 5 && hash.Length == 0; page++)
            {
                using var files = JsonDocument.Parse(await TextAsync(api + "/packages/" + package.GetProperty("id").GetInt32() + "/package_files?per_page=100&page=" + page, cancellation));
                foreach (var file in files.RootElement.EnumerateArray())
                    if (file.GetProperty("file_name").GetString() == name) hash = file.GetProperty("file_sha256").GetString() ?? "";
                if (files.RootElement.GetArrayLength() < 100) break;
            }
        }
        if (hash.Length == 0) hash = ReadHash(await TextAsync(official + ".sha256asc", cancellation), name);
        return new("Compiler", version, name, hash, "Arm 官方 GitLab", [official]);
    }

    public async Task<ToolPackage> AlternativeAsync(string key, CancellationToken cancellation)
    {
        var name = key switch { "CMake" => "cmake", "Ninja" => "ninja-build", "Compiler" => "arm-none-eabi-gcc", _ => "openocd" };
        var packageName = "@xpack-dev-tools/" + name;
        string json;
        try { json = await TextAsync("https://registry.npmjs.org/" + packageName + "/latest", cancellation); }
        catch (Exception ex) when (!cancellation.IsCancellationRequested && ex is HttpRequestException or TaskCanceledException or InvalidDataException)
        {
            log("备用发行版索引切换到国内 npmmirror。");
            json = await TextAsync("https://registry.npmmirror.com/" + packageName + "/latest", cancellation);
        }
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.GetProperty("name").GetString() != packageName) throw new InvalidDataException("备用索引的包名不匹配。");
        var binary = root.GetProperty("xpack").GetProperty("binaries");
        var windows = binary.GetProperty("platforms").GetProperty("win32-x64");
        var fileName = windows.GetProperty("fileName").GetString()!;
        var baseUrl = binary.GetProperty("baseUrl").GetString()!;
        if (!baseUrl.StartsWith("https://github.com/xpack-dev-tools/" + name + "-xpack/releases/download/", StringComparison.Ordinal))
            throw new InvalidDataException("备用发行版的发布地址不匹配。");
        var tag = baseUrl.Split('/').Last();
        var url = baseUrl + "/" + fileName;
        var urls = new List<string>(); AddProxy(urls, url);
        urls.Add($"https://downloads.sourceforge.net/project/{name}-xpack/{tag}/{fileName}");
        urls.Add(url);
        return new(key, tag.TrimStart('v'), fileName, windows.GetProperty("sha256").GetString()!, "xPack 备用发行版（可能与官方最新版本不同）", urls);
    }

    private void AddProxy(List<string> urls, string original)
    {
        if (!string.IsNullOrWhiteSpace(settings.GitHubMirror)) urls.Add(settings.GitHubMirror.Replace("{url}", original, StringComparison.Ordinal));
    }
    private static string ReadHash(string text, string fileName)
    {
        var line = text.Split('\n').FirstOrDefault(x => x.Contains(fileName, StringComparison.Ordinal)) ?? text;
        var match = Regex.Match(line, @"\b[0-9a-fA-F]{64}\b");
        return match.Success ? match.Value : throw new InvalidDataException("没有找到安装包的 SHA-256。");
    }
    private async Task<string> TextAsync(string url, CancellationToken cancellation)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(25));
        using var response = await http.GetAsync(url, timeout.Token);
        response.EnsureSuccessStatusCode();
        var text = await response.Content.ReadAsStringAsync(timeout.Token);
        if (text.Length == 0) throw new InvalidDataException("发布索引返回了空内容。");
        return text;
    }
}
