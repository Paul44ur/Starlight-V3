using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Starlight.Platform;

/// <summary>
/// A Windows Job Object with KILL_ON_JOB_CLOSE: every child process put in it ends when Starlight ends,
/// even after a crash. Optional limits are used for the code sandbox. On other systems it does nothing.
/// </summary>
public sealed partial class JobObject : IDisposable
{
    private readonly nint _handle;

    public JobObject(long? memoryLimitBytes = null, bool noChildProcesses = false, int? cpuSeconds = null)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        _handle = CreateJobObjectW(0, null);
        if (_handle == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE | JOB_OBJECT_LIMIT_DIE_ON_UNHANDLED_EXCEPTION;
        if (memoryLimitBytes is { } mem)
        {
            info.BasicLimitInformation.LimitFlags |= JOB_OBJECT_LIMIT_PROCESS_MEMORY;
            info.ProcessMemoryLimit = (nuint)mem;
        }
        if (noChildProcesses)
        {
            info.BasicLimitInformation.LimitFlags |= JOB_OBJECT_LIMIT_ACTIVE_PROCESS;
            info.BasicLimitInformation.ActiveProcessLimit = 1;
        }
        if (cpuSeconds is { } cpu)
        {
            info.BasicLimitInformation.LimitFlags |= JOB_OBJECT_LIMIT_PROCESS_TIME;
            info.BasicLimitInformation.PerProcessUserTimeLimit = cpu * 10_000_000L;
        }
        var size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        var ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, ptr, false);
            if (!SetInformationJobObject(_handle, JobObjectExtendedLimitInformation, ptr, (uint)size))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    public void Add(Process process)
    {
        if (_handle != 0 && !AssignProcessToJobObject(_handle, process.Handle))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    /// <summary>Adds a process by its raw handle (the sandbox starts processes itself, suspended).</summary>
    public void AddHandle(nint process)
    {
        if (_handle != 0 && !AssignProcessToJobObject(_handle, process))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    /// <summary>Ends every process in the job now.</summary>
    public void Terminate(uint exitCode = 1)
    {
        if (_handle != 0)
        {
            TerminateJobObject(_handle, exitCode);
        }
    }

    public void Dispose()
    {
        if (_handle != 0)
        {
            CloseHandle(_handle);
        }
    }

    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JOB_OBJECT_LIMIT_PROCESS_TIME = 0x2;
    private const uint JOB_OBJECT_LIMIT_ACTIVE_PROCESS = 0x8;
    private const uint JOB_OBJECT_LIMIT_PROCESS_MEMORY = 0x100;
    private const uint JOB_OBJECT_LIMIT_DIE_ON_UNHANDLED_EXCEPTION = 0x400;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateJobObjectW(nint attributes, string? name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(nint job, int infoClass, nint info, uint length);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(nint job, nint process);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TerminateJobObject(nint job, uint exitCode);
}
