using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Nexus.Agent.Admin;
using Nexus.Core.Protocol;
using Nexus.Core.Session;
using Nexus.Windows;

namespace Nexus.Agent.Session;

/// <summary>
/// Named pipe for Nexus.Tray (ARCHITECTURE.md §4.1). Open to interactive users and administrators, network denied.
/// On Windows the agent accepts a client only if its executable is the installed Nexus.Tray.exe
/// (under Program Files, writable by administrators only); anything else is disconnected.
/// </summary>
internal sealed partial class TraySessionServer(
    TrayRegistry registry, AdminService admin, string pipeName, string? expectedTrayPath, ILogger<TraySessionServer> logger) : BackgroundService
{
    public const string DefaultPipeName = "NexusPC.Session";
    private const int MaxInstances = 8;

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
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Never let a pipe problem stop the whole agent (a BackgroundService exception stops the host).
                LogCreateFailed(logger, ex);
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken).ConfigureAwait(false);
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
        var sessionId = 0;
        if (OperatingSystem.IsWindows())
        {
            var verified = VerifyClient(pipe);
            if (verified is null)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                return;
            }
            sessionId = verified.Value;
        }

        var channel = new JsonLineChannel(pipe);
        var connection = new TrayConnection(sessionId, channel);
        var state = new AdminConnectionState();
        registry.Add(connection);
        LogTrayConnected(logger, sessionId);
        try
        {
            await channel.RunAsync((op, args, ct) => HandleFromTrayAsync(op, args, state, ct), stoppingToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidDataException or System.Text.Json.JsonException or ObjectDisposedException)
        {
            LogTrayDisconnected(logger, ex);
        }
        finally
        {
            registry.Remove(connection);
            admin.ConnectionClosed(state);
            await channel.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task<RpcResult> HandleFromTrayAsync(string op, JsonObject args, AdminConnectionState state, CancellationToken cancellationToken)
    {
        if (!SessionOps.AllowedFromTray.Contains(op))
        {
            return RpcResult.Failure(ErrorCodes.PermissionDenied, $"'{op}' needs an elevated administrator (Nexus.Agent CLI).");
        }
        var request = new JsonObject { ["op"] = op };
        foreach (var (key, value) in args)
        {
            if (key != "op")
            {
                request[key] = value?.DeepClone();
            }
        }
        var response = await admin.HandleAsync(request, state, cancellationToken).ConfigureAwait(false);
        if (response["ok"]?.GetValue<bool>() == true)
        {
            response.Remove("ok");
            return RpcResult.Success(response);
        }
        return RpcResult.Failure("admin.failed", response["error"]?.GetValue<string>() ?? "failed");
    }

    [SupportedOSPlatform("windows")]
    private int? VerifyClient(NamedPipeServerStream pipe)
    {
        var pid = WindowsProcesses.PipeClientProcessId(pipe.SafePipeHandle);
        var path = pid is { } p ? WindowsProcesses.ImagePath(p) : null;
        var session = pid is { } q ? WindowsProcesses.SessionId(q) : null;
        if (path is null || session is null || expectedTrayPath is null
            || !string.Equals(Path.GetFullPath(path), Path.GetFullPath(expectedTrayPath), StringComparison.OrdinalIgnoreCase))
        {
            LogRejectedClient(logger, path ?? "?");
            return null;
        }
        return session;
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
        // The creating account needs CreateNewInstance for every instance after the first. It is SYSTEM
        // in production; without this, any other account (e.g. an administrator running tests) fails
        // on the second connection. Found by CI on windows-latest.
        if (WindowsIdentity.GetCurrent().User is { } self)
        {
            security.AddAccessRule(new PipeAccessRule(self, PipeAccessRights.FullControl, AccessControlType.Allow));
        }
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.InteractiveSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        // Elevated administrators may connect too (they control the PC anyway); the executable check still applies.
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        var options = PipeOptions.Asynchronous | (first ? PipeOptions.FirstPipeInstance : PipeOptions.None);
        return NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, MaxInstances, PipeTransmissionMode.Byte, options, 0, 0, security);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not create a pipe instance; retrying")]
    private static partial void LogCreateFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Nexus.Tray connected from session {SessionId}")]
    private static partial void LogTrayConnected(ILogger logger, int sessionId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Nexus.Tray disconnected")]
    private static partial void LogTrayDisconnected(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Refused a session pipe client that is not the installed Nexus.Tray: {Path}")]
    private static partial void LogRejectedClient(ILogger logger, string path);
}
