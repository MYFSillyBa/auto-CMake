using System.Diagnostics;
using System.Text;
using System.Xml.Linq;
using CMakeDapLink.Core;
using CMakeDapLink.TestFixtures;

internal static class ConversionCoverageChecks
{
    public static void Run(string root, Action<string, Action> check)
    {
        check("unused include paths are preserved once per path and converted firmware builds", () =>
        {
            var project = Path.Combine(root, "include coverage"); CubeMxFixture.Create(project);
            var mdk = Path.Combine(project, "MDK-ARM", CubeMxFixture.Target + ".uvprojx");
            var xml = XDocument.Load(mdk);
            foreach (var controls in xml.Descendants("VariousControls")) controls.Element("IncludePath")!.Value += ";../USB_DEVICE/App;../USB_DEVICE/Target";
            xml.Save(mdk); var original = File.ReadAllBytes(mdk);
            var plan = CubeMxConverter.CreatePlan(new(project, ConversionDirection.MdkToCMake, CubeMxFixture.Target));
            Require(plan.CanApply, plan);
            var notices = plan.Issues.Where(x => x.Message.Contains("包含目录不存在")).ToArray();
            Require(notices.Length == 2 && notices.All(x => !x.Blocking), plan);
            ChangeHistory.Apply(project, "include coverage", plan.Changes);
            Require(File.ReadAllText(plan.OutputProject).Contains("USB_DEVICE/App") && File.ReadAllBytes(mdk).SequenceEqual(original), plan);
            var build = Build(project);
            var reverse = CubeMxConverter.CreatePlan(new(project, ConversionDirection.CMakeToMdk, CubeMxFixture.Target, BuildDirectory: build.BuildDirectory));
            Require(reverse.CanApply && reverse.Issues.Count(x => x.Message.Contains("包含目录不存在")) == 2, reverse);
        });
        check("GNU port in RVDS and paired CMSIS DSP archive remain linked through conversion", () =>
        {
            var project = Path.Combine(root, "H7 DSP and port"); CubeMxFixture.Create(project);
            File.WriteAllText(Path.Combine(project, "ConversionDemo.ioc"), "Mcu.CPN=STM32H723VGT6\nMcu.Name=STM32H723VGTx\nProjectManager.ProjectName=ConversionDemo\nProjectManager.FirmwarePackage=STM32Cube FW_H7 V1.11.2\n");
            var mdk = Path.Combine(project, "MDK-ARM", CubeMxFixture.Target + ".uvprojx"); var xml = XDocument.Load(mdk);
            xml.Descendants("Device").Single().Value = "STM32H723VGTx";
            xml.Descendants("AdsCpuType").Single().Value = "\"Cortex-M7\"";
            xml.Descendants("RvdsVP").Single().Value = "3";
            var port = Path.Combine(project, "Middlewares", "Third_Party", "FreeRTOS", "Source", "portable", "RVDS", "ARM_CM4F"); Directory.CreateDirectory(port);
            var kernel = Path.GetFullPath("../../../include", port); Directory.CreateDirectory(kernel);
            File.WriteAllText(Path.Combine(kernel, "task.h"), "#define tskKERNEL_VERSION_NUMBER \"V10.3.1\"\n");
            File.WriteAllText(Path.Combine(port, "portmacro.h"), "#pragma once\n#define portINLINE __attribute__((always_inline)) inline\n");
            File.WriteAllText(Path.Combine(port, "port.c"), "/* FreeRTOS Kernel V10.3.1 */\n#include \"portmacro.h\"\n__attribute__((noinline)) int port_value(void) { __asm volatile(\"nop\"); return 1; }\n");
            xml.Descendants("Cads").Single().Element("VariousControls")!.Element("IncludePath")!.Value += ";../Middlewares/Third_Party/FreeRTOS/Source/portable/RVDS/ARM_CM4F";
            var arm = Path.Combine(project, "Drivers", "CMSIS", "DSP", "Lib", "ARM"); var gcc = Path.Combine(Path.GetDirectoryName(arm)!, "GCC"); Directory.CreateDirectory(arm); Directory.CreateDirectory(gcc);
            var dsp = Path.Combine(gcc, "libarm_cortexM7lfdp_math.a");
            var tools = EnvironmentScanner.Scan(); var toolDir = Path.GetDirectoryName(tools.Compiler)!;
            var c = Path.Combine(gcc, "dsp.c"); var obj = Path.ChangeExtension(c, ".o"); File.WriteAllText(c, "int dsp_value(void) { return 3; }\n");
            Run(tools.Compiler!, ["-mcpu=cortex-m7", "-mthumb", "-mfpu=fpv5-d16", "-mfloat-abi=hard", "-c", c, "-o", obj], project);
            Run(Path.Combine(toolDir, "arm-none-eabi-ar.exe"), ["rcs", dsp, obj], project);
            File.Copy(dsp, Path.Combine(arm, "arm_cortexM7lfdp_math.lib"));
            XElement source(string path, string type) => new("File", new XElement("FileName", Path.GetFileName(path)), new XElement("FileType", type), new XElement("FilePath", Path.GetRelativePath(Path.GetDirectoryName(mdk)!, path)));
            xml.Descendants("Files").First().Add(source(Path.Combine(port, "port.c"), "1"), source(Path.Combine(arm, "arm_cortexM7lfdp_math.lib"), "4")); xml.Save(mdk);
            var main = Path.Combine(project, "Core", "Src", "main.c"); var code = File.ReadAllText(main);
            File.WriteAllText(main, "extern int dsp_value(void);\nextern int port_value(void);\n" + code.Replace("zero_value = ram_value()", "zero_value = dsp_value() + port_value() + ram_value()"));
            var originals = Directory.EnumerateFiles(project, "*", SearchOption.AllDirectories).ToDictionary(x => x, File.ReadAllBytes);
            var plan = CubeMxConverter.CreatePlan(new(project, ConversionDirection.MdkToCMake, CubeMxFixture.Target)); Require(plan.CanApply, plan);
            Require(plan.Issues.Any(x => x.Message.Contains("GNU") && !x.Blocking) && plan.Issues.Any(x => x.Message.Contains("DSP") && !x.Blocking), plan);
            ChangeHistory.Apply(project, "DSP coverage", plan.Changes); var build = Build(project);
            var elf = CurrentFirmwareArtifacts.Find(build.BuildDirectory, "Debug").Single();
            var symbols = Run(Path.Combine(toolDir, "arm-none-eabi-nm.exe"), [elf], project);
            Require(symbols.Contains(" dsp_value") && symbols.Contains(" port_value") && originals.All(x => File.ReadAllBytes(x.Key).SequenceEqual(x.Value)), plan);
            var reverse = CubeMxConverter.CreatePlan(new(project, ConversionDirection.CMakeToMdk, CubeMxFixture.Target, BuildDirectory: build.BuildDirectory)); Require(reverse.CanApply, reverse);
            var output = XDocument.Parse(Encoding.UTF8.GetString(reverse.Changes.Single(x => x.Path == reverse.OutputProject).After!));
            Require(output.Descendants("FilePath").Any(x => x.Value.EndsWith("arm_cortexM7lfdp_math.lib")), reverse);

            // A real substitution: original ARM syntax, same-version GNU sibling, trailing include separator.
            var gccPort = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(port)!)!, "GCC", "ARM_CM4F"); Directory.CreateDirectory(gccPort);
            File.Copy(Path.Combine(port, "port.c"), Path.Combine(gccPort, "port.c")); File.Copy(Path.Combine(port, "portmacro.h"), Path.Combine(gccPort, "portmacro.h"));
            File.WriteAllText(Path.Combine(port, "port.c"), "/* FreeRTOS Kernel V10.3.1 */\n__asm void port_value(void) { bx lr }\n");
            var includes = xml.Descendants("Cads").Single().Element("VariousControls")!.Element("IncludePath")!; includes.Value += "\\";
            xml.Descendants("File").First().Add(new XElement("FileOption", new XElement("FileArmAds", new XElement("Cads", new XElement("VariousControls", new XElement("IncludePath", "../Middlewares/Third_Party/FreeRTOS/Source/portable/RVDS/ARM_CM4F/"))))));
            xml.Save(mdk);
            var substitute = CubeMxConverter.CreatePlan(new(project, ConversionDirection.MdkToCMake, CubeMxFixture.Target)); Require(substitute.CanApply, substitute);
            var text = Encoding.UTF8.GetString(substitute.Changes.Single(x => x.Path == substitute.OutputProject).After!);
            Require(text.Contains("portable/GCC/ARM_CM4F") && !text.Contains("portable/RVDS/ARM_CM4F"), substitute);
            ChangeHistory.Apply(project, "GCC port substitution", substitute.Changes); Build(project);

            // Ordinary positive cross-drive case when a second writable fixed drive is available.
            var drive = DriveInfo.GetDrives().FirstOrDefault(x => x.IsReady && x.DriveType == DriveType.Fixed && !x.Name.Equals(Path.GetPathRoot(project), StringComparison.OrdinalIgnoreCase));
            if (drive != null)
            {
                var external = Path.Combine(drive.RootDirectory.FullName, "CMakeDapLinkVerification-" + Guid.NewGuid().ToString("N"));
                try
                {
                    Directory.CreateDirectory(Path.Combine(external, "ARM")); Directory.CreateDirectory(Path.Combine(external, "GCC"));
                    var externalArm = Path.Combine(external, "ARM", "arm_cortexM7lfdp_math.lib"); File.Copy(Path.Combine(arm, "arm_cortexM7lfdp_math.lib"), externalArm);
                    File.Copy(dsp, Path.Combine(external, "GCC", Path.GetFileName(dsp)));
                    xml.Descendants("FilePath").Single(x => x.Value.EndsWith("arm_cortexM7lfdp_math.lib")).Value = externalArm; xml.Save(mdk);
                    var crossDrive = CubeMxConverter.CreatePlan(new(project, ConversionDirection.MdkToCMake, CubeMxFixture.Target)); Require(crossDrive.CanApply, crossDrive);
                    var generated = Encoding.UTF8.GetString(crossDrive.Changes.Single(x => x.Path == crossDrive.OutputProject).After!);
                    Require(!generated.Contains("${CMAKE_CURRENT_SOURCE_DIR}/" + drive.Name.Replace('\\', '/')), crossDrive);
                    ChangeHistory.Apply(project, "external DSP coverage", crossDrive.Changes); Build(project);
                }
                finally { if (Directory.Exists(external)) Directory.Delete(external, true); }
            }
        });
    }
    private static CMakeBuildPlan Build(string project)
    {
        var tools = EnvironmentScanner.Scan();
        var build = CMakeBuildPlan.Create(new(project, tools.CMake!, tools.Ninja!, tools.Compiler!, "", "", "target/stm32h7x.cfg", "Debug", "Debug", null, Path.Combine(project, "cmake", "gcc-arm-none-eabi.cmake")));
        build.PrepareArtifactQuery(); Run(build.Configure.Executable, build.Configure.Arguments, project, build.Configure.PathPrefix); Run(build.Build.Executable, build.Build.Arguments, project, build.Build.PathPrefix); return build;
    }
    private static void Require(bool value, ConversionPlan plan) { if (!value) throw new Exception(string.Join("; ", plan.Issues.Select(x => x.Message + " " + x.Action))); }
    private static string Run(string executable, IReadOnlyList<string> arguments, string root, string? pathPrefix = null)
    {
        using var process = new Process { StartInfo = new(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = root } };
        foreach (var arg in arguments) process.StartInfo.ArgumentList.Add(arg);
        if (pathPrefix != null) process.StartInfo.Environment["PATH"] = pathPrefix + Path.PathSeparator + process.StartInfo.Environment["PATH"];
        process.Start(); var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(120000)) { process.Kill(true); throw new Exception("compile timeout"); }
        var text = output.Result + errors.Result; if (process.ExitCode != 0) throw new Exception(text); return text;
    }
}
