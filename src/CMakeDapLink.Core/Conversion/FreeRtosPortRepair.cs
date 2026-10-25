using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace CMakeDapLink.Core;

public sealed record FreeRtosRepairProgress(int Percent, string Stage);
public sealed record FreeRtosRepairInput(string Path, string Sha256);
public sealed record FreeRtosRepairPlan(ConversionRequest Request, IReadOnlyList<FileChange> Changes,
    IReadOnlyList<FreeRtosRepairInput> Inputs, IReadOnlyList<string> Notes, string Summary);

/// <summary>Prepares an exact-version GNU port pair without modifying the project.</summary>
public static class FreeRtosPortRepair
{
    private sealed record Port(string Original, string Directory, string Version, string Name);
    private sealed record Context(ConversionRequest Request, List<Port> Ports, List<FreeRtosRepairInput> Inputs);
    private static readonly string[] Pair = ["port.c", "portmacro.h"];
    private static readonly UTF8Encoding Utf8 = new(false, true);
    internal const int MaximumFileBytes = 512 * 1024;

    public static async Task<FreeRtosRepairPlan> PreviewAsync(ConversionRequest request, string? localDirectory = null,
        IProgress<FreeRtosRepairProgress>? progress = null, CancellationToken cancellation = default, HttpClient? client = null)
    {
        cancellation.ThrowIfCancellationRequested();
        progress?.Report(new(5, "检查 FreeRTOS 内核版本与移植层"));
        var context = Analyze(request);
        var changes = new List<FileChange>(); var notes = new List<string>();
        if (context.Ports.Count == 0) return new(context.Request, changes, context.Inputs, notes, "当前目标无需补全 FreeRTOS GNU 移植层。");
        if (context.Ports.Count > 4) throw new InvalidDataException("当前目标引用的 FreeRTOS 移植层过多，请分别处理。");
        if (localDirectory != null && context.Ports.Select(x => (x.Name, x.Version)).Distinct().Count() > 1)
            throw new InvalidDataException("本地导入一次只能补全一种版本和核心的 FreeRTOS 移植层。");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(90));
        try
        {
            for (var index = 0; index < context.Ports.Count; index++)
            {
                deadline.Token.ThrowIfCancellationRequested(); var port = context.Ports[index];
                progress?.Report(new(15 + index * 60 / context.Ports.Count, "读取匹配版本的 FreeRTOS GNU 移植文件"));
                Dictionary<string, byte[]> files;
                if (localDirectory != null)
                {
                    var folder = Path.GetFullPath(localDirectory); Ordinary(folder);
                    files = Pair.ToDictionary(x => x, x => Read(Path.Combine(folder, x), MaximumFileBytes));
                    notes.Add($"本地导入：FreeRTOS V{port.Version} / {port.Name}");
                }
                else
                {
                    var download = await FreeRtosOfficialSource.GetAsync(port.Version, port.Name, client, deadline.Token).ConfigureAwait(false);
                    files = download.Files;
                    notes.Add($"官方来源：FreeRTOS/FreeRTOS-Kernel / {download.Tag} / {download.Commit}");
                }
                ValidatePair(files, port.Version, port.Name);
                foreach (var name in Pair)
                {
                    var target = Child(context.Request.Root, Path.Combine(port.Directory, name));
                    if (File.Exists(target))
                    {
                        var current = Read(target, MaximumFileBytes);
                        if (!current.SequenceEqual(files[name])) throw new IOException($"现有文件与所选移植层不同，保留原文件：{target}");
                        AddInput(context.Inputs, target);
                    }
                    else changes.Add(new(target, null, files[name]));
                }
                notes.Add($"内核与移植层版本一致：V{port.Version} / {port.Name}");
            }
            cancellation.ThrowIfCancellationRequested();
            VerifyInputs(context.Request.Root, context.Inputs);
            progress?.Report(new(100, "补全文件已校验，等待确认写入"));
            notes.Add("只添加缺失的 GCC 移植文件，保留内核和原 Keil/RVDS 文件；写入后需要重新预览转换并实际编译。");
            return new(context.Request, changes, context.Inputs, notes, $"FreeRTOS 移植层补全：新增 {changes.Count} 个文件。");
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        { throw new TimeoutException("FreeRTOS 移植层读取超时，请重试或使用本地移植层导入。"); }
    }

