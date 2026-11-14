using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Nexus.Agent.Admin;

/// <summary>
/// Local admin channel: a named pipe that only SYSTEM and elevated Administrators can open, with
/// network access explicitly denied. On other OSes (development) it is restricted to the current user.
/// One JSON object per line in each direction.
/// </summary>
internal sealed partial class AdminPipeServer(AdminService admin, string pipeName, ILogger<AdminPipeServer> logger) : BackgroundService
{
    public const string DefaultPipeName = "NexusPC.Admin";
    private const int MaxInstances = 4;
    internal const int MaxLineChars = 16 * 1024;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var first = true;
        while (!stoppingToken.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = Create(pipeName, first);
                first = false;
            }
            catch (IOException) when (!first)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken).ConfigureAwait(false); // all instances busy
                continue;
            }

            try
            {
                await pipe.WaitForConnectionAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                return;
            }
            _ = ServeAsync(pipe, stoppingToken);
        }
    }

    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken stoppingToken)
    {
        var state = new AdminConnectionState();
        using var connection = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        try
        {
            await using (pipe.ConfigureAwait(false))
            {
                using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
                await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
                while (await reader.ReadLineAsync(connection.Token).ConfigureAwait(false) is { } line)
                {
                    JsonObject response;
                    if (line.Length > MaxLineChars || JsonNode.Parse(line) is not JsonObject request)
                    {
                        response = new JsonObject { ["ok"] = false, ["error"] = "invalid request" };
                    }
                    else
                    {
                        response = await admin.HandleAsync(request, state, connection.Token).ConfigureAwait(false);
                    }
                    await writer.WriteLineAsync(response.ToJsonString().AsMemory(), connection.Token).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or JsonException or ObjectDisposedException)
        {
            LogConnectionEnded(logger, ex);
        }
        finally
        {
            admin.ConnectionClosed(state);
        }
    }

    private static NamedPipeServerStream Create(string name, bool first)
    {
        if (OperatingSystem.IsWindows())
        {
            return CreateWindows(name, first);
        }
        return new NamedPipeServerStream(name, PipeDirection.InOut, MaxInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    }

    [SupportedOSPlatform("windows")]
    private static NamedPipeServerStream CreateWindows(string name, bool first)
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        // FirstPipeInstance: fail instead of joining a pipe someone else created first under our name.
        var options = PipeOptions.Asynchronous | (first ? PipeOptions.FirstPipeInstance : PipeOptions.None);
        return NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, MaxInstances, PipeTransmissionMode.Byte, options, 0, 0, security);
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Admin connection ended")]
    private static partial void LogConnectionEnded(ILogger logger, Exception exception);
}

/// <summary>Client side, used by the command-line verbs (pair, devices, status).</summary>
internal sealed class AdminClient : IAsyncDisposable
{
    private readonly NamedPipeClientStream _pipe;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;

    private AdminClient(NamedPipeClientStream pipe)
    {
        _pipe = pipe;
        _reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
        _writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
    }

    public static async Task<AdminClient> ConnectAsync(string pipeName, CancellationToken cancellationToken)
    {
        var options = PipeOptions.Asynchronous | (OperatingSystem.IsWindows() ? PipeOptions.None : PipeOptions.CurrentUserOnly);
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, options);
        try
        {
            await pipe.ConnectAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            return new AdminClient(pipe);
        }
        catch
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<JsonObject> SendAsync(JsonObject request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await _writer.WriteLineAsync(request.ToJsonString().AsMemory(), cancellationToken).ConfigureAwait(false);
        var line = await _reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) ?? throw new IOException("The agent closed the connection.");
        return JsonNode.Parse(line)?.AsObject() ?? throw new IOException("Empty response from the agent.");
    }

    public async ValueTask DisposeAsync()
    {
        _reader.Dispose();
        await _writer.DisposeAsync().ConfigureAwait(false);
        await _pipe.DisposeAsync().ConfigureAwait(false);
    }
}
