using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Nexus.Agent.Server;
using Nexus.Core.Security;
using Nexus.RGB;
using Nexus.Windows;
using Xunit.Abstractions;

namespace Nexus.Tests;

/// <summary>A process as the fake TCP table reports it; all copies opened for one PID share one exit.</summary>
internal sealed class FakePeerProcess(int pid, DateTime start, string? image, ProcessIntegrity integrity, TaskCompletionSource exited) : IPeerProcess
{
    public int ProcessId => pid;
    public DateTime StartTimeUtc => start;
    public string? ImagePath => image;
    public ProcessIntegrity Integrity => integrity;
    public Task Exited => exited.Task;
    public bool Disposed { get; private set; }

    public void Dispose() => Disposed = true;
}

/// <summary>Fake TCP owner table: which PID holds the server end of a connection to a given port.</summary>
internal sealed class FakeOpenRgbOwners : IConnectionOwnerSource
{
    public static readonly DateTime ChildStart = new(2026, 11, 1, 8, 0, 0, DateTimeKind.Utc);
    public const int ChildPid = 4242;

    private readonly ConcurrentDictionary<int, (DateTime Start, string? Image, ProcessIntegrity Integrity, TaskCompletionSource Exited)> _processes = new();

    public ConcurrentDictionary<int, int> PortOwners { get; } = new();
    public int Lookups;

    public int? ServerProcessId(IPEndPoint clientLocal, IPEndPoint clientRemote)
    {
        Interlocked.Increment(ref Lookups);
        return PortOwners.TryGetValue(clientRemote.Port, out var pid) ? pid : null;
    }

    public IPeerProcess? Open(int processId) =>
        _processes.TryGetValue(processId, out var p) ? new FakePeerProcess(processId, p.Start, p.Image, p.Integrity, p.Exited) : null;

    public FakeOpenRgbOwners Process(int pid, DateTime start, string? image = null, ProcessIntegrity integrity = ProcessIntegrity.Medium)
    {
        _processes[pid] = (start, image, integrity, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        return this;
    }

    public void Exit(int pid) => _processes[pid].Exited.TrySetResult();

    /// <summary>An agent whose OpenRGB port is served by a fake server confirmed as the bundled child (case A).</summary>
    public static async Task<AgentTestHost> StartTrustedAsync(int port)
    {
        var owners = new FakeOpenRgbOwners().Process(ChildPid, ChildStart);
        owners.PortOwners[port] = ChildPid;
        var host = await AgentTestHost.StartAsync(openRgbPort: port, openRgbOwners: owners);
        host.Services.OpenRgb!.TrustAsChildForTests(new ChildIdentity(ChildPid, ChildStart));
        return host;
    }
}

/// <summary>A loopback server that only counts the bytes it receives and notices when the client closes.</summary>
internal sealed class RecordingServer : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);

    public RecordingServer()
    {
        _listener.Start();
        _ = AcceptAsync();
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public int BytesReceived;
    public TaskCompletionSource Closed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private async Task AcceptAsync()
    {
        try
        {
            while (true)
            {
                var client = await _listener.AcceptTcpClientAsync();
                _ = Task.Run(async () =>
                {
                    using (client)
                    {
                        var buffer = new byte[256];
                        try
                        {
                            int read;
                            while ((read = await client.GetStream().ReadAsync(buffer)) > 0)
                            {
                                Interlocked.Add(ref BytesReceived, read);
                            }
                        }
                        catch (IOException)
                        {
                        }
                        Closed.TrySetResult();
                    }
                });
            }
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
        }
    }

    public void Dispose() => _listener.Stop();
}

