using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Nexus.Core.Security;

namespace Nexus.Agent.Server;

/// <summary>
/// Runs the OpenRGB copy installed with Nexus PC (Program Files\NexusPC\OpenRGB) as its SDK server, without
/// a window, as a child of the service (so with the rights OpenRGB needs for motherboard and RAM lighting, and
/// no UAC prompt). The server listens on 127.0.0.1 only: the phone reaches lighting through the agent, never
/// OpenRGB directly.
/// T2: the agent talks only to a confirmed OpenRGB process — its own child (A) or the OpenRGB window an
/// administrator opened from the tray (B) — and checks the process that accepted each connection
/// (<see cref="OpenRgbConnectionGuard"/>). Any other process on the port gives "conflict".
/// </summary>
internal sealed partial class OpenRgbHost : BackgroundService
{
    /// <summary>SHA-256 of OpenRGB.exe from OpenRGB 1.1 (8e16de2), the copy CI puts into the installer.</summary>
    public const string PinnedSha256 = "e26ae71162795a8abcfb90c05c1572a00ba06ca63b886aca57392a0138963b1e";

    private static readonly TimeSpan PortCheckInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan WarningInterval = TimeSpan.FromMinutes(10);
    private readonly Lock _lock = new();
    private readonly string? _executable;
    private readonly string _configDirectory;
    private readonly int _port;
    private readonly ILogger<OpenRgbHost> _logger;
    private readonly Func<ProcessStartInfo, Process?> _start;
    private Process? _process;
    private volatile ChildIdentity? _child;
    private CancellationTokenSource? _run;
    private DateTimeOffset _pausedUntil;
    private DateTimeOffset _lastWarning = DateTimeOffset.MinValue;

    // Decided when the host is created, not in ExecuteAsync: since .NET 10 all of ExecuteAsync runs in the
    // background, so a caller right after start could otherwise see "starting" for a copy that is not there.
    private readonly bool _installed;
    private volatile string? _state;

    public OpenRgbHost(string? executable, string configDirectory, int port, ILogger<OpenRgbHost> logger)
        : this(executable, configDirectory, port, logger, owners: null, PinnedSha256, start: null, anyOs: false)
    {
    }

    /// <summary>Tests: a fake connection-owner source, pinned hash and process start; anyOs runs the loop off Windows.</summary>
    internal OpenRgbHost(string? executable, string configDirectory, int port, ILogger<OpenRgbHost> logger,
        IConnectionOwnerSource? owners, string pinnedSha256, Func<ProcessStartInfo, Process?>? start, bool anyOs)
    {
        _executable = executable;
        _configDirectory = configDirectory;
        _port = port;
        _logger = logger;
        _start = start ?? Process.Start;
        _installed = executable is not null && (anyOs || OperatingSystem.IsWindows()) && File.Exists(executable);
        Policy = new OpenRgbIdentityPolicy(executable ?? DefaultExecutable, pinnedSha256, OpenRgbIdentityPolicy.Sha256OfFile);
        Guard = new OpenRgbConnectionGuard(owners ?? DefaultOwners(), Policy, () => _child, OnVerdict);
    }

    /// <summary>notInstalled, starting, running, external (the OpenRGB window, case B), paused, restarting, conflict.</summary>
    public string State
    {
        get => _state ?? (_installed ? "starting" : "notInstalled");
        private set => _state = value;
    }

    public OpenRgbIdentityPolicy Policy { get; }

    /// <summary>Checks every connection to the SDK port (this host's probes and RgbService's commands).</summary>
    public OpenRgbConnectionGuard Guard { get; }

    /// <summary>Tests only: treat this process identity as the bundled child (case A) without starting OpenRGB.</summary>
    internal void TrustAsChildForTests(ChildIdentity child) => _child = child;

