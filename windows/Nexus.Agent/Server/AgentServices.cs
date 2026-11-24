using Nexus.Core.Power;
using Nexus.Agent.Session;
using Nexus.Agent.Storage;
using Nexus.Monitoring;
using Nexus.Core.Pairing;
using Nexus.Core.Protocol;
using Nexus.Core.Security;

namespace Nexus.Agent.Server;

/// <summary>
/// What runs on this PC without the PC key (E4-3a): tray channel, metrics, lighting, automations, history and audit.
/// No identity, no pcId, no pairing, no network and no plan token exist at this level.
/// </summary>
internal class LocalServices
{
    public LocalServices(IDeviceStore devices, IPowerController power, AuditLog audit, TimeProvider time, IMetricsSource? metricsSource = null)
    {
        Devices = devices;
        Power = power;
        Audit = audit;
        Time = time;
        Trays = new TrayRegistry();
        History = new HistoryStore(Path.GetDirectoryName(audit.FilePath)!, time);
        Metrics = new MetricsSampler(metricsSource ?? new PortableMetricsSource(), time);
    }

    public IDeviceStore Devices { get; }
    public IPowerController Power { get; }
    public AuditLog Audit { get; }
    public TimeProvider Time { get; }
    public TrayRegistry Trays { get; }
    public MetricsSampler Metrics { get; }
    public RgbService? Rgb { get; set; }
    public OpenRgbHost? OpenRgb { get; set; }
    public AutomationService? Automations { get; set; }
    public HistoryStore History { get; }

    /// <summary>
    /// The plan automations are held to; null = plans are not enforced (no pinned keys). Without the PC key there
    /// is no token, so it is always the Free plan (E4-3a, owner decision: automations run on the Free plan).
    /// </summary>
    public virtual Nexus.Core.Entitlements.EntitlementStatus? EnforcedPlan => Nexus.Core.Entitlements.EntitlementStatus.Free;
}

/// <summary>Everything a connection needs, wired once per agent process. Exists only after the PC key opened.</summary>
internal sealed class AgentServices : LocalServices
{
    public AgentServices(AgentIdentity identity, IDeviceStore devices, IPowerController power, AuditLog audit, TimeProvider time, TimeSpan? pairingLifetime = null, TimeSpan? handshakeTimeout = null, IMetricsSource? metricsSource = null)
        : base(devices, power, audit, time, metricsSource)
    {
        Identity = identity;
        PairingLifetime = pairingLifetime ?? PairingManager.DefaultLifetime;
        HandshakeTimeout = handshakeTimeout ?? ConnectionSession.DefaultHandshakeTimeout;
        Pairing = new PairingManager(devices, time);
        Validator = CommandValidator.CreateDefault(time);
        Dispatcher = new CommandDispatcher(this);
    }

    public AgentIdentity Identity { get; }
    public TimeSpan PairingLifetime { get; }
    public TimeSpan HandshakeTimeout { get; }
    public PairingManager Pairing { get; }
    public CommandValidator Validator { get; }
    public CommandDispatcher Dispatcher { get; }
    /// <summary>Plan state (ENT-2); set by AgentHost right after construction.</summary>
    public Nexus.Core.Entitlements.EntitlementTracker Entitlements { get; set; } = null!;
    /// <summary>Account server client (E4-4); null = no server configured in this build (login and refresh off).</summary>
    public Nexus.Agent.Account.AccountClient? Account { get; set; }

    public override Nexus.Core.Entitlements.EntitlementStatus? EnforcedPlan =>
        Entitlements is { Enforced: true } entitlements ? entitlements.Current : null;
}