/// <summary>T2 decisions, with a fake TCP table and fake processes (run everywhere).</summary>
public sealed class OpenRgbIdentityTests : IDisposable
{
    private const string Bundled = @"C:\Program Files\NexusPC\OpenRGB\OpenRGB.exe";
    private const string Good = "aa11";
    private readonly string _dir = Directory.CreateTempSubdirectory("nexus-t2-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static OpenRgbIdentityPolicy Policy(string? hash = Good) => new(Bundled, Good, _ => hash);

    private static FakePeerProcess Peer(int pid, DateTime start, string? image = null, ProcessIntegrity integrity = ProcessIntegrity.Medium) =>
        new(pid, start, image, integrity, new TaskCompletionSource());

    [Fact]
    public void Missing_counterpart_row_is_a_conflict()
    {
        Assert.Equal(PeerKind.Conflict, Policy().Check(null, null, new ChildIdentity(1, FakeOpenRgbOwners.ChildStart)).Kind);
    }

    [Fact]
    public void Bundled_child_pid_with_another_start_time_is_a_conflict()
    {
        var child = new ChildIdentity(7, FakeOpenRgbOwners.ChildStart);
        var verdict = Policy().Check(7, Peer(7, FakeOpenRgbOwners.ChildStart.AddSeconds(3), Bundled, ProcessIntegrity.System), child);
        Assert.Equal(PeerKind.Conflict, verdict.Kind);
        Assert.Contains("reused", verdict.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Bundled_child_with_the_same_pid_and_start_time_is_ours()
    {
        var child = new ChildIdentity(7, FakeOpenRgbOwners.ChildStart);
        Assert.Equal(PeerKind.Bundled, Policy().Check(7, Peer(7, FakeOpenRgbOwners.ChildStart), child).Kind);
    }

    [Fact]
    public void Setup_window_at_medium_integrity_is_a_conflict()
    {
        Assert.Equal(PeerKind.Conflict, Policy().Check(9, Peer(9, DateTime.UtcNow, Bundled, ProcessIntegrity.Medium), null).Kind);
    }

    [Fact]
    public void Setup_window_with_a_changed_file_is_a_conflict()
    {
        Assert.Equal(PeerKind.Conflict, Policy(hash: "bb22").Check(9, Peer(9, DateTime.UtcNow, Bundled, ProcessIntegrity.High), null).Kind);
        Assert.Equal(PeerKind.Conflict, Policy(hash: null).Check(9, Peer(9, DateTime.UtcNow, Bundled, ProcessIntegrity.High), null).Kind);
    }

    [Theory]
    [InlineData(ProcessIntegrity.High)]
    [InlineData(ProcessIntegrity.System)]
    public void Setup_window_elevated_from_the_bundled_path_with_the_pinned_hash_is_external(ProcessIntegrity integrity)
    {
        var upperCase = Bundled.ToUpperInvariant();
        Assert.Equal(PeerKind.SetupWindow, Policy().Check(9, Peer(9, DateTime.UtcNow, upperCase, integrity), null).Kind);
    }

    [Fact]
    public void Same_name_elsewhere_or_a_process_that_cannot_be_opened_is_a_conflict()
    {
        Assert.Equal(PeerKind.Conflict, Policy().Check(9, Peer(9, DateTime.UtcNow, @"C:\Users\me\OpenRGB\OpenRGB.exe", ProcessIntegrity.High), null).Kind);
        Assert.Equal(PeerKind.Conflict, Policy().Check(9, null, null).Kind);
        Assert.Equal(PeerKind.Conflict, Policy().Check(9, Peer(10, DateTime.UtcNow, Bundled, ProcessIntegrity.High), null).Kind);
    }

    [Fact]
    public async Task Foreign_pid_on_the_counterpart_row_closes_the_connection_without_sending_and_reports_conflict()
    {
        using var server = new RecordingServer();
        var owners = new FakeOpenRgbOwners().Process(999, DateTime.UtcNow, @"C:\Other\rgb.exe");
        owners.PortOwners[server.Port] = 999;
        var guard = new OpenRgbConnectionGuard(owners, Policy(), () => new ChildIdentity(FakeOpenRgbOwners.ChildPid, FakeOpenRgbOwners.ChildStart));
        var rgb = new RgbService(_dir, server.Port, guard);

        var status = await rgb.StatusAsync(CancellationToken.None);

        Assert.False(status["available"]!.GetValue<bool>());
        Assert.Equal("conflict", status["state"]!.GetValue<string>());
        Assert.Equal(RgbService.ConflictMessage, status["reason"]!.GetValue<string>());
        await server.Closed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, server.BytesReceived);
        var apply = await Assert.ThrowsAsync<RgbService.RgbConflictException>(() => rgb.ApplyAsync(new Nexus.Core.Protocol.RgbLook("#FFFFFF", 100, "static"), null, CancellationToken.None));
        Assert.Equal(RgbService.ConflictMessage, apply.Message);
        Assert.Equal(0, server.BytesReceived);
    }

    [Fact]
    public async Task Missing_counterpart_row_closes_without_sending()
    {
        using var server = new RecordingServer();
        var guard = new OpenRgbConnectionGuard(new FakeOpenRgbOwners(), Policy(), () => null);
        await Assert.ThrowsAsync<OpenRgbConflictException>(() => OpenRgbClient.ConnectAsync("127.0.0.1", server.Port, guard.Accept, CancellationToken.None));
        await server.Closed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, server.BytesReceived);
    }

    [Fact]
    public async Task Not_a_byte_is_sent_before_the_check_passes()
    {
        await using var server = FakeOpenRgbServer.OwnersPc();
        var checkedBeforeAnyByte = false;
        await using var client = await OpenRgbClient.ConnectAsync("127.0.0.1", server.Port, tcp =>
        {
            Thread.Sleep(200); // anything already sent would have reached the server by now
            checkedBeforeAnyByte = server.Received.IsEmpty;
            return new FakePeerProcess(1, DateTime.UtcNow, null, ProcessIntegrity.Medium, new TaskCompletionSource());
        }, CancellationToken.None);
        Assert.True(checkedBeforeAnyByte);
        var devices = await client.ListDevicesAsync(CancellationToken.None);
        Assert.NotEmpty(devices);
        Assert.Equal(OpenRgbClient.SetClientName, server.Received.First().Packet); // the first byte only after the check
    }

    [Fact]
    public async Task Exit_of_the_accepting_process_closes_the_connection_and_the_next_one_is_checked_again()
    {
        using var server = new RecordingServer();
        var owners = new FakeOpenRgbOwners().Process(FakeOpenRgbOwners.ChildPid, FakeOpenRgbOwners.ChildStart);
        owners.PortOwners[server.Port] = FakeOpenRgbOwners.ChildPid;
        ChildIdentity? child = new(FakeOpenRgbOwners.ChildPid, FakeOpenRgbOwners.ChildStart);
        var guard = new OpenRgbConnectionGuard(owners, Policy(), () => child);

        var tcp = new TcpClient(AddressFamily.InterNetwork);
        await tcp.ConnectAsync(IPAddress.Loopback, server.Port);
        Assert.Equal(PeerKind.Bundled, guard.Check(tcp, out var lease).Kind);
        Assert.NotNull(lease);
        Assert.Equal(1, owners.Lookups);

        owners.Exit(FakeOpenRgbOwners.ChildPid);
        child = null; // the host forgets an exited child
        await server.Closed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        lease!.Dispose();

        using var again = new TcpClient(AddressFamily.InterNetwork);
        await again.ConnectAsync(IPAddress.Loopback, server.Port);
        Assert.Equal(PeerKind.Conflict, guard.Check(again, out var none).Kind);
        Assert.Null(none);
        Assert.Equal(2, owners.Lookups);
    }

    private OpenRgbHost Host(string exe, string pinned, FakeOpenRgbOwners owners, int port, Action? started = null) =>
        new(exe, _dir, port, NullLogger<OpenRgbHost>.Instance, owners, pinned, _ => { started?.Invoke(); return null; }, anyOs: true);

    private string Executable()
    {
        var exe = Path.Combine(_dir, "OpenRGB.exe");
        File.WriteAllBytes(exe, [1, 2, 3]);
        return exe;
    }

    private static async Task WaitForState(OpenRgbHost host, string state)
    {
        for (var i = 0; i < 200 && host.State != state; i++)
        {
            await Task.Delay(50);
        }
        Assert.Equal(state, host.State);
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    [Fact]
    public async Task Changed_executable_is_not_started_and_reads_conflict()
    {
        var exe = Executable();
        var starts = 0;
        using var host = Host(exe, new string('0', 64), new FakeOpenRgbOwners(), FreePort(), () => starts++);
        await host.StartAsync(CancellationToken.None);
        await WaitForState(host, "conflict");
        await host.StopAsync(CancellationToken.None);
        Assert.Equal(0, starts);
    }

    [Fact]
    public async Task Matching_executable_is_locked_and_started()
    {
        var exe = Executable();
        var starts = 0;
        using var host = Host(exe, OpenRgbIdentityPolicy.Sha256OfFile(exe)!, new FakeOpenRgbOwners(), FreePort(), () => starts++);
        await host.StartAsync(CancellationToken.None);
        for (var i = 0; i < 100 && starts == 0; i++)
        {
            await Task.Delay(50);
        }
        await host.StopAsync(CancellationToken.None);
        Assert.True(starts > 0);
    }

    [Fact]
    public async Task Port_held_by_the_trusted_child_reads_running_and_no_second_copy_starts()
    {
        using var server = new RecordingServer();
        var exe = Executable();
        var owners = new FakeOpenRgbOwners().Process(FakeOpenRgbOwners.ChildPid, FakeOpenRgbOwners.ChildStart);
        owners.PortOwners[server.Port] = FakeOpenRgbOwners.ChildPid;
        var starts = 0;
        using var host = Host(exe, OpenRgbIdentityPolicy.Sha256OfFile(exe)!, owners, server.Port, () => starts++);
        host.TrustAsChildForTests(new ChildIdentity(FakeOpenRgbOwners.ChildPid, FakeOpenRgbOwners.ChildStart));
        await host.StartAsync(CancellationToken.None);
        await WaitForState(host, "running");
        await host.StopAsync(CancellationToken.None);
        Assert.Equal(0, starts);
        Assert.Equal(0, server.BytesReceived);
    }

    [Fact]
    public async Task Elevated_setup_window_reads_external_and_a_foreign_process_conflict()
    {
        using var server = new RecordingServer();
        var exe = Executable();
        var owners = new FakeOpenRgbOwners().Process(77, DateTime.UtcNow, exe, ProcessIntegrity.High).Process(78, DateTime.UtcNow, @"C:\Other\rgb.exe", ProcessIntegrity.High);
        owners.PortOwners[server.Port] = 77;
        var starts = 0;
        using var host = Host(exe, OpenRgbIdentityPolicy.Sha256OfFile(exe)!, owners, server.Port, () => starts++);
        await host.StartAsync(CancellationToken.None);
        await WaitForState(host, "external");
        owners.PortOwners[server.Port] = 78;
        await WaitForState(host, "conflict");
        await host.StopAsync(CancellationToken.None);
        Assert.Equal(0, starts);
        Assert.Equal(0, server.BytesReceived);
    }

    [Fact]
    public async Task During_the_pause_a_foreign_process_is_a_conflict_and_only_the_setup_window_is_external()
    {
        using var server = new RecordingServer();
        var exe = Executable();
        var owners = new FakeOpenRgbOwners().Process(78, DateTime.UtcNow, @"C:\Other\rgb.exe", ProcessIntegrity.System).Process(77, DateTime.UtcNow, exe, ProcessIntegrity.High);
        owners.PortOwners[server.Port] = 78;
        using var host = Host(exe, OpenRgbIdentityPolicy.Sha256OfFile(exe)!, owners, server.Port);
        host.Pause(TimeSpan.FromMinutes(1));
        await host.StartAsync(CancellationToken.None);
        await WaitForState(host, "conflict");
        owners.PortOwners[server.Port] = 77;
        await WaitForState(host, "external");
        await host.StopAsync(CancellationToken.None);
        Assert.Equal(0, server.BytesReceived);
    }

    [Fact]
    public async Task Pause_while_in_conflict_explains_why()
    {
        var exe = Executable();
        await using var agent = await AgentTestHost.StartAsync();
        try
        {
            var conflicted = Host(exe, new string('0', 64), new FakeOpenRgbOwners(), FreePort());
            await conflicted.StartAsync(CancellationToken.None);
            await WaitForState(conflicted, "conflict");
            agent.Services.OpenRgb = conflicted;
            var reply = await agent.Admin.HandleAsync(new JsonObject { ["op"] = "openrgb.pause" }, new Nexus.Agent.Admin.AdminConnectionState(), CancellationToken.None);
            Assert.False(reply["ok"]!.GetValue<bool>());
            Assert.Contains("перехватила другая программа", reply["error"]!.GetValue<string>(), StringComparison.Ordinal);
            var status = await agent.Admin.HandleAsync(new JsonObject { ["op"] = "status" }, new Nexus.Agent.Admin.AdminConnectionState(), CancellationToken.None);
            Assert.Equal("conflict", status["openRgb"]!.GetValue<string>());
            await conflicted.StopAsync(CancellationToken.None);
            conflicted.Dispose();
        }
        finally
        {
            AgentTestHost.DeleteDirectory(agent.DataDirectory);
        }
    }

    [Fact]
    public void Pinned_hash_is_the_one_of_openrgb_1_1()
    {
        Assert.Equal("e26ae71162795a8abcfb90c05c1572a00ba06ca63b886aca57392a0138963b1e", OpenRgbHost.PinnedSha256);
    }
}

/// <summary>
/// T2 on real Windows (CI windows-latest; they return early elsewhere): the counterpart row of a loopback
/// connection between two processes, the integrity level, the held executable, exit tracking. Observed PIDs are
/// printed so the CI log shows what Windows reports.
/// </summary>
public sealed class OpenRgbWindowsTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("nexus-t2w-").FullName;

    public void Dispose()
    {
        File.WriteAllText(Path.Combine(_dir, "release"), "");
        for (var i = 0; i < 20; i++)
        {
            try
            {
                Directory.Delete(_dir, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(250); // the child may still be exiting; %TEMP% is cleaned by the runner
            }
        }
    }

    [Fact]
    public async Task Counterpart_row_names_the_other_process_that_listens_and_accepts()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        using var child = StartHelper("--tcp-listen");
        var port = int.Parse(await WaitForFile("port"), System.Globalization.CultureInfo.InvariantCulture);
        using var tcp = new TcpClient(AddressFamily.InterNetwork);
        await tcp.ConnectAsync(IPAddress.Loopback, port);
        var owners = new TcpConnectionOwners();
        var local = (IPEndPoint)tcp.Client.LocalEndPoint!;
        var remote = (IPEndPoint)tcp.Client.RemoteEndPoint!;
        var beforeAccept = owners.ServerProcessId(local, remote);
        File.WriteAllText(Path.Combine(_dir, "go"), "");
        await WaitForFile("accepted");
        var afterAccept = owners.ServerProcessId(local, remote);
        Print("same process listens and accepts", child.Id, local, remote, beforeAccept, afterAccept);
        Assert.Equal(child.Id, afterAccept);
        Assert.Equal(child.Id, beforeAccept);
        Assert.NotEqual(Environment.ProcessId, afterAccept);
    }

    [Fact]
    public async Task Listener_created_by_one_process_and_accepted_by_another_is_reported()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        using var child = StartHelper("--tcp-accept-dup");
        int port;
        using (var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
        {
            listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            listener.Listen(4);
            port = ((IPEndPoint)listener.LocalEndPoint!).Port;
            var info = listener.DuplicateAndClose(child.Id); // WSADuplicateSocket; this process's handle is closed
            File.WriteAllBytes(Path.Combine(_dir, "socket.bin.tmp"), info.ProtocolInformation);
            File.Move(Path.Combine(_dir, "socket.bin.tmp"), Path.Combine(_dir, "socket.bin"));
        }
        await WaitForFile("ready");
        using var tcp = new TcpClient(AddressFamily.InterNetwork);
        await tcp.ConnectAsync(IPAddress.Loopback, port);
        var owners = new TcpConnectionOwners();
        var local = (IPEndPoint)tcp.Client.LocalEndPoint!;
        var remote = (IPEndPoint)tcp.Client.RemoteEndPoint!;
        var beforeAccept = owners.ServerProcessId(local, remote);
        File.WriteAllText(Path.Combine(_dir, "go"), "");
        await WaitForFile("accepted");
        var afterAccept = owners.ServerProcessId(local, remote);
        output.WriteLine($"listener created by test pid {Environment.ProcessId}, accept() by child pid {child.Id}");
        Print("listener in one process, accept() in another", child.Id, local, remote, beforeAccept, afterAccept);
        output.WriteLine($"RESULT owningPid after accept = {(afterAccept == child.Id ? "acceptor (child)" : afterAccept == Environment.ProcessId ? "creator (test)" : "other/none")}");
        // Recorded, not asserted on which one: the finding goes to the owner (T2, section 2 item 6).
        Assert.NotNull(afterAccept);
        Assert.True(afterAccept == child.Id || afterAccept == Environment.ProcessId, $"unexpected owner {afterAccept}");
    }

    [Fact]
    public void Integrity_level_of_this_process_is_read()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        var level = TcpConnectionOwners.CurrentProcessIntegrity();
        output.WriteLine($"integrity {level}, elevated {Elevated()}, system {IsSystem()}");
        var expected = IsSystem() ? ProcessIntegrity.System : Elevated() ? ProcessIntegrity.High : ProcessIntegrity.Medium;
        Assert.Equal(expected, level);
    }

