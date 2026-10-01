using System.Reflection;
using System.Text.Json;
using CMakeDapLink.App;
using CMakeDapLink.Core;

namespace CMakeDapLink.UiTests;

internal static class SoftwareUiFlow
{
    public static int Run(string[] args)
    {
        var output = args.FirstOrDefault() ?? Path.Combine(Path.GetTempPath(), "cmake-software-ui");
        Directory.CreateDirectory(output);
        var root = Path.Combine(Path.GetTempPath(), "cmake-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "module", "include"));
        try
        {
            var tools = EnvironmentScanner.Scan();
            var compilerDirectory = Path.GetDirectoryName(tools.Compiler!)!.Replace('\\', '/');
            File.WriteAllText(Path.Combine(root, "sample.ioc"), "Mcu.CPN=STM32H723VGT6\nMcu.Name=STM32H723VGTx\n");
            File.WriteAllText(Path.Combine(root, "toolchain.cmake"), $"set(CMAKE_SYSTEM_NAME Generic)\nset(CMAKE_SYSTEM_PROCESSOR arm)\nset(CMAKE_C_COMPILER \"{compilerDirectory}/arm-none-eabi-gcc.exe\")\nset(CMAKE_CXX_COMPILER \"{compilerDirectory}/arm-none-eabi-g++.exe\")\nset(CMAKE_ASM_COMPILER \"{compilerDirectory}/arm-none-eabi-gcc.exe\")\nset(CMAKE_TRY_COMPILE_TARGET_TYPE STATIC_LIBRARY)\n");
            File.WriteAllText(Path.Combine(root, "CMakeLists.txt"), "cmake_minimum_required(VERSION 3.22)\nproject(probe C)\nadd_executable(probe main.c)\nset_target_properties(probe PROPERTIES SUFFIX .elf)\ntarget_compile_options(probe PRIVATE -mcpu=cortex-m7 -mthumb)\ntarget_link_options(probe PRIVATE -mcpu=cortex-m7 -mthumb -nostdlib -Wl,-e,main)\n");
            File.WriteAllText(Path.Combine(root, "main.c"), "int main(void) { return 0; }\n");
            File.WriteAllText(Path.Combine(root, "module", "motor.cpp"), "extern \"C\" int motor_value(void) { return 7; }\n");
            File.WriteAllText(Path.Combine(root, "module", "include", "motor.hpp"), "#pragma once\n");
            File.WriteAllText(Path.Combine(root, "module", "support.S"), ".syntax unified\n.thumb\n.text\n.global support_fn\n.thumb_func\nsupport_fn:\n bx lr\n");
            File.WriteAllText(Path.Combine(root, "CMakePresets.json"), """
                {"version":3,"configurePresets":[
                 {"name":"Debug","generator":"Ninja","binaryDir":"${sourceDir}/build/Debug","toolchainFile":"${sourceDir}/toolchain.cmake","cacheVariables":{"CMAKE_BUILD_TYPE":"Debug"}},
                 {"name":"Release","generator":"Ninja","binaryDir":"${sourceDir}/build/Release","toolchainFile":"${sourceDir}/toolchain.cmake","cacheVariables":{"CMAKE_BUILD_TYPE":"Release"}},
                 {"name":"Multi","generator":"Ninja Multi-Config","binaryDir":"${sourceDir}/build/Multi","toolchainFile":"${sourceDir}/toolchain.cmake"}],
                 "buildPresets":[{"name":"DebugBuild","configurePreset":"Multi","configuration":"Debug"},{"name":"ReleaseBuild","configurePreset":"Multi","configuration":"Release"}]}
                """);
            using var form = new MainForm { Size = new(1453, 1032), ShowInTaskbar = false };
            form.Show(); Pump(1200); Await(Invoke(form, "LoadProjectAsync", root));
            Require(Field<TextBox>(form, "_target").Text == "target/stm32h7x.cfg", "芯片和软件 Target 自动识别");
            var picker = Field<Control>(form, "_presetPicker");
            picker.GetType().GetProperty("SelectedIndex")!.SetValue(picker, 1);
            Until(() => !Field<bool>(form, "_busy"));
            Require(Field<ProjectInfo>(form, "_project").ConfigurePreset == "Release", "Release 预设切换");
            Require(Field<TextBox>(form, "_target").Text == "target/stm32h7x.cfg", "切换预设后同步烧录目标");
            picker.GetType().GetProperty("SelectedIndex")!.SetValue(picker, 2);
            Until(() => !Field<bool>(form, "_busy"));
            var buildPicker = Field<Control>(form, "_buildPresetPicker");
            buildPicker.GetType().GetProperty("SelectedIndex")!.SetValue(buildPicker, 2);
            Require(Field<ProjectInfo>(form, "_project").BuildPreset == "ReleaseBuild", "同一配置预设下可选择 Release 编译预设");
            var options = (SetupOptions)typeof(MainForm).GetMethod("CreateOptions", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(form, [true])!;
            Require(options.BuildConfiguration == "Release" && options.BuildPreset == "ReleaseBuild", "多配置构建传递所选 Release 设置");
            Screenshot(form, Path.Combine(output, "software-workbench.png"));
            Field<Button>(form, "_navSources").PerformClick();
            Field<Label>(form, "_sourceFolder").Text = Path.Combine(root, "module");
            typeof(MainForm).GetMethod("RefreshSourcePreview", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(form, null);
            var files = Field<Control>(form, "_sourcePreview");
            Require((int)files.GetType().GetProperty("Count")!.GetValue(files)! == 3, "C++、ASM 和头文件目录树");
            files.GetType().GetMethod("SetAll")!.Invoke(files, [false]);
            files.GetType().GetMethod("SetChecked")!.Invoke(files, [0, true]);
            var selectedBefore = ((IReadOnlyList<string>)files.GetType().GetProperty("CheckedPaths")!.GetValue(files)!).ToArray();
            Field<Control>(form, "_sourceSearch").Text = "support";
            Require((int)files.GetType().GetProperty("VisibleFileCount")!.GetValue(files)! == 1, "搜索过滤文件");
            Require(((IReadOnlyList<string>)files.GetType().GetProperty("CheckedPaths")!.GetValue(files)!).SequenceEqual(selectedBefore), "过滤保留隐藏文件的勾选");
            Field<Control>(form, "_sourceSearch").Text = "";
            files.GetType().GetMethod("SetAll")!.Invoke(files, [true]);
            files.GetType().GetMethod("CollapseAll")!.Invoke(files, null);
            files.GetType().GetMethod("ExpandAll")!.Invoke(files, null);
            Screenshot(form, Path.Combine(output, "software-sources.png"));
            var previews = 0;
            using var accept = new System.Windows.Forms.Timer { Interval = 200 };
            accept.Tick += (_, _) =>
            {
                foreach (var dialog in Application.OpenForms.Cast<Form>().Where(x => x.GetType().Name == "ChangePreviewDialog").ToArray())
                {
                    accept.Stop();
                    previews++; Screenshot(dialog, Path.Combine(output, "software-diff.png"));
                    dialog.AcceptButton!.PerformClick();
                    accept.Start();
                }
            };
            accept.Start(); Await(Invoke(form, "ApplySourceAsync"));
            Require(Field<Label>(form, "_sourceStatus").Text.StartsWith("验证通过"), "预览后写入并实际编译 C/C++/ASM");
            Field<Button>(form, "_navSetup").PerformClick(); Await(Invoke(form, "ConfigureAsync")); accept.Stop();
            Require(previews == 2 && File.Exists(Path.Combine(root, ".vscode", "tasks.json")), "编译及脚本解析后预览并写入 VS Code 任务：预览数=" + previews + "，状态=" + Field<Label>(form, "_stage").Text);
            Require(!Directory.EnumerateFiles(root, "*.ps1", SearchOption.AllDirectories).Any(), "不生成 PowerShell 脚本");
            var snapshot = ChangeHistory.List(root).First(x => x.Label.Contains("配置"));
            ChangeHistory.Restore(root, snapshot.Id);
            Require(!File.Exists(Path.Combine(root, ".vscode", "tasks.json")), "配置任务可恢复到未创建状态");
            var assembly = typeof(MainForm).Assembly;
            using var toolDialog = (Form)Activator.CreateInstance(assembly.GetType("CMakeDapLink.App.ToolManagementDialog")!, tools)!;
            toolDialog.Show(form); Pump(1000); Screenshot(toolDialog, Path.Combine(output, "software-tools.png")); toolDialog.Close();
            using var logDialog = (Form)Activator.CreateInstance(assembly.GetType("CMakeDapLink.App.BuildProblemsDialog")!,
                new object[] { Array.Empty<BuildProblem>(), "正常固件编译通过。" })!;
            logDialog.Show(form); Pump(300); Screenshot(logDialog, Path.Combine(output, "software-log.png")); logDialog.Close();
            Require(Walk(form).OfType<Button>().All(x => x.FlatAppearance.BorderSize == 0), "主界面按钮无黑色描边");
            form.Close(); Console.WriteLine("PASS 普通窗口完整软件流程；截图：" + output); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally
        {
            var absolute = Path.GetFullPath(root);
            if (absolute.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(absolute).StartsWith("cmake-ui-", StringComparison.Ordinal)) Directory.Delete(absolute, true);
        }
    }
    private static void Require(bool value, string label) { if (!value) throw new Exception(label); Console.WriteLine("PASS " + label); }
    private static T Field<T>(Form form, string name) => (T)typeof(MainForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
    private static Task Invoke(Form form, string method, params object[] args) => (Task)typeof(MainForm).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, args)!;
    private static IEnumerable<Control> Walk(Control control) { foreach (Control child in control.Controls) { yield return child; foreach (var item in Walk(child)) yield return item; } }
    private static void Screenshot(Form form, string path) { Pump(150); using var bitmap = new Bitmap(form.Width, form.Height); form.DrawToBitmap(bitmap, new(Point.Empty, bitmap.Size)); bitmap.Save(path); }
    private static void Await(Task task) { Until(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
    private static void Until(Func<bool> ready) { var limit = DateTime.UtcNow.AddMinutes(3); while (!ready() && DateTime.UtcNow < limit) Pump(20); if (!ready()) throw new TimeoutException("软件界面操作超时"); }
    private static void Pump(int ms) { var limit = DateTime.UtcNow.AddMilliseconds(ms); while (DateTime.UtcNow < limit) { Application.DoEvents(); Thread.Sleep(10); } }
}
