using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using CMakeDapLink.Core;
using CMakeDapLink.TestFixtures;

internal static class FreeRtosRepairChecks
{
    private const string Gnu = "/* FreeRTOS Kernel V10.3.1 */\n#include \"portmacro.h\"\nvoid handler(void) __attribute__((naked));\nvoid handler(void) { __asm volatile(\"mrs r0, basepri\\nvstmdbeq r0!, {s16-s31}\\nvldmiaeq r0!, {s16-s31}\\nbx lr\"); }\n";
    private const string Macro = "/* FreeRTOS Kernel V10.3.1 */\n#define portINLINE __attribute__((always_inline)) inline\n";
    public static void Run(string root, Action<string, Action> check)
    {
        check("FreeRTOS official repair previews two files, keeps originals and restores through history", () =>
        {
            var request = Fixture(root, "official"); var originals = Snapshot(request.Root);
            Require(!CubeMxConverter.CreatePlan(request).CanApply, "Missing GCC port must block conversion before repair.");
            using var client = new HttpClient(new Official());
            var plan = FreeRtosPortRepair.PreviewAsync(request, client: client).GetAwaiter().GetResult();
            Require(plan.Changes.Count == 2 && plan.Changes.All(x => x.Before == null), "Preview should add exactly the pair.");
            Require(originals.All(x => File.ReadAllBytes(x.Key).SequenceEqual(x.Value)) && !Directory.Exists(Gcc(request)), "Preview must not write project files.");
            var saved = FreeRtosPortRepair.Apply(plan);
            Require(saved != null && CubeMxConverter.CreatePlan(request).CanApply, "Repair should unblock matching conversion.");
            Require(originals.All(x => File.ReadAllBytes(x.Key).SequenceEqual(x.Value)), "Existing MDK, kernel and port files must remain unchanged.");
            var again = FreeRtosPortRepair.PreviewAsync(request, client: client).GetAwaiter().GetResult();
            Require(again.Changes.Count == 0, "A complete compatible port needs no download or rewrite.");
            ChangeHistory.Restore(request.Root, saved!.Id);
            Require(!File.Exists(Path.Combine(Gcc(request), "port.c")), "History must restore the missing-file state.");
        });
        check("FreeRTOS local repair detects version changes and never overwrites existing files", () =>
        {
            var request = Fixture(root, "offline"); var local = Path.Combine(root, "local GNU port"); Directory.CreateDirectory(local);
            File.WriteAllText(Path.Combine(local, "port.c"), Gnu); File.WriteAllText(Path.Combine(local, "portmacro.h"), Macro);
            var plan = FreeRtosPortRepair.PreviewAsync(request, local).GetAwaiter().GetResult();
            var kernel = Path.Combine(request.Root, "Middlewares/Third_Party/FreeRTOS/Source/include/task.h");
            File.AppendAllText(kernel, "\n/* changed after preview */");
            Throws(() => FreeRtosPortRepair.Apply(plan));
            Require(!Directory.Exists(Gcc(request)), "Stale dependency inputs must prevent all writes.");
            Directory.CreateDirectory(Gcc(request)); File.WriteAllText(Path.Combine(Gcc(request), "port.c"), "custom port\n");
            Throws(() => FreeRtosPortRepair.PreviewAsync(request, local).GetAwaiter().GetResult());
            Require(File.ReadAllText(Path.Combine(Gcc(request), "port.c")) == "custom port\n", "Do not replace a custom or partial port.");
        });
        check("FreeRTOS rejects an unverified upstream blob and honors cancellation without project writes", () =>
        {
            var request = Fixture(root, "integrity"); using var client = new HttpClient(new Official(corrupt: true));
            Throws(() => FreeRtosPortRepair.PreviewAsync(request, client: client).GetAwaiter().GetResult());
            Require(!Directory.Exists(Gcc(request)), "An integrity failure must leave no partial project directory.");
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            Throws(() => FreeRtosPortRepair.PreviewAsync(request, cancellation: cancelled.Token).GetAwaiter().GetResult());
            Require(!Directory.Exists(Gcc(request)), "Cancellation must not write project files.");
        });
        check("FreeRTOS local import cannot substitute a same-version M3 port for an M4F port", () =>
        {
            var request = Fixture(root, "wrong core"); var local = Path.Combine(root, "M3 local port"); Directory.CreateDirectory(local);
            File.WriteAllText(Path.Combine(local, "port.c"), Gnu.Replace("vstmdbeq r0!, {s16-s31}", "nop").Replace("vldmiaeq r0!, {s16-s31}", "nop"));
            File.WriteAllText(Path.Combine(local, "portmacro.h"), Macro);
            Throws(() => FreeRtosPortRepair.PreviewAsync(request, local).GetAwaiter().GetResult());
            Require(!Directory.Exists(Gcc(request)), "A wrong-core import must not write files.");
        });
    }