    [Fact]
    public async Task Open_process_reports_its_path_and_start_time_and_watches_its_exit()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        using var child = StartHelper("--tcp-listen");
        using var peer = new TcpConnectionOwners().Open(child.Id);
        Assert.NotNull(peer);
        Assert.Equal(child.Id, peer.ProcessId);
        Assert.True(OpenRgbIdentityPolicy.SamePath(child.MainModule!.FileName, peer.ImagePath!), peer.ImagePath);
        Assert.True((peer.StartTimeUtc - child.StartTime.ToUniversalTime()).Duration() < TimeSpan.FromSeconds(1));
        Assert.False(peer.Exited.IsCompleted);
        child.Kill();
        await peer.Exited.WaitAsync(TimeSpan.FromSeconds(10)); // throws TimeoutException if the exit is not seen through the held handle
    }

    [Fact]
    public async Task Guard_on_real_windows_trusts_the_child_and_closes_the_connection_when_it_exits()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        using var child = StartHelper("--tcp-listen");
        var port = int.Parse(await WaitForFile("port"), System.Globalization.CultureInfo.InvariantCulture);
        var owners = new TcpConnectionOwners();
        using var held = owners.Open(child.Id)!;
        var identity = new ChildIdentity(held.ProcessId, held.StartTimeUtc);
        var guard = new OpenRgbConnectionGuard(owners, new OpenRgbIdentityPolicy(@"C:\none\OpenRGB.exe", "00", _ => null), () => identity);
        File.WriteAllText(Path.Combine(_dir, "go"), "");
        var tcp = new TcpClient(AddressFamily.InterNetwork);
        await tcp.ConnectAsync(IPAddress.Loopback, port);
        Assert.Equal(PeerKind.Bundled, guard.Check(tcp, out var lease).Kind);
        using (lease)
        {
            child.Kill();
            var closed = false;
            for (var i = 0; i < 100 && !closed; i++)
            {
                try
                {
                    _ = tcp.GetStream(); // works until the lease disposes the client
                    await Task.Delay(100);
                }
                catch (ObjectDisposedException)
                {
                    closed = true;
                }
            }
            Assert.True(closed, "the connection stayed open after the accepting process exited");
        }
        using var stranger = new TcpClient(AddressFamily.InterNetwork);
        var foreign = new OpenRgbConnectionGuard(owners, new OpenRgbIdentityPolicy(@"C:\none\OpenRGB.exe", "00", _ => null), () => null);
        using var other = StartHelper("--tcp-listen", "second");
        var otherPort = int.Parse(await WaitForFile("port", "second"), System.Globalization.CultureInfo.InvariantCulture);
        await stranger.ConnectAsync(IPAddress.Loopback, otherPort);
        Assert.Equal(PeerKind.Conflict, foreign.Check(stranger, out var none).Kind);
        Assert.Null(none);
        File.WriteAllText(Path.Combine(_dir, "second", "release"), "");
    }

    [Fact]
    public void Held_executable_cannot_be_written_or_deleted_but_still_starts()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var exe = Path.Combine(_dir, "OpenRGB.exe");
        File.Copy(Path.Combine(system, "hostname.exe"), exe);
        using var host = new OpenRgbHost(exe, _dir, 1, NullLogger<OpenRgbHost>.Instance, null, OpenRgbIdentityPolicy.Sha256OfFile(exe)!, null, anyOs: false);
        using (var held = host.LockVerifiedExecutable(exe))
        {
            Assert.NotNull(held);
            Assert.ThrowsAny<IOException>(() => new FileStream(exe, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete).Dispose());
            Assert.ThrowsAny<Exception>(() => File.Delete(exe));
            Assert.True(File.Exists(exe));
            Assert.ThrowsAny<Exception>(() => File.Move(exe, exe + ".old"));
            using var run = Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
            Assert.True(run.WaitForExit(TimeSpan.FromSeconds(30)));
            Assert.Equal(0, run.ExitCode);
        }
        File.Delete(exe); // released: allowed again
        Assert.False(File.Exists(exe));
    }

    [SupportedOSPlatform("windows")]
    private Process StartHelper(string mode, string? sub = null)
    {
        var folder = sub is null ? _dir : Directory.CreateDirectory(Path.Combine(_dir, sub)).FullName;
        var dotnet = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", "dotnet.exe"));
        Assert.True(File.Exists(dotnet), "dotnet.exe not found at " + dotnet);
        var start = new ProcessStartInfo(dotnet) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { "exec", typeof(TestHelperProgram).Assembly.Location, mode, folder })
        {
            start.ArgumentList.Add(argument);
        }
        return Process.Start(start)!;
    }

    private async Task<string> WaitForFile(string name, string? sub = null)
    {
        var path = sub is null ? Path.Combine(_dir, name) : Path.Combine(_dir, sub, name);
        for (var i = 0; i < 600; i++)
        {
            if (File.Exists(path))
            {
                return await File.ReadAllTextAsync(path);
            }
            await Task.Delay(50);
        }
        throw new TimeoutException("helper did not write " + name);
    }

    [SupportedOSPlatform("windows")]
    private void Print(string scenario, int childPid, IPEndPoint local, IPEndPoint remote, int? before, int? after)
    {
        output.WriteLine($"--- {scenario}");
        output.WriteLine($"test pid {Environment.ProcessId}, child pid {childPid}, client {local} -> server {remote}");
        output.WriteLine($"counterpart owningPid before accept: {before?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none"}, after accept: {after?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none"}");
        foreach (var row in TcpConnectionOwners.Rows().Where(r => r.Local.Port == remote.Port || r.Remote.Port == remote.Port))
        {
            output.WriteLine($"row state {row.State} {row.Local} -> {row.Remote} pid {row.Pid}");
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool Elevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    [SupportedOSPlatform("windows")]
    private static bool IsSystem()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.IsSystem;
    }
}
