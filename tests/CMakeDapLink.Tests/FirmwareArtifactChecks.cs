using System.Text.Json;
using CMakeDapLink.Core;

internal static class FirmwareArtifactChecks
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "FirmwareArtifacts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var reply = Path.Combine(root, ".cmake", "api", "v1", "reply");
            Directory.CreateDirectory(reply);
            void Write(string name, object value) => File.WriteAllText(Path.Combine(reply, name), JsonSerializer.Serialize(value));
            object Target(string name, string type, string path)
            {
                Write(name + ".json", new { type, artifacts = new[] { new { path } } });
                var output = Path.Combine(root, path); Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                File.WriteAllText(output, "artifact");
                return new { jsonFile = name + ".json" };
            }
            var debug = Target("debug", "EXECUTABLE", "Debug/current.elf");
            var loader = Target("loader", "EXECUTABLE", "Debug/loader.elf");
            var release = Target("release", "EXECUTABLE", "Release/current.elf");
            var library = Target("library", "STATIC_LIBRARY", "Debug/library.elf");
            File.WriteAllText(Path.Combine(root, "Debug", "Chassis.elf"), "obsolete target");
            Write("current.json", new { paths = new { build = root }, configurations = new[] {
                new { name = "Debug", targets = new[] { debug, loader, library } },
                new { name = "Release", targets = new[] { release } }
            } });
            Write("old.json", new { paths = new { build = root }, configurations = new[] {
                new { name = "Debug", targets = new[] { release } }
            } });
            object Index(string model) => new { objects = new[] { new { kind = "codemodel", version = new { major = 2 }, jsonFile = model } } };
            Write("index-2026-06-04.json", Index("old.json"));
            Write("index-2026-10-01.json", Index("current.json"));
            var found = CMakeBuildPlan.FindElfs(root, "Debug");
            if (found.Count != 2 || !found.Contains(Path.Combine(root, "Debug", "current.elf")) ||
                !found.Contains(Path.Combine(root, "Debug", "loader.elf"))) throw new Exception("current executable targets mismatch");
            if (CMakeBuildPlan.FindElfs(root, "release").Single() != Path.Combine(root, "Release", "current.elf"))
                throw new Exception("unambiguous configuration spelling did not resolve to the actual configuration");
            var relWithDebInfo = Target("relwithdebinfo", "EXECUTABLE", "updated.elf");
            Write("single.json", new { paths = new { build = root }, configurations = new[] {
                new { name = "RelWithDebInfo", targets = new[] { relWithDebInfo } }
            } });
            void WriteGeneratorIndex(bool multiConfig) => Write("index-2026-11-28.json", new
            {
                cmake = new { generator = new { multiConfig } },
                objects = new[] { new { kind = "codemodel", version = new { major = 2 }, jsonFile = "single.json" } }
            });
            WriteGeneratorIndex(false);
            if (CMakeBuildPlan.FindElfs(root, "Debug").Single() != Path.Combine(root, "updated.elf"))
                throw new Exception("single-config generator must select the actual CMake configuration");
            WriteGeneratorIndex(true);
            try
            {
                CMakeBuildPlan.FindElfs(root, "Debug");
                throw new Exception("multi-config generator must require the requested configuration even with one entry");
            }
            catch (InvalidOperationException) { }
            CurrentFirmwareArtifacts.PrepareQuery(root);
            if (!File.Exists(Path.Combine(root, ".cmake", "api", "v1", "query", "client-cmake-daplink", "codemodel-v2")))
                throw new Exception("missing artifact query");
        }
        finally { Directory.Delete(root, true); }
    }
}
