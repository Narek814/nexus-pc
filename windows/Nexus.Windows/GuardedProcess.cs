using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Nexus.Windows;

/// <summary>
/// A process created suspended (CreateProcessW, CREATE_SUSPENDED, no handle inheritance, absolute application name, no
/// search) and placed in its own job object with JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE before it runs a single instruction
/// (T5 L1, L2). The caller checks the image path while it is suspended, then <see cref="Resume"/>s it or
/// <see cref="Terminate"/>s it. Disposing closes the job: anything the process left running is killed.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class GuardedProcess : IDisposable
{
    private const uint CreateSuspended = 0x0000_0004;
    private const uint CreateNoWindow = 0x0800_0000;
    private const uint CreateUnicodeEnvironment = 0x0000_0400;
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;
    private const uint WaitObject0 = 0, WaitTimeout = 0x102;

    private readonly SafeFileHandle _job;
    private readonly SafeFileHandle _thread;
    private bool _resumed;

    private GuardedProcess(SafeFileHandle job, SafeProcessHandle process, SafeFileHandle thread, int id)
    {
        _job = job;
        Process = process;
        _thread = thread;
        Id = id;
    }

    public SafeProcessHandle Process { get; }

    public int Id { get; }

    public static GuardedProcess StartSuspended(string applicationPath, IReadOnlyList<string> arguments, string workingDirectory)
    {
        ArgumentNullException.ThrowIfNull(applicationPath);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(workingDirectory);
        if (!Path.IsPathFullyQualified(applicationPath))
        {
            throw new ArgumentException("absolute path required", nameof(applicationPath));
        }
        var job = CreateJobObjectW(IntPtr.Zero, null);
        if (job.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreateJobObject");
        }
        try
        {
            var limits = new ExtendedLimitInformation { Basic = new BasicLimitInformation { LimitFlags = JobObjectLimitKillOnJobClose } };
            if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref limits, (uint)Marshal.SizeOf<ExtendedLimitInformation>()))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "SetInformationJobObject");
            }
            var commandLine = new StringBuilder(Quote(applicationPath));
            foreach (var argument in arguments)
            {
                commandLine.Append(' ').Append(Quote(argument));
            }
            var line = commandLine.Append('\0').ToString().ToCharArray();
            var startup = new StartupInfo { Size = (uint)Marshal.SizeOf<StartupInfo>() };
            if (!CreateProcessW(applicationPath, line, IntPtr.Zero, IntPtr.Zero, false, CreateSuspended | CreateNoWindow | CreateUnicodeEnvironment,
                    IntPtr.Zero, workingDirectory, ref startup, out var info))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreateProcess " + applicationPath);
            }
            var process = new SafeProcessHandle(info.Process, ownsHandle: true);
            var thread = new SafeFileHandle(info.Thread, ownsHandle: true);
            if (!AssignProcessToJobObject(job, process))
            {
                var error = Marshal.GetLastPInvokeError();
                _ = TerminateProcess(process, 1);
                process.Dispose();
                thread.Dispose();
                throw new Win32Exception(error, "AssignProcessToJobObject");
            }
            return new GuardedProcess(job, process, thread, (int)info.ProcessId);
        }
        catch
        {
            job.Dispose();
            throw;
        }
    }

    public void Resume()
    {
        if (ResumeThread(_thread) == uint.MaxValue)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "ResumeThread");
        }
        _resumed = true;
    }

    public bool Resumed => _resumed;

    /// <summary>Kills the process (suspended or running) and waits for it to end.</summary>
    public void Terminate(uint exitCode = 1)
    {
        _ = TerminateProcess(Process, exitCode);
        _ = WaitForSingleObject(Process, 30_000);
    }

    /// <summary>Exit code, or null if it did not end within <paramref name="timeout"/>.</summary>
    public int? WaitForExit(TimeSpan timeout)
    {
        var result = WaitForSingleObject(Process, (uint)Math.Clamp(timeout.TotalMilliseconds, 0, uint.MaxValue - 1));
        if (result == WaitTimeout)
        {
            return null;
        }
        if (result != WaitObject0 || !GetExitCodeProcess(Process, out var code))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "WaitForSingleObject");
        }
        return unchecked((int)code);
    }

    /// <summary>Closing the job kills everything still in it (KILL_ON_JOB_CLOSE).</summary>
    public void Dispose()
    {
        _thread.Dispose();
        Process.Dispose();
        _job.Dispose();
    }

    /// <summary>One argument quoted the way CommandLineToArgvW / the C runtime read it back.</summary>
    internal static string Quote(string argument)
    {
        if (argument.Length > 0 && argument.IndexOfAny([' ', '\t', '\n', '\v', '"']) < 0)
        {
            return argument;
        }
        var result = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var c in argument)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }
            result.Append('\\', c == '"' ? (backslashes * 2) + 1 : backslashes);
            backslashes = 0;
            result.Append(c);
        }
        return result.Append('\\', backslashes * 2).Append('"').ToString();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public uint Size;
        public IntPtr Reserved, Desktop, Title;
        public uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public ushort ShowWindow, Reserved2;
        public IntPtr Reserved3, StdInput, StdOutput, StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process, Thread;
        public uint ProcessId, ThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimitInformation
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimitInformation
    {
        public BasicLimitInformation Basic;
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
        public nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateJobObjectW(IntPtr attributes, string? name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(SafeFileHandle job, int infoClass, ref ExtendedLimitInformation info, uint length);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(SafeFileHandle job, SafeProcessHandle process);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateProcessW(string applicationName, [In, Out] char[] commandLine, IntPtr processAttributes, IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint creationFlags, IntPtr environment, string currentDirectory,
        ref StartupInfo startupInfo, out ProcessInformation processInformation);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint ResumeThread(SafeFileHandle thread);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TerminateProcess(SafeProcessHandle process, uint exitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetExitCodeProcess(SafeProcessHandle process, out uint exitCode);
}
