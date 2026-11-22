using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;

namespace Nexus.Tests;

/// <summary>
/// Child-process side of OpenRgbWindowsTests (T2, section 2 item 6): a real loopback server in another process,
/// so the test can see whose PID the TCP table shows for the server end of one connection. Files in the
/// folder given on the command line are the only way the two processes talk:
///   --tcp-listen &lt;folder&gt;     listens on 127.0.0.1:0, writes "port", waits for "go", accepts, writes "accepted"
///   --tcp-accept-dup &lt;folder&gt; waits for "socket.bin" (a listening socket duplicated into this process by the
///                               test with WSADuplicateSocket), writes "ready", waits for "go", accepts, writes "accepted"
/// Both then wait for "release" (or 2 minutes) and exit. Nothing is ever sent on the connection.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class TcpOwnerProbe
{
    private static readonly TimeSpan Limit = TimeSpan.FromMinutes(2);

    public static int Listen(string folder)
    {
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(4);
        Write(folder, "port", ((IPEndPoint)listener.LocalEndPoint!).Port.ToString(CultureInfo.InvariantCulture));
        return AcceptAndHold(listener, folder);
    }

    public static int AcceptDuplicated(string folder)
    {
        if (!WaitFor(folder, "socket.bin"))
        {
            return 2;
        }
        using var listener = new Socket(new SocketInformation
        {
            ProtocolInformation = File.ReadAllBytes(Path.Combine(folder, "socket.bin")),
            Options = SocketInformationOptions.Listening,
        });
        Write(folder, "ready", "");
        return AcceptAndHold(listener, folder);
    }

    private static int AcceptAndHold(Socket listener, string folder)
    {
        if (!WaitFor(folder, "go"))
        {
            return 3;
        }
        using var accepted = listener.Accept();
        Write(folder, "accepted", "");
        WaitFor(folder, "release");
        return 0;
    }

    private static bool WaitFor(string folder, string name)
    {
        var deadline = DateTime.UtcNow + Limit;
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(Path.Combine(folder, name)))
            {
                return true;
            }
            Thread.Sleep(50);
        }
        return false;
    }

    private static void Write(string folder, string name, string text)
    {
        var temp = Path.Combine(folder, name + ".tmp");
        File.WriteAllText(temp, text);
        File.Move(temp, Path.Combine(folder, name));
    }
}