    private static ConversionRequest Fixture(string root, string name)
    {
        var project = Path.Combine(root, name); CubeMxFixture.Create(project);
        File.WriteAllText(Path.Combine(project, "ConversionDemo.ioc"), "Mcu.CPN=STM32F407VGT6\nMcu.Name=STM32F407VGTx\nProjectManager.ProjectName=ConversionDemo\nProjectManager.FirmwarePackage=STM32Cube FW_F4 V1.28.0\n");
        var source = Path.Combine(project, "Middlewares/Third_Party/FreeRTOS/Source");
        var port = Path.Combine(source, "portable/RVDS/ARM_CM4F"); Directory.CreateDirectory(port); Directory.CreateDirectory(Path.Combine(source, "include"));
        File.WriteAllText(Path.Combine(port, "port.c"), "/* FreeRTOS Kernel V10.3.1 */\n__asm void handler(void) { bx lr }\n");
        File.WriteAllText(Path.Combine(port, "portmacro.h"), "/* FreeRTOS Kernel V10.3.1 */\n#define portFORCE_INLINE __forceinline\n");
        File.WriteAllText(Path.Combine(source, "include/task.h"), "#define tskKERNEL_VERSION_NUMBER \"V10.3.1\"\n");
        File.WriteAllText(Path.Combine(source, "include/FreeRTOS.h"), "/* FreeRTOS Kernel V10.3.1 */\n");
        File.WriteAllText(Path.Combine(project, "Core/Inc/FreeRTOSConfig.h"), "#define configENABLE_MPU 0\n#define configENABLE_FPU 1\n");
        var mdk = Path.Combine(project, "MDK-ARM", CubeMxFixture.Target + ".uvprojx"); var xml = XDocument.Load(mdk);
        xml.Descendants("Device").Single().Value = "STM32F407VGTx";
        xml.Descendants("AdsCpuType").Single().Value = "\"Cortex-M4\"";
        xml.Descendants("RvdsVP").Single().Value = "2";
        xml.Descendants("Files").First().Add(new XElement("File", new XElement("FileName", "port.c"), new XElement("FileType", "1"), new XElement("FilePath", Path.GetRelativePath(Path.GetDirectoryName(mdk)!, Path.Combine(port, "port.c")))));
        xml.Descendants("Cads").Single().Element("VariousControls")!.Element("IncludePath")!.Value += ";../Middlewares/Third_Party/FreeRTOS/Source/include;../Middlewares/Third_Party/FreeRTOS/Source/portable/RVDS/ARM_CM4F";
        xml.Save(mdk); return new(project, ConversionDirection.MdkToCMake, CubeMxFixture.Target, mdk);
    }
    private static string Gcc(ConversionRequest request) => Path.Combine(request.Root, "Middlewares/Third_Party/FreeRTOS/Source/portable/GCC/ARM_CM4F");
    private static Dictionary<string, byte[]> Snapshot(string root) => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToDictionary(x => x, File.ReadAllBytes);
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Throws(Action action) { try { action(); } catch (Exception) { return; } throw new Exception("Expected a safe rejection."); }
    private sealed class Official(bool corrupt = false) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            object data;
            if (request.RequestUri!.AbsolutePath.Contains("/git/ref/tags/")) data = new { @object = new { type = "commit", sha = new string('a', 40) } };
            else
            {
                var raw = Encoding.UTF8.GetBytes(request.RequestUri.AbsolutePath.EndsWith("portmacro.h") ? Macro : Gnu);
                var blob = Encoding.ASCII.GetBytes($"blob {raw.Length}\0").Concat(raw).ToArray();
                data = new { type = "file", encoding = "base64", content = Convert.ToBase64String(raw), sha = corrupt ? new string('0', 40) : Convert.ToHexString(SHA1.HashData(blob)).ToLowerInvariant() };
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(data), Encoding.UTF8, "application/json") });
        }
    }
}
