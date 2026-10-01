namespace CMakeDapLink.Core;

public sealed record ToolCommand(string Executable, IReadOnlyList<string> Arguments, string WorkingDirectory, string PathPrefix);

public sealed record CMakeBuildPlan(ToolCommand Configure, ToolCommand Build)
{
    public static CMakeBuildPlan Create(SetupOptions options)
    {
        var root = Path.GetFullPath(options.Root);
        var buildDirectory = options.BuildDirectory ?? Path.Combine(root, "build", "daplink-debug");
        var pathPrefix = string.Join(Path.PathSeparator,
            new[] { options.Compiler, options.Ninja, options.CMake }.Select(Path.GetDirectoryName)
                .Where(x => !string.IsNullOrEmpty(x)).Distinct(StringComparer.OrdinalIgnoreCase));
        var configureArgs = options.ConfigurePreset != null
            ? new List<string> { "--preset", options.ConfigurePreset }
            : new List<string> { "-S", root, "-B", buildDirectory, "-G", "Ninja", "-DCMAKE_BUILD_TYPE=Debug",
                "-DCMAKE_MAKE_PROGRAM=" + options.Ninja };
        if (options.ConfigurePreset == null && options.ToolchainFile != null)
            configureArgs.Add("-DCMAKE_TOOLCHAIN_FILE=" + options.ToolchainFile);
        var buildArgs = options.BuildPreset != null
            ? new List<string> { "--build", "--preset", options.BuildPreset }
            : new List<string> { "--build", buildDirectory, "--parallel" };
        return new(new(options.CMake, configureArgs, root, pathPrefix), new(options.CMake, buildArgs, root, pathPrefix));
    }

    public static string FindSingleElf(string buildDirectory)
    {
        if (!Directory.Exists(buildDirectory)) throw new InvalidOperationException("构建目录不存在：" + buildDirectory);
        var files = Directory.EnumerateFiles(buildDirectory, "*.elf", SearchOption.AllDirectories).ToArray();
        if (files.Length != 1)
            throw new InvalidOperationException($"构建目录中找到 {files.Length} 个 ELF 文件；需要恰好一个，才能生成安全的烧录任务。");
        return Path.GetFullPath(files[0]);
    }
}
