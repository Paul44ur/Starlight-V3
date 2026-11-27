using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Serilog;
using Starlight.Platform;

namespace Starlight.Tools.Code;

public sealed record SandboxResult(int ExitCode, string Output, bool TimedOut);

/// <summary>Runs a Python script with access to one folder only and no network.</summary>
public interface ISandbox
{
    /// <param name="script">The .py file; it is copied into the folder's sandbox area, so the original is never touched.</param>
    /// <param name="folder">The only folder the script may read and write. It is passed as sys.argv[1].</param>
    Task<SandboxResult> RunAsync(string script, string folder, TimeSpan timeout, CancellationToken ct);
}

/// <summary>
/// The code sandbox (plan 5.10): an AppContainer process with no capabilities (so no network at all, loopback
/// included), allowed to read the bundled Python and to change one folder, inside a Job Object with a memory
/// limit, a CPU limit and no child processes.
/// </summary>
public sealed partial class AppContainerSandbox(string pythonDir, string scratchDir) : ISandbox
{
    private const string ContainerName = "Starlight.CodeSandbox";
    private const int MaxOutput = 64 * 1024;

    public static bool IsAvailable(string pythonDir) => OperatingSystem.IsWindows() && File.Exists(Path.Combine(pythonDir, "python.exe"));

