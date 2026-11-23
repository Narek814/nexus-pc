using Nexus.Core.Power;
using Nexus.Agent.Session;
using Nexus.Agent.Storage;
using Nexus.Monitoring;
using Nexus.Core.Pairing;
using Nexus.Core.Protocol;
using Nexus.Core.Security;

namespace Nexus.Agent.Server;

/// <summary>Everything a connection needs, wired once per agent process.</summary>
internal sealed class AgentServices
{
    public AgentServices(AgentIdentity identity, IDeviceStore devices, IPowerController power, AuditLog audit, TimeProvider time, TimeSpan? pairingLifetime = null, TimeSpan? handshakeTimeout = null, IMetricsSource? metricsSource = null)
    {
        Identity = identity;
        Devices = devices;
        Power = power;
        Audit = audit;
        Time = time;
        PairingLifetime = pairingLifetime ?? PairingManager.DefaultLifetime;
        HandshakeTimeout = handshakeTimeout ?? ConnectionSession.DefaultHandshakeTimeout;
        Pairing = new PairingManager(devices, time);
        Validator = CommandValidator.CreateDefault(time);
        Trays = new TrayRegistry();
        History = new HistoryStore(Path.GetDirectoryName(audit.FilePath)!, time);
        Metrics = new MetricsSampler(metricsSource ?? new PortableMetricsSource(), time);
        Dispatcher = new CommandDispatcher(this);
    }

    public AgentIdentity Identity { get; }
    public IDeviceStore Devices { get; }
    public IPowerController Power { get; }
    public AuditLog Audit { get; }
    public TimeProvider Time { get; }
    public TimeSpan PairingLifetime { get; }
    public TimeSpan HandshakeTimeout { get; }
    public PairingManager Pairing { get; }
    public CommandValidator Validator { get; }
    public CommandDispatcher Dispatcher { get; }
    public TrayRegistry Trays { get; }
    public MetricsSampler Metrics { get; }
    public RgbService? Rgb { get; set; }
    public OpenRgbHost? OpenRgb { get; set; }
    public AutomationService? Automations { get; set; }
    public HistoryStore History { get; }
    /// <summary>Plan state (ENT-2); set by AgentHost right after construction.</summary>
    public Nexus.Core.Entitlements.EntitlementTracker Entitlements { get; set; } = null!;
}
