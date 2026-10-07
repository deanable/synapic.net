using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Synapic.Avalonia.Services;

/// <summary>
/// Windows Job Object wrapper that ties a child process's lifetime to this
/// app's: when the app process exits - cleanly, crashed, or killed - the OS
/// terminates every process in the job. This makes "server stops when the
/// app exits" unconditional; no orphaned sidecar can survive between runs
/// (the direct cause of the app reporting "already running" on launch).
/// </summary>
public static class ProcessJob
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;

    // Handles are intentionally kept open for the app's lifetime: closing the
    // last job handle is what triggers kill-on-job-close, so the job must stay
    // referenced until the process itself dies.
    private static readonly List<IntPtr> OpenHandles = new();

    public static void AssignChild(Process process)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var handle = CreateJobObject(IntPtr.Zero, null);
            if (handle == IntPtr.Zero)
            {
                SynapicLog.Warning(nameof(ProcessJob), $"CreateJobObject failed (error {Marshal.GetLastWin32Error()}); the sidecar will not be job-tracked");
                return;
            }

            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;

            var size = Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
            if (!SetInformationJobObject(handle, JobObjectExtendedLimitInformation, ref info, size))
            {
                SynapicLog.Warning(nameof(ProcessJob), $"SetInformationJobObject failed (error {Marshal.GetLastWin32Error()})");
                CloseHandle(handle);
                return;
            }

            if (!AssignProcessToJobObject(handle, process.Handle))
            {
                SynapicLog.Warning(nameof(ProcessJob), $"AssignProcessToJobObject failed (error {Marshal.GetLastWin32Error()})");
                CloseHandle(handle);
                return;
            }

            lock (OpenHandles) OpenHandles.Add(handle);
            SynapicLog.Debug(nameof(ProcessJob), $"Sidecar assigned to kill-on-close job object (pid {process.Id})");
        }
        catch (Exception e)
        {
            SynapicLog.Warning(nameof(ProcessJob), $"Job object assignment failed: {e.Message}");
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(
        IntPtr hJob, int jobObjectInfoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION lpJobObjectInfo, int cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }
}
