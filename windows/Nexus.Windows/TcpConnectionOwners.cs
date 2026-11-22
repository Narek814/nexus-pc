using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using Nexus.Core.Security;

namespace Nexus.Windows;

/// <summary>
/// T2: who holds the server end of one TCP connection (GetExtendedTcpTable, TCP_TABLE_OWNER_PID_ALL), and the
/// process behind that PID opened by handle (OpenProcess with PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE):
/// image path, start time, token integrity level, exit. IPv4 only (OpenRGB listens on 127.0.0.1).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class TcpConnectionOwners : IConnectionOwnerSource
{
    private const int AfInet = 2;
    private const int TcpTableOwnerPidAll = 5;
    private const uint ErrorInsufficientBuffer = 122;
    private const int RowSize = 24; // MIB_TCPROW_OWNER_PID: state, localAddr, localPort, remoteAddr, remotePort, owningPid
    private const uint StateEstablished = 5;

    public int? ServerProcessId(IPEndPoint clientLocal, IPEndPoint clientRemote)
    {
        ArgumentNullException.ThrowIfNull(clientLocal);
        ArgumentNullException.ThrowIfNull(clientRemote);
        var local = V4(clientLocal);
        var remote = V4(clientRemote);
        if (local is null || remote is null)
        {
            return null;
        }
        int? found = null;
        foreach (var row in Rows())
        {
            // The counterpart row: its local end is the server we connected to, its remote end is us.
            if (row.State == StateEstablished && row.Local.Equals(remote) && row.Remote.Equals(local))
            {
                if (found is not null && found != row.Pid)
                {
                    return null; // ambiguous: never guess
                }
                found = row.Pid;
            }
        }
        return found;
    }

    public IPeerProcess? Open(int processId) => WindowsPeerProcess.Open(processId);

    /// <summary>Every IPv4 TCP row with its owning PID (also used by the Windows tests to print what they see).</summary>
    public static List<(uint State, IPEndPoint Local, IPEndPoint Remote, int Pid)> Rows()
    {
        var size = 0u;
        var buffer = IntPtr.Zero;
        try
        {
            for (var attempt = 0; attempt < 8; attempt++)
            {
                var result = GetExtendedTcpTable(buffer, ref size, false, AfInet, TcpTableOwnerPidAll, 0);
                if (result == 0)
                {
                    break;
                }
                if (result != ErrorInsufficientBuffer)
                {
                    throw new InvalidOperationException($"GetExtendedTcpTable failed: {result}");
                }
                Marshal.FreeHGlobal(buffer);
                buffer = IntPtr.Zero;
                size += 16 * RowSize; // the table may grow between the two calls
                buffer = Marshal.AllocHGlobal((int)size);
            }
            if (buffer == IntPtr.Zero)
            {
                return [];
            }
            var count = (uint)Marshal.ReadInt32(buffer);
            var rows = new List<(uint, IPEndPoint, IPEndPoint, int)>((int)Math.Min(count, 65536));
            for (var i = 0L; i < count && 4 + (i + 1) * RowSize <= size; i++)
            {
                var row = new byte[RowSize];
                Marshal.Copy(buffer + (int)(4 + i * RowSize), row, 0, RowSize);
                var state = BinaryPrimitives.ReadUInt32LittleEndian(row);
                rows.Add((state, Endpoint(row, 4), Endpoint(row, 12), (int)BinaryPrimitives.ReadUInt32LittleEndian(row.AsSpan(20))));
            }
            return rows;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>Integrity level of the current process's token (for the Windows tests).</summary>
    public static ProcessIntegrity CurrentProcessIntegrity()
    {
        using var self = WindowsPeerProcess.Open(Environment.ProcessId);
        return self?.Integrity ?? ProcessIntegrity.Unknown;
    }

    private static IPEndPoint? V4(IPEndPoint endpoint)
    {
        var address = endpoint.Address.IsIPv4MappedToIPv6 ? endpoint.Address.MapToIPv4() : endpoint.Address;
        return address.AddressFamily == AddressFamily.InterNetwork ? new IPEndPoint(address, endpoint.Port) : null;
    }

    // Address: 4 bytes in network order as stored; port: network order in the low 16 bits of a DWORD.
    private static IPEndPoint Endpoint(byte[] row, int offset) =>
        new(new IPAddress(row.AsSpan(offset, 4)), (row[offset + 4] << 8) | row[offset + 5]);

    [LibraryImport("iphlpapi.dll")]
    private static partial uint GetExtendedTcpTable(IntPtr table, ref uint size, [MarshalAs(UnmanagedType.Bool)] bool order, int addressFamily, int tableClass, uint reserved);
}

/// <summary>A process held open by handle: the PID cannot be reused while this object lives.</summary>
[SupportedOSPlatform("windows")]
internal sealed partial class WindowsPeerProcess : IPeerProcess
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint Synchronize = 0x00100000;
    private const uint TokenQuery = 0x0008;
    private const int TokenIntegrityLevel = 25;

    private readonly SafeProcessHandle _handle;
    private readonly ProcessWaitHandle _wait;
    private readonly RegisteredWaitHandle _registration;
    private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private WindowsPeerProcess(SafeProcessHandle handle, int processId, DateTime start, string? image, ProcessIntegrity integrity)
    {
        _handle = handle;
        ProcessId = processId;
        StartTimeUtc = start;
        ImagePath = image;
        Integrity = integrity;
        _wait = new ProcessWaitHandle(handle);
        _registration = ThreadPool.RegisterWaitForSingleObject(_wait, (_, _) => _exited.TrySetResult(), null, Timeout.Infinite, executeOnlyOnce: true);
    }

    public int ProcessId { get; }

    public DateTime StartTimeUtc { get; }

    public string? ImagePath { get; }

    public ProcessIntegrity Integrity { get; }

    public Task Exited => _exited.Task;

    public static WindowsPeerProcess? Open(int processId)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation | Synchronize, false, (uint)processId);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            return null;
        }
        if (!GetProcessTimes(handle, out var created, out _, out _, out _))
        {
            handle.Dispose();
            return null;
        }
        return new WindowsPeerProcess(handle, processId, DateTime.FromFileTimeUtc(created), ImageOf(handle), IntegrityOf(handle));
    }

    public void Dispose()
    {
        _registration.Unregister(null);
        _wait.Dispose();
        _handle.Dispose();
    }

    private static string? ImageOf(SafeProcessHandle handle)
    {
        var buffer = new char[32768];
        var length = (uint)buffer.Length;
        return QueryFullProcessImageNameW(handle, 0, buffer, ref length) ? new string(buffer, 0, (int)length) : null;
    }

    private static ProcessIntegrity IntegrityOf(SafeProcessHandle handle)
    {
        if (!OpenProcessToken(handle, TokenQuery, out var token))
        {
            return ProcessIntegrity.Unknown;
        }
        try
        {
            GetTokenInformation(token, TokenIntegrityLevel, IntPtr.Zero, 0, out var needed);
            if (needed == 0 || needed > 1024)
            {
                return ProcessIntegrity.Unknown;
            }
            var buffer = Marshal.AllocHGlobal((int)needed);
            try
            {
                if (!GetTokenInformation(token, TokenIntegrityLevel, buffer, needed, out _))
                {
                    return ProcessIntegrity.Unknown;
                }
                var sid = Marshal.ReadIntPtr(buffer); // TOKEN_MANDATORY_LABEL.Label.Sid
                var subCount = Marshal.ReadByte(GetSidSubAuthorityCount(sid));
                if (subCount == 0)
                {
                    return ProcessIntegrity.Unknown;
                }
                return FromRid((uint)Marshal.ReadInt32(GetSidSubAuthority(sid, (uint)(subCount - 1))));
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            CloseHandle(token);
        }
    }

    internal static ProcessIntegrity FromRid(uint rid) => rid switch
    {
        >= 0x4000 => ProcessIntegrity.System,
        >= 0x3000 => ProcessIntegrity.High,
        >= 0x2000 => ProcessIntegrity.Medium,
        >= 0x1000 => ProcessIntegrity.Low,
        _ => ProcessIntegrity.Untrusted,
    };

    private sealed class ProcessWaitHandle : WaitHandle
    {
        public ProcessWaitHandle(SafeProcessHandle process) => SafeWaitHandle = new SafeWaitHandle(process.DangerousGetHandle(), ownsHandle: false);
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial SafeProcessHandle OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessTimes(SafeProcessHandle process, out long creation, out long exit, out long kernel, out long user);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryFullProcessImageNameW(SafeProcessHandle process, uint flags, [Out] char[] buffer, ref uint size);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(SafeProcessHandle process, uint desiredAccess, out IntPtr token);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetTokenInformation(IntPtr token, int informationClass, IntPtr information, uint length, out uint returnLength);

    [LibraryImport("advapi32.dll")]
    private static partial IntPtr GetSidSubAuthorityCount(IntPtr sid);

    [LibraryImport("advapi32.dll")]
    private static partial IntPtr GetSidSubAuthority(IntPtr sid, uint subAuthority);
}
