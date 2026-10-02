using System.Text.Json;
using System.Text.RegularExpressions;

namespace CMakeDapLink.Core;

public sealed record ProjectInfo(string Root, bool IsCMakeProject, string? Chip, string? TargetScript,
    string? ConfigurePreset, string? BuildPreset, string? BuildDirectory, string? ToolchainFile,
    IReadOnlyList<string> Notes)
{
    public string ChipEvidence { get; init; } = "";
    public IReadOnlyList<string> ChipCandidates { get; init; } = [];
    public IReadOnlyList<ConfigurePresetInfo> ConfigurePresets { get; init; } = [];
}

public static class ProjectInspector
{
    private const string ChipPattern = @"\bSTM32[A-Z]{1,2}[0-9][A-Z0-9]{2,12}\b";
    private static readonly (string Prefix, string Script)[] Targets =
    [
        ("STM32H7", "target/stm32h7x.cfg"), ("STM32F7", "target/stm32f7x.cfg"),
        ("STM32F4", "target/stm32f4x.cfg"), ("STM32F3", "target/stm32f3x.cfg"),
        ("STM32F2", "target/stm32f2x.cfg"), ("STM32F1", "target/stm32f1x.cfg"),
        ("STM32F0", "target/stm32f0x.cfg"), ("STM32G4", "target/stm32g4x.cfg"),
        ("STM32G0", "target/stm32g0x.cfg"), ("STM32L4", "target/stm32l4x.cfg"),
        ("STM32L0", "target/stm32l0.cfg"), ("STM32U5", "target/stm32u5x.cfg")
    ];
    private static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase)
        { ".git", ".vs", ".vscode", "build", "bin", "obj", "Drivers", "CMSIS", "Middlewares", "third_party", "node_modules" };

    public static ProjectInfo Inspect(string root, string? selectedPreset = null, bool usePresets = true)
    {
        root = Path.GetFullPath(root);
        var notes = new List<string>();
        if (!File.Exists(Path.Combine(root, "CMakeLists.txt")))
            return new(root, false, null, null, null, null, null, null, ["未找到根目录 CMakeLists.txt。请选择 CMake 工程根目录。"]);

        var reader = new CMakePresetReader(root, notes);
        var presets = reader.Read();
        var chosen = !usePresets ? null : selectedPreset == null
            ? presets.FirstOrDefault(p => p.Name.Contains("debug", StringComparison.OrdinalIgnoreCase)) ?? presets.FirstOrDefault()
            : presets.FirstOrDefault(p => p.Name == selectedPreset);
        if (usePresets && selectedPreset != null && chosen == null) notes.Add("所选配置预设不可用：" + selectedPreset + "。请重新选择可见预设。");
        var buildDirectory = chosen?.BinaryDirectory;
        if (chosen == null) buildDirectory = Path.Combine(root, "build", "daplink-debug");

        var clues = new List<(string Chip, string Evidence)>();
        void AddChips(string value, string evidence)
        {
            value = Regex.Replace(value, @"(?:-D|/D)(?=STM32)", " ", RegexOptions.IgnoreCase);
            foreach (Match match in Regex.Matches(value, ChipPattern, RegexOptions.IgnoreCase))
                clues.Add((match.Value.ToUpperInvariant(), evidence + "：" + match.Value.ToUpperInvariant()));
        }
        if (chosen != null)
            foreach (var definition in reader.ChipDefinitions(chosen.Name)) AddChips(definition.Value, definition.Source);
        foreach (var ioc in Directory.EnumerateFiles(root, "*.ioc", SearchOption.TopDirectoryOnly).Order(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var content = File.ReadAllText(ioc);
                foreach (var key in new[] { "Mcu.CPN", "Mcu.Name", "ProjectManager.DeviceId", "Mcu.UserName" })
                {
                    var match = Regex.Match(content, @"^\s*" + Regex.Escape(key) + @"\s*=\s*(" + ChipPattern + ")", RegexOptions.IgnoreCase | RegexOptions.Multiline);
                    if (!match.Success) continue;
                    AddChips(match.Groups[1].Value, Path.GetFileName(ioc) + " / " + key);
                    break;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { notes.Add(Path.GetFileName(ioc) + " 无法读取：" + ex.Message); }
        }
        var projectFiles = EnumerateProjectFiles(root, notes).ToArray();
        foreach (var file in projectFiles.Where(f => Path.GetFileName(f).Equals("CMakeLists.txt", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(f).Equals(".cmake", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                var content = File.ReadAllText(file);
                content = Regex.Replace(content, @"#\[(=*)\[[\s\S]*?\]\1\]", "");
                content = Regex.Replace(content, @"(?m)#.*$", "");
                AddChips(content, Path.GetRelativePath(root, file) + " / CMake 编译配置");
                foreach (Match startup in Regex.Matches(content, @"startup[_-](stm32[a-z0-9]+)\.(?:s|asm)", RegexOptions.IgnoreCase))
                    AddChips(startup.Groups[1].Value, Path.GetRelativePath(root, file) + " / 启动文件引用");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { notes.Add(Path.GetRelativePath(root, file) + " 无法读取：" + ex.Message); }
        }
        foreach (var file in projectFiles.Where(f => Regex.IsMatch(Path.GetFileName(f), @"^startup[_-]stm32[a-z0-9]+\.(?:s|asm)$", RegexOptions.IgnoreCase)))
        {
            var match = Regex.Match(Path.GetFileName(file), @"stm32[a-z0-9]+", RegexOptions.IgnoreCase);
            AddChips(match.Value, Path.GetRelativePath(root, file) + " / 启动文件");
        }
        ReadCompileCommands(root, buildDirectory, AddChips, notes);

        var candidates = new List<string>();
        foreach (var candidate in clues.Select(c => c.Chip).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(c => c.Length).ThenBy(c => c, StringComparer.OrdinalIgnoreCase))
        {
            if (!candidates.Any(existing => Compatible(existing, candidate))) candidates.Add(candidate);
        }
        candidates.Sort(StringComparer.OrdinalIgnoreCase);
        var chip = candidates.Count == 1 ? candidates[0] : null;
        var target = chip == null ? null : TargetForChip(chip);
        if (candidates.Count > 1) notes.Add("芯片识别线索存在冲突（" + string.Join("、", candidates) + "），请根据证据确认目标芯片；未自动选择。");
        if (target == null) notes.Add("芯片型号无法可靠映射到 OpenOCD target，请手动填写 target/*.cfg。");

        var toolchain = chosen?.ToolchainFile;
        if (chosen == null)
            toolchain = projectFiles.Where(f => Path.GetExtension(f).Equals(".cmake", StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault(f => Path.GetFileName(f).Contains("toolchain", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(f).Contains("arm-none-eabi", StringComparison.OrdinalIgnoreCase));
        if (toolchain == null && chosen == null) notes.Add("未找到 toolchain 文件；将使用工程自身的 CMake 配置。");
        return new(root, true, chip, target, chosen?.Name, chosen?.BuildPreset, buildDirectory, toolchain, notes)
        {
            ChipEvidence = string.Join(Environment.NewLine, clues.Select(c => c.Evidence).Distinct(StringComparer.Ordinal)),
            ChipCandidates = candidates.AsReadOnly(), ConfigurePresets = presets
        };
    }

    public static string? TargetForChip(string chip) => Targets
        .FirstOrDefault(x => chip.Trim().StartsWith(x.Prefix, StringComparison.OrdinalIgnoreCase)).Script;

    private static bool Compatible(string first, string second)
    {
        var a = first.TrimEnd('X'); var b = second.TrimEnd('X');
        return a.StartsWith(b, StringComparison.OrdinalIgnoreCase) || b.StartsWith(a, StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> EnumerateProjectFiles(string directory, List<string> notes)
    {
        string[] files, subdirectories;
        try { files = Directory.GetFiles(directory); subdirectories = Directory.GetDirectories(directory); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { notes.Add("目录无法读取：" + directory + "；" + ex.Message); yield break; }
        foreach (var file in files.Order(StringComparer.OrdinalIgnoreCase)) yield return file;
        foreach (var subdirectory in subdirectories.Order(StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(subdirectory);
            if (SkippedDirectories.Contains(name) || name.StartsWith("cmake-build-", StringComparison.OrdinalIgnoreCase) ||
                File.Exists(Path.Combine(subdirectory, "CMakeCache.txt")) || (File.GetAttributes(subdirectory) & FileAttributes.ReparsePoint) != 0) continue;
            foreach (var file in EnumerateProjectFiles(subdirectory, notes)) yield return file;
        }
    }

    private static void ReadCompileCommands(string root, string? buildDirectory, Action<string, string> add, List<string> notes)
    {
        var path = buildDirectory == null ? null : Path.Combine(buildDirectory, "compile_commands.json");
        if (path == null || !File.Exists(path)) return;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.ValueKind != JsonValueKind.Array) return;
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var command = item.TryGetProperty("command", out var commandValue) && commandValue.ValueKind == JsonValueKind.String ? commandValue.GetString() : null;
                if (command == null && item.TryGetProperty("arguments", out var arguments) && arguments.ValueKind == JsonValueKind.Array)
                    command = string.Join(" ", arguments.EnumerateArray().Where(a => a.ValueKind == JsonValueKind.String).Select(a => a.GetString()));
                if (command == null) continue;
                foreach (Match macro in Regex.Matches(command, @"(?:-D|/D)\s*[""']?(STM32[A-Z]{1,2}[0-9][A-Z0-9]{2,12})\b", RegexOptions.IgnoreCase))
                    add(macro.Groups[1].Value, Path.GetRelativePath(root, path) + " / 实际编译宏");
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { notes.Add("compile_commands.json 无法读取：" + ex.Message); }
    }
}
