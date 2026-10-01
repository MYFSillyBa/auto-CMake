namespace CMakeDapLink.Core;

/// <summary>A proposed file operation. Null represents a file that does not exist.</summary>
public sealed record FileChange(string Path, byte[]? Before, byte[]? After);

public sealed record ChangeRecord(string Id, string Root, string Label, DateTimeOffset CreatedUtc,
    IReadOnlyList<FileChange> Changes, DateTimeOffset? RestoredUtc = null)
{
    public string? RestorationOf { get; init; }
    public string Status { get; init; } = "Complete";
}

public sealed class ChangeConflictException : IOException
{
    public IReadOnlyList<FileChange> Conflicts { get; }

    public ChangeConflictException(string message, IReadOnlyList<FileChange> conflicts) : base(message)
        => Conflicts = conflicts;
}
