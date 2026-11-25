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
using Nexus.Agent.Security;
using Nexus.Core.Power;
using Nexus.Windows;
using Nexus.Agent.Server;
using Nexus.Agent.Session;
using Nexus.Agent.Storage;
using Nexus.Monitoring;

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

    /// <summary>Time zone for scheduled automations (tests use UTC).</summary>
    public TimeZoneInfo TimeZone { get; init; } = TimeZoneInfo.Local;
    public string AdminPipeName { get; init; } = AdminPipeServer.DefaultPipeName;
    public bool EnableAdminPipe { get; init; } = true;
    public string SessionPipeName { get; init; } = TraySessionServer.DefaultPipeName;
    public bool EnableSessionPipe { get; init; } = true;
    /// <summary>Overrides the telemetry source (tests). Default: Windows sources on Windows, portable elsewhere.</summary>
    public IMetricsSource? MetricsSource { get; init; }
    /// <summary>OpenRGB SDK server port on this PC (tests point it at a fake server).</summary>
    public int OpenRgbPort { get; init; } = RgbService.DefaultPort;
    /// <summary>OpenRGB installed with Nexus PC, run as the SDK server; null disables it (tests).</summary>
    public string? OpenRgbExecutable { get; init; } = OpenRgbHost.DefaultExecutable;
    /// <summary>Who holds the server end of a TCP connection (T2); null = the Windows TCP table. Tests pass a fake.</summary>
    internal Nexus.Core.Security.IConnectionOwnerSource? OpenRgbOwners { get; init; }
    /// <summary>Overrides the executable allowed on the session pipe (tests).</summary>
    public string? TrayPath { get; init; }
    public TimeSpan? PairingLifetime { get; init; }
    public TimeSpan? HandshakeTimeout { get; init; }
    /// <summary>Entitlement keys, revoked kids and issuer; null = the values built into the agent (tests inject test keys).</summary>
    internal Nexus.Core.Entitlements.EntitlementTrust? EntitlementTrust { get; init; }
    /// <summary>Clock for entitlement terms (tests move it); null = the system clock.</summary>
    internal Nexus.Core.Entitlements.IEntitlementClock? EntitlementClock { get; init; }
    /// <summary>
    /// Opens the permanent PC key; null = <see cref="DeviceKeyOpener.OpenDefault"/> (Windows: the machine CNG key).
    /// Tests inject a file-backed key or a factory that fails.
    /// </summary>
    internal Func<IDeviceKey>? DeviceKeyFactory { get; init; }
    /// <summary>Account server (E4-4); null = the values built into the agent (none yet: the account client is off).</summary>
    internal Account.AccountServerConfig? AccountServer { get; init; }
    /// <summary>HTTP handler for the account server (tests: the fake server); null = a default SocketsHttpHandler.</summary>
    internal Func<HttpMessageHandler>? AccountHttpHandler { get; init; }
    /// <summary>Refresh token sealing; null = DPAPI LocalMachine on Windows (elsewhere the account client is off).</summary>
    internal Account.IRefreshProtector? RefreshProtector { get; init; }
    internal IReadOnlyList<TimeSpan>? AccountRetryPauses { get; init; }
    /// <summary>Pauses between key attempts; null = 2, 5, 10 s (tests pass zeros).</summary>
    internal IReadOnlyList<TimeSpan>? DeviceKeyPauses { get; init; }
    /// <summary>Where "key unavailable" is reported besides audit.log; null = the Windows event log (console elsewhere).</summary>
    internal Action<string>? EventLog { get; init; }
}

/// <summary>Exit code for Program after the host stops (non-zero: the service manager restarts the agent).</summary>
internal sealed class AgentRunState
{
    /// <summary>"key.retry": stopped on purpose so that the service manager starts the agent again.</summary>
    public const int KeyRetryExitCode = 4;

    public int ExitCode { get; set; }
}

internal static class AgentHost
{
    public const string ServiceName = "NexusPCAgent";
    public const string WebSocketPath = "/v1/ws";

