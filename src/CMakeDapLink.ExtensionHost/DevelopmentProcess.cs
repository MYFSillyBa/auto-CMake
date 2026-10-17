using System.Diagnostics;
using System.Text;
using CMakeDapLink.Core;

namespace CMakeDapLink.ExtensionHost;

internal sealed record DevelopmentProcessResult(int ExitCode, string Output, bool TimedOut);

/// <summary>Retains diagnostic output even when a bounded hardware inspection times out.</summary>
internal static class DevelopmentProcess
{
    public static async Task<DevelopmentProcessResult> RunAsync(ToolCommand command, TimeSpan limit, CancellationToken cancellation)
    {
        WorkspaceSafety.Ordinary(command.Executable);
        var start = new ProcessStartInfo(command.Executable)
        {
            WorkingDirectory = command.WorkingDirectory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in command.Arguments) start.ArgumentList.Add(argument);
        if (command.PathPrefix.Length > 0) start.Environment["PATH"] = command.PathPrefix + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
        cancellation.ThrowIfCancellationRequested();
        using var process = Process.Start(start) ?? throw new IOException("无法启动诊断工具：" + command.Executable);
        using var job = new WindowsProcessJob();
        try { job.Assign(process); }
        catch
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            throw;
        }
        var stdout = ReadAsync(process.StandardOutput);
        var stderr = ReadAsync(process.StandardError);
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        bounded.CancelAfter(limit);
        var timedOut = false;
        try { await process.WaitForExitAsync(bounded.Token); }
        catch (OperationCanceledException)
        {
            timedOut = !cancellation.IsCancellationRequested;
            job.Dispose();
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        }
        job.Dispose();
        await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.ThrowIfCancellationRequested();
        return new(timedOut ? -1 : process.ExitCode, ((await stdout) + Environment.NewLine + (await stderr)).Trim(), timedOut);
    }

    private static async Task<string> ReadAsync(StreamReader reader)
    {
        const int maximum = 4 * 1024 * 1024;
        var builder = new StringBuilder(); var buffer = new char[8192]; var truncated = false;
        int count;
        while ((count = await reader.ReadAsync(buffer)) != 0)
        {
            var remaining = maximum - builder.Length;
            if (remaining > 0) builder.Append(buffer, 0, Math.Min(remaining, count));
            if (count > remaining) truncated = true;
        }
        if (truncated) builder.AppendLine().Append("日志超过 4 MB，已截断。");
        return builder.ToString();
    }
}
