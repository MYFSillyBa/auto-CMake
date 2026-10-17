using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CMakeDapLink.Core;

public sealed record ProbeCheckEvidence(bool ProbeFound, bool ChipConnected);
public sealed record DevelopmentTask(string Name, string State, int? Priority = null, long? StackHighWater = null);
public sealed record DevelopmentRtosSnapshot(IReadOnlyList<DevelopmentTask> Tasks, string[] Notes, bool FreeRtosAvailable, bool Detached);

/// <summary>Builds fixed, read-only diagnostic commands; never performs a connection itself.</summary>
public static class DevelopmentHardware
{
    public const string ProbeWarning = "硬件检查会连接探针与芯片，可能暂停或复位目标；不会烧录固件。";
    public const string RtosWarning = "RTOS 刷新会连接已有的本机 GDB 服务，可能暂停目标；读取后尝试分离，不会烧录固件。";
    public const string ExamineMarker = "STM32_HELPER_CHIP_CONNECTED=1";
    public const string SnapshotMarker = "STM32_HELPER_RTOS=";

    public static IReadOnlyList<string> ProbeArguments(SetupOptions options)
    {
        var probe = OpenOcdProbe.Resolve(options);
        return probe.Arguments(options).Concat([
            "-c", "gdb_port disabled", "-c", "tcl_port disabled", "-c", "telnet_port disabled",
            "-c", "foreach t [target names] { set stm32_helper_event [$t cget -event examine-end]; append stm32_helper_event {; echo " + ExamineMarker + "}; $t configure -event examine-end $stm32_helper_event }",
            "-c", "init", "-c", "targets", "-c", "shutdown"
        ]).ToArray();
    }

    public static ProbeCheckEvidence ParseProbeCheck(string output, int exitCode)
    {
        var probeFound = Regex.IsMatch(output, @"(?im)^\s*(?:Info\s*:\s*)?(?:CMSIS-DAP:\s*(?:SWD supported|JTAG supported|Interface Initialised|FW Version)|STLINK\s+V\S+.*(?:API|VID)|ST-Link.*(?:Serial|Firmware)|J-Link.*(?:Serial|Hardware|V\d.*compiled))");
        var chipConnected = exitCode == 0 && output.Split('\n').Any(line => line.Trim() == ExamineMarker);
        // A successfully examined chip also proves a live adapter. Script loading alone never does.
        return new(probeFound || chipConnected, chipConnected);
    }

    public static string LocalEndpoint(string? host, int port)
    {
        host = string.IsNullOrWhiteSpace(host) ? "127.0.0.1" : host.Trim();
        if (port is < 1 or > 65535) throw new ArgumentException("GDB 端口必须为 1–65535。");
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return "127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture);
        if (!IPAddress.TryParse(host.Trim('[', ']'), out var address) || !IPAddress.IsLoopback(address)) throw new ArgumentException("RTOS 只允许连接本机已有的 GDB 服务。");
        return (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? "[" + address + "]" : address.ToString()) + ":" + port.ToString(CultureInfo.InvariantCulture);
    }

    public static IReadOnlyList<string> GdbArguments(string elf, string endpoint, bool extendedRemote = false)
    {
        // Endpoint is validated again here because it is interpolated into a GDB command.
        var match = Regex.Match(endpoint, @"\A(?<host>127(?:\.\d{1,3}){3}|\[::1\]):(?<port>\d{1,5})\z");
        if (!match.Success) throw new ArgumentException("RTOS 只允许连接本机已有的 GDB 服务。");
        LocalEndpoint(match.Groups["host"].Value, int.Parse(match.Groups["port"].Value, CultureInfo.InvariantCulture));
        if (!Path.IsPathFullyQualified(elf)) throw new ArgumentException("请选择当前构建配置的 ELF 固件。");
        var script = PythonSnapshot.Replace("@TARGET@", (extendedRemote ? "target extended-remote " : "target remote ") + endpoint, StringComparison.Ordinal);
        return ["--nx", "--nh", "--quiet", "--batch", "-iex", "set auto-load off", "-iex", "set debuginfod enabled off", "-iex", "set pagination off", "-iex", "set confirm off", "-iex", "set remotetimeout 4", "--se=" + elf, "-ex", "python exec(" + JsonSerializer.Serialize(script) + ")"];
    }