    /// <summary>
    /// Normal mode when the PC key opens. Otherwise "key unavailable" (E4-3a, fail-closed for the network): no
    /// identity, no certificate, no plan token and no listening port; config.json, entitlement.json and the
    /// certificate are not read or changed. Local functions keep working.
    /// </summary>
    public static IHost Build(string[] args, AgentHostOptions options)
    {
        AgentPaths.EnsureSecureDirectory(options.DataDirectory);
        var audit = new AuditLog(options.DataDirectory, options.Time);
        var factory = options.DeviceKeyFactory ?? (() => DeviceKeyOpener.OpenDefault(Environment.GetEnvironmentVariable, options.DataDirectory));
        var deviceKey = DeviceKeyOpener.Open(factory, options.DeviceKeyPauses ?? DeviceKeyOpener.DefaultPauses, options.Time, out var failure);
        var devices = new JsonDeviceStore(options.DataDirectory);
        var power = options.Power ?? (OperatingSystem.IsWindows() ? new WindowsPowerController() : new UnsupportedPowerController());
        var metricsSource = options.MetricsSource ?? (OperatingSystem.IsWindows() ? new WindowsMetricsSource() : new PortableMetricsSource());
        if (deviceKey is null)
        {
            audit.Record(null, "device.keyUnavailable", ok: false, failure!.Error, null);
            var report = $"Nexus PC: the PC protection key could not be opened ({failure.Error}, {failure.Attempts} attempts). "
                + "Phones cannot connect or pair until it opens; local functions keep working. "
                + "Restart the Nexus PC service to try again. If the key is lost, every phone must be paired again.";
            if (options.EventLog is { } log)
            {
                log(report);
            }
            else if (OperatingSystem.IsWindows())
            {
                AgentPaths.WriteEventLog(report, AgentPaths.KeyUnavailableEventId);
            }
            else
            {
                Console.Error.WriteLine(report);
            }
            return BuildKeyUnavailable(args, options, new LocalServices(devices, power, audit, options.Time, metricsSource), failure);
        }
        return BuildNormal(args, options, deviceKey, new DeviceSetup(devices, power, audit, metricsSource));
    }

    private sealed record DeviceSetup(JsonDeviceStore Devices, IPowerController Power, AuditLog Audit, IMetricsSource MetricsSource);

