namespace CMakeDapLink.App;

internal static class AppIcon
{
    public static Icon Chip { get; } = Load();

    private static Icon Load()
    {
        using var stream = typeof(AppIcon).Assembly.GetManifestResourceStream("CMakeDapLink.ChipIcon")
            ?? throw new InvalidOperationException("程序缺少芯片图标资源。");
        using var icon = new Icon(stream);
        return (Icon)icon.Clone();
    }
}