    public static ChangeRecord? Apply(FreeRtosRepairPlan plan)
    {
        if (plan.Changes.Count == 0) return null;
        if (plan.Changes.Count > 8 || plan.Inputs.Count is < 1 or > 48) throw new InvalidDataException("FreeRTOS 补全计划无效，请重新预览。");
        var context = Analyze(plan.Request);
        var allowed = context.Ports.SelectMany(p => Pair.Select(n => Path.GetFullPath(Path.Combine(p.Directory, n)))).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var change in plan.Changes)
            if (change.Before != null || change.After == null || change.After.Length > MaximumFileBytes || !allowed.Contains(Child(context.Request.Root, change.Path)))
                throw new InvalidDataException("FreeRTOS 补全只允许添加所选目标缺失的移植文件。");
        var locks = new List<FileStream>();
        try
        {
            // Hold read-only handles during the small atomic write batch; editors cannot change version inputs midway.
            foreach (var input in plan.Inputs)
            {
                var path = Child(context.Request.Root, input.Path);
                var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); locks.Add(stream);
                if (!Hash(stream).Equals(input.Sha256, StringComparison.OrdinalIgnoreCase)) throw Stale();
            }
            foreach (var input in context.Inputs)
                if (!plan.Inputs.Any(x => Path.GetFullPath(x.Path).Equals(input.Path, StringComparison.OrdinalIgnoreCase) && x.Sha256 == input.Sha256)) throw Stale();
            foreach (var port in context.Ports)
            {
                var pair = Pair.ToDictionary(name => name, name =>
                {
                    var path = Path.Combine(port.Directory, name);
                    return plan.Changes.SingleOrDefault(x => Path.GetFullPath(x.Path).Equals(path, StringComparison.OrdinalIgnoreCase))?.After ?? Read(path, MaximumFileBytes);
                });
                ValidatePair(pair, port.Version, port.Name);
            }
            return ChangeHistory.Apply(context.Request.Root, "补全 FreeRTOS GNU 移植层", plan.Changes);
        }
        finally { foreach (var stream in locks) stream.Dispose(); }
    }

    private static Context Analyze(ConversionRequest request)
    {
        if (request.Direction != ConversionDirection.MdkToCMake) throw new InvalidDataException("FreeRTOS 移植层补全仅用于 MDK 转 CMake。");
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.Root)); Ordinary(root);
        var inspection = CubeMxConverter.Inspect(root, ConversionDirection.MdkToCMake);
        if (inspection.Issues.Any(x => x.Blocking) || string.IsNullOrEmpty(inspection.Chip)) throw new InvalidDataException("请先选择可识别芯片的 CubeMX MDK 工程。");
        var source = request.SourceProject;
        if (string.IsNullOrWhiteSpace(source)) source = inspection.SourceProjects.Count == 1 ? inspection.SourceProjects[0] : throw new InvalidDataException("请明确选择 MDK 工程文件及目标后补全。");
        source = Child(root, source);
        if (!inspection.SourceProjects.Any(x => Path.GetFullPath(x).Equals(source, StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("所选 MDK 工程不属于当前工程。");
        if (!MdkConversionReader.Targets(source).Contains(request.Target, StringComparer.Ordinal)) throw new InvalidDataException("所选 MDK 构建目标不存在。");
        Read(source, 8 * 1024 * 1024);
        var project = MdkConversionReader.Read(root, inspection.Chip, source, request.Target);
        var inputs = new List<FreeRtosRepairInput>(); AddInput(inputs, source);
        foreach (var ioc in Directory.EnumerateFiles(root, "*.ioc", SearchOption.TopDirectoryOnly)) AddInput(inputs, ioc);
        var ports = new List<Port>();
        foreach (var original in project.FreeRtosPortSources.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            Child(root, original); Read(original, MaximumFileBytes);
            var originalMacro = Path.Combine(Path.GetDirectoryName(original)!, "portmacro.h"); Read(originalMacro, MaximumFileBytes);
            var match = Regex.Match(original.Replace('\\', '/'), @"/portable/RVDS/(?<name>[^/]+(?:/r0p1)?)/port\.c$", RegexOptions.IgnoreCase);
            if (!match.Success) throw new InvalidDataException("无法确认 FreeRTOS 移植层目录，请手工核对。");
            var name = match.Groups["name"].Value;
            var validCpu = name.ToUpperInvariant() switch
            {
                "ARM_CM0" => project.Cpu is "cortex-m0" or "cortex-m0plus",
                "ARM_CM3" => project.Cpu == "cortex-m3",
                "ARM_CM4F" => project.Cpu is "cortex-m4" or "cortex-m7" && project.Fpu != null,
                _ => false
            };
            if (!validCpu) throw new InvalidDataException($"此核心或移植层需要手工核对，不能自动补全：{name}");
            // Directory spelling is canonical in the upstream repository, independent of Windows casing.
            name = name.ToUpperInvariant();
            var sourceRoot = original.Replace('\\', '/')[..match.Index];
            var task = Child(root, Path.Combine(sourceRoot, "include/task.h"));
            var taskText = Utf8.GetString(Read(task, 2 * 1024 * 1024));
            var version = Regex.Match(taskText, "(?m)^\\s*#\\s*define\\s+tskKERNEL_VERSION_NUMBER\\s+\"V?(\\d+\\.\\d+\\.\\d+)\"").Groups[1].Value;
            var kernel = Child(root, Path.Combine(sourceRoot, "include/FreeRTOS.h")); Read(kernel, 2 * 1024 * 1024);
            if (version.Length == 0 || ConversionCompatibility.Version(original) != version || ConversionCompatibility.Version(originalMacro) != version || ConversionCompatibility.Version(kernel) != version)
                throw new InvalidDataException("FreeRTOS 内核与原移植层版本不一致或无法确认，不能自动补全。");
            if (ConversionCompatibility.GnuPort(original)) continue;
            foreach (var path in new[] { original, originalMacro, task, kernel }) AddInput(inputs, path);
            var configs = project.Includes.Select(x => Path.Combine(x, "FreeRTOSConfig.h"))
                .Concat(project.Sources.SelectMany(x => x.Includes).Select(x => Path.Combine(x, "FreeRTOSConfig.h")))
                .Append(Path.Combine(root, "Core/Inc/FreeRTOSConfig.h")).Select(Path.GetFullPath).Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (configs.Length > 1) throw new InvalidDataException("检测到多个 FreeRTOSConfig.h，请先明确所选目标的配置。");
            foreach (var config in configs)
            {
                Child(root, config); var text = Utf8.GetString(Read(config, 2 * 1024 * 1024)); AddInput(inputs, config);
                text = Regex.Replace(text, @"/\*[\s\S]*?\*/|//[^\r\n]*", "");
                var mpu = Regex.Match(text, @"(?m)^\s*#\s*define\s+configENABLE_MPU\s+([^\r\n]+)");
                if (mpu.Success && !Regex.IsMatch(mpu.Groups[1].Value.Trim(), @"^\(?\s*0[uUlL]*\s*\)?$"))
                    throw new InvalidDataException("MPU 配置需要专用移植层，不能自动套用普通 GNU port。");
            }
            var target = Child(root, Path.Combine(sourceRoot, "portable/GCC", name));
            if (Pair.All(n => File.Exists(Path.Combine(target, n))) && ConversionCompatibility.GnuPort(Path.Combine(target, "port.c")) &&
                Pair.All(n => ConversionCompatibility.Version(Path.Combine(target, n)) == version)) continue;
            ports.Add(new(original, target, version, name));
        }
        return new(request with { Root = root, SourceProject = source }, ports, inputs);
    }

    private static void ValidatePair(IReadOnlyDictionary<string, byte[]> files, string version, string port)
    {
        string Text(string name)
        {
            var bytes = files[name]; if (bytes.Length is < 16 or > MaximumFileBytes) throw new InvalidDataException("FreeRTOS 移植文件大小无效。");
            var value = Utf8.GetString(bytes);
            var found = Regex.Match(value, @"FreeRTOS(?:\s+Kernel)?\s+V?(\d+\.\d+\.\d+)", RegexOptions.IgnoreCase).Groups[1].Value;
            if (found != version) throw new InvalidDataException("下载或导入的 FreeRTOS 移植层版本与当前内核不一致。");
            return Regex.Replace(value, @"/\*[\s\S]*?\*/|//[^\r\n]*", "");
        }
        var body = Text("port.c"); var macro = Text("portmacro.h");
        if (!Regex.IsMatch(body, @"\b__attribute__\s*\(") || !Regex.IsMatch(body, @"\b(?:__asm|__asm__)\s+(?:volatile|__volatile__)\s*\(") ||
            !Regex.IsMatch(macro, @"\b(?:__attribute__|__asm|__asm__)\b") || Regex.IsMatch(body + macro, @"\b__forceinline\b|(?m)^\s*__asm\s+(?:void|BaseType_t)\b"))
            throw new InvalidDataException("移植文件不是可确认的 GNU C/汇编语法，请核对所选目录。");
        if (Regex.IsMatch(body, "(?m)^\\s*#\\s*include\\s+\"(?!FreeRTOS\\.h|task\\.h|portmacro\\.h)[^\"]+\""))
            throw new InvalidDataException("此 GNU 移植层还依赖额外源码或头文件，请手工导入完整移植目录。");
        // A version banner and GNU syntax alone cannot distinguish an M3 port from an M4F port.
        var text = (body + "\n" + macro).Replace("\\n", "\n", StringComparison.Ordinal).Replace("\\t", "\t", StringComparison.Ordinal);
        bool Has(string pattern) => Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase);
        var basepri = Has(@"\bbasepri\b"); var primask = Has(@"\bprimask\b");
        var saveFp = Has(@"\bvstmdb(?:eq)?\b"); var restoreFp = Has(@"\bvldmia(?:eq)?\b");
        var fpRegisters = Has(@"\bs16\s*-\s*s31\b");
        var architectureMatches = port switch
        {
            "ARM_CM0" => primask && !basepri && !saveFp && !restoreFp,
            "ARM_CM3" => basepri && !saveFp && !restoreFp,
            "ARM_CM4F" => basepri && saveFp && restoreFp && fpRegisters &&
                !Has(@"configASSERT\s*\(\s*portCPUID\s*==\s*portCORTEX_M7_r0p1_ID"),
            _ => false
        };
        if (!architectureMatches) throw new InvalidDataException($"移植文件的核心/浮点上下文特征与所需 {port} 不匹配，未写入工程。");
    }

    private static byte[] Read(string path, int limit)
    {
        Ordinary(path); using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > limit) throw new InvalidDataException("FreeRTOS 补全输入文件超过大小限制。");
        using var buffer = new MemoryStream(); stream.CopyTo(buffer);
        if (buffer.Length > limit) throw new InvalidDataException("FreeRTOS 补全输入文件超过大小限制。");
        return buffer.ToArray();
    }
    private static void AddInput(List<FreeRtosRepairInput> inputs, string path)
    {
        path = Path.GetFullPath(path); Ordinary(path);
        if (inputs.Any(x => x.Path.Equals(path, StringComparison.OrdinalIgnoreCase))) return;
        using var stream = File.OpenRead(path); inputs.Add(new(path, Hash(stream)));
    }
    private static string Hash(Stream stream) => Convert.ToHexString(SHA256.HashData(stream));
    private static void VerifyInputs(string root, IEnumerable<FreeRtosRepairInput> inputs)
    {
        foreach (var input in inputs)
        {
            using var stream = File.OpenRead(Child(root, input.Path));
            if (!Hash(stream).Equals(input.Sha256, StringComparison.OrdinalIgnoreCase)) throw Stale();
        }
    }
    private static IOException Stale() => new("FreeRTOS 或 MDK 配置在预览后发生变化，请重新预览补全。");
    private static string Child(string root, string path)
    {
        var full = Path.GetFullPath(path, root); var relative = Path.GetRelativePath(root, full);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar)) throw new IOException("FreeRTOS 补全路径必须在当前工程内。");
        Ordinary(full); return full;
    }
    private static void Ordinary(string path)
    {
        for (var item = Path.GetFullPath(path); item != null; item = Path.GetDirectoryName(item))
            try { if ((File.GetAttributes(item) & FileAttributes.ReparsePoint) != 0) throw new IOException("FreeRTOS 补全不支持符号链接或联接目录。"); }
            catch (FileNotFoundException) { } catch (DirectoryNotFoundException) { }
    }
}
