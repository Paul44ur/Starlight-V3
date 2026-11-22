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

    /// <summary>
    /// Starts a program inside the job before it runs a single instruction: it is created suspended, put in the
    /// job and then resumed, so children it starts at once (a browser does) cannot escape the job.
    /// No window, no redirected output. On other systems it is a plain start.
    /// </summary>
    public Process Start(string exe, IEnumerable<string> args)
    {
        if (!OperatingSystem.IsWindows())
        {
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in args)
            {
                psi.ArgumentList.Add(a);
            }
            return Process.Start(psi) ?? throw new InvalidOperationException("The process did not start.");
        }
        var cmd = Marshal.StringToHGlobalUni(CommandLine(exe, args));
        try
        {
            var si = new STARTUPINFOW { cb = Marshal.SizeOf<STARTUPINFOW>() };
            if (!CreateProcessW(exe, cmd, 0, 0, false, CREATE_SUSPENDED | CREATE_NO_WINDOW | CREATE_UNICODE_ENVIRONMENT, 0, null, ref si, out var pi))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            try
            {
                if (!AssignProcessToJobObject(_handle, pi.hProcess))
                {
                    var error = Marshal.GetLastWin32Error();
                    TerminateProcess(pi.hProcess, 1);
                    throw new Win32Exception(error);
                }
                var process = Process.GetProcessById(pi.dwProcessId);
                _ = process.Handle; // opened now, so the exit code stays readable after a quick exit
                ResumeThread(pi.hThread);
                return process;
            }
            finally
            {
                CloseHandle(pi.hThread);
                CloseHandle(pi.hProcess);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(cmd);
        }
    }

    /// <summary>A Windows command line that the C runtime splits back into exactly these arguments.</summary>
    public static string CommandLine(string exe, IEnumerable<string> args)
    {
        var sb = new System.Text.StringBuilder();
        Quote(sb, exe);
        foreach (var a in args)
        {
            sb.Append(' ');
            Quote(sb, a);
        }
        return sb.ToString();
    }

    private static void Quote(System.Text.StringBuilder sb, string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny([' ', '\t', '\n', '\v', '"']) < 0)
        {
            sb.Append(arg);
            return;
        }
        sb.Append('"');
        var slashes = 0;
        foreach (var c in arg)
        {
            if (c == '\\')
            {
                slashes++;
                continue;
            }
            // Backslashes count only before a quote: double them there, and escape the quote.
            sb.Append('\\', c == '"' ? slashes * 2 + 1 : slashes);
            slashes = 0;
            sb.Append(c);
        }
        sb.Append('\\', slashes * 2);
        sb.Append('"');
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

    private const uint CREATE_SUSPENDED = 0x4;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x400;
    private const uint CREATE_NO_WINDOW = 0x08000000;

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFOW
    {
        public int cb;
        public nint lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public nint lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public nint hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateProcessW(string app, nint cmd, nint pa, nint ta, [MarshalAs(UnmanagedType.Bool)] bool inherit,
        uint flags, nint env, string? dir, ref STARTUPINFOW si, out PROCESS_INFORMATION pi);

    [LibraryImport("kernel32.dll")]
    private static partial uint ResumeThread(nint thread);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TerminateProcess(nint process, uint exitCode);

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
