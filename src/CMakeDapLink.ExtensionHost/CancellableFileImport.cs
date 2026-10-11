using CMakeDapLink.Core;

namespace CMakeDapLink.ExtensionHost;

internal static class CancellableFileImport
{
    public static async Task<IReadOnlyList<string>> ApplyAsync(string root, FileImportPlan plan, bool confirmedExistingFolder,
        Action<int, string, string?> progress, CancellationToken cancellation)
    {
        ValidateFolder(root, plan);
        if (plan.FolderExisted && !confirmedExistingFolder)
            throw new InvalidOperationException("目标文件夹已存在，需要确认后才能追加文件。");
        foreach (var file in plan.Files) ValidateFile(root, plan, file);
        var created = new List<string>();
        var staged = new List<string>();
        var totalBytes = plan.Files.Sum(x => x.Length);
        long copiedBytes = 0;
        var lastPercent = -1;
        try
        {
            cancellation.ThrowIfCancellationRequested();
            ValidateFolder(root, plan);
            Directory.CreateDirectory(plan.FolderPath);
            WorkspaceSafety.Child(root, plan.FolderPath);
            foreach (var file in plan.Files)
            {
                cancellation.ThrowIfCancellationRequested();
                WorkspaceSafety.Child(root, plan.FolderPath);
                ValidateFile(root, plan, file);
                var temporary = WorkspaceSafety.Child(root, Path.Combine(plan.FolderPath, ".stm32-import-" + Guid.NewGuid().ToString("N") + ".tmp"));
                await using (var destination = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    staged.Add(temporary);
                    await using var source = new FileStream(file.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                        128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    var buffer = new byte[128 * 1024];
                    while (true)
                    {
                        var count = await source.ReadAsync(buffer, cancellation);
                        if (count == 0) break;
                        await destination.WriteAsync(buffer.AsMemory(0, count), cancellation);
                        copiedBytes += count;
                        var percent = totalBytes == 0 ? 100 : (int)Math.Clamp(copiedBytes * 100d / totalBytes, 0, 100);
                        if (percent != lastPercent) { lastPercent = percent; progress(percent, "加入文件", file.DestinationPath); }
                    }
                    await destination.FlushAsync(cancellation);
                    if (destination.Length != file.Length) throw new IOException("复制后的文件大小校验失败：" + file.DestinationPath);
                }
                // Check the source snapshot and both paths again before publishing the completed file.
                cancellation.ThrowIfCancellationRequested();
                ValidateFile(root, plan, file);
                WorkspaceSafety.Child(root, temporary);
                File.Move(temporary, file.DestinationPath, overwrite: false);
                staged.Remove(temporary);
                created.Add(file.DestinationPath);
            }
            cancellation.ThrowIfCancellationRequested();
            return created;
        }
        catch
        {
            foreach (var path in created.Concat(staged).Reverse())
            {
                WorkspaceSafety.Child(root, path);
                if (File.Exists(path)) File.Delete(path);
            }
            WorkspaceSafety.Child(root, plan.FolderPath);
            if (!plan.FolderExisted && Directory.Exists(plan.FolderPath) && !Directory.EnumerateFileSystemEntries(plan.FolderPath).Any())
                Directory.Delete(plan.FolderPath);
            throw;
        }
    }

    private static void ValidateFolder(string root, FileImportPlan plan)
    {
        var folder = WorkspaceSafety.Child(root, plan.FolderPath);
        if (!Path.GetDirectoryName(folder)!.Equals(root, StringComparison.OrdinalIgnoreCase) || File.Exists(folder))
            throw new ArgumentException("目标文件夹必须直接位于当前工程内。");
        if (Directory.Exists(folder) != plan.FolderExisted)
            throw new IOException("目标文件夹在预览后发生变化，请重新预览后再加入文件。");
    }

    private static void ValidateFile(string root, FileImportPlan plan, ImportFile file)
    {
        WorkspaceSafety.Ordinary(file.SourcePath);
        var info = new FileInfo(file.SourcePath);
        if (!info.Exists || info.Length != file.Length || info.LastWriteTimeUtc != file.LastWriteTimeUtc)
            throw new IOException("来源文件在预览后发生变化：" + file.SourcePath);
        var destination = WorkspaceSafety.Child(root, file.DestinationPath);
        if (!Path.GetDirectoryName(destination)!.Equals(plan.FolderPath, StringComparison.OrdinalIgnoreCase) ||
            File.Exists(destination) || Directory.Exists(destination))
            throw new IOException("目标文件名在预览后发生变化，请重新预览。");
    }
}
