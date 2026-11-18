using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Nexus.Agent.Server;

/// <summary>
/// Runs the OpenRGB copy installed with Nexus PC (Program Files\NexusPC\OpenRGB) as its SDK server, without
/// a window, as a child of the service (so with the rights OpenRGB needs for motherboard and RAM lighting, and
/// no UAC prompt). The server listens on 127.0.0.1 only: the phone reaches lighting through the agent, never
/// OpenRGB directly. If another OpenRGB server already holds the port (one the owner started), it is used as is.
/// </summary>
internal sealed partial class OpenRgbHost(string? executable, string configDirectory, int port, ILogger<OpenRgbHost> logger) : BackgroundService
{
    private static readonly TimeSpan PortCheckInterval = TimeSpan.FromSeconds(5);
    private readonly Lock _lock = new();
    private Process? _process;
    private CancellationTokenSource? _run;
    private DateTimeOffset _pausedUntil;

    // Decided when the host is created, not in ExecuteAsync: since .NET 10 all of ExecuteAsync runs in the
    // background, so a caller right after start could otherwise see "starting" for a copy that is not there.
    private readonly bool _installed = executable is not null && OperatingSystem.IsWindows() && File.Exists(executable);
    private string? _state;

    /// <summary>notInstalled, starting, running, external (another OpenRGB server), paused, restarting.</summary>
    public string State
    {
        get => _state ?? (_installed ? "starting" : "notInstalled");
        private set => _state = value;
    }

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
        if (!_installed || executable is null)
        {
            return; // State already reads notInstalled
        }
        Directory.CreateDirectory(configDirectory); // inherits the data folder's SYSTEM/Administrators-only access
        StopStale(executable);
        var backoff = TimeSpan.FromSeconds(5);
        while (!stoppingToken.IsCancellationRequested)
        {
            if (DateTimeOffset.UtcNow < _pausedUntil)
            {
                State = "paused";
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken).ConfigureAwait(false);
                continue;
            }
            if (await PortInUseAsync(port, stoppingToken).ConfigureAwait(false))
            {
                State = "external";
                await Task.Delay(PortCheckInterval, stoppingToken).ConfigureAwait(false);
                continue;
            }

            var started = DateTimeOffset.UtcNow;
            int? exitCode = await RunOnceAsync(executable, stoppingToken).ConfigureAwait(false);
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
            LogExited(logger, exitCode.Value, backoff.TotalSeconds);
            await Task.Delay(backoff, stoppingToken).ConfigureAwait(false);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        Kill();
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
        foreach (var argument in ServerArguments(configDirectory, port))
        {
            start.ArgumentList.Add(argument);
        }
        CancellationTokenSource run;
        lock (_lock)
        {
            run = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            _run = run;
            _process = Process.Start(start);
        }
        using (run)
        {
            if (_process is null)
            {
                return -1;
            }
            State = "running";
            LogStarted(logger, _process.Id);
            try
            {
                await _process.WaitForExitAsync(run.Token).ConfigureAwait(false);
                return _process.ExitCode;
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
                    _process?.Dispose();
                    _process = null;
                    _run = null;
                }
            }
        }
    }

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

    private static async Task<bool> PortInUseAsync(int port, CancellationToken cancellationToken)
    {
        using var client = new TcpClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(500));
        try
        {
            await client.ConnectAsync("127.0.0.1", port, timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "OpenRGB server started (pid {Pid})")]
    private static partial void LogStarted(ILogger logger, int pid);

    [LoggerMessage(Level = LogLevel.Warning, Message = "OpenRGB exited with code {Code}; restarting in {Seconds} s")]
    private static partial void LogExited(ILogger logger, int code, double seconds);
}
