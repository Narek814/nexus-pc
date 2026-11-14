using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Nexus.Windows;

/// <summary>
/// Asks the Service Control Manager which process runs a service. Standard users may query this
/// (unlike opening a SYSTEM process), and only administrators can install or change a service.
/// </summary>
[SupportedOSPlatform("windows")]
public static partial class WindowsServiceInfo
{
    private const uint ScManagerConnect = 0x0001;
    private const uint ServiceQueryStatus = 0x0004;
    private const int ScStatusProcessInfo = 0;
    private const int ServiceRunning = 4;

    /// <summary>Process id of a running service, or null (not installed, not running, or no access).</summary>
    public static int? RunningProcessId(string serviceName)
    {
        var scm = OpenSCManagerW(null, null, ScManagerConnect);
        if (scm == IntPtr.Zero)
        {
            return null;
        }
        try
        {
            var service = OpenServiceW(scm, serviceName, ServiceQueryStatus);
            if (service == IntPtr.Zero)
            {
                return null;
            }
            try
            {
                // SERVICE_STATUS_PROCESS: 9 DWORDs; dwCurrentState at offset 4, dwProcessId at offset 28.
                var buffer = new byte[36];
                if (!QueryServiceStatusEx(service, ScStatusProcessInfo, buffer, (uint)buffer.Length, out _))
                {
                    return null;
                }
                var state = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(4));
                var pid = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(28));
                return state == ServiceRunning && pid > 0 ? pid : null;
            }
            finally
            {
                CloseServiceHandle(service);
            }
        }
        finally
        {
            CloseServiceHandle(scm);
        }
    }

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr OpenSCManagerW(string? machineName, string? databaseName, uint desiredAccess);

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr OpenServiceW(IntPtr scManager, string serviceName, uint desiredAccess);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryServiceStatusEx(IntPtr service, int infoLevel, [Out] byte[] buffer, uint bufferSize, out uint bytesNeeded);

    [LibraryImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseServiceHandle(IntPtr handle);
}
