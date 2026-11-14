using System.IO.Pipes;
using System.Text.Json.Nodes;
using Nexus.Core.Apps;
using Nexus.Core.Session;
using Nexus.Windows;

namespace Nexus.Tray;

/// <summary>
/// Keeps a connection to Nexus.Agent's session pipe and serves its requests. Before trusting the pipe
/// it checks that the process on the other end is the NexusPCAgent service (see <see cref="CheckAgent"/>).
/// </summary>
internal sealed class AgentConnection(TrayCommandHandler handler, string expectedAgentPath, Action<string> log) : IDisposable
{
    private const string PipeName = "NexusPC.Session";
    private const string ServiceName = "NexusPCAgent";
    private readonly CancellationTokenSource _stop = new();
    private JsonLineChannel? _channel;

    public event EventHandler<bool>? ConnectedChanged;

    /// <summary>Human-readable reason shown in the tray menu while not connected.</summary>
    public event EventHandler<string>? StatusChanged;

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
                if (CheckAgent(pipe) is { } problem)
                {
                    Report("Служба найдена, но не прошла проверку: " + problem);
                    await pipe.DisposeAsync().ConfigureAwait(false);
                    await Task.Delay(TimeSpan.FromSeconds(15), stop).ConfigureAwait(false);
                    continue;
                }
                var channel = new JsonLineChannel(pipe);
                _channel = channel;
                log("Connected to the agent.");
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
            catch (TimeoutException)
            {
                Report("Служба Nexus PC Agent не отвечает (не запущена?).");
                await pipe.DisposeAsync().ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException)
            {
                Report("Нет доступа к каналу службы.");
                await pipe.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException or OperationCanceledException)
            {
                Report("Связь со службой прервалась: " + ex.GetType().Name);
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

    /// <summary>
    /// Null if the pipe server is the agent. The tray runs without admin rights, so it usually cannot
    /// open the SYSTEM service process to read its path (found on a real PC). Instead it asks the
    /// Service Control Manager which process runs NexusPCAgent: only administrators can install or
    /// change that service. If the path is readable after all, it must also match.
    /// </summary>
    private string? CheckAgent(NamedPipeClientStream pipe)
    {
        if (WindowsProcesses.PipeServerProcessId(pipe.SafePipeHandle) is not { } serverPid)
        {
            return "не удалось узнать процесс службы.";
        }
        if (WindowsServiceInfo.RunningProcessId(ServiceName) is not { } servicePid)
        {
            return $"служба {ServiceName} не найдена или не запущена.";
        }
        if (servicePid != serverPid)
        {
            log($"Pipe server pid {serverPid} is not the {ServiceName} service (pid {servicePid}).");
            return "канал открыт другим процессом, не службой Nexus PC.";
        }
        if (WindowsProcesses.ImagePath(serverPid) is { } path
            && !string.Equals(Path.GetFullPath(path), Path.GetFullPath(expectedAgentPath), StringComparison.OrdinalIgnoreCase))
        {
            log($"Agent runs from an unexpected path: {path}");
            return "служба запущена не из папки установки Nexus PC.";
        }
        return null;
    }

    private void Report(string status)
    {
        log(status);
        StatusChanged?.Invoke(this, status);
    }

    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
    }
}