    public static DevelopmentRtosSnapshot ParseRtos(string output)
    {
        var line = output.Split('\n').LastOrDefault(x => x.StartsWith(SnapshotMarker, StringComparison.Ordinal));
        if (line == null) return new([], ["GDB 未返回 RTOS 快照；请检查本机服务、ELF 调试符号及 GDB 的 Python 支持。"], false, false);
        try
        {
            using var document = JsonDocument.Parse(line[SnapshotMarker.Length..]);
            var root = document.RootElement;
            var tasks = root.GetProperty("tasks").EnumerateArray().Take(512).Select(task => new DevelopmentTask(
                task.GetProperty("name").GetString() ?? "", task.GetProperty("state").GetString() ?? "unknown",
                task.TryGetProperty("priority", out var priority) && priority.ValueKind == JsonValueKind.Number && priority.TryGetInt32(out var p) ? p : null,
                task.TryGetProperty("stackHighWater", out var stack) && stack.ValueKind == JsonValueKind.Number && stack.TryGetInt64(out var s) ? s : null)).ToArray();
            return new(tasks, root.GetProperty("notes").EnumerateArray().Select(x => x.GetString() ?? "").ToArray(), root.GetProperty("freeRtosAvailable").GetBoolean(), root.GetProperty("detached").GetBoolean());
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        { return new([], ["GDB 返回的 RTOS 快照格式无效。"], false, false); }
    }

    // Traverse the actual kernel lists and read TCB fields. No inferior function calls,
    // memory writes, reset, run, load, or download command is used.
    private const string PythonSnapshot = """
import gdb, json, time
snapshot = {'tasks': [], 'notes': [], 'freeRtosAvailable': False, 'detached': False}
connected = False
deadline = time.monotonic() + 10.0
def note(value):
    if value not in snapshot['notes']:
        snapshot['notes'].append(value)
def bounded():
    if time.monotonic() > deadline:
        raise RuntimeError('RTOS 符号读取达到时间限制。')
def value(expression):
    bounded()
    return gdb.parse_and_eval(expression)
def add_task(pointer, state, current):
    bounded()
    address = int(pointer)
    if not address or address in seen:
        return
    if len(seen) >= 512:
        raise RuntimeError('RTOS 任务数量超过 512。')
    seen.add(address)
    tcb = pointer.cast(tcb_type.pointer()).dereference()
    if state == 'suspended':
        try:
            if int(tcb['xEventListItem']['pvContainer']) != 0:
                state = 'blocked'
        except Exception:
            state = 'suspended-or-blocked'
    task = {'name': tcb['pcTaskName'].string(errors='replace', length=min(128, int(tcb['pcTaskName'].type.sizeof))).split('\x00')[0], 'state': 'running' if address in current else state, 'priority': int(tcb['uxPriority']), 'stackHighWater': None}
    # A measured fill-pattern count is useful only when the ELF exposes both
    # stack bounds and the known-fill/high-water feature. Otherwise return null.
    try:
        fields = {field.name for field in tcb_type.fields()}
        fill_enabled = gdb.lookup_global_symbol('uxTaskGetStackHighWaterMark') is not None or gdb.lookup_global_symbol('uxTaskGetStackHighWaterMark2') is not None
        for flag in ('INCLUDE_uxTaskGetStackHighWaterMark', 'INCLUDE_uxTaskGetStackHighWaterMark2', 'configUSE_TRACE_FACILITY'):
            try:
                fill_enabled = fill_enabled or int(value(flag)) > 0
            except Exception:
                pass
        try:
            fill_enabled = fill_enabled or int(value('configCHECK_FOR_STACK_OVERFLOW')) > 1
        except Exception:
            pass
        if 'pxEndOfStack' in fields and fill_enabled:
            low = int(tcb['pxStack'])
            high = int(tcb['pxEndOfStack'])
            unit = int(tcb['pxStack'].type.target().sizeof)
            if high > low and high - low <= 1024 * 1024:
                growth = int(value('portSTACK_GROWTH'))
                remaining = high - low + unit
                count = 0
                while remaining > 0:
                    bounded()
                    size = min(256, remaining)
                    start = low + count if growth < 0 else high + unit - count - size
                    data = bytes(gdb.selected_inferior().read_memory(start, size))
                    if growth > 0:
                        data = data[::-1]
                    filled = 0
                    for byte in data:
                        if byte != 0xa5:
                            break
                        filled += 1
                    count += filled
                    remaining -= size
                    if filled != size:
                        break
                task['stackHighWater'] = count // unit
    except Exception:
        pass
    snapshot['tasks'].append(task)
def walk(kernel_list, state, current):
    bounded()
    if kernel_list.type.strip_typedefs().code == gdb.TYPE_CODE_PTR:
        kernel_list = kernel_list.dereference()
    size = int(kernel_list['uxNumberOfItems'])
    if size < 0 or size > 512:
        raise RuntimeError('FreeRTOS 列表长度无效或不可读取。')
    end = kernel_list['xListEnd'].address
    item = kernel_list['xListEnd']['pxNext']
    visited = set()
    for index in range(size):
        bounded()
        if int(item) == int(end) or int(item) in visited:
            raise RuntimeError('FreeRTOS 列表已变化或损坏。')
        visited.add(int(item))
        node = item.dereference()
        add_task(node['pvOwner'], state, current)
        item = node['pxNext']
try:
    gdb.execute('@TARGET@', to_string=True)
    connected = True
    try:
        print(gdb.execute('info threads', to_string=True))
    except Exception as error:
        note('GDB 线程信息不可用：' + str(error))
    try:
        try:
            tcb_type = gdb.lookup_type('TCB_t').strip_typedefs()
        except Exception:
            tcb_type = gdb.lookup_type('struct tskTaskControlBlock').strip_typedefs()
        ready = value('pxReadyTasksLists')
        current = set()
        try:
            current.add(int(value('pxCurrentTCB')))
        except Exception:
            try:
                running = value('pxCurrentTCBs')
                bounds = running.type.strip_typedefs().range()
                for index in range(bounds[0], min(bounds[1] + 1, bounds[0] + 32)):
                    current.add(int(running[index]))
            except Exception:
                note('当前 FreeRTOS 任务符号不可用；运行中的任务可能显示为就绪。')
        seen = set()
        bounds = ready.type.strip_typedefs().range()
        if bounds[1] - bounds[0] > 255:
            raise RuntimeError('FreeRTOS 就绪列表数组超过支持范围。')
        for index in range(bounds[0], bounds[1] + 1):
            walk(ready[index], 'ready', current)
        snapshot['freeRtosAvailable'] = True
        for symbol, state in [('xDelayedTaskList1', 'blocked'), ('xDelayedTaskList2', 'blocked'), ('xSuspendedTaskList', 'suspended'), ('xTasksWaitingTermination', 'deleted')]:
            try:
                walk(value(symbol), state, current)
            except Exception as error:
                note('FreeRTOS 列表不可用（' + symbol + '）：' + str(error))
        if any(task['stackHighWater'] is None for task in snapshot['tasks']):
            note('无法确认栈边界、增长方向或填充配置，栈高水位返回 null，不能视为测量值。')
    except Exception as error:
        note('当前符号或内核结构不支持 FreeRTOS TCB 读取：' + str(error))
    if not snapshot['freeRtosAvailable']:
        snapshot['tasks'] = []
        try:
            for thread in gdb.selected_inferior().threads()[:512]:
                snapshot['tasks'].append({'name': thread.name or ('Thread ' + str(thread.num)), 'state': 'running' if thread.is_running() else ('stopped' if thread.is_stopped() else 'exited'), 'priority': None, 'stackHighWater': None})
            note('当前仅显示 GDB 线程；FreeRTOS 优先级与栈数据不可用。')
        except Exception as error:
            note('普通 GDB 线程也不可用：' + str(error))
except Exception as error:
    note('GDB 连接或读取失败：' + str(error))
finally:
    if connected:
        try:
            gdb.execute('detach', to_string=True)
            snapshot['detached'] = True
        except Exception as error:
            note('GDB 分离失败，请在调试器中确认目标状态：' + str(error))
    print('STM32_HELPER_RTOS=' + json.dumps(snapshot, ensure_ascii=True))
""";
}
