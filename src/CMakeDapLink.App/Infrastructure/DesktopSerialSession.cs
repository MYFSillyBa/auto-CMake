using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CMakeDapLink.App;

internal sealed record DesktopSerialEvent(string Kind, byte[]? Bytes = null, string? Error = null,
    string? Port = null, int Baud = 0);

// The serial API is provided by Windows PowerShell's .NET Framework runtime.
// Raw bytes cross the helper boundary only as canonical base64, never as command text.
internal sealed class DesktopSerialSession : IDisposable
{
    private readonly Action<DesktopSerialEvent> _onEvent;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _commands = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly StringBuilder _errors = new();
    private Process? _process;
    private TaskCompletionSource<JsonElement>? _pending;
    private string? _expected;
    private bool _disposed;

    public DesktopSerialSession(Action<DesktopSerialEvent> onEvent) => _onEvent = onEvent;

    public async Task<string[]> ListAsync()
    {
        var result = await CommandAsync("LIST", "ports");
        if (result.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Invalid serial port response.");
        return result.EnumerateArray().Select(item => item.GetString() ?? "")
            .Where(name => Regex.IsMatch(name, @"\ACOM[1-9]\d{0,3}\z", RegexOptions.IgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public Task ConnectAsync(string port, int baud)
    {
        if (!Regex.IsMatch(port, @"\ACOM[1-9]\d{0,3}\z", RegexOptions.IgnoreCase) || baud is < 50 or > 4000000)
            throw new ArgumentException("Use a valid COM port and a baud rate from 50 to 4000000.");
        return CommandAsync("OPEN\t" + port.ToUpperInvariant() + "\t" + baud.ToString(CultureInfo.InvariantCulture), "opened");
    }

    public Task SendAsync(byte[] bytes)
    {
        if (bytes.Length > 65536) throw new ArgumentException("Serial payload exceeds 64 KiB.");
        return CommandAsync("SEND\t" + Convert.ToBase64String(bytes), "sent");
    }

    public Task DisconnectAsync()
    {
        lock (_sync) if (_process == null) return Task.CompletedTask;
        return CommandAsync("CLOSE", "closed");
    }

    private async Task<JsonElement> CommandAsync(string command, string expected)
    {
        await _commands.WaitAsync(_lifetime.Token).ConfigureAwait(false);
        try
        {
            var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            Process process;
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                Start(); process = _process!; _pending = completion; _expected = expected;
            }
            try
            {
                await process.StandardInput.WriteLineAsync(command).ConfigureAwait(false);
                await process.StandardInput.FlushAsync().ConfigureAwait(false);
                return await completion.Task.WaitAsync(TimeSpan.FromSeconds(12), _lifetime.Token).ConfigureAwait(false);
            }
            catch (TimeoutException) { Stop(); throw new TimeoutException("Serial operation timed out. The port has been closed."); }
            catch (IOException) { Stop(); throw; }
            finally
            {
                lock (_sync) if (ReferenceEquals(_pending, completion)) { _pending = null; _expected = null; }
            }
        }
        finally { _commands.Release(); }
    }

    private void Start()
    {
        if (_process != null) return;
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Serial sessions require local Windows.");
        var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        var script = "$ErrorActionPreference='Stop'; Add-Type -TypeDefinition @'\n" + HelperProgram +
            "\n'@ -ReferencedAssemblies 'System.dll','System.Core.dll','System.Web.Extensions.dll'; [Stm32DesktopSerial]::Run()";
        var process = new Process
        {
            StartInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            },
            EnableRaisingEvents = true
        };
        foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) })
            process.StartInfo.ArgumentList.Add(argument);
        process.OutputDataReceived += (_, args) => { if (args.Data != null) Receive(process, args.Data); };
        process.ErrorDataReceived += (_, args) =>
        {
            if (args.Data == null) return;
            lock (_sync) { _errors.AppendLine(args.Data); if (_errors.Length > 8192) _errors.Remove(0, _errors.Length - 8192); }
        };
        process.Exited += (_, _) =>
        {
            string error;
            lock (_sync)
            {
                if (!ReferenceEquals(_process, process)) return;
                _process = null; error = _errors.Length > 0 ? _errors.ToString() : "Serial helper exited. The port is closed.";
                _pending?.TrySetException(new IOException(error));
            }
            _onEvent(new("error", Error: error)); _onEvent(new("closed"));
            process.Dispose();
        };
        _errors.Clear();
        try
        {
            if (!process.Start()) throw new InvalidOperationException("Unable to start the serial helper.");
            _process = process; process.BeginOutputReadLine(); process.BeginErrorReadLine();
        }
        catch { process.Dispose(); _process = null; throw; }
    }

    private void Receive(Process process, string line)
    {
        lock (_sync) if (_disposed || !ReferenceEquals(_process, process)) return;
        try
        {
            if (line.Length > 100000) throw new InvalidDataException("Serial helper message exceeds the allowed size.");
            using var document = JsonDocument.Parse(line);
            var message = document.RootElement; var kind = message.GetProperty("type").GetString(); var data = message.GetProperty("data");
            if (kind is "data" or "sent") _onEvent(new(kind, Bytes: DecodeBase64(data.GetString() ?? "")));
            else if (kind == "opened") _onEvent(new(kind, Port: data.GetProperty("name").GetString(), Baud: data.GetProperty("baud").GetInt32()));
            else if (kind == "closed") _onEvent(new(kind));
            if (kind == "error")
            {
                var error = data.GetString() ?? "Serial operation failed.";
                lock (_sync) _pending?.TrySetException(new IOException(error));
                _onEvent(new("error", Error: error));
            }
            else lock (_sync) if (_expected == kind) _pending?.TrySetResult(data.Clone());
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or FormatException or InvalidOperationException or KeyNotFoundException or ArgumentException)
        {
            lock (_sync) _pending?.TrySetException(new InvalidDataException("Invalid serial helper response.", ex));
            _onEvent(new("error", Error: "Invalid serial helper response: " + ex.Message));
            Stop();
        }
    }

    internal static byte[] CreatePayload(string text, string mode, string newline)
    {
        if (text.Length > 131072) throw new ArgumentException("Serial payload exceeds 64 KiB.");
        byte[] bytes;
        if (mode == "HEX")
        {
            var hex = string.Concat(text.Where(character => !char.IsWhiteSpace(character)));
            if (hex.Length == 0 || hex.Length % 2 != 0 || !hex.All(Uri.IsHexDigit))
                throw new ArgumentException("HEX requires complete hexadecimal bytes, for example 01 A5 FF.");
            bytes = Convert.FromHexString(hex);
        }
        else
        {
            if (mode != "ASCII" || text.Any(character => character > 127)) throw new ArgumentException("ASCII mode accepts only ASCII characters.");
            bytes = Encoding.ASCII.GetBytes(text);
        }
        var tail = newline switch { "none" => Array.Empty<byte>(), "LF" => new byte[] { 10 }, "CRLF" => [13, 10], _ => throw new ArgumentException("Choose none, LF, or CRLF for the line ending.") };
        if (bytes.Length + tail.Length > 65536) throw new ArgumentException("Serial payload exceeds 64 KiB.");
        return [.. bytes, .. tail];
    }

    internal static byte[] DecodeBase64(string text)
    {
        if (text.Length > 87384) throw new ArgumentException("Serial payload exceeds 64 KiB.");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(text); }
        catch (FormatException ex) { throw new ArgumentException("Invalid serial base64 payload.", ex); }
        if (bytes.Length > 65536 || Convert.ToBase64String(bytes) != text) throw new ArgumentException("Serial payload must use canonical base64.");
        return bytes;
    }

    internal static string FormatBytes(byte[] bytes, string mode)
    {
        if (mode == "HEX") return string.Join(' ', bytes.Select(value => value.ToString("X2", CultureInfo.InvariantCulture)));
        var text = new StringBuilder();
        foreach (var value in bytes)
            if (value is >= 32 and <= 126 or 9 or 10 or 13) text.Append((char)value);
            else text.Append("\\x").Append(value.ToString("X2", CultureInfo.InvariantCulture));
        return text.ToString();
    }

    private void Stop()
    {
        Process? process;
        lock (_sync)
        {
            process = _process; _process = null;
            _pending?.TrySetException(new IOException("Serial helper stopped. The port is closed."));
        }
        if (process != null)
        {
            try { process.StandardInput.Close(); if (!process.WaitForExit(250)) process.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception) { }
            finally { process.Dispose(); }
        }
        _onEvent(new("closed"));
    }

    public void Dispose()
    {
        lock (_sync) { if (_disposed) return; _disposed = true; }
        _lifetime.Cancel(); Stop();
    }

    private const string HelperProgram = """
using System; using System.IO.Ports; using System.Threading; using System.Threading.Tasks; using System.Web.Script.Serialization;
public static class Stm32DesktopSerial {
 static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
 static void Emit(string type, object data) { Console.WriteLine(Json.Serialize(new { type, data })); Console.Out.Flush(); }
 public static void Run() {
  Console.InputEncoding = new System.Text.UTF8Encoding(false); Console.OutputEncoding = new System.Text.UTF8Encoding(false);
  SerialPort port = null; Task<string> input = Task.Run(() => Console.ReadLine());
  try { while (true) {
   if (input.IsCompleted) {
    string line = input.GetAwaiter().GetResult(); if (line == null) break; string[] parts = line.Split('\t');
    try {
     if (parts[0] == "LIST" && parts.Length == 1) { var names = SerialPort.GetPortNames(); Array.Sort(names); Emit("ports", names); }
     else if (parts[0] == "OPEN" && parts.Length == 3) {
      if (!System.Text.RegularExpressions.Regex.IsMatch(parts[1], @"\ACOM[1-9]\d{0,3}\z")) throw new ArgumentException("Invalid COM port.");
      int baud = int.Parse(parts[2]); if (baud < 50 || baud > 4000000) throw new ArgumentException("Invalid baud rate.");
      if (port != null) { port.Dispose(); port = null; }
      SerialPort candidate = new SerialPort(parts[1], baud, Parity.None, 8, StopBits.One);
      try { candidate.ReadTimeout = 200; candidate.WriteTimeout = 1000; candidate.DtrEnable = false; candidate.RtsEnable = false; candidate.Open(); port = candidate; }
      catch { candidate.Dispose(); throw; }
      Emit("opened", new { name = parts[1], baud = port.BaudRate });
     } else if (parts[0] == "SEND" && parts.Length == 2) {
      if (port == null || !port.IsOpen) throw new InvalidOperationException("Serial port is not open.");
      if (parts[1].Length > 87384) throw new ArgumentException("Serial payload exceeds 64 KiB.");
      byte[] bytes = Convert.FromBase64String(parts[1]);
      if (bytes.Length > 65536 || Convert.ToBase64String(bytes) != parts[1]) throw new ArgumentException("Invalid serial base64 payload.");
      port.Write(bytes, 0, bytes.Length); Emit("sent", parts[1]);
     } else if (parts[0] == "CLOSE" && parts.Length == 1) { if (port != null) { port.Dispose(); port = null; } Emit("closed", true); }
     else throw new ArgumentException("Invalid serial command.");
    } catch (Exception e) { Emit("error", e.Message); }
    input = Task.Run(() => Console.ReadLine());
   }
   if (port != null && port.IsOpen) {
    try { int count = Math.Min(port.BytesToRead, 4096); if (count > 0) { byte[] bytes = new byte[count]; count = port.Read(bytes, 0, count); Emit("data", Convert.ToBase64String(bytes, 0, count)); } }
    catch (Exception e) { Emit("error", e.Message); port.Dispose(); port = null; Emit("closed", true); }
   }
   Thread.Sleep(15);
  } } finally { if (port != null) port.Dispose(); }
 }
}
""";
}
