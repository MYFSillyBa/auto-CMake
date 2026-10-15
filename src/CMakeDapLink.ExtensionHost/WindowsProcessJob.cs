using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace CMakeDapLink.ExtensionHost;

// Children inherit the host job at creation, closing the launch/assignment race.
// Each command also has its own nested job for cancellation and normal cleanup.
internal sealed class WindowsProcessJob : IDisposable
{
    private static WindowsProcessJob? hostLifetimeJob;
    private readonly JobHandle? handle;

    public WindowsProcessJob()
    {
        if (!OperatingSystem.IsWindows()) return;
        handle = CreateJobObjectW(IntPtr.Zero, null);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        var limits = new ExtendedLimitInformation { BasicLimitInformation = new() { LimitFlags = 0x00002000 } };
        if (!SetInformationJobObject(handle, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimitInformation>()))
        {
            var error = Marshal.GetLastWin32Error(); handle.Dispose();
            throw new Win32Exception(error);
        }
    }

    public static void ProtectHost()
    {
        if (hostLifetimeJob != null || !OperatingSystem.IsWindows()) return;
        var job = new WindowsProcessJob();
        try { using var host = Process.GetCurrentProcess(); job.Assign(host); hostLifetimeJob = job; }
        catch { job.Dispose(); throw; }
        // Deliberately retain the handle for the process lifetime. Windows closes it
        // when the host exits, including termination by the extension bridge.
    }

    public void Assign(Process process)
    {
        if (handle == null) return;
        if (AssignProcessToJobObject(handle, process.Handle)) return;
        var error = Marshal.GetLastWin32Error();
        if (process.HasExited) return;
        throw new Win32Exception(error);
    }

    public void Dispose() => handle?.Dispose();

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimitInformation
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimitInformation
    {
        public BasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    private sealed class JobHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public JobHandle() : base(true) { }
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern JobHandle CreateJobObjectW(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(JobHandle job, int informationClass, ref ExtendedLimitInformation information, uint length);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(JobHandle job, IntPtr process);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
