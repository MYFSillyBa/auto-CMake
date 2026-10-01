using System.Reflection;
using System.Runtime.InteropServices;
using CMakeDapLink.App;

namespace CMakeDapLink.UiTests;

internal static class FileImportUiChecks
{
    public static void Run()
    {
        var fixture = Path.Combine(Path.GetTempPath(), "CMakeDapLinkUi-" + Guid.NewGuid().ToString("N"));
        var project = Path.Combine(fixture, "project"); Directory.CreateDirectory(project);
        File.WriteAllText(Path.Combine(project, "CMakeLists.txt"), "project(demo)\nadd_executable(app main.c)\n");
        File.WriteAllText(Path.Combine(project, "board.ioc"), "Mcu.CPN=STM32H723VGT6\n");
        var file = Path.Combine(fixture, "driver.c"); File.WriteAllText(file, "int driver(void) { return 0; }\n");
        try
        {
            using var form = new MainForm(1f) { Opacity = 0, ShowInTaskbar = false };
            form.Show(); Application.DoEvents();
            using var closeHelp = new System.Windows.Forms.Timer { Interval = 40 };
            closeHelp.Tick += (_, _) =>
            {
                var help = Application.OpenForms.OfType<MainForm.CompletionDialog>().FirstOrDefault();
                if (help != null) { help.DialogResult = DialogResult.Cancel; help.Close(); }
            };
            closeHelp.Start(); Pump(Call(form, "LoadProjectAsync", project)); closeHelp.Stop();
            Find<Button>(form, "_navImport").PerformClick();
            Find<Control>(form, "_importName").Text = "Utilities";
            CallVoid(form, "AddImportFiles", new[] { file });
            Pump(Call(form, "CopyImportFilesAsync"));
            var copied = Path.Combine(project, "Utilities", "driver.c");
            if (!File.Exists(copied) || File.ReadAllText(copied) != File.ReadAllText(file)) throw new Exception("UI import did not copy files");
            CallVoid(form, "AddImportFiles", new[] { file });
            WithConfirmation(form, false, () => Pump(Call(form, "CopyImportFilesAsync")));
            if (Directory.GetFiles(Path.GetDirectoryName(copied)!).Length != 1 || !Find<Label>(form, "_importStatus").Text.Contains("更改文件夹名称"))
                throw new Exception("No confirmation wrote files or failed to request a new folder name");
            WithConfirmation(form, true, () => Pump(Call(form, "CopyImportFilesAsync")));
            if (!File.Exists(Path.Combine(project, "Utilities", "driver (1).c")) || !File.Exists(file))
                throw new Exception("Yes confirmation did not append or source file was moved");
            Find<Button>(form, "_openImported").PerformClick();
            if (Find<Label>(form, "_sourceFolder").Text != Path.Combine(project, "Utilities") || !Find<Button>(form, "_applySource").Enabled)
                throw new Exception("import-to-source page navigation failed");
            Console.WriteLine("PASS UI 实际导入：新建、同名文件夹否/是、保留原文件、跳转源文件页");

            var empty = Path.Combine(fixture, "empty"); Directory.CreateDirectory(empty);
            var seen = false;
            using var autoHelp = new System.Windows.Forms.Timer { Interval = 40 };
            autoHelp.Tick += (_, _) =>
            {
                var help = Application.OpenForms.OfType<MainForm.CompletionDialog>().FirstOrDefault();
                if (help == null) return;
                seen = Labels(help).Any(x => x.Text.Contains("未选择有效的 CMake 工程"));
                help.DialogResult = DialogResult.Cancel; help.Close();
            };
            autoHelp.Start(); Pump(Call(form, "LoadProjectAsync", empty)); autoHelp.Stop();
            if (!seen) throw new Exception("missing environment did not automatically open completion guidance");
            Console.WriteLine("PASS 缺项自动弹窗：检测到无效工程，显示具体补全说明");
            form.Hide();
        }
        finally { Directory.Delete(fixture, true); }
    }
    private static T Find<T>(Form form, string field) where T : Control =>
        (T)typeof(MainForm).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
    private static Task Call(Form form, string method, params object[] args) => (Task)typeof(MainForm).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, args)!;
    private static void CallVoid(Form form, string method, string[] files) => typeof(MainForm).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, [files]);
    private static void Pump(Task task)
    {
        var timeout = DateTime.UtcNow.AddSeconds(35);
        while (!task.IsCompleted && DateTime.UtcNow < timeout) { Application.DoEvents(); Thread.Sleep(10); }
        if (!task.IsCompleted) throw new Exception("UI task timed out");
        task.GetAwaiter().GetResult(); Application.DoEvents();
    }
    private static IEnumerable<Label> Labels(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            if (child is Label label) yield return label;
            foreach (var nested in Labels(child)) yield return nested;
        }
    }
    private static void WithConfirmation(Form owner, bool yes, Action action)
    {
        var clicked = false;
        using var timer = new System.Windows.Forms.Timer { Interval = 40 };
        timer.Tick += (_, _) =>
        {
            var window = FindWindow("#32770", "文件夹已存在");
            if (window == IntPtr.Zero || GetWindow(window, 4) != owner.Handle) return;
            clicked = true; timer.Stop(); SendMessage(GetDlgItem(window, yes ? 6 : 7), 0xF5, IntPtr.Zero, IntPtr.Zero);
        };
        timer.Start(); action(); timer.Stop();
        if (!clicked) throw new Exception("existing-folder confirmation was not displayed");
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string className, string title);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("user32.dll")] private static extern IntPtr GetDlgItem(IntPtr window, int id);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
