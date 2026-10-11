using CMakeDapLink.App;
using CMakeDapLink.Core;
using System.Reflection;

namespace CMakeDapLink.UiTests;

internal static class ToolSelectionUiChecks
{
    public static int Run()
    {
        var saved = File.Exists(ManagedTools.PathsPath) ? File.ReadAllBytes(ManagedTools.PathsPath) : null;
        var pathValue = Environment.GetEnvironmentVariable("PATH");
        var fixture = Path.Combine(Path.GetTempPath(), "ToolSelectionUi-" + Guid.NewGuid().ToString("N"));
        var source = (pathValue ?? "").Split(Path.PathSeparator).Select(x => Path.Combine(x.Trim('"'), "ninja.exe")).First(File.Exists);
        var first = Path.Combine(fixture, "first", "ninja.exe"); var second = Path.Combine(fixture, "second", "ninja.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(first)!); Directory.CreateDirectory(Path.GetDirectoryName(second)!);
        File.Copy(source, first); File.Copy(source, second);
        try
        {
            ManagedTools.SavePaths(new(null, null, null, null, null));
            Environment.SetEnvironmentVariable("PATH", Path.GetDirectoryName(first) + Path.PathSeparator + Path.GetDirectoryName(second) + Path.PathSeparator + pathValue);
            using (var form = OpenForm())
            {
                var boxes = Field<Dictionary<string, TextBox>>(form, "_toolBoxes");
                Require(boxes["Ninja"].Text.Contains("待选择"), "ambiguous tool did not request selection: " + boxes["Ninja"].Text + " / " + Field<Label>(form, "_stage").Text);
                Require(Field<ToolPaths>(form, "_tools").Ninja == null, "startup silently selected a candidate");
                var before = File.ReadAllBytes(ManagedTools.PathsPath); var seen = false;
                using var cancel = new System.Windows.Forms.Timer { Interval = 30 };
                cancel.Tick += (_, _) =>
                {
                    var dialog = Application.OpenForms.OfType<ToolSelectionDialog>().FirstOrDefault();
                    if (dialog == null) return;
                    var grid = dialog.Controls.Find("ToolCandidates", true).OfType<DataGridView>().Single();
                    var rows = grid.Rows.Cast<DataGridViewRow>().ToArray();
                    seen = rows.Any(x => x.Cells[2].Value?.ToString() == first) && rows.Any(x => x.Cells[2].Value?.ToString() == second) &&
                        rows.All(x => !string.IsNullOrWhiteSpace(x.Cells[1].Value?.ToString()));
                    dialog.DialogResult = DialogResult.Cancel; dialog.Close();
                };
                cancel.Start(); Pump(Call(form, "BrowseToolAsync", "Ninja")); cancel.Stop();
                Require(seen, "picker omitted detected full paths or versions");
                Require(before.AsSpan().SequenceEqual(File.ReadAllBytes(ManagedTools.PathsPath)) && Field<ToolPaths>(form, "_tools").Ninja == null, "cancel changed selections");
                Console.WriteLine("PASS UI multiple installed candidates show version/full path; cancel preserves settings and active tool");
                Pump(Call(form, "SelectToolPathAsync", "Ninja", second));
                Require(Field<ToolPaths>(form, "_tools").Ninja == second && ManagedTools.LoadPaths().Ninja == second, "manual UI choice was not saved");
                var invalid = Path.Combine(fixture, "invalid", "ninja.exe"); Directory.CreateDirectory(Path.GetDirectoryName(invalid)!); File.WriteAllText(invalid, "not executable");
                before = File.ReadAllBytes(ManagedTools.PathsPath);
                Pump(Call(form, "SelectToolPathAsync", "Ninja", invalid));
                Require(before.AsSpan().SequenceEqual(File.ReadAllBytes(ManagedTools.PathsPath)) && Field<ToolPaths>(form, "_tools").Ninja == second, "failed validation changed UI or settings");
                Pump(Call(form, "RescanAsync", false));
                Require(Field<ToolPaths>(form, "_tools").Ninja == second, "rescan lost manual choice");
            }
            using (var restarted = OpenForm())
            {
                Require(Field<ToolPaths>(restarted, "_tools").Ninja == second, "new form lost manual selection");
                var rows = new[] { new ToolInstallation("Ninja", "1.0", first, null, Path.GetDirectoryName(first)!, 1, false, false),
                    new ToolInstallation("Ninja", "1.0", second, null, Path.GetDirectoryName(second)!, 1, false, true) };
                using var picker = new ToolSelectionDialog("Ninja", rows) { Opacity = 0, ShowInTaskbar = false };
                picker.Show(); Application.DoEvents();
                var grid = picker.Controls.Find("ToolCandidates", true).OfType<DataGridView>().Single();
                Require(grid.CurrentRow!.Cells[0].Value?.ToString() == "当前使用" && grid.CurrentRow.Cells[2].Value?.ToString() == second, "current tool indicator or initial highlight incorrect");
                picker.Close();
            }
            Console.WriteLine("PASS UI manual selection survives rescan and new form; invalid executable preserves choice; current selection is marked");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine("FAIL " + ex); return 1; }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", pathValue);
            if (saved == null) File.Delete(ManagedTools.PathsPath); else File.WriteAllBytes(ManagedTools.PathsPath, saved);
            Directory.Delete(fixture, true);
        }
    }

    private static MainForm OpenForm()
    {
        var form = new MainForm(1f) { Opacity = 0, ShowInTaskbar = false }; form.Show(); Application.DoEvents();
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (Field<bool>(form, "_busy") && DateTime.UtcNow < deadline) { Application.DoEvents(); Thread.Sleep(10); }
        Require(!Field<bool>(form, "_busy"), "startup scan timeout");
        return form;
    }
    private static T Field<T>(MainForm form, string name) => (T)typeof(MainForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
    private static Task Call(MainForm form, string name, params object[] args) => (Task)typeof(MainForm).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, args)!;
    private static void Pump(Task task)
    {
        var deadline = DateTime.UtcNow.AddSeconds(120);
        while (!task.IsCompleted && DateTime.UtcNow < deadline) { Application.DoEvents(); Thread.Sleep(10); }
        Require(task.IsCompleted, "UI operation timed out"); task.GetAwaiter().GetResult();
    }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
}
