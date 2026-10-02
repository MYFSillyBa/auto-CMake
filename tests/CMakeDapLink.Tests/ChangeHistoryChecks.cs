using System.Text;
using System.Text.Json;
using CMakeDapLink.Core;

internal static class ChangeHistoryChecks
{
    public static void Run(string root, Action<string, Action> check)
    {
        check("renamed application updates old managed tasks without duplicate labels", () =>
        {
            var project = Path.Combine(root, "legacy application name"); Directory.CreateDirectory(project);
            File.WriteAllText(Path.Combine(project, "CMakeLists.txt"), "project(example C)\n");
            var elf = Path.Combine(project, "build", "daplink-debug", "example.elf");
            Directory.CreateDirectory(Path.GetDirectoryName(elf)!); File.WriteAllText(elf, "fixture ELF");
            var options = new SetupOptions(project, "C:/cmake.exe", "C:/ninja.exe", "C:/arm-none-eabi-gcc.exe",
                "C:/openocd.exe", "C:/scripts", "target/stm32h7x.cfg", null, null, null, FirmwareElfPath: elf);
            ConfigurationWriter.Write(options);
            var tasksPath = Path.Combine(project, ".vscode", "tasks.json");
            File.WriteAllText(tasksPath, File.ReadAllText(tasksPath).Replace("由 STM32 工程助手管理", "由 CMake · DAPLink 配置助手管理"));
            ConfigurationWriter.Write(options);
            using var document = JsonDocument.Parse(File.ReadAllText(tasksPath));
            var tasks = document.RootElement.GetProperty("tasks").EnumerateArray().ToArray();
            if (tasks.Length != 3 || tasks.Any(x => x.GetProperty("detail").GetString() != "由 STM32 工程助手管理") || ConfigurationWriter.Preview(options).Count != 0)
                throw new Exception("legacy managed tasks were duplicated or not upgraded");
        });
        check("previews JSONC tasks without writes and preserves comments, user tasks, BOM and settings", () =>
        {
            var project = Path.Combine(root, "change preview");
            var vscode = Path.Combine(project, ".vscode");
            Directory.CreateDirectory(vscode);
            File.WriteAllText(Path.Combine(project, "CMakeLists.txt"), "project(example C)\n");
            var elf = Path.Combine(project, "build", "daplink-debug", "example.elf");
            Directory.CreateDirectory(Path.GetDirectoryName(elf)!); File.WriteAllText(elf, "fixture ELF");
            var tasksPath = Path.Combine(vscode, "tasks.json");
            const string userTask = "{ \"label\": \"user build\", \"type\": \"shell\", \"command\": \"echo user\" }";
            var text = "{\r\n  // workspace note\r\n  \"version\": \"2.0.0\",\r\n  \"custom\": { \"keep\": true },\r\n  \"tasks\": [\r\n    // user task note, keep comma in comment\r\n    "
                + userTask + ",\r\n  ],\r\n}\r\n";
            var original = new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes(text)).ToArray();
            File.WriteAllBytes(tasksPath, original);
            var settings = Path.Combine(vscode, "settings.json");
            File.WriteAllText(settings, "{\"user.setting\":true}");
            var settingsBefore = File.ReadAllBytes(settings);
            var options = new SetupOptions(project, "C:/cmake.exe", "C:/ninja.exe", "C:/arm-none-eabi-gcc.exe",
                "C:/openocd.exe", "C:/scripts", "target/stm32h7x.cfg", null, null, null, FirmwareElfPath: elf);
            var preview = ConfigurationWriter.Preview(options);
            if (preview.Count != 2 || !File.ReadAllBytes(tasksPath).AsSpan().SequenceEqual(original))
                throw new Exception("preview changed a file or missed the task change");
            var after = preview[0].After!;
            var rendered = Encoding.UTF8.GetString(after.AsSpan(3));
            if (!after.AsSpan().StartsWith(new UTF8Encoding(true).GetPreamble()) || !rendered.Contains(userTask)
                || !rendered.Contains("// workspace note") || !rendered.Contains("// user task note, keep comma in comment")
                || !rendered.Contains("\"custom\": { \"keep\": true }") || rendered.Replace("\r\n", "").Contains('\n'))
                throw new Exception("user formatting, comments or encoding were changed");
            using var document = JsonDocument.Parse(rendered, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (document.RootElement.GetProperty("tasks").GetArrayLength() != 4) throw new Exception("managed tasks not present");
            var record = ChangeHistory.Apply(project, "configure fixture", preview) ?? throw new Exception("missing history record");
            if (ConfigurationWriter.Preview(options).Count != 0) throw new Exception("repeated preview is not stable");
            ConfigurationWriter.Write(options);
            if (ChangeHistory.List(project).Count != 1 || !File.ReadAllBytes(settings).AsSpan().SequenceEqual(settingsBefore))
                throw new Exception("no-op created history or changed user settings");
            if (Directory.EnumerateDirectories(project).Any(d => Path.GetFileName(d).Contains("history", StringComparison.OrdinalIgnoreCase))
                || Directory.EnumerateFiles(vscode).Any(f => Path.GetFileName(f).Contains(".bak-", StringComparison.Ordinal)))
                throw new Exception("backup data written inside project");
            ChangeHistory.Restore(project, record.Id);
            if (!File.ReadAllBytes(tasksPath).AsSpan().SequenceEqual(original)) throw new Exception("restore lost original bytes");
        });

        check("records and restores a complete modify, create and delete file batch", () =>
        {
            var project = Path.Combine(root, "batch history"); Directory.CreateDirectory(project);
            var changed = Path.Combine(project, "CMakeLists.txt"); var created = Path.Combine(project, "module", "new.txt");
            var deleted = Path.Combine(project, "old.txt");
            var original = new UnicodeEncoding(false, true).GetPreamble().Concat(Encoding.Unicode.GetBytes("project(original)\r\n")).ToArray();
            File.WriteAllBytes(changed, original); File.WriteAllText(deleted, "original deleted content");
            var deletedBefore = File.ReadAllBytes(deleted);
            var record = ChangeHistory.Apply(project, "three file batch", [new(changed, original, Encoding.UTF8.GetBytes("project(updated)\n")),
                new(created, null, Encoding.UTF8.GetBytes("new file\n")), new(deleted, deletedBefore, null)])!;
            var listed = ChangeHistory.List(project).Single();
            if (listed.Id != record.Id || listed.Changes.Count != 3 || !File.Exists(created) || File.Exists(deleted))
                throw new Exception("incomplete recorded operation");
            var restorePreview = ChangeHistory.GetRestoreChanges(project, record.Id);
            if (restorePreview.Count != 3 || ChangeHistory.GetRestoreConflicts(project, record.Id).Count != 0)
                throw new Exception("wrong restore preview");
            var restoration = ChangeHistory.Restore(project, record.Id, false, restorePreview);
            if (!File.ReadAllBytes(changed).AsSpan().SequenceEqual(original) || File.Exists(created)
                || !File.ReadAllBytes(deleted).AsSpan().SequenceEqual(deletedBefore) || restoration.RestorationOf != record.Id)
                throw new Exception("batch restoration did not restore exact state");
            if (ChangeHistory.List(project).Single(r => r.Id == record.Id).RestoredUtc == null)
                throw new Exception("original record not marked restored");
        });

        check("keeps a user task with the generated label and supports missing or null tasks arrays", () =>
        {
            foreach (var scenario in new[] { "same label", "missing array", "null array", "UTF16" })
            {
                var project = Path.Combine(root, "tasks " + scenario); var vscode = Path.Combine(project, ".vscode");
                Directory.CreateDirectory(vscode); File.WriteAllText(Path.Combine(project, "CMakeLists.txt"), "project(example C)\n");
                var elf = Path.Combine(project, "build", "daplink-debug", "example.elf");
                Directory.CreateDirectory(Path.GetDirectoryName(elf)!); File.WriteAllText(elf, "fixture ELF");
                var tasks = Path.Combine(vscode, "tasks.json");
                var contents = scenario switch
                {
                    "same label" => "{\"version\":\"2.0.0\",\"tasks\":[{ \"label\": \"一键编译\", \"type\": \"shell\", \"command\": \"user-command\" }]}",
                    "null array" => "{\"version\":\"2.0.0\",\"tasks\":null}",
                    _ => "{\"version\":\"2.0.0\", /* keep workspace comment */ \"custom\":true,}"
                };
                File.WriteAllText(tasks, contents, scenario == "UTF16" ? new UnicodeEncoding(false, true) : new UTF8Encoding(false));
                var original = File.ReadAllBytes(tasks);
                var options = new SetupOptions(project, "C:/cmake.exe", "C:/ninja.exe", "C:/arm-none-eabi-gcc.exe", "C:/openocd.exe",
                    "C:/scripts", "target/stm32h7x.cfg", null, null, null, FirmwareElfPath: elf);
                ConfigurationWriter.Write(options);
                var result = File.ReadAllText(tasks);
                using var document = JsonDocument.Parse(result, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                var entries = document.RootElement.GetProperty("tasks").EnumerateArray().ToArray();
                if (entries.Length != (scenario == "same label" ? 4 : 3) || ConfigurationWriter.Preview(options).Count != 0)
                    throw new Exception("task array was not updated stably: " + scenario);
                if (scenario == "same label" && (!result.Contains("{ \"label\": \"一键编译\", \"type\": \"shell\", \"command\": \"user-command\" }")
                    || entries.Where(t => t.GetProperty("label").GetString() == "一键编译").Count() != 1))
                    throw new Exception("user task was overwritten or label collides");
                if (scenario == "UTF16" && !File.ReadAllBytes(tasks).AsSpan().StartsWith(new UnicodeEncoding(false, true).GetPreamble()))
                    throw new Exception("UTF16 BOM was changed");
                ChangeHistory.Restore(project, ChangeHistory.List(project).Single().Id);
                if (!File.ReadAllBytes(tasks).AsSpan().SequenceEqual(original)) throw new Exception("restore changed original bytes: " + scenario);
            }
        });

        check("shows later manual edits and saves them when user chooses overwrite during recovery", () =>
        {
            var project = Path.Combine(root, "manual edit recovery"); Directory.CreateDirectory(project);
            var path = Path.Combine(project, "CMakeLists.txt");
            var before = Encoding.UTF8.GetBytes("project(before)\n"); var after = Encoding.UTF8.GetBytes("project(after)\n");
            File.WriteAllBytes(path, before);
            var operation = ChangeHistory.Apply(project, "configuration edit", [new(path, before, after)])!;
            var manual = Encoding.UTF8.GetBytes("project(after)\n# manual note\n"); File.WriteAllBytes(path, manual);
            var conflicts = ChangeHistory.GetRestoreConflicts(project, operation.Id);
            if (conflicts.Count != 1 || !conflicts[0].After!.AsSpan().SequenceEqual(manual)) throw new Exception("manual edit not visible");
            var preview = ChangeHistory.GetRestoreChanges(project, operation.Id);
            var recovery = ChangeHistory.Restore(project, operation.Id, true, preview);
            if (!File.ReadAllBytes(path).AsSpan().SequenceEqual(before) || !recovery.Changes.Single().Before!.AsSpan().SequenceEqual(manual))
                throw new Exception("recovery did not preserve the overwritten manual edit in its snapshot");
            ChangeHistory.Restore(project, recovery.Id);
            if (!File.ReadAllBytes(path).AsSpan().SequenceEqual(manual)) throw new Exception("cannot undo recovery");
        });
    }
}
