using System.Text;
using System.Text.RegularExpressions;

namespace CMakeDapLink.Core;

public enum ConversionDirection { MdkToCMake, CMakeToMdk }
public sealed record ConversionIssue(string Message, string Action, bool Blocking);
public sealed record ConversionInspection(string Root, string Chip, IReadOnlyList<string> Targets,
    IReadOnlyList<string> SourceProjects, IReadOnlyList<ConversionIssue> Issues);
public sealed record ConversionRequest(string Root, ConversionDirection Direction, string Target,
    string? SourceProject = null, string? BuildDirectory = null, string Configuration = "Debug");
public sealed record ConversionPlan(string Root, string OutputProject, IReadOnlyList<FileChange> Changes,
    IReadOnlyList<ConversionIssue> Issues, int SourceCount, int IncludeCount, string Summary)
{
    public bool CanApply => !Issues.Any(x => x.Blocking) && Changes.Count > 0;
}

internal sealed record ConversionSource(string Path, string Group, string Language,
    IReadOnlyList<string> Includes, IReadOnlyList<string> Defines)
{
    public IReadOnlyList<string> CompilerFlags { get; init; } = [];
}
internal sealed record ConversionMemory(ulong FlashStart, ulong FlashSize, ulong RamStart, ulong RamSize,
    ulong StackSize = 0x400, ulong HeapSize = 0x200, bool RamFunctions = false);
internal sealed class ConversionProject
{
    public required string Root { get; init; }
    public required string Name { get; init; }
    public required string Chip { get; init; }
    public string OutputName { get; set; } = "";
    public string Cpu { get; set; } = "";
    public string? Fpu { get; set; }
    public string FloatAbi { get; set; } = "soft";
    public List<ConversionSource> Sources { get; } = [];
    public List<string> Libraries { get; } = [];
    public List<string> Includes { get; } = [];
    public List<string> Defines { get; } = [];
    public ConversionMemory? Memory { get; set; }
    public string? Startup { get; set; }
    public string? Linker { get; set; }
    public List<ConversionIssue> Issues { get; } = [];
}

internal static class ConversionPaths
{
    public static string Resolve(string directory, string path) => Path.GetFullPath(path.Replace('\\', Path.DirectorySeparatorChar), directory);
    public static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');
    public static string SafeName(string name)
    {
        var safe = Regex.Replace(name, @"[^A-Za-z0-9_.-]", "_");
        return string.IsNullOrEmpty(safe) || !char.IsLetter(safe[0]) ? "firmware_" + safe : safe;
    }
    public static string Quote(string text) => "\"" + text.Replace('\\', '/').Replace("\"", "\\\"").Replace("$", "\\$").Replace(";", "\\;") + "\"";
    public static FileChange Change(string root, string path, string text)
    {
        var full = Resolve(root, path);
        return new(full, File.Exists(full) ? File.ReadAllBytes(full) : null, new UTF8Encoding(false).GetBytes(text.Replace("\r\n", "\n")));
    }
    public static ConversionIssue Block(string path, string reason, string action) => new($"{path}: {reason}", action, true);
    public static void Distinct(List<string> list)
    {
        var values = list.Distinct(StringComparer.Ordinal).ToArray(); list.Clear(); list.AddRange(values);
    }
}