    private static IHost BuildKeyUnavailable(string[] args, AgentHostOptions options, LocalServices services, DeviceKeyUnavailable failure)
    {
        // A plain host: no Kestrel at all, so no port can be bound (not even ASP.NET Core's default one).
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = args, ContentRootPath = AppContext.BaseDirectory });
        builder.Services.AddWindowsService(o => o.ServiceName = ServiceName);
        var runState = new AgentRunState();
        builder.Services.AddSingleton(runState);
        builder.Services.AddSingleton(services);
        builder.Services.AddSingleton(sp => new AdminService(services, failure, () =>
        {
            runState.ExitCode = AgentRunState.KeyRetryExitCode;
            sp.GetRequiredService<IHostApplicationLifetime>().StopApplication();
        }));
        AddLocalServices(builder.Services, options, services, options.TrayPath);
        var app = builder.Build();
        WireLocalServices(app.Services, options, services);
        return app;
    }

    private static WebApplication BuildNormal(string[] args, AgentHostOptions options, IDeviceKey deviceKey, DeviceSetup setup)
    {
        // pcId and config.json only from here on, after the key opened.
        var config = AgentConfig.LoadOrCreate(options.DataDirectory, Nexus.Core.Security.PcIdDerivation.FromSpki(deviceKey.Spki), out var pcIdChange);
        var certificate = AgentCertificate.LoadOrCreate(options.DataDirectory, ref config);
        var identity = new AgentIdentity(config.PcId, Environment.MachineName, certificate, deviceKey);
        var services = new AgentServices(identity, setup.Devices, setup.Power, setup.Audit, options.Time, options.PairingLifetime, options.HandshakeTimeout, setup.MetricsSource);
        var entitlementStore = new EntitlementFileStore(options.DataDirectory);
        services.Entitlements = new Nexus.Core.Entitlements.EntitlementTracker(
            new Nexus.Core.Entitlements.EntitlementVerifier(options.EntitlementTrust ?? Nexus.Core.Entitlements.PinnedEntitlementKeys.Trust),
            identity.PcId, entitlementStore, options.EntitlementClock ?? new Nexus.Core.Entitlements.SystemEntitlementClock(options.Time));
        // Logged once: only when the key or config.json changes. Paired phones keep their records but must pair
        // again (owner decision 1A): their auth transcript carries the former pcId and no longer verifies.
        if (deviceKey.Created)
        {
            services.Audit.Record(null, "device.key", ok: true, null, deviceKey.Provider);
        }
        if (pcIdChange == AgentConfig.PcIdChange.MigratedFromLegacy)
        {
            services.Audit.Record(null, "pcId.migrated", ok: true, null, config.LegacyPcId?.ToString("D"));
        }
        else if (pcIdChange == AgentConfig.PcIdChange.KeyReplaced)
        {
            services.Audit.Record(null, "pcId.keyReplaced", ok: false, "device.keyLost", deviceKey.Provider);
        }
        if (entitlementStore.DiscardedUntrusted)
        {
            // Someone else wrote or re-permissioned the file: deleted, treated as "no token".
            services.Audit.Record(null, "entitlement.file", ok: false, "entitlement.untrustedFile", null);
        }

        var port = options.Port ?? config.Port;
        // Production listens on all IPv4 addresses and filters every connection by the local address it
        // arrived on (loopback, private LAN), before TLS. Binding to the addresses seen at start
        // missed Wi-Fi that came up after the service (found on a real PC: only the WSL adapter existed at
        // boot). Public addresses are refused here and by the "Private only" firewall rule.
        var addresses = options.BindAddresses ?? [IPAddress.Any];

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
                kestrel.Listen(address, port, listen =>
                {
                    listen.Use(next => connection =>
                    {
                        if (connection.LocalEndPoint is not IPEndPoint local || !NetworkAddresses.IsAllowedLocalAddress(local.Address))
                        {
                            connection.Abort();
                            return Task.CompletedTask;
                        }
                        return next(connection);
                    });
                    listen.UseHttps(new HttpsConnectionAdapterOptions
                    {
                        ServerCertificate = certificate,
                        SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                        ClientCertificateMode = ClientCertificateMode.NoCertificate,
                    });
                });
            }
        });

        builder.Services.AddSingleton(new AgentRunState());
        builder.Services.AddSingleton(services);
        builder.Services.AddSingleton<LocalServices>(services);
        builder.Services.AddSingleton(_ => new AdminService(services));
        builder.Services.AddHostedService(_ => new EntitlementLoop(services.Entitlements));
        AddLocalServices(builder.Services, options, services, options.TrayPath ?? config.TrayPath);

        var app = builder.Build();
        WireLocalServices(app.Services, options, services);
        services.Account = CreateAccountClient(app.Services, options, services, deviceKey);
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

    /// <summary>Both modes: metrics, OpenRGB, automations, the tray channel and the admin pipe.</summary>
    private static void AddLocalServices(IServiceCollection collection, AgentHostOptions options, LocalServices services, string? trayPath)
    {
        collection.AddHostedService(sp => new MetricsLoop(services.Metrics, sp.GetRequiredService<ILogger<MetricsLoop>>()));
        collection.AddSingleton(sp => new OpenRgbHost(options.OpenRgbExecutable, OpenRgbHost.ConfigDirectory(options.DataDirectory), options.OpenRgbPort, sp.GetRequiredService<ILogger<OpenRgbHost>>(),
            options.OpenRgbOwners, OpenRgbHost.PinnedSha256, start: null, anyOs: false));
        collection.AddHostedService(sp => sp.GetRequiredService<OpenRgbHost>());
        collection.AddSingleton(sp => new AutomationService(services, options.DataDirectory, options.TimeZone, sp.GetRequiredService<ILogger<AutomationService>>()));
        collection.AddHostedService(sp => sp.GetRequiredService<AutomationService>());
        if (options.EnableSessionPipe)
        {
            var expectedTray = trayPath ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Tray", "Nexus.Tray.exe"));
            collection.AddHostedService(sp => new TraySessionServer(services.Trays, sp.GetRequiredService<AdminService>(), options.SessionPipeName, expectedTray, sp.GetRequiredService<ILogger<TraySessionServer>>()));
        }
        if (options.EnableAdminPipe)
        {
            collection.AddHostedService(sp => new AdminPipeServer(sp.GetRequiredService<AdminService>(), options.AdminPipeName, sp.GetRequiredService<ILogger<AdminPipeServer>>()));
        }
    }

    private static void WireLocalServices(IServiceProvider provider, AgentHostOptions options, LocalServices services)
    {
        services.Automations = provider.GetRequiredService<AutomationService>();
        services.OpenRgb = provider.GetRequiredService<OpenRgbHost>();
        services.Rgb = new RgbService(options.DataDirectory, options.OpenRgbPort, services.OpenRgb.Guard);
    }

    /// <summary>
    /// Only with a configured server, pinned entitlement keys (tokens can be verified) and a refresh sealer;
    /// otherwise null and the tray's login operations answer "disabled". Never in key-unavailable mode.
    /// </summary>
    private static Account.AccountClient? CreateAccountClient(IServiceProvider provider, AgentHostOptions options, AgentServices services, IDeviceKey deviceKey)
    {
        var server = options.AccountServer ?? Account.AccountServerConfig.FromBuild();
        var protector = options.RefreshProtector ?? (OperatingSystem.IsWindows() ? new Account.DpapiLocalMachineProtector() : null);
        if (server is null || protector is null || !services.Entitlements.Enforced)
        {
            return null;
        }
        var handler = options.AccountHttpHandler?.Invoke() ?? new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        var client = new Account.AccountClient(server, handler, deviceKey, services.Identity.PcId, services.Identity.PcName,
            services.Entitlements, new Account.RefreshTokenStore(options.DataDirectory, protector,
                // Foreign access on account.dat: deleted unread; audited without path or content (E4-6).
                onUntrusted: () => services.Audit.Record(null, "account.file", ok: false, "account.untrustedFile", null)), options.Time,
            provider.GetRequiredService<ILoggerFactory>().CreateLogger("Nexus.Agent.Account"), options.AccountRetryPauses,
            (action, ok, error) => services.Audit.Record(null, action, ok, error, null));
        provider.GetRequiredService<IHostApplicationLifetime>().ApplicationStopped.Register(client.Dispose);
        return client;
    }

    private static List<IPEndPoint> BoundEndpoints(WebApplication app)
    {
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses ?? [];
        return [.. addresses.Select(a => new Uri(a)).Select(u => new IPEndPoint(IPAddress.Parse(u.Host.Trim('[', ']')), u.Port))];
    }
}
