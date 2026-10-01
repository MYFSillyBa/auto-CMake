using System.Text.Json;
using System.Text.RegularExpressions;

namespace CMakeDapLink.Core;

public sealed record ProjectInfo(string Root, bool IsCMakeProject, string? Chip, string? TargetScript,
    string? ConfigurePreset, string? BuildPreset, string? BuildDirectory, string? ToolchainFile,
    IReadOnlyList<string> Notes);

public static class ProjectInspector
{
    private const string ChipPattern = @"STM32[A-Z]{1,2}[0-9][A-Z0-9]{2,12}";
    private static readonly (string Prefix, string Script)[] Targets =
    [
        ("STM32H7", "target/stm32h7x.cfg"), ("STM32F7", "target/stm32f7x.cfg"),
        ("STM32F4", "target/stm32f4x.cfg"), ("STM32F3", "target/stm32f3x.cfg"),
        ("STM32F2", "target/stm32f2x.cfg"), ("STM32F1", "target/stm32f1x.cfg"),
        ("STM32F0", "target/stm32f0x.cfg"), ("STM32G4", "target/stm32g4x.cfg"),
        ("STM32G0", "target/stm32g0x.cfg"), ("STM32L4", "target/stm32l4x.cfg"),
        ("STM32L0", "target/stm32l0.cfg"), ("STM32U5", "target/stm32u5x.cfg")
    ];

    public static ProjectInfo Inspect(string root)
    {
        root = Path.GetFullPath(root);
        var notes = new List<string>();
        var cmakeFile = Path.Combine(root, "CMakeLists.txt");
        if (!File.Exists(cmakeFile))
            return new(root, false, null, null, null, null, null, null, ["未找到根目录 CMakeLists.txt。请选择 CMake 工程根目录。"]);

        var content = File.ReadAllText(cmakeFile);
        var ioc = Directory.EnumerateFiles(root, "*.ioc", SearchOption.TopDirectoryOnly).FirstOrDefault();
        string? chip = null;
        if (ioc != null)
        {
            var iocContent = File.ReadAllText(ioc);
            foreach (var key in new[] { "Mcu.CPN", "Mcu.Name", "ProjectManager.DeviceId", "Mcu.UserName" })
            {
                var match = Regex.Match(iocContent, @"^\s*" + Regex.Escape(key) + @"\s*=\s*(" + ChipPattern + @")", RegexOptions.IgnoreCase | RegexOptions.Multiline);
                if (match.Success) { chip = match.Groups[1].Value.ToUpperInvariant(); break; }
            }
        }
        chip ??= Regex.Match(content, ChipPattern, RegexOptions.IgnoreCase).Value.ToUpperInvariant();
        if (chip.Length == 0) chip = null;
        var target = chip == null ? null : Targets.FirstOrDefault(x => chip.StartsWith(x.Prefix, StringComparison.OrdinalIgnoreCase)).Script;
        if (target == null) notes.Add("芯片型号无法可靠映射到 OpenOCD target，请手动填写 target/*.cfg。");

        string? configurePreset = null, buildPreset = null, buildDir = null;
        var presetFile = Path.Combine(root, "CMakePresets.json");
        if (File.Exists(presetFile))
        {
            try
            {
                using var json = JsonDocument.Parse(File.ReadAllText(presetFile), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
                if (json.RootElement.TryGetProperty("configurePresets", out var presets) && presets.ValueKind == JsonValueKind.Array)
                {
                    var allEntries = presets.EnumerateArray().Where(x => x.TryGetProperty("name", out _)).ToArray();
                    var entries = allEntries.Where(x => !x.TryGetProperty("hidden", out var hidden) || hidden.ValueKind != JsonValueKind.True).ToArray();
                    var chosen = entries.FirstOrDefault(x => x.GetProperty("name").GetString()?.Contains("debug", StringComparison.OrdinalIgnoreCase) == true);
                    if (chosen.ValueKind == JsonValueKind.Undefined) chosen = entries.FirstOrDefault();
                    if (chosen.ValueKind != JsonValueKind.Undefined)
                    {
                        configurePreset = chosen.GetProperty("name").GetString();
                        buildDir = ResolveBinaryDir(root, chosen, allEntries, configurePreset!);
                        if (json.RootElement.TryGetProperty("buildPresets", out var builds) && builds.ValueKind == JsonValueKind.Array)
                        {
                            var match = builds.EnumerateArray().FirstOrDefault(x =>
                                (!x.TryGetProperty("hidden", out var hidden) || hidden.ValueKind != JsonValueKind.True) &&
                                x.TryGetProperty("configurePreset", out var cp) && cp.GetString() == configurePreset);
                            if (match.ValueKind != JsonValueKind.Undefined) buildPreset = match.GetProperty("name").GetString();
                        }
                        if (buildPreset == null && buildDir == null)
                            notes.Add("预设没有可解析的 binaryDir 或 buildPreset，需调整 CMakePresets.json 后再配置。");
                    }
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                notes.Add("CMakePresets.json 无法读取：" + ex.Message);
            }
        }
        if (configurePreset == null) buildDir = Path.Combine(root, "build", "daplink-debug");
        var toolchain = Directory.EnumerateFiles(root, "*.cmake", SearchOption.AllDirectories)
            .Where(x => !x.Contains(Path.DirectorySeparatorChar + "build" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            .Where(x => Path.GetFileName(x).Contains("toolchain", StringComparison.OrdinalIgnoreCase) ||
                Path.GetFileName(x).Contains("arm-none-eabi", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault();
        if (toolchain == null && configurePreset == null) notes.Add("未找到 toolchain 文件；将使用工程自身的 CMake 配置。");
        return new(root, true, chip, target, configurePreset, buildPreset, buildDir, toolchain, notes);
    }

    private static string? ResolveBinaryDir(string root, JsonElement preset, JsonElement[] presets, string name)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? Find(JsonElement current)
        {
            var key = current.GetProperty("name").GetString()!;
            if (!seen.Add(key)) return null;
            if (current.TryGetProperty("binaryDir", out var binary) && binary.ValueKind == JsonValueKind.String)
                return binary.GetString();
            if (current.TryGetProperty("inherits", out var inherits))
            {
                var parents = inherits.ValueKind == JsonValueKind.String ? [inherits.GetString()!] :
                    inherits.ValueKind == JsonValueKind.Array ? inherits.EnumerateArray().Select(x => x.GetString()!).ToArray() : [];
                foreach (var parent in parents)
                {
                    var item = presets.FirstOrDefault(x => x.GetProperty("name").GetString() == parent);
                    if (item.ValueKind != JsonValueKind.Undefined)
                    {
                        var result = Find(item);
                        if (result != null) return result;
                    }
                }
            }
            return null;
        }
        var value = Find(preset);
        if (value == null) return null;
        value = value.Replace("${sourceDir}", root).Replace("${sourceParentDir}", Directory.GetParent(root)?.FullName ?? root)
            .Replace("${sourceDirName}", Path.GetFileName(root)).Replace("${presetName}", name);
        return value.Contains("${", StringComparison.Ordinal) ? null : Path.GetFullPath(Path.IsPathRooted(value) ? value : Path.Combine(root, value));
    }
}
