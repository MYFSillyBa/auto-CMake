using System.Text.RegularExpressions;

namespace CMakeDapLink.Core;

public sealed record OpenOcdProbe(string InterfaceScript, string Transport, string DisplayName)
{
    public static void Validate(SetupOptions options)
    {
        if (options.ProbeType is not ("cmsis-dap" or "stlink" or "jlink")) throw new ArgumentException("请选择 CMSIS-DAP、ST-Link 或 J-Link 探针。");
        if (options.AdapterSpeed is < 1 or > 50000) throw new ArgumentException("探针速度必须为 1–50000 kHz。");
        if (options.ResetMode is not ("default" or "connect-under-reset" or "sysresetreq")) throw new ArgumentException("请选择有效的复位模式。");
    }

    public static OpenOcdProbe Resolve(SetupOptions options)
    {
        Validate(options);
        var name = options.ProbeType switch { "stlink" => "stlink", "jlink" => "jlink", _ => "cmsis-dap" };
        var script = "interface/" + name + ".cfg";
        var path = Path.Combine(options.Scripts, script);
        ToolVersionManager.EnsureOrdinaryPath(path);
        if (!File.Exists(path)) throw new FileNotFoundException("探针接口脚本未找到：" + script, path);
        if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException("探针接口脚本超过大小限制。");
        var content = File.ReadAllText(path);
        var active = Regex.Replace(content, @"(?m)^\s*#.*$", "");
        var driver = Regex.Match(active, @"(?m)^\s*(?:adapter\s+driver|interface)\s+([\w-]+)\b").Groups[1].Value;
        var transport = options.ProbeType switch
        {
            "cmsis-dap" when driver == "cmsis-dap" => "swd",
            "jlink" when driver == "jlink" => "swd",
            "stlink" when driver == "hla" => "hla_swd",
            "stlink" when driver == "st-link" && Regex.IsMatch(content, @"\btransport\s+select\s+swd\b") => "swd",
            "stlink" when driver == "st-link" && Regex.IsMatch(content, @"\btransport\s+select\s+dapdirect_swd\b") => "dapdirect_swd",
            _ => throw new InvalidDataException("探针脚本的驱动或 SWD 传输无法确认，请选择兼容的完整 OpenOCD 脚本包。")
        };
        if (transport == "hla_swd" && options.ResetMode == "sysresetreq")
            throw new ArgumentException("当前 ST-Link HLA 驱动不支持 sysresetreq，请使用默认复位或更新 OpenOCD。");
        return new(script, transport, options.ProbeType switch { "stlink" => "ST-Link", "jlink" => "J-Link", _ => "DAPLINK" });
    }

    public IReadOnlyList<string> Arguments(SetupOptions options)
    {
        var args = new List<string> { "-s", options.Scripts, "-f", InterfaceScript, "-c", "transport select " + Transport, "-f", options.TargetScript,
            "-c", "adapter speed " + options.AdapterSpeed };
        if (options.ResetMode == "connect-under-reset") args.AddRange(["-c", "reset_config srst_only srst_nogate connect_assert_srst"]);
        if (options.ResetMode == "sysresetreq") args.AddRange(["-c", "cortex_m reset_config sysresetreq"]);
        return args;
    }

    public static string TclPath(string path) => "\"" + path.Replace('\\', '/').Replace("$", "\\$", StringComparison.Ordinal)
        .Replace("[", "\\[", StringComparison.Ordinal).Replace("]", "\\]", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
}
