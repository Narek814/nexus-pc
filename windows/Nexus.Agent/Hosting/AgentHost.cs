using System.Net;
using System.Security.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Nexus.Agent.Admin;
using Nexus.Agent.Power;
using Nexus.Agent.Server;
using Nexus.Agent.Storage;

namespace Nexus.Agent.Hosting;

internal sealed record AgentHostOptions
{
    public required string DataDirectory { get; init; }
    /// <summary>Overrides config.json; 0 picks a free port (tests).</summary>
    public int? Port { get; init; }
    /// <summary>Overrides automatic address selection (tests).</summary>
    public IReadOnlyList<IPAddress>? BindAddresses { get; init; }
    public IPowerController? Power { get; init; }
    public TimeProvider Time { get; init; } = TimeProvider.System;
    public string AdminPipeName { get; init; } = AdminPipeServer.DefaultPipeName;
    public bool EnableAdminPipe { get; init; } = true;
    public TimeSpan? PairingLifetime { get; init; }
    public TimeSpan? HandshakeTimeout { get; init; }
}

internal static class AgentHost
{
    public const string ServiceName = "NexusPCAgent";
    public const string WebSocketPath = "/v1/ws";

    public static WebApplication Build(string[] args, AgentHostOptions options)
    {
        AgentPaths.EnsureSecureDirectory(options.DataDirectory);
        var config = AgentConfig.LoadOrCreate(options.DataDirectory);
        var certificate = AgentCertificate.LoadOrCreate(options.DataDirectory, ref config);
        var identity = new AgentIdentity(config.PcId, Environment.MachineName, certificate);
        var devices = new JsonDeviceStore(options.DataDirectory);
        var power = options.Power ?? (OperatingSystem.IsWindows() ? new WindowsPowerController() : new UnsupportedPowerController());
        var services = new AgentServices(identity, devices, power, new AuditLog(options.DataDirectory, options.Time), options.Time, options.PairingLifetime, options.HandshakeTimeout);

        var port = options.Port ?? config.Port;
        // Loopback always (local tools); LAN and Tailscale addresses only — never 0.0.0.0 or a public address.
        var addresses = options.BindAddresses ?? [IPAddress.Loopback, .. NetworkAddresses.EligibleLocalAddresses()];

        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory });
        builder.WebHost.UseKestrelHttpsConfiguration();
        builder.Host.UseWindowsService(o => o.ServiceName = ServiceName);
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            kestrel.Limits.MaxConcurrentConnections = 32;
            kestrel.Limits.MaxConcurrentUpgradedConnections = 16;
            kestrel.Limits.MaxRequestBodySize = 1024;
            kestrel.Limits.MaxRequestHeadersTotalSize = 16 * 1024;
            kestrel.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
            foreach (var address in addresses)
            {
                kestrel.Listen(address, port, listen => listen.UseHttps(new HttpsConnectionAdapterOptions
                {
                    ServerCertificate = certificate,
                    SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    ClientCertificateMode = ClientCertificateMode.NoCertificate,
                }));
            }
        });

        builder.Services.AddSingleton(services);
        builder.Services.AddSingleton<AdminService>();
        if (options.EnableAdminPipe)
        {
            builder.Services.AddHostedService(sp => new AdminPipeServer(sp.GetRequiredService<AdminService>(), options.AdminPipeName, sp.GetRequiredService<ILogger<AdminPipeServer>>()));
        }

        var app = builder.Build();
        app.Lifetime.ApplicationStarted.Register(() => identity.Endpoints = BoundEndpoints(app));
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });
        app.Map(WebSocketPath, async context =>
        {
            // Native clients send no Origin; browsers always do. Refuse cross-site WebSocket attempts.
            if (!context.WebSockets.IsWebSocketRequest || context.Request.Headers.Origin.Count > 0)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }
            using var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
            using var stopping = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, app.Lifetime.ApplicationStopping);
            var session = new ConnectionSession(socket, services, context.Connection.RemoteIpAddress?.ToString() ?? "?");
            await session.RunAsync(stopping.Token).ConfigureAwait(false);
        });
        return app;
    }

    private static List<IPEndPoint> BoundEndpoints(WebApplication app)
    {
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses ?? [];
        return [.. addresses.Select(a => new Uri(a)).Select(u => new IPEndPoint(IPAddress.Parse(u.Host.Trim('[', ']')), u.Port))];
    }
}
