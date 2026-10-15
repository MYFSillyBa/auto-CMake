using System.Diagnostics;
using System.Text;
using CMakeDapLink.Core;

namespace CMakeDapLink.ExtensionHost;

internal sealed record ProcessResult(int ExitCode, string Output);

internal static class HostProcess
{
    public static async Task<ProcessResult> RunAsync(ToolCommand command, TimeSpan limit, CancellationToken cancellation, Func<string, string>? display = null)
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
        using var job = new WindowsProcessJob();
        using var process = Process.Start(start) ?? throw new IOException("无法启动工具：" + command.Executable);
        try { job.Assign(process); }
        catch
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            throw;
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(limit);
        var stdout = ReadBoundedAsync(process.StandardOutput, display);
        var stderr = ReadBoundedAsync(process.StandardError, display);
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            job.Dispose();
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.ThrowIfCancellationRequested();
            throw new TimeoutException("工具运行超时：" + Path.GetFileName(command.Executable));
        }
        job.Dispose();
        await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(5));
        return new(process.ExitCode, (await stdout + Environment.NewLine + await stderr).Trim());
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, Func<string, string>? display)
    {
        const int maximum = 4 * 1024 * 1024;
        var text = new StringBuilder(); var buffer = new char[8192]; bool truncated = false;
        while (await reader.ReadAsync(buffer) is var count && count != 0)
        {
            var available = maximum - text.Length;
            if (available > 0) text.Append(buffer, 0, Math.Min(count, available));
            if (count > available) truncated = true;
        }
        if (truncated) text.Append('\n').Append(display?.Invoke("日志超过 4 MB，已截断。") ?? "日志超过 4 MB，已截断。").Append('\n');
        return text.ToString();
    }
}