    public static string DefaultExecutable =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "OpenRGB", "OpenRGB.exe"));

    public static string ConfigDirectory(string dataDirectory) => Path.Combine(dataDirectory, "OpenRGB");

    /// <summary>Headless SDK server on loopback; its own settings and profiles in the agent's protected data folder.</summary>
    public static IReadOnlyList<string> ServerArguments(string configDirectory, int port) =>
        ["--server", "--server-host", "127.0.0.1", "--server-port", port.ToString(CultureInfo.InvariantCulture), "--noautoconnect", "--config", configDirectory];

    /// <summary>The same plus the window, for the owner to set up devices (zone sizes etc.); the phone keeps working meanwhile.</summary>
    public static IReadOnlyList<string> GuiArguments(string configDirectory, int port) => ["--gui", .. ServerArguments(configDirectory, port)];

    /// <summary>Stops the bundled server and does not restart it for a while, so the OpenRGB window can take the port.</summary>
    public void Pause(TimeSpan duration)
    {
        lock (_lock)
        {
            _pausedUntil = DateTimeOffset.UtcNow + duration;
            _run?.Cancel();
        }
    }

    public void Resume()
    {
        lock (_lock)
        {
            _pausedUntil = DateTimeOffset.MinValue;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_installed || _executable is null)
        {
            return; // State already reads notInstalled
        }
        Directory.CreateDirectory(_configDirectory); // inherits the data folder's SYSTEM/Administrators-only access
        if (OperatingSystem.IsWindows())
        {
            StopStale(_executable);
        }
        var backoff = TimeSpan.FromSeconds(5);
        while (!stoppingToken.IsCancellationRequested)
        {
            var paused = DateTimeOffset.UtcNow < _pausedUntil;
            var holder = await ProbeAsync(stoppingToken).ConfigureAwait(false);
            if (holder is not null)
            {
                // Somebody holds the port: never start a second copy. Only case B is "external"; during the pause too.
                State = holder.Kind == PeerKind.SetupWindow ? "external" : holder.Kind == PeerKind.Bundled ? "running" : "conflict";
                await Task.Delay(paused ? TimeSpan.FromSeconds(1) : PortCheckInterval, stoppingToken).ConfigureAwait(false);
                continue;
            }
            if (paused)
            {
                State = "paused";
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken).ConfigureAwait(false);
                continue;
            }

            // Section 3: check the file and keep it locked against writes and deletion for the life of the child.
            var executableLock = LockVerifiedExecutable(_executable);
            if (executableLock is null)
            {
                State = "conflict";
                await Task.Delay(PortCheckInterval, stoppingToken).ConfigureAwait(false);
                continue;
            }
            int? exitCode;
            var started = DateTimeOffset.UtcNow;
            using (executableLock)
            {
                exitCode = await RunOnceAsync(_executable, stoppingToken).ConfigureAwait(false);
            }
            if (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            if (exitCode is null)
            {
                continue; // paused while running
            }
            backoff = DateTimeOffset.UtcNow - started > TimeSpan.FromMinutes(1) ? TimeSpan.FromSeconds(5) : TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, TimeSpan.FromMinutes(5).Ticks));
            State = "restarting";
            LogExited(_logger, exitCode.Value, backoff.TotalSeconds);
            await Task.Delay(backoff, stoppingToken).ConfigureAwait(false);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        Kill();
    }

    /// <summary>
    /// Opens OpenRGB.exe for reading with others allowed to read only (no writing, no deletion) and checks its
    /// SHA-256 through that handle. Null (and a journal warning) when it does not match the pinned hash.
    /// </summary>
    internal FileStream? LockVerifiedExecutable(string exe)
    {
        FileStream? stream = null;
        try
        {
            stream = new FileStream(exe, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (Policy.HashMatches(OpenRgbIdentityPolicy.Sha256(stream)))
            {
                return stream;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // locked or unreadable: treated like a mismatch
        }
        stream?.Dispose();
        Warn(null, exe, "OpenRGB.exe does not match the pinned SHA-256; the bundled server is not started");
        return null;
    }

    /// <summary>Who holds the SDK port, checked on the connection itself; null when nothing listens.</summary>
    private async Task<PeerVerdict?> ProbeAsync(CancellationToken cancellationToken)
    {
        using var client = new TcpClient(AddressFamily.InterNetwork);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(500));
        try
        {
            await client.ConnectAsync(IPAddress.Loopback, _port, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        var verdict = Guard.Check(client, out var lease);
        lease?.Dispose();
        return verdict; // the probe closes without sending anything
    }

    /// <summary>Exit code, or null when stopped on purpose (pause or service stop).</summary>
    private async Task<int?> RunOnceAsync(string exe, CancellationToken stoppingToken)
    {
        var start = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
        };
        foreach (var argument in ServerArguments(_configDirectory, _port))
        {
            start.ArgumentList.Add(argument);
        }
        CancellationTokenSource run;
        Process? process;
        IPeerProcess? held;
        lock (_lock)
        {
            run = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            _run = run;
            _process = process = _start(start);
            // PID and start time read through our own handle, held until the child exits (PID reuse protection).
            held = process is null ? null : Guard.Owners.Open(process.Id);
            _child = held is null ? null : new ChildIdentity(held.ProcessId, held.StartTimeUtc);
        }
        using (run)
        {
            if (process is null)
            {
                return -1;
            }
            if (held is null)
            {
                Kill();
                lock (_lock)
                {
                    _process?.Dispose();
                    _process = null;
                    _run = null;
                }
                return -1;
            }
            State = "running";
            LogStarted(_logger, process.Id);
            try
            {
                await process.WaitForExitAsync(run.Token).ConfigureAwait(false);
                return process.ExitCode;
            }
            catch (OperationCanceledException)
            {
                Kill();
                return null;
            }
            finally
            {
                lock (_lock)
                {
                    _child = null;
                    held.Dispose();
                    _process?.Dispose();
                    _process = null;
                    _run = null;
                }
            }
        }
    }

    private void OnVerdict(PeerVerdict verdict, int? pid, string? path)
    {
        if (verdict.Kind == PeerKind.Conflict)
        {
            if (_installed)
            {
                State = "conflict";
            }
            Warn(pid, path, verdict.Reason);
        }
    }

    /// <summary>One journal warning (PID, path, reason) at most every 10 minutes.</summary>
    private void Warn(int? pid, string? path, string reason)
    {
        lock (_lock)
        {
            var now = DateTimeOffset.UtcNow;
            if (now - _lastWarning < WarningInterval)
            {
                return;
            }
            _lastWarning = now;
        }
        LogConflict(_logger, pid?.ToString(CultureInfo.InvariantCulture) ?? "-", path ?? "-", reason);
    }

    private static IConnectionOwnerSource DefaultOwners() =>
        OperatingSystem.IsWindows() ? new Nexus.Windows.TcpConnectionOwners() : new NoConnectionOwners();

    private void Kill()
    {
        lock (_lock)
        {
            try
            {
                if (_process is { HasExited: false })
                {
                    _process.Kill(entireProcessTree: true);
                    _process.WaitForExit(5000);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // already gone
            }
        }
    }

    /// <summary>A server left running by an agent that crashed: same executable, so it is ours to stop.</summary>
    private static void StopStale(string exe)
    {
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe)))
        {
            using (process)
            {
                try
                {
                    if (string.Equals(process.MainModule?.FileName, exe, StringComparison.OrdinalIgnoreCase))
                    {
                        process.Kill(entireProcessTree: true);
                        process.WaitForExit(5000);
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // exited, or not accessible: not ours
                }
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "OpenRGB server started (pid {Pid})")]
    private static partial void LogStarted(ILogger logger, int pid);

    [LoggerMessage(Level = LogLevel.Warning, Message = "OpenRGB exited with code {Code}; restarting in {Seconds} s")]
    private static partial void LogExited(ILogger logger, int code, double seconds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Lighting taken over by another program: pid {Pid}, path {Path}, reason: {Reason}. Nexus PC does not connect to it.")]
    private static partial void LogConflict(ILogger logger, string pid, string path, string reason);
}

/// <summary>Refused connection to the SDK port (T2): nothing was sent.</summary>
internal sealed class OpenRgbConflictException(PeerVerdict verdict) : Exception(verdict.Reason)
{
    public PeerVerdict Verdict { get; } = verdict;
}

/// <summary>
/// T2, section 2: right after connecting, before a single byte is sent, find the counterpart row of this very
/// connection in the TCP table, open the process that holds it, check it (<see cref="OpenRgbIdentityPolicy"/>)
/// and keep its handle open for the life of the connection. When that process exits, the connection is closed,
/// so the next one is checked again.
/// </summary>
internal sealed class OpenRgbConnectionGuard(IConnectionOwnerSource owners, OpenRgbIdentityPolicy policy, Func<ChildIdentity?> child, Action<PeerVerdict, int?, string?>? onVerdict = null)
{
    public IConnectionOwnerSource Owners => owners;

    /// <summary>Verdict for a connected socket; on success the lease holds the process handle and watches its exit.</summary>
    public PeerVerdict Check(TcpClient tcp, out IDisposable? lease)
    {
        ArgumentNullException.ThrowIfNull(tcp);
        lease = null;
        int? pid = null;
        IPeerProcess? process = null;
        PeerVerdict verdict;
        try
        {
            if (tcp.Client.LocalEndPoint is IPEndPoint local && tcp.Client.RemoteEndPoint is IPEndPoint remote)
            {
                pid = owners.ServerProcessId(local, remote);
            }
            process = pid is { } id ? owners.Open(id) : null;
            verdict = policy.Check(pid, process, child());
        }
        catch (Exception ex) when (ex is InvalidOperationException or SocketException or ObjectDisposedException)
        {
            verdict = new PeerVerdict(PeerKind.Conflict, "connection owner unknown: " + ex.Message);
        }
        onVerdict?.Invoke(verdict, pid, process?.ImagePath);
        if (verdict.Kind == PeerKind.Conflict || process is null)
        {
            process?.Dispose();
            tcp.Dispose(); // closed with nothing sent
            return verdict;
        }
        lease = new Lease(tcp, process);
        return verdict;
    }

    /// <summary>For <see cref="Nexus.RGB.OpenRgbClient.ConnectAsync(string, int, Func{TcpClient, IDisposable}?, CancellationToken)"/>: throws on conflict.</summary>
    public IDisposable Accept(TcpClient tcp)
    {
        var verdict = Check(tcp, out var lease);
        return lease ?? throw new OpenRgbConflictException(verdict);
    }

    private sealed class Lease : IDisposable
    {
        private readonly IPeerProcess _process;

        public Lease(TcpClient tcp, IPeerProcess process)
        {
            _process = process;
            // The accepting process exited: close the connection; the next one is checked from scratch.
            process.Exited.ContinueWith(_ => tcp.Dispose(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        public void Dispose() => _process.Dispose();
    }
}

/// <summary>Off Windows there is no TCP owner table: no connection can be confirmed.</summary>
internal sealed class NoConnectionOwners : IConnectionOwnerSource
{
    public int? ServerProcessId(IPEndPoint clientLocal, IPEndPoint clientRemote) => null;

    public IPeerProcess? Open(int processId) => null;
}
