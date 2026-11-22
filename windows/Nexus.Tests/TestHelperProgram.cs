using System.Globalization;
using Nexus.Core.Storage;

namespace Nexus.Tests;

/// <summary>
/// Entry point of the test assembly. Test runners never call it; it exists for one helper mode used by
/// DataDirectoryWindowsTests.Protected_lock_excludes_a_holder_in_another_session (run as SYSTEM in session 0
/// by a temporary scheduled task):
///   dotnet exec Nexus.Tests.dll --hold-datadir-lock &lt;folder&gt; &lt;seconds&gt;
/// It takes the protected lock, writes "acquired", waits for "release" (or the time limit), releases, writes
/// "released" and exits. Nothing else is touched.
/// A second helper mode, --dll-search-probe, is described in DllSearchProbe.
/// </summary>
public static class TestHelperProgram
{
    public static int Main(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        // NativeLibraryLoadingTests (T6): the process-wide DLL search order is set only in this child process.
        if (args.Length == 4 && args[0] == "--dll-search-probe" && OperatingSystem.IsWindows())
        {
            return DllSearchProbe.Run(args[1], args[2], args[3]);
        }
        // OpenRgbWindowsTests (T2): a loopback server in another process, see TcpOwnerProbe.
        if (args.Length == 2 && args[0] == "--tcp-listen" && OperatingSystem.IsWindows())
        {
            return TcpOwnerProbe.Listen(args[1]);
        }
        if (args.Length == 2 && args[0] == "--tcp-accept-dup" && OperatingSystem.IsWindows())
        {
            return TcpOwnerProbe.AcceptDuplicated(args[1]);
        }
        if (args.Length == 3 && args[0] == "--hold-datadir-lock" && OperatingSystem.IsWindows()
            && int.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) && seconds is > 0 and <= 300)
        {
            return HoldLock(args[1], TimeSpan.FromSeconds(seconds));
        }
        return 0;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static int HoldLock(string folder, TimeSpan limit)
    {
        var outcome = new Nexus.Windows.ProtectedDataDirectoryLock().TryAcquire(out var held, out var failure);
        if (outcome != LockOutcome.Acquired || held is null)
        {
            File.WriteAllText(Path.Combine(folder, "failed"), outcome + " " + failure);
            return 1;
        }
        using (held)
        {
            File.WriteAllText(Path.Combine(folder, "acquired"), Environment.UserName + " session " + System.Diagnostics.Process.GetCurrentProcess().SessionId.ToString(CultureInfo.InvariantCulture));
            var deadline = DateTime.UtcNow + limit;
            while (DateTime.UtcNow < deadline && !File.Exists(Path.Combine(folder, "release")))
            {
                Thread.Sleep(100);
            }
        }
        File.WriteAllText(Path.Combine(folder, "released"), "");
        return 0;
    }
}
