using System.Text.Json.Nodes;
using Nexus.Core.Protocol;
using Nexus.Core.Session;
using Nexus.Windows;

namespace Nexus.Agent.Session;

internal sealed record TrayConnection(int SessionId, JsonLineChannel Channel);

/// <summary>
/// Connected Nexus.Tray instances, one per Windows session. Desktop commands go to the Tray of the
/// session at the physical console, i.e. the person sitting at the PC.
/// </summary>
internal sealed class TrayRegistry
{
    public static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(15);

    private readonly Lock _lock = new();
    private readonly List<TrayConnection> _connections = [];

    public void Add(TrayConnection connection)
    {
        lock (_lock)
        {
            _connections.Add(connection);
        }
    }

    public void Remove(TrayConnection connection)
    {
        lock (_lock)
        {
            _connections.Remove(connection);
        }
    }

    public bool IsAvailable => Current() is not null;

    public TrayConnection? Current()
    {
        lock (_lock)
        {
            if (_connections.Count == 0)
            {
                return null;
            }
            if (OperatingSystem.IsWindows() && WindowsProcesses.ActiveConsoleSessionId() is { } console)
            {
                return _connections.LastOrDefault(c => c.SessionId == console);
            }
            return _connections[^1]; // development hosts: the most recent connection
        }
    }

    public async Task<RpcResult> CallAsync(string op, JsonObject? args, CancellationToken cancellationToken)
    {
        if (Current() is not { } tray)
        {
            return RpcResult.Failure(ErrorCodes.SessionUnavailable, "Nexus.Tray is not running in the signed-in session.");
        }
        try
        {
            return await tray.Channel.CallAsync(op, args, CallTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or ObjectDisposedException)
        {
            return RpcResult.Failure(ErrorCodes.SessionUnavailable, "Nexus.Tray did not answer.");
        }
    }
}