    public async Task<SandboxResult> RunAsync(string script, string folder, TimeSpan timeout, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new ToolException("The code sandbox only runs on Windows.");
        }
        var python = Path.Combine(pythonDir, "python.exe");
        if (!File.Exists(python))
        {
            throw new ToolException("The bundled Python is missing, so scripts can't run. Reinstall Starlight.");
        }
        var sid = ContainerSid();
        var temp = Directory.CreateDirectory(Path.Combine(scratchDir, "tmp-" + Guid.NewGuid().ToString("N")[..8])).FullName;
        var scriptCopy = Path.Combine(temp, "script.py");
        File.Copy(script, scriptCopy);
        Grant(pythonDir, sid, FileSystemRights.ReadAndExecute);
        Grant(temp, sid, FileSystemRights.Modify);
        var folderAce = Grant(folder, sid, FileSystemRights.Modify);
        try
        {
            return await Task.Run(() => OperatingSystem.IsWindows() ? Run(sid, python, scriptCopy, folder, temp, timeout, ct) : throw new PlatformNotSupportedException(), ct);
        }
        finally
        {
            Revoke(folder, folderAce);
            try
            {
                Directory.Delete(temp, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static SecurityIdentifier ContainerSid()
    {
        var hr = CreateAppContainerProfile(ContainerName, "Starlight code sandbox", "Runs scripts Starlight writes, without network.", 0, 0, out var sidPtr);
        if (hr == unchecked((int)0x800700B7))
        {
            hr = DeriveAppContainerSidFromAppContainerName(ContainerName, out sidPtr);
        }
        if (hr != 0)
        {
            throw new ToolException($"The sandbox could not be created (0x{hr:X8}).");
        }
        try
        {
            return new SecurityIdentifier(sidPtr);
        }
        finally
        {
            FreeSid(sidPtr);
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static FileSystemAccessRule? Grant(string dir, SecurityIdentifier sid, FileSystemRights rights)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }
        var info = new DirectoryInfo(dir);
        var acl = info.GetAccessControl();
        var rule = new FileSystemAccessRule(sid, rights, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow);
        var present = acl.GetAccessRules(true, false, typeof(SecurityIdentifier)).OfType<FileSystemAccessRule>()
            .Any(r => r.IdentityReference == sid && (r.FileSystemRights & rights) == rights);
        if (present)
        {
            return null;
        }
        acl.AddAccessRule(rule);
        info.SetAccessControl(acl);
        return rule;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void Revoke(string dir, FileSystemAccessRule? rule)
    {
        if (rule is null || !OperatingSystem.IsWindows())
        {
            return;
        }
        try
        {
            var info = new DirectoryInfo(dir);
            var acl = info.GetAccessControl();
            acl.RemoveAccessRuleSpecific(rule);
            info.SetAccessControl(acl);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning(ex, "Could not remove the sandbox's access to {Dir}", dir);
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static SandboxResult Run(SecurityIdentifier sid, string python, string script, string folder, string temp, TimeSpan timeout, CancellationToken ct)
    {
        using var job = new JobObject(memoryLimitBytes: 1L << 30, noChildProcesses: true, cpuSeconds: (int)Math.Max(10, timeout.TotalSeconds));
        var sa = new SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(), bInheritHandle = 1 };
        if (!CreatePipe(out var readPipe, out var writePipe, ref sa, 0))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        using var reader = readPipe;
        SetHandleInformation(readPipe, 1, 0); // our end is not inherited
        var sidBytes = new byte[sid.BinaryLength];
        sid.GetBinaryForm(sidBytes, 0);
        var sidHandle = GCHandle.Alloc(sidBytes, GCHandleType.Pinned);
        var attrList = nint.Zero;
        var capsPtr = nint.Zero;
        var handleListPtr = nint.Zero;
        PROCESS_INFORMATION pi = default;
        try
        {
            nint size = 0;
            InitializeProcThreadAttributeList(0, 2, 0, ref size);
            attrList = Marshal.AllocHGlobal(size);
            if (!InitializeProcThreadAttributeList(attrList, 2, 0, ref size))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            var caps = new SECURITY_CAPABILITIES { AppContainerSid = sidHandle.AddrOfPinnedObject(), Capabilities = 0, CapabilityCount = 0, Reserved = 0 };
            capsPtr = Marshal.AllocHGlobal(Marshal.SizeOf<SECURITY_CAPABILITIES>());
            Marshal.StructureToPtr(caps, capsPtr, false);
            if (!UpdateProcThreadAttribute(attrList, 0, PROC_THREAD_ATTRIBUTE_SECURITY_CAPABILITIES, capsPtr, Marshal.SizeOf<SECURITY_CAPABILITIES>(), 0, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            handleListPtr = Marshal.AllocHGlobal(nint.Size);
            Marshal.WriteIntPtr(handleListPtr, writePipe.DangerousGetHandle());
            if (!UpdateProcThreadAttribute(attrList, 0, PROC_THREAD_ATTRIBUTE_HANDLE_LIST, handleListPtr, nint.Size, 0, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            var si = new STARTUPINFOEX
            {
                StartupInfo = new STARTUPINFO
                {
                    cb = Marshal.SizeOf<STARTUPINFOEX>(),
                    dwFlags = STARTF_USESTDHANDLES,
                    hStdInput = 0,
                    hStdOutput = writePipe.DangerousGetHandle(),
                    hStdError = writePipe.DangerousGetHandle(),
                },
                lpAttributeList = attrList,
            };
            var env = Environment("SYSTEMROOT=" + System.Environment.GetEnvironmentVariable("SYSTEMROOT"),
                "TEMP=" + temp, "TMP=" + temp, "PYTHONIOENCODING=utf-8", "PYTHONDONTWRITEBYTECODE=1", "PYTHONUTF8=1", "PYTHONNOUSERSITE=1");
            var cmd = (JobObject.CommandLine(python, ["-I", "-B", script, folder]) + "\0").ToCharArray(); // writable, as CreateProcessW wants
            if (!CreateProcessW(null, cmd, 0, 0, true, EXTENDED_STARTUPINFO_PRESENT | CREATE_SUSPENDED | CREATE_NO_WINDOW | CREATE_UNICODE_ENVIRONMENT,
                env, folder, ref si, out pi))
            {
                throw new ToolException($"The sandbox could not start Python (error {Marshal.GetLastWin32Error()}).");
            }
            try
            {
                job.AddHandle(pi.hProcess);
            }
            catch (Win32Exception)
            {
                TerminateProcess(pi.hProcess, 1); // never let it run outside the limits
                throw new ToolException("The sandbox could not put Python under its limits, so it did not run.");
            }
            if (ResumeThread(pi.hThread) == uint.MaxValue)
            {
                TerminateProcess(pi.hProcess, 1);
                throw new ToolException("The sandbox could not start Python.");
            }
            writePipe.Dispose(); // only the child holds the write end now, so reading ends when it exits

            var output = new MemoryStream();
            var readTask = Task.Run(() =>
            {
                using var stream = new FileStream(reader, FileAccess.Read, 4096, false);
                var buf = new byte[4096];
                int n;
                while ((n = stream.Read(buf, 0, buf.Length)) > 0)
                {
                    if (output.Length < MaxOutput)
                    {
                        output.Write(buf, 0, (int)Math.Min(n, MaxOutput - output.Length));
                    }
                }
            }, CancellationToken.None); // reads until the child ends; the loop below ends the child on cancel
            var deadline = DateTime.UtcNow + timeout;
            var timedOut = false;
            while (WaitForSingleObject(pi.hProcess, 200) != 0)
            {
                if (ct.IsCancellationRequested || DateTime.UtcNow > deadline)
                {
                    timedOut = !ct.IsCancellationRequested;
                    job.Terminate();
                    break;
                }
            }
            readTask.Wait(TimeSpan.FromSeconds(3), CancellationToken.None);
            GetExitCodeProcess(pi.hProcess, out var exit);
            ct.ThrowIfCancellationRequested();
            var text = Encoding.UTF8.GetString(output.ToArray());
            if (output.Length >= MaxOutput)
            {
                text += "\n[output cut at 64 KB]";
            }
            return new SandboxResult((int)exit, text, timedOut);
        }
        finally
        {
            if (!writePipe.IsClosed)
            {
                writePipe.Dispose();
            }
            if (pi.hThread != 0)
            {
                CloseHandle(pi.hThread);
            }
            if (pi.hProcess != 0)
            {
                CloseHandle(pi.hProcess);
            }
            if (attrList != 0)
            {
                DeleteProcThreadAttributeList(attrList);
                Marshal.FreeHGlobal(attrList);
            }
            if (capsPtr != 0)
            {
                Marshal.FreeHGlobal(capsPtr);
            }
            if (handleListPtr != 0)
            {
                Marshal.FreeHGlobal(handleListPtr);
            }
            sidHandle.Free();
        }
    }

    private static string Environment(params string[] vars) => string.Join('\0', vars) + "\0\0";

    private const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    private const uint CREATE_SUSPENDED = 0x00000004;
    private const uint CREATE_NO_WINDOW = 0x08000000;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    private const int STARTF_USESTDHANDLES = 0x00000100;
    private static readonly nint PROC_THREAD_ATTRIBUTE_HANDLE_LIST = 0x00020002;
    private static readonly nint PROC_THREAD_ATTRIBUTE_SECURITY_CAPABILITIES = 0x00020009;

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES
    {
        public int nLength;
        public nint lpSecurityDescriptor;
        public int bInheritHandle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_CAPABILITIES
    {
        public nint AppContainerSid;
        public nint Capabilities;
        public uint CapabilityCount;
        public uint Reserved;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public nint lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFOEX
    {
        public STARTUPINFO StartupInfo;
        public nint lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public nint hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [DllImport("userenv.dll", CharSet = CharSet.Unicode)]
    private static extern int CreateAppContainerProfile(string name, string display, string description, nint caps, int count, out nint sid);

    [DllImport("userenv.dll", CharSet = CharSet.Unicode)]
    private static extern int DeriveAppContainerSidFromAppContainerName(string name, out nint sid);

    [DllImport("advapi32.dll")]
    private static extern nint FreeSid(nint sid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, ref SECURITY_ATTRIBUTES sa, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetHandleInformation(SafeHandle handle, int mask, int flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(nint list, int count, int flags, ref nint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(nint list, uint flags, nint attribute, nint value, nint size, nint previous, nint returnSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(nint list);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessW(string? app, char[] cmd, nint pa, nint ta, bool inherit, uint flags, string env, string dir, ref STARTUPINFOEX si, out PROCESS_INFORMATION pi);

    [DllImport("kernel32.dll")]
    private static extern uint ResumeThread(nint thread);

    [DllImport("kernel32.dll")]
    private static extern bool TerminateProcess(nint process, uint exitCode);

    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(nint handle, uint ms);

    [DllImport("kernel32.dll")]
    private static extern bool GetExitCodeProcess(nint process, out uint code);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(nint handle);
}
