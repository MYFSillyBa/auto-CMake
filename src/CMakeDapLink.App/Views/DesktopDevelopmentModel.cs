using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CMakeDapLink.Core;

namespace CMakeDapLink.App;

internal static class DesktopDevelopmentModel
{
    internal static void Ordinary(string path)
    {
        for (var current = Path.GetFullPath(path); current != null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("不能通过符号链接或联接目录访问同步输入或恢复记录：" + current);
    }
    private static readonly Regex ManagedBlock = new(@"(?m)^# BEGIN CMakeDapLink managed folders\r?\n[\s\S]*?^# END CMakeDapLink managed folders", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex Register = new(@"\G\s*[,;]?\s*(?<name>[A-Za-z][A-Za-z0-9_]*)\s*[:=]\s*(?<value>0x[0-9a-fA-F]+|[0-9]+)\b", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly HashSet<string> Skipped = new(StringComparer.OrdinalIgnoreCase) { ".git", ".vscode", ".vs", "build", "bin", "obj", "node_modules", ".cache", ".cmake", "CMakeFiles" };

    internal static IReadOnlyDictionary<string, string> ParseRegisters(string text)
    {
        text = text.Trim();
        if (text.Length > 32768) throw new ArgumentException("寄存器输入不能超过 32 KB。");
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Add(string name, string value)
        {
            if (result.Count >= 40 || name.Length > 32) throw new ArgumentException("寄存器数量或名称长度超过限制。");
            var hex = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
            if (!uint.TryParse(hex ? value[2..] : value, hex ? NumberStyles.AllowHexSpecifier : NumberStyles.None, CultureInfo.InvariantCulture, out var number)) throw new ArgumentException("寄存器值必须是 32 位无符号整数：" + name);
            name = name.ToUpperInvariant();
            var canonical = "0x" + number.ToString("X8", CultureInfo.InvariantCulture);
            if (result.TryGetValue(name, out var previous) && previous != canonical) throw new ArgumentException("同一寄存器包含不一致的值：" + name);
            result[name] = canonical;
        }
        if (text.StartsWith('{'))
        {
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new ArgumentException("寄存器 JSON 必须是对象。");
            foreach (var item in document.RootElement.EnumerateObject())
                Add(item.Name, item.Value.ValueKind == JsonValueKind.String ? item.Value.GetString()! : item.Value.GetRawText());
        }
        else
        {
            var offset = 0;
            while (offset < text.Length)
            {
                var match = Register.Match(text, offset);
                if (!match.Success) { if (string.IsNullOrWhiteSpace(text[offset..])) break; throw new ArgumentException("无法识别寄存器输入，请使用 CFSR=0x00000000 或 JSON 对象。"); }
                Add(match.Groups["name"].Value, match.Groups["value"].Value);
                offset = match.Index + match.Length;
            }
        }
        if (result.Count == 0) throw new ArgumentException("请粘贴实际捕获的异常寄存器。");
        return result;
    }

    internal static bool Relevant(string relative) => !relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(Skipped.Contains)
        && (Path.GetExtension(relative).ToLowerInvariant() is ".ioc" or ".uvprojx" or ".ld" or ".sct" || Path.GetFileName(relative) is "CMakeLists.txt" or "CMakePresets.json" or "CMakeUserPresets.json");

    internal static async Task<Dictionary<string, string>> FingerprintAsync(string root, CancellationToken cancellation)
    {
        return await Task.Run(() =>
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var queue = new Queue<string>(); queue.Enqueue(root); int directories = 0; long bytes = 0;
            while (queue.TryDequeue(out var directory))
            {
                cancellation.ThrowIfCancellationRequested();
                if (++directories > 6000) throw new IOException("同步扫描目录过多，请选择更具体的工程目录。");
                foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
                {
                    cancellation.ThrowIfCancellationRequested();
                    var attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                    if ((attributes & FileAttributes.Directory) != 0) { if (!Skipped.Contains(Path.GetFileName(entry))) queue.Enqueue(entry); continue; }
                    var relative = Path.GetRelativePath(root, entry);
                    if (!Relevant(relative)) continue;
                    var length = new FileInfo(entry).Length;
                    if (length > 4 * 1024 * 1024 || (bytes += length) > 32 * 1024 * 1024) throw new IOException("同步配置输入超过大小限制。");
                    result[relative] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(entry)));
                }
            }
            return result;
        }, cancellation);
    }

