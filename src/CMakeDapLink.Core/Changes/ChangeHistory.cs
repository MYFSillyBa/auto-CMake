using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CMakeDapLink.Core;

/// <summary>Stores complete file snapshots in user data and applies checked file batches.</summary>
public static class ChangeHistory
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static ChangeRecord? Apply(string root, string label, IReadOnlyList<FileChange> changes)
    {
        root = NormalizeRoot(root);
        using var gate = Gate(root);
        return ApplyLocked(root, label, changes, null);
    }

    public static IReadOnlyList<ChangeRecord> List(string root)
    {
        root = NormalizeRoot(root);
        using var gate = Gate(root);
        return ReadRecords(root);
    }

    public static IReadOnlyList<FileChange> GetRestoreChanges(string root, string id)
    {
        root = NormalizeRoot(root);
        using var gate = Gate(root);
        var record = Find(root, id);
        return record.Changes.Select(c => new FileChange(c.Path, Read(Resolve(root, c.Path)), c.Before?.ToArray())).ToArray();
    }

    /// <summary>Before is the recorded post-operation content; After is the current content.</summary>
    public static IReadOnlyList<FileChange> GetRestoreConflicts(string root, string id)
    {
        root = NormalizeRoot(root);
        using var gate = Gate(root);
        return RestoreConflicts(root, Find(root, id));
    }

    public static ChangeRecord Restore(string root, string id) => Restore(root, id, false);

    public static ChangeRecord Restore(string root, string id, bool overwriteConflicts) => Restore(root, id, overwriteConflicts, null);

    public static ChangeRecord Restore(string root, string id, bool overwriteConflicts, IReadOnlyList<FileChange>? reviewedChanges)
    {
        root = NormalizeRoot(root);
        using var gate = Gate(root);
        var original = Find(root, id);
        if (original.RestoredUtc != null) throw new InvalidOperationException("这条记录已经恢复。请选择对应的恢复记录以撤销恢复。");
        var conflicts = RestoreConflicts(root, original);
        if (conflicts.Length > 0 && !overwriteConflicts)
            throw new ChangeConflictException("文件在这次操作之后又被修改，请先查看差异并决定是否覆盖。", conflicts);
        var changes = original.Changes.Select(c => new FileChange(c.Path, Read(Resolve(root, c.Path)), c.Before?.ToArray())).ToArray();
        if (reviewedChanges != null)
        {
            if (reviewedChanges.Count != changes.Length || changes.Any(c => !reviewedChanges.Any(p =>
                    string.Equals(Resolve(root, p.Path), Resolve(root, c.Path), StringComparison.OrdinalIgnoreCase)
                    && Equal(p.Before, c.Before) && Equal(p.After, c.After))))
                throw new ChangeConflictException("文件在恢复预览后发生变化，请重新预览。", changes);
            changes = reviewedChanges.ToArray();
        }
        var restored = ApplyLocked(root, "恢复 · " + original.Label, changes, original.Id);
        var marked = original with { RestoredUtc = DateTimeOffset.UtcNow };
        SaveManifest(root, marked, "Complete");
        return restored ?? marked;
    }

    private static ChangeRecord? ApplyLocked(string root, string label, IReadOnlyList<FileChange> requested, string? restorationOf)
    {
        var changes = requested.Select(c => new FileChange(System.IO.Path.GetRelativePath(root, Resolve(root, c.Path)),
            c.Before?.ToArray(), c.After?.ToArray())).ToArray();
        if (changes.Select(c => c.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != changes.Length)
            throw new ArgumentException("一次操作不能多次修改同一文件。", nameof(requested));
        changes = changes.Where(c => !Equal(c.Before, c.After)).ToArray();
        if (changes.Length == 0) return null;
        CheckCurrent(root, changes);
        var record = new ChangeRecord(DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N"),
            root, label, DateTimeOffset.UtcNow, changes) { RestorationOf = restorationOf };
        SaveManifest(root, record, "Pending");
        var staged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var applied = new List<FileChange>();
        var createdDirectories = new List<string>();
        try
        {
            foreach (var change in changes.Where(c => c.After != null))
            {
                var path = Resolve(root, change.Path);
                var directory = System.IO.Path.GetDirectoryName(path)!;
                var missing = new Stack<string>();
                for (var item = directory; !Directory.Exists(item); item = System.IO.Path.GetDirectoryName(item)!) missing.Push(item);
                while (missing.Count > 0) { var item = missing.Pop(); Directory.CreateDirectory(item); createdDirectories.Add(item); }
                var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
                staged.Add(path, temporary);
                WriteBytes(temporary, change.After!);
            }
            CheckCurrent(root, changes);
            foreach (var change in changes)
            {
                var path = Resolve(root, change.Path);
                if (!Equal(Read(path), change.Before))
                    throw new ChangeConflictException("文件在预览后发生变化，请重新预览。", [change]);
                if (change.After == null) File.Delete(path);
                else File.Move(staged[path], path, true);
                applied.Add(change);
            }
            SaveManifest(root, record, "Complete");
            return record;
        }
        catch (Exception failure)
        {
            var rollbackErrors = new List<Exception>();
            foreach (var change in applied.AsEnumerable().Reverse())
            {
                try
                {
                    var path = Resolve(root, change.Path);
                    if (!Equal(Read(path), change.After)) throw new IOException("回滚期间文件被修改：" + change.Path);
                    Replace(path, change.Before);
                }
                catch (Exception ex) { rollbackErrors.Add(ex); }
            }
            try { SaveManifest(root, record, rollbackErrors.Count == 0 ? "RolledBack" : "RecoveryNeeded"); }
            catch (Exception ex) { rollbackErrors.Add(ex); }
            if (rollbackErrors.Count > 0) throw new AggregateException("操作未完成；快照已保留，部分文件需要从快照恢复。", new[] { failure }.Concat(rollbackErrors));
            throw;
        }
        finally
        {
            foreach (var temporary in staged.Values) if (File.Exists(temporary)) File.Delete(temporary);
            foreach (var directory in createdDirectories.AsEnumerable().Reverse())
                if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
        }
    }

    private static void CheckCurrent(string root, IEnumerable<FileChange> changes)
    {
        var conflicts = changes.Where(c => !Equal(Read(Resolve(root, c.Path)), c.Before)).ToArray();
        if (conflicts.Length > 0) throw new ChangeConflictException("文件在预览后发生变化，请重新预览。", conflicts);
    }

    private static FileChange[] RestoreConflicts(string root, ChangeRecord record) => record.Changes
        .Select(c => new FileChange(c.Path, c.After, Read(Resolve(root, c.Path))))
        .Where(c => !Equal(c.Before, c.After)).ToArray();

    private static ChangeRecord Find(string root, string id) => ReadRecords(root).FirstOrDefault(r => r.Id == id)
        ?? throw new FileNotFoundException("找不到这条恢复记录。");

    private static IReadOnlyList<ChangeRecord> ReadRecords(string root)
    {
        var directory = HistoryDirectory(root);
        if (!Directory.Exists(directory)) return [];
        var records = new List<ChangeRecord>();
        foreach (var path in Directory.EnumerateFiles(directory, "record.json", SearchOption.AllDirectories))
        {
            var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllBytes(path), JsonOptions)
                ?? throw new InvalidDataException("无法读取恢复快照：" + path);
            if (manifest.Status is "Complete" or "Pending" or "RecoveryNeeded"
                && string.Equals(root, manifest.Record.Root, StringComparison.OrdinalIgnoreCase))
                records.Add(manifest.Record with { Status = manifest.Status });
        }
        return records.OrderByDescending(r => r.CreatedUtc).ToArray();
    }

    private static void SaveManifest(string root, ChangeRecord record, string status)
    {
        var directory = System.IO.Path.Combine(HistoryDirectory(root), record.Id);
        Directory.CreateDirectory(directory);
        Replace(System.IO.Path.Combine(directory, "record.json"), JsonSerializer.SerializeToUtf8Bytes(new Manifest(status, record), JsonOptions));
    }

    private static string HistoryDirectory(string root) => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CMakeDapLink", "change-history", Key(root));

    private static string Key(string root) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root.TrimEnd('\\', '/').ToUpperInvariant())));

    private static string NormalizeRoot(string root) => System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(root));

    private static IDisposable Gate(string root) => new HistoryGate("Local\\CMakeDapLink-Changes-" + Key(root));

    private static string Resolve(string root, string path)
    {
        var full = System.IO.Path.GetFullPath(System.IO.Path.IsPathRooted(path) ? path : System.IO.Path.Combine(root, path));
        var prefix = root.TrimEnd('\\', '/') + System.IO.Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("文件必须位于工程目录内：" + path);
        for (var item = full; !string.Equals(item, root, StringComparison.OrdinalIgnoreCase); item = System.IO.Path.GetDirectoryName(item)!)
            if ((File.Exists(item) || Directory.Exists(item)) && (File.GetAttributes(item) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("无法通过链接目录修改文件：" + path);
        return full;
    }

    private static byte[]? Read(string path) => File.Exists(path) ? File.ReadAllBytes(path) : null;
    private static bool Equal(byte[]? left, byte[]? right) => left == null ? right == null : right != null && left.AsSpan().SequenceEqual(right);

    private static void Replace(string path, byte[]? bytes)
    {
        if (bytes == null) { File.Delete(path); return; }
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try { WriteBytes(temporary, bytes); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void WriteBytes(string path, byte[] bytes)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(bytes);
        stream.Flush(true);
    }

    private sealed record Manifest(string Status, ChangeRecord Record);

    private sealed class HistoryGate : IDisposable
    {
        private readonly Mutex _mutex;
        public HistoryGate(string name)
        {
            _mutex = new Mutex(false, name);
            try { _mutex.WaitOne(); }
            catch (AbandonedMutexException) { }
        }
        public void Dispose() { _mutex.ReleaseMutex(); _mutex.Dispose(); }
    }
}
