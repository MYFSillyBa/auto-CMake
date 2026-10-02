using System.Xml.Linq;
using System.Xml.Schema;
using CMakeDapLink.Core;

namespace CMakeDapLink.App;

public sealed partial class MainForm
{
    private static string? FindConversionKeil()
    {
        var candidates = new List<string>();
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            if (!string.IsNullOrWhiteSpace(directory)) candidates.Add(Path.Combine(directory.Trim('"'), "UV4.exe"));
        foreach (var drive in DriveInfo.GetDrives().Where(x => x.IsReady && x.DriveType == DriveType.Fixed))
        {
            try
            {
                foreach (var directory in Directory.EnumerateDirectories(drive.RootDirectory.FullName, "Keil*", SearchOption.TopDirectoryOnly))
                    candidates.Add(Path.Combine(directory, "UV4", "UV4.exe"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return candidates.Distinct(StringComparer.OrdinalIgnoreCase).FirstOrDefault(File.Exists);
    }
    private async Task<(bool Compiled, string Details)> VerifyConvertedMdkAsync(string path)
    {
        var document = XDocument.Load(path);
        var projectDirectory = Path.GetDirectoryName(path)!;
        foreach (var file in document.Descendants("FilePath"))
        {
            var source = Path.GetFullPath(file.Value.Replace('\\', Path.DirectorySeparatorChar), projectDirectory);
            if (!File.Exists(source)) throw new InvalidOperationException("MDK 引用的文件不存在：" + source);
        }
        var keil = FindConversionKeil();
        if (keil == null) return (false, "MDK 工程文件和源文件引用检查通过。未找到 Keil 编译环境，尚未运行 MDK 编译；转换本身不需要安装或启动 CubeMX。");
        var schema = Path.Combine(Path.GetDirectoryName(keil)!, "project_projx.xsd");
        if (File.Exists(schema))
        {
            var schemas = new XmlSchemaSet(); schemas.Add(null, schema);
            document.Validate(schemas, (_, e) => { if (e.Severity == XmlSeverityType.Error) throw new InvalidOperationException("MDK 工程格式检查：" + e.Message); });
        }
        _conversionStage.Text = "正在通过本机 Keil 编译验证 MDK 工程…"; LayoutConversionPage();
        var log = Path.Combine(Path.GetTempPath(), "cmake-mdk-build-" + Guid.NewGuid().ToString("N") + ".log");
        try
        {
            var name = document.Descendants("TargetName").Single().Value;
            var result = await ProcessTools.RunAsync(keil, ["-b", path, "-j0", "-sg", "-t", name, "-o", log], projectDirectory, TimeSpan.FromMinutes(15), AppendConversion);
            if (File.Exists(log)) AppendConversion(await File.ReadAllTextAsync(log));
            if (result.ExitCode > 1 || result.ExitCode < 0)
                throw new InvalidOperationException($"MDK 工程已生成，但 Keil 编译未通过（退出码 {result.ExitCode}）。请查看日志并核对芯片 Pack、Arm Compiler 6 和许可证；不需要调用 CubeMX。");
            var outputDirectory = document.Descendants("OutputDirectory").FirstOrDefault()?.Value ?? ".";
            var outputName = document.Descendants("OutputName").FirstOrDefault()?.Value ?? name;
            var axf = Path.GetFullPath(Path.Combine(outputDirectory, outputName + ".axf").Replace('\\', Path.DirectorySeparatorChar), projectDirectory);
            if (!File.Exists(axf)) throw new InvalidOperationException("Keil 未报告编译错误，但未找到预期 AXF 输出：" + axf + "。请核对日志中的输出设置。");
            return (true, "Keil 实际编译通过。" + (result.ExitCode == 1 ? "有编译警告，可展开日志查看。" : "") + "\n" + FirmwareSummary(axf, ConversionConfiguration));
        }
        finally { if (File.Exists(log)) File.Delete(log); }
    }
}
