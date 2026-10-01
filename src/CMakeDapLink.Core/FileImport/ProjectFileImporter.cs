using System.Text.RegularExpressions;

namespace CMakeDapLink.Core;

public sealed record ImportFile(string SourcePath, string DestinationPath, long Length, DateTime LastWriteTimeUtc);
public sealed record FileImportPlan(string Root, string FolderPath, bool FolderExisted, IReadOnlyList<ImportFile> Files);

public static class ProjectFileImporter
{
    public static FileImportPlan Preview(string root, string folderName, IReadOnlyCollection<string> sourceFiles)
    {
        root = Path.GetFullPath(root);
        if (!Directory.Exists(root)) throw new ArgumentException("请先选择当前工程。", nameof(root));
        ValidateName(folderName);
        var folder = Path.Combine(root, folderName);
        ValidateFolder(root, folder);
        if (sourceFiles.Count == 0) throw new ArgumentException("请至少选择一个要加入的文件。", nameof(sourceFiles));
        var used = Directory.Exists(folder) ? Directory.EnumerateFileSystemEntries(folder)
            .Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase) : new HashSet<string?>(StringComparer.OrdinalIgnoreCase);
        var files = new List<ImportFile>();
        foreach (var source in sourceFiles.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var info = new FileInfo(source);
            if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new ArgumentException("文件不存在或是链接：" + source, nameof(sourceFiles));
            var name = info.Name;
            var index = 1;
            while (!used.Add(name)) name = Path.GetFileNameWithoutExtension(info.Name) + $" ({index++})" + info.Extension;
            files.Add(new(source, Path.Combine(folder, name), info.Length, info.LastWriteTimeUtc));
        }
        return new(root, folder, Directory.Exists(folder), files);
    }

    public static IReadOnlyList<string> Apply(FileImportPlan plan, bool confirmedExistingFolder, Action<int, int, string>? progress = null)
    {
        ValidateFolder(plan.Root, plan.FolderPath);
        if (Directory.Exists(plan.FolderPath) != plan.FolderExisted)
            throw new IOException("目标文件夹在预览后发生变化，请重新预览后再加入文件。");
        if (plan.FolderExisted && !confirmedExistingFolder)
            throw new InvalidOperationException("目标文件夹已存在，需要确认后才能追加文件。");
        foreach (var file in plan.Files)
        {
            var info = new FileInfo(file.SourcePath);
            if (!info.Exists || info.Length != file.Length || info.LastWriteTimeUtc != file.LastWriteTimeUtc ||
                (info.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("来源文件在预览后发生变化：" + file.SourcePath);
            if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(file.DestinationPath)), plan.FolderPath, StringComparison.OrdinalIgnoreCase) ||
                File.Exists(file.DestinationPath) || Directory.Exists(file.DestinationPath))
                throw new IOException("目标文件名在预览后发生变化，请重新预览。");
        }
        var created = new List<string>();
        try
        {
            Directory.CreateDirectory(plan.FolderPath);
            foreach (var file in plan.Files)
            {
                ValidateFolder(plan.Root, plan.FolderPath);
                // Reserve each destination so rollback also removes partially copied files.
                using var destination = new FileStream(file.DestinationPath, FileMode.CreateNew, FileAccess.Write);
                created.Add(file.DestinationPath);
                using var source = File.OpenRead(file.SourcePath);
                source.CopyTo(destination);
                if (destination.Length != file.Length) throw new IOException("复制后的文件大小校验失败：" + file.DestinationPath);
                progress?.Invoke(created.Count, plan.Files.Count, file.DestinationPath);
            }
            return created;
        }
        catch
        {
            foreach (var file in created) File.Delete(file);
            if (!plan.FolderExisted && Directory.Exists(plan.FolderPath) && !Directory.EnumerateFileSystemEntries(plan.FolderPath).Any())
                Directory.Delete(plan.FolderPath);
            throw;
        }
    }

    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 240 || name is "." or ".." ||
            name.EndsWith('.') || name.EndsWith(' ') || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            name.Contains('/') || name.Contains('\\') || name.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
            name.Equals(".vscode", StringComparison.OrdinalIgnoreCase) ||
            Regex.IsMatch(name, @"\A(?:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)", RegexOptions.IgnoreCase))
            throw new ArgumentException("请输入有效的文件夹名称（不含路径分隔符、末尾空格或 Windows 保留名称）。", nameof(name));
    }

    private static void ValidateFolder(string root, string folder)
    {
        root = Path.GetFullPath(root); folder = Path.GetFullPath(folder);
        if (!string.Equals(Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(folder) ?? ""), Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("目标文件夹必须直接位于当前工程内。");
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0 ||
            Directory.Exists(folder) && (File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0)
            throw new ArgumentException("工程或目标文件夹不能是符号链接或联接目录。");
        if (File.Exists(folder)) throw new ArgumentException("该名称已被文件占用，请更改文件夹名称。");
    }
}
