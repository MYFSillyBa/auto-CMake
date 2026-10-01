using CMakeDapLink.Core;

internal static class FileWorkflowTests
{
    public static void Run(string root, Action<string, Action> check)
    {
        var project = Path.Combine(root, "selected files");
        var module = Path.Combine(project, "module");
        Directory.CreateDirectory(Path.Combine(module, "private"));
        File.WriteAllText(Path.Combine(project, "CMakeLists.txt"), "project(demo)\nadd_executable(app main.c)\n");
        foreach (var file in new[] { "yes.c", "skip.c", "yes.h", "private/skip.h" })
            File.WriteAllText(Path.Combine(module, file), "");

        check("writes only checked C/H files and checked header directories", () =>
        {
            var plan = Selected(project, module, ["module/yes.c", "module/yes.h"]);
            if (plan.SourceFiles.Count != 1 || plan.HeaderFiles.Count != 1 || plan.IncludeDirectories.Count != 1 ||
                plan.UpdatedText.Contains("skip.c") || plan.UpdatedText.Contains("private/skip.h"))
                throw new Exception("unchecked files or directories included");
            SourceFolderPlanner.Apply(plan);
        });
        check("keeps exclusions when reopening and adding another folder", () =>
        {
            var again = SourceFolderPlanner.Preview(project, module);
            if (again.SourceFiles.Contains("module/skip.c") || again.HeaderFiles.Contains("module/private/skip.h"))
                throw new Exception("excluded files were reselected");
            var extra = Path.Combine(project, "extra"); Directory.CreateDirectory(extra);
            File.WriteAllText(Path.Combine(extra, "new.c"), "");
            var merged = SourceFolderPlanner.Preview(project, extra);
            if (!merged.SourceFiles.Contains("extra/new.c") || !merged.SourceFiles.Contains("module/yes.c") ||
                merged.SourceFiles.Contains("module/skip.c")) throw new Exception("selection not preserved during merge");
        });
        check("rejects unchecked-only and non-candidate source selections", () =>
        {
            Expect<InvalidOperationException>(() => Selected(project, module, []));
            Expect<ArgumentException>(() => Selected(project, module, ["../outside.c"]));
        });
        check("can reselect remaining candidates after previously checked files are removed", () =>
        {
            File.Delete(Path.Combine(module, "yes.c")); File.Delete(Path.Combine(module, "yes.h"));
            var preview = SourceFolderPlanner.Preview(project, module);
            if (preview.CandidateFiles.Count != 2 || preview.SourceFiles.Count + preview.HeaderFiles.Count != 0)
                throw new Exception("remaining excluded candidates cannot be chosen");
            var selected = Selected(project, module, ["module/skip.c"]);
            if (selected.SourceFiles.Count != 1 || selected.HeaderFiles.Count != 0) throw new Exception("reselection failed");
        });

        var external = Path.Combine(root, "incoming"); Directory.CreateDirectory(external);
        var first = Path.Combine(external, "driver.c"); File.WriteAllText(first, "incoming");
        var existing = Path.Combine(project, "DriversNew"); Directory.CreateDirectory(existing);
        File.WriteAllText(Path.Combine(existing, "driver.c"), "original");
        check("previews renamed destinations and requires existing-folder confirmation", () =>
        {
            var plan = ImportPreview(project, "DriversNew", [first]);
            Expect<InvalidOperationException>(() => ImportApply(plan, false));
            if (Directory.GetFiles(existing).Length != 1) throw new Exception("unconfirmed import wrote files");
            ImportApply(plan, true);
            if (File.ReadAllText(Path.Combine(existing, "driver.c")) != "original" ||
                File.ReadAllText(Path.Combine(existing, "driver (1).c")) != "incoming" || !File.Exists(first))
                throw new Exception("import overwrote existing data or moved source");
        });
        check("creates a new folder and handles incoming duplicate filenames", () =>
        {
            var other = Path.Combine(external, "other"); Directory.CreateDirectory(other);
            var second = Path.Combine(other, "driver.c"); File.WriteAllText(second, "second");
            var plan = ImportPreview(project, "Utilities", [first, second]);
            if (Directory.Exists(Path.Combine(project, "Utilities"))) throw new Exception("preview mutated project");
            ImportApply(plan, false);
            if (File.ReadAllText(Path.Combine(project, "Utilities", "driver.c")) != "incoming" ||
                File.ReadAllText(Path.Combine(project, "Utilities", "driver (1).c")) != "second")
                throw new Exception("duplicate filenames not retained");
        });
        check("rejects invalid names and stale file import previews", () =>
        {
            foreach (var name in new[] { "../escape", "D:\\other", "CON", "bad.", "a/b", ".vscode", "" })
                Expect<ArgumentException>(() => ImportPreview(project, name, [first]));
            var plan = ImportPreview(project, "Stale", [first]);
            Directory.CreateDirectory(Path.Combine(project, "Stale"));
            Expect<IOException>(() => ImportApply(plan, false));
            if (Directory.GetFiles(Path.Combine(project, "Stale")).Length > 0) throw new Exception("stale preview wrote files");
        });
        check("rolls back added files if a later source cannot be read", () =>
        {
            var locked = Path.Combine(external, "locked.h"); File.WriteAllText(locked, "locked");
            var plan = ImportPreview(project, "Rollback", [first, locked]);
            using var stream = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Expect<IOException>(() => ImportApply(plan, false));
            if (Directory.Exists(Path.Combine(project, "Rollback"))) throw new Exception("partial import left files behind");
            if (!File.Exists(first)) throw new Exception("rollback removed source");
        });
        check("environment report explains each missing tool and script", () =>
        {
            var report = Report(ProjectInspector.Inspect(project), new(null, null, null, null, null), "");
            foreach (var text in new[] { "cmake.exe", "ninja.exe", "arm-none-eabi-gcc.exe", "openocd.exe", "cmsis-dap.cfg", "target/" })
                if (!report.Contains(text)) throw new Exception("missing guidance: " + text);
            var fakeExe = Path.Combine(root, "tool.exe"); File.WriteAllText(fakeExe, "");
            var scripts = Path.Combine(root, "good scripts");
            Directory.CreateDirectory(Path.Combine(scripts, "interface")); Directory.CreateDirectory(Path.Combine(scripts, "target"));
            File.WriteAllText(Path.Combine(scripts, "interface", "cmsis-dap.cfg"), "");
            File.WriteAllText(Path.Combine(scripts, "target", "stm32h7x.cfg"), "");
            var validProject = new ProjectInfo(project, true, "STM32H723VGT6", "target/stm32h7x.cfg", null, null, null, null, []);
            if (Report(validProject, new(fakeExe, fakeExe, fakeExe, fakeExe, scripts), "target/stm32h7x.cfg").Length > 0)
                throw new Exception("healthy environment reported missing items");
            var runtime = EnvironmentReport.Create(validProject, new(fakeExe, fakeExe, fakeExe, fakeExe, scripts), "target/stm32h7x.cfg",
                new Dictionary<string, string> { ["Compiler"] = "启动失败" });
            if (runtime.Count != 1 || !runtime[0].Instructions.Contains("启动失败")) throw new Exception("runtime failure guidance absent");
        });
    }

    private static SourceFolderPlan Selected(string root, string folder, string[] files) => SourceFolderPlanner.Preview(root, folder, "app", files);
    private static FileImportPlan ImportPreview(string root, string folder, string[] files) => ProjectFileImporter.Preview(root, folder, files);
    private static void ImportApply(FileImportPlan plan, bool confirmed) => ProjectFileImporter.Apply(plan, confirmed);
    private static string Report(ProjectInfo project, ToolPaths tools, string target) => string.Join("\n",
        EnvironmentReport.Create(project, tools, target, new Dictionary<string, string>()).Select(x => x.Instructions));
    private static void Expect<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new Exception("expected " + typeof(T).Name);
    }
}
