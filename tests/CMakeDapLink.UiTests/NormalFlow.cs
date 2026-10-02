using System.Reflection;
using CMakeDapLink.App;

namespace CMakeDapLink.UiTests;

internal static class NormalFlow
{
    [STAThread]
    public static int Run(string[] args)
    {
        try
        {
            using var form = new MainForm { Opacity = 0, ShowInTaskbar = false, Size = new(1453, 1032) };
            form.Show(); Pump(1000);
            if (args.Length > 1) Await(Invoke(form, "LoadProjectAsync", args[1]));
            if (!Field<Button>(form, "_autoRepair").Enabled) throw new Exception("自动修复按钮未启用");
            var completionShown = false;
            using var closeDialog = new System.Windows.Forms.Timer { Interval = 200 };
            closeDialog.Tick += (_, _) => {
                foreach (var dialog in Application.OpenForms.Cast<Form>().OfType<MainForm.CompletionDialog>().ToArray())
                { completionShown = true; dialog.DialogResult = DialogResult.Cancel; dialog.Close(); }
            };
            closeDialog.Start();
            Await(Invoke(form, "AutoRepairAsync", false)); closeDialog.Stop();
            if (!completionShown || !Field<Label>(form, "_stage").Text.Contains("验证通过")) throw new Exception("正常自动修复流程没有通过");
            foreach (var button in Walk(form).OfType<Button>())
                if (button.FlatAppearance.BorderSize != 0) throw new Exception("按钮仍有描边：" + button.Text);
            var output = args.FirstOrDefault() ?? Path.GetFullPath("normal-flow.png");
            form.ActiveControl = Field<Button>(form, "_autoRepair"); Pump(300);
            using (var image = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(image, new(Point.Empty, image.Size)); image.Save(output); }
            Console.WriteLine("PASS 正常窗口：工程识别、自动修复验证、路径回填、完成弹窗、无按钮描边。" );
            var mirrorShown = false;
            using var mirrorTimer = new System.Windows.Forms.Timer { Interval = 200 };
            mirrorTimer.Tick += (_, _) => {
                var dialog = Application.OpenForms.Cast<Form>().FirstOrDefault(x => x.Text == "镜像源");
                if (dialog == null) return;
                mirrorShown = true;
                using var image = new Bitmap(dialog.Width, dialog.Height);
                dialog.DrawToBitmap(image, new(Point.Empty, image.Size));
                image.Save(Path.Combine(Path.GetDirectoryName(output)!, "mirror-source.png"));
                Walk(dialog).OfType<Button>().First(x => x.Text == "保存设置").PerformClick();
            };
            mirrorTimer.Start(); Field<Button>(form, "_mirrorSource").PerformClick(); mirrorTimer.Stop();
            if (!mirrorShown) throw new Exception("镜像设置未打开");
            Console.WriteLine("PASS 镜像源弹窗正常打开、保存。" );
            form.Hide(); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    private static T Field<T>(Form form, string name) => (T)typeof(MainForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
    private static Task Invoke(Form form, string method, params object[] args) => (Task)typeof(MainForm).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, args)!;
    private static IEnumerable<Control> Walk(Control control) { foreach (Control child in control.Controls) { yield return child; foreach (var item in Walk(child)) yield return item; } }
    private static void Await(Task task) { var deadline = DateTime.UtcNow.AddMinutes(3); while (!task.IsCompleted && DateTime.UtcNow < deadline) Pump(20); task.GetAwaiter().GetResult(); }
    private static void Pump(int milliseconds) { var until = DateTime.UtcNow.AddMilliseconds(milliseconds); while (DateTime.UtcNow < until) { Application.DoEvents(); Thread.Sleep(10); } }
}
