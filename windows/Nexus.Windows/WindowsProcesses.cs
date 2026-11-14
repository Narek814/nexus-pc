using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Nexus.Windows;

/// <summary>Identifies the process at the other end of a named pipe, so each side can check who it talks to.</summary>
[SupportedOSPlatform("windows")]
public static partial class WindowsProcesses
{
    private const uint ProcessQueryLimitedInformation = 0x1000;

    public static int? PipeClientProcessId(SafePipeHandle pipe) =>
        GetNamedPipeClientProcessId(pipe, out var pid) ? (int)pid : null;

    public static int? PipeServerProcessId(SafePipeHandle pipe) =>
        GetNamedPipeServerProcessId(pipe, out var pid) ? (int)pid : null;

    /// <summary>Full path of a process's executable, or null if it cannot be read.</summary>
    public static string? ImagePath(int processId)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, (uint)processId);
        if (handle == IntPtr.Zero)
        {
            return null;
        }
        try
        {
            var buffer = new char[32768];
            var length = (uint)buffer.Length;
            return QueryFullProcessImageNameW(handle, 0, buffer, ref length) ? new string(buffer, 0, (int)length) : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    public static int? SessionId(int processId) =>
        ProcessIdToSessionId((uint)processId, out var session) ? (int)session : null;

    /// <summary>The session attached to the physical console (the person at the PC); null if none.</summary>
    public static int? ActiveConsoleSessionId()
    {
        var id = WTSGetActiveConsoleSessionId();
        return id == 0xFFFFFFFF ? null : (int)id;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryFullProcessImageNameW(IntPtr process, uint flags, [Out] char[] exeName, ref uint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ProcessIdToSessionId(uint processId, out uint sessionId);

    [LibraryImport("kernel32.dll")]
    private static partial uint WTSGetActiveConsoleSessionId();

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);
}
