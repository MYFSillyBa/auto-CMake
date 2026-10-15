namespace CMakeDapLink.Core;

public sealed record FileChangeMetadata(string Path, string Kind, long BeforeSize, long AfterSize);
public sealed record ChangeRecordMetadata(string Id, string Label, DateTimeOffset CreatedUtc,
    DateTimeOffset? RestoredUtc, string Status, string? RestorationOf, IReadOnlyList<FileChangeMetadata> Changes);
