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
            File.AppendAllText(Path.Combine(root, "CMakeLists.txt"), "if(EXISTS \"${CMAKE_SOURCE_DIR}/.vscode/tasks.json\")\n file(WRITE \"${CMAKE_BINARY_DIR}/tasks-first.txt\" \"tasks present before configure\")\nendif()\n");
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
            var exitCode = 1;
            form.Shown += (_, _) => form.BeginInvoke(() =>
            {
            try
            {
            Pump(1200); Until(() => !Field<bool>(form, "_busy"));
            Require(!Field<Button>(form, "_configure").Enabled && Field<Label>(form, "_readiness").Text.Contains("先选择工程"), "禁用配置按钮说明原因");
            Screenshot(form, Path.Combine(output, "experience-empty-workbench.png"));
            Field<Button>(form, "_navSources").PerformClick();
            Require(Field<Button>(form, "_sourceChooseProject").Visible && !Field<Panel>(form, "_sourceFiles").Visible, "源文件页无工程时显示选择入口");
            Screenshot(form, Path.Combine(output, "experience-empty-sources.png"));
            Field<Button>(form, "_navImport").PerformClick();
            Require(Field<Button>(form, "_importChooseProject").Visible, "加入文件页无工程时显示选择入口");
            Field<Button>(form, "_navSetup").PerformClick();
            Await(Invoke(form, "LoadProjectAsync", root));
            Require(Field<TextBox>(form, "_target").Text == "target/stm32h7x.cfg", "芯片和软件 Target 自动识别");
            Require(Field<OpenOcdScriptResolution>(form, "_targetScriptResolution").Ready &&
                Field<Label>(form, "_result").Text.Contains("内置 Target 配置已验证"), "内置 Target 与配套依赖通过 OpenOCD 软件解析");
            var embeddedScripts = Task.Run(() => BundledOpenOcdScripts.GetDirectoryAsync()).GetAwaiter().GetResult();
            Require(Field<ToolPaths>(form, "_tools").Scripts == embeddedScripts, "使用 EXE 内置脚本目录");
            var existingTools = Field<ToolPaths>(form, "_tools");
            Await(Invoke(form, "AutoRepairAsync", true));
            Require(Field<ToolPaths>(form, "_tools") == existingTools &&
                Field<Label>(form, "_stage").Text.Contains("内置 Target 已就绪"), "Target 专项检查使用内置脚本并保留其他工具路径");
            Field<TextBox>(form, "_target").Text = "target/stm32h7x_dual_bank.cfg";
            Await(Invoke(form, "RescanAsync", false));
            Require(Field<TextBox>(form, "_target").Text == "target/stm32h7x_dual_bank.cfg" &&
                Field<OpenOcdScriptResolution>(form, "_targetScriptResolution").Ready, "手动指定的 Target 在重新检测后保留并验证");
            Field<TextBox>(form, "_target").Clear();
            Await(Invoke(form, "RescanAsync", false));
            Require(Field<TextBox>(form, "_target").Text == "target/stm32h7x.cfg" &&
                Field<OpenOcdScriptResolution>(form, "_targetScriptResolution").Ready, "Target 留空后按芯片自动选择内置脚本");
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
            Require((int)files.GetType().GetProperty("HiddenCheckedCount")!.GetValue(files)! == 1 && Field<Label>(form, "_sourceCount").Text.Contains("隐藏的已选 1"), "过滤后的隐藏已选数量可见");
            Field<Control>(form, "_sourceSearch").Text = "";
            files.GetType().GetMethod("SetAll")!.Invoke(files, [true]);
            files.GetType().GetMethod("CollapseAll")!.Invoke(files, null);
            Require((int)files.GetType().GetProperty("HiddenCheckedCount")!.GetValue(files)! == 0, "目录折叠不计入过滤隐藏数量");
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
            Require(Field<Panel>(form, "_sourceCompletionCard").Visible && !Field<RichTextBox>(form, "_sourceLog").Visible, "源文件结果卡和默认收起日志");
            Require((int)files.GetType().GetProperty("RegisteredFileCount")!.GetValue(files)! == 3, "刚写入的托管文件显示已引用");
            Screenshot(form, Path.Combine(output, "experience-source-result.png"));
            Field<Button>(form, "_navSetup").PerformClick();
            var configuring = Invoke(form, "ConfigureAsync");
            var configureButton = Field<Button>(form, "_configure");
            Require(configureButton.Text.Contains("验证中") && (bool)configureButton.GetType().GetProperty("IsBusy")!.GetValue(configureButton)! && !configureButton.Enabled, "配置按钮显示忙态并禁止重复点击");
            Require(StepStates(form, "_workflowSteps").Contains("Running"), "执行步骤显示当前进行中的阶段");
            Screenshot(form, Path.Combine(output, "experience-building.png"));
            Await(configuring); accept.Stop();
            Require(previews == 2 && File.Exists(Path.Combine(root, ".vscode", "tasks.json")) && File.Exists(Path.Combine(root, ".vscode", "stm32-daplink.cmake")), "预览并先写入 VS Code 任务及 CMake 辅助文件：预览数=" + previews + "，状态=" + Field<Label>(form, "_stage").Text);
            Require(File.Exists(Path.Combine(root, "build", "Multi", "tasks-first.txt")), "实际 CMake 配置开始前任务已存在");
            Require(!Directory.EnumerateFiles(root, "*.ps1", SearchOption.AllDirectories).Any(), "不生成 PowerShell 脚本");
            Require(StepStates(form, "_workflowSteps").All(x => x == "Complete"), "五个配置步骤全部完成");
            Require(Field<Panel>(form, "_completionCard").Visible && Field<Label>(form, "_completionBody").Text.Contains("probe.elf") && Field<Label>(form, "_completionBody").Text.Contains("下一步"), "结果卡显示实际固件和下一步操作");
            Require(!Field<RichTextBox>(form, "_log").Visible, "详细日志默认折叠");
            Screenshot(form, Path.Combine(output, "experience-completed.png"));
            Field<Button>(form, "_toggleLog").PerformClick(); Require(Field<RichTextBox>(form, "_log").Visible, "可展开详细日志");
            Field<Button>(form, "_toggleLog").PerformClick();
            var originalBuildIndex = (int)buildPicker.GetType().GetProperty("SelectedIndex")!.GetValue(buildPicker)!;
            buildPicker.GetType().GetProperty("SelectedIndex")!.SetValue(buildPicker, 1);
            Require(!Field<bool>(form, "_completionShown") && StepStates(form, "_workflowSteps").All(x => x == "Pending"), "切换配置使旧成功结果失效");
            buildPicker.GetType().GetProperty("SelectedIndex")!.SetValue(buildPicker, originalBuildIndex);
            var snapshot = ChangeHistory.List(root).First(x => x.Label.Contains("配置"));
            ChangeHistory.Restore(root, snapshot.Id);
            Require(!File.Exists(Path.Combine(root, ".vscode", "tasks.json")), "配置任务可恢复到未创建状态");
            File.AppendAllText(Path.Combine(root, "CMakeLists.txt"), "add_executable(other main.c)\nset_target_properties(other PROPERTIES OUTPUT_NAME other.v2 SUFFIX .elf)\ntarget_compile_options(other PRIVATE -mcpu=cortex-m7 -mthumb)\ntarget_link_options(other PRIVATE -mcpu=cortex-m7 -mthumb -nostdlib -Wl,-e,main)\n");
            var firmwarePicks = 0;
            var tasksAtPicker = false;
            using var pickFirmware = new System.Windows.Forms.Timer { Interval = 200 };
            pickFirmware.Tick += (_, _) =>
            {
                foreach (var dialog in Application.OpenForms.Cast<Form>().Where(x => x.GetType().Name == "FirmwarePickerDialog").ToArray())
                {
                    pickFirmware.Stop();
                    var list = Walk(dialog).OfType<ListBox>().Single();
                    list.SelectedIndex = Enumerable.Range(0, list.Items.Count).First(i => list.Items[i]!.ToString()!.Contains("other.v2.elf"));
                    tasksAtPicker = File.Exists(Path.Combine(root, ".vscode", "tasks.json"));
                    firmwarePicks++;
                    Walk(dialog).OfType<Button>().Single(x => x.Text == "使用所选固件").PerformClick();
                }
            };
            var previewsBeforeBinding = previews;
            accept.Start(); pickFirmware.Start(); Await(Invoke(form, "ConfigureAsync")); accept.Stop(); pickFirmware.Stop();
            Require(tasksAtPicker && firmwarePicks == 1 && previews == previewsBeforeBinding + 2, "多个固件先写入任务，再确认并预览所选固件绑定");
            using (var boundTasks = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, ".vscode", "tasks.json"))))
            {
                var boundFlash = boundTasks.RootElement.GetProperty("tasks").EnumerateArray().Single(x => x.GetProperty("label").GetString() == "一键烧录(DAPLINK)");
                Require(boundFlash.GetProperty("args").EnumerateArray().Any(x => x.GetString()!.StartsWith("-DFIRMWARE_ELF=") && x.GetString()!.Contains("other.v2.elf")), "烧录任务绑定用户选择的实际固件");
            }
            Require(StepStates(form, "_workflowSteps").All(x => x == "Complete") && Field<Label>(form, "_completionBody").Text.Contains("other.v2.elf"), "多个固件流程通过编译和软件脚本检查");
            Field<Button>(form, "_navImport").PerformClick();
            var incoming = Path.Combine(root, "incoming"); Directory.CreateDirectory(incoming);
            File.WriteAllText(Path.Combine(incoming, "fresh.c"), "int fresh_value(void) { return 9; }\n");
            File.WriteAllText(Path.Combine(incoming, "fresh.h"), "#pragma once\nint fresh_value(void);\n");
            Field<Control>(form, "_importName").Text = "extras";
            typeof(MainForm).GetMethod("AddImportFiles", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(form, [new[] { Path.Combine(incoming, "fresh.c"), Path.Combine(incoming, "fresh.h") }]);
            Await(Invoke(form, "CopyImportFilesAsync"));
            Require(StepStates(form, "_importSteps").All(x => x == "Complete") && !Field<RichTextBox>(form, "_importLog").Visible, "复制完成反馈及默认收起记录");
            Require(Field<Control>(form, "_importNotice").Visible, "复制成功使用界面通知");
            var notice = Field<Control>(form, "_importNotice");
            using (var renderedNotice = new Bitmap(notice.Width, notice.Height))
            {
                notice.DrawToBitmap(renderedNotice, new(Point.Empty, renderedNotice.Size));
                var corner = renderedNotice.GetPixel(0, 0);
                Require(corner.R > 100 && corner.G > 100 && corner.B > 100, "提示条圆角显示页面背景，无黑色残影");
            }
            Screenshot(form, Path.Combine(output, "experience-import-completed.png"));
            File.WriteAllText(Path.Combine(root, "extras", "unused.c"), "int unused_value(void) { return 2; }\n");
            Field<Button>(form, "_openImported").PerformClick();
            var importedSelection = (IReadOnlyList<string>)files.GetType().GetProperty("CheckedPaths")!.GetValue(files)!;
            Require(importedSelection.Contains("extras/fresh.c") && importedSelection.Contains("extras/fresh.h") && !importedSelection.Contains("extras/unused.c") && selectedBefore.All(importedSelection.Contains), "复制后预选新文件并保留原有托管选择");
            Screenshot(form, Path.Combine(output, "experience-import-next.png"));
            accept.Start(); Await(Invoke(form, "ApplySourceAsync")); accept.Stop();
            Require(File.ReadAllText(Path.Combine(root, "CMakeLists.txt")).Contains("extras/fresh.c") && Field<Label>(form, "_sourceStatus").Text.StartsWith("验证通过"), "复制的新文件加入 CMake 并通过实际编译");
            var assembly = typeof(MainForm).Assembly;
            using var toolDialog = (Form)Activator.CreateInstance(assembly.GetType("CMakeDapLink.App.ToolManagementDialog")!, tools)!;
            toolDialog.Show(form); Pump(1000); Screenshot(toolDialog, Path.Combine(output, "software-tools.png")); toolDialog.Close();
            using var logDialog = (Form)Activator.CreateInstance(assembly.GetType("CMakeDapLink.App.BuildProblemsDialog")!,
                new object[] { Array.Empty<BuildProblem>(), "正常固件编译通过。" })!;
            logDialog.Show(form); Pump(300); Screenshot(logDialog, Path.Combine(output, "software-log.png")); logDialog.Close();
            Require(Walk(form).OfType<Button>().All(x => x.FlatAppearance.BorderSize == 0), "主界面按钮无黑色描边");
            Await(Invoke(form, "LoadProjectAsync", root));
            Require(!Field<bool>(form, "_completionShown") && !Field<bool>(form, "_sourceCompletionShown")
                && StepStates(form, "_workflowSteps").All(x => x == "Pending")
                && StepStates(form, "_sourceSteps").All(x => x == "Pending")
                && StepStates(form, "_importSteps").All(x => x == "Pending"), "重新选择工程清除旧结果并重置所有步骤");
            Console.WriteLine("PASS 普通窗口完整软件流程；截图：" + output); exitCode = 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); }
            finally { form.Close(); Application.ExitThread(); }
            });
            Application.Run(form);
            return exitCode;
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
    private static string[] StepStates(Form form, string field) => ((System.Collections.IEnumerable)Field<Control>(form, field).GetType().GetProperty("States")!.GetValue(Field<Control>(form, field))!).Cast<object>().Select(x => x.ToString()!).ToArray();
    private static T Field<T>(Form form, string name) => (T)typeof(MainForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
    private static Task Invoke(Form form, string method, params object[] args) => (Task)typeof(MainForm).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, args)!;
    private static IEnumerable<Control> Walk(Control control) { foreach (Control child in control.Controls) { yield return child; foreach (var item in Walk(child)) yield return item; } }
    private static void Screenshot(Form form, string path) { Pump(150); using var bitmap = new Bitmap(form.Width, form.Height); form.DrawToBitmap(bitmap, new(Point.Empty, bitmap.Size)); bitmap.Save(path); }
    private static void Await(Task task) { Until(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
    private static void Until(Func<bool> ready) { var limit = DateTime.UtcNow.AddMinutes(3); while (!ready() && DateTime.UtcNow < limit) Pump(20); if (!ready()) throw new TimeoutException("软件界面操作超时"); }
    private static void Pump(int ms) { var limit = DateTime.UtcNow.AddMilliseconds(ms); while (DateTime.UtcNow < limit) { Application.DoEvents(); Thread.Sleep(10); } }
}
