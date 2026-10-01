using System.Diagnostics;

namespace CMakeDapLink.App;

internal sealed record CommandResult(int ExitCode, string Output);

internal static class ProcessTools
{
    public static async Task<CommandResult> RunAsync(string executable, IEnumerable<string> arguments, string workingDirectory,
        TimeSpan timeout, Action<string>? onLine = null, string? pathPrefix = null)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        if (!string.IsNullOrWhiteSpace(pathPrefix))
            process.StartInfo.Environment["PATH"] = pathPrefix + Path.PathSeparator +
                (process.StartInfo.Environment["PATH"] ?? Environment.GetEnvironmentVariable("PATH") ?? "");
        var lines = new System.Collections.Concurrent.ConcurrentQueue<string>();
        void ReadLine(object sender, DataReceivedEventArgs e)
        {
            if (e.Data == null) return;
            lines.Enqueue(e.Data);
            onLine?.Invoke(e.Data);
        }
        process.OutputDataReceived += ReadLine;
        process.ErrorDataReceived += ReadLine;
        if (!process.Start()) throw new InvalidOperationException("进程无法启动：" + executable);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        using var cts = new CancellationTokenSource(timeout);
        try { await process.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw new TimeoutException("命令运行超时：" + executable);
        }
        return new(process.ExitCode, string.Join(Environment.NewLine, lines));
    }
}
