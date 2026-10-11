using System.Diagnostics;
using System.Text;
using CMakeDapLink.Core;

namespace CMakeDapLink.ExtensionHost;

internal sealed record ProcessResult(int ExitCode, string Output);

internal static class HostProcess
{
    public static async Task<ProcessResult> RunAsync(ToolCommand command, TimeSpan limit, CancellationToken cancellation)
    {
        WorkspaceSafety.Ordinary(command.Executable);
        var start = new ProcessStartInfo(command.Executable)
        {
            WorkingDirectory = command.WorkingDirectory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in command.Arguments) start.ArgumentList.Add(argument);
        if (command.PathPrefix.Length != 0) start.Environment["PATH"] = command.PathPrefix + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
        cancellation.ThrowIfCancellationRequested();
        using var process = Process.Start(start) ?? throw new IOException("无法启动工具：" + command.Executable);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(limit);
        var stdout = ReadBoundedAsync(process.StandardOutput);
        var stderr = ReadBoundedAsync(process.StandardError);
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(stdout, stderr);
            cancellation.ThrowIfCancellationRequested();
            throw new TimeoutException("工具运行超时：" + Path.GetFileName(command.Executable));
        }
        return new(process.ExitCode, (await stdout + Environment.NewLine + await stderr).Trim());
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader)
    {
        const int maximum = 4 * 1024 * 1024;
        var text = new StringBuilder(); var buffer = new char[8192]; bool truncated = false;
        while (await reader.ReadAsync(buffer) is var count && count != 0)
        {
            var available = maximum - text.Length;
            if (available > 0) text.Append(buffer, 0, Math.Min(count, available));
            if (count > available) truncated = true;
        }
        if (truncated) text.Append("\n日志超过 4 MB，已截断。\n");
        return text.ToString();
    }
}
