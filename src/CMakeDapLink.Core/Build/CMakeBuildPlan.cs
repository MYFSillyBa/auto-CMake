namespace CMakeDapLink.Core;

public sealed record ToolCommand(string Executable, IReadOnlyList<string> Arguments, string WorkingDirectory, string PathPrefix);

public sealed record CMakeBuildPlan(ToolCommand Configure, ToolCommand Build)
{
    public string BuildDirectory { get; init; } = "";
    public string BuildConfiguration { get; init; } = "Debug";

    public void PrepareArtifactQuery() => CurrentFirmwareArtifacts.PrepareQuery(BuildDirectory);

    public static CMakeBuildPlan Create(SetupOptions options)
    {
        var root = Path.GetFullPath(options.Root);
        var buildDirectory = options.BuildDirectory ??
            (options.ConfigurePreset == null ? null : ProjectInspector.Inspect(root, options.ConfigurePreset).BuildDirectory) ??
            Path.Combine(root, "build", "daplink-debug");
        var pathPrefix = string.Join(Path.PathSeparator,
            new[] { options.Compiler, options.Ninja, options.CMake }.Select(Path.GetDirectoryName)
                .Where(x => !string.IsNullOrEmpty(x)).Distinct(StringComparer.OrdinalIgnoreCase));
        var configureArgs = options.ConfigurePreset != null
            ? new List<string> { "--preset", options.ConfigurePreset }
            : new List<string> { "-S", root, "-B", buildDirectory, "-G", "Ninja", "-DCMAKE_BUILD_TYPE=" + options.BuildConfiguration,
                "-DCMAKE_MAKE_PROGRAM=" + options.Ninja };
        if (options.ConfigurePreset == null && options.ToolchainFile != null)
            configureArgs.Add("-DCMAKE_TOOLCHAIN_FILE=" + options.ToolchainFile);
        var buildArgs = options.BuildPreset != null
            ? new List<string> { "--build", "--preset", options.BuildPreset }
            : new List<string> { "--build", buildDirectory, "--parallel" };
        if (!string.IsNullOrWhiteSpace(options.BuildConfiguration))
        {
            buildArgs.Add("--config");
            buildArgs.Add(options.BuildConfiguration);
        }
        return new(new(options.CMake, configureArgs, root, pathPrefix), new(options.CMake, buildArgs, root, pathPrefix))
        {
            BuildDirectory = Path.GetFullPath(buildDirectory, root),
            BuildConfiguration = options.BuildConfiguration
        };
    }

    public static IReadOnlyList<string> FindElfs(string buildDirectory, string configuration = "Debug") =>
        CurrentFirmwareArtifacts.Find(buildDirectory, configuration);

    public static string FindSingleElf(string buildDirectory)
    {
        var files = FindElfs(buildDirectory);
        if (files.Count != 1)
            throw new InvalidOperationException($"构建目录中找到 {files.Count} 个 ELF 文件；请明确选择要烧录的固件。");
        return files[0];
    }
}
