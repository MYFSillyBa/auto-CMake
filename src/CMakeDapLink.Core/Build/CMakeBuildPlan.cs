namespace CMakeDapLink.Core;

public sealed record ToolCommand(string Executable, IReadOnlyList<string> Arguments, string WorkingDirectory, string PathPrefix);

public sealed record CMakeBuildPlan(ToolCommand Configure, ToolCommand Build)
{
    public string BuildDirectory { get; init; } = "";
    public string BuildConfiguration { get; init; } = "Debug";
    internal BuildToolSelection? ToolSelection { get; init; }

    public void PrepareArtifactQuery(Action<string>? report = null)
    {
        ToolSelection?.Prepare(Configure, report);
        CurrentFirmwareArtifacts.PrepareQuery(BuildDirectory);
    }

    public ToolCommand? PrepareToolSelection() => ToolSelection?.PrepareCommand(Configure);

    public static CMakeBuildPlan Create(SetupOptions options)
    {
        CMakeConfigurations.Validate(options.BuildConfiguration);
        OpenOcdProbe.Validate(options);
        var root = Path.GetFullPath(options.Root);
        var project = options.ConfigurePreset == null ? null : ProjectInspector.Inspect(root, options.ConfigurePreset);
        var preset = project?.ConfigurePresets.FirstOrDefault(x => x.Name == options.ConfigurePreset);
        var buildDirectory = options.BuildDirectory ?? project?.BuildDirectory ??
            Path.Combine(root, "build", "daplink-debug");
        var pathPrefix = string.Join(Path.PathSeparator,
            new[] { options.Compiler, options.Ninja, options.CMake }.Select(Path.GetDirectoryName)
                .Where(x => !string.IsNullOrEmpty(x)).Distinct(StringComparer.OrdinalIgnoreCase));
        var selection = new BuildToolSelection(options, buildDirectory);
        var configureArgs = options.ConfigurePreset != null
            ? new List<string> { "--preset", options.ConfigurePreset }
            : new List<string> { "-S", root, "-B", buildDirectory, "-G", "Ninja", "-DCMAKE_BUILD_TYPE=" + options.BuildConfiguration,
                "-DCMAKE_MAKE_PROGRAM=" + options.Ninja };
        // --toolchain overrides even a preset's toolchainFile field. The wrapper
        // retains the original chain and binds compilers to the user's selection.
        configureArgs.AddRange(["--toolchain", selection.ToolchainPath]);
        if (options.ConfigurePreset != null)
        {
            configureArgs.Add("-DCMAKE_MAKE_PROGRAM=" + options.Ninja);
            if (preset != null && !preset.IsMultiConfig && string.IsNullOrWhiteSpace(preset.ConfigureConfiguration))
                configureArgs.Add("-DCMAKE_BUILD_TYPE=" + options.BuildConfiguration);
        }
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
            BuildConfiguration = options.BuildConfiguration,
            ToolSelection = selection
        };
    }

    public static IReadOnlyList<string> FindElfs(string buildDirectory, string configuration = "Debug", string? sourceRoot = null) =>
        CurrentFirmwareArtifacts.Find(buildDirectory, configuration, sourceRoot);

    public static string FindSingleElf(string buildDirectory)
    {
        var files = FindElfs(buildDirectory);
        if (files.Count != 1)
            throw new InvalidOperationException($"构建目录中找到 {files.Count} 个 ELF 文件；请明确选择要烧录的固件。");
        return files[0];
    }
}