    internal static string[] ReadBlocks(string root)
    {
        var file = Path.Combine(root, "CMakeLists.txt");
        Ordinary(file);
        if (!File.Exists(file)) return [];
        if (new FileInfo(file).Length > 4 * 1024 * 1024) throw new IOException("CMakeLists.txt 超过恢复读取范围。");
        return ManagedBlock.Matches(File.ReadAllText(file)).Select(x => x.Value).Take(8).ToArray();
    }

    internal static List<FileChange> PreserveManagedBlocks(string root, List<FileChange> changes, string[] remembered)
    {
        var path = Path.Combine(root, "CMakeLists.txt");
        Ordinary(path);
        var existing = changes.FindIndex(x => Path.GetFullPath(x.Path, root).Equals(path, StringComparison.OrdinalIgnoreCase));
        var before = existing >= 0 ? changes[existing].Before : File.Exists(path) ? File.ReadAllBytes(path) : null;
        var content = existing >= 0 ? changes[existing].After : before;
        if (content == null) return changes;
        using var reader = new StreamReader(new MemoryStream(content), Encoding.UTF8, true);
        var text = reader.ReadToEnd(); var encoding = reader.CurrentEncoding;
        var preamble = encoding.GetPreamble();
        if (!content.AsSpan().StartsWith(preamble)) preamble = [];
        var additions = remembered.Where(block => ManagedBlock.IsMatch(block) && block.Length <= 4 * 1024 * 1024 && !text.Contains(block, StringComparison.Ordinal)).Distinct(StringComparer.Ordinal).ToArray();
        if (additions.Length == 0) return changes;
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        text = text.TrimEnd('\r', '\n') + newline + newline + string.Join(newline, additions) + newline;
        var change = new FileChange(path, before, preamble.Concat(encoding.GetBytes(text)).ToArray());
        if (existing >= 0) changes[existing] = change; else changes.Add(change);
        return changes;
    }
}

internal sealed class DesktopDevelopmentSyncState(string root)
{
    private Dictionary<string, string>? baseline;
    internal string[] Blocks { get; private set; } = [];
    private string RecoveryPath => Path.Combine(ManagedTools.UserDataRoot, "desktop-sync", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root.ToUpperInvariant()))) + ".json");

    internal async Task InitializeAsync(CancellationToken cancellation)
    {
        baseline = await DesktopDevelopmentModel.FingerprintAsync(root, cancellation);
        Blocks = DesktopDevelopmentModel.ReadBlocks(root);
        var path = RecoveryPath;
        DesktopDevelopmentModel.Ordinary(path);
        if (Blocks.Length == 0 && File.Exists(path) && new FileInfo(path).Length <= 4 * 1024 * 1024)
        {
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path, cancellation));
            if (document.RootElement.TryGetProperty("root", out var savedRoot) && string.Equals(savedRoot.GetString(), root, StringComparison.OrdinalIgnoreCase))
                Blocks = document.RootElement.GetProperty("blocks").EnumerateArray().Take(8).Select(x => x.GetString() ?? "").Where(x => x.Length <= 4 * 1024 * 1024).ToArray();
        }
        if (DesktopDevelopmentModel.ReadBlocks(root).Length > 0) SaveBlocks();
    }

    internal async Task<string[]> ChangedAsync(CancellationToken cancellation)
    {
        var current = await DesktopDevelopmentModel.FingerprintAsync(root, cancellation);
        if (baseline == null) { baseline = current; return []; }
        var changed = baseline.Keys.Union(current.Keys, StringComparer.OrdinalIgnoreCase).Where(path => baseline.GetValueOrDefault(path) != current.GetValueOrDefault(path)).ToList();
        if (Blocks.Length > 0 && DesktopDevelopmentModel.ReadBlocks(root).Length == 0 && !changed.Contains("CMakeLists.txt", StringComparer.OrdinalIgnoreCase)) changed.Add("CMakeLists.txt");
        return changed.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    internal async Task AcceptBaselineAsync(CancellationToken cancellation)
    {
        var blocks = DesktopDevelopmentModel.ReadBlocks(root);
        if (blocks.Length > 0) { Blocks = blocks; SaveBlocks(); }
        baseline = await DesktopDevelopmentModel.FingerprintAsync(root, cancellation);
    }
    private void SaveBlocks()
    {
        var path = RecoveryPath;
        DesktopDevelopmentModel.Ordinary(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(new { root, blocks = Blocks })); File.Move(temporary, path, overwrite: true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
