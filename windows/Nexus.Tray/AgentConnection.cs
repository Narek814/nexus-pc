using System.IO.Pipes;
using System.Text.Json.Nodes;
using Nexus.Core.Apps;
using Nexus.Core.Session;
using Nexus.Windows;

namespace Nexus.Tray;

/// <summary>
/// Keeps a connection to Nexus.Agent's session pipe and serves its requests. Before trusting the pipe
/// it checks that the process on the other end is the installed Nexus.Agent.exe.
/// </summary>
internal sealed class AgentConnection(TrayCommandHandler handler, string expectedAgentPath) : IDisposable
{
    private const string PipeName = "NexusPC.Session";
    private readonly CancellationTokenSource _stop = new();
    private JsonLineChannel? _channel;

    public event EventHandler<bool>? ConnectedChanged;

    public bool IsConnected => _channel is not null;

    public void Start() => _ = Task.Run(() => LoopAsync(_stop.Token));

    public async Task<RpcResult> CallAsync(string op, JsonObject? args = null, TimeSpan? timeout = null)
    {
        if (_channel is not { } channel)
        {
            return RpcResult.Failure("session.unavailable", "Нет связи со службой Nexus PC Agent.");
        }
        try
        {
            return await channel.CallAsync(op, args, timeout ?? TimeSpan.FromSeconds(15), _stop.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or ObjectDisposedException)
        {
            return RpcResult.Failure("session.unavailable", "Служба Nexus PC Agent не ответила.");
        }
    }

    private async Task LoopAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await pipe.ConnectAsync(TimeSpan.FromSeconds(5), stop).ConfigureAwait(false);
                if (!IsGenuineAgent(pipe))
                {
                    await pipe.DisposeAsync().ConfigureAwait(false);
                    await Task.Delay(TimeSpan.FromSeconds(30), stop).ConfigureAwait(false);
                    continue;
                }
                var channel = new JsonLineChannel(pipe);
                _channel = channel;
                ConnectedChanged?.Invoke(this, true);
                try
                {
                    await channel.RunAsync(handler.HandleAsync, stop).ConfigureAwait(false);
                }
                finally
                {
                    _channel = null;
                    ConnectedChanged?.Invoke(this, false);
                    await channel.DisposeAsync().ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException or OperationCanceledException)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
            }
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(3), stop).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private bool IsGenuineAgent(NamedPipeClientStream pipe)
    {
        var pid = WindowsProcesses.PipeServerProcessId(pipe.SafePipeHandle);
        var path = pid is { } p ? WindowsProcesses.ImagePath(p) : null;
        return path is not null && string.Equals(Path.GetFullPath(path), Path.GetFullPath(expectedAgentPath), StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
    }
}
